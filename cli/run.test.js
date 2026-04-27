const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const os = require('node:os');
const fs = require('node:fs');
const { renderOutput } = require('./lib/output');
const { compareSemver } = require('./lib/update-check');

const cliPath = path.join(__dirname, 'run.js');

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
        'C:\\Program Files (x86)\\GeneXus\\GeneXus18',
        '--format',
        'json'
    ];

    const first = runCli(args);
    assert.equal(first.status, 0);
    const firstParsed = JSON.parse(first.stdout);
    assert.equal(firstParsed.ok.noOp, false);

    const second = runCli(args);
    assert.equal(second.status, 0);
    const secondParsed = JSON.parse(second.stdout);
    assert.equal(secondParsed.ok.noOp, true);

    const cfgPath = path.join(kbDir, 'config.json');
    assert.equal(fs.existsSync(cfgPath), true);

    fs.rmSync(tempRoot, { recursive: true, force: true });
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
    const result = runCli(['--quiet'], {
        env: {
            GX_CONFIG_PATH: '',
            GENEXUS_MCP_GATEWAY_EXE: 'C:\\missing\\nope.exe'
        }
    });

    assert.equal(result.status, 1);
    assert.equal(result.stderr.trim(), '');
});

test('update --help returns usage entry', () => {
    const result = runCli(['update', '--help', '--format', 'json']);
    assert.equal(result.status, 0);

    const parsed = JSON.parse(result.stdout);
    assert.equal(parsed.meta.command, 'help');
    assert.equal(parsed.ok.command, 'update');
    assert.ok(parsed.ok.usage.includes('genexus-mcp update'));
});

test('compareSemver detects newer, older, equal versions', () => {
    assert.equal(compareSemver('1.3.1', '1.3.0'), 1);
    assert.equal(compareSemver('v1.4.0', '1.3.9'), 1);
    assert.equal(compareSemver('1.3.0', '1.3.0'), 0);
    assert.equal(compareSemver('1.2.9', '1.3.0'), -1);
    assert.equal(compareSemver('garbage', '1.0.0'), 0);
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
