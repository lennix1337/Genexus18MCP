const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const os = require('node:os');
const fs = require('node:fs');
const { renderOutput } = require('./lib/output');
const { compareSemver, detectInstallMethod, upgradePlanFor } = require('./lib/update-check');
const { detectClientInstalled, readJsonFileSafe, getLauncher } = require('./lib/config');

const cliPath = path.join(__dirname, 'run.js');
const testGxPath = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-gx-'));
fs.writeFileSync(path.join(testGxPath, 'genexus.exe'), '');
const testGatewayEnv = { GENEXUS_MCP_GATEWAY_EXE: process.execPath };
test.after(() => fs.rmSync(testGxPath, { recursive: true, force: true }));

function runCli(args, opts = {}) {
    return spawnSync(process.execPath, [cliPath, ...args], {
        encoding: 'utf8',
        cwd: opts.cwd || process.cwd(),
        env: { ...process.env, ...(opts.env || {}) }
    });
}

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

    const second = runCli(args, { env: testGatewayEnv });
    assert.equal(second.status, 0);
    const secondParsed = JSON.parse(second.stdout);
    assert.equal(secondParsed.ok.noOp, true);

    const cfgPath = path.join(kbDir, 'config.json');
    assert.equal(fs.existsSync(cfgPath), true);

    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('whoami without config returns disconnected state', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-test-'));
    const res = runCli(['whoami', '--format', 'json'], { cwd: tempRoot });
    assert.equal(res.status, 0);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.ok.connected, false);
    assert.ok(parsed.ok.reason, 'should explain why not connected');
    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('--fields validation returns usage error for invalid doctor field', () => {
    const result = runCli(['doctor', '--fields', 'id,unknown', '--format', 'json']);
    assert.equal(result.status, 2);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.error.code, 'usage_error');
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
    assert.ok(['pass', 'warn', 'fail'].includes(smoke.status));
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('detectClientInstalled treats an existing config file as installed', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-detect-'));
    const cfg = path.join(tempRoot, 'settings.json');
    fs.writeFileSync(cfg, '{}');
    const client = { name: 'Gemini CLI', path: cfg, installMarkers: [] };

    const det = detectClientInstalled(client);
    assert.equal(det.installed, true);
    assert.equal(det.hasConfig, true);

    fs.rmSync(tempRoot, { recursive: true, force: true });
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
});

test('clients list reports OpenCode Desktop as manual with exact setup fields', () => {
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
        assert.equal(row.writeSupported, false);
        assert.equal(row.registrationMode, 'manual');
        assert.equal(row.manualSetup.transport, 'local');
        assert.deepEqual(row.manualSetup.args, ['-y', 'genexus-mcp@latest']);
        assert.equal(row.manualSetup.environment.GX_CONFIG_PATH, '<config.json path printed by genexus-mcp init>');
        assert.ok(row.manualSetup.steps.some((step) => step.includes('genexus_whoami')));
        assert.match(row.note, /does not write/i);
    } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
    }
});

test('clients add reports OpenCode Desktop manual skip without mutating its app-managed file', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-desktop-add-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cfgPath = path.join(tempRoot, 'config.json');
        const desktopDir = path.join(env.APPDATA, 'ai.opencode.desktop');
        const desktopConfig = path.join(desktopDir, 'mcp.json');
        const original = { unrelated: { keep: true } };
        fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));
        fs.mkdirSync(desktopDir, { recursive: true });
        fs.writeFileSync(desktopConfig, JSON.stringify(original, null, 2));

        const result = runCli(['clients', 'add', '--clients', 'opencode-desktop', '--format', 'json'], {
            env: { ...env, GX_CONFIG_PATH: cfgPath }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        assert.deepEqual(parsed.ok.patchedClients, []);
        const skipped = parsed.meta.skippedClients.find((entry) => entry.client === 'OpenCode Desktop');
        assert.ok(skipped && /Settings > MCP|manual setup/i.test(skipped.reason));
        assert.equal(skipped.registrationMode, 'manual');
        assert.equal(skipped.installed, true);
        assert.equal(skipped.manualSetup.environment.GX_CONFIG_PATH, cfgPath);
        assert.ok(parsed.help.some((entry) => /manual setup/i.test(entry)));
        assert.deepEqual(JSON.parse(fs.readFileSync(desktopConfig, 'utf8')), original);
    } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
    }
});

test('clients add always reports undetected OpenCode Desktop manual setup', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-opencode-desktop-undetected-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const cfgPath = path.join(tempRoot, 'config.json');
        fs.writeFileSync(cfgPath, JSON.stringify({ Environment: { KBPath: tempRoot } }));

        const result = runCli(['clients', 'add', '--clients', 'opencode-desktop', '--format', 'json'], {
            env: {
                ...env,
                GX_CONFIG_PATH: cfgPath,
                GENEXUS_MCP_GATEWAY_EXE: path.join(tempRoot, 'missing', 'GxMcp.Gateway.exe')
            }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        assert.deepEqual(parsed.ok.patchedClients, []);
        const skipped = parsed.meta.skippedClients.find((entry) => entry.client === 'OpenCode Desktop');
        assert.ok(skipped, 'undetected detect-only client should still be actionable');
        assert.equal(skipped.installed, false);
        assert.equal(skipped.manualSetup.environment.GX_CONFIG_PATH, cfgPath);
        assert.equal(skipped.manualSetup.command, process.platform === 'win32' ? 'npx.cmd' : 'npx');
    } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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
    assert.deepEqual(written.mcp.genexus18mcp.environment, { GX_CONFIG_PATH: cfgPath });

    const listed = runCli(['clients', '--format', 'json'], { env });
    assert.equal(listed.status, 0);
    const row = JSON.parse(listed.stdout).ok.clients.find((client) => client.id === 'opencode');
    assert.ok(row && row.registered, 'clients list should read the direct OpenCode entry');

    fs.rmSync(tempRoot, { recursive: true, force: true });
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
    assert.deepEqual(written.mcp.servers.genexus18mcp.environment, { GX_CONFIG_PATH: cfgPath });

    const listed = runCli(['clients', '--format', 'json'], { env });
    assert.equal(listed.status, 0);
    const row = JSON.parse(listed.stdout).ok.clients.find((client) => client.id === 'opencode');
    assert.ok(row && row.registered, 'clients list should read the nested OpenCode entry');

    fs.rmSync(tempRoot, { recursive: true, force: true });
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
            assert.deepEqual(entry.environment, { GX_CONFIG_PATH: path.join(kbDir, 'config.json') });
            assert.ok(label === 'nested' ? written.mcp.servers.other : written.mcp.other);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('clients list marks an Antigravity launcher from an old package cache as stale', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-antigravity-stale-'));
    try {
        const env = sandboxHomeEnv(tempRoot);
        const currentGateway = path.join(tempRoot, 'current', 'GxMcp.Gateway.exe');
        const oldGateway = path.join(tempRoot, 'old-cache', 'GxMcp.Gateway.exe');
        fs.mkdirSync(path.dirname(currentGateway), { recursive: true });
        fs.mkdirSync(path.dirname(oldGateway), { recursive: true });
        fs.writeFileSync(currentGateway, 'current');
        fs.writeFileSync(oldGateway, 'old');

        const antigravityCfg = path.join(tempRoot, '.gemini', 'antigravity', 'mcp_config.json');
        fs.mkdirSync(path.dirname(antigravityCfg), { recursive: true });
        fs.writeFileSync(antigravityCfg, JSON.stringify({
            mcpServers: { genexus18mcp: { command: oldGateway, args: [] } }
        }, null, 2));

        const result = runCli(['clients', '--format', 'json'], {
            env: { ...env, GENEXUS_MCP_GATEWAY_EXE: currentGateway }
        });
        assert.equal(result.status, 0);
        const parsed = JSON.parse(result.stdout);
        const antigravity = parsed.ok.clients.find((client) => client.id === 'antigravity');
        assert.equal(antigravity.commandStale, true);
        assert.match(antigravity.commandStaleReason, /different package gateway/);
    } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
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
    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('readJsonFileSafe strips a genuine trailing comma', () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'genexus-mcp-jsonc3-'));
    const f = path.join(tempRoot, 'mcp.json');
    fs.writeFileSync(f, '{\n  // c\n  "a": [1, 2, 3,],\n  "b": { "x": 1, },\n}\n');
    const parsed = readJsonFileSafe(f);
    assert.deepEqual(parsed.a, [1, 2, 3]);
    assert.deepEqual(parsed.b, { x: 1 });
    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('compareSemver detects newer, older, equal versions', () => {
    assert.equal(compareSemver('1.3.1', '1.3.0'), 1);
    assert.equal(compareSemver('v1.4.0', '1.3.9'), 1);
    assert.equal(compareSemver('1.3.0', '1.3.0'), 0);
    assert.equal(compareSemver('1.2.9', '1.3.0'), -1);
    assert.equal(compareSemver('garbage', '1.0.0'), 0);
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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
        fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
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

    fs.rmSync(tempRoot, { recursive: true, force: true });
});

test('clients add rejects invalid --server-name characters with usage error', () => {
    const res = runCli(['clients', 'add', '--clients', 'cursor', '--server-name', 'bad name with spaces!', '--format', 'json']);
    assert.equal(res.status, 2);
    const parsed = JSON.parse(res.stdout);
    assert.equal(parsed.error.code, 'usage_error');
    assert.match(parsed.error.message, /alphanumeric/);
});
