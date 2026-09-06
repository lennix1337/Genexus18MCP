import * as http from "http";
import * as vscode from "vscode";
import { GxShadowService } from "../gxShadowService";
import { DEFAULT_MCP_PORT } from "../constants";
import { Logger } from "../utils/Logger";

const LEGACY_MCP_PROTOCOL_VERSION = "2025-11-25";
const MODERN_MCP_PROTOCOL_VERSION = "2026-07-28";
const SLOW_REQUEST_MS = 1200;
const FALLBACK_CLIENT_VERSION = "0.0.0";

/**
 * Raised when a mutating tool may have reached the Gateway but its response
 * was lost. Callers must inspect the operation before offering a reapply; the
 * client deliberately never retries this error with a fresh request id.
 */
export class GxMcpOutcomeUnknownError extends Error {
  public readonly code = "outcome_unknown";
  public readonly toolName: string;
  public readonly operationKey?: string;
  public readonly retryAllowed = false;

  constructor(toolName: string, operationKey: string | undefined, cause: unknown) {
    const keySuffix = operationKey ? ` (operation ${operationKey})` : "";
    super(`MCP mutation outcome is unknown for ${toolName}${keySuffix}; inspect before retrying.`);
    this.name = "GxMcpOutcomeUnknownError";
    this.toolName = toolName;
    this.operationKey = operationKey;
    if (cause instanceof Error && cause.stack) {
      this.stack = `${this.stack}\nCaused by: ${cause.stack}`;
    }
  }
}

// This allowlist is intentionally conservative. A tool not listed here is
// treated as a possible mutation, even when its name sounds read-only.
const SAFE_RETRY_TOOL_NAMES = new Set([
  "genexus_analyze",
  "genexus_list_objects",
  "genexus_query",
  "genexus_read",
  "genexus_whoami",
]);

export class GxGatewayClient {
  private _baseUrl = `http://127.0.0.1:${DEFAULT_MCP_PORT}/mcp`;
  private _mcpSessionId?: string;
  private _mcpProtocolVersion?: string;
  private _modernProtocol = false;
  private _protocolNegotiation?: Promise<void>;
  private _shadowService?: GxShadowService;
  private readonly _requestIdPrefix = `nexus-${process.pid}-${Math.random().toString(36).slice(2, 10)}`;
  private readonly _modernClientId = `${this._requestIdPrefix}-client`;
  private _requestSequence = 0;
  private static readonly statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
  private static activeRequests = 0;

  constructor(baseUrl: string, shadowService?: GxShadowService) {
    this._baseUrl = baseUrl;
    this._shadowService = shadowService;
  }

  public get baseUrl(): string {
    return this._baseUrl;
  }

  set baseUrl(url: string) {
    this._baseUrl = url;
  }

  private get mcpBaseUrl(): string {
    if (this._baseUrl.endsWith("/mcp")) {
      return this._baseUrl;
    }
    return this._baseUrl;
  }

  async initializeMcpSession(customTimeout?: number, signal?: AbortSignal): Promise<string> {
    if (this._modernProtocol) {
      return "";
    }
    if (this._mcpSessionId) {
      return this._mcpSessionId;
    }

    if (this._protocolNegotiation) {
      await this._protocolNegotiation;
      return this._modernProtocol ? "" : this._mcpSessionId ?? "";
    }

    this._protocolNegotiation = this.negotiateProtocol(customTimeout, signal);
    try {
      await this._protocolNegotiation;
    } finally {
      this._protocolNegotiation = undefined;
    }
    return this._modernProtocol ? "" : this._mcpSessionId ?? "";
  }

  private async negotiateProtocol(customTimeout?: number, signal?: AbortSignal): Promise<void> {
    // 2026-07-28 is sessionless. Probe it first so the extension consumes the
    // same transport contract as modern MCP clients; older gateways answer
    // with method-not-found/unsupported-version and fall through to initialize.
    try {
      const modernResponse = await this.postRawJsonRpc(
        this.mcpBaseUrl,
        {
          jsonrpc: "2.0",
          id: this.nextRequestId("server_discover"),
          method: "server/discover",
          params: { _meta: this.modernRequestMeta() },
        },
        customTimeout,
        this.modernHeaders("server/discover"),
        signal,
      );
      const discovered = this.unwrapGatewayResponse(modernResponse.body);
      const supportedVersions = Array.isArray(discovered?.supportedVersions)
        ? discovered.supportedVersions
        : [];
      if (modernResponse.statusCode >= 200 && modernResponse.statusCode < 300 &&
          supportedVersions.includes(MODERN_MCP_PROTOCOL_VERSION)) {
        this._modernProtocol = true;
        this._mcpProtocolVersion = MODERN_MCP_PROTOCOL_VERSION;
        return;
      }
    } catch (error) {
      if (this.isAbortError(error)) {
        throw error;
      }
      Logger.debug(`[GxGateway] Modern MCP discovery unavailable; falling back to legacy session: ${String(error)}`);
    }

    const response = await this.postRawJsonRpc(
      this.mcpBaseUrl,
      {
        jsonrpc: "2.0",
        id: this.nextRequestId("initialize"),
        method: "initialize",
        params: {
          protocolVersion: LEGACY_MCP_PROTOCOL_VERSION,
          capabilities: {},
          clientInfo: {
            name: "nexus-ide",
            version: this.clientVersion(),
          },
        },
      },
      customTimeout,
      {
        "MCP-Protocol-Version": LEGACY_MCP_PROTOCOL_VERSION,
      },
      signal,
    );

    const sessionId = response.headers["mcp-session-id"];
    if (!sessionId) {
      throw new Error("MCP session was not established by the gateway.");
    }

    this._mcpSessionId = Array.isArray(sessionId) ? sessionId[0] : sessionId;
    this._mcpProtocolVersion = response.headers["mcp-protocol-version"]?.toString() ||
      LEGACY_MCP_PROTOCOL_VERSION;

    // The legacy handshake is complete only after the client notification. The
    // gateway acknowledges it with 204/202 and no JSON body; keeping this on the
    // same session prevents the first real call from racing initialization.
    await this.postRawJsonRpc(
      this.mcpBaseUrl,
      {
        jsonrpc: "2.0",
        method: "notifications/initialized",
        params: {},
      },
      customTimeout,
      {
        "MCP-Protocol-Version": this._mcpProtocolVersion,
        "MCP-Session-Id": this._mcpSessionId,
      },
      signal,
    );
    return;
  }

  async callMcp(method: string, params?: any, customTimeout?: number, signal?: AbortSignal): Promise<any> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= 3; attempt++) {
      let requestAttempted = false;
      try {
        const sessionId = await this.initializeMcpSession(customTimeout, signal);
        requestAttempted = true;
        const modern = this._modernProtocol;
        const request = {
          jsonrpc: "2.0",
          id: this.nextRequestId(method),
          method,
          params: modern ? this.withModernRequestMeta(params) : params,
        };
        const response = await this.postRawJsonRpc(
          this.mcpBaseUrl,
          request,
          customTimeout,
          modern
            ? this.modernHeaders(method, request.params)
            : {
              "MCP-Protocol-Version": this._mcpProtocolVersion ?? LEGACY_MCP_PROTOCOL_VERSION,
              "MCP-Session-Id": sessionId,
            },
          signal,
        );

        const unwrapped = this.unwrapGatewayResponse(response.body);
        if (this.isExpiredSessionResponse(unwrapped)) {
          this.resetMcpSession();
          if ((method !== "tools/call" || this.isSafeToolCall(params)) && attempt < 3) continue;
        }

        return unwrapped;
      } catch (error) {
        lastError = error;
        // A lost response cannot tell us whether the SDK committed a tool call.
        // Reconnect on the next explicit call, but never replay this attempt.
        if (method === "tools/call" && requestAttempted) {
          if (!this.isSafeToolCall(params) || !this.isRetriableTransportError(error)) {
            this.resetMcpSession();
            throw this.toOutcomeUnknownError(params, error);
          }
        }
        if (this.isAbortError(error) || !this.isRetriableTransportError(error) || attempt === 3) {
          throw error;
        }

        this.resetMcpSession();
        await this.delay(350 * attempt);
      }
    }

    throw lastError instanceof Error
      ? lastError
      : new Error(String(lastError ?? "Unknown MCP error"));
  }

  async listMcpTools(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("tools/list", undefined, customTimeout);
    return Array.isArray(result?.tools) ? result.tools : [];
  }

  async listMcpResources(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("resources/list", undefined, customTimeout);
    return Array.isArray(result?.resources) ? result.resources : [];
  }

  async listMcpResourceTemplates(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp(
      "resources/templates/list",
      undefined,
      customTimeout,
    );
    return Array.isArray(result?.resourceTemplates)
      ? result.resourceTemplates
      : [];
  }

  async listMcpPrompts(customTimeout?: number): Promise<any[]> {
    const result = await this.callMcp("prompts/list", undefined, customTimeout);
    return Array.isArray(result?.prompts) ? result.prompts : [];
  }

  async callMcpTool(name: string, args?: any, customTimeout?: number, signal?: AbortSignal): Promise<any> {
    return this.callMcp(
      "tools/call",
      {
        name,
        arguments: args ?? {},
      },
      customTimeout,
      signal,
    );
  }

  /**
   * Read the durable Gateway journal for a mutation whose response was lost.
   * This call is intentionally separate from reapply helpers: inspection is
   * safe, while a caller must independently verify the target before closing
   * the fence with reconcileMcpOperation.
   */
  async inspectMcpOperation(
    operationTool: string,
    operationKey: string,
    kb?: string,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool(
      "genexus_lifecycle",
      {
        action: "inspect",
        operationTool,
        operationKey,
        ...(kb ? { kb } : {}),
      },
      customTimeout,
    );
  }

  /**
   * Close an unknown-operation fence after the caller has read the affected
   * object and supplied a concise verification statement. The Gateway stores
   * only a hash and still requires a fresh key for any later write.
   */
  async reconcileMcpOperation(
    operationTool: string,
    operationKey: string,
    verification: string,
    kb?: string,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcpTool(
      "genexus_lifecycle",
      {
        action: "reconcile",
        operationTool,
        operationKey,
        verification,
        confirmed: true,
        ...(kb ? { kb } : {}),
      },
      customTimeout,
    );
  }

  async readMcpResource(uri: string, customTimeout?: number): Promise<any> {
    return this.callMcp(
      "resources/read",
      {
        uri,
      },
      customTimeout,
    );
  }

  async getMcpPrompt(
    name: string,
    args?: any,
    customTimeout?: number,
  ): Promise<any> {
    return this.callMcp(
      "prompts/get",
      {
        name,
        arguments: args ?? {},
      },
      customTimeout,
    );
  }

  private unwrapGatewayResponse(body: string): any {
    try {
      Logger.info(`[GxGateway] Response body received (length: ${body.length})`);
      const fullResponse = JSON.parse(body);

      if (fullResponse && fullResponse.result) {
        const mcpResult = fullResponse.result;
        const blocks = Array.isArray(mcpResult.content)
          ? mcpResult.content
          : Array.isArray(mcpResult.contents)
            ? mcpResult.contents
            : null;
        if (blocks && blocks.length > 0) {
          const text = blocks[0].text;
          try {
            const trimmed = text.trim();
            if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
              try {
                return JSON.parse(trimmed);
              } catch (innerE) {
                Logger.error(`[GxGateway] JSON parse error in content: ${innerE}`);
                return text;
              }
            }
            return text;
          } catch (e) {
            Logger.debug(`[GxGateway] Content text inspection failed: ${e}`);
            return text;
          }
        }

        Logger.info(`[GxGateway] Found result wrapper but no content list.`);
        return fullResponse.result;
      }

      Logger.info(`[GxGateway] No result wrapper found.`);
      return fullResponse;
    } catch (e) {
      Logger.warn(`[GxGateway] Gateway response body was not valid JSON; returning raw body: ${e}`);
      return body;
    }
  }

  private resetMcpSession(): void {
    this._mcpSessionId = undefined;
    if (!this._modernProtocol) {
      this._mcpProtocolVersion = undefined;
    }
  }

  private modernRequestMeta(): Record<string, unknown> {
    return {
      "io.modelcontextprotocol/protocolVersion": MODERN_MCP_PROTOCOL_VERSION,
      "io.modelcontextprotocol/clientCapabilities": {},
    };
  }

  private withModernRequestMeta(params: any): any {
    const source = params && typeof params === "object" && !Array.isArray(params)
      ? params
      : {};
    const existingMeta = source._meta && typeof source._meta === "object" && !Array.isArray(source._meta)
      ? source._meta
      : {};
    return {
      ...source,
      _meta: {
        ...this.modernRequestMeta(),
        ...existingMeta,
      },
    };
  }

  private modernHeaders(method: string, params?: any): Record<string, string> {
    const headers: Record<string, string> = {
      "MCP-Protocol-Version": MODERN_MCP_PROTOCOL_VERSION,
      "Mcp-Method": method,
      "Mcp-Client-Id": this._modernClientId,
    };
    const name = params?.name ?? params?.uri ?? params?.taskId;
    if (typeof name === "string" && name.length > 0) {
      headers["Mcp-Name"] = name;
    }
    return headers;
  }

  private nextRequestId(method: string): string {
    this._requestSequence += 1;
    return `${this._requestIdPrefix}-${method.replace(/[^a-zA-Z0-9_-]/g, "_")}-${this._requestSequence}`;
  }

  private clientVersion(): string {
    try {
      const extension = vscode.extensions.getExtension("lennix1337.nexus-ide");
      const version = extension?.packageJSON?.version;
      return typeof version === "string" && version.trim().length > 0
        ? version
        : FALLBACK_CLIENT_VERSION;
    } catch {
      return FALLBACK_CLIENT_VERSION;
    }
  }

  private isExpiredSessionResponse(payload: unknown): boolean {
    if (!payload || typeof payload !== "object") {
      return false;
    }

    const errorValue = (payload as { error?: unknown }).error;
    return typeof errorValue === "string" &&
      errorValue.toLowerCase().includes("unknown or expired mcp session");
  }

  private isAbortError(error: unknown): boolean {
    return (error instanceof Error && error.name === "AbortError") ||
      (typeof error === "object" && error !== null && (error as { code?: unknown }).code === "ABORT_ERR");
  }

  private isRetriableTransportError(error: unknown): boolean {
    const message = error instanceof Error ? error.message : String(error ?? "");
    const lowered = message.toLowerCase();
    return lowered.includes("econnreset") ||
      lowered.includes("socket hang up") ||
      lowered.includes("unknown or expired mcp session") ||
      lowered.includes("mcp session was not established") ||
      lowered.includes("connect econnrefused");
  }

  private isSafeToolCall(params: any): boolean {
    const toolName = params && typeof params.name === "string" ? params.name : "";
    if (toolName === "genexus_lifecycle") {
      const action = params?.arguments?.action;
      return action === "inspect" || action === "status" || action === "result" || action === "snapshots-list";
    }
    return SAFE_RETRY_TOOL_NAMES.has(toolName);
  }

  private toOutcomeUnknownError(params: any, cause: unknown): Error {
    const toolName = params && typeof params.name === "string" ? params.name : "unknown";
    const args = params?.arguments;
    const operationKey = args && typeof args === "object"
      ? (typeof args.operationKey === "string" ? args.operationKey :
        typeof args.idempotencyKey === "string" ? args.idempotencyKey : undefined)
      : undefined;
    return new GxMcpOutcomeUnknownError(toolName, operationKey, cause);
  }

  private async delay(ms: number): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, ms));
  }

  private async postRawJsonRpc(
    targetUrl: string,
    command: any,
    customTimeout?: number,
    extraHeaders?: Record<string, string>,
    signal?: AbortSignal,
  ): Promise<{ body: string; headers: http.IncomingHttpHeaders; statusCode: number }> {
    return new Promise((resolve, reject) => {
      const requestLabel = this.describeCommand(command);
      const startedAt = Date.now();
      let finished = false;
      let req: http.ClientRequest | undefined;
      let removeAbortListener = (): void => undefined;

      const cleanupAbortListener = (): void => {
        removeAbortListener();
        removeAbortListener = (): void => undefined;
      };

      const failRequest = (error: Error, outcome: string): void => {
        if (finished) return;
        finished = true;
        cleanupAbortListener();
        this.finishTrackedRequest(requestLabel, startedAt, outcome);
        reject(error);
      };

      const onAbort = (): void => {
        const error = new Error("MCP request aborted.");
        error.name = "AbortError";
        (error as { code?: string }).code = "ABORT_ERR";
        failRequest(error, "aborted");
        req?.destroy();
      };

      try {
        GxGatewayClient.activeRequests++;
        this.updateBusyStatus(requestLabel);
        Logger.debug(`[GxGateway] -> ${requestLabel}`);

        if (this._shadowService && command.params) {
          command.params.shadowPath = this._shadowService.shadowRoot;
        }

        const data = JSON.stringify(command);
        const timeout = customTimeout || 120000;

        Logger.info(
          `[GxGateway] Calling: ${targetUrl} with module ${command.module ?? command.method}...`,
        );
        const url = new URL(targetUrl);
        req = http.request(
          url,
          {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              "Accept": "application/json, text/event-stream",
              "Content-Length": Buffer.byteLength(data),
              ...(extraHeaders ?? {}),
            },
            timeout: timeout,
            signal,
          },
          (res) => {
            Logger.info(
              `[GxGateway] Response status: ${res.statusCode} for module: ${command.module ?? command.method}`,
            );
            let body = "";
            res.on("data", (chunk) => (body += chunk));
            res.on("end", () => {
              if (!finished) {
                finished = true;
                cleanupAbortListener();
                this.finishTrackedRequest(requestLabel, startedAt, `HTTP ${res.statusCode}`);
              }
              resolve({ body, headers: res.headers, statusCode: res.statusCode ?? 0 });
            });
            // A peer can close a response before emitting `end` (for example a
            // gateway restart during a notification). Ensure the request
            // accounting and promise settle exactly once in that case.
            res.on("close", () => {
              if (finished) return;
              failRequest(
                new Error("MCP response closed before completion."),
                "response_closed",
              );
            });
          },
        );

        if (signal) {
          signal.addEventListener("abort", onAbort, { once: true });
          removeAbortListener = () => signal.removeEventListener("abort", onAbort);
          if (signal.aborted) {
            onAbort();
            return;
          }
        }

        req.on("timeout", () => {
          const error = new Error(`Timeout Gateway (${timeout / 1000}s)`);
          failRequest(error, "timeout");
          req?.destroy();
        });

        req.on("error", (error) => {
          failRequest(error, `error: ${error.message}`);
        });
        req.write(data);
        req.end();
      } catch (syncError) {
        if (!finished) {
          finished = true;
          cleanupAbortListener();
          // In case activeRequests was already incremented
          if (GxGatewayClient.activeRequests > 0) {
              this.finishTrackedRequest(requestLabel, startedAt, `sync_error: ${syncError}`);
          }
        }
        reject(syncError);
      }
    });
  }

  private describeCommand(command: any): string {
    if (command?.method === "tools/call") {
      return `tool:${command?.params?.name ?? "unknown"}`;
    }

    if (command?.method === "resources/read") {
      return `resource:${command?.params?.uri ?? "unknown"}`;
    }

    if (command?.method === "prompts/get") {
      return `prompt:${command?.params?.name ?? "unknown"}`;
    }

    return command?.method ?? "unknown";
  }

  private updateBusyStatus(requestLabel?: string): void {
    if (GxGatewayClient.activeRequests <= 0) {
      GxGatewayClient.statusBarItem.hide();
      return;
    }

    const suffix = requestLabel ? ` ${requestLabel}` : "";
    GxGatewayClient.statusBarItem.text = `$(sync~spin) GeneXus MCP: ${GxGatewayClient.activeRequests} op${GxGatewayClient.activeRequests === 1 ? "" : "s"}${suffix}`;
    GxGatewayClient.statusBarItem.tooltip = "Operacoes MCP em andamento";
    GxGatewayClient.statusBarItem.show();
  }

  private finishTrackedRequest(requestLabel: string, startedAt: number, outcome: string): void {
    const duration = Date.now() - startedAt;
    const slowMarker = duration >= SLOW_REQUEST_MS ? " SLOW" : "";
    Logger.debug(`[GxGateway] <- ${requestLabel} (${duration}ms) ${outcome}${slowMarker}`);
    GxGatewayClient.activeRequests = Math.max(0, GxGatewayClient.activeRequests - 1);
    this.updateBusyStatus();
  }
}
