const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn, spawnSync } = require('node:child_process');
const { Readable, Writable } = require('node:stream');
const path = require('node:path');
const os = require('node:os');
const fs = require('node:fs');
const { protectedRoots } = require('./test-support/home-write-guard');
const { renderOutput } = require('./lib/output');
const {
    compareSemver,
    parseSemver,
    validateChannel,
    detectInstallMethod,
    upgradePlanFor,
    runCommand
} = require('./lib/update-check');
const {
    detectClientInstalled,
    readJsonFileSafe,
    getLauncher,
    getGeneXusMajor,
    getGeneXusCatalogEntries,
    discoverGeneXusInstallation,
    readGeneXusInstallationIdentity,
    directoryLooksLikeKnowledgeBase,
    readGeneXusKbIdentity,
    compareGeneXusKbAndInstallation,
    readKbCatalog,
    switchActiveKb,
    patchClientConfig
} = require('./lib/config');
const { handleInit, resolveMcpSmokeTarget, buildMcpSmokeCommand } = require('./commands/axi');

const cliPath = path.join(__dirname, 'run.js');
const testGxPath = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-gx-'));
fs.writeFileSync(path.join(testGxPath, 'genexus.exe'), '');
const testGatewayPath = path.join(os.tmpdir(), `genexus-mcp-test-${process.pid}`, 'GxMcp.Gateway.exe');
fs.mkdirSync(path.dirname(testGatewayPath), { recursive: true });
if (process.platform === 'win32') {
    fs.copyFileSync(process.env.ComSpec || 'C:\\Windows\\System32\\cmd.exe', testGatewayPath);
} else {
    fs.writeFileSync(testGatewayPath, '#!/bin/sh\nexit 0\n');
    fs.chmodSync(testGatewayPath, 0o755);
}
const testGatewayEnv = { GENEXUS_MCP_GATEWAY_EXE: testGatewayPath };
const testGatewayDir = path.dirname(testGatewayPath);

const RETRYABLE_REMOVE_CODES = new Set(['EPERM', 'EBUSY', 'EACCES']);
function waitForRemoveRetry(delayMs) {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, delayMs);
}

function removeTempPath(targetPath, options = {}, deps = {}) {
    const fsImpl = deps.fsImpl || fs;
    const platform = deps.platform || process.platform;
    const sleep = deps.sleep || waitForRemoveRetry;
    const maxAttempts = platform === 'win32' ? 8 : 1;

    for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
        try {
            fsImpl.rmSync(targetPath, options);
            return;
        } catch (error) {
            const retryable = RETRYABLE_REMOVE_CODES.has(error?.code);
            if (!retryable || attempt === maxAttempts) throw error;
            sleep(Math.min(10 * (2 ** (attempt - 1)), 1000));
        }
    }
}

// Issue #211/#248 teardown safety net: a probe child that outlives its terminate
// signal keeps the stubbed GxMcp.Gateway.exe image mapped, which turns the
// cleanup rmSync into EPERM. Terminate only processes started from this test's
// own temp dir — never a machine-wide GxMcp.Gateway.exe sweep, which would hit
// other checkouts and the operator's own gateways.
function stopLingeringGatewayStubs(dirPath, deps = {}) {
    const platform = deps.platform || process.platform;
    const run = deps.run || ((command, args) => spawnSync(command, args, {
        encoding: 'utf8',
        windowsHide: true,
        timeout: 20000
    }));
    if (platform !== 'win32') return false;

    let canonicalDir = String(dirPath);
    try { canonicalDir = fs.realpathSync.native(canonicalDir); } catch { }
    const escapedDir = canonicalDir.replace(/'/g, "''");
    const processPipeline = [
        `Get-CimInstance Win32_Process -Filter "Name = 'GxMcp.Gateway.exe'"`,
        'Where-Object { if (-not $targetDirectory) { return $false }; $imagePath = $_.ExecutablePath; if (-not $imagePath) { $imagePath = (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).Path }; if (-not $imagePath) { return $false }; $processDirectory = (Get-Item -LiteralPath ([System.IO.Path]::GetDirectoryName($imagePath)) -ErrorAction SilentlyContinue).FullName; $processDirectory -and [System.StringComparer]::OrdinalIgnoreCase.Equals($processDirectory, $targetDirectory) }',
        'ForEach-Object { $processId = $_.ProcessId; Stop-Process -Id $processId -Force; try { Wait-Process -Id $processId -Timeout 5 -ErrorAction SilentlyContinue } catch { } }'
    ].join(' | ');
    const script = `$ErrorActionPreference = 'SilentlyContinue'; $targetDirectory = (Get-Item -LiteralPath '${escapedDir}' -ErrorAction SilentlyContinue).FullName; ${processPipeline}`;
    try {
        run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script]);
    } catch { }
    return true;
}

test.after(() => {
    stopLingeringGatewayStubs(testGatewayDir);
    let firstError = null;
    for (const directory of [testGxPath, testGatewayDir]) {
        try {
            removeTempPath(directory, { recursive: true, force: true });
        } catch (error) {
            if (!firstError) firstError = error;
        }
    }
    if (firstError) throw firstError;
});

// init/uninstall must never patch the operator's clients or ~/.genexus-mcp.
// Explicit fixture homes override this suite sandbox without changing the parent env.
const cliHome = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-cli-home-'));
test.after(() => removeTempPath(cliHome, { recursive: true, force: true }));

function runCli(args, opts = {}) {
    const spawnOptions = {
        encoding: 'utf8',
        cwd: opts.cwd || process.cwd(),
        env: {
            ...process.env, ...sandboxHomeEnv(fs.mkdtempSync(path.join(cliHome, 'case-'))),
            GX_CONFIG_PATH: '', GENEXUS_MCP_GATEWAY_EXE: '', GENEXUS_HOME: '', NODE_OPTIONS: '',
            GENEXUS_MCP_NO_UPDATE_CHECK: '1', ...(opts.env || {}),
            GXMCP_TEST_PROTECTED_ROOTS: JSON.stringify(protectedRoots)
        }
    };
    return spawnSync(process.execPath, ['--require', path.join(__dirname, 'test-support/home-write-guard.js'), cliPath, ...args], spawnOptions);
}

test('home write guard stops mutation before backup and cannot be swallowed', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-guard-'));
    const operatorConfig = path.join(tempRoot, 'operator', '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(operatorConfig), { recursive: true });
    fs.writeFileSync(operatorConfig, '{"mcpServers":{"keep":{}}}');
    try {
        const guard = path.join(__dirname, 'test-support/home-write-guard.js');
        const launcherDir = path.join(tempRoot, 'operator', '.genexus-mcp');
        const claudeConfig = path.join(tempRoot, 'operator', '.claude.json');
        const env = { ...process.env, ...sandboxHomeEnv(path.join(tempRoot, 'operator')),
            GENEXUS_MCP_GATEWAY_EXE: '', NODE_OPTIONS: '',
            GXMCP_TEST_PROTECTED_ROOTS: JSON.stringify([path.dirname(operatorConfig), launcherDir, claudeConfig]) };
        const script = `try { require(${JSON.stringify(path.join(__dirname, 'lib/config.js'))}).patchClientConfig('fixture.json', { ids: ['cursor'], onlyExisting: false }); } catch {} process.exit(0);`;
        const result = spawnSync(process.execPath, ['--require', guard, '-e', script], { env, encoding: 'utf8' });
        assert.equal(result.status, 97, result.stderr);
        assert.match(result.stderr, /GXMCP_TEST_HOME_WRITE_BLOCKED/);
        assert.equal(fs.readFileSync(operatorConfig, 'utf8'), '{"mcpServers":{"keep":{}}}');
        assert.deepEqual(fs.readdirSync(path.dirname(operatorConfig)), ['mcp.json']);

        // Exercise launcher writes and atomic/backup paths independently of client detection.
        for (const operation of [
            `writeFileSync(${JSON.stringify(operatorConfig + '.tmp-test')}, 'bad')`,
            `copyFileSync(${JSON.stringify(operatorConfig)}, ${JSON.stringify(operatorConfig + '.bak')})`,
            `unlinkSync(${JSON.stringify(operatorConfig)})`,
            `openSync(${JSON.stringify(operatorConfig)}, 'w')`,
            `mkdirSync(${JSON.stringify(launcherDir)}, { recursive: true })`,
            `writeFileSync(${JSON.stringify(claudeConfig + '.20260921.bak')}, 'bad')`
        ]) {
            const denied = spawnSync(process.execPath, ['--require', guard, '-e',
                `try { require('node:fs').${operation}; } catch {} process.exit(0);`], { env, encoding: 'utf8' });
            assert.equal(denied.status, 97, operation + denied.stderr);
        }
        assert.equal(fs.readFileSync(operatorConfig, 'utf8'), '{"mcpServers":{"keep":{}}}');
        assert.deepEqual(fs.readdirSync(path.dirname(operatorConfig)), ['mcp.json']);
        assert.equal(fs.existsSync(launcherDir), false);
        assert.equal(fs.existsSync(claudeConfig + '.20260921.bak'), false);

        const fixture = path.join(tempRoot, 'fixture.json');
        const allowed = spawnSync(process.execPath, ['--require', guard, '-e',
            `require('node:fs').writeFileSync(${JSON.stringify(fixture)}, 'ok')`], { env, encoding: 'utf8' });
        assert.equal(allowed.status, 0, allowed.stderr);
        assert.equal(fs.readFileSync(fixture, 'utf8'), 'ok');
    } finally { removeTempPath(tempRoot, { recursive: true, force: true }); }
});

test('temporary cleanup retries transient Windows removal errors', () => {
    let attempts = 0;
    const sleeps = [];
    const fakeFs = {
        rmSync() {
            attempts += 1;
            if (attempts < 3) {
                const error = new Error('file is still in use');
                error.code = 'EPERM';
                throw error;
            }
        }
    };

    removeTempPath('fixture', { recursive: true, force: true }, {
        fsImpl: fakeFs,
        platform: 'win32',
        sleep: (delayMs) => sleeps.push(delayMs)
    });

    assert.equal(attempts, 3);
    assert.deepEqual(sleeps, [10, 20]);

    const permanentError = Object.assign(new Error('invalid path'), { code: 'EINVAL' });
    assert.throws(() => removeTempPath('fixture', {}, {
        fsImpl: { rmSync: () => { throw permanentError; } },
        platform: 'win32',
        sleep: () => assert.fail('non-retryable cleanup errors must not sleep')
    }), permanentError);
});

test('windows cleanup retries keep growing the backoff up to the attempt budget', () => {
    const sleeps = [];
    let attempts = 0;
    const lockedFs = {
        rmSync() {
            attempts += 1;
            throw Object.assign(new Error('file is still in use'), { code: 'EBUSY' });
        }
    };

    assert.throws(() => removeTempPath('fixture', { force: true }, {
        fsImpl: lockedFs,
        platform: 'win32',
        sleep: (delayMs) => sleeps.push(delayMs)
    }));

    assert.equal(attempts, 8);
    assert.deepEqual(sleeps, [10, 20, 40, 80, 160, 320, 640]);
});

test('lingering gateway stub cleanup stays scoped to the test temp dir', () => {
    const posixCalls = [];
    const ranOnPosix = stopLingeringGatewayStubs('C:\\Temp\\genexus-mcp-test-1', {
        platform: 'linux',
        run: (command, args) => posixCalls.push([command, args])
    });
    assert.equal(ranOnPosix, false);
    assert.deepEqual(posixCalls, [], 'non-Windows teardown must not spawn a shell');

    const windowsCalls = [];
    const ranOnWindows = stopLingeringGatewayStubs('C:\\Temp\\genexus-mcp-test-1', {
        platform: 'win32',
        run: (command, args) => windowsCalls.push([command, args])
    });
    assert.equal(ranOnWindows, true);
    assert.equal(windowsCalls.length, 1);
    assert.equal(windowsCalls[0][0], 'powershell.exe');

    const script = windowsCalls[0][1].join(' ');
    assert.ok(script.includes("'SilentlyContinue'; $targetDirectory") && script.includes('; Get-CimInstance'), 'must set PowerShell error handling before starting the process pipeline');
    assert.ok(script.includes("GxMcp.Gateway.exe"), 'must target the stubbed gateway image');
    assert.ok(script.includes('C:\\Temp\\genexus-mcp-test-1'), 'must be scoped to the test temp dir');
    assert.ok(script.includes('Wait-Process -Id $processId -Timeout 5'), 'must wait for targeted processes to exit before removing the temp directory');
    assert.ok(!script.includes('Stop-Process -Name'), 'must never terminate by process name machine-wide');
});

test('lingering gateway stub cleanup escapes PowerShell path literals', () => {
    const calls = [];
    stopLingeringGatewayStubs("C:\\Temp\\o'brien", {
        platform: 'win32',
        run: (_command, args) => calls.push(args.join(' '))
    });
    assert.equal(calls.length, 1);
    assert.ok(calls[0].includes("C:\\Temp\\o''brien"), 'single quotes must be doubled inside the PowerShell literal');
});

function waitForChildExit(child, timeoutMs = 10000) {
    return new Promise((resolve) => {
        if (!child || child.exitCode !== null || child.signalCode !== null) {
            resolve(true);
            return;
        }
        const timer = setTimeout(() => resolve(false), timeoutMs);
        child.once('exit', () => {
            clearTimeout(timer);
            resolve(true);
        });
    });
}

test('windows cleanup stops a real scoped gateway stub before removal', { skip: process.platform !== 'win32' }, async () => {
    const probeDir = path.join(testGatewayDir, 'wait-probe');
    const probePath = path.join(probeDir, 'GxMcp.Gateway.exe');
    fs.mkdirSync(probeDir, { recursive: true });
    fs.copyFileSync(testGatewayPath, probePath);
    const child = spawn(probePath, ['/c', 'ping 127.0.0.1 -n 5 > nul'], {
        stdio: 'ignore',
        windowsHide: true
    });
    await new Promise((resolve, reject) => {
        child.once('spawn', resolve);
        child.once('error', reject);
    });

    stopLingeringGatewayStubs(probeDir);
    let removeError = null;
    try {
        fs.rmSync(probeDir, { recursive: true, force: true });
    } catch (error) {
        removeError = error;
    }
    const exited = await waitForChildExit(child);
    if (!exited) {
        try { child.kill(); } catch { }
    }
    assert.equal(removeError, null, `the scoped cleanup must release the gateway image before removal: ${removeError?.message || removeError}`);
    assert.equal(exited, true, 'the scoped cleanup must terminate the gateway stub before directory removal');
});

test('a failed stub cleanup does not mask the test result', () => {
    const ran = stopLingeringGatewayStubs('C:\\Temp\\genexus-mcp-test-1', {
        platform: 'win32',
        run: () => { throw new Error('powershell unavailable'); }
    });
    assert.equal(ran, true);
});

test('status returns structured json envelope with schema version', () => {
    const result = runCli(['status', '--format', 'json']);
    assert.equal(result.status, 0);
    assert.equal(result.stderr, '');

    const parsed = JSON.parse(result.stdout);
    assert.ok(parsed.ok);
    assert.equal(typeof parsed.ok.ready, 'boolean');
    assert.equal(parsed.meta.schemaVersion, 'axi-cli/1');
    assert.equal(parsed.meta.command, 'status');
});

test('home command returns compact AXI orientation payload', () => {
    const result = runCli(['home', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'home');
    assert.equal(typeof parsed.ok.bin, 'string');
    assert.equal(typeof parsed.ok.description, 'string');
    assert.equal(typeof parsed.ok.ready, 'boolean');
    assert.ok(Array.isArray(parsed.ok.next));
    assert.ok(parsed.ok.next.length >= 1);
});

test('axi home aliases to home response', () => {
    const result = runCli(['axi', 'home', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'home');
    assert.equal(typeof parsed.ok.ready, 'boolean');
});

test('llm help returns machine-oriented usage guidance', () => {
    const result = runCli(['llm', 'help', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'llm.help');
    assert.equal(typeof parsed.ok.objective, 'string');
    assert.ok(Array.isArray(parsed.ok.resources));
    assert.ok(parsed.ok.resources.includes('genexus://kb/llm-playbook'));
});

test('layout status returns structured payload', () => {
    const result = runCli(['layout', 'status', '--format', 'json']);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'layout.status');
    assert.ok([0, 1].includes(result.status));
    if (result.status === 0) {
        assert.equal(typeof parsed.ok.running, 'boolean');
        assert.equal(typeof parsed.ok.layoutTabDetected, 'boolean');
        return;
    }

    assert.ok(['operation_error', 'operational_error'].includes(parsed.error.code));
    assert.equal(typeof parsed.error.message, 'string');
});

test('layout inspect returns structured controls payload', () => {
    const result = runCli(['layout', 'inspect', '--limit', '10', '--format', 'json']);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'layout.inspect');
    assert.ok([0, 1].includes(result.status));
    if (result.status === 0) {
        assert.equal(typeof parsed.ok.returned, 'number');
        assert.ok(Array.isArray(parsed.ok.controls));
        return;
    }

    assert.ok(['operation_error', 'operational_error'].includes(parsed.error.code));
    assert.equal(typeof parsed.error.message, 'string');
});

test('subcommand help works with status --help', () => {
    const result = runCli(['status', '--help', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.ok.command, 'status');
    assert.equal(typeof parsed.ok.bin, 'string');
    assert.ok(parsed.ok.usage.includes('genexus-mcp status'));
});

test('layout --help returns usage with run action contract', () => {
    const result = runCli(['layout', '--help', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'help');
    assert.equal(parsed.ok.command, 'layout');
    assert.ok(parsed.ok.usage.includes('layout run'));
    assert.ok(parsed.ok.usage.includes('layout inspect'));
});

test('init without required non-interactive flags exits with usage code', () => {
    const result = runCli(['init', '--format', 'json']);
    assert.equal(result.status, 2);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.ok(Array.isArray(parsed.help));
});

test('non-interactive init supports idempotent no-op', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-a');
    fs.mkdirSync(kbDir, { recursive: true });

    const args = [
        'init',
        '--kb',
        kbDir,
        '--gx',
        testGxPath,
        '--no-smoke',
        '--format',
        'json'
    ];

    const first = runCli(args, { env: testGatewayEnv });
    assert.equal(first.status, 0);
    const firstParsed = JSON.parse(first.stdout);
    assert.equal(firstParsed.ok.noOp, false);
    assert.ok(firstParsed.ok.verification, 'init should include verification block');
    assert.ok(firstParsed.ok.verification.summary, 'verification should have summary');
    assert.ok(Array.isArray(firstParsed.ok.verification.checks), 'verification should have checks array');
    assert.equal(firstParsed.meta.smokeSkipped, true, '--no-smoke should be reflected in meta');

    const cfgPath = path.join(kbDir, 'config.json');
    assert.equal(fs.existsSync(cfgPath), true);
    const createdConfig = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
    assert.equal(createdConfig.Server.ToolProfile, 'standard');

    createdConfig.Server.ToolProfile = 'all';
    const existingConfigBytes = JSON.stringify(createdConfig, null, 2);
    fs.writeFileSync(cfgPath, existingConfigBytes);

    const second = runCli(args, { env: testGatewayEnv });
    assert.equal(second.status, 0);
    const secondParsed = JSON.parse(second.stdout);
    assert.equal(secondParsed.ok.noOp, true);
    assert.equal(fs.readFileSync(cfgPath, 'utf8'), existingConfigBytes, 'init must not rewrite an existing profile');

    delete createdConfig.Server.ToolProfile;
    const profilelessConfigBytes = JSON.stringify(createdConfig, null, 2);
    fs.writeFileSync(cfgPath, profilelessConfigBytes);
    const third = runCli(args, { env: testGatewayEnv });
    assert.equal(third.status, 0);
    assert.equal(JSON.parse(third.stdout).ok.noOp, true);
    assert.equal(fs.readFileSync(cfgPath, 'utf8'), profilelessConfigBytes, 'init must not retrofit profile-less configs');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('config create creates explicit neutral runtime without KB or client registration', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-neutral-'));
    const output = path.join(tempRoot, 'nested', 'runtime.json');
    const result = runCli([
        'config', 'create', '--config-scope', 'neutral', '--output', output,
        '--gx', 'C:\\GeneXus18', '--worker', 'C:\\worker.exe',
        '--gateway-mode', 'stdio-isolated', '--resolution-policy', 'strict', '--format', 'json'
    ]);
    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'config.create');
    assert.equal(parsed.ok.configPath, path.resolve(output));
    assert.equal(parsed.ok.clientsPatchedCount, 0);
    assert.equal(parsed.ok.config.Environment.KBPath, undefined);
    assert.equal(parsed.ok.config.Environment.KBs, undefined);
    assert.equal(parsed.meta.clientRegistration, 'not_attempted');
    assert.equal(parsed.meta.kbCatalog, 'not_created');
    assert.deepEqual(JSON.parse(fs.readFileSync(output, 'utf8')), parsed.ok.config);
    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('config create rejects missing new-format flags', () => {
    const result = runCli(['config', 'create', '--config-scope', 'neutral', '--format', 'json']);
    assert.equal(result.status, 2);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.match(parsed.error.message, /--output/);
    assert.match(parsed.error.message, /--worker/);
    assert.match(parsed.error.message, /--gateway-mode/);
    assert.match(parsed.error.message, /--resolution-policy/);
});

test('config create rejects KB in neutral runtime', () => {
    const result = runCli([
        'config', 'create', '--config-scope', 'neutral', '--output', path.join(os.tmpdir(), 'should-not-write.json'),
        '--gx', 'C:\\GeneXus18', '--worker', 'C:\\worker.exe', '--gateway-mode', 'stdio-isolated',
        '--resolution-policy', 'strict', '--kb', 'C:\\KBs\\forbidden', '--format', 'json'
    ]);
    assert.equal(result.status, 2);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.match(parsed.error.message, /--kb is not allowed/);
});

test('config create does not modify client registration', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-neutral-reg-'));
    const output = path.join(tempRoot, 'runtime.json');
    const marker = path.join(tempRoot, 'client-config.json');
    fs.writeFileSync(marker, JSON.stringify({ mcpServers: { existing: { command: 'keep' } } }, null, 2));
    const before = fs.readFileSync(marker, 'utf8');
    const result = runCli([
        'config', 'create', '--config-scope', 'neutral', '--output', output,
        '--gx', 'C:\\GeneXus18', '--worker', 'C:\\worker.exe', '--gateway-mode', 'stdio-isolated',
        '--resolution-policy', 'strict', '--format', 'json'
    ]);
    assert.equal(result.status, 0);
    assert.equal(fs.readFileSync(marker, 'utf8'), before);
    assert.deepEqual(JSON.parse(result.stdout).ok.config, JSON.parse(fs.readFileSync(output, 'utf8')));
    removeTempPath(tempRoot, { recursive: true, force: true });
});
test('config migrate creates a neutral config with an atomic backup and read-back receipt', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-migrate-'));
    try {
        const source = path.join(tempRoot, 'legacy.json');
        const target = path.join(tempRoot, 'neutral.json');
        const legacy = {
            GeneXus: { InstallationPath: 'C:\\GeneXus18', WorkerExecutable: 'C:\\worker.exe' },
            Server: { HttpPort: 5000, McpStdio: true },
            Environment: { KBPath: 'C:\\KBs\\main', DefaultKb: 'main', KBs: [{ Alias: 'main', Path: 'C:\\KBs\\main' }] }
        };
        fs.writeFileSync(source, JSON.stringify(legacy, null, 2));
        const result = runCli(['config', 'migrate', '--from', source, '--output', target, '--format', 'json']);
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        assert.equal(parsed.meta.command, 'config.migrate');
        assert.equal(parsed.ok.readBack, true);
        assert.ok(fs.existsSync(parsed.ok.backupPath));
        assert.deepEqual(JSON.parse(fs.readFileSync(parsed.ok.backupPath, 'utf8')), legacy);
        assert.equal(JSON.parse(fs.readFileSync(target, 'utf8')).ConfigSchemaVersion, 2);
        assert.deepEqual(parsed.ok.notMigrated, ['Environment.KBPath', 'Environment.KBs', 'Environment.DefaultKb']);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('config migrate rolls back an existing destination when read-back fails', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-migrate-rollback-'));
    try {
        const source = path.join(tempRoot, 'legacy.json');
        const target = path.join(tempRoot, 'neutral.json');
        fs.writeFileSync(source, JSON.stringify({ GeneXus: { InstallationPath: 'C:\\GeneXus18' } }));
        const original = JSON.stringify({ keep: 'old' }, null, 2);
        fs.writeFileSync(target, original);
        const result = runCli(['config', 'migrate', '--from', source, '--output', target, '--format', 'json'], {
            env: { GENEXUS_MCP_MIGRATE_FAIL_READBACK: '1' }
        });
        assert.equal(result.status, 1);
        const parsed = JSON.parse(result.stdout);
        assert.equal(parsed.error.code, 'operation_error');
        assert.equal(parsed.ok, undefined);
        assert.equal(fs.readFileSync(target, 'utf8'), original);
        assert.equal(parsed.error.rollback.rolledBack, true);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('config migrate rejects non-migratable fields when explicitly requested', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-migrate-reject-'));
    try {
        const source = path.join(tempRoot, 'legacy.json');
        const target = path.join(tempRoot, 'neutral.json');
        fs.writeFileSync(source, JSON.stringify({ Environment: { KBPath: 'C:\\KBs\\main' } }));
        const result = runCli(['config', 'migrate', '--from', source, '--output', target, '--reject-non-migratable', '--format', 'json']);
        assert.equal(result.status, 2);
        const parsed = JSON.parse(result.stdout);
        assert.match(parsed.error.message, /non-migratable/i);
        assert.equal(fs.existsSync(target), false);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('whoami without config returns disconnected state', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const res = runCli(['whoami', '--format', 'json'], { cwd: tempRoot });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.connected, false);
    assert.ok(parsed.ok.reason, 'should explain why not connected');
    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('whoami with config returns kb and geneXus details', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-w');
    fs.mkdirSync(kbDir, { recursive: true });

    runCli(['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const res = runCli(['whoami', '--format', 'json'], { cwd: kbDir });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.connected, true);
    assert.equal(parsed.ok.kb.path, kbDir);
    assert.equal(parsed.ok.kb.name, path.basename(kbDir));
    assert.equal(parsed.ok.geneXus.installationPath, testGxPath);
    assert.equal(parsed.meta.command, 'whoami');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('uninstall --yes removes local config and reports plan', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-u');
    fs.mkdirSync(kbDir, { recursive: true });

    runCli(['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const cfgPath = path.join(kbDir, 'config.json');
    assert.equal(fs.existsSync(cfgPath), true, 'precondition: config.json exists');

    const res = runCli(['uninstall', '--yes', '--format', 'json'], { cwd: kbDir });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.action, 'uninstall');
    assert.equal(parsed.ok.cancelled, false);
    assert.equal(parsed.ok.configRemoved, true);
    assert.equal(fs.existsSync(cfgPath), false, 'config.json should be deleted');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('uninstall --help returns usage entry', () => {
    const res = runCli(['uninstall', '--help', '--format', 'json']);
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.command, 'uninstall');
    assert.ok(parsed.ok.usage.includes('--yes'), 'usage should mention --yes flag');
});

test('whoami --help returns usage entry', () => {
    const res = runCli(['whoami', '--help', '--format', 'json']);
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.command, 'whoami');
});

test('init auto-discovers KB from cwd when --kb is omitted', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-disco');
    fs.mkdirSync(kbDir, { recursive: true });
    fs.writeFileSync(path.join(kbDir, 'KnowledgeBase.Connection'), '');

    const res = runCli(
        ['init', '--gx', testGxPath, '--no-smoke', '--format', 'json'],
        { cwd: kbDir, env: testGatewayEnv }
    );

    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.resolved.kb.path, kbDir);
    assert.equal(parsed.ok.resolved.kb.source, 'cwd');
    assert.equal(parsed.ok.resolved.gx.source, 'flag');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('GeneXus installation identity falls back to executable metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-version-'));
    try {
        fs.writeFileSync(path.join(tempRoot, 'GeneXus.exe'), 'not-a-real-executable');
        const identity = readGeneXusInstallationIdentity(tempRoot, {
            readExecutableVersion: () => '18.0.10.184260'
        });
        assert.deepEqual(identity, {
            version: '18.0.10.184260',
            major: '18',
            source: 'executable-metadata'
        });
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('classic GeneXus installation identity reads gx.exe metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-classic-version-'));
    try {
        fs.writeFileSync(path.join(tempRoot, 'gx.exe'), 'not-a-real-executable');
        const identity = readGeneXusInstallationIdentity(tempRoot, {
            readExecutableVersion: (exePath) => exePath.endsWith('gx.exe') ? '9.0.123' : null
        });
        assert.deepEqual(identity, {
            version: '9.0.123',
            major: '9',
            source: 'executable-metadata'
        });
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('GeneXus 8 installation identity reads gxw32.exe metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-gx8-version-'));
    try {
        fs.writeFileSync(path.join(tempRoot, 'gxw32.exe'), 'not-a-real-executable');
        const identity = readGeneXusInstallationIdentity(tempRoot, {
            readExecutableVersion: (exePath) => exePath.endsWith('gxw32.exe') ? '8.0.0.632' : null
        });
        assert.deepEqual(identity, {
            version: '8.0.0.632',
            major: '8',
            source: 'executable-metadata'
        });
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('classic GeneXus installation discovery accepts GENEXUS_HOME with gx.exe', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-classic-home-'));
    const previousHome = process.env.GENEXUS_HOME;
    try {
        fs.writeFileSync(path.join(tempRoot, 'gx.exe'), 'not-a-real-executable');
        process.env.GENEXUS_HOME = tempRoot;
        assert.equal(discoverGeneXusInstallation(), tempRoot);
    } finally {
        if (previousHome === undefined) delete process.env.GENEXUS_HOME;
        else process.env.GENEXUS_HOME = previousHome;
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('GeneXus installation identity ignores an invalid version file when executable metadata is valid', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-invalid-version-'));
    try {
        fs.writeFileSync(path.join(tempRoot, 'version.txt'), 'not-a-version');
        fs.writeFileSync(path.join(tempRoot, 'GeneXus.exe'), 'not-a-real-executable');
        const identity = readGeneXusInstallationIdentity(tempRoot, {
            readExecutableVersion: () => '17.0.11.163677'
        });
        assert.deepEqual(identity, {
            version: '17.0.11.163677',
            major: '17',
            source: 'executable-metadata'
        });
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('GeneXus installation identity does not infer a major from a missing folder', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-missing-install-'));
    try {
        const identity = readGeneXusInstallationIdentity(path.join(tempRoot, 'GeneXus17'));
        assert.deepEqual(identity, {
            version: null,
            major: null,
            source: 'unavailable'
        });
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('KB identity reads the GeneXus major from its gxw metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-'));
    try {
        fs.writeFileSync(
            path.join(tempRoot, 'KnowledgeBase.gxw'),
            '<KnowledgeBase><FriendlyVersion>17.0.11 U11</FriendlyVersion><VersionNumber>17.0.11.163677</VersionNumber></KnowledgeBase>'
        );
        const identity = readGeneXusKbIdentity(tempRoot);
        assert.equal(identity.version, '17.0.11.163677');
        assert.equal(identity.major, '17');
        assert.equal(identity.source, 'gxw-version');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('KB identity fails closed for malformed gxw metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-malformed-'));
    try {
        fs.writeFileSync(
            path.join(tempRoot, 'KnowledgeBase.gxw'),
            '<KnowledgeBase><VersionNumber>17.0.11.163677</KnowledgeBase>'
        );
        const identity = readGeneXusKbIdentity(tempRoot);
        assert.equal(identity.major, null);
        assert.equal(identity.source, 'unavailable');
        assert.equal(identity.reason, 'malformed-gxw');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('catalog discovery ordering can prefer the KB major', () => {
    assert.equal(getGeneXusMajor('17.0.11.163677'), '17');
    assert.equal(getGeneXusMajor('10.3.0.86550'), '10.3');
    assert.equal(getGeneXusMajor('10.2.0'), '10.2');
    assert.equal(getGeneXusMajor('10.1.0'), '10.1');
    assert.equal(getGeneXusMajor('9.0.123'), '9');
    assert.equal(getGeneXusMajor('8.0.456'), '8');
    assert.equal(getGeneXusCatalogEntries('17')[0].major, '17');
    assert.equal(getGeneXusCatalogEntries('18')[0].major, '18');
    assert.equal(getGeneXusCatalogEntries('10.3')[0].major, '10.3');
    assert.equal(getGeneXusCatalogEntries('9')[0].major, '9');
    assert.equal(getGeneXusCatalogEntries('8')[0].major, '8');
});

test('KB identity reads Evolution 3 decimal major from gxw metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-ev3-'));
    try {
        fs.writeFileSync(
            path.join(tempRoot, 'KnowledgeBase.gxw'),
            '<KnowledgeBase><FriendlyVersion>GeneXus Evolution 3</FriendlyVersion><VersionNumber>10.3.0.86550</VersionNumber></KnowledgeBase>'
        );
        const identity = readGeneXusKbIdentity(tempRoot);
        assert.equal(identity.version, '10.3.0.86550');
        assert.equal(identity.major, '10.3');
        assert.equal(identity.source, 'gxw-version');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('KB identity reads GeneXus 9.0 classic major from gxi when gxw is absent', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-gx9-'));
    try {
        fs.writeFileSync(path.join(tempRoot, 'MyLegacyKb.gxi'), 'binary-or-header-data');
        const identity = readGeneXusKbIdentity(tempRoot);
        assert.equal(identity.major, '9');
        assert.equal(identity.version, '9.0');
        assert.equal(identity.source, 'gxi-classic');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('classic DAT KB identity recognizes GX8/9 model markers without inventing a major', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-classic-dat-'));
    try {
        for (const marker of ['DATA001', 'GXSPC001', 'ATTRIBUT.DAT', 'ATT.XPW']) {
            fs.writeFileSync(path.join(tempRoot, marker), 'classic');
        }
        const identity = readGeneXusKbIdentity(tempRoot);
        assert.equal(identity.major, null);
        assert.equal(identity.source, 'classic-dat');
        assert.equal(identity.reason, 'classic-generation-requires-provider');
        assert.equal(directoryLooksLikeKnowledgeBase(tempRoot), true);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('KB catalog edits preserve per-KB legacy driver metadata', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-catalog-'));
    const configPath = path.join(tempRoot, 'config.json');
    try {
        fs.writeFileSync(configPath, JSON.stringify({
            Environment: {
                KBs: {
                    sect80: {
                        Path: 'D:\\\\GX80\\\\SECT',
                        Driver: 'com-gxpublic',
                        InstallationPath: 'C:\\\\gxw80',
                        Major: '8'
                    }
                },
                ActiveKb: 'sect80'
            }
        }));

        const catalog = readKbCatalog(configPath);
        assert.equal(catalog.kbs.sect80, 'D:\\\\GX80\\\\SECT');
        assert.equal(Object.keys(catalog.kbs).length, 1);
        const switched = switchActiveKb(configPath, { name: 'sect80' });
        assert.equal(switched.ok, true);
        const persisted = JSON.parse(fs.readFileSync(configPath, 'utf8'));
        assert.equal(persisted.Environment.KBs.sect80.Driver, 'com-gxpublic');
        assert.equal(persisted.Environment.KBs.sect80.InstallationPath, 'C:\\\\gxw80');
        assert.equal(persisted.Environment.KBs.sect80.Major, '8');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('classic gxi identity does not invent a GX8/GX9 mismatch before provider open', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-kb-gxi-compare-'));
    const gx8Root = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-gx8-compare-'));
    try {
        fs.writeFileSync(path.join(tempRoot, 'Legacy.gxi'), 'classic-kb');
        fs.writeFileSync(path.join(gx8Root, 'gxw32.exe'), 'not-a-real-executable');
        const compatibility = compareGeneXusKbAndInstallation(tempRoot, gx8Root, {
            readExecutableVersion: (exePath) => exePath.endsWith('gxw32.exe') ? '8.0.0.632' : null
        });
        assert.equal(compatibility.status, 'unresolved');
        assert.equal(compatibility.kb.source, 'gxi-classic');
        assert.equal(compatibility.gx.major, '8');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
        removeTempPath(gx8Root, { recursive: true, force: true });
    }
});

test('init rejects a known KB and SDK major mismatch before writing config', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-mismatch-'));
    const kbDir = path.join(tempRoot, 'kb17');
    const gxDir = path.join(tempRoot, 'GeneXus18');
    fs.mkdirSync(kbDir, { recursive: true });
    fs.mkdirSync(gxDir, { recursive: true });
    fs.writeFileSync(
        path.join(kbDir, 'KnowledgeBase.gxw'),
        '<KnowledgeBase><VersionNumber>17.0.11.163677</VersionNumber></KnowledgeBase>'
    );
    fs.writeFileSync(path.join(gxDir, 'GeneXus.exe'), 'not-a-real-executable');

    try {
        const result = runCli(
            ['init', '--kb', kbDir, '--gx', gxDir, '--no-smoke', '--no-write-clients', '--format', 'json'],
            { env: testGatewayEnv }
        );
        assert.equal(result.status, 1);
        const parsed = JSON.parse(result.stdout);
        assert.equal(parsed.error.code, 'sdk_kb_mismatch');
        assert.equal(fs.existsSync(path.join(kbDir, 'config.json')), false);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('interactive init does not silently choose the primary SDK when KB metadata is unresolved', async () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-interactive-unresolved-'));
    const kbDir = path.join(tempRoot, 'kb');
    const gxDir = path.join(tempRoot, 'GeneXus18');
    fs.mkdirSync(kbDir, { recursive: true });
    fs.mkdirSync(gxDir, { recursive: true });
    fs.writeFileSync(path.join(kbDir, 'KnowledgeBase.gxw'), '');
    fs.writeFileSync(path.join(kbDir, 'knowledgebase.connection'), 'connection');
    fs.writeFileSync(path.join(gxDir, 'GeneXus.exe'), 'not-a-real-executable');

    const previousGeneXusHome = process.env.GENEXUS_HOME;
    process.env.GENEXUS_HOME = gxDir;
    const input = new Readable({ read() { } });
    setTimeout(() => input.push('\n'), 0);
    setTimeout(() => input.push('\n'), 25);
    setTimeout(() => input.push(null), 100);
    try {
        const result = await handleInit(
            { interactive: true, quiet: true },
            {
                cwd: kbDir,
                input,
                stderr: new Writable({ write(_chunk, _encoding, callback) { callback(); } }),
                EXIT_CODES: { OK: 0, ERROR: 1, USAGE: 2 }
            }
        );
        assert.equal(result.exitCode, 1);
        assert.equal(result.envelope.error.code, 'sdk_selection_required');
        assert.equal(fs.existsSync(path.join(kbDir, 'config.json')), false);
    } finally {
        if (previousGeneXusHome === undefined) delete process.env.GENEXUS_HOME;
        else process.env.GENEXUS_HOME = previousGeneXusHome;
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('init fails clearly when paths cannot be auto-discovered', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));

    const res = runCli(
        ['init', '--gx', testGxPath, '--no-smoke', '--format', 'json'],
        { cwd: tempRoot }
    );

    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.ok(parsed.error.message.includes('--kb'), 'error should mention --kb');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb list shows the KB auto-registered by init', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-list');
    fs.mkdirSync(kbDir, { recursive: true });

    runCli(['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const res = runCli(['kb', 'list', '--format', 'json'], { cwd: kbDir });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.meta.command, 'kb.list');
    assert.equal(parsed.ok.activeKb, path.basename(kbDir));
    assert.equal(parsed.ok.kbs.length, 1);
    assert.equal(parsed.ok.kbs[0].active, true);
    assert.equal(parsed.ok.kbs[0].path, kbDir);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb list reads gateway-style KB arrays and DefaultKb', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbA = path.join(tempRoot, 'kb-array-a');
    const kbB = path.join(tempRoot, 'kb-array-b');
    fs.mkdirSync(kbA, { recursive: true });
    fs.mkdirSync(kbB, { recursive: true });
    fs.writeFileSync(path.join(tempRoot, 'config.json'), JSON.stringify({
        Environment: {
            DefaultKb: 'legacy',
            KBs: [
                { Alias: 'main', Path: kbA },
                { alias: 'legacy', path: kbB }
            ]
        }
    }));

    const res = runCli(['kb', 'list', '--format', 'json'], { cwd: tempRoot });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.activeKb, 'legacy');
    assert.deepEqual(parsed.ok.kbs.map((entry) => entry.name), ['main', 'legacy']);
    assert.equal(parsed.ok.kbs[1].active, true);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb switch preserves gateway-style array entries', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbA = path.join(tempRoot, 'kb-switch-a');
    const kbB = path.join(tempRoot, 'kb-switch-b');
    fs.mkdirSync(kbA, { recursive: true });
    fs.mkdirSync(kbB, { recursive: true });
    const configPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(configPath, JSON.stringify({
        Environment: {
            DefaultKb: 'main',
            KBs: [
                { Alias: 'main', Path: kbA },
                { Alias: 'legacy', Path: kbB }
            ]
        }
    }));

    const res = runCli(['kb', 'switch', '--name', 'legacy', '--format', 'json'], { cwd: tempRoot });
    assert.equal(res.status, 0);
    const cfg = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    assert.deepEqual(Object.keys(cfg.Environment.KBs).sort(), ['legacy', 'main']);
    assert.equal(cfg.Environment.KBs.main, kbA);
    assert.equal(cfg.Environment.KBs.legacy, kbB);
    assert.equal(cfg.Environment.ActiveKb, 'legacy');
    assert.equal(cfg.Environment.DefaultKb, 'legacy');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb add and switch update active KB', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbA = path.join(tempRoot, 'kb-a');
    const kbB = path.join(tempRoot, 'kb-b');
    fs.mkdirSync(kbA, { recursive: true });
    fs.mkdirSync(kbB, { recursive: true });

    runCli(['init', '--kb', kbA, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const addRes = runCli(['kb', 'add', '--name', 'bravo', '--kb', kbB, '--format', 'json'], { cwd: kbA });
    assert.equal(addRes.status, 0);
    const addParsed = JSON.parse(addRes.stdout);
    assert.equal(addParsed.ok.registeredCount, 2);
    assert.equal(addParsed.ok.activeKb, path.basename(kbA), 'active KB should remain the first one');

    const switchRes = runCli(['kb', 'switch', '--name', 'bravo', '--format', 'json'], { cwd: kbA });
    assert.equal(switchRes.status, 0);
    const switchParsed = JSON.parse(switchRes.stdout);
    assert.equal(switchParsed.ok.activeKb, 'bravo');
    assert.equal(switchParsed.ok.kbPath, kbB);

    const cfg = JSON.parse(fs.readFileSync(path.join(kbA, 'config.json'), 'utf8'));
    assert.equal(cfg.Environment.KBPath, kbB, 'legacy KBPath should be updated');
    assert.equal(cfg.Environment.ActiveKb, 'bravo');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb switch rejects unknown name', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-x');
    fs.mkdirSync(kbDir, { recursive: true });

    runCli(['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const res = runCli(['kb', 'switch', '--name', 'nonexistent', '--format', 'json'], { cwd: kbDir });
    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.ok(parsed.error.message.includes('nonexistent'));

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb remove deletes entry and reassigns active when applicable', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbA = path.join(tempRoot, 'kb-r-a');
    const kbB = path.join(tempRoot, 'kb-r-b');
    fs.mkdirSync(kbA, { recursive: true });
    fs.mkdirSync(kbB, { recursive: true });

    runCli(['init', '--kb', kbA, '--gx', testGxPath, '--no-smoke', '--format', 'json']);
    runCli(['kb', 'add', '--name', 'second', '--kb', kbB, '--format', 'json'], { cwd: kbA });

    const removeRes = runCli(['kb', 'remove', '--name', path.basename(kbA), '--format', 'json'], { cwd: kbA });
    assert.equal(removeRes.status, 0);
    const parsed = JSON.parse(removeRes.stdout);
    assert.equal(parsed.ok.removed, true);
    assert.equal(parsed.ok.activeKb, 'second', 'active should fall back to remaining KB');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb switch --kb refuses to overwrite existing entry with different path', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbA = path.join(tempRoot, 'a', 'Sales');
    const kbB = path.join(tempRoot, 'b', 'Sales');
    fs.mkdirSync(kbA, { recursive: true });
    fs.mkdirSync(kbB, { recursive: true });

    runCli(['init', '--kb', kbA, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const res = runCli(['kb', 'switch', '--kb', kbB, '--format', 'json'], { cwd: kbA });
    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.ok(/already registered/i.test(parsed.error.message), 'should warn about basename collision');

    const cfg = JSON.parse(fs.readFileSync(path.join(kbA, 'config.json'), 'utf8'));
    assert.equal(cfg.Environment.KBs.Sales, kbA, 'original entry must be preserved');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb remove of last KB clears legacy KBPath', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-last');
    fs.mkdirSync(kbDir, { recursive: true });

    runCli(['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    runCli(['kb', 'remove', '--name', path.basename(kbDir), '--format', 'json'], { cwd: kbDir });

    const cfg = JSON.parse(fs.readFileSync(path.join(kbDir, 'config.json'), 'utf8'));
    assert.equal(cfg.Environment.KBPath, undefined, 'KBPath should be cleared after removing last KB');
    assert.equal(cfg.Environment.ActiveKb, undefined, 'ActiveKb should be cleared');
    assert.equal(cfg.Environment.DefaultKb, undefined, 'DefaultKb should be cleared');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('kb subcommand validation: missing subcommand returns usage error', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const kbDir = path.join(tempRoot, 'kb-v');
    fs.mkdirSync(kbDir, { recursive: true });

    runCli(['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json']);

    const res = runCli(['kb', '--format', 'json'], { cwd: kbDir });
    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.error.code, 'usage_error');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('tool_definitions.json is valid and disambiguation tools have use-when guidance', () => {
    const defsPath = path.join(__dirname, '..', 'src', 'GxMcp.Gateway', 'tool_definitions.json');
    const defs = JSON.parse(fs.readFileSync(defsPath, 'utf8'));
    assert.ok(Array.isArray(defs) && defs.length > 0, 'tool defs should be a non-empty array');

    const byName = Object.fromEntries(defs.map((t) => [t.name, t]));
    const disambiguationTools = ['genexus_inspect', 'genexus_analyze', 'genexus_doc'];
    for (const name of disambiguationTools) {
        assert.ok(byName[name], `${name} should exist`);
        const desc = byName[name].description || '';
        assert.ok(
            /use when|don't use|use this|use to/i.test(desc),
            `${name} description should include use-when/don't-use guidance`
        );
    }
});

test('tools list supports query and category aggregate', () => {
    const result = runCli([
        'tools',
        'list',
        '--query',
        'read',
        '--limit',
        '5',
        '--fields',
        'name,category',
        '--format',
        'json'
    ]);

    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);

    assert.ok(Array.isArray(parsed.ok.tools));
    assert.ok(parsed.ok.returned <= 5);
    assert.ok(parsed.meta.totalByCategory);
    assert.equal(parsed.meta.query, 'read');
});

test('tools list returns definitive empty state for no matches', () => {
    const result = runCli([
        'tools',
        'list',
        '--query',
        '__definitely_no_tool_name__',
        '--format',
        'json'
    ]);

    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.ok.returned, 0);
    assert.equal(parsed.ok.total, 0);
    assert.equal(parsed.ok.empty, true);
    assert.ok(parsed.help.some((h) => h.toLowerCase().includes('no tools matched')));
});

test('tools list does not suggest --full when description is not requested', () => {
    const result = runCli(['tools', 'list', '--limit', '3', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.ok(Array.isArray(parsed.help));
    assert.equal(parsed.help.some((h) => h.includes('--full')), false);
    assert.equal(parsed.meta.truncated, false);
});

test('config show truncates large raw content and suggests --full', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const configPath = path.join(tempRoot, 'config.json');
    const largeComment = 'x'.repeat(1400);

    const config = {
        GeneXus: { InstallationPath: 'C:\\GX' },
        Server: { HttpPort: 5000, McpStdio: true },
        Environment: { KBPath: 'C:\\KB' },
        Extra: largeComment
    };

    fs.writeFileSync(configPath, JSON.stringify(config, null, 2));

    const result = runCli(['config', 'show', '--format', 'json'], {
        env: { GX_CONFIG_PATH: configPath }
    });

    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);

    assert.equal(parsed.meta.truncated, true);
    assert.ok(parsed.help.some((h) => h.includes('--full')));

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('config show suppresses truncation hint when raw field is not requested', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const configPath = path.join(tempRoot, 'config.json');
    const largeComment = 'x'.repeat(1400);

    const config = {
        GeneXus: { InstallationPath: 'C:\\GX' },
        Server: { HttpPort: 5000, McpStdio: true },
        Environment: { KBPath: 'C:\\KB' },
        Extra: largeComment
    };

    fs.writeFileSync(configPath, JSON.stringify(config, null, 2));

    const result = runCli(['config', 'show', '--fields', 'path,kbPath', '--format', 'json'], {
        env: { GX_CONFIG_PATH: configPath }
    });

    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.truncated, false);
    assert.equal(parsed.help.length, 0);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('--fields validation returns usage error for invalid doctor field', () => {
    const result = runCli(['doctor', '--fields', 'id,unknown', '--format', 'json']);
    assert.equal(result.status, 2);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.error.code, 'usage_error');
});

test('doctor client_config_sync describes the effective gateway and its source', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-gateway-source-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const configuredGateway = path.join(tempRoot, 'checkout', 'publish', 'GxMcp.Gateway.exe');
        const mismatchedGateway = path.join(tempRoot, 'other', 'GxMcp.Gateway.exe');
        const clientConfig = path.join(tempRoot, '.gemini', 'settings.json');
        fs.mkdirSync(path.dirname(configuredGateway), { recursive: true });
        fs.mkdirSync(path.dirname(clientConfig), { recursive: true });
        fs.writeFileSync(configuredGateway, '');
        fs.writeFileSync(clientConfig, JSON.stringify({
            mcpServers: {
                genexus18mcp: { command: configuredGateway, args: [] }
            }
        }));

        const overrideEnv = { ...env, GENEXUS_MCP_GATEWAY_EXE: configuredGateway };
        const matching = runCli(['doctor', '--format', 'json'], { env: overrideEnv });
        assert.equal(matching.status, 0);
        const matchingCheck = JSON.parse(matching.stdout).ok.checks.find((c) => c.id === 'client_config_sync');
        assert.ok(matchingCheck);
        assert.equal(matchingCheck.status, 'pass');
        assert.ok(matchingCheck.detail.includes(`Configured Gateway: ${configuredGateway} (source: GENEXUS_MCP_GATEWAY_EXE)`));
        assert.doesNotMatch(matchingCheck.detail, /npm-package gateway exe/);

        fs.writeFileSync(clientConfig, JSON.stringify({
            mcpServers: {
                genexus18mcp: { command: mismatchedGateway, args: [] }
            }
        }));
        const mismatching = runCli(['doctor', '--format', 'json'], { env: overrideEnv });
        assert.equal(mismatching.status, 0);
        const mismatchingCheck = JSON.parse(mismatching.stdout).ok.checks.find((c) => c.id === 'client_config_sync');
        assert.ok(mismatchingCheck);
        assert.equal(mismatchingCheck.status, 'warn');
        assert.ok(mismatchingCheck.detail.includes(`Configured Gateway: ${configuredGateway} (source: GENEXUS_MCP_GATEWAY_EXE)`));
        assert.doesNotMatch(mismatchingCheck.detail, /this npm package's bundled exe/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor fails closed when GX_CONFIG_PATH points at a missing file', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-config-selection-'));
    try {
        const cwdConfig = path.join(tempRoot, 'config.json');
        const missingConfig = path.join(tempRoot, 'missing.json');
        fs.writeFileSync(cwdConfig, JSON.stringify({ Environment: { KBPath: 'C:\\wrong-context' } }));

        const result = runCli(['doctor', '--mcp-smoke', '--format', 'json'], {
            cwd: tempRoot,
            env: {
                ...sandboxHomeEnv(tempRoot),
                GX_CONFIG_PATH: missingConfig,
                GENEXUS_MCP_GATEWAY_EXE: process.execPath
            }
        });
        assert.equal(result.status, 0);
        const checks = JSON.parse(result.stdout).ok.checks;
        const configCheck = checks.find((c) => c.id === 'config_file');
        const probeCheck = checks.find((c) => c.id === 'gateway_spawn_probe');
        const smokeCheck = checks.find((c) => c.id === 'mcp_smoke');
        assert.ok(configCheck);
        assert.equal(configCheck.status, 'fail');
        assert.ok(configCheck.detail.includes(missingConfig));
        assert.ok(probeCheck);
        assert.equal(probeCheck.status, 'not_applicable');
        assert.ok(smokeCheck);
        assert.equal(smokeCheck.status, 'not_applicable');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor rejects malformed tool definitions instead of reporting only file presence', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-tool-defs-'));
    try {
        const configPath = path.join(tempRoot, 'config.json');
        const toolDefinitionsPath = path.join(tempRoot, 'tool_definitions.json');
        fs.writeFileSync(configPath, JSON.stringify({}));
        fs.writeFileSync(toolDefinitionsPath, '{ invalid json');

        const result = runCli(['doctor', '--format', 'json'], {
            cwd: tempRoot,
            env: {
                ...sandboxHomeEnv(tempRoot),
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: path.join(tempRoot, 'missing', 'GxMcp.Gateway.exe'),
                GENEXUS_MCP_TOOL_DEFINITIONS: toolDefinitionsPath
            }
        });
        assert.equal(result.status, 0);
        const check = JSON.parse(result.stdout).ok.checks.find((c) => c.id === 'tool_definitions');
        assert.ok(check);
        assert.equal(check.status, 'fail');
        assert.ok(check.detail.includes(toolDefinitionsPath));
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor reports strict KB catalog ambiguity instead of ignoring Environment.KBs', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-kb-catalog-'));
    try {
        const kbOne = path.join(tempRoot, 'kb-one');
        const kbTwo = path.join(tempRoot, 'kb-two');
        const configPath = path.join(tempRoot, 'config.json');
        fs.mkdirSync(kbOne, { recursive: true });
        fs.mkdirSync(kbTwo, { recursive: true });
        fs.writeFileSync(path.join(kbOne, 'model.gxw'), '');
        fs.writeFileSync(path.join(kbTwo, 'model.gxw'), '');
        fs.writeFileSync(configPath, JSON.stringify({
            GatewayMode: 'stdio-isolated',
            Environment: {
                ResolutionPolicy: 'strict',
                KBs: {
                    one: { Path: kbOne },
                    two: { Path: kbTwo }
                }
            }
        }));

        const result = runCli(['doctor', '--format', 'json'], {
            cwd: tempRoot,
            env: {
                ...sandboxHomeEnv(tempRoot),
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: path.join(tempRoot, 'missing', 'GxMcp.Gateway.exe')
            }
        });
        assert.equal(result.status, 0);
        const check = JSON.parse(result.stdout).ok.checks.find((c) => c.id === 'kb_catalog');
        assert.ok(check);
        assert.equal(check.status, 'warn');
        assert.match(check.detail, /neither ActiveKb nor DefaultKb/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor finds tool_definitions.json next to the gateway exe (not just dev-tree)', () => {
    // Regression for v2.6.6 bug: getToolDefinitionsPath() hard-coded the dev-tree
    // location, so every installed copy reported "tool_definitions.json is missing"
    // even though the file was published alongside GxMcp.Gateway.exe.
    const result = runCli(['doctor', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    const check = parsed.ok.checks.find((c) => c.id === 'tool_definitions');
    assert.ok(check, 'tool_definitions check must be present');
    assert.equal(check.status, 'pass', `expected pass, got '${check.status}': ${check.detail}`);
    assert.match(check.detail, /Tool definition file found \(\d+ tools\) at .+/);
});

test('doctor honours GENEXUS_MCP_TOOL_DEFINITIONS override and reports it on miss', () => {
    const bogusPath = path.join(os.tmpdir(), 'nonexistent-tool-defs-' + Date.now() + '.json');
    const result = runCli(['doctor', '--format', 'json'], { env: { GENEXUS_MCP_TOOL_DEFINITIONS: bogusPath } });
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    const check = parsed.ok.checks.find((c) => c.id === 'tool_definitions');
    assert.ok(check);
    assert.equal(check.status, 'warn');
    assert.match(check.detail, /GENEXUS_MCP_TOOL_DEFINITIONS=/);
    assert.ok(check.detail.includes(bogusPath) || check.detail.includes(bogusPath.replace(/\\/g, '/')),
        `detail should mention the bogus override path; got: ${check.detail}`);
});

test('doctor --mcp-smoke adds explicit mcp_smoke check', () => {
    const result = runCli(['doctor', '--mcp-smoke', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    const smoke = parsed.ok.checks.find((c) => c.id === 'mcp_smoke');
    assert.ok(smoke);
    assert.ok(['pass', 'warn', 'fail', 'not_applicable'].includes(smoke.status));
});

test('doctor marks HTTP smoke not applicable for stdio-isolated runtime', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-stdio-'));
    const configPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(configPath, JSON.stringify({
        GatewayMode: 'stdio-isolated',
        Server: { HttpPort: 0, McpStdio: true }
    }));

    try {
        const target = resolveMcpSmokeTarget(tempRoot);
        assert.equal(target.applicable, false);
        assert.equal(target.status, 'not_applicable');
        assert.equal(target.baseUrl, null);
        assert.match(target.detail, /stdio-isolated|HTTP listener is disabled/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor MCP smoke uses the PowerShell 7 host', () => {
    const command = buildMcpSmokeCommand(path.join('repo', 'scripts', 'mcp_smoke.ps1'), 'http://127.0.0.1:5000/mcp');
    assert.equal(command.shell, 'pwsh');
    assert.deepEqual(command.args.slice(0, 3), ['-NoProfile', '-ExecutionPolicy', 'Bypass']);
    assert.ok(command.args.some((arg) => arg.endsWith('mcp_smoke.ps1')));
});

test('doctor reports a KB and SDK major mismatch', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-major-'));
    const kbDir = path.join(tempRoot, 'kb17');
    const gxDir = path.join(tempRoot, 'GeneXus18');
    fs.mkdirSync(kbDir, { recursive: true });
    fs.mkdirSync(gxDir, { recursive: true });
    fs.writeFileSync(path.join(kbDir, 'KnowledgeBase.gxw'), '<KnowledgeBase><VersionNumber>17.0.11.163677</VersionNumber></KnowledgeBase>');
    fs.writeFileSync(path.join(gxDir, 'GeneXus.exe'), 'not-a-real-executable');
    const configPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(configPath, JSON.stringify({
        GeneXus: { InstallationPath: gxDir },
        Environment: { KBPath: kbDir }
    }));

    try {
        const compatibility = compareGeneXusKbAndInstallation(kbDir, gxDir);
        assert.equal(compatibility.status, 'mismatch');
        const result = runCli(['doctor', '--format', 'json'], {
            env: {
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: process.execPath,
                LOCALAPPDATA: tempRoot
            }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const check = parsed.ok.checks.find((row) => row.id === 'kb_sdk_compatibility');
        assert.ok(check);
        assert.equal(check.status, 'fail');
        assert.match(check.detail, /KB major 17.*SDK major 18/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor rejects a KB and SDK major that is outside the compatibility catalog', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-unsupported-major-'));
    const kbDir = path.join(tempRoot, 'kb19');
    const gxDir = path.join(tempRoot, 'GeneXus19');
    fs.mkdirSync(kbDir, { recursive: true });
    fs.mkdirSync(gxDir, { recursive: true });
    fs.writeFileSync(path.join(kbDir, 'KnowledgeBase.gxw'), '<KnowledgeBase><VersionNumber>19.0.0.0</VersionNumber></KnowledgeBase>');
    fs.writeFileSync(path.join(gxDir, 'GeneXus.exe'), 'not-a-real-executable');
    const configPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(configPath, JSON.stringify({
        GeneXus: { InstallationPath: gxDir },
        Environment: { KBPath: kbDir }
    }));

    try {
        const result = runCli(['doctor', '--format', 'json'], {
            env: {
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: process.execPath,
                LOCALAPPDATA: tempRoot
            }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const check = parsed.ok.checks.find((row) => row.id === 'kb_sdk_compatibility');
        assert.ok(check);
        assert.equal(check.status, 'fail');
        assert.match(check.detail, /KB major 19 is not supported/);
        assert.match(check.detail, /Supported majors: 16, 17, 18/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor evaluates gxpublic_com_registration and legacy_ide_lock checks for classic GeneXus', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-classic-'));
    const kbDir = path.join(tempRoot, 'kb9');
    const gxDir = path.join(tempRoot, 'GeneXus9');
    fs.mkdirSync(kbDir, { recursive: true });
    fs.mkdirSync(gxDir, { recursive: true });
    fs.writeFileSync(path.join(kbDir, 'test.gxi'), 'legacy classic kb index');
    fs.writeFileSync(path.join(gxDir, 'gx.exe'), 'classic-genexus-exe');
    const configPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(configPath, JSON.stringify({
        GeneXus: { InstallationPath: gxDir },
        Environment: { KBPath: kbDir }
    }));

    try {
        const result = runCli(['doctor', '--format', 'json', '--limit', '50'], {
            env: {
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: process.execPath,
                LOCALAPPDATA: tempRoot
            }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const comCheck = parsed.ok.checks.find((row) => row.id === 'gxpublic_com_registration');
        assert.ok(comCheck, 'gxpublic_com_registration check should be present');
        assert.ok(['pass', 'warn'].includes(comCheck.status), `expected pass or warn, got ${comCheck.status}`);

        const ideCheck = parsed.ok.checks.find((row) => row.id === 'legacy_ide_lock');
        assert.ok(ideCheck, 'legacy_ide_lock check should be present');
        assert.ok(['pass', 'warn'].includes(ideCheck.status), `expected pass or warn, got ${ideCheck.status}`);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('invalid format returns usage exit code 2', () => {
    const result = runCli(['status', '--format', 'yaml']);
    assert.equal(result.status, 2);
    assert.ok(result.stdout.includes('usage_error'));
});

test('toon output key ordering is stable', () => {
    const out = renderOutput({ ok: { b: 1, a: 2 }, meta: { z: true, y: true } }, 'toon');
    const okIndex = out.indexOf('ok:');
    const aIndex = out.indexOf('a: 2');
    const bIndex = out.indexOf('b: 1');
    assert.ok(okIndex >= 0);
    assert.ok(aIndex > okIndex);
    assert.ok(bIndex > aIndex);
});

test('quiet flag suppresses launcher stderr noise', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-quiet-'));
    try {
        const result = runCli(['--quiet'], {
            env: {
                GX_CONFIG_PATH: '',
                GENEXUS_MCP_GATEWAY_EXE: 'C:\\missing\\nope.exe',
                LOCALAPPDATA: tempRoot
            }
        });

        assert.equal(result.status, 1);
        assert.equal(result.stderr.trim(), '');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('stdio launcher writes a breadcrumb when the gateway is missing before spawn', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-stdio-missing-'));
    try {
        const configPath = path.join(tempRoot, 'config.json');
        fs.writeFileSync(configPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        const missingGateway = path.join(tempRoot, 'missing', 'GxMcp.Gateway.exe');
        const result = runCli([], {
            env: {
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: missingGateway,
                LOCALAPPDATA: tempRoot
            }
        });

        assert.equal(result.status, 1);
        const logPath = path.join(tempRoot, 'GenexusMCP', 'logs', 'last-stdio-error.txt');
        assert.equal(fs.existsSync(logPath), true, 'pre-spawn failure should leave a diagnostic file');
        const log = fs.readFileSync(logPath, 'utf8');
        assert.match(log, /timestampUtc:/);
        assert.match(log, /exitCode: 1/);
        assert.match(log, /Gateway executable not found/);
        assert.match(log, /missing[\\/]GxMcp\.Gateway\.exe/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('doctor surfaces the stdio error breadcrumb when one exists', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-doctor-stdio-'));
    try {
        const logPath = path.join(tempRoot, 'GenexusMCP', 'logs', 'last-stdio-error.txt');
        fs.mkdirSync(path.dirname(logPath), { recursive: true });
        fs.writeFileSync(logPath, 'timestampUtc: 2026-08-31T00:00:00.000Z\nexitCode: 1\n');

        const result = runCli(['doctor', '--format', 'json'], {
            env: { ...sandboxHomeEnv(tempRoot), LOCALAPPDATA: tempRoot }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const check = parsed.ok.checks.find((row) => row.id === 'stdio_error_log');
        assert.ok(check);
        assert.equal(check.status, 'warn');
        assert.ok(check.detail.includes(logPath));
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('update --help returns usage entry', () => {
    const result = runCli(['update', '--help', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'help');
    assert.equal(parsed.ok.command, 'update');
    assert.ok(parsed.ok.usage.includes('genexus-mcp update'));
});

test('detectClientInstalled flags an agent installed via marker even with no MCP config', () => {
    // Regression for the field report where Antigravity showed "not detected":
    // an agent whose install dir exists but which hasn't created our MCP config
    // file yet must still be detected as installed.
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-detect-'));
    const installDir = path.join(tempRoot, 'Programs', 'Antigravity');
    fs.mkdirSync(installDir, { recursive: true });

    const client = {
        name: 'Antigravity',
        path: path.join(tempRoot, 'never-created', 'mcp_config.json'),
        installMarkers: [installDir]
    };

    const det = detectClientInstalled(client);
    assert.equal(det.installed, true, 'marker dir present => installed');
    assert.equal(det.hasConfig, false, 'config file does not exist yet');
    assert.equal(det.markerHit, installDir);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('detectClientInstalled reports not-installed and lists checked paths', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-detect-'));
    const client = {
        name: 'Antigravity',
        path: path.join(tempRoot, 'nope.json'),
        installMarkers: [path.join(tempRoot, 'absent-a'), path.join(tempRoot, 'absent-b')]
    };

    const det = detectClientInstalled(client);
    assert.equal(det.installed, false);
    assert.equal(det.markerHit, null);
    assert.deepEqual(det.markersChecked, client.installMarkers);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('detectClientInstalled treats an existing config file as installed', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-detect-'));
    const cfg = path.join(tempRoot, 'settings.json');
    fs.writeFileSync(cfg, '{}');
    const client = { name: 'Gemini CLI', path: cfg, installMarkers: [] };

    const det = detectClientInstalled(client);
    assert.equal(det.installed, true);
    assert.equal(det.hasConfig, true);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

// Build a throwaway HOME so client-config writes never touch the real machine.
function sandboxHomeEnv(root) {
    return {
        HOME: root,
        USERPROFILE: root,
        APPDATA: path.join(root, 'AppData', 'Roaming'),
        LOCALAPPDATA: path.join(root, 'AppData', 'Local'),
        XDG_CONFIG_HOME: path.join(root, '.config')
    };
}

test('clients list returns structured status with summary', () => {
    const parentHome = sandboxHomeEnv('unused');
    for (const key of Object.keys(parentHome)) parentHome[key] = process.env[key];
    const result = runCli(['clients', '--format', 'json']);
    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'clients.list');
    assert.ok(Array.isArray(parsed.ok.clients));
    assert.ok(parsed.ok.clients.length >= 8);
    assert.equal(typeof parsed.ok.summary.installed, 'number');
    assert.equal(typeof parsed.ok.summary.registered, 'number');
    const row = parsed.ok.clients.find((c) => c.id === 'antigravity');
    assert.ok(row, 'antigravity should be listed');
    assert.equal(typeof row.installed, 'boolean');
    assert.equal(typeof row.registered, 'boolean');
    for (const client of parsed.ok.clients) {
        assert.ok(client.configPath.startsWith(cliHome + path.sep), client.configPath);
    }
    for (const [key, value] of Object.entries(parentHome)) assert.equal(process.env[key], value);
});

test('clients list reports OpenCode Desktop with shared opencode config', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-desktop-status-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        fs.mkdirSync(path.join(env.APPDATA, 'ai.opencode.desktop'), { recursive: true });

        const result = runCli(['clients', '--format', 'json'], { env });
        assert.equal(result.status, 0);
        const row = JSON.parse(result.stdout).ok.clients.find((client) => client.id === 'opencode-desktop');
        assert.ok(row, 'OpenCode Desktop should be listed');
        assert.equal(row.installed, true);
        assert.equal(row.registered, false);
        assert.equal(row.writeSupported, true);
        assert.equal(row.registrationMode, 'automatic');
        assert.equal(row.manualSetup, null);
        assert.equal(row.note, null);
        assert.equal(row.configPath, path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json'));

        // When the shared opencode.jsonc has the server registered, Desktop is recognized as registered
        const opencodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.jsonc');
        fs.mkdirSync(path.dirname(opencodeCfg), { recursive: true });
        fs.writeFileSync(opencodeCfg, JSON.stringify({
            mcp: {
                genexus18mcp: {
                    type: 'local',
                    command: ['npx.cmd', '-y', 'genexus-mcp@latest'],
                    environment: { GX_CONFIG_PATH: 'C:\\test\\config.json' }
                }
            }
        }, null, 2));

        const resultRegistered = runCli(['clients', '--format', 'json'], { env });
        assert.equal(resultRegistered.status, 0);
        const rowRegistered = JSON.parse(resultRegistered.stdout).ok.clients.find((client) => client.id === 'opencode-desktop');
        assert.equal(rowRegistered.installed, true);
        assert.equal(rowRegistered.registered, true);
        assert.equal(rowRegistered.command, 'npx.cmd');
        assert.equal(rowRegistered.configPath, opencodeCfg);
        assert.equal(rowRegistered.registrationMode, 'automatic');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add patches OpenCode Desktop into shared opencode config', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-desktop-add-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cfgPath = path.join(tempRoot, 'config.json');
        const desktopDir = path.join(env.APPDATA, 'ai.opencode.desktop');
        fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.mkdirSync(desktopDir, { recursive: true });

        const result = runCli(['clients', 'add', '--clients', 'opencode-desktop', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: cfgPath }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        assert.ok(parsed.ok.patchedClients.includes('OpenCode Desktop'));
        const opencodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
        assert.ok(fs.existsSync(opencodeCfg), 'shared opencode config should be created');
        const written = JSON.parse(fs.readFileSync(opencodeCfg, 'utf8'));
        assert.ok(written.mcp.genexus18mcp, 'shared config should contain genexus18mcp entry');
        assert.equal(written.mcp.genexus18mcp.environment?.GX_CONFIG_PATH, undefined);

        const listRes = runCli(['clients', '--format', 'json'], { env });
        const row = JSON.parse(listRes.stdout).ok.clients.find((client) => client.id === 'opencode-desktop');
        assert.ok(row && row.registered, 'OpenCode Desktop should now report registered');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('OpenCode status and remove inspect both json and jsonc when they coexist', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-dual-config-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const configPath = path.join(tempRoot, 'config.json');
        const openCodeDir = path.join(env.XDG_CONFIG_HOME, 'opencode');
        const jsonPath = path.join(openCodeDir, 'opencode.json');
        const jsoncPath = path.join(openCodeDir, 'opencode.jsonc');
        fs.mkdirSync(openCodeDir, { recursive: true });
        fs.writeFileSync(configPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.writeFileSync(jsoncPath, JSON.stringify({ mcp: { servers: {} } }));
        fs.writeFileSync(jsonPath, JSON.stringify({ mcp: { genexus18mcp: {
            type: 'local',
            command: ['npx.cmd', '-y', 'genexus-mcp@latest']
        } } }));

        const status = runCli(['clients', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: configPath }
        });
        assert.equal(status.status, 0);
        const row = JSON.parse(status.stdout).ok.clients.find((client) => client.id === 'opencode');
        assert.ok(row && row.registered);
        assert.equal(row.configPath, jsonPath);

        const removed = runCli(['clients', 'remove', '--clients', 'opencode', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: configPath }
        });
        assert.equal(removed.status, 0);
        assert.equal(JSON.parse(fs.readFileSync(jsonPath, 'utf8')).mcp.genexus18mcp, undefined);
        assert.equal(JSON.parse(fs.readFileSync(jsoncPath, 'utf8')).mcp.servers.genexus18mcp, undefined);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('OpenCode Desktop is not falsely reported as installed when only CLI config exists', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-desktop-undetected-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const opencodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.jsonc');
        fs.mkdirSync(path.dirname(opencodeCfg), { recursive: true });
        fs.writeFileSync(opencodeCfg, JSON.stringify({ mcp: {} }));

        const result = runCli(['clients', '--format', 'json'], { env });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const desktopRow = parsed.ok.clients.find((client) => client.id === 'opencode-desktop');
        assert.ok(desktopRow, 'OpenCode Desktop should be present in targets');
        assert.equal(desktopRow.installed, false, 'Desktop must not be detected installed without its install markers');

        const cliRow = parsed.ok.clients.find((client) => client.id === 'opencode');
        assert.ok(cliRow, 'OpenCode CLI should be present in targets');
        assert.equal(cliRow.installed, true, 'OpenCode CLI is detected installed via config file');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add still refuses a missing gateway for writable clients', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-client-missing-gateway-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cfgPath = path.join(tempRoot, 'config.json');
        const cursorConfig = path.join(tempRoot, '.cursor', 'mcp.json');
        fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

        const result = runCli(['clients', 'add', '--clients', 'cursor', '--format', 'json'], {
            env: {
                ...env,
                GX_CONFIG_PATH: cfgPath,
                GENEXUS_MCP_GATEWAY_EXE: path.join(tempRoot, 'missing', 'GxMcp.Gateway.exe')
            }
        });
        assert.equal(result.status, 1);
        const parsed = JSON.parse(result.stdout);
        assert.equal(parsed.error.code, 'operation_error');
        assert.match(parsed.error.message, /GATEWAY_EXE_MISSING|does not exist/i);
        assert.equal(fs.existsSync(cursorConfig), false, 'failed validation must not create a client config');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('getLauncher prefers the packaged gateway for Antigravity only', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-launcher-'));
    const gatewayPath = path.join(tempRoot, 'publish', 'GxMcp.Gateway.exe');
    const previous = process.env.GENEXUS_MCP_GATEWAY_EXE;
    delete process.env.GENEXUS_MCP_GATEWAY_EXE;
    try {
        fs.mkdirSync(path.dirname(gatewayPath), { recursive: true });
        fs.writeFileSync(gatewayPath, 'gateway');

        assert.deepEqual(getLauncher({ preferDirectGateway: true }, gatewayPath), {
            command: gatewayPath,
            args: []
        });
        assert.deepEqual(getLauncher({ preferDirectGateway: false }, gatewayPath), {
            command: process.platform === 'win32' ? 'npx.cmd' : 'npx',
            args: ['-y', 'genexus-mcp@latest']
        });
    } finally {
        if (previous === undefined) delete process.env.GENEXUS_MCP_GATEWAY_EXE;
        else process.env.GENEXUS_MCP_GATEWAY_EXE = previous;
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add selects a direct gateway for Antigravity and npx for other clients', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-client-launchers-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cfgPath = path.join(tempRoot, 'config.json');
        fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        const result = runCli(['clients', 'add', '--clients', 'antigravity,cursor', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: cfgPath, GENEXUS_MCP_GATEWAY_EXE: '' }
        });
        assert.equal(result.status, 0);

        const antigravityPath = path.join(tempRoot, '.gemini', 'antigravity', 'mcp_config.json');
        const cursorPath = path.join(tempRoot, '.cursor', 'mcp.json');
        const antigravity = JSON.parse(fs.readFileSync(antigravityPath, 'utf8')).mcpServers.genexus18mcp;
        const cursor = JSON.parse(fs.readFileSync(cursorPath, 'utf8')).mcpServers.genexus18mcp;
        const packagedGateway = path.join(__dirname, '..', 'publish', 'GxMcp.Gateway.exe');

        assert.equal(cursor.command, 'npx.cmd');
        assert.deepEqual(cursor.args, ['-y', 'genexus-mcp@latest']);
        if (fs.existsSync(packagedGateway)) {
            assert.equal(antigravity.command, packagedGateway);
            assert.deepEqual(antigravity.args, []);
        } else {
            assert.equal(antigravity.command, 'npx.cmd', 'source checkouts without publish artifacts retain the fallback');
        }
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add uses the unified Antigravity config when it already exists', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-antigravity-unified-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const configPath = path.join(tempRoot, 'config.json');
        const unifiedPath = path.join(tempRoot, '.gemini', 'config', 'mcp_config.json');
        fs.writeFileSync(configPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.mkdirSync(path.dirname(unifiedPath), { recursive: true });
        fs.writeFileSync(unifiedPath, JSON.stringify({ mcpServers: { unrelated: { command: 'keep-me' } } }));

        const result = runCli(['clients', 'add', '--clients', 'antigravity', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: configPath, GENEXUS_MCP_GATEWAY_EXE: '' }
        });
        assert.equal(result.status, 0);

        const written = JSON.parse(fs.readFileSync(unifiedPath, 'utf8'));
        const entry = written.mcpServers.genexus18mcp;
        const packagedGateway = path.join(__dirname, '..', 'publish', 'GxMcp.Gateway.exe');
        assert.ok(written.mcpServers.unrelated);
        if (fs.existsSync(packagedGateway)) {
            assert.equal(entry.command, packagedGateway);
            assert.deepEqual(entry.args, []);
        } else {
            assert.equal(entry.command, 'npx.cmd');
            assert.deepEqual(entry.args, ['-y', 'genexus-mcp@latest']);
        }
        assert.equal(fs.existsSync(path.join(tempRoot, '.gemini', 'antigravity', 'mcp_config.json')), false);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add registers a client into a sandbox home with backup + atomic write', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-clients-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

    // Pre-existing cursor config so we can assert a backup is taken.
    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    fs.writeFileSync(cursorCfg, JSON.stringify({ mcpServers: { other: { command: 'x' } } }, null, 2));

    const res = runCli(['clients', 'add', '--clients', 'cursor', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.meta.command, 'clients.add');
    assert.ok(parsed.ok.patchedClients.includes('Cursor'));

    const written = JSON.parse(fs.readFileSync(cursorCfg, 'utf8'));
    assert.ok(written.mcpServers.genexus18mcp, 'genexus18mcp entry should be written');
    assert.ok(written.mcpServers.other, 'pre-existing entries preserved');

    const baks = fs.readdirSync(path.dirname(cursorCfg)).filter((f) => f.includes('.bak'));
    assert.ok(baks.length >= 1, 'a .bak backup should be created before mutating');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add tolerates a JSONC (commented) VS Code mcp.json', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-jsonc-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

    const vscodeCfg = path.join(env.APPDATA, 'Code', 'User', 'mcp.json');
    fs.mkdirSync(path.dirname(vscodeCfg), { recursive: true });
    fs.writeFileSync(vscodeCfg, '{\n  // user comment\n  "servers": {\n    "foo": { "command": "bar" },\n  }\n}\n');

    const res = runCli(['clients', 'add', '--clients', 'vscode', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.ok(parsed.ok.patchedClients.includes('VS Code'), 'VS Code should be patched despite comments');

    const written = JSON.parse(fs.readFileSync(vscodeCfg, 'utf8'));
    assert.ok(written.servers.genexus18mcp, 'genexus18mcp server entry written');
    assert.ok(written.servers.foo, 'pre-existing server preserved');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add preserves OpenCode 1.x direct mcp shape', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-v1-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
    fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
    fs.writeFileSync(openCodeCfg, JSON.stringify({
        mcp: { other: { type: 'local', command: ['other-tool'] } }
    }, null, 2));

    const res = runCli(['clients', 'add', '--clients', 'opencode', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
    assert.ok(written.mcp.genexus18mcp, 'direct OpenCode entry should be written');
    assert.equal(written.mcp.genexus18mcp.enabled, true);
    assert.equal(written.mcp.genexus18mcp.disabled, undefined);
    assert.ok(written.mcp.other, 'unrelated direct MCP server should be preserved');
    assert.equal(written.mcp.servers, undefined);
    assert.equal(written.mcp.genexus18mcp.environment?.GX_CONFIG_PATH, undefined);

    const listed = runCli(['clients', '--format', 'json'], { env });
    assert.equal(listed.status, 0);
    const row = JSON.parse(listed.stdout).ok.clients.find((client) => client.id === 'opencode');
    assert.ok(row && row.registered, 'clients list should read the direct OpenCode entry');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add preserves OpenCode v2 nested mcp.servers shape', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-v2-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
    fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
    fs.writeFileSync(openCodeCfg, JSON.stringify({
        mcp: { servers: { other: { type: 'local', command: ['other-tool'] } } }
    }, null, 2));

    const res = runCli(['clients', 'add', '--clients', 'opencode', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
    assert.ok(written.mcp.servers.genexus18mcp, 'nested OpenCode entry should be written');
    assert.equal(written.mcp.servers.genexus18mcp.disabled, false);
    assert.equal(written.mcp.servers.genexus18mcp.enabled, undefined);
    assert.ok(written.mcp.servers.other, 'unrelated nested MCP server should be preserved');
    assert.equal(written.mcp.genexus18mcp, undefined);
    assert.equal(written.mcp.servers.genexus18mcp.environment?.GX_CONFIG_PATH, undefined);

    const listed = runCli(['clients', '--format', 'json'], { env });
    assert.equal(listed.status, 0);
    const row = JSON.parse(listed.stdout).ok.clients.find((client) => client.id === 'opencode');
    assert.ok(row && row.registered, 'clients list should read the nested OpenCode entry');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('init auto-registers detected OpenCode in either config layout', () => {
    for (const [label, mcp] of [
        ['direct', { other: { type: 'local', command: ['other-tool'] } }],
        ['nested', { servers: { other: { type: 'local', command: ['other-tool'] } } }]
    ]) {
        const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), `genexus-mcp-opencode-init-${label}-`));
        try {
            const env = sandboxHomeEnv(tempRoot);
            const kbDir = path.join(tempRoot, 'kb');
            const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
            fs.mkdirSync(kbDir, { recursive: true });
            fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
            fs.writeFileSync(openCodeCfg, JSON.stringify({ mcp }, null, 2));

            const result = runCli(
                ['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json'],
                { cwd: kbDir, env: { ...env, ...testGatewayEnv } }
            );
            assert.equal(result.status, 0, `${label} OpenCode init should succeed: ${result.stderr}`);

            const parsed = JSON.parse(result.stdout);
            assert.ok(parsed.ok.clientsPatchedCount >= 1, `${label} OpenCode should be auto-registered`);
            assert.ok(parsed.meta.patchedClients.includes('OpenCode (CLI)'));

            const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
            const entry = label === 'nested' ? written.mcp.servers.genexus18mcp : written.mcp.genexus18mcp;
            assert.ok(entry, `${label} OpenCode entry should be present after init`);
            assert.equal(entry.environment?.GX_CONFIG_PATH, undefined);
            assert.ok(label === 'nested' ? written.mcp.servers.other : written.mcp.other);
        } finally {
            removeTempPath(tempRoot, { recursive: true, force: true });
        }
    }
});

test('init with --global-config persists GX_CONFIG_PATH into client entry', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-init-global-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const kbDir = path.join(tempRoot, 'kb');
        const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
        fs.mkdirSync(kbDir, { recursive: true });
        fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
        fs.writeFileSync(openCodeCfg, JSON.stringify({ mcp: {} }, null, 2));

        const result = runCli(
            ['init', '--kb', kbDir, '--gx', testGxPath, '--global-config', '--no-smoke', '--format', 'json'],
            { cwd: kbDir, env: { ...env, ...testGatewayEnv } }
        );
        assert.equal(result.status, 0, `init should succeed: ${result.stderr}`);

        const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
        assert.ok(written.mcp.genexus18mcp);
        assert.equal(written.mcp.genexus18mcp.environment.GX_CONFIG_PATH, path.join(kbDir, 'config.json'));
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add with --global-config persists GX_CONFIG_PATH into client entry', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-add-global-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cfgPath = path.join(tempRoot, 'config.json');
        const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
        fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
        fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.writeFileSync(openCodeCfg, JSON.stringify({ mcp: {} }, null, 2));

        const res = runCli(['clients', 'add', '--clients', 'opencode', '--global-config', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: cfgPath }
        });
        assert.equal(res.status, 0);
        const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
        assert.ok(written.mcp.genexus18mcp);
        assert.equal(written.mcp.genexus18mcp.environment.GX_CONFIG_PATH, cfgPath);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('init auto-registers detected OpenCode Desktop when its marker is present', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-desktop-init-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const kbDir = path.join(tempRoot, 'kb');
        fs.mkdirSync(kbDir, { recursive: true });
        fs.mkdirSync(path.join(env.APPDATA, 'ai.opencode.desktop'), { recursive: true });

        const result = runCli(
            ['init', '--kb', kbDir, '--gx', testGxPath, '--no-smoke', '--format', 'json'],
            { cwd: kbDir, env: { ...env, ...testGatewayEnv } }
        );
        assert.equal(result.status, 0, `init should succeed: ${result.stderr}`);

        const parsed = JSON.parse(result.stdout);
        assert.ok(parsed.meta.patchedClients.includes('OpenCode Desktop'));
        const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
        assert.ok(fs.existsSync(openCodeCfg));
        const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
        assert.ok(written.mcp.genexus18mcp);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients add replaces a legacy genexus18 entry instead of duplicating it', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-legacy-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    fs.writeFileSync(cursorCfg, JSON.stringify({ mcpServers: { genexus18: { command: 'C:\\old\\start_mcp.bat' } } }, null, 2));

    const res = runCli(['clients', 'add', '--clients', 'cursor', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);

    const written = JSON.parse(fs.readFileSync(cursorCfg, 'utf8'));
    assert.ok(written.mcpServers.genexus18mcp, 'new genexus18mcp entry present');
    assert.equal(written.mcpServers.genexus18, undefined, 'legacy genexus18 removed (no duplicate)');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients list flags a registered command pointing at a missing launcher as stale (.bat too)', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-stale-'));
    const env = sandboxHomeEnv(tempRoot);
    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    // A non-.exe launcher (.bat) that no longer exists must also be flagged stale.
    const missing = path.join(tempRoot, 'gone', 'start_mcp.bat');
    fs.writeFileSync(cursorCfg, JSON.stringify({ mcpServers: { genexus18mcp: { command: missing, args: [] } } }, null, 2));

    const res = runCli(['clients', '--format', 'json'], { env });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    const cursor = parsed.ok.clients.find((c) => c.id === 'cursor');
    assert.ok(cursor.registered, 'cursor should read as registered');
    assert.equal(cursor.commandStale, true, 'missing launcher => stale');
    assert.ok(parsed.help.some((h) => h.includes('missing gateway exe')), 'help should call out the stale client');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients list separates a registered node launcher without an entrypoint from semantic validity', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-node-no-entrypoint-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const codexCfg = path.join(env.HOME, '.codex', 'config.toml');
        fs.mkdirSync(path.dirname(codexCfg), { recursive: true });
        const nodeCommand = process.execPath.replace(/\\/g, '\\\\').replace(/"/g, '\\\\"');
        fs.writeFileSync(codexCfg, [
            '[mcp_servers.genexus18mcp]',
            `command = "${nodeCommand}"`,
            'args = []',
            ''
        ].join('\n'));

        const result = runCli(['clients', '--format', 'json'], { env });
        assert.equal(result.status, 0, 'clients diagnostics retain the successful list exit code');
        const parsed = JSON.parse(result.stdout);
        const codex = parsed.ok.clients.find((client) => client.id === 'codex-cli');
        assert.ok(codex, 'Codex CLI should be listed');
        assert.equal(codex.registered, true, 'the config entry remains registered');
        assert.equal(codex.command, process.execPath, 'the effective command is reported');
        assert.deepEqual(codex.args, [], 'the effective args are reported');
        assert.equal(codex.launcherStructuralState, 'present', 'node.exe exists locally');
        assert.equal(codex.launcherSemanticState, 'invalid', 'node.exe without an entrypoint cannot start MCP');
        assert.match(codex.launcherSemanticReason, /entrypoint/i, 'the reason tells the operator what is missing');
        assert.equal(codex.commandStale, true, 'known invalid launcher is actionable through the existing stale flag');
        assert.match(codex.commandStaleReason, /entrypoint/i);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients list validates existing Gateway and known npx launchers locally', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-known-launchers-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const gatewayPath = path.join(tempRoot, 'publish', 'GxMcp.Gateway.exe');
        const antigravityCfg = path.join(tempRoot, '.gemini', 'antigravity', 'mcp_config.json');
        const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
        const codexCfg = path.join(tempRoot, '.codex', 'config.toml');
        const nodeEntrypoint = path.join(__dirname, 'run.js');
        fs.mkdirSync(path.dirname(gatewayPath), { recursive: true });
        fs.mkdirSync(path.dirname(antigravityCfg), { recursive: true });
        fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
        fs.mkdirSync(path.dirname(codexCfg), { recursive: true });
        fs.writeFileSync(gatewayPath, 'gateway');
        fs.writeFileSync(antigravityCfg, JSON.stringify({
            mcpServers: { genexus18mcp: { command: gatewayPath, args: [] } }
        }, null, 2));
        fs.writeFileSync(cursorCfg, JSON.stringify({
            mcpServers: { genexus18mcp: { command: 'npx.cmd', args: ['-y', 'genexus-mcp@latest'] } }
        }, null, 2));
        const nodeCommand = process.execPath.replace(/\\/g, '\\\\');
        const nodeScript = nodeEntrypoint.replace(/\\/g, '\\\\');
        fs.writeFileSync(codexCfg, [
            '[mcp_servers.genexus18mcp]',
            `command = "${nodeCommand}"`,
            `args = ["${nodeScript}"]`,
            ''
        ].join('\n'));

        const result = runCli(['clients', '--format', 'json'], {
            env: { ...env, GENEXUS_MCP_GATEWAY_EXE: gatewayPath }
        });
        assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
        const parsed = JSON.parse(result.stdout);
        const gateway = parsed.ok.clients.find((client) => client.id === 'antigravity');
        const npx = parsed.ok.clients.find((client) => client.id === 'cursor');
        assert.ok(gateway && npx, 'both configured clients should be listed');

        assert.equal(gateway.registered, true);
        assert.equal(gateway.command, gatewayPath);
        assert.deepEqual(gateway.args, []);
        assert.equal(gateway.launcherStructuralState, 'present');
        assert.equal(gateway.launcherSemanticState, 'valid');
        assert.equal(gateway.commandStale, false);

        assert.equal(npx.registered, true);
        assert.equal(npx.command, 'npx.cmd');
        assert.deepEqual(npx.args, ['-y', 'genexus-mcp@latest']);
        assert.equal(npx.launcherStructuralState, 'present');
        assert.equal(npx.launcherSemanticState, 'valid');
        assert.equal(npx.commandStale, false);

        const node = parsed.ok.clients.find((client) => client.id === 'codex-cli');
        assert.ok(node, 'Codex CLI should be listed');
        assert.equal(node.command, process.execPath);
        assert.deepEqual(node.args, [nodeEntrypoint]);
        assert.equal(node.launcherStructuralState, 'present');
        assert.equal(node.launcherSemanticState, 'valid');
        assert.equal(node.commandStale, false);
        assert.equal(parsed.ok.summary.semanticInvalid, 0);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients list keeps an existing unrecognized launcher indeterminate', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-unknown-launcher-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
        fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
        fs.writeFileSync(cursorCfg, JSON.stringify({
            mcpServers: { genexus18mcp: { command: 'custom-mcp-launcher', args: ['--stdio'] } }
        }, null, 2));

        const result = runCli(['clients', '--format', 'json'], { env });
        assert.equal(result.status, 0);
        const cursor = JSON.parse(result.stdout).ok.clients.find((client) => client.id === 'cursor');
        assert.equal(cursor.registered, true);
        assert.equal(cursor.launcherStructuralState, 'indeterminate');
        assert.equal(cursor.launcherSemanticState, 'unknown');
        assert.equal(cursor.commandStale, false, 'unknown commands are not treated as invalid');
        assert.equal(cursor.launcherPathDrift, false, 'drift fields are always present in the payload');
        assert.equal(cursor.launcherPathDriftReason, null);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients list reports a checkout gateway as path drift, not as stale', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-checkout-drift-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        // A checkout install registers the client against the checkout's own
        // publish/ gateway while this CLI compares against its own gateway.
        const checkoutGateway = path.join(tempRoot, 'checkout', 'publish', 'GxMcp.Gateway.exe');
        const currentGateway = path.join(tempRoot, 'current', 'GxMcp.Gateway.exe');
        fs.mkdirSync(path.dirname(checkoutGateway), { recursive: true });
        fs.mkdirSync(path.dirname(currentGateway), { recursive: true });
        fs.writeFileSync(checkoutGateway, 'checkout');
        fs.writeFileSync(currentGateway, 'current');

        const antigravityCfg = path.join(tempRoot, '.gemini', 'antigravity', 'mcp_config.json');
        fs.mkdirSync(path.dirname(antigravityCfg), { recursive: true });
        fs.writeFileSync(antigravityCfg, JSON.stringify({
            mcpServers: { genexus18mcp: { command: checkoutGateway, args: [] } }
        }, null, 2));

        const result = runCli(['clients', '--format', 'json'], {
            env: { ...env, GENEXUS_MCP_GATEWAY_EXE: currentGateway }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const antigravity = parsed.ok.clients.find((client) => client.id === 'antigravity');
        assert.equal(antigravity.commandStale, false, 'an existing gateway from another install is not broken');
        assert.equal(antigravity.launcherStructuralState, 'present');
        assert.equal(antigravity.launcherSemanticState, 'valid');
        assert.equal(antigravity.launcherPathDrift, true, 'the drift is still reported');
        assert.match(antigravity.launcherPathDriftReason, /differs from this CLI's gateway/);
        assert.ok(parsed.help.some((h) => h.startsWith('Note:')), 'drift is informational, not a repair instruction');
        assert.ok(!parsed.help.some((h) => h.includes('missing gateway exe')), 'a valid launcher must not be listed as missing');
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('clients list marks the same gateway as this CLI without drift', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-same-gateway-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const gatewayPath = path.join(tempRoot, 'publish', 'GxMcp.Gateway.exe');
        fs.mkdirSync(path.dirname(gatewayPath), { recursive: true });
        fs.writeFileSync(gatewayPath, 'gateway');
        const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
        fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
        fs.writeFileSync(cursorCfg, JSON.stringify({
            mcpServers: { genexus18mcp: { command: gatewayPath, args: [] } }
        }, null, 2));

        const result = runCli(['clients', '--format', 'json'], {
            env: { ...env, GENEXUS_MCP_GATEWAY_EXE: gatewayPath }
        });
        assert.equal(result.status, 0);
        const cursor = JSON.parse(result.stdout).ok.clients.find((client) => client.id === 'cursor');
        assert.equal(cursor.commandStale, false);
        assert.equal(cursor.launcherPathDrift, false);
        assert.equal(cursor.launcherPathDriftReason, null);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('readJsonFileSafe parses JSONC without corrupting string values containing commas', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-jsonc2-'));
    const f = path.join(tempRoot, 'mcp.json');
    // Leading comment forces the JSONC fallback; the string value contains ", ]"
    // and the object has a legitimate trailing comma that SHOULD be stripped.
    fs.writeFileSync(f, '{\n  // comment\n  "servers": { "x": { "command": "a, ]b", "args": ["c,]"] } },\n}\n');
    const parsed = readJsonFileSafe(f);
    assert.ok(parsed, 'should parse');
    assert.equal(parsed.servers.x.command, 'a, ]b', 'comma inside string value preserved');
    assert.deepEqual(parsed.servers.x.args, ['c,]'], 'comma inside array string preserved');
    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('readJsonFileSafe strips a genuine trailing comma', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-jsonc3-'));
    const f = path.join(tempRoot, 'mcp.json');
    fs.writeFileSync(f, '{\n  // c\n  "a": [1, 2, 3,],\n  "b": { "x": 1, },\n}\n');
    const parsed = readJsonFileSafe(f);
    assert.deepEqual(parsed.a, [1, 2, 3]);
    assert.deepEqual(parsed.b, { x: 1 });
    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add without --clients is a usage error', () => {
    const res = runCli(['clients', 'add', '--format', 'json']);
    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.error.code, 'usage_error');
});

test('clients remove drops the genexus entry (sandbox home)', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-rm-'));
    const env = sandboxHomeEnv(tempRoot);
    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    fs.writeFileSync(cursorCfg, JSON.stringify({ mcpServers: { genexus18mcp: { command: 'npx' }, genexus18: { command: 'old' } } }, null, 2));

    const res = runCli(['clients', 'remove', '--clients', 'cursor', '--format', 'json'], { env });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.ok(parsed.ok.removedClients.includes('Cursor'));

    const written = JSON.parse(fs.readFileSync(cursorCfg, 'utf8'));
    assert.equal(written.mcpServers.genexus18mcp, undefined, 'genexus18mcp removed');
    assert.equal(written.mcpServers.genexus18, undefined, 'legacy genexus18 also removed');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients remove drops both OpenCode config shapes and legacy key', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-rm-'));
    const env = sandboxHomeEnv(tempRoot);
    const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
    fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
    fs.writeFileSync(openCodeCfg, JSON.stringify({
        mcp: {
            genexus: { type: 'local', command: ['old'] },
            genexus18: { type: 'local', command: ['older'] },
            genexus18mcp: { type: 'local', command: ['latest'] },
            servers: {
                genexus: { type: 'local', command: ['nested'] },
                genexus18: { type: 'local', command: ['nested-old'] },
                genexus18mcp: { type: 'local', command: ['nested-latest'] },
                other: { type: 'local', command: ['other'] }
            }
        }
    }, null, 2));

    const res = runCli(['clients', 'remove', '--clients', 'opencode', '--format', 'json'], { env });
    assert.equal(res.status, 0);
    const written = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
    assert.equal(written.mcp.genexus, undefined);
    assert.equal(written.mcp.genexus18, undefined);
    assert.equal(written.mcp.genexus18mcp, undefined);
    assert.equal(written.mcp.servers.genexus, undefined);
    assert.equal(written.mcp.servers.genexus18, undefined);
    assert.equal(written.mcp.servers.genexus18mcp, undefined);
    assert.ok(written.mcp.servers.other, 'unrelated nested MCP server should be preserved');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('strict semver compares prerelease precedence and ignores build metadata', () => {
    assert.deepEqual(parseSemver('v1.2.3-alpha.1+build.7'), {
        valid: true, major: 1, minor: 2, patch: 3,
        prerelease: ['alpha', '1'], build: ['build', '7']
    });
    assert.equal(compareSemver('1.0.0-alpha', '1.0.0-alpha.1'), -1);
    assert.equal(compareSemver('1.0.0', '1.0.0-rc.1'), 1);
    assert.equal(compareSemver('1.0.0+one', '1.0.0+two'), 0);
});

test('semver and channel validation are explicit for malformed input', () => {
    assert.equal(parseSemver('1.2'), null);
    assert.equal(parseSemver('1.2.3-01'), null);
    assert.equal(compareSemver('garbage', '1.0.0'), null);
    assert.equal(validateChannel('latest'), 'latest');
    assert.equal(validateChannel('next-2026'), 'next-2026');
    assert.equal(validateChannel('bad channel'), null);
    assert.equal(validateChannel(''), null);
});

test('npx update plan does not use npm cache clean as an update', () => {
    const plan = upgradePlanFor('npx-latest', 'latest');
    assert.equal(plan.applyCommand, null);
    assert.equal(plan.auto, true);
});

test('runCommand reports exit code and kills timed out child', async () => {
    const result = await runCommand(process.execPath, ['-e', 'setTimeout(() => {}, 1000)'], { timeoutMs: 30 });
    assert.equal(result.ok, false);
    assert.equal(result.timedOut, true);
    assert.equal(typeof result.code, 'number');
});

test('detectInstallMethod returns fixed-path when GENEXUS_MCP_GATEWAY_EXE is set', () => {
    const prev = process.env.GENEXUS_MCP_GATEWAY_EXE;
    process.env.GENEXUS_MCP_GATEWAY_EXE = 'C:\\Tools\\GenexusMCP\\GxMcp.Gateway.exe';
    try {
        const r = detectInstallMethod();
        assert.equal(r.method, 'fixed-path');
        assert.equal(r.detail, 'C:\\Tools\\GenexusMCP\\GxMcp.Gateway.exe');
    } finally {
        if (prev === undefined) delete process.env.GENEXUS_MCP_GATEWAY_EXE;
        else process.env.GENEXUS_MCP_GATEWAY_EXE = prev;
    }
});

test('upgradePlanFor carries the selected channel through every install method', () => {
    const npx = upgradePlanFor('npx-latest', 'next');
    assert.equal(npx.channel, 'next');
    assert.match(npx.steps.join(' '), /npx genexus-mcp@next/);
    assert.doesNotMatch(npx.steps.join(' '), /@latest/);

    const npm = upgradePlanFor('npm-global', 'next');
    assert.equal(npm.channel, 'next');
    assert.deepEqual(npm.applyCommand.args, ['install', '-g', 'genexus-mcp@next']);

    const fixed = upgradePlanFor('fixed-path', 'next');
    assert.equal(fixed.channel, 'next');
    assert.match(fixed.steps.join(' '), /next/);
    assert.doesNotMatch(fixed.steps.join(' '), /npm @latest/);

    const direct = upgradePlanFor('package-direct', 'next');
    assert.equal(direct.channel, 'next');
});

test('upgradePlanFor encodes the per-method upgrade strategy', () => {
    const npx = upgradePlanFor('npx-latest', 'latest');
    assert.equal(npx.auto, true, 'npx@latest auto-updates on restart');
    assert.ok(npx.steps.join(' ').toLowerCase().includes('restart'));

    const npm = upgradePlanFor('npm-global', 'latest');
    assert.equal(npm.auto, false);
    assert.deepEqual(npm.applyCommand.args, ['install', '-g', 'genexus-mcp@latest']);

    const npmNext = upgradePlanFor('npm-global', 'next');
    assert.deepEqual(npmNext.applyCommand.args, ['install', '-g', 'genexus-mcp@next']);

    const fixed = upgradePlanFor('fixed-path', 'latest');
    assert.equal(fixed.auto, false);
    assert.equal(fixed.applyCommand, null, 'fixed-path has no npm apply; uses the installer');

    const direct = upgradePlanFor('package-direct', 'latest');
    assert.equal(direct.auto, false);
    assert.equal(direct.applyCommand, null);
    assert.ok(direct.steps.join(' ').includes('clients add --clients antigravity'));
});

test('gateway passthrough remains intact when no AXI subcommand is used', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const fakeGateway = path.join(tempRoot, 'fake-gateway.js');
    const fakeConfig = path.join(tempRoot, 'config.json');

    fs.writeFileSync(fakeConfig, JSON.stringify({ ok: true }));
    fs.writeFileSync(fakeGateway, 'process.stdout.write(`gateway:${process.argv.slice(2).join(",")}`); process.exit(0);');

    const result = runCli([fakeGateway, 'hello', 'world'], {
        env: {
            GX_CONFIG_PATH: fakeConfig,
            GENEXUS_MCP_GATEWAY_EXE: process.execPath
        }
    });

    assert.equal(result.status, 0);
    assert.ok(result.stdout.includes('gateway:hello,world'));

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('parseArgs recognizes every version alias as a command token', () => {
    const { parseArgs } = require('./index');
    for (const alias of ['--version', '-v', 'version']) {
        const parsed = parseArgs([alias]);
        assert.equal(parsed.command, 'version', `${alias} must route to the version command`);
        assert.deepEqual(parsed.unknownFlags, [], `${alias} must not be reported as an unknown flag`);
    }
    // Version aliases are consumed as command tokens, so the remaining flags are parsed.
    assert.equal(parseArgs(['version', '--format', 'json']).options.format, 'json');
    assert.equal(parseArgs(['-v', '--format', 'json']).options.format, 'json');
    assert.equal(parseArgs(['--version', '--format', 'json']).options.format, 'json');
    // Arbitrary passthrough arguments stay in the passthrough lane.
    assert.equal(parseArgs(['hello', 'world']).command, null);
});

test('version aliases print the package version without entering gateway passthrough', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-version-'));
    const fakeConfig = path.join(tempRoot, 'config.json');
    fs.writeFileSync(fakeConfig, JSON.stringify({ ok: true }));

    const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'package.json'), 'utf8'));
    // Pointing the gateway exe at the Node binary makes a regression loud: a leaked
    // passthrough of `--version` would print Node's version instead of the package's.
    const env = { GX_CONFIG_PATH: fakeConfig, GENEXUS_MCP_GATEWAY_EXE: process.execPath };

    try {
        for (const alias of [['--version'], ['-v'], ['version']]) {
            const result = runCli(alias, { env });
            assert.equal(result.status, 0, `${alias.join(' ')} must exit 0`);
            assert.equal(result.stdout.trim(), pkg.version, `${alias.join(' ')} must print the package version`);
        }
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('version --format json returns an axi-cli/1 envelope for every alias', () => {
    const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'package.json'), 'utf8'));

    for (const args of [
        ['version', '--format', 'json'],
        ['-v', '--format', 'json'],
        ['--version', '--format', 'json']
    ]) {
        const result = runCli(args);
        assert.equal(result.status, 0, `${args.join(' ')} must exit 0`);
        const parsed = JSON.parse(result.stdout);
        assert.equal(parsed.ok.version, pkg.version);
        assert.equal(parsed.meta.schemaVersion, 'axi-cli/1');
        assert.equal(parsed.meta.command, 'version');
    }
});

test('version rejects unknown flags instead of printing a version', () => {
    const result = runCli(['version', '--bogus']);
    assert.equal(result.status, 2);
    assert.match(result.stdout, /Unknown flag: --bogus/);
});

test('version --help documents the command', () => {
    const result = runCli(['version', '--help', '--format', 'json']);
    assert.equal(result.status, 0);
    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.ok.command, 'version');
    assert.match(parsed.ok.usage, /--version/);
});

test('version fails loudly when the package version cannot be read', async () => {
    const { handleVersion } = require('./commands/axi');
    const result = await handleVersion({}, { EXIT_CODES: { OK: 0, ERROR: 1, USAGE: 2 } }, {
        getPackageVersion: () => null
    });

    assert.equal(result.exitCode, 1);
    assert.equal(result.envelope.error.code, 'version_unavailable');
    assert.match(result.envelope.error.message, /package\.json/);
});

test('stdio launcher persists the last child stderr when the gateway exits non-zero', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-stdio-crash-'));
    try {
        const configPath = path.join(tempRoot, 'config.json');
        const fakeGateway = path.join(tempRoot, 'fake-gateway.js');
        fs.writeFileSync(configPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.writeFileSync(fakeGateway, 'process.stderr.write("gateway bootstrap failed\\nsecond line\\n"); process.exit(7);');

        const result = runCli([fakeGateway], {
            env: {
                GX_CONFIG_PATH: configPath,
                GENEXUS_MCP_GATEWAY_EXE: process.execPath,
                LOCALAPPDATA: tempRoot
            }
        });

        assert.equal(result.status, 7);
        assert.match(result.stderr, /gateway bootstrap failed/);
        const logPath = path.join(tempRoot, 'GenexusMCP', 'logs', 'last-stdio-error.txt');
        assert.equal(fs.existsSync(logPath), true, 'child crash should leave a diagnostic file');
        const log = fs.readFileSync(logPath, 'utf8');
        assert.match(log, /timestampUtc:/);
        assert.match(log, /exitCode: 7/);
        assert.match(log, /gateway bootstrap failed/);
        assert.match(log, /second line/);
    } finally {
        removeTempPath(tempRoot, { recursive: true, force: true });
    }
});

test('npm package contains all bin, postinstall and required runtime files', () => {
    const pkgPath = path.join(__dirname, '..', 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));

    if (typeof pkg.bin === 'string') {
        assert.ok(fs.existsSync(path.join(__dirname, '..', pkg.bin)), `bin file ${pkg.bin} must exist`);
    } else if (typeof pkg.bin === 'object' && pkg.bin !== null) {
        for (const [name, binRel] of Object.entries(pkg.bin)) {
            assert.ok(fs.existsSync(path.join(__dirname, '..', binRel)), `bin entry ${name} (${binRel}) must exist`);
        }
    }

    if (pkg.scripts && pkg.scripts.postinstall) {
        const match = pkg.scripts.postinstall.match(/node\s+([\w./\\-]+)/);
        if (match) {
            const scriptRel = match[1];
            assert.ok(fs.existsSync(path.join(__dirname, '..', scriptRel)), `postinstall script ${scriptRel} must exist on disk`);

            const normalizedTarget = scriptRel.replace(/\\/g, '/');
            const isCovered = (pkg.files || []).some((pattern) => {
                const normalizedPat = pattern.replace(/\\/g, '/');
                return normalizedTarget === normalizedPat || normalizedTarget.startsWith(normalizedPat.endsWith('/') ? normalizedPat : normalizedPat + '/');
            });
            assert.ok(
                isCovered,
                `postinstall target '${scriptRel}' must be included in package.json files array so it is shipped in the npm tarball (regression for #114)`
            );
        }
    }

    const requiredFiles = ['cli/lib/stdio-diagnostics.js', 'scripts/test-one.js'];
    for (const relative of requiredFiles) {
        assert.ok(fs.existsSync(path.join(__dirname, '..', relative)), `runtime file ${relative} must exist on disk`);
        const normalized = relative.replace(/\\/g, '/');
        const covered = (pkg.files || []).some((pattern) => {
            const normalizedPattern = pattern.replace(/\\/g, '/');
            return normalized === normalizedPattern || normalized.startsWith(normalizedPattern.endsWith('/') ? normalizedPattern : `${normalizedPattern}/`);
        });
        assert.ok(covered, `runtime file '${relative}' must be included by package.json files`);
    }
});

test('isOurMcpEntry identifies third-party / HTTP servers vs our own entry', () => {
    const { isOurMcpEntry } = require('./lib/config');

    // Official / third-party MCP servers
    assert.equal(isOurMcpEntry({ url: 'http://localhost:8001/mcp' }), false);
    assert.equal(isOurMcpEntry({ type: 'http', url: 'https://example.com/mcp' }), false);
    assert.equal(isOurMcpEntry({ type: 'sse', url: 'http://127.0.0.1:8000/sse' }), false);
    assert.equal(isOurMcpEntry({ command: 'node', args: ['some-other-server.js'] }), false);

    // Our entries
    assert.equal(isOurMcpEntry({ command: 'npx.cmd', args: ['-y', 'genexus-mcp@latest'] }), true);
    assert.equal(isOurMcpEntry({ command: 'C:\\path\\start_mcp.bat' }), true);
    assert.equal(isOurMcpEntry({ command: 'C:\\bin\\GxMcp.Gateway.exe' }), true);
    assert.equal(isOurMcpEntry({ command: 'node', env: { GX_CONFIG_PATH: 'C:\\kb\\config.json' } }), true);
    assert.equal(isOurMcpEntry({ url: 'http://custom' }, 'genexus18mcp'), true);
});

test('clients add refuses to overwrite a third-party HTTP MCP server without --force', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-collision-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

    // Pre-existing official GeneXus MCP entry under genexus18mcp
    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    fs.writeFileSync(cursorCfg, JSON.stringify({
        mcpServers: {
            genexus18mcp: { url: 'http://localhost:8001/mcp', type: 'http' }
        }
    }, null, 2));

    const res = runCli(['clients', 'add', '--clients', 'cursor', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.patchedClients.length, 0);
    assert.equal(parsed.meta.failedClients.length, 1);
    assert.match(parsed.meta.failedClients[0].reason, /third-party or HTTP MCP server/);

    // Ensure the original HTTP server was NOT modified or deleted
    const after = JSON.parse(fs.readFileSync(cursorCfg, 'utf8'));
    assert.equal(after.mcpServers.genexus18mcp.url, 'http://localhost:8001/mcp');

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add with --force overwrites an existing third-party entry', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-force-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    fs.writeFileSync(cursorCfg, JSON.stringify({
        mcpServers: {
            genexus18mcp: { url: 'http://localhost:8001/mcp', type: 'http' }
        }
    }, null, 2));

    const res = runCli(['clients', 'add', '--clients', 'cursor', '--force', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.ok(parsed.ok.patchedClients.includes('Cursor'));

    const after = JSON.parse(fs.readFileSync(cursorCfg, 'utf8'));
    assert.equal(after.mcpServers.genexus18mcp.url, undefined);
    assert.ok(after.mcpServers.genexus18mcp.command);

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add supports custom --server-name across all formats for multi-MCP coexistence', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-custom-name-'));
    const env = sandboxHomeEnv(tempRoot);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

    // 1. mcpServers (Cursor)
    const cursorCfg = path.join(tempRoot, '.cursor', 'mcp.json');
    fs.mkdirSync(path.dirname(cursorCfg), { recursive: true });
    fs.writeFileSync(cursorCfg, JSON.stringify({
        mcpServers: { genexus: { url: 'http://localhost:8001/mcp' } }
    }, null, 2));

    // 2. VS Code servers
    const vscodeCfg = path.join(env.APPDATA, 'Code', 'User', 'mcp.json');
    fs.mkdirSync(path.dirname(vscodeCfg), { recursive: true });
    fs.writeFileSync(vscodeCfg, JSON.stringify({
        servers: { genexus: { url: 'http://localhost:8001/mcp' } }
    }, null, 2));

    // 3. OpenCode nested
    const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
    fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
    fs.writeFileSync(openCodeCfg, JSON.stringify({
        mcp: { servers: { genexus: { url: 'http://localhost:8001/mcp' } } }
    }, null, 2));

    // 4. Codex TOML
    const codexCfg = path.join(tempRoot, '.codex', 'config.toml');
    fs.mkdirSync(path.dirname(codexCfg), { recursive: true });
    fs.writeFileSync(codexCfg, '[mcp_servers.genexus]\nurl = "http://localhost:8001/mcp"\n');

    const res = runCli(['clients', 'add', '--clients', 'cursor,vscode,opencode,codex-cli', '--server-name', 'Gx18byLennix', '--format', 'json'], {
        env: { ...env, GX_CONFIG_PATH: cfgPath }
    });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.patchedCount, 4);

    // Assert both coexist side-by-side
    const cursorWritten = JSON.parse(fs.readFileSync(cursorCfg, 'utf8'));
    assert.equal(cursorWritten.mcpServers.genexus.url, 'http://localhost:8001/mcp');
    assert.ok(cursorWritten.mcpServers.Gx18byLennix);

    const vsCodeWritten = JSON.parse(fs.readFileSync(vscodeCfg, 'utf8'));
    assert.equal(vsCodeWritten.servers.genexus.url, 'http://localhost:8001/mcp');
    assert.ok(vsCodeWritten.servers.Gx18byLennix);

    const openCodeWritten = JSON.parse(fs.readFileSync(openCodeCfg, 'utf8'));
    assert.equal(openCodeWritten.mcp.servers.genexus.url, 'http://localhost:8001/mcp');
    assert.ok(openCodeWritten.mcp.servers.Gx18byLennix);

    const codexWritten = fs.readFileSync(codexCfg, 'utf8');
    assert.ok(codexWritten.includes('[mcp_servers.genexus]'));
    assert.ok(codexWritten.includes('[mcp_servers.Gx18byLennix]'));

    removeTempPath(tempRoot, { recursive: true, force: true });
});

test('clients add rejects invalid --server-name characters with usage error', () => {
    const res = runCli(['clients', 'add', '--clients', 'cursor', '--server-name', 'bad name with spaces!', '--format', 'json']);
    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.match(parsed.error.message, /alphanumeric/);
});

function fsFailureProxy(realFs, failure) {
    let calls = 0;
    return new Proxy(realFs, {
        get(target, property) {
            if (property === failure.method) {
                return (...args) => {
                    calls += 1;
                    if (!failure.match || failure.match(args, calls)) throw new Error(failure.message);
                    return target[property](...args);
                };
            }
            const value = target[property];
            return typeof value === 'function' ? value.bind(target) : value;
        }
    });
}

function patchFailureFixture(prefix, t) {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
    const env = sandboxHomeEnv(tempRoot);
    const previous = Object.fromEntries(Object.keys(env).map(key => [key, process.env[key]]));
    t.after(() => {
        for (const [key, value] of Object.entries(previous)) {
            if (value === undefined) delete process.env[key];
            else process.env[key] = value;
        }
    });
    Object.assign(process.env, env);
    const cfgPath = path.join(tempRoot, 'config.json');
    fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
    const openCodeCfg = path.join(env.XDG_CONFIG_HOME, 'opencode', 'opencode.json');
    const vscodeCfg = path.join(env.APPDATA, 'Code', 'User', 'mcp.json');
    fs.mkdirSync(path.dirname(openCodeCfg), { recursive: true });
    fs.mkdirSync(path.dirname(vscodeCfg), { recursive: true });
    fs.writeFileSync(openCodeCfg, JSON.stringify({ mcp: { other: { type: 'local' } } }));
    fs.writeFileSync(vscodeCfg, JSON.stringify({ servers: { other: { type: 'stdio' } } }));
    return { tempRoot, env, cfgPath, openCodeCfg, vscodeCfg };
}

test('patchClientConfig reports backup failure without claiming that client patched', (t) => {
    const fixture = patchFailureFixture('genexus-mcp-partial-backup-', t);
    try {
        const result = patchClientConfig(fixture.cfgPath, {
            ids: ['opencode', 'vscode'], onlyExisting: false,
            fs: fsFailureProxy(fs, { method: 'copyFileSync', message: 'backup denied', match: ([source]) => source === fixture.openCodeCfg })
        });
        assert.deepEqual(result.patched, ['VS Code']);
        assert.deepEqual(result.failed, [{ client: 'OpenCode (CLI)', reason: 'backup denied' }]);
        assert.equal(JSON.parse(fs.readFileSync(fixture.openCodeCfg, 'utf8')).mcp.genexus18mcp, undefined);
        assert.ok(JSON.parse(fs.readFileSync(fixture.vscodeCfg, 'utf8')).servers.genexus18mcp);
    } finally { removeTempPath(fixture.tempRoot, { recursive: true, force: true }); }
});

test('patchClientConfig preserves earlier success when a later client write fails', (t) => {
    const fixture = patchFailureFixture('genexus-mcp-partial-write-', t);
    try {
        const result = patchClientConfig(fixture.cfgPath, {
            ids: ['opencode', 'vscode'], onlyExisting: false,
            fs: fsFailureProxy(fs, { method: 'writeFileSync', message: 'write denied', match: ([filePath]) => filePath === `${fixture.vscodeCfg}.tmp-${process.pid}` })
        });
        assert.deepEqual(result.patched, ['OpenCode (CLI)']);
        assert.deepEqual(result.failed, [{ client: 'VS Code', reason: 'write denied' }]);
        assert.ok(JSON.parse(fs.readFileSync(fixture.openCodeCfg, 'utf8')).mcp.genexus18mcp);
        assert.equal(JSON.parse(fs.readFileSync(fixture.vscodeCfg, 'utf8')).servers.genexus18mcp, undefined);
    } finally { removeTempPath(fixture.tempRoot, { recursive: true, force: true }); }
});

test('patchClientConfig reports post-write read-back failure as partial state', (t) => {
    const fixture = patchFailureFixture('genexus-mcp-partial-readback-', t);
    try {
        const result = patchClientConfig(fixture.cfgPath, {
            ids: ['opencode', 'vscode'], onlyExisting: false,
            fs: fsFailureProxy(fs, { method: 'readFileSync', message: 'read-back denied', match: ([filePath], calls) => filePath === fixture.openCodeCfg && calls === 2 })
        });
        assert.deepEqual(result.patched, ['VS Code']);
        assert.deepEqual(result.failed, [{ client: 'OpenCode (CLI)', reason: 'post-write verification failed (genexus18mcp entry not found after write)' }]);
        assert.ok(JSON.parse(fs.readFileSync(fixture.openCodeCfg, 'utf8')).mcp.genexus18mcp);
        assert.ok(JSON.parse(fs.readFileSync(fixture.vscodeCfg, 'utf8')).servers.genexus18mcp);
    } finally { removeTempPath(fixture.tempRoot, { recursive: true, force: true }); }
});

test('patchClientConfig keeps a stale same-second backup and writes a distinct backup', (t) => {
    const fixture = patchFailureFixture('genexus-mcp-stale-backup-', t);
    try {
        const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
        const stale = `${fixture.openCodeCfg}.${stamp}.bak`;
        fs.writeFileSync(stale, 'stale backup');
        const result = patchClientConfig(fixture.cfgPath, { ids: ['opencode'], onlyExisting: false });
        assert.deepEqual(result.failed, []);
        assert.equal(fs.readFileSync(stale, 'utf8'), 'stale backup');
        const backups = fs.readdirSync(path.dirname(fixture.openCodeCfg)).filter((name) => name.startsWith(path.basename(fixture.openCodeCfg)) && name.endsWith('.bak'));
        assert.equal(backups.length, 2);
        assert.ok(backups.some((name) => name !== path.basename(stale)));
    } finally { removeTempPath(fixture.tempRoot, { recursive: true, force: true }); }
});
