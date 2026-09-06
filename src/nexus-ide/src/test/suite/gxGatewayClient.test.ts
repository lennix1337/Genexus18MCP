import * as assert from "assert";
import * as http from "http";
import { GxGatewayClient, GxMcpOutcomeUnknownError } from "../../infra/GxGatewayClient";

/**
 * Characterization tests for GxGatewayClient's pure parsing/unwrap helpers and
 * its session-init/retry logic against a real (local, in-process) HTTP server.
 *
 * These do NOT hit a real gateway: a throwaway http.Server stands in for the
 * transport so the retry/session behavior is exercised end-to-end without
 * network I/O leaving the machine.
 */
suite("GxGatewayClient", () => {
  for (const method of ["tools/call", "tools/list"]) {
    for (const failure of ["dropped response", "expired session"]) {
    test(`${method} uses a safe retry policy after ${failure}`, async () => {
      let calls = 0;
      const server = http.createServer((req, res) => {
        let body = "";
        req.on("data", c => body += c);
        req.on("end", () => {
          const command = JSON.parse(body);
          if (command.method === "initialize") {
            res.setHeader("mcp-session-id", "retry-policy-session");
            res.end(JSON.stringify({ result: {} }));
            return;
          }
          if (command.method === "server/discover") {
            res.end(JSON.stringify({ result: { supportedVersions: ["2025-11-25"] } }));
            return;
          }
          if (command.method === "notifications/initialized") {
            res.writeHead(202).end();
            return;
          }
          calls++;
          if (calls === 1 && failure === "dropped response") req.socket.destroy();
          else if (calls === 1) res.end(JSON.stringify({ result: { error: "unknown or expired mcp session" } }));
          else res.end(JSON.stringify({ result: { ok: true } }));
        });
      });
      await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
      try {
        const address = server.address() as import("net").AddressInfo;
        const client = new GxGatewayClient(`http://127.0.0.1:${address.port}/mcp`);
        if (method === "tools/call") {
          if (failure === "dropped response") {
            await assert.rejects(
              () => client.callMcpTool("genexus_edit", { idempotencyKey: "edit-123" }, 2000),
              (error: unknown) => {
                assert.ok(error instanceof GxMcpOutcomeUnknownError);
                assert.strictEqual(error.code, "outcome_unknown");
                assert.strictEqual(error.toolName, "genexus_edit");
                assert.strictEqual(error.operationKey, "edit-123");
                assert.strictEqual(error.retryAllowed, false);
                return true;
              },
            );
          } else {
            assert.deepStrictEqual(await client.callMcpTool("genexus_edit", {}, 2000),
              { error: "unknown or expired mcp session" });
          }
          assert.strictEqual(calls, 1, "a lost response must not repeat a potentially committed mutation");
        } else {
          assert.deepStrictEqual(await client.callMcp(method, undefined, 2000), { ok: true });
          assert.strictEqual(calls, 2, "safe discovery should retain transport recovery");
        }
      } finally {
        server.closeAllConnections();
        await new Promise<void>(resolve => server.close(() => resolve()));
      }
    });
    }
  }

  test("retries a dropped response for an explicitly safe read tool", async () => {
    let calls = 0;
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (chunk) => body += chunk);
      req.on("end", () => {
        const command = JSON.parse(body);
        if (command.method === "initialize") {
          res.setHeader("mcp-session-id", "read-retry-session");
          res.end(JSON.stringify({ result: {} }));
          return;
        }
        if (command.method === "notifications/initialized") {
          res.writeHead(202).end();
          return;
        }
        calls++;
        if (calls === 1) {
          req.socket.destroy();
          return;
        }
        res.end(JSON.stringify({ result: { ok: true } }));
      });
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address() as import("net").AddressInfo;
      const client = new GxGatewayClient(`http://127.0.0.1:${address.port}/mcp`);
      assert.deepStrictEqual(
        await client.callMcpTool("genexus_read", { name: "ProcedureA" }, 2000),
        { ok: true },
      );
      assert.strictEqual(calls, 2);
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });

  test("retries an expired session for an explicitly safe read tool", async () => {
    let calls = 0;
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (chunk) => body += chunk);
      req.on("end", () => {
        const command = JSON.parse(body);
        if (command.method === "initialize") {
          res.setHeader("mcp-session-id", `read-expired-${calls}`);
          res.end(JSON.stringify({ result: {} }));
          return;
        }
        if (command.method === "notifications/initialized") {
          res.writeHead(202).end();
          return;
        }
        calls++;
        if (calls === 1) {
          res.end(JSON.stringify({ result: { error: "unknown or expired mcp session" } }));
          return;
        }
        res.end(JSON.stringify({ result: { ok: true } }));
      });
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address() as import("net").AddressInfo;
      const client = new GxGatewayClient(`http://127.0.0.1:${address.port}/mcp`);
      assert.deepStrictEqual(
        await client.callMcpTool("genexus_read", { name: "ProcedureA" }, 2000),
        { ok: true },
      );
      assert.strictEqual(calls, 2);
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });

  test("operation recovery helpers send inspect and explicit reconcile actions", async () => {
    const commands: any[] = [];
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (chunk) => body += chunk);
      req.on("end", () => {
        const command = JSON.parse(body);
        commands.push(command);
        if (command.method === "initialize") {
          res.setHeader("mcp-session-id", "recovery-session");
          res.end(JSON.stringify({ result: {} }));
          return;
        }
        if (command.method === "notifications/initialized") {
          res.writeHead(202).end();
          return;
        }
        res.end(JSON.stringify({ result: { status: "ok" } }));
      });
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address() as import("net").AddressInfo;
      const client = new GxGatewayClient(`http://127.0.0.1:${address.port}/mcp`);
      await client.inspectMcpOperation("genexus_edit", "edit-123", "dev", 2000);
      await client.reconcileMcpOperation("genexus_edit", "edit-123", "readback matches", "dev", 2000);
      const toolCalls = commands.filter((command) => command.method === "tools/call");
      assert.deepStrictEqual(toolCalls.map((command) => command.params.arguments), [
        { action: "inspect", operationTool: "genexus_edit", operationKey: "edit-123", kb: "dev" },
        { action: "reconcile", operationTool: "genexus_edit", operationKey: "edit-123", verification: "readback matches", confirmed: true, kb: "dev" },
      ]);
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });

  function makeClient(baseUrl: string): GxGatewayClient {
    return new GxGatewayClient(baseUrl);
  }

  // --- Pure helpers (private methods reached via cast, no I/O) ---

  test("unwrapGatewayResponse parses nested JSON-in-JSON content blocks", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({
      result: { content: [{ type: "text", text: JSON.stringify({ ok: true, value: 42 }) }] },
    });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.deepStrictEqual(unwrapped, { ok: true, value: 42 });
  });

  test("unwrapGatewayResponse falls back to raw text when content is not JSON", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({
      result: { content: [{ type: "text", text: "plain text result" }] },
    });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.strictEqual(unwrapped, "plain text result");
  });

  test("unwrapGatewayResponse returns the result wrapper when there is no content list", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({ result: { tools: [{ name: "foo" }] } });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.deepStrictEqual(unwrapped, { tools: [{ name: "foo" }] });
  });

  test("unwrapGatewayResponse returns the full response when there is no result wrapper", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const body = JSON.stringify({ error: "boom" });
    const unwrapped = client.unwrapGatewayResponse(body);
    assert.deepStrictEqual(unwrapped, { error: "boom" });
  });

  test("unwrapGatewayResponse returns the raw body when it is not valid JSON", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    const unwrapped = client.unwrapGatewayResponse("not json at all");
    assert.strictEqual(unwrapped, "not json at all");
  });

  test("isExpiredSessionResponse detects the expired-session error string", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    assert.strictEqual(
      client.isExpiredSessionResponse({ error: "Unknown or expired MCP session" }),
      true,
    );
    assert.strictEqual(client.isExpiredSessionResponse({ error: "some other error" }), false);
    assert.strictEqual(client.isExpiredSessionResponse(null), false);
    assert.strictEqual(client.isExpiredSessionResponse("string payload"), false);
  });

  test("isRetriableTransportError recognizes known transient transport failures", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    assert.strictEqual(client.isRetriableTransportError(new Error("ECONNRESET")), true);
    assert.strictEqual(client.isRetriableTransportError(new Error("socket hang up")), true);
    assert.strictEqual(
      client.isRetriableTransportError(new Error("connect ECONNREFUSED 127.0.0.1:5000")),
      true,
    );
    assert.strictEqual(
      client.isRetriableTransportError(new Error("Unknown or expired MCP session")),
      true,
    );
    assert.strictEqual(client.isRetriableTransportError(new Error("totally unrelated")), false);
  });

  test("describeCommand labels tool/resource/prompt calls distinctly", () => {
    const client = makeClient("http://127.0.0.1:1") as any;
    assert.strictEqual(
      client.describeCommand({ method: "tools/call", params: { name: "genexus_query" } }),
      "tool:genexus_query",
    );
    assert.strictEqual(
      client.describeCommand({ method: "resources/read", params: { uri: "gx://x" } }),
      "resource:gx://x",
    );
    assert.strictEqual(
      client.describeCommand({ method: "prompts/get", params: { name: "p1" } }),
      "prompt:p1",
    );
    assert.strictEqual(client.describeCommand({ method: "tools/list" }), "tools/list");
  });

  // --- Session-init / retry logic against a real local HTTP double ---

  test("initializeMcpSession stores the mcp-session-id header and reuses it", async () => {
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (c) => (body += c));
        req.on("end", () => {
          const command = body ? JSON.parse(body) : undefined;
          if (command?.method === "notifications/initialized") {
            res.writeHead(202).end();
            return;
          }
          res.setHeader("mcp-session-id", "session-abc");
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ result: { ok: true } }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      const sessionId = await client.initializeMcpSession(2000);
      assert.strictEqual(sessionId, "session-abc");

      // Second call must reuse the cached session id without another init round-trip.
      const sessionIdAgain = await client.initializeMcpSession(2000);
      assert.strictEqual(sessionIdAgain, "session-abc");
    } finally {
      server.close();
    }
  });

  test("negotiates the sessionless 2026 transport and sends per-request metadata", async () => {
    const commands: any[] = [];
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (chunk) => body += chunk);
      req.on("end", () => {
        const command = body ? JSON.parse(body) : undefined;
        commands.push({ command, headers: req.headers });
        res.setHeader("Content-Type", "application/json");
        if (command?.method === "server/discover") {
          res.end(JSON.stringify({
            jsonrpc: "2.0",
            id: command.id,
            result: { supportedVersions: ["2025-11-25", "2026-07-28"] },
          }));
          return;
        }
        if (command?.method === "tools/list") {
          res.end(JSON.stringify({
            jsonrpc: "2.0",
            id: command.id,
            result: { tools: [{ name: "genexus_read" }] },
          }));
          return;
        }
        res.end(JSON.stringify({ jsonrpc: "2.0", id: command?.id ?? null, result: {} }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address() as import("net").AddressInfo;
      const client = new GxGatewayClient(`http://127.0.0.1:${address.port}/mcp`);
      assert.strictEqual(await client.initializeMcpSession(2000), "");
      assert.deepStrictEqual(await client.callMcp("tools/list", undefined, 2000), {
        tools: [{ name: "genexus_read" }],
      });

      assert.deepStrictEqual(commands.map((entry) => entry.command.method), [
        "server/discover",
        "tools/list",
      ]);
      const modernCall = commands[1];
      assert.strictEqual(modernCall.headers["mcp-protocol-version"], "2026-07-28");
      assert.strictEqual(modernCall.headers["mcp-method"], "tools/list");
      assert.match(modernCall.headers["mcp-client-id"], /^nexus-\d+-[a-z0-9]+-client$/);
      assert.strictEqual(
        modernCall.command.params._meta["io.modelcontextprotocol/protocolVersion"],
        "2026-07-28",
      );
      assert.deepStrictEqual(
        modernCall.command.params._meta["io.modelcontextprotocol/clientCapabilities"],
        {},
      );
      assert.notStrictEqual(commands[0].command.id, modernCall.command.id);
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });

  test("initializeMcpSession throws when the gateway never returns a session id", async () => {
    const server = http.createServer((req, res) => {
      req.on("data", () => {});
      req.on("end", () => {
        res.setHeader("Content-Type", "application/json");
        res.end(JSON.stringify({ result: { ok: true } }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      await assert.rejects(
        () => client.initializeMcpSession(2000),
        /MCP session was not established/,
      );
    } finally {
      server.close();
    }
  });

  test("callMcp retries and re-initializes the session on an expired-session error", async () => {
    let callCount = 0;
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        const parsed = JSON.parse(body);
        res.setHeader("Content-Type", "application/json");

        if (parsed.method === "initialize") {
          res.setHeader("mcp-session-id", `session-${callCount}`);
          res.end(JSON.stringify({ result: { ok: true } }));
          return;
        }
        if (parsed.method === "notifications/initialized") {
          res.writeHead(202).end();
          return;
        }

        callCount++;
        if (callCount === 1) {
          // First real call: report the session as expired so the client retries.
          res.end(JSON.stringify({ result: { error: "unknown or expired mcp session" } }));
          return;
        }

        res.end(JSON.stringify({ result: { tools: ["genexus_query"] } }));
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      const result = await client.callMcp("tools/list", undefined, 2000);
      assert.deepStrictEqual(result, { tools: ["genexus_query"] });
      assert.strictEqual(callCount, 2, "expected exactly one retry after the expired session");
    } finally {
      server.close();
    }
  });

  test("callMcp surfaces a timeout error when the gateway never responds", async () => {
    const server = http.createServer(() => {
      // Never respond; let the client's own timeout fire.
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);

      await assert.rejects(() => client.callMcp("tools/list", undefined, 300), /Timeout Gateway/);
    } finally {
      server.close();
    }
  });

  // --- Abort-signal wiring (plan 066) ---

  test("callMcp rejects promptly when the signal aborts, well before the timeout", async () => {
    const server = http.createServer(() => {
      // Never respond; the abort should tear the request down long before
      // the 60s customTimeout would ever fire.
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);
      const controller = new AbortController();
      const baselineActiveRequests = (client as any).constructor.activeRequests;

      const started = Date.now();
      const pending = assert.rejects(() =>
        client.callMcp("tools/list", undefined, 60000, controller.signal),
      );
      controller.abort();
      await pending;
      const elapsed = Date.now() - started;

      assert.ok(elapsed < 5000, `expected prompt rejection on abort, took ${elapsed}ms`);
      assert.strictEqual(
        (client as any).constructor.activeRequests,
        baselineActiveRequests,
        "an aborted request must not leave an extra active request behind",
      );
    } finally {
      server.close();
    }
  });

  test("an aborted call is not retried even when it looks like a retriable transport error", async () => {
    let requestCount = 0;
    const server = http.createServer((req, res) => {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        const parsed = JSON.parse(body);
        res.setHeader("Content-Type", "application/json");
        if (parsed.method === "initialize") {
          res.setHeader("mcp-session-id", "session-abort-test");
          res.end(JSON.stringify({ result: { ok: true } }));
          return;
        }
        if (parsed.method === "notifications/initialized") {
          res.writeHead(202).end();
          return;
        }
        requestCount++;
        // Never respond to the real call; the abort fires before any response.
      });
    });

    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    try {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      const client = new GxGatewayClient(`http://127.0.0.1:${port}`);
      const controller = new AbortController();

      const pending = assert.rejects(() =>
        client.callMcp("tools/list", undefined, 60000, controller.signal),
      );
      // Give the real (non-initialize) request a tick to be issued, then abort it.
      setTimeout(() => controller.abort(), 50);
      await pending;

      assert.strictEqual(requestCount, 1, "expected exactly one attempt; abort must not be retried");
    } finally {
      server.close();
    }
  });
});
