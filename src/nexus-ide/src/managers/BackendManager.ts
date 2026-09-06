import * as vscode from "vscode";
import * as path from "path";
import * as fs from "fs";
import * as cp from "child_process";
import { GxFileSystemProvider } from "../gxFileSystem";
import { Logger } from "../utils/Logger";
import {
  buildGatewayIdentity,
  GATEWAY_LEASE_STALE_AFTER_MS,
  GatewayIdentity,
  readGatewayLease,
  readJsonFile,
  resolveGatewayConfigPath,
  resolveGatewayHttpPort,
} from "../utils/GatewayConfig";
import { 
  CONFIG_SECTION, 
  CONFIG_AUTO_START, 
  CONFIG_KB_PATH,
  CONFIG_INSTALL_PATH,
  HEALTH_CHECK_INTERVAL,
  HEALTH_CHECK_TIMEOUT,
  HEALTH_CHECK_TIMEOUT_INDEXING
} from "../constants";

export class BackendManager {
  private backendProcess: cp.ChildProcess | undefined;
  private healthMonitor: BackendHealthMonitor | undefined;
  private ownsBackendProcess = false;
  public onRecovered: (() => Promise<void>) | undefined;
  private static readonly STARTUP_RETRIES = 20;
  private static readonly STARTUP_DELAY_MS = 1500;
  private readonly backendLogPath: string;

  constructor(private readonly context: vscode.ExtensionContext) {
    this.backendLogPath = path.join(
      this.context.extensionPath,
      "backend_manager_debug.log",
    );
  }

  /**
   * Starting the Gateway persists KB/install configuration and spawns a local
   * process. VS Code must explicitly trust the workspace before either side
   * effect is allowed.
   */
  static isWorkspaceTrusted(): boolean {
    return BackendManager.canStartWorkspace(vscode.workspace.isTrusted);
  }

  static canStartWorkspace(isTrusted: boolean | undefined): boolean {
    return isTrusted !== false;
  }

  static shouldPersistRuntimeConfig(forceStart: boolean, autoStart: boolean | undefined): boolean {
    return forceStart || autoStart === true;
  }

  async start(provider: GxFileSystemProvider, forceStart = false): Promise<boolean> {
    if (!BackendManager.isWorkspaceTrusted()) {
      this.trace("Backend startup blocked because the workspace is not trusted.");
      Logger.warn(
        "[BackendManager] Workspace is not trusted; backend startup and config persistence are blocked.",
      );
      return false;
    }

    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    const autoStart = config.get<boolean>(CONFIG_AUTO_START, false);

    const resolvedBackend = this.resolveBackendDirectory();
    const backendDir = resolvedBackend.backendDir;
    const gatewayExe = resolvedBackend.gatewayExe;

    const configFile = resolveGatewayConfigPath(this.context.extensionPath);

    if (!fs.existsSync(gatewayExe)) {
      this.trace(`Gateway executable not found: ${gatewayExe}`);
      vscode.window.showErrorMessage(
        "GeneXus MCP Gateway not found. Please build the project or check installation.",
      );
      return false;
    }

    let persistedConfig: any = undefined;
    if (fs.existsSync(configFile)) {
      try {
        persistedConfig = readJsonFile(configFile);
      } catch (e) {
        Logger.error(`[BackendManager] Failed to read canonical config.json: ${e}`);
      }
    }

    const kbPath =
      (await this.findBestKbPath()) ||
      persistedConfig?.Environment?.KBPath ||
      "";
    const installationPath =
      this.findBestInstallationPath() ||
      persistedConfig?.GeneXus?.InstallationPath ||
      "";

    if (!kbPath || !installationPath) {
      this.trace(
        `Auto-start aborted. kbPath='${kbPath}' installationPath='${installationPath}'`,
      );
      Logger.info(
        "[BackendManager] Missing KB Path or Installation Path. Auto-start aborted.",
      );
      return false;
    }

    const gatewayIdentity = buildGatewayIdentity(
      this.context.extensionPath,
      config,
      kbPath,
      installationPath,
    );

    if (await this.isGatewayAlreadyReady(provider, gatewayIdentity)) {
      Logger.info("[BackendManager] Reusing already running MCP Gateway.");
      this.trace(
        `gateway_reused pid=${readGatewayLease(gatewayIdentity.leasePath)?.processId ?? "unknown"} key=${gatewayIdentity.instanceKey}`,
      );
      this.ownsBackendProcess = false;
      this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
      this.healthMonitor.start();
      return true;
    }

    if (!BackendManager.shouldPersistRuntimeConfig(forceStart, autoStart)) {
      this.trace("Auto-start disabled and no ready gateway was detected.");
      return false;
    }

    // Persist only after an effective start was authorized. In particular,
    // autoStart=false must not rewrite the shared config as a background side
    // effect while the extension is merely probing for a ready gateway.
    this.persistRuntimeConfig(
      configFile,
      persistedConfig,
      installationPath,
      kbPath,
      config,
    );

    await this.cleanupBrokenGatewayInstance(gatewayIdentity, provider);

    Logger.info("[BackendManager] Starting MCP Gateway...");
    this.trace(`Starting MCP Gateway. backendDir='${backendDir}' configFile='${configFile}' effectivePort='${effectivePortPreview(config)}'`);
    try {
      const effectivePort = this.getEffectivePort(config);
      const launchSpec = this.resolveLaunchSpec(backendDir);
      Logger.info(
        `[BackendManager] Launch command: ${launchSpec.command} ${launchSpec.args.join(" ")}`.trim(),
      );
      this.trace(
        `Launch command: ${launchSpec.command} ${launchSpec.args.join(" ")}`.trim(),
      );

      this.backendProcess = cp.spawn(launchSpec.command, launchSpec.args, {
        cwd: backendDir,
        detached: false,
        stdio: ["pipe", "ignore", "ignore"],
        windowsHide: true,
        env: {
          ...process.env,
          GX_CONFIG_PATH: configFile,
          GX_MCP_PORT: String(effectivePort),
          GX_MCP_STDIO: "false",
        },
      });
      this.ownsBackendProcess = true;
      this.trace(`Spawned gateway PID=${this.backendProcess?.pid ?? "unknown"}`);

      if (this.backendProcess) {
        this.backendProcess.on("error", (error) => {
          this.trace(`Gateway spawn error: ${error.message}`);
          Logger.error(`[BackendManager] Gateway spawn failed: ${error}`);
        });

        this.backendProcess.on("exit", (code) => {
          this.trace(`Gateway process exit. code=${code ?? "null"}`);
          Logger.info(`[BackendManager] Gateway exited with code ${code}`);
          this.backendProcess = undefined;
        });
      }
    } catch (e) {
      this.trace(`Failed to spawn gateway: ${String(e)}`);
      Logger.error(`[BackendManager] Failed to spawn Gateway: ${e}`);
    }

    await this.waitForGatewayReady(provider);
    this.trace("waitForGatewayReady completed successfully.");

    this.healthMonitor = new BackendHealthMonitor(provider, this.context, this);
    this.healthMonitor.start();
    return true;
  }

  stop() {
    this.trace(
      `stop() called. ownsBackendProcess=${this.ownsBackendProcess} pid=${this.backendProcess?.pid ?? "none"}`,
    );
    this.healthMonitor?.stop();
    if (this.backendProcess && this.ownsBackendProcess) {
      this.backendProcess.kill();
    }
    this.backendProcess = undefined;
    this.ownsBackendProcess = false;
  }

  private async findBestKbPath(): Promise<string> {
    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    const kbPath = config.get<string>(CONFIG_KB_PATH, "");

    if (kbPath && fs.existsSync(kbPath)) {
      return kbPath;
    }

    try {
      Logger.info("[BackendManager] Searching for .gxw files...");
      const files = await vscode.workspace.findFiles(
        "*.gxw",
        "**/node_modules/**",
        1,
      );
      Logger.info(
        `[BackendManager] findFiles returned ${files.length} results.`,
      );
      if (files.length > 0) {
        const found = path.dirname(files[0].fsPath);
        Logger.info(`[BackendManager] Found KB at: ${found}`);
        return found;
      }
    } catch (e) {
      Logger.error(`[BackendManager] Error in findFiles: ${e}`);
    }

    // Use configuration or empty string, no hardcoded defaults
    return "";
  }

  private findBestInstallationPath(): string {
    const config = vscode.workspace.getConfiguration(CONFIG_SECTION);
    const currentPath = config.get<string>(CONFIG_INSTALL_PATH, "");
    return currentPath;
  }

  private getEffectivePort(config: vscode.WorkspaceConfiguration): number {
    return resolveGatewayHttpPort(this.context.extensionPath, config);
  }

  private persistRuntimeConfig(
    configFile: string,
    persistedConfig: any,
    installationPath: string,
    kbPath: string,
    config: vscode.WorkspaceConfiguration,
  ): void {
    if (!fs.existsSync(configFile)) return;
    try {
      const currentConfig = persistedConfig ?? readJsonFile(configFile);
      currentConfig.GeneXus = currentConfig.GeneXus || {};
      currentConfig.Environment = currentConfig.Environment || {};
      currentConfig.Server = currentConfig.Server || {};
      currentConfig.GeneXus.InstallationPath = installationPath;
      currentConfig.Environment.KBPath = kbPath;
      currentConfig.Server.HttpPort = this.getEffectivePort(config);
      fs.writeFileSync(configFile, JSON.stringify(currentConfig, null, 2));
    } catch (e) {
      Logger.error(`[BackendManager] Failed to update canonical config.json: ${e}`);
    }
  }

  async restart(provider: GxFileSystemProvider, forceStart = false) {
    this.stop();
    await this.start(provider, forceStart);
    if (this.onRecovered) {
      try { await this.onRecovered(); }
      catch (e) { Logger.error(`[BackendManager] onRecovered callback failed: ${e}`); }
    }
  }

  private resolveLaunchSpec(backendDir: string): { command: string; args: string[] } {
    const gatewayDll = path.join(backendDir, "GxMcp.Gateway.dll");
    const gatewayExe = path.join(backendDir, "GxMcp.Gateway.exe");

    if (fs.existsSync(gatewayExe)) {
      return {
        command: gatewayExe,
        args: [],
      };
    }

    if (fs.existsSync(gatewayDll)) {
      return {
        command: "dotnet",
        args: [gatewayDll],
      };
    }

    return {
      command: gatewayExe,
      args: [],
    };
  }

  private resolveBackendDirectory(): { backendDir: string; gatewayExe: string } {
    // Packaged backend/ under the extension install dir is the FIRST, authoritative
    // resolution. Dev-tree bin/publish paths are only consulted when we can prove
    // we're running from the dev checkout (extensionMode === Development) - never
    // let a dev path win in a packaged install.
    const packagedBackendDir = path.join(this.context.extensionPath, "backend");
    const packagedGatewayExe = path.join(packagedBackendDir, "GxMcp.Gateway.exe");

    if (this.context.extensionMode === vscode.ExtensionMode.Development) {
      const devGatewayDir = path.join(
        this.context.extensionPath,
        "..",
        "GxMcp.Gateway",
        "bin",
        "Debug",
        "net10.0-windows",
      );
      const devGatewayExe = path.join(devGatewayDir, "GxMcp.Gateway.exe");

      if (fs.existsSync(devGatewayExe)) {
        Logger.info(`[BackendManager] Using development gateway at: ${devGatewayDir}`);
        return {
          backendDir: devGatewayDir,
          gatewayExe: devGatewayExe,
        };
      }

      const publishDir = path.join(this.context.extensionPath, "..", "..", "publish");
      const publishGatewayExe = path.join(publishDir, "GxMcp.Gateway.exe");
      if (fs.existsSync(publishGatewayExe)) {
        Logger.info(`[BackendManager] Using development publish backend at: ${publishDir}`);
        return {
          backendDir: publishDir,
          gatewayExe: publishGatewayExe,
        };
      }
    }

    return {
      backendDir: packagedBackendDir,
      gatewayExe: packagedGatewayExe,
    };
  }

  private async cleanupBrokenGatewayInstance(
    gatewayIdentity: GatewayIdentity,
    provider: GxFileSystemProvider,
  ): Promise<void> {
    const lease = readGatewayLease(gatewayIdentity.leasePath);
    if (!lease) {
      return;
    }

    if (lease.instanceKey !== gatewayIdentity.instanceKey) {
      return;
    }

    const leaseAgeMs = Date.now() - Date.parse(lease.updatedUtc);
    const processAlive = this.isProcessAlive(lease.processId);
    if (!processAlive) {
      this.trace(`lease_recovered pid=${lease.processId} key=${gatewayIdentity.instanceKey}`);
      this.deleteLeaseFile(gatewayIdentity.leasePath);
      return;
    }

    if (leaseAgeMs <= GATEWAY_LEASE_STALE_AFTER_MS) {
      return;
    }

    try {
      const status = await provider.callMcpMethod("ping", undefined, 2000);
      if (status) {
        return;
      }
    } catch (e) {
      // Load-bearing: ping throwing here confirms the leased gateway isn't
      // responding, so control falls through to the taskkill below.
      Logger.debug(`[BackendManager] Stale-lease ping failed (falling through to cleanup): ${e}`);
    }

    try {
      cp.spawnSync(
        "taskkill.exe",
        ["/PID", String(lease.processId), "/T", "/F"],
        { stdio: "ignore", windowsHide: true },
      );
      this.trace(`duplicate_instance_prevented pid=${lease.processId} key=${gatewayIdentity.instanceKey}`);
      this.deleteLeaseFile(gatewayIdentity.leasePath);
    } catch (error) {
      this.trace(`Failed to cleanup stale gateway pid=${lease.processId}: ${String(error)}`);
    }
  }

  private async waitForGatewayReady(provider: GxFileSystemProvider): Promise<void> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= BackendManager.STARTUP_RETRIES; attempt++) {
      try {
        const status = await provider.callMcpMethod("ping", undefined, 2000);
        if (status) {
          this.trace(`Gateway ready after ${attempt} attempt(s).`);
          Logger.info(
            `[BackendManager] Gateway ready after ${attempt} attempt(s).`,
          );
          return;
        }
      } catch (e) {
        lastError = e;
        this.trace(`Gateway not ready on attempt ${attempt}: ${String(e)}`);
      }

      await new Promise((resolve) =>
        setTimeout(resolve, BackendManager.STARTUP_DELAY_MS),
      );
    }

    throw new Error(
      `Gateway did not become ready in time. Last error: ${lastError}`,
    );
  }

  private async isGatewayAlreadyReady(
    provider: GxFileSystemProvider,
    gatewayIdentity: GatewayIdentity,
  ): Promise<boolean> {
    try {
      const status = await provider.callMcpMethod("ping", undefined, 5000);
      if (!status) {
        return false;
      }
    } catch (e) {
      Logger.debug(`[BackendManager] isGatewayAlreadyReady ping failed: ${e}`);
      return false;
    }

    const lease = readGatewayLease(gatewayIdentity.leasePath);
    if (!lease) {
      this.trace(`gateway_reused port_only port=${gatewayIdentity.port}`);
      return true;
    }

    if (lease.instanceKey !== gatewayIdentity.instanceKey) {
      this.trace(
        `gateway_reused lease_mismatch port=${gatewayIdentity.port} liveKey=${lease.instanceKey} expectedKey=${gatewayIdentity.instanceKey}`,
      );
      return true;
    }

    if (!this.isProcessAlive(lease.processId)) {
      this.trace(`lease_recovered pid=${lease.processId} key=${gatewayIdentity.instanceKey}`);
      this.deleteLeaseFile(gatewayIdentity.leasePath);
      return false;
    }

    const leaseAgeMs = Date.now() - Date.parse(lease.updatedUtc);
    if (leaseAgeMs > GATEWAY_LEASE_STALE_AFTER_MS) {
      this.trace(`Lease is stale for key=${gatewayIdentity.instanceKey} pid=${lease.processId}`);
      return false;
    }

    return true;
  }

  private trace(message: string): void {
    try {
      fs.appendFileSync(
        this.backendLogPath,
        `[${new Date().toISOString()}] ${message}\n`,
      );
    } catch (e) {
      Logger.debug(`[BackendManager] Failed to append trace log: ${e}`);
    }
  }

  private isProcessAlive(processId: number): boolean {
    if (!Number.isInteger(processId) || processId <= 0) {
      return false;
    }

    try {
      process.kill(processId, 0);
      return true;
    } catch {
      // process.kill(pid, 0) throwing ESRCH is the normal/expected way this
      // check reports "not alive" - not a failure worth logging (called on
      // every health-check tick).
      return false;
    }
  }

  private deleteLeaseFile(leasePath: string): void {
    try {
      if (fs.existsSync(leasePath)) {
        fs.unlinkSync(leasePath);
      }
    } catch (e) {
      Logger.debug(`[BackendManager] Failed to delete lease file ${leasePath}: ${e}`);
    }
  }
}

function effectivePortPreview(config: vscode.WorkspaceConfiguration): number {
  return resolveGatewayHttpPort(
    path.resolve(__dirname, "..", ".."),
    config,
  );
}

class BackendHealthMonitor {
  private _interval: NodeJS.Timeout | undefined;
  private _consecutiveFailures = 0;
  private _isRestarting = false;

  constructor(
    private readonly provider: GxFileSystemProvider,
    private readonly context: vscode.ExtensionContext,
    private readonly manager: BackendManager,
  ) {}

  start() {
    if (this._interval) return;
    this._interval = setInterval(() => this.check(), HEALTH_CHECK_INTERVAL);
  }

  async check() {
    if (this._isRestarting) return;

    const isIndexing = this.provider.isBulkIndexing;
    const timeout = isIndexing ? HEALTH_CHECK_TIMEOUT_INDEXING : HEALTH_CHECK_TIMEOUT;

    try {
      const status = await this.provider.callMcpMethod("ping", undefined, timeout);
      if (status) {
        this._consecutiveFailures = 0;
      } else {
        throw new Error("No response");
      }
    } catch {
      if (isIndexing) return;

      this._consecutiveFailures++;
      if (this._consecutiveFailures >= 3) {
        this.showWarning();
      }
    }
  }

  private async showWarning() {
    const selection = await vscode.window.showWarningMessage(
      "GeneXus MCP Server parou de responder.",
      "Restart Services",
      "Wait",
    );

    if (selection === "Restart Services") {
      this._isRestarting = true;
      await this.manager.restart(this.provider);
      this._isRestarting = false;
      this._consecutiveFailures = 0;
    } else {
      this._consecutiveFailures = 0;
    }
  }

  stop() {
    if (this._interval) clearInterval(this._interval);
  }
}
