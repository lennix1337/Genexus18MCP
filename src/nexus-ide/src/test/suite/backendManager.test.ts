import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { BackendManager } from "../../managers/BackendManager";
import {
  buildGatewayIdentity,
  readGatewayLease,
  resolveGatewayConfigPath,
} from "../../utils/GatewayConfig";

/**
 * Characterization tests for the path-resolution + lease logic BackendManager
 * relies on before it ever spawns a process. The actual `cp.spawn` / process
 * lifecycle is NOT exercised here (per plan 051 scope: pure-ish parts only).
 */
suite("BackendManager path resolution + lease logic", () => {
  test("workspace trust blocks side effects only when VS Code explicitly reports false", () => {
    assert.strictEqual(BackendManager.canStartWorkspace(false), false);
    assert.strictEqual(BackendManager.canStartWorkspace(true), true);
    assert.strictEqual(BackendManager.canStartWorkspace(undefined), true);
  });

  test("runtime config is persisted only for an explicitly authorized start", () => {
    assert.strictEqual(BackendManager.shouldPersistRuntimeConfig(false, false), false);
    assert.strictEqual(BackendManager.shouldPersistRuntimeConfig(false, undefined), false);
    assert.strictEqual(BackendManager.shouldPersistRuntimeConfig(true, false), true);
    assert.strictEqual(BackendManager.shouldPersistRuntimeConfig(false, true), true);
  });

  function makeContext(
    extensionPath: string,
    extensionMode: vscode.ExtensionMode = vscode.ExtensionMode.Production,
  ): vscode.ExtensionContext {
    return { extensionPath, extensionMode } as unknown as vscode.ExtensionContext;
  }

  test("resolveBackendDirectory prefers a dev gateway build when present in Development mode", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-backend-dev-"));
    try {
      const extensionDir = path.join(tempRoot, "extension");
      fs.mkdirSync(extensionDir, { recursive: true });

      const devGatewayDir = path.join(tempRoot, "GxMcp.Gateway", "bin", "Debug", "net10.0-windows");
      fs.mkdirSync(devGatewayDir, { recursive: true });
      fs.writeFileSync(path.join(devGatewayDir, "GxMcp.Gateway.exe"), "");

      const manager = new BackendManager(
        makeContext(extensionDir, vscode.ExtensionMode.Development),
      ) as any;
      const resolved = manager.resolveBackendDirectory();

      assert.strictEqual(resolved.backendDir, devGatewayDir);
      assert.strictEqual(resolved.gatewayExe, path.join(devGatewayDir, "GxMcp.Gateway.exe"));
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("resolveBackendDirectory falls back to the packaged backend dir when nothing else exists", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-backend-none-"));
    try {
      const extensionDir = path.join(tempRoot, "extension");
      fs.mkdirSync(extensionDir, { recursive: true });

      const manager = new BackendManager(
        makeContext(extensionDir, vscode.ExtensionMode.Development),
      ) as any;
      const resolved = manager.resolveBackendDirectory();

      assert.strictEqual(resolved.backendDir, path.join(extensionDir, "backend"));
      assert.strictEqual(
        resolved.gatewayExe,
        path.join(extensionDir, "backend", "GxMcp.Gateway.exe"),
      );
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("resolveBackendDirectory anchors to the packaged backend dir in Production mode, even when dev paths exist", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-backend-packaged-"));
    try {
      const extensionDir = path.join(tempRoot, "extension");
      fs.mkdirSync(extensionDir, { recursive: true });

      // A dev gateway build AND a publish dir both exist alongside the
      // packaged extension - a packaged (Production-mode) install must
      // never resolve to either of these dev-tree paths.
      const devGatewayDir = path.join(tempRoot, "GxMcp.Gateway", "bin", "Debug", "net10.0-windows");
      fs.mkdirSync(devGatewayDir, { recursive: true });
      fs.writeFileSync(path.join(devGatewayDir, "GxMcp.Gateway.exe"), "");

      const publishDir = path.join(tempRoot, "publish");
      fs.mkdirSync(publishDir, { recursive: true });
      fs.writeFileSync(path.join(publishDir, "GxMcp.Gateway.exe"), "");

      const packagedBackendDir = path.join(extensionDir, "backend");
      fs.mkdirSync(packagedBackendDir, { recursive: true });
      fs.writeFileSync(path.join(packagedBackendDir, "GxMcp.Gateway.exe"), "");

      const manager = new BackendManager(
        makeContext(extensionDir, vscode.ExtensionMode.Production),
      ) as any;
      const resolved = manager.resolveBackendDirectory();

      assert.strictEqual(resolved.backendDir, packagedBackendDir);
      assert.strictEqual(
        resolved.gatewayExe,
        path.join(packagedBackendDir, "GxMcp.Gateway.exe"),
      );
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("resolveBackendDirectory falls back to the publish dir in Development mode when only it exists", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-backend-publish-"));
    try {
      const extensionDir = path.join(tempRoot, "src", "extension");
      fs.mkdirSync(extensionDir, { recursive: true });

      const publishDir = path.join(tempRoot, "publish");
      fs.mkdirSync(publishDir, { recursive: true });
      fs.writeFileSync(path.join(publishDir, "GxMcp.Gateway.exe"), "");

      const manager = new BackendManager(
        makeContext(extensionDir, vscode.ExtensionMode.Development),
      ) as any;
      const resolved = manager.resolveBackendDirectory();

      assert.strictEqual(resolved.backendDir, publishDir);
      assert.strictEqual(resolved.gatewayExe, path.join(publishDir, "GxMcp.Gateway.exe"));
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("resolveLaunchSpec runs the exe directly when present", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-launch-exe-"));
    try {
      fs.writeFileSync(path.join(tempRoot, "GxMcp.Gateway.exe"), "");
      const manager = new BackendManager(makeContext(tempRoot)) as any;
      const spec = manager.resolveLaunchSpec(tempRoot);

      assert.strictEqual(spec.command, path.join(tempRoot, "GxMcp.Gateway.exe"));
      assert.deepStrictEqual(spec.args, []);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("resolveLaunchSpec falls back to 'dotnet <dll>' when only the dll is present", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-launch-dll-"));
    try {
      fs.writeFileSync(path.join(tempRoot, "GxMcp.Gateway.dll"), "");
      const manager = new BackendManager(makeContext(tempRoot)) as any;
      const spec = manager.resolveLaunchSpec(tempRoot);

      assert.strictEqual(spec.command, "dotnet");
      assert.deepStrictEqual(spec.args, [path.join(tempRoot, "GxMcp.Gateway.dll")]);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("isProcessAlive rejects non-positive / non-integer pids without calling process.kill", () => {
    const manager = new BackendManager(makeContext(os.tmpdir())) as any;
    assert.strictEqual(manager.isProcessAlive(0), false);
    assert.strictEqual(manager.isProcessAlive(-5), false);
    assert.strictEqual(manager.isProcessAlive(1.5), false);
    assert.strictEqual(manager.isProcessAlive(Number.NaN), false);
  });

  test("isProcessAlive reports the current process as alive", () => {
    const manager = new BackendManager(makeContext(os.tmpdir())) as any;
    assert.strictEqual(manager.isProcessAlive(process.pid), true);
  });

  test("isProcessAlive reports an implausible pid as not alive", () => {
    const manager = new BackendManager(makeContext(os.tmpdir())) as any;
    // PID space on Windows/most OSes won't have a live process this high.
    assert.strictEqual(manager.isProcessAlive(999_999_999), false);
  });
});

suite("GatewayConfig lease + identity helpers", () => {
  test("resolveGatewayConfigPath prefers the workspace-root config.json over backend/config.json", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-cfgpath-"));
    try {
      const extensionDir = path.join(tempRoot, "src", "nexus-ide");
      fs.mkdirSync(extensionDir, { recursive: true });
      const rootConfig = path.join(tempRoot, "config.json");
      fs.writeFileSync(rootConfig, "{}");

      const resolved = resolveGatewayConfigPath(extensionDir);
      assert.strictEqual(resolved, rootConfig);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("resolveGatewayConfigPath falls back to backend/config.json when the root one is absent", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-cfgpath-fallback-"));
    try {
      const extensionDir = path.join(tempRoot, "extension");
      const backendConfig = path.join(extensionDir, "backend", "config.json");
      fs.mkdirSync(path.dirname(backendConfig), { recursive: true });
      fs.writeFileSync(backendConfig, "{}");

      const resolved = resolveGatewayConfigPath(extensionDir);
      assert.strictEqual(resolved, backendConfig);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("buildGatewayIdentity normalizes paths and derives a stable instance key", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-identity-"));
    try {
      const extensionDir = path.join(tempRoot, "extension");
      fs.mkdirSync(extensionDir, { recursive: true });

      const kbPath = path.join(tempRoot, "MyKB") + path.sep;
      const installPath = path.join(tempRoot, "GeneXus18");

      const identityA = buildGatewayIdentity(extensionDir, undefined, kbPath, installPath);
      const identityB = buildGatewayIdentity(
        extensionDir,
        undefined,
        kbPath.toUpperCase(),
        installPath.toUpperCase(),
      );

      // Trailing separators and casing must not change the derived identity key
      // (this is what lets the manager recognize "the same" running instance).
      assert.strictEqual(identityA.instanceKey, identityB.instanceKey);
      assert.strictEqual(identityA.kbPath, path.resolve(kbPath).replace(/[\\/]+$/, "").toLowerCase());
      assert.ok(identityA.leasePath.endsWith(".json"));
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("buildGatewayIdentity changes the instance key when the KB path differs", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-identity-diff-"));
    try {
      const extensionDir = path.join(tempRoot, "extension");
      fs.mkdirSync(extensionDir, { recursive: true });
      const installPath = path.join(tempRoot, "GeneXus18");

      const identityA = buildGatewayIdentity(
        extensionDir,
        undefined,
        path.join(tempRoot, "KB_A"),
        installPath,
      );
      const identityB = buildGatewayIdentity(
        extensionDir,
        undefined,
        path.join(tempRoot, "KB_B"),
        installPath,
      );

      assert.notStrictEqual(identityA.instanceKey, identityB.instanceKey);
      assert.notStrictEqual(identityA.leasePath, identityB.leasePath);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("readGatewayLease returns undefined for a missing lease file and parses one that exists", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-lease-"));
    try {
      const missingPath = path.join(tempRoot, "missing.json");
      assert.strictEqual(readGatewayLease(missingPath), undefined);

      const leasePath = path.join(tempRoot, "lease.json");
      const record = {
        instanceKey: "key-1",
        processId: process.pid,
        httpPort: 5000,
        kbPath: "c:\\kb",
        programDir: "c:\\gx",
        shadowPath: "c:\\kb\\.gx_mirror",
        updatedUtc: new Date().toISOString(),
      };
      fs.writeFileSync(leasePath, JSON.stringify(record));

      const parsed = readGatewayLease(leasePath);
      assert.deepStrictEqual(parsed, record);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });

  test("readGatewayLease returns undefined for a corrupt lease file instead of throwing", () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-ide-lease-corrupt-"));
    try {
      const leasePath = path.join(tempRoot, "lease.json");
      fs.writeFileSync(leasePath, "{ not valid json");

      assert.strictEqual(readGatewayLease(leasePath), undefined);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  });
});
