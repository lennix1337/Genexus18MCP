import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { GxShadowService } from "./gxShadowService";
import { GxDiagnosticProvider } from "./diagnosticProvider";
import { GxGatewayClient } from "./infra/GxGatewayClient";
import { GxUriParser } from "./utils/GxUriParser";
import { formatMcpErrorMessage } from "./utils/McpErrorFormatter";
import { GxPartMapper, TYPE_SUFFIX, VALID_TYPES } from "./utils/GxPartMapper";
import { GxCacheManager } from "./managers/GxCacheManager";
import {
  GX_SCHEME,
  DEFAULT_MCP_PORT,
  MODULE_SEARCH,
  MODULE_KB,
  MODULE_HEALTH,
  DEFAULT_STATUS_BAR_TIMEOUT,
  ROOT_PARENT_NAME,
} from "./constants";

export { TYPE_SUFFIX };

const BROWSE_QUERY_LIMIT = 5000;
const BROWSE_FALLBACK_TYPES = [
  "Folder",
  "Module",
  "Procedure",
  "Transaction",
  "WebPanel",
  "DataProvider",
  "SDT",
  "StructuredDataType",
  "Domain",
  "Image",
  "Table",
  "DataView",
  "Attribute",
  "SDPanel",
  "WorkPanel",
  "ExternalObject",
  "Menu",
];

export class GxFileSystemProvider implements vscode.FileSystemProvider {
  private _emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile: vscode.Event<vscode.FileChangeEvent[]> =
    this._emitter.event;

  private _gateway: GxGatewayClient;
  private _cache: GxCacheManager;
  private _shadowService?: GxShadowService;
  private _diagnosticProvider?: GxDiagnosticProvider;
  private _metadataWarmupStarted: boolean = false;
  public isBulkIndexing: boolean = false;
  public isWorkspaceHydrating: boolean = false;
  private _interactiveHydrations = 0;

  private _kbInitPromise: Promise<any> | null = null;
  private _pendingQueryRequests = new Map<string, Promise<any>>();

  constructor() {
    this._cache = new GxCacheManager();
    this._gateway = new GxGatewayClient(
      `http://127.0.0.1:${DEFAULT_MCP_PORT}/mcp`,
    );
  }

  public set baseUrl(value: string) {
    this._gateway.baseUrl = value;
  }
  public get baseUrl(): string {
    return this._gateway.baseUrl;
  }

  public setShadowService(service: GxShadowService) {
    this._shadowService = service;
    (this._gateway as any)._shadowService = service;
  }

  public ensureMirrorPartFile(
    type: string,
    name: string,
    part: string,
  ): vscode.Uri | null {
    const filePath = this._shadowService?.ensureMirrorPartFile({ type, name }, part);
    return filePath ? vscode.Uri.file(filePath) : null;
  }

  public setDiagnosticProvider(provider: GxDiagnosticProvider) {
    this._diagnosticProvider = provider;
  }

  public setPart(uri: vscode.Uri, partName: string) {
    this._cache.filePartState.set(uri.path, partName);
    this._cache.invalidatePartCache(uri.toString(), partName);
    this._cache.mtimes.set(uri.toString(), Date.now());
    this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
  }

  public fireFileChange(uri: vscode.Uri) {
    const uriStr = uri.toString();
    this._cache.contentCache.delete(uriStr);
    this._cache.partsCache.delete(uriStr);
    for (const key of this._cache.metadataCache.keys()) {
      if (key.startsWith(uriStr + ":")) {
        this._cache.metadataCache.delete(key);
      }
    }
    this._cache.mtimes.set(uriStr, Date.now());
    this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
  }

  public beginInteractiveHydration(): void {
    this._interactiveHydrations++;
  }

  public endInteractiveHydration(): void {
    this._interactiveHydrations = Math.max(0, this._interactiveHydrations - 1);
  }

  public get hasInteractiveHydration(): boolean {
    return this._interactiveHydrations > 0;
  }

  public getPart(uri: vscode.Uri): string {
    const parsed = GxUriParser.parse(uri);
    if (parsed?.part) {
      return parsed.part;
    }
    return GxPartMapper.getPart(uri, this._cache.filePartState);
  }

  public async initKb() {
    if (this._kbInitPromise) return this._kbInitPromise;

    console.log("[Nexus IDE] Warming up KB...");
    try {
      await this.callMcpMethod("ping", undefined, 2000);
    } catch {}

    this._kbInitPromise = (async () => {
      let lastError: unknown;
      for (let attempt = 1; attempt <= 20; attempt++) {
        try {
          return await this.callMcpMethod("ping", undefined, 5000);
        } catch (error) {
          lastError = error;
          await new Promise((resolve) => setTimeout(resolve, 1500));
        }
      }

      throw lastError ?? new Error("Gateway did not respond to KB initialization ping.");
    })();

    this._kbInitPromise.then(() => {
      console.log("[Nexus IDE] KB SDK Init complete. Refreshing Root...");
      this._emitter.fire([
        {
          type: vscode.FileChangeType.Changed,
          uri: vscode.Uri.from({ scheme: GX_SCHEME, path: "/" }),
        },
      ]);
    }).catch((error) => {
      console.error("[Nexus IDE] KB init failed:", error);
      this._kbInitPromise = null;
    });

    return this._kbInitPromise;
  }

  public warmMetadataAfterBootstrap(): void {
    if (this._metadataWarmupStarted) {
      return;
    }

    this._metadataWarmupStarted = true;
    setTimeout(() => {
      void this.shadowMetadata().catch((error) => {
        console.error("[Nexus IDE] Deferred metadata warmup failed:", error);
      });
    }, 1000);
  }

  private async shadowMetadata() {
    try {
      const result = await this.queryObjects(
        "type:Transaction or type:SDT",
        5,
        120000,
      );

      if (result && result.results) {
        for (const obj of result.results) {
          const suffix = TYPE_SUFFIX[obj.type]
            ? `.${TYPE_SUFFIX[obj.type]}`
            : "";
          const uri = vscode.Uri.parse(
            `${GX_SCHEME}:/${obj.type}/${obj.name}${suffix}.gx`,
          );
          const part = obj.type === "Transaction" ? "Structure" : "Source";
          void this.fetchAndCacheMetadata(uri, obj.type, obj.name, part);
          await new Promise((resolve) => setTimeout(resolve, 150));
        }
      }
    } catch (e) {
      console.error("[Nexus IDE] Shadowing failed:", e);
    }
  }

  private async fetchAndCacheMetadata(
    uri: vscode.Uri,
    objType: string,
    objName: string,
    partName: string,
  ) {
    try {
      const target = VALID_TYPES.has(objType)
        ? `${objType}:${objName}`
        : objName;
      const res = await this.callMcpTool(
        "genexus_read",
        {
          name: target,
          part: partName,
        },
        15000,
      );
      if (res && typeof res.source === "string") {
        let decoded = res.isBase64
          ? Buffer.from(res.source, "base64").toString("utf8")
          : res.source;
        this._cache.metadataCache.set(uri.toString() + ":" + partName, decoded);
      }
    } catch {}
  }

  watch(
    _uri: vscode.Uri,
    _options: { recursive: boolean; excludes: string[] },
  ): vscode.Disposable {
    return new vscode.Disposable(() => {});
  }

  stat(uri: vscode.Uri): vscode.FileStat {
    const info = GxUriParser.parse(uri);

    // Support IDE metadata probes (VS Code / Antigravity)
    if (
      info?.path === ".vscode" ||
      info?.path === ".mcp" ||
      info?.path === ".antigravity"
    ) {
      return {
        type: vscode.FileType.Directory,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }
    if (
      info?.path.includes("mcp.json") ||
      info?.path.includes("tasks.json") ||
      info?.path.includes("settings.json")
    ) {
      return {
        type: vscode.FileType.File,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }

    if (info?.path.startsWith(".") && !info?.path.startsWith(".gx")) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }

    if (info?.path === "" || !info?.path.endsWith(".gx")) {
      return {
        type: vscode.FileType.Directory,
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0,
      };
    }

    const cachedContent = this._cache.contentCache.get(uri.toString());
    let size = cachedContent ? cachedContent.byteLength : 0;

    if (size === 0) {
      const info = GxUriParser.parse(uri);
      if (info) {
        const shadowPath = path.join(
          this._shadowService?.shadowRoot || "",
          info.type,
          `${info.name}.gx`,
        );
        size = fs.existsSync(shadowPath) ? fs.statSync(shadowPath).size : 0;
      }
    }

    if (!this._cache.mtimes.has(uri.toString()))
      this._cache.mtimes.set(uri.toString(), Date.now());

    return {
      type: vscode.FileType.File,
      ctime: Date.now(),
      mtime: this._cache.mtimes.get(uri.toString())!,
      size: size,
    };
  }

  async readDirectory(uri: vscode.Uri): Promise<[string, vscode.FileType][]> {
    try {
      const info = GxUriParser.parse(uri);
      const pathStr = info ? info.path : "";

      const parentName = (info?.path === "" || !info)
        ? ROOT_PARENT_NAME
        : info.path.split("/").pop()!;
      const pathSegments = pathStr
        ? pathStr.split("/").filter((segment) => segment.length > 0)
        : [];
      const cacheKey = `dir:${pathStr}`;

      const cached = this._cache.dirCache.get(cacheKey);
      if (cached && Date.now() - cached.time < 300000) {
        return cached.entries;
      }

      let objects: any[] = [];

      if (VALID_TYPES.has(parentName) && pathSegments.length > 0) {
        const result = await this.queryObjects(
          `type:${parentName} @quick`,
          BROWSE_QUERY_LIMIT,
          60000,
        );
        objects = Array.isArray(result?.results)
          ? result.results
          : Array.isArray(result)
            ? result
            : [];
      } else {
        objects = await this.browseObjects(pathStr);
      }

      if (Array.isArray(objects)) {
        const mapped = objects.map((obj: any) => {
          const isDir = obj.type === "Folder" || obj.type === "Module";
          const suffix =
            !isDir && TYPE_SUFFIX[obj.type] ? `.${TYPE_SUFFIX[obj.type]}` : "";
          const name = isDir ? obj.name : `${obj.name}${suffix}.gx`;
          return [
            name,
            isDir ? vscode.FileType.Directory : vscode.FileType.File,
          ];
        }) as [string, vscode.FileType][];

        this._cache.dirCache.set(cacheKey, {
          entries: mapped,
          time: Date.now(),
        });
        return mapped;
      }
    } catch (e) {
      console.error(`[GxFS] readDirectory error for ${uri.toString()}:`, e);
    }
    return [];
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const partName = this.getPart(uri);
    const uriStr = uri.toString();
    const cacheKey = this._cache.getCacheKey(uri, partName);

    const pCache = this._cache.partsCache.get(uriStr);
    if (pCache && pCache.has(partName)) return pCache.get(partName)!;

    const shadowed = this._cache.metadataCache.get(uriStr + ":" + partName);
    if (shadowed) return Buffer.from(shadowed, "utf8");

    if (this._cache.contentCache.has(uriStr))
      return this._cache.contentCache.get(uriStr)!;

    const cached = this._cache.readCache.get(cacheKey);
    if (cached && Date.now() - cached.time < 30000) return cached.data;

    if (this._cache.pendingReadRequests.has(cacheKey))
      return this._cache.pendingReadRequests.get(cacheKey)!;

    const request = (async () => {
      const info = GxUriParser.parse(uri);
      const target = info ? `${info.type}:${info.name}` : null;
      if (!target) {
        return Buffer.alloc(0);
      }
      try {
        const partsToFetch = Array.from(
          new Set(["Source", "Rules", "Events", "Variables", partName]),
        );
        const allPartsResult = await this.callMcpTool(
          "genexus_batch_read",
          {
            items: partsToFetch.map((part) => ({ name: target, part })),
          },
          120000,
        );
        if (allPartsResult && Array.isArray(allPartsResult.results)) {
          const newPCache = new Map<string, Uint8Array>();
          for (const partResult of allPartsResult.results) {
            const fetchedPart = partResult?.part || partResult?.requestedPart;
            const source = partResult?.source;
            if (typeof fetchedPart !== "string" || typeof source !== "string") {
              continue;
            }

            const partData = partResult?.isBase64
              ? Buffer.from(source, "base64")
              : Buffer.from(source, "utf8");
            newPCache.set(fetchedPart, partData);
          }
          this._cache.partsCache.set(uriStr, newPCache);
          if (newPCache.has(partName)) {
            const data = newPCache.get(partName)!;
            this._cache.contentCache.set(uriStr, data);
            this._shadowService?.syncToDisk(uri, data, partName);
            return data;
          }
        }

        const result = await this.callMcpTool(
          "genexus_read",
          {
            name: target,
            part: partName,
          },
          120000,
        );
        const data =
          result && typeof result.source === "string"
            ? result.isBase64
              ? Buffer.from(result.source, "base64")
              : Buffer.from(result.source, "utf8")
            : Buffer.from(`// Part not available: ${partName}`, "utf8");

        this._cache.readCache.set(cacheKey, { data, time: Date.now() });
        this._cache.contentCache.set(uriStr, data);
        this._shadowService?.syncToDisk(uri, data, partName);
        return data;
      } catch (error) {
        return Buffer.from(`// Error reading part: ${error}`, "utf8");
      } finally {
        this._cache.pendingReadRequests.delete(cacheKey);
      }
    })();

    this._cache.pendingReadRequests.set(cacheKey, request);
    return request;
  }

  writeFile(
    uri: vscode.Uri,
    content: Uint8Array,
    options: { create: boolean; overwrite: boolean },
  ): Promise<void> {
    return this._writeFile(uri, content, options);
  }

  async preWarm(uri: vscode.Uri): Promise<void> {
    if (uri.scheme === "file") {
      return;
    }

    if (
      !this._cache.partsCache.has(uri.toString()) &&
      !this._cache.pendingReadRequests.has(uri.toString())
    ) {
      this.readFile(uri).catch(() => {});
    }
  }

  private async _writeFile(
    uri: vscode.Uri,
    content: Uint8Array,
    options: { create: boolean; overwrite: boolean },
  ): Promise<void> {
    const parsed = GxUriParser.parse(uri);
    const target = parsed
      ? `${parsed.type}:${parsed.name}`
      : GxPartMapper.getObjectTarget(uri.path);
    const partName = parsed?.part || this.getPart(uri);
    this._cache.contentCache.set(uri.toString(), content);
    this._cache.mtimes.set(uri.toString(), Date.now());

    try {
      const result = await this.callMcpTool(
        "genexus_edit",
        {
          name: target,
          part: partName,
          mode: "full",
          content: Buffer.from(content).toString("utf8"),
        },
        120000,
      );
      if (!result || result.error || result.status === "Error") {
        if (result?.issues && this._diagnosticProvider) {
          const editor = vscode.window.visibleTextEditors.find(
            (e: vscode.TextEditor) =>
              e.document.uri.toString() === uri.toString(),
          );
          if (editor)
            this._diagnosticProvider.setDiagnostics(
              editor.document,
              result.issues,
            );
        }
        throw new Error(result?.error || "Save failed");
      }

      this._cache.commitWrite(uri, partName);
      this._shadowService?.syncToDisk(uri, content, partName);
      this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);

      // Force directory refresh for structural changes
      this.clearDirCache();

      vscode.window.setStatusBarMessage(`$(check) Saved ${target}`, DEFAULT_STATUS_BAR_TIMEOUT);
    } catch (err: any) {
      vscode.window.showErrorMessage(formatMcpErrorMessage("Save Error:", err));
      throw err;
    }
  }

  public async triggerSave(
    uri: vscode.Uri,
    content: Uint8Array,
  ): Promise<void> {
    return this._writeFile(uri, content, { create: false, overwrite: true });
  }

  public async listMcpTools(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpTools(customTimeout);
  }

  public async listMcpResources(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpResources(customTimeout);
  }

  public async listMcpResourceTemplates(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpResourceTemplates(customTimeout);
  }

  public async listMcpPrompts(customTimeout?: number): Promise<any[]> {
    return this._gateway.listMcpPrompts(customTimeout);
  }

  public async callMcpTool(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.callMcpTool(name, args, customTimeout);
  }

  public async readMcpResource(
    uri: string,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.readMcpResource(uri, customTimeout);
  }

  public async getMcpPrompt(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.getMcpPrompt(name, args, customTimeout);
  }

  public async callMcpMethod(
    method: string,
    params?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this._gateway.callMcp(method, params, customTimeout);
  }

  public async queryObjects(
    query: string,
    limit?: number,
    customTimeout?: number,
    filters?: { typeFilter?: string; domainFilter?: string },
  ): Promise<any> {
    const requestKey = `${query}::${limit ?? ""}::${customTimeout ?? ""}::${filters?.typeFilter ?? ""}::${filters?.domainFilter ?? ""}`;
    const pending = this._pendingQueryRequests.get(requestKey);
    if (pending) {
      return pending;
    }

    const request = this.callMcpTool(
      "genexus_query",
      {
        query,
        limit,
        ...(filters?.typeFilter ? { typeFilter: filters.typeFilter } : {}),
        ...(filters?.domainFilter ? { domainFilter: filters.domainFilter } : {}),
      },
      customTimeout,
    ).finally(() => {
      this._pendingQueryRequests.delete(requestKey);
    });

    this._pendingQueryRequests.set(requestKey, request);
    return request;
  }

  public async listObjects(
    options?: {
      filter?: string;
      limit?: number;
      offset?: number;
      parent?: string;
      parentPath?: string;
      typeFilter?: string;
    },
    customTimeout?: number,
  ): Promise<any[]> {
    const result = await this.callMcpTool(
      "genexus_list_objects",
      {
        ...(options?.filter ? { filter: options.filter } : {}),
        ...(options?.limit !== undefined ? { limit: options.limit } : {}),
        ...(options?.offset !== undefined ? { offset: options.offset } : {}),
        ...(options?.parent !== undefined ? { parent: options.parent } : {}),
        ...(options?.parentPath !== undefined ? { parentPath: options.parentPath } : {}),
        ...(options?.typeFilter ? { typeFilter: options.typeFilter } : {}),
      },
      customTimeout,
    );

    if (typeof result?.error === "string" && result.error.length > 0) {
      throw new Error(result.error);
    }

    return Array.isArray(result)
      ? result
      : Array.isArray(result?.results)
        ? result.results
        : [];
  }

  public async browseObjects(parentPath: string): Promise<any[]> {
    const parentName = parentPath.length > 0
      ? parentPath.split("/").filter((segment) => segment.length > 0).pop() ?? ROOT_PARENT_NAME
      : ROOT_PARENT_NAME;
    const normalizeQueryResults = (result: any): any[] =>
      Array.isArray(result?.results)
        ? result.results
        : Array.isArray(result)
            ? result
            : [];

    const getEntryKey = (entry: any): string => {
      const type = String(entry?.type ?? "").toLowerCase();
      const path = typeof entry?.path === "string" ? entry.path.trim() : "";
      if (path.length > 0) {
        return `${type}:${path.toLowerCase()}`;
      }
      const parentPath = typeof entry?.parentPath === "string" ? entry.parentPath.trim() : "";
      const name = String(entry?.name ?? "").toLowerCase();
      return parentPath.length > 0
        ? `${type}:${parentPath.toLowerCase()}/${name}`
        : `${type}:${name}`;
    };

    const getTypeSortBucket = (type: unknown): number => {
      const normalized = typeof type === "string" ? type.toLowerCase() : "";
      return normalized === "folder" || normalized === "module" ? 0 : 1;
    };

    const sortEntries = (entries: any[]): any[] =>
      [...entries].sort((left, right) => {
        const bucketDiff =
          getTypeSortBucket(left?.type) - getTypeSortBucket(right?.type);
        if (bucketDiff !== 0) {
          return bucketDiff;
        }

        const nameDiff = String(left?.name ?? "").localeCompare(
          String(right?.name ?? ""),
          undefined,
          { sensitivity: "accent" },
        );
        if (nameDiff !== 0) {
          return nameDiff;
        }

        return String(left?.type ?? "").localeCompare(String(right?.type ?? ""), undefined, {
          sensitivity: "accent",
        });
      });

    const dedupeEntries = (entries: any[]): any[] => {
      const unique = new Map<string, any>();
      for (const entry of entries) {
        if (!entry?.name || !entry?.type) continue;
        unique.set(getEntryKey(entry), entry);
      }
      return sortEntries(Array.from(unique.values()));
    };

    const isTimeoutError = (error: unknown): boolean => {
      const message =
        typeof error === "string"
          ? error
          : error instanceof Error
            ? error.message
            : String(error ?? "");
      return /timeout gateway/i.test(message);
    };

    const loadTypedQueriesSequentially = async (
      query: string,
      typeFilters: string[],
      timeoutMs: number,
      options?: {
        continueOnError?: boolean;
        stopOnFirstMatch?: boolean;
        scopeLabel?: string;
      },
    ): Promise<any[]> => {
      const collected: any[] = [];

      for (const typeFilter of typeFilters) {
        try {
          const typedResult = await this.queryObjects(
            query,
            BROWSE_QUERY_LIMIT,
            timeoutMs,
            { typeFilter },
          );

          if (typeof typedResult?.error === "string" && typedResult.error.length > 0) {
            throw new Error(typedResult.error);
          }

          const entries = normalizeQueryResults(typedResult);
          if (entries.length > 0) {
            collected.push(...entries);
            if (options?.stopOnFirstMatch) {
              break;
            }
          }
        } catch (error) {
          if (!options?.continueOnError) {
            throw error;
          }

          console.warn(
            `[GxFS] ${options?.scopeLabel ?? "typed browse"}: ${typeFilter} failed with '${error instanceof Error ? error.message : String(error)}'. Continuing.`,
          );
        }
      }

      return dedupeEntries(collected);
    };

    const isRootEntry = (entry: any): boolean => {
      if (typeof entry?.parentPath === "string") {
        return entry.parentPath.trim().length === 0;
      }
      const entryParent =
        typeof entry?.parent === "string" ? entry.parent.trim() : "";
      return entryParent.length === 0 || entryParent === ROOT_PARENT_NAME;
    };

    const isDirectChild = (entry: any): boolean => {
      if (!entry?.name || !entry?.type) {
        return false;
      }

      if (typeof entry?.parentPath === "string") {
        return entry.parentPath.trim() === parentPath;
      }

      const entryParent =
        typeof entry?.parent === "string" ? entry.parent.trim() : "";

      if (parentPath.length === 0) {
        return entryParent.length === 0 || entryParent === ROOT_PARENT_NAME;
      }

      // Nested parent: a name-only match is ambiguous across modules
      // (e.g. ModuloA/Procs vs ModuloB/Procs). Require parentPath to disambiguate.
      if (parentPath.includes("/")) {
        return false;
      }

      return entryParent === parentName;
    };

    const loadChildrenFromStructuralList = async (): Promise<any[]> => {
      const collected: any[] = [];
      const seenKeys = new Set<string>();
      let offset = 0;
      let pageNumber = 0;

      while (true) {
        pageNumber++;
        const entries = await this.listObjects(
          {
            parent: parentName,
            parentPath,
            limit: BROWSE_QUERY_LIMIT,
            offset,
          },
          120000,
        );

        if (!Array.isArray(entries) || entries.length === 0) {
          break;
        }

        const normalizedPage = dedupeEntries(entries).filter((entry) =>
          isDirectChild(entry),
        );

        let pageAdded = 0;
        for (const entry of normalizedPage) {
          const key = getEntryKey(entry);
          if (seenKeys.has(key)) {
            continue;
          }

          seenKeys.add(key);
          collected.push(entry);
          pageAdded++;
        }

        if (pageNumber > 1 && pageAdded > 0) {
          console.warn(
            `[GxFS] Structural list page ${pageNumber} added ${pageAdded} child(ren) for ${parentName}.`,
          );
        }

        if (entries.length < BROWSE_QUERY_LIMIT) {
          break;
        }

        if (pageAdded === 0) {
          console.warn(
            `[GxFS] Structural list pagination stalled for ${parentName} at offset ${offset}. Aborting further page fetches to avoid an infinite loop.`,
          );
          break;
        }

        offset += BROWSE_QUERY_LIMIT;
      }

      const normalized = sortEntries(collected);

      if (normalized.length > 0) {
        console.warn(
          `[GxFS] Structural list resolved ${normalized.length} child(ren) for ${parentName}.`,
        );
      }

      return normalized;
    };

    const loadRootFallback = async (): Promise<any[]> => {
      console.warn(
        `[GxFS] Falling back to sequential root enumeration after direct root browse failed or returned no items.`,
      );

      const rootContainerTypes = ["Folder", "Module"];
      const rootLeafTypes = BROWSE_FALLBACK_TYPES.filter(
        (typeFilter) => !rootContainerTypes.includes(typeFilter),
      );

      const containerEntries = (
        await loadTypedQueriesSequentially(
          "@quick",
          rootContainerTypes,
          45000,
          {
            continueOnError: true,
            scopeLabel: "root container fallback",
          },
        )
      ).filter(isRootEntry);

      const leafEntries = (
        await loadTypedQueriesSequentially(
          "@quick",
          rootLeafTypes,
          30000,
          {
            continueOnError: true,
            stopOnFirstMatch: false,
            scopeLabel: "root leaf fallback",
          },
        )
      ).filter(isRootEntry);

      const combinedEntries = dedupeEntries([
        ...containerEntries,
        ...leafEntries,
      ]);

      if (combinedEntries.length > 0) {
        console.warn(
          `[GxFS] Root fallback resolved ${combinedEntries.length} root item(s), including ${containerEntries.length} container(s).`,
        );
      }

      return combinedEntries;
    };

    try {
      const structuralChildren = await loadChildrenFromStructuralList();
      if (structuralChildren.length > 0) {
        return structuralChildren;
      }
    } catch (error) {
      console.warn(
        `[GxFS] Structural list failed for ${parentName}: ${error instanceof Error ? error.message : String(error)}. Falling back to search-based browse.`,
      );
    }

    if (parentName === ROOT_PARENT_NAME) {
      const fallbackObjects = await loadRootFallback();
      if (fallbackObjects.length > 0) {
        console.warn(
          `[GxFS] Root browse resolved immediately via fallback query with ${fallbackObjects.length} container(s).`,
        );
        return fallbackObjects;
      }
    }

    let result: any;
    try {
      const scopedQuery = parentPath.length === 0
        ? "@quick"
        : `parentPath:"${parentPath}" @quick`;
      result = await this.queryObjects(
        scopedQuery,
        BROWSE_QUERY_LIMIT,
        90000,
      );
    } catch (error) {
      if (parentPath.length === 0) {
        const fallbackObjects = await loadRootFallback();
        if (fallbackObjects.length > 0) {
          console.warn(
            `[GxFS] Root browse failed with '${error instanceof Error ? error.message : String(error)}'; recovered ${fallbackObjects.length} root containers via fallback query.`,
          );
          return fallbackObjects;
        }
      }
      throw error;
    }

    if (typeof result?.error === "string" && result.error.length > 0) {
      if (parentPath.length === 0 && /timeout gateway/i.test(result.error)) {
        const fallbackObjects = await loadRootFallback();
        if (fallbackObjects.length > 0) {
          console.warn(
            `[GxFS] Root browse timed out; recovered ${fallbackObjects.length} root containers via fallback query.`,
          );
          return fallbackObjects;
        }
      }
      throw new Error(result.error);
    }

    const objects = parentPath.length === 0
      ? normalizeQueryResults(result).filter((entry) => isRootEntry(entry))
      : normalizeQueryResults(result);

    if (parentPath.length === 0 && objects.length === 0) {
      const fallbackObjects = await loadRootFallback();
      if (fallbackObjects.length > 0) {
        console.warn(
          `[GxFS] Root browse returned 0 objects; recovered ${fallbackObjects.length} root containers via fallback query.`,
        );
        return fallbackObjects;
      }
    }

    if (!(result?.isTruncated === true)) {
      return objects;
    }

      if (parentPath.length === 0) {
        const fallbackObjects = await loadRootFallback();
        if (fallbackObjects.length > 0) {
          console.warn(
            `[GxFS] Root browse used fallback after truncated result and recovered ${fallbackObjects.length} item(s).`,
          );
          return fallbackObjects;
        }
      }

      return parentPath.length === 0
        ? loadRootFallback()
        : loadTypedQueriesSequentially(
            `parentPath:"${parentPath}" @quick`,
            BROWSE_FALLBACK_TYPES,
            90000,
            {
              scopeLabel: `typed child browse for ${parentPath}`,
            },
          );
    }

  public async readObjectVariables(name: string, customTimeout?: number): Promise<any[]> {
    const result = await this.readMcpResource(
      `genexus://objects/${name}/variables`,
      customTimeout,
    );
    return Array.isArray(result) ? result : [];
  }

  public async readAttributeMetadata(name: string, customTimeout?: number): Promise<any> {
    return this.readMcpResource(`genexus://attributes/${name}`, customTimeout);
  }

  public async inspectObject(
    name: string,
    include?: string[],
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool(
      "genexus_inspect",
      include && include.length > 0 ? { name, include } : { name },
      customTimeout,
    );
  }

  public async analyzeObject(
    name: string,
    mode: string,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool("genexus_analyze", { name, mode }, customTimeout);
  }

  public async getStructure(
    name: string,
    action: "get_visual" | "update_visual" | "get_indexes" | "get_logic",
    payload?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool(
      "genexus_structure",
      payload === undefined ? { action, name } : { action, name, payload },
      customTimeout,
    );
  }

  public async formatCode(code: string, customTimeout?: number): Promise<any> {
    return this.callMcpTool("genexus_format", { code }, customTimeout);
  }

  public async refactor(
    args: {
      action: string;
      target?: string;
      newName?: string;
      objectName?: string;
      code?: string;
      procedureName?: string;
    },
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool("genexus_refactor", args, customTimeout);
  }

  public clearDirCache(): void {
    this._cache.clearDirectoryCache();
    this._emitter.fire([
      {
        type: vscode.FileChangeType.Changed,
        uri: vscode.Uri.from({ scheme: GX_SCHEME, path: "/" }),
      },
    ]);
  }

  createDirectory(uri: vscode.Uri): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
  delete(uri: vscode.Uri, options: { recursive: boolean }): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
  rename(
    oldUri: vscode.Uri,
    newUri: vscode.Uri,
    options: { overwrite: boolean },
  ): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
  copy(
    source: vscode.Uri,
    destination: vscode.Uri,
    options: { overwrite: boolean },
  ): void {
    throw vscode.FileSystemError.NoPermissions("Not supported");
  }
}
