const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const {
    getClientAdapter,
    McpServersJsonAdapter,
    VsCodeServersAdapter,
    OpenCodeJsoncAdapter,
    CodexTomlAdapter,
    ClientConfigManager
} = require('./client-adapters');
const {
    generateNeutralConfig,
    applyLauncherConfigOrExit
} = require('./config');

test('getClientAdapter returns correct strategy for each format', () => {
    assert.ok(getClientAdapter('mcpServers') instanceof McpServersJsonAdapter);
    assert.ok(getClientAdapter('vscode-servers') instanceof VsCodeServersAdapter);
    assert.ok(getClientAdapter('opencode') instanceof OpenCodeJsoncAdapter);
    assert.ok(getClientAdapter('codex-toml') instanceof CodexTomlAdapter);
});

test('ClientConfigManager applies and reads mcpServers format cleanly without GX_CONFIG_PATH by default', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-cli-test-'));
    const configPath = path.join(tmpDir, 'mcp.json');
    const client = { id: 'test-cursor', name: 'Test Cursor', format: 'mcpServers', path: configPath };
    const launcher = { command: 'node', args: ['run.js'] };

    try {
        const manager = new ClientConfigManager();
        // Decoupled default
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp' });

        let raw = fs.readFileSync(configPath, 'utf8');
        let parsed = JSON.parse(raw);
        assert.ok(parsed.mcpServers);
        assert.ok(parsed.mcpServers.genexus18mcp);
        assert.equal(parsed.mcpServers.genexus18mcp.command, 'node');
        assert.equal(parsed.mcpServers.genexus18mcp.env, undefined);

        // With globalConfig: true
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp', globalConfig: true, force: true });
        raw = fs.readFileSync(configPath, 'utf8');
        parsed = JSON.parse(raw);
        assert.ok(parsed.mcpServers.genexus18mcp.env);
        assert.equal(parsed.mcpServers.genexus18mcp.env.GX_CONFIG_PATH, 'C:\\MyKb\\config.json');

        const readEntry = manager.read(client, 'genexus18mcp');
        assert.ok(readEntry);
        assert.equal(readEntry.command, 'node');

        const wasRemoved = manager.remove(client, { serverName: 'genexus18mcp' });
        assert.equal(wasRemoved, true);

        const afterRemove = JSON.parse(fs.readFileSync(configPath, 'utf8'));
        assert.equal(afterRemove.mcpServers.genexus18mcp, undefined);
    } finally {
        try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { }
    }
});

test('ClientConfigManager applies vscode-servers format cleanly without GX_CONFIG_PATH by default', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-cli-test-'));
    const configPath = path.join(tmpDir, 'mcp.json');
    const client = { id: 'test-vscode', name: 'Test VSCode', format: 'vscode-servers', path: configPath };
    const launcher = { command: 'node', args: ['run.js'] };

    try {
        const manager = new ClientConfigManager();
        // Decoupled default
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp' });

        let raw = fs.readFileSync(configPath, 'utf8');
        let parsed = JSON.parse(raw);
        assert.ok(parsed.servers);
        assert.ok(parsed.servers.genexus18mcp);
        assert.equal(parsed.servers.genexus18mcp.command, 'node');
        assert.equal(parsed.servers.genexus18mcp.env, undefined);

        // With globalConfig: true
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp', globalConfig: true, force: true });
        raw = fs.readFileSync(configPath, 'utf8');
        parsed = JSON.parse(raw);
        assert.ok(parsed.servers.genexus18mcp.env);
        assert.equal(parsed.servers.genexus18mcp.env.GX_CONFIG_PATH, 'C:\\MyKb\\config.json');
    } finally {
        try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { }
    }
});

test('ClientConfigManager applies opencode format cleanly without GX_CONFIG_PATH by default', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-cli-test-'));
    const configPath = path.join(tmpDir, 'opencode.json');
    const client = { id: 'test-opencode', name: 'Test OpenCode', format: 'opencode', path: configPath };
    const launcher = { command: 'node', args: ['run.js'] };

    try {
        const manager = new ClientConfigManager();
        // Decoupled default
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp' });

        let raw = fs.readFileSync(configPath, 'utf8');
        let parsed = JSON.parse(raw);
        assert.ok(parsed.mcp);
        assert.ok(parsed.mcp.genexus18mcp);
        assert.equal(parsed.mcp.genexus18mcp.environment, undefined);

        // With globalConfig: true
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp', globalConfig: true, force: true });
        raw = fs.readFileSync(configPath, 'utf8');
        parsed = JSON.parse(raw);
        assert.ok(parsed.mcp.genexus18mcp.environment);
        assert.equal(parsed.mcp.genexus18mcp.environment.GX_CONFIG_PATH, 'C:\\MyKb\\config.json');
    } finally {
        try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { }
    }
});

test('ClientConfigManager applies codex-toml format cleanly without GX_CONFIG_PATH by default', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-cli-test-'));
    const configPath = path.join(tmpDir, 'config.toml');
    const client = { id: 'test-codex', name: 'Test Codex', format: 'codex-toml', path: configPath };
    const launcher = { command: 'node', args: ['run.js'] };

    try {
        const manager = new ClientConfigManager();
        // Decoupled default
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp' });

        let raw = fs.readFileSync(configPath, 'utf8');
        assert.ok(raw.includes('[mcp_servers.genexus18mcp]'));
        assert.ok(!raw.includes('GX_CONFIG_PATH'));

        // With globalConfig: true
        manager.apply(client, launcher, 'C:\\MyKb\\config.json', { serverName: 'genexus18mcp', globalConfig: true, force: true });
        raw = fs.readFileSync(configPath, 'utf8');
        assert.ok(raw.includes('[mcp_servers.genexus18mcp.env]'));
        assert.ok(raw.includes('GX_CONFIG_PATH'));
    } finally {
        try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { }
    }
});

test('generateNeutralConfig produces valid shape without KB fields', () => {
    const cfg = generateNeutralConfig('C:\\GeneXus18');
    assert.equal(cfg.ConfigSchemaVersion, 2);
    assert.equal(cfg.GatewayMode, 'stdio-isolated');
    assert.equal(cfg.GeneXus.InstallationPath, 'C:\\GeneXus18');
    assert.ok(cfg.GeneXus.WorkerExecutable.endsWith(path.join('worker', 'GxMcp.Worker.exe')));
    assert.equal(cfg.Server.McpStdio, true);
    assert.equal(cfg.Server.TransportMode, undefined);
    assert.equal(cfg.Server.HttpPort, 0);
    assert.equal(cfg.Environment.ResolutionPolicy, 'strict');
    assert.equal(cfg.Environment.KBs, undefined);
    assert.equal(cfg.Environment.KBPath, undefined);
});

test('all client adapters preserve unrelated servers and emit no structural KB overrides', () => {
    const cases = [
        { format: 'mcpServers', file: 'claude.json', root: { mcpServers: { unrelated: { command: 'other' } } } },
        { format: 'mcpServers', file: 'gemini.json', root: { mcpServers: { unrelated: { command: 'other' } } } },
        { format: 'mcpServers', file: 'antigravity.json', root: { mcpServers: { unrelated: { command: 'other' } } } },
        { format: 'vscode-servers', file: 'vscode.json', root: { servers: { unrelated: { type: 'stdio', command: 'other' } } } },
        { format: 'opencode', file: 'opencode.json', root: { $schema: 'custom', mcp: { unrelated: { type: 'local', command: ['other'] } } } },
        { format: 'codex-toml', file: 'config.toml', root: '[mcp_servers.unrelated]\ncommand = "other"\n' }
    ];
    const launcher = { command: 'gateway.exe', args: [] };
    for (const entry of cases) {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-adapter-matrix-'));
        try {
            const configPath = path.join(tmpDir, entry.file);
            fs.writeFileSync(configPath, typeof entry.root === 'string' ? entry.root : JSON.stringify(entry.root, null, 2));
            new ClientConfigManager().apply({ format: entry.format, path: configPath }, launcher, path.join(tmpDir, 'neutral.json'), { serverName: 'genexus18mcp' });
            const raw = fs.readFileSync(configPath, 'utf8');
            assert.equal(/DefaultKb|ActiveKb|KBs/.test(raw), false, entry.format);
            if (entry.format === 'codex-toml') {
                assert.match(raw, /\[mcp_servers\.unrelated\]/);
                assert.match(raw, /\[mcp_servers\.genexus18mcp\]/);
            } else {
                const parsed = JSON.parse(raw);
                const servers = entry.format === 'vscode-servers' ? parsed.servers : entry.format === 'opencode' ? parsed.mcp : parsed.mcpServers;
                const original = entry.format === 'vscode-servers' ? entry.root.servers : entry.format === 'opencode' ? entry.root.mcp : entry.root.mcpServers;
                assert.deepEqual(servers.unrelated, original.unrelated);
                assert.ok(servers.genexus18mcp);
            }
        } finally {
            fs.rmSync(tmpDir, { recursive: true, force: true });
        }
    }
});

test('applyLauncherConfigOrExit creates neutral config outside a KB', () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-outside-kb-'));
    const origEnv = process.env.GX_CONFIG_PATH;
    const origGeneXusHome = process.env.GENEXUS_HOME;
    delete process.env.GX_CONFIG_PATH;
    const fakeGeneXusHome = path.join(tmpDir, 'GeneXus18');
    fs.mkdirSync(fakeGeneXusHome, { recursive: true });
    fs.writeFileSync(path.join(fakeGeneXusHome, 'genexus.exe'), 'test fixture');
    process.env.GENEXUS_HOME = fakeGeneXusHome;

    try {
        const stderr = { write: () => {} };
        const result = applyLauncherConfigOrExit({ cwd: tmpDir, stderr, quiet: true });

        assert.equal(result.ok, true);
        assert.ok(process.env.GX_CONFIG_PATH);
        assert.ok(fs.existsSync(process.env.GX_CONFIG_PATH));
        const content = JSON.parse(fs.readFileSync(process.env.GX_CONFIG_PATH, 'utf8'));
        assert.equal(content.Environment?.KBs, undefined);
        assert.equal(content.Environment?.DefaultKb, undefined);
        assert.equal(content.Environment?.ActiveKb, undefined);
        assert.equal(content.Environment?.KBPath, undefined);
    } finally {
        if (origEnv !== undefined) {
            process.env.GX_CONFIG_PATH = origEnv;
        } else {
            delete process.env.GX_CONFIG_PATH;
        }
        if (origGeneXusHome !== undefined) {
            process.env.GENEXUS_HOME = origGeneXusHome;
        } else {
            delete process.env.GENEXUS_HOME;
        }
        try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { }
    }
});
