import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { GxFileSystemProvider } from "./gxFileSystem";
import { GxTreeProvider } from "./gxTreeProvider";
import { GxActionsProvider } from "./gxActionsProvider";
import { GxDiagnosticProvider } from "./diagnosticProvider";
import { GxShadowService } from "./gxShadowService";

import { BackendManager } from "./managers/BackendManager";
import { ShadowManager } from "./managers/ShadowManager";
import { ContextManager } from "./managers/ContextManager";
import { CommandManager } from "./managers/CommandManager";
import { ProviderManager } from "./managers/ProviderManager";
import { McpDiscoveryManager } from "./managers/McpDiscoveryManager";
import { SyncManager } from "./managers/SyncManager";
import { GxUriParser } from "./utils/GxUriParser";
import { resolveGatewayHttpPort, tryReadGatewayConfig } from "./utils/GatewayConfig";
import { Logger } from "./utils/Logger";
import { 
  GX_SCHEME, 
  STATE_KEY_FOLDER_ADDED, 
  VIEW_EXPLORER, 
  VIEW_ACTIONS,
  CONFIG_SECTION,
  DEFAULT_STATUS_BAR_TIMEOUT,
  ROOT_PARENT_NAME,
} from "./constants";

let backendManager: BackendManager;
const IS_TEST_MODE = process.env.NEXUS_IDE_TEST_MODE === "1";
let isActivated = false;
let activeShadowRoot: string | undefined;
let pendingMountPromise: Promise<void> | undefined;
let pendingKbBootstrapPromise: Promise<void> | undefined;
let lifecycleLogPath: string | undefined;
let activeProvider: GxFileSystemProvider | undefined;
let activeTreeProvider: GxTreeProvider | undefined;
let activeShadowService: GxShadowService | undefined;
let initializationPromise: Promise<void> | undefined;

type BootstrapReporter = (message: string) => void;

function reportBootstrapStatus(message: string, report?: BootstrapReporter): void {
  const formatted = `[Nexus IDE] ${message}`;
  Logger.info(formatted);
  appendLifecycleLog(formatted);
  report?.(message);
}

function appendLifecycleLog(message: string): void {
  if (!lifecycleLogPath) {
    return;
  }

  try {
    fs.appendFileSync(
      lifecycleLogPath,
      `[${new Date().toISOString()}] ${message}\n`,
    );
  } catch (e) {
    Logger.debug(`[Nexus IDE] Failed to append lifecycle log: ${e}`);
  }
}

function isMirrorReadyForMount(shadowRoot: string): boolean {
  if (!fs.existsSync(shadowRoot)) {
    return false;
  }

  const indexPath = path.join(shadowRoot, ".gx_index.json");
  if (!fs.existsSync(indexPath)) {
    return false;
  }

  try {
    const raw = fs.readFileSync(indexPath, "utf8");
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) && parsed.length > 0;
  } catch (e) {
    Logger.warn(`[Nexus IDE] Mirror index at ${indexPath} is unreadable or corrupt: ${e}`);
    return false;
  }
}

function getErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error ?? "");
}

function isGatewayTimeout(error: unknown): boolean {
  const message = getErrorMessage(error).toLowerCase();
  return message.includes("timeout gateway");
}

function isGatewayConnectionRefused(error: unknown): boolean {
  const message = getErrorMessage(error).toLowerCase();
  return message.includes("connect econnrefused");
}

function isRootBrowseEntry(entry: any): boolean {
  if (!entry?.name || !entry?.type) {
    return false;
  }

  if (typeof entry?.parentPath === "string") {
    return entry.parentPath.trim().length === 0;
  }

  const entryParent =
    typeof entry?.parent === "string" ? entry.parent.trim() : "";
  return entryParent.length === 0 || entryParent === ROOT_PARENT_NAME;
}

function containsReplacementCharacter(text: string | undefined): boolean {
  return typeof text === "string" && text.includes("\uFFFD");
}

async function readIndexStatus(provider: GxFileSystemProvider): Promise<any> {
  const status = await provider.callMcpTool(
    "genexus_lifecycle",
    { action: "status", _ts: Date.now() },
    60000,
  );

  if (typeof status?.error === "string" && status.error.length > 0) {
    throw new Error(status.error);
  }

  return status;
}

export async function hasUsableSearchIndex(
  provider: Pick<GxFileSystemProvider, "browseObjects">,
  status: any,
): Promise<boolean> {
  const initialProcessed = Number(status?.processed ?? 0);
  const initialTotal = Number(status?.total ?? 0);
  const initialStatusText =
    typeof status?.status === "string" ? status.status : "";

  if (
    status?.isIndexing !== true &&
    initialTotal > 0 &&
    initialStatusText.toLowerCase() !== "error"
  ) {
    return true;
  }

  if (
    status?.isIndexing === true ||
    initialTotal !== 0 ||
    initialProcessed !== 0 ||
    initialStatusText.toLowerCase() === "complete"
  ) {
    return false;
  }

  try {
    const rootEntries = await provider.browseObjects("");
    return Array.isArray(rootEntries) && rootEntries.some(isRootBrowseEntry);
  } catch (e) {
    Logger.debug(`[Nexus IDE] Root browse fallback check failed: ${e}`);
    return false;
  }
}

async function rebuildSearchIndex(
  provider: GxFileSystemProvider,
  report?: BootstrapReporter,
): Promise<void> {
  reportBootstrapStatus("Starting KB reindex...", report);
  vscode.window.setStatusBarMessage(
    "$(sync~spin) GeneXus: Reconstruindo indice da KB...",
    5000,
  );

  const startResult = await provider.callMcpTool(
    "genexus_lifecycle",
    { action: "index", _ts: Date.now() },
    300000,
  );

  if (typeof startResult?.error === "string" && startResult.error.length > 0) {
    throw new Error(startResult.error);
  }

  const timeoutAt = Date.now() + 15 * 60 * 1000;
  while (Date.now() < timeoutAt) {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    const status = await readIndexStatus(provider);

    const processed = Number(status?.processed ?? 0);
    const total = Number(status?.total ?? 0);
    const statusText =
      typeof status?.status === "string" ? status.status : "";
    const progressMessage =
      `Indexando KB ${processed}/${total}${statusText ? ` - ${statusText}` : ""}`;

    vscode.window.setStatusBarMessage(
      `$(sync~spin) GeneXus: ${progressMessage}`,
      2000,
    );
    reportBootstrapStatus(progressMessage, report);

    if (status?.isIndexing === true) {
      continue;
    }

    if (
      total > 0 &&
      statusText.toLowerCase() !== "error" &&
      (statusText.toLowerCase() === "complete" ||
        processed >= total ||
        processed > 0)
    ) {
      reportBootstrapStatus("Search index completed.", report);
      return;
    }
  }

  throw new Error("Timed out waiting for KB reindex.");
}

async function ensureSearchIndexReady(
  provider: GxFileSystemProvider,
  report?: BootstrapReporter,
): Promise<void> {
  let initialStatus: any;
  try {
    initialStatus = await readIndexStatus(provider);
  } catch (error) {
    if (isGatewayTimeout(error)) {
      reportBootstrapStatus(
        "Index status request timed out. Proceeding directly to materialization queries.",
        report,
      );
      return;
    }
    throw error;
  }
  const initialProcessed = Number(initialStatus?.processed ?? 0);
  const initialTotal = Number(initialStatus?.total ?? 0);
  const initialStatusText =
    typeof initialStatus?.status === "string" ? initialStatus.status : "";

  if (await hasUsableSearchIndex(provider, initialStatus)) {
    reportBootstrapStatus(
      initialTotal > 0
        ? `Search index already available (${initialProcessed}/${initialTotal}${initialStatusText ? ` - ${initialStatusText}` : ""}).`
        : "Search index counters were reset, but root browse already returns objects. Reusing the existing index.",
      report,
    );
    return;
  }

  if (
    initialStatus?.isIndexing !== true &&
    initialTotal === 0 &&
    initialProcessed === 0 &&
    initialStatusText.toLowerCase() !== "complete"
  ) {
    reportBootstrapStatus(
      "Search index unavailable during startup: lifecycle status reported no indexed objects.",
      report,
    );
    vscode.window.setStatusBarMessage(
      "$(sync~spin) GeneXus: Construindo índice inicial da KB...",
      5000,
    );

    report?.("Construindo indice inicial da KB...");
    await rebuildSearchIndex(provider, report);
    return;
  }

  await rebuildSearchIndex(provider, report);
}

async function mountMaterializedMirror(
  context: vscode.ExtensionContext,
  provider: GxFileSystemProvider,
  shadowRoot: string,
): Promise<void> {
  if (!pendingMountPromise) {
    pendingMountPromise = addKbFolder(context, 5, 2000, provider, shadowRoot)
      .finally(() => {
        pendingMountPromise = undefined;
      });
  }

  await pendingMountPromise;
}

type BootstrapKbOptions = {
  forceRematerialize?: boolean;
  reason?: string;
  skipBackendStart?: boolean;
};

async function bootstrapKbExplorer(
  context: vscode.ExtensionContext,
  provider: GxFileSystemProvider,
  shadowService: GxShadowService,
  treeProvider: GxTreeProvider,
  options: BootstrapKbOptions = {},
): Promise<void> {
  if (!pendingKbBootstrapPromise) {
    const forceRematerialize = options.forceRematerialize === true;
    const reason = options.reason ?? "manual request";
    const skipBackendStart = options.skipBackendStart === true;

    pendingKbBootstrapPromise = (async () => {
      try {
        await vscode.window.withProgress(
          {
            location: vscode.ProgressLocation.Window,
            title: "GeneXus KB",
          },
          async (progress) => {
            const report: BootstrapReporter = (message) => {
              progress.report({ message });
            };

            reportBootstrapStatus(`Preparing KB Explorer for ${reason}.`, report);

            if (!skipBackendStart) {
              const started = await backendManager.start(provider, true);
              if (!started) {
                throw new Error(`Backend could not be started for ${reason}.`);
              }
            }

            await provider.initKb();
            shadowService.consolidateLegacyMirrorFiles();

            const shadowDirExists = fs.existsSync(shadowService.shadowRoot);
            const mirrorReady =
              shadowDirExists && shadowService.hasMaterializedWorkspace();

            if (!forceRematerialize && mirrorReady) {
              reportBootstrapStatus("Existing mirror is valid. Refreshing Explorer views.", report);
              treeProvider.refresh();
              provider.clearDirCache();
              await mountMaterializedMirror(context, provider, shadowService.shadowRoot);
              provider.warmMetadataAfterBootstrap();
              return;
            }

            provider.isWorkspaceHydrating = true;
            try {
              shadowService.resetMirrorWorkspace();
              reportBootstrapStatus("Mirror reset complete. Ensuring search index is ready...", report);
              await ensureSearchIndexReady(provider, report);
              reportBootstrapStatus("Materializing workspace mirror...", report);
              await shadowService.materializeWorkspaceWithProgress(provider, (state) => {
                const currentParent =
                  state.currentParent === ROOT_PARENT_NAME ? "KB root" : state.currentParent;
                report(
                  `${state.directoriesCreated} pastas, ${state.filesPrepared} arquivos, atual: ${currentParent}`,
                );
              });
            } finally {
              provider.isWorkspaceHydrating = false;
            }

            treeProvider.refresh();
            provider.clearDirCache();
            await mountMaterializedMirror(context, provider, shadowService.shadowRoot);
            provider.warmMetadataAfterBootstrap();
            vscode.window.setStatusBarMessage(
              "$(check) GeneXus: Workspace espelho pronto",
              DEFAULT_STATUS_BAR_TIMEOUT,
            );
          },
        );
      } finally {
        pendingKbBootstrapPromise = undefined;
      }
    })();
  }

  await pendingKbBootstrapPromise;
}

export async function ensureKbExplorerReady(
  context: vscode.ExtensionContext,
  options: BootstrapKbOptions = {},
): Promise<void> {
  if (initializationPromise) {
    await initializationPromise;
  }

  if (!activeProvider || !activeTreeProvider || !activeShadowService) {
    throw new Error("Extension services are not ready yet.");
  }

  await bootstrapKbExplorer(
    context,
    activeProvider,
    activeShadowService,
    activeTreeProvider,
    options,
  );
}

export function activate(context: vscode.ExtensionContext) {
  if (isActivated) {
    Logger.info("[Nexus IDE] Activation skipped; extension already initialized.");
    return;
  }
  isActivated = true;
  Logger.activate(context);

  Logger.info("[Nexus IDE] Extension activating...");
  lifecycleLogPath = path.join(context.extensionPath, "extension_lifecycle.log");
  appendLifecycleLog("[Nexus IDE] activate()");

  const provider = new GxFileSystemProvider();
  activeProvider = provider;

  // 1. REGISTER COMMANDS FIRST (Ensure they are always available)
  context.subscriptions.push(
    vscode.commands.registerCommand("nexus-ide.openKb", async () => {
      Logger.info("[Nexus IDE] Command 'nexus-ide.openKb' triggered.");
      await ensureKbExplorerReady(context, {
        forceRematerialize: false,
        reason: "Open KB",
      });
    }),
    vscode.commands.registerCommand("nexus-ide.addKbFolder", async () => {
      Logger.info("[Nexus IDE] Manual 'nexus-ide.addKbFolder' triggered.");
      await ensureKbExplorerReady(context, {
        forceRematerialize: false,
        reason: "Force Add KB Folder",
      });
    }),
    vscode.commands.registerCommand("nexus-ide.refreshFilesystem", async () => {
      Logger.info(
        "[Nexus IDE] Command 'nexus-ide.refreshFilesystem' triggered.",
      );
      await ensureKbExplorerReady(context, {
        forceRematerialize: true,
        reason: "Refresh KB Explorer",
      });
    }),
  );

  // 2. REGISTER FILESYSTEM PROVIDER
  try {
    context.subscriptions.push(
      vscode.workspace.registerFileSystemProvider(GX_SCHEME, provider, {
        isCaseSensitive: false,
        isReadonly: false,
      }),
    );
    Logger.info(
      `[Nexus IDE] GxFileSystemProvider registered for scheme '${GX_SCHEME}'.`,
    );

    // Warm up
    vscode.workspace.fs
      .stat(vscode.Uri.from({ scheme: GX_SCHEME, path: "/" }))
      .then(
        () => Logger.info(`[Nexus IDE] Scheme '${GX_SCHEME}' warm up success.`),
        () => {},
      );
  } catch (e) {
    Logger.error(`[Nexus IDE] FS registration failed: ${e}`);
  }

  // 3. DEFERRED INITIALIZATION
  initializationPromise = (async () => {
    try {
      await initializeExtension(context, provider);
      Logger.info("[Nexus IDE] Initialization complete.");
    } catch (e) {
      Logger.error(`[Nexus IDE] Init error: ${e}`);
      if (e instanceof Error) {
        Logger.error(`[Nexus IDE] Stack: ${e.stack}`);
      }
    }
  })();

  setImmediate(async () => {
    await initializationPromise;
  });

  // Auto-add folder will now happen inside initializeExtension or on command
}

function resolveShadowRootPath(context: vscode.ExtensionContext): string {
  const config = tryReadGatewayConfig(context.extensionPath);
  const configured = config?.Environment?.GX_SHADOW_PATH;
  if (typeof configured === "string" && configured.trim().length > 0) {
    return configured;
  }

  return path.join(context.extensionPath, ".gx_mirror");
}

export async function addKbFolder(
  context: vscode.ExtensionContext,
  maxRetries = 5,
  delayMs = 2000,
  provider?: any,
  shadowRoot?: string,
) {
  const folders = vscode.workspace.workspaceFolders || [];
  const targetShadowRoot = shadowRoot || activeShadowRoot || resolveShadowRootPath(context);
  const mirrorReady = isMirrorReadyForMount(targetShadowRoot);

  if (!mirrorReady) {
    reportBootstrapStatus("Mirror is not ready for mount yet. Skipping workspace add.");
    vscode.window.setStatusBarMessage(
      "$(sync~spin) GeneXus: aguardando materializacao da KB...",
      DEFAULT_STATUS_BAR_TIMEOUT,
    );
    return;
  }

  const shadowUri = vscode.Uri.file(targetShadowRoot);
  const hasShadowFolder = folders.some((f) => f.uri.scheme === "file" && f.uri.fsPath === targetShadowRoot);
  if (hasShadowFolder) {
    reportBootstrapStatus("Mirror workspace already mounted. Forcing refresh.");

    try {
      provider?.clearDirCache?.();
      activeTreeProvider?.refresh();
    } catch (e) {
      Logger.warn(`[Nexus IDE] Failed to refresh mounted mirror folder: ${getErrorMessage(e)}`);
    }

    try {
      await vscode.commands.executeCommand(`${VIEW_EXPLORER}.focus`);
    } catch (e) {
      Logger.debug(`[Nexus IDE] Explorer view focus failed: ${e}`);
    }

    vscode.window.setStatusBarMessage(
      "$(folder-opened) GeneXus KB pronta no Explorer",
      DEFAULT_STATUS_BAR_TIMEOUT,
    );
    return;
  }

  if (!hasShadowFolder) {
    reportBootstrapStatus("Checking if mirror workspace is ready for auto-mount...");

    for (let attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        if (!fs.existsSync(targetShadowRoot)) {
          fs.mkdirSync(targetShadowRoot, { recursive: true });
        }

        reportBootstrapStatus(`Adding workspace folder: ${targetShadowRoot} on attempt ${attempt}`);
        vscode.workspace.updateWorkspaceFolders(folders.length, 0, {
          uri: shadowUri,
          name: "GeneXus KB",
        });
        context.globalState.update(STATE_KEY_FOLDER_ADDED, true);
        try {
          await vscode.commands.executeCommand(`${VIEW_EXPLORER}.focus`);
        } catch (e) {
          Logger.debug(`[Nexus IDE] Explorer view focus failed: ${e}`);
        }
        vscode.window.setStatusBarMessage(
          "$(folder-opened) GeneXus KB montada no Explorer",
          DEFAULT_STATUS_BAR_TIMEOUT,
        );
        return; // Success, exit retry loop
      } catch {
        Logger.warn(
          `[Nexus IDE] Mirror mount point not ready yet (Attempt ${attempt}/${maxRetries}). Retrying in ${delayMs}ms...`,
        );
        if (attempt < maxRetries) {
          await new Promise(resolve => setTimeout(resolve, delayMs));
        } else {
          Logger.error("[Nexus IDE] Auto-mount failed after maximum retries.");
          vscode.window.showWarningMessage("Failed to connect to GeneXus KB MCP Server. You can try reconnecting manually from the Command Palette.");
        }
      }
    }
  }
}
function initializeExtension(
  context: vscode.ExtensionContext,
  provider: GxFileSystemProvider,
) {
  Logger.info("[Nexus IDE] Starting deferred initialization...");
  appendLifecycleLog("[Nexus IDE] initializeExtension()");

  const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
  const port = resolveGatewayHttpPort(context.extensionPath, config);
  Logger.info(`[Nexus IDE] Using MCP port ${port}.`);
  provider.baseUrl = `http://127.0.0.1:${port}/mcp`;
  activeShadowRoot = resolveShadowRootPath(context);

  // Initialize Managers
  backendManager = new BackendManager(context);
  const contextManager = new ContextManager(context, provider);
  const shadowService = new GxShadowService(provider.baseUrl, activeShadowRoot);
  provider.setShadowService(shadowService);

  const discoveryManager = new McpDiscoveryManager(context, provider);
  discoveryManager.cleanupDevelopmentDiscoveryFiles();
  // Defer discovery registration until AFTER backend is potentially ready
  // discoveryManager.register(); // Moved below

  const diagnosticProvider = new GxDiagnosticProvider(provider);
  provider.setDiagnosticProvider(diagnosticProvider);

  const treeProvider = new GxTreeProvider(
    shadowService.shadowRoot,
    context.extensionUri,
  );
  activeTreeProvider = treeProvider;
  activeShadowService = shadowService;

  const shadowManager = new ShadowManager(
    context,
    provider,
    shadowService,
    diagnosticProvider,
  );
  shadowManager.register();

  // UI Components
  const treeView = vscode.window.createTreeView(VIEW_EXPLORER, {
    treeDataProvider: treeProvider,
    showCollapseAll: true,
  });
  context.subscriptions.push(treeView);

  treeView.onDidChangeSelection((e) => {
    if (e.selection.length > 0) {
      const item: any = e.selection[0];
      if (item && item.resourceUri) {
        provider.preWarm(item.resourceUri);
      }
    }
  });

  const actionsProvider = new GxActionsProvider();
  vscode.window.createTreeView(VIEW_ACTIONS, {
    treeDataProvider: actionsProvider,
    showCollapseAll: false,
  });

  const providerManager = new ProviderManager(context, provider, shadowService);
  providerManager.register();

  const syncManager = new SyncManager(context, provider, shadowService);
  syncManager.register();
  context.subscriptions.push({ dispose: () => syncManager.dispose() });

  const commandManager = new CommandManager(
    context,
    provider,
    treeProvider,
    diagnosticProvider,
    contextManager,
    providerManager.historyProvider,
  );
  commandManager.register();

  contextManager.register();
  diagnosticProvider.subscribeToEvents(context);

  let pendingBackendRecovery: Promise<boolean> | undefined;
  const pendingMirrorHydrations = new Map<string, Promise<void>>();
  const recoverBackend = async (
    reason: string,
    report?: BootstrapReporter,
  ): Promise<boolean> => {
    if (!pendingBackendRecovery) {
      pendingBackendRecovery = (async () => {
        reportBootstrapStatus(
          `Backend unavailable during ${reason}. Attempting recovery...`,
          report,
        );
        const started = await backendManager.start(provider, true);
        if (!started) {
          reportBootstrapStatus(
            `Backend recovery could not start during ${reason}.`,
            report,
          );
          return false;
        }

        reportBootstrapStatus(
          `Backend recovery completed for ${reason}.`,
          report,
        );
        return true;
      })().finally(() => {
        pendingBackendRecovery = undefined;
      });
    }

    return pendingBackendRecovery;
  };

  // Recovery callback: re-materialize workspace when backend restarts
  backendManager.onRecovered = async () => {
    Logger.info("[Nexus IDE] Backend recovered. Re-materializing workspace...");
    try {
      await provider.initKb();
      if (!shadowService.hasMaterializedWorkspace()) {
        provider.isWorkspaceHydrating = true;
        await shadowService.materializeWorkspace(provider);
        provider.isWorkspaceHydrating = false;
      }
      treeProvider.refresh();
      provider.clearDirCache();
      vscode.window.setStatusBarMessage(
        "$(check) GeneXus: Workspace recuperado",
        DEFAULT_STATUS_BAR_TIMEOUT,
      );
    } catch (e) {
      provider.isWorkspaceHydrating = false;
      Logger.error(`[Nexus IDE] Re-materialization after recovery failed: ${e}`);
    }
  };

  // Start Backend and register discovery tools
  const startTrustedBackend = async (reason: string): Promise<void> => {
    if (!BackendManager.isWorkspaceTrusted()) {
      Logger.info(`[Nexus IDE] Workspace trust is required before starting the backend (${reason}).`);
      return;
    }

    const started = await backendManager.start(provider, true);
    if (!started) {
      Logger.warn(`[Nexus IDE] Backend startup was skipped or aborted (${reason}).`);
    }
  };

  if (!BackendManager.isWorkspaceTrusted()) {
    context.subscriptions.push(
      vscode.workspace.onDidGrantWorkspaceTrust(() => {
        void startTrustedBackend("workspace trust granted");
      }),
    );
  }

  backendManager
    .start(provider)
    .then(async (started) => {
      if (!started) {
        Logger.warn("[Nexus IDE] Backend startup was skipped or aborted.");
        return;
      }
      if (IS_TEST_MODE) {
        Logger.info("[Nexus IDE] Test mode: skipping live MCP discovery registration.");
        return;
      }
      if (context.extensionMode === vscode.ExtensionMode.Development) {
        Logger.info(
          "[Nexus IDE] Backend started successfully. Skipping discovery registration in development mode.",
        );
      } else {
        Logger.info(
          "[Nexus IDE] Backend started successfully. Registering discovery tools...",
        );
        discoveryManager.register();
      }

      try {
        await bootstrapKbExplorer(context, provider, shadowService, treeProvider, {
          reason: "startup",
          skipBackendStart: true,
        });
      } catch (kbError) {
        Logger.error(`[Nexus IDE] KB init failed: ${kbError}`);
      }
    })
    .catch((e) => Logger.error(`[Nexus IDE] Backend start failed: ${e}`));

  // Watch for Save event

  const hydrateMirrorDocument = async (doc: vscode.TextDocument): Promise<void> => {
    const hydrateKey = doc.uri.toString();
    const existingHydration = pendingMirrorHydrations.get(hydrateKey);
    if (existingHydration) {
      await existingHydration;
      return;
    }

    const runHydration = async (): Promise<void> => {
      if (!GxUriParser.isGeneXusUri(doc.uri) || doc.uri.scheme !== "file") {
        return;
      }

      const shouldHydrate =
        doc.getText().startsWith("// GXMCP_PLACEHOLDER:") ||
        shadowService.isPlaceholder(doc.uri.fsPath) ||
        containsReplacementCharacter(doc.getText());

      if (!shouldHydrate) {
        return;
      }

      Logger.info(`[Nexus IDE] Hydrating mirror file: ${doc.uri.fsPath}`);
      provider.beginInteractiveHydration();

      let hydrated = false;
      let hydrationError: unknown;
      try {
        hydrated = await shadowService.hydrateOpenedFile(
          doc.uri,
          provider,
          doc.getText(),
        );
      } catch (error) {
        hydrationError = error;
        if (isGatewayConnectionRefused(error)) {
          try {
            const recovered = await recoverBackend("file hydration");
            if (recovered) {
              hydrated = await shadowService.hydrateOpenedFile(
                doc.uri,
                provider,
                doc.getText(),
              );
            }
          } catch (recoveryError) {
            Logger.error(
              `[Nexus IDE] Backend recovery failed for ${doc.uri.fsPath}: ${recoveryError}`,
            );
            return;
          }
        } else {
          Logger.error(`[Nexus IDE] Hydration failed for ${doc.uri.fsPath}: ${error}`);
          return;
        }
      } finally {
        provider.endInteractiveHydration();
      }
      if (!hydrated && shadowService.isPlaceholder(doc.uri.fsPath)) {
        Logger.error(
          `[Nexus IDE] Hydration failed for ${doc.uri.fsPath}: ${hydrationError ?? "hydrate returned false"}`,
        );
      }
      if (!hydrated || doc.isDirty) {
        return;
      }

      let hydratedText: string;
      try {
        hydratedText = fs.readFileSync(doc.uri.fsPath, "utf8");
      } catch (error) {
        Logger.error(`[Nexus IDE] Failed to read hydrated mirror file: ${error}`);
        return;
      }

      if (
        !hydratedText ||
        hydratedText.startsWith("// GXMCP_PLACEHOLDER:") ||
        hydratedText === doc.getText()
      ) {
        return;
      }

      provider.fireFileChange(doc.uri);
      const activeEditor = vscode.window.activeTextEditor;
      if (activeEditor?.document.uri.toString() === doc.uri.toString()) {
        try {
          await vscode.commands.executeCommand("workbench.action.files.revert");
        } catch (error) {
          Logger.error(
            `[Nexus IDE] Failed to revert hydrated mirror file ${doc.uri.fsPath}: ${error}`,
          );
        }
      }

      setTimeout(() => {
        const refreshedDoc = vscode.workspace.textDocuments.find(
          (candidate) => candidate.uri.toString() === doc.uri.toString(),
        );
        if (refreshedDoc) {
          void diagnosticProvider.refreshDiagnostics(refreshedDoc);
        }
      }, 2000);
    };

    const hydrationPromise = runHydration().finally(() => {
      pendingMirrorHydrations.delete(hydrateKey);
    });
    pendingMirrorHydrations.set(hydrateKey, hydrationPromise);
    await hydrationPromise;
  };

  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((doc: vscode.TextDocument) => {
      if (GxUriParser.isGeneXusUri(doc.uri)) {
        setTimeout(() => {
          diagnosticProvider.refreshAll();
        }, 1000);
      }
    }),
    vscode.workspace.onDidOpenTextDocument(async (doc: vscode.TextDocument) => {
      await hydrateMirrorDocument(doc);
    }),
    vscode.window.onDidChangeActiveTextEditor(async (editor) => {
      if (!editor) {
        return;
      }

      await hydrateMirrorDocument(editor.document);
    }),
  );

  const visibleMirrorDocs = Array.from(
    new Map(
      vscode.window.visibleTextEditors.map((editor) => [
        editor.document.uri.toString(),
        editor.document,
      ]),
    ).values(),
  );

  void Promise.all(
    visibleMirrorDocs.map((doc) => hydrateMirrorDocument(doc)),
  ).catch((error) => {
    Logger.error(`[Nexus IDE] Initial mirror hydration failed: ${error}`);
  });

  Logger.info("[Nexus IDE] Deferred initialization complete.");
}

export function deactivate() {
  appendLifecycleLog("[Nexus IDE] deactivate()");
  isActivated = false;
  if (backendManager) {
    backendManager.stop();
  }
}
