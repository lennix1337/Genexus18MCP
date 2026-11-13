#!/usr/bin/env node
'use strict';

// Measures the launcher's own overhead: `node cli/run.js` start -> stub Gateway
// spawned -> first `initialize` response. Nothing real is touched: the script
// builds a throw-away installed-package layout (cli/ + publish/ with a stub
// Gateway), a temp home and a temp runtime root, and removes them afterwards.
//
//   node scripts/bench-launcher-startup.js [--runs 15] [--scenario warm|old-in-use|cold|all] [--repo <checkout>] [--json]
//
// Scenarios (all measure a launch with the staged runtime already present
// except `cold`):
//   warm        staged runtime present, nothing else under the runtime root
//   old-in-use  warm + three older runtime dirs, the oldest still used by a
//               running (stub) Gateway: the case that made runtime GC probe
//               processes through powershell on every launch
//   cold        first launch of a version: stages (copies) the publish tree
//
// Phase breakdown comes from GENEXUS_MCP_TRACE_STARTUP=1 (cli/lib/startup-timing.js);
// it is read from the launcher's stderr, so it is absent on code without that hook.
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

let repoRoot = path.resolve(__dirname, '..'); // --repo points the package layout at another checkout (e.g. a baseline export)
const stubPreload = path.join(__dirname, 'bench-stub-gateway.js');

function parseArgs(argv) {
    const options = { runs: 15, scenario: 'all', json: false, coldRuns: 3 };
    for (let i = 0; i < argv.length; i += 1) {
        if (argv[i] === '--runs') options.runs = Number(argv[++i]);
        else if (argv[i] === '--cold-runs') options.coldRuns = Number(argv[++i]);
        else if (argv[i] === '--scenario') options.scenario = argv[++i];
        else if (argv[i] === '--repo') repoRoot = path.resolve(argv[++i]);
        else if (argv[i] === '--json') options.json = true;
        else throw new Error(`Unknown argument: ${argv[i]}`);
    }
    if (!Number.isInteger(options.runs) || options.runs < 1) throw new Error('--runs must be a positive integer');
    return options;
}

function copyDir(from, to, skip) {
    fs.cpSync(from, to, { recursive: true, filter: (src) => !skip(path.basename(src)) });
}

function buildPackage(root) {
    const pkgRoot = path.join(root, 'pkg');
    copyDir(path.join(repoRoot, 'cli'), path.join(pkgRoot, 'cli'), (name) => name.endsWith('.test.js') || name === 'test-support');
    fs.mkdirSync(path.join(pkgRoot, 'config'), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, 'package.json'), path.join(pkgRoot, 'package.json'));
    fs.copyFileSync(path.join(repoRoot, 'config', 'gx-versions.json'), path.join(pkgRoot, 'config', 'gx-versions.json'));
    const publish = path.join(pkgRoot, 'publish');
    fs.mkdirSync(publish, { recursive: true });
    fs.copyFileSync(process.execPath, path.join(publish, 'GxMcp.Gateway.exe'));
    const size = fs.statSync(path.join(publish, 'GxMcp.Gateway.exe')).size;
    // Same shape the real publish tree ships: an artifact list verified on staging.
    fs.writeFileSync(path.join(publish, 'gxmcp-manifest.json'), JSON.stringify({ artifacts: [{ path: 'GxMcp.Gateway.exe', size }] }));
    return pkgRoot;
}

function buildEnvironment(root) {
    const home = path.join(root, 'home');
    fs.mkdirSync(path.join(home, '.genexus-mcp'), { recursive: true });
    const gxDir = path.join(root, 'GeneXus');
    fs.mkdirSync(gxDir, { recursive: true });
    fs.writeFileSync(path.join(home, '.genexus-mcp', 'config.json'), JSON.stringify({ GeneXus: { InstallationPath: gxDir } }));
    const cwd = path.join(root, 'cwd');
    fs.mkdirSync(cwd, { recursive: true });
    const env = {
        ...process.env,
        USERPROFILE: home,
        HOME: home,
        LOCALAPPDATA: path.join(root, 'localappdata'),
        GENEXUS_MCP_RUNTIME_DIR: path.join(root, 'runtime'),
        GENEXUS_MCP_NO_UPDATE_CHECK: '1',
        GENEXUS_MCP_TRACE_STARTUP: '1',
        NODE_OPTIONS: `--require ${stubPreload}`
    };
    for (const key of ['GX_CONFIG_PATH', 'GENEXUS_MCP_GATEWAY_EXE', 'GENEXUS_MCP_NO_STAGING', 'GENEXUS_MCP_FORCE_STAGING', 'GENEXUS_HOME']) delete env[key];
    return { env, cwd, runtimeRoot: env.GENEXUS_MCP_RUNTIME_DIR };
}

// One launch: returns timestamps (ms, relative to just before the launcher was spawned).
function launchOnce(pkgRoot, { env, cwd }) {
    return new Promise((resolve, reject) => {
        const t0 = Date.now();
        const child = spawn(process.execPath, [path.join(pkgRoot, 'cli', 'run.js')], { cwd, env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
        const result = { stubUp: null, stubInitialize: null, response: null, phases: null, stdoutLines: [] };
        let stdout = '';
        let stderr = '';
        const timer = setTimeout(() => { child.kill('SIGKILL'); reject(new Error(`launcher timed out; stderr: ${stderr}`)); }, 60000);
        child.stdout.setEncoding('utf8');
        child.stdout.on('data', (chunk) => {
            stdout += chunk;
            if (result.response === null && stdout.includes('\n')) {
                result.response = Date.now() - t0;
                child.stdin.end();
            }
        });
        child.stderr.setEncoding('utf8');
        child.stderr.on('data', (chunk) => { stderr += chunk; });
        child.on('error', (err) => { clearTimeout(timer); reject(err); });
        child.on('close', (code) => {
            clearTimeout(timer);
            for (const line of stderr.split(/\r?\n/)) {
                let match = /^STUB_UP (\d+)$/.exec(line);
                if (match) result.stubUp = Number(match[1]) - t0;
                match = /^STUB_INITIALIZE (\d+)$/.exec(line);
                if (match) result.stubInitialize = Number(match[1]) - t0;
                match = /startup-timing (\{.*\})$/.exec(line);
                if (match) result.phases = JSON.parse(match[1]);
            }
            result.stdoutLines = stdout.split(/\r?\n/).filter(Boolean);
            result.exitCode = code;
            result.stderr = stderr;
            resolve(result);
        });
        // The client sends `initialize` right away; the OS pipe holds it until the Gateway reads it.
        child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'bench', version: '0' } } })}\n`);
    });
}

function median(values) {
    const sorted = [...values].sort((a, b) => a - b);
    const mid = sorted.length >> 1;
    return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

function summarize(values) {
    const med = median(values);
    const mad = median(values.map((v) => Math.abs(v - med)));
    return { n: values.length, median: Math.round(med), min: Math.round(Math.min(...values)), max: Math.round(Math.max(...values)), mad: Math.round(mad) };
}

function assertHygiene(result) {
    if (result.exitCode !== 0) throw new Error(`launcher exited ${result.exitCode}: ${result.stderr}`);
    if (result.stdoutLines.length !== 1 || !result.stdoutLines[0].includes('"bench-stub"')) {
        throw new Error(`launcher stdout must carry only the Gateway response, got: ${JSON.stringify(result.stdoutLines)}`);
    }
}

async function runScenario(name, options) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-launcher-bench-'));
    const keepAlive = [];
    try {
        const pkgRoot = buildPackage(root);
        const ctx = buildEnvironment(root);
        const samples = [];
        if (name === 'cold') {
            for (let i = 0; i < options.coldRuns; i += 1) {
                fs.rmSync(ctx.runtimeRoot, { recursive: true, force: true });
                const result = await launchOnce(pkgRoot, ctx);
                assertHygiene(result);
                samples.push(result);
            }
        } else {
            const first = await launchOnce(pkgRoot, ctx); // stages the runtime
            assertHygiene(first);
            if (name === 'old-in-use') {
                const staged = fs.readdirSync(ctx.runtimeRoot).filter((entry) => !entry.startsWith('.'));
                if (staged.length !== 1) throw new Error(`expected one staged runtime, found ${staged.join(', ')}`);
                const old = ['0.0.1-aaaaaaaa', '0.0.2-bbbbbbbb', '0.0.3-cccccccc'];
                for (const [index, dir] of old.entries()) {
                    const full = path.join(ctx.runtimeRoot, dir);
                    fs.mkdirSync(full, { recursive: true });
                    if (index === 0) fs.copyFileSync(process.execPath, path.join(full, 'GxMcp.Gateway.exe'));
                    const when = new Date(Date.now() - (10 - index) * 86400000);
                    fs.utimesSync(full, when, when);
                }
                // A Gateway of the oldest runtime that is still running (another MCP client's session).
                const running = spawn(path.join(ctx.runtimeRoot, old[0], 'GxMcp.Gateway.exe'), [], { env: ctx.env, stdio: ['pipe', 'ignore', 'ignore'], windowsHide: true });
                keepAlive.push(running);
                await new Promise((resolve) => setTimeout(resolve, 500));
            }
            for (let i = 0; i < options.runs; i += 1) {
                const result = await launchOnce(pkgRoot, ctx);
                assertHygiene(result);
                samples.push(result);
            }
        }
        const metrics = {
            stubUpMs: summarize(samples.map((s) => s.stubUp)),
            initializeResponseMs: summarize(samples.map((s) => s.response))
        };
        const phased = samples.filter((s) => s.phases);
        if (phased.length === samples.length) {
            const phaseNames = Object.keys(phased[0].phases);
            metrics.phasesMs = Object.fromEntries(phaseNames.map((phase) => [phase, summarize(phased.map((s) => s.phases[phase]))]));
            // Launcher-only time: from the first line of run.js to the Gateway spawn, without Node's own boot.
            metrics.phasesMs['launcher-own (start->spawned)'] = summarize(phased.map((s) => s.phases['gateway:spawned'] - s.phases['run.js:start']));
        }
        return { scenario: name, runs: samples.length, ...metrics };
    } finally {
        for (const proc of keepAlive) { try { proc.kill('SIGKILL'); } catch { /* already gone */ } }
        await new Promise((resolve) => setTimeout(resolve, 300));
        try { fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch { /* temp dir; best effort */ }
    }
}

async function main() {
    const options = parseArgs(process.argv.slice(2));
    const names = options.scenario === 'all' ? ['warm', 'old-in-use', 'cold'] : [options.scenario];
    const results = [];
    for (const name of names) results.push(await runScenario(name, options));
    if (options.json) {
        process.stdout.write(`${JSON.stringify({ node: process.version, platform: process.platform, results }, null, 2)}\n`);
        return;
    }
    for (const r of results) {
        const fmt = (s) => `median ${s.median} ms (min ${s.min}, max ${s.max}, MAD ${s.mad}, n=${s.n})`;
        process.stdout.write(`${r.scenario}\n  stub gateway up:      ${fmt(r.stubUpMs)}\n  initialize response:  ${fmt(r.initializeResponseMs)}\n`);
        for (const [phase, s] of Object.entries(r.phasesMs || {})) process.stdout.write(`    ${phase.padEnd(24)} ${fmt(s)}\n`);
    }
}

main().catch((err) => { process.stderr.write(`${err.stack || err}\n`); process.exit(1); });
