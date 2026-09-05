const { spawn } = require('child_process');
const path = require('path');
const readline = require('readline');
const fs = require('fs');
const {
    getGatewayExePath,
    getToolDefinitionsPath,
    resolveConfigPathNoMutate,
    readJsonFileSafe,
    directoryLooksLikeKnowledgeBase,
    createConfigFile,
    patchClientConfig,
    unpatchClientConfig,
    getClientConfigTargets,
    detectClientInstalled,
    clientsStatus,
    filterClientTargets,
    listSupportedClientIds,
    getLocalAppDataCacheDir,
    readGeneXusVersionFromInstall,
    discoverGeneXusInstallation,
    discoverKnowledgeBase,
    discoverKnowledgeBases,
    readKbCatalog,
    addKbToConfig,
    removeKbFromConfig,
    switchActiveKb,
    isPathLikelyAppLockerBlocked,
    normalizeExePath,
    readClientCommandEntry,
    DEFAULT_MCP_SERVER_NAME
} = require('../lib/config');
const { getStdioErrorLogPath } = require('../lib/stdio-diagnostics');

function resolveClientIds(options) {
    if (!options || !options.clients) return null;
    return String(options.clients)
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
}

function validateClientIds(ids) {
    if (!ids) return { ok: true };
    const supported = new Set(listSupportedClientIds());
    const invalid = ids.filter((id) => !supported.has(id));
    if (invalid.length === 0) return { ok: true };
    return {
        ok: false,
        message: `Unknown client id(s): ${invalid.join(', ')}. Supported: ${[...supported].join(', ')}.`
    };
}

function parseFieldSelection(raw) {
    if (!raw) return null;
    return raw.split(',').map((v) => v.trim()).filter(Boolean);
}

function sanitizeOperationalMessage(message, fallback = 'Operation failed.') {
    const raw = typeof message === 'string' ? message : '';
    const singleLine = raw.replace(/\r?\n/g, ' ').trim();
    if (!singleLine) return fallback;
    if (singleLine.length > 220) return `${singleLine.slice(0, 217)}...`;
    return singleLine;
}

function validateFieldSelection(raw, allowed, commandName, ctx) {
    const selected = parseFieldSelection(raw);

    if (!raw) {
        return { selectedFields: null };
    }

    if (!selected || selected.length === 0) {
        return {
            errorResult: {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope(`--fields for ${commandName} cannot be empty. Allowed: ${allowed.join(', ')}.`, ctx.EXIT_CODES.USAGE)
            }
        };
    }

    const invalid = selected.filter((field) => !allowed.includes(field));
    if (invalid.length > 0) {
        return {
            errorResult: {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope(
                    `Invalid --fields for ${commandName}: ${invalid.join(', ')}. Allowed: ${allowed.join(', ')}.`,
                    ctx.EXIT_CODES.USAGE
                )
            }
        };
    }

    return { selectedFields: selected };
}

function pickFields(obj, selectedFields) {
    if (!selectedFields || selectedFields.length === 0) return obj;
    const out = {};
    for (const f of selectedFields) {
        if (Object.prototype.hasOwnProperty.call(obj, f)) {
            out[f] = obj[f];
        }
    }
    return out;
}

function resolveToolCategory(name) {
    const n = (name || '').toLowerCase();
    if (n.includes('read') || n.includes('list') || n.includes('query') || n.includes('inspect')) return 'read';
    if (n.includes('edit') || n.includes('write') || n.includes('create') || n.includes('refactor') || n.includes('add_variable')) return 'write';
    if (n.includes('analyze') || n.includes('summarize') || n.includes('explain') || n.includes('doc')) return 'analysis';
    if (n.includes('lifecycle') || n.includes('test') || n.includes('format') || n.includes('build')) return 'lifecycle';
    return 'other';
}

function usageEnvelope(message, exitCode) {
    return {
        error: { code: 'usage_error', message },
        help: [
            'Run `genexus-mcp help` for command reference.',
            'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` for non-interactive setup.'
        ],
        meta: { exitCode }
    };
}

function operationalErrorEnvelope(message, exitCode, help = []) {
    return {
        error: { code: 'operation_error', message: sanitizeOperationalMessage(message) },
        help,
        meta: { exitCode }
    };
}

function buildStatusData(cwd) {
    const configPath = resolveConfigPathNoMutate(cwd);
    const gatewayExePath = getGatewayExePath();
    const gatewayExeFound = fs.existsSync(gatewayExePath);
    const configFound = !!configPath;

    let kbLooksValid = false;
    let kbPath = null;
    let gxPath = null;
    let configSource = null;

    if (process.env.GX_CONFIG_PATH && fs.existsSync(process.env.GX_CONFIG_PATH)) {
        configSource = 'env';
    } else if (configPath) {
        configSource = 'cwd';
    }

    if (configPath) {
        const cfg = readJsonFileSafe(configPath);
        if (cfg) {
            kbPath = cfg.Environment && cfg.Environment.KBPath ? cfg.Environment.KBPath : null;
            gxPath = cfg.GeneXus && cfg.GeneXus.InstallationPath ? cfg.GeneXus.InstallationPath : null;
            if (kbPath) kbLooksValid = directoryLooksLikeKnowledgeBase(kbPath);
        }
    }

    const ready = configFound && gatewayExeFound;
    return { ready, configFound, gatewayExeFound, kbLooksValid, configPath, gatewayExePath, kbPath, gxPath, configSource };
}

async function spawnGatewayProbe({ env = process.env, spawnHoldMs, timeoutMs, label, successDetail }) {
    const gatewayExePath = getGatewayExePath();

    return await new Promise((resolve) => {
        let done = false;
        const finish = (result) => {
            if (done) return;
            done = true;
            resolve(result);
        };

        try {
            const child = spawn(gatewayExePath, ['--axi-spawn-probe'], {
                stdio: 'ignore',
                windowsHide: true,
                env
            });

            child.once('error', (err) => {
                const code = err && (err.code || err.errno);
                const accessDenied = code === 'EACCES' || code === 'EPERM' || /access is denied|access denied|acesso negado/i.test(err.message || '');
                if (accessDenied) {
                    const zone = isPathLikelyAppLockerBlocked(gatewayExePath);
                    const hint = zone
                        ? ` Gateway exe is under %${zone}% — likely blocked by AppLocker/SRP. Reinstall via scripts/install.ps1 to a whitelisted path.`
                        : ' Access denied. AppLocker/SRP or AV may be blocking execution. Move the exe to a whitelisted path (see scripts/install.ps1).';
                    finish({ status: 'fail', detail: `${label} failed: ${err.message}.${hint}` });
                } else {
                    finish({ status: 'fail', detail: `${label} failed: ${err.message}` });
                }
            });

            child.once('spawn', () => {
                setTimeout(() => {
                    try { child.kill(); } catch { }
                    finish({ status: 'pass', detail: successDetail });
                }, spawnHoldMs);
            });

            setTimeout(() => {
                if (!done) {
                    try { child.kill(); } catch { }
                    finish({ status: 'warn', detail: `${label} timed out; process was force-stopped.` });
                }
            }, timeoutMs);
        } catch (err) {
            finish({ status: 'fail', detail: `${label} threw: ${err.message}` });
        }
    });
}

async function probeGatewaySpawn() {
    return spawnGatewayProbe({
        spawnHoldMs: 180,
        timeoutMs: 900,
        label: 'Spawn probe',
        successDetail: 'Gateway process can be spawned (probe launched and terminated).'
    });
}

function resolveMcpBaseUrl(cwd) {
    const configPath = resolveConfigPathNoMutate(cwd);
    const fallback = 'http://127.0.0.1:5000/mcp';
    if (!configPath) return fallback;

    const cfg = readJsonFileSafe(configPath);
    if (!cfg || typeof cfg !== 'object') return fallback;

    const server = cfg.Server && typeof cfg.Server === 'object' ? cfg.Server : {};
    const host = server.BindAddress && typeof server.BindAddress === 'string'
        ? server.BindAddress
        : '127.0.0.1';
    const parsedPort = Number.parseInt(String(server.HttpPort || ''), 10);
    const port = Number.isFinite(parsedPort) && parsedPort > 0 ? parsedPort : 5000;
    return `http://${host}:${port}/mcp`;
}

async function runMcpSmokeProbe(cwd) {
    const scriptPath = path.join(__dirname, '..', '..', 'scripts', 'mcp_smoke.ps1');
    if (!fs.existsSync(scriptPath)) {
        return { status: 'warn', detail: 'MCP smoke script is missing.' };
    }

    const baseUrl = resolveMcpBaseUrl(cwd);
    const shell = process.platform === 'win32' ? 'powershell' : 'pwsh';
    const args = process.platform === 'win32'
        ? ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath, '-BaseUrl', baseUrl]
        : ['-NoProfile', '-File', scriptPath, '-BaseUrl', baseUrl];

    return await new Promise((resolve) => {
        let stdout = '';
        let stderr = '';
        let resolved = false;

        const finish = (payload) => {
            if (resolved) return;
            resolved = true;
            resolve(payload);
        };

        try {
            const child = spawn(shell, args, {
                cwd,
                stdio: ['ignore', 'pipe', 'pipe'],
                windowsHide: true,
                env: process.env
            });

            child.stdout.on('data', (chunk) => {
                stdout += chunk.toString();
            });

            child.stderr.on('data', (chunk) => {
                stderr += chunk.toString();
            });

            child.on('error', (err) => {
                finish({
                    status: 'warn',
                    detail: sanitizeOperationalMessage(`Unable to run MCP smoke probe: ${err.message}.`)
                });
            });

            child.on('exit', (code) => {
                if (code === 0) {
                    finish({ status: 'pass', detail: `MCP smoke succeeded at ${baseUrl}.` });
                    return;
                }

                const preview = sanitizeOperationalMessage((stderr || stdout || '').trim(), '');
                finish({
                    status: 'fail',
                    detail: preview
                        ? `MCP smoke failed at ${baseUrl}: ${preview}`
                        : `MCP smoke failed at ${baseUrl}.`
                });
            });

            setTimeout(() => {
                if (resolved) return;
                try {
                    child.kill();
                } catch {
                }
                finish({ status: 'warn', detail: `MCP smoke timed out at ${baseUrl}.` });
            }, 30000);
        } catch (err) {
            finish({
                status: 'warn',
                detail: sanitizeOperationalMessage(`MCP smoke launch failed: ${err.message}.`)
            });
        }
    });
}

async function handleStatus(options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const riskyZone = isPathLikelyAppLockerBlocked(data.gatewayExePath);

    const ok = options.full
        ? {
            ready: data.ready,
            configFound: data.configFound,
            gatewayExeFound: data.gatewayExeFound,
            kbLooksValid: data.kbLooksValid,
            pathSafetyWarn: !!riskyZone,
            configSource: data.configSource,
            configPath: data.configPath,
            gatewayExePath: data.gatewayExePath,
            kbPath: data.kbPath,
            gxPath: data.gxPath
        }
        : {
            ready: data.ready,
            configFound: data.configFound,
            gatewayExeFound: data.gatewayExeFound,
            kbLooksValid: data.kbLooksValid,
            pathSafetyWarn: !!riskyZone
        };

    const help = data.ready
        ? ['Run `genexus-mcp tools list --limit 10` to inspect available MCP tools.']
        : [
            'Run `genexus-mcp doctor` for expanded checks.',
            'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to create config.'
        ];
    if (riskyZone) {
        help.unshift(`Gateway exe is under %${riskyZone}% — likely AppLocker-blocked on corporate Windows. Reinstall to a whitelisted path via scripts/install.ps1.`);
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: { ok, help }
    };
}

function buildClientExeCrossCheck(packageExePath) {
    const packageNorm = normalizeExePath(packageExePath);
    const targets = filterClientTargets(getClientConfigTargets(), { platform: process.platform });

    const mismatches = [];
    const matches = [];
    const errors = [];
    let inspected = 0;

    for (const client of targets) {
        if (!fs.existsSync(client.path)) continue;
        let entry;
        try {
            entry = readClientCommandEntry(client);
        } catch (err) {
            errors.push(`${client.name}: ${err.message || 'read failed'}`);
            continue;
        }
        if (!entry || !entry.command) continue;
        inspected += 1;

        const cmd = entry.command;
        const lowerCmd = cmd.toLowerCase();

        // Skip launchers that always resolve via npm at runtime (npx, node CLI shim).
        if (/(^|[\\/])(npx|npx\.cmd|node|node\.exe)$/i.test(lowerCmd) || /[\\/]genexus-mcp(\.cmd)?$/i.test(lowerCmd)) {
            continue;
        }

        // Only compare when the configured command points to an .exe.
        if (!/\.exe$/i.test(lowerCmd)) continue;

        const configuredNorm = normalizeExePath(cmd);
        if (configuredNorm === packageNorm) {
            matches.push(client.name);
        } else {
            const configuredExists = fs.existsSync(cmd);
            mismatches.push({
                client: client.name,
                configured: cmd,
                exists: configuredExists
            });
        }
    }

    if (inspected === 0) {
        return { status: 'warn', detail: 'No AI client config with a direct gateway exe found (or all clients use npx). Run `genexus-mcp init --write-clients` if you want to register one.' };
    }

    if (mismatches.length === 0) {
        return {
            status: 'pass',
            detail: `All inspected client configs (${matches.join(', ')}) point at the npm-package gateway exe.`
        };
    }

    const detailParts = mismatches.map((m) => `${m.client} -> ${m.configured}${m.exists ? '' : ' (missing)'}`);
    return {
        status: 'warn',
        detail: `Client(s) reference a gateway exe that is NOT this npm package's bundled exe. \`npm install -g genexus-mcp@latest\` will NOT update those instances. Bundled: ${packageExePath}. Mismatches: ${detailParts.join('; ')}. Re-run scripts/install.ps1 (or genexus-mcp init --write-clients) to resync.`
    };
}

// v2.6.6 — probe Stream B's per-KB worker lock. The worker writes
// `.worker-<hash>.lock` into TempPath (or the configured lockDir); the file
// holds the live PID. We list every matching file, classify it as live /
// stale, and surface stale ones so the user can clean them up.
function buildWorkerSingleInstanceLockCheck() {
    const tempDir = require('os').tmpdir();
    let entries;
    try {
        entries = fs.readdirSync(tempDir).filter((n) => /^\.worker-[0-9a-f]{16}\.lock$/i.test(n));
    } catch (err) {
        return { status: 'warn', detail: `Unable to scan ${tempDir} for worker lock files: ${err.message || err}` };
    }

    if (!entries || entries.length === 0) {
        return { status: 'pass', detail: 'No worker single-instance lock files found (no worker currently registered).' };
    }

    const live = [];
    const stale = [];
    for (const name of entries) {
        const full = path.join(tempDir, name);
        let pid = null;
        try {
            const txt = fs.readFileSync(full, 'utf8').trim();
            const parsed = Number.parseInt(txt, 10);
            if (Number.isFinite(parsed) && parsed > 0) pid = parsed;
        } catch {
            // Lock file holds a delete-on-close handle on the owning worker —
            // if reading fails with EBUSY/EACCES it's almost certainly live.
            live.push({ name, pid: null, note: 'file is locked (owner alive)' });
            continue;
        }

        if (pid === null) {
            stale.push({ name, pid: null, note: 'pid file unreadable / empty' });
            continue;
        }

        let alive = false;
        try {
            process.kill(pid, 0);
            alive = true;
        } catch (err) {
            alive = err.code === 'EPERM'; // EPERM => process exists but we can't signal it
        }

        if (alive) live.push({ name, pid, note: null });
        else stale.push({ name, pid, note: 'pid not running' });
    }

    if (stale.length === 0) {
        const desc = live.map((l) => l.pid ? `pid=${l.pid}` : (l.note || 'locked')).join(', ');
        return {
            status: 'pass',
            detail: `Worker single-instance lock healthy (${live.length} live owner${live.length === 1 ? '' : 's'}: ${desc}).`
        };
    }

    const livePart = live.length ? `live: ${live.map((l) => l.pid || '?').join(', ')}` : 'no live owners';
    const stalePart = `stale: ${stale.map((s) => path.join(tempDir, s.name)).join('; ')}`;
    return {
        status: 'warn',
        detail: `Worker lock files include ${stale.length} stale entr${stale.length === 1 ? 'y' : 'ies'} (${livePart}; ${stalePart}). Delete the stale .lock file(s) to clear; Stream B's lock will recreate on next worker start.`
    };
}

// v2.6.6 — probe Stream D's in-process build path. Confirms
// `Genexus.MsBuild.Tasks.dll` is reachable under GX_PROGRAM_DIR / configured
// GeneXus install. If missing, the worker falls back to spawning MSBuild.exe
// (the slow path) — that still works but loses the IDE-parity build daemon.
function buildInProcessBuildAssemblyLoadCheck(gxPath) {
    const dllName = 'Genexus.MsBuild.Tasks.dll';
    const candidates = [];
    if (process.env.GX_PROGRAM_DIR) candidates.push(path.join(process.env.GX_PROGRAM_DIR, dllName));
    if (gxPath) candidates.push(path.join(gxPath, dllName));

    if (candidates.length === 0) {
        return {
            status: 'warn',
            detail: `In-process build assembly check skipped: GX_PROGRAM_DIR not set and no configured GeneXus path. Build will fall back to MSBuild.exe spawn (the slow path).`
        };
    }

    for (const candidate of candidates) {
        try {
            const stat = fs.statSync(candidate);
            if (stat && stat.isFile() && stat.size > 0) {
                return {
                    status: 'pass',
                    detail: `In-process build assembly is loadable from ${candidate} (${Math.round(stat.size / 1024)} KB). Stream D's build daemon is ready.`
                };
            }
        } catch {
            // try next candidate
        }
    }

    return {
        status: 'warn',
        detail: `In-process build assembly ${dllName} not found at: ${candidates.join('; ')}. The worker will fall back to spawning MSBuild.exe (the slow path) — set GXMCP_INPROCESS_BUILD=0 to silence, or install/repair GeneXus.`
    };
}

function redactConfig(cfg) {
    // Replace absolute paths with `<redacted:hash8>` so the structure is preserved
    // but filesystem layout, usernames, and KB names are not leaked. Hash is stable
    // across the dump so the support engineer can still correlate "this redacted KB
    // is the same as that one in worker logs."
    if (!cfg || typeof cfg !== 'object') return cfg;
    const crypto = require('crypto');
    const hash = (s) => crypto.createHash('sha256').update(s).digest('hex').slice(0, 8);
    const looksLikePath = (s) => typeof s === 'string' && /[\\/]/.test(s) && (s.length > 3);
    const walk = (node) => {
        if (Array.isArray(node)) return node.map(walk);
        if (node && typeof node === 'object') {
            const out = {};
            for (const k of Object.keys(node)) out[k] = walk(node[k]);
            return out;
        }
        if (looksLikePath(node)) return `<redacted:${hash(node)}>`;
        return node;
    };
    return walk(cfg);
}

async function buildSupportDump({ checks, summary, data, gatewayExePath, ctx }) {
    const os = require('os');
    const crypto = require('crypto');
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const tmpBase = path.join(os.tmpdir(), `genexus-mcp-dump-${stamp}`);
    fs.mkdirSync(tmpBase, { recursive: true });

    const entries = [];

    const writeEntry = (relPath, content) => {
        const full = path.join(tmpBase, relPath);
        fs.mkdirSync(path.dirname(full), { recursive: true });
        fs.writeFileSync(full, typeof content === 'string' ? content : JSON.stringify(content, null, 2));
        entries.push(relPath);
    };

    writeEntry('doctor.json', { summary, checks, generatedAt: new Date().toISOString() });

    if (data.configPath && fs.existsSync(data.configPath)) {
        const cfg = readJsonFileSafe(data.configPath);
        writeEntry('config.redacted.json', redactConfig(cfg));
    }

    let gxVersion = null;
    try { gxVersion = readGeneXusVersionFromInstall(data.gxPath); } catch { }

    writeEntry('environment.json', {
        platform: process.platform,
        arch: process.arch,
        nodeVersion: process.version,
        osRelease: os.release(),
        cwdHash: crypto.createHash('sha256').update(ctx.cwd || '').digest('hex').slice(0, 8),
        gatewayExePath,
        gatewayExeExists: fs.existsSync(gatewayExePath),
        configSource: data.configSource,
        kbConfigured: !!data.kbPath,
        gxConfigured: !!data.gxPath,
        gxVersion,
        envFlags: {
            GX_CONFIG_PATH: !!process.env.GX_CONFIG_PATH,
            GENEXUS_MCP_GATEWAY_EXE: !!process.env.GENEXUS_MCP_GATEWAY_EXE,
            GXMCP_INPROCESS_BUILD: process.env.GXMCP_INPROCESS_BUILD || null,
            HTTPS_PROXY: !!process.env.HTTPS_PROXY,
            HTTP_PROXY: !!process.env.HTTP_PROXY
        }
    });

    // Worker logs: scan the standard log dir (LocalAppData) for recent worker logs,
    // grab the tail of each. Keep it bounded so the zip stays small.
    const logRoots = [];
    if (process.env.LOCALAPPDATA) logRoots.push(path.join(process.env.LOCALAPPDATA, 'GenexusMCP', 'logs'));
    logRoots.push(path.join(os.tmpdir(), 'GenexusMCP', 'logs'));

    let logCount = 0;
    for (const root of logRoots) {
        if (logCount >= 5) break;
        try {
            if (!fs.existsSync(root)) continue;
            const files = fs.readdirSync(root)
                .filter((f) => /\.log$/i.test(f))
                .map((f) => ({ f, full: path.join(root, f), mtime: fs.statSync(path.join(root, f)).mtimeMs }))
                .sort((a, b) => b.mtime - a.mtime)
                .slice(0, 5 - logCount);
            for (const entry of files) {
                try {
                    const stat = fs.statSync(entry.full);
                    const tailBytes = 64 * 1024;
                    const start = Math.max(0, stat.size - tailBytes);
                    const fd = fs.openSync(entry.full, 'r');
                    try {
                        const buf = Buffer.alloc(stat.size - start);
                        fs.readSync(fd, buf, 0, buf.length, start);
                        writeEntry(`logs/${entry.f}.tail.txt`, buf.toString('utf8'));
                    } finally {
                        fs.closeSync(fd);
                    }
                    logCount += 1;
                } catch { }
            }
        } catch { }
    }

    // Zip via PowerShell's built-in Compress-Archive on Windows; tar elsewhere.
    const { execFileSync } = require('child_process');
    let zipPath;
    if (process.platform === 'win32') {
        zipPath = `${tmpBase}.zip`;
        execFileSync('powershell.exe', ['-NoProfile', '-Command', `Compress-Archive -Path '${tmpBase}\\*' -DestinationPath '${zipPath}' -Force`], { windowsHide: true });
    } else {
        zipPath = `${tmpBase}.tar.gz`;
        execFileSync('tar', ['-czf', zipPath, '-C', path.dirname(tmpBase), path.basename(tmpBase)]);
    }

    // Clean up the staging dir; the zip is the artifact.
    try { fs.rmSync(tmpBase, { recursive: true, force: true }); } catch { }

    return {
        zipPath,
        sizeBytes: fs.statSync(zipPath).size,
        entries
    };
}

async function handleDoctor(options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const toolDefPath = getToolDefinitionsPath();
    const toolDefsExists = fs.existsSync(toolDefPath);
    const gatewayExePath = getGatewayExePath();
    const stdioErrorLogPath = getStdioErrorLogPath();
    const stdioErrorLogExists = !!(stdioErrorLogPath && fs.existsSync(stdioErrorLogPath));

    let toolCount = 0;
    if (toolDefsExists) {
        try {
            const parsed = JSON.parse(fs.readFileSync(toolDefPath, 'utf8'));
            if (Array.isArray(parsed)) toolCount = parsed.length;
        } catch {
            toolCount = 0;
        }
    }

    const kbPath = data.kbPath;
    const gxPath = data.gxPath;
    const kbExists = !!(kbPath && fs.existsSync(kbPath));
    const gxExeExists = !!(gxPath && fs.existsSync(path.join(gxPath, 'genexus.exe')));

    const riskyZone = isPathLikelyAppLockerBlocked(gatewayExePath);
    const clientCrossCheck = buildClientExeCrossCheck(gatewayExePath);

    const checks = [
        { id: 'config_file', status: data.configFound ? 'pass' : 'fail', detail: data.configFound ? 'GX config file was found.' : 'GX config file is missing.' },
        { id: 'gateway_exe', status: data.gatewayExeFound ? 'pass' : 'fail', detail: data.gatewayExeFound ? 'Gateway executable is available.' : 'Gateway executable is missing.' },
        {
            id: 'stdio_error_log',
            status: stdioErrorLogExists ? 'warn' : 'pass',
            detail: stdioErrorLogExists
                ? `Previous stdio launcher failure recorded at ${stdioErrorLogPath}. Read this file for the last exit code and stderr tail.`
                : `Stdio launcher error log: ${stdioErrorLogPath || 'not available on this platform'}.`
        },
        {
            id: 'gateway_exe_path_safety',
            status: riskyZone ? 'warn' : 'pass',
            detail: riskyZone
                ? `Gateway exe is under %${riskyZone}%, which is commonly blocked by AppLocker/SRP in Windows domains. If clients show "Failed to connect" / "Access denied", reinstall to a whitelisted path via scripts/install.ps1 (defaults to C:\\Tools\\GenexusMCP or %LOCALAPPDATA%\\Programs\\GenexusMCP).`
                : 'Gateway exe is in a path unlikely to be blocked by AppLocker/SRP.'
        },
        // A KB path configured but absent on disk is fatal — the worker can't open a KB
        // that doesn't exist. Only when no KB is configured at all do we soften to warn.
        { id: 'kb_path_exists', status: kbExists ? 'pass' : (kbPath ? 'fail' : 'warn'), detail: kbExists ? 'Configured KB path exists.' : (kbPath ? `Configured KB path does not exist: ${kbPath}` : 'No KB path is configured.') },
        { id: 'kb_shape', status: data.kbLooksValid ? 'pass' : 'warn', detail: data.kbLooksValid ? 'KB folder shape looks valid.' : 'KB markers were not found in configured KB path.' },
        // Same logic for the GeneXus install: missing genexus.exe at a configured path
        // guarantees a worker crash on first MCP call. Promote from warn to fail so init
        // exits non-zero and the caller (install.ps1, AI client) actually sees the problem.
        { id: 'gx_installation', status: gxExeExists ? 'pass' : (gxPath ? 'fail' : 'warn'), detail: gxExeExists ? 'GeneXus installation has genexus.exe.' : (gxPath ? `Configured GeneXus installation is missing genexus.exe at: ${gxPath}` : 'No GeneXus installation path is configured.') },
        { id: 'tool_definitions', status: toolDefsExists ? 'pass' : 'warn', detail: toolDefsExists ? `Tool definition file found (${toolCount} tools) at ${toolDefPath}.` : (process.env.GENEXUS_MCP_TOOL_DEFINITIONS ? `tool_definitions.json missing at GENEXUS_MCP_TOOL_DEFINITIONS=${toolDefPath}. Unset the env var or point it at a valid file.` : `tool_definitions.json missing. Expected at ${toolDefPath} (next to the gateway exe). The csproj should copy it on publish — reinstall via scripts/install.ps1, or set GENEXUS_MCP_TOOL_DEFINITIONS to override.`) },
        { id: 'gx_env', status: process.env.GX_CONFIG_PATH ? 'pass' : 'warn', detail: process.env.GX_CONFIG_PATH ? 'GX_CONFIG_PATH env var is set.' : 'GX_CONFIG_PATH env var is not set for this process.' },
        { id: 'client_config_sync', status: clientCrossCheck.status, detail: clientCrossCheck.detail }
    ];

    // v2.6.6 — Stream B / Stream D doctor checks.
    const lockCheck = buildWorkerSingleInstanceLockCheck();
    checks.push({ id: 'worker_single_instance_lock', status: lockCheck.status, detail: lockCheck.detail });
    const inProcessLoad = buildInProcessBuildAssemblyLoadCheck(gxPath);
    checks.push({ id: 'in_process_build_assembly_load', status: inProcessLoad.status, detail: inProcessLoad.detail });

    // Client registration summary — one line answering "are my AI agents wired up?".
    const clientRows = clientsStatus();
    const installedRows = clientRows.filter((r) => r.installed);
    const staleRows = clientRows.filter((r) => r.commandStale);
    const installedUnregistered = installedRows.filter((r) => !r.registered && r.writeSupported);
    let clientsStatusLevel = 'pass';
    let clientsDetail;
    if (staleRows.length > 0) {
        clientsStatusLevel = 'warn';
        clientsDetail = `${staleRows.map((r) => r.name).join(', ')} point at a missing gateway exe. Re-register: genexus-mcp clients add --clients ${staleRows.map((r) => r.id).join(',')}.`;
    } else if (installedUnregistered.length > 0) {
        clientsStatusLevel = 'warn';
        clientsDetail = `${installedUnregistered.length} installed agent(s) not registered (${installedUnregistered.map((r) => r.name).join(', ')}). Run: genexus-mcp clients add --clients ${installedUnregistered.map((r) => r.id).join(',')}.`;
    } else {
        const reg = clientRows.filter((r) => r.registered).length;
        clientsDetail = `${reg} agent(s) registered; ${installedRows.length} installed. Run \`genexus-mcp clients\` for the full table.`;
    }
    checks.push({ id: 'clients_registered', status: clientsStatusLevel, detail: clientsDetail });

    if (data.gatewayExeFound) {
        const probe = await probeGatewaySpawn();
        checks.push({ id: 'gateway_spawn_probe', status: probe.status, detail: probe.detail });
    } else {
        checks.push({ id: 'gateway_spawn_probe', status: 'warn', detail: 'Spawn probe skipped: gateway exe not found.' });
    }

    if (options.mcpSmoke) {
        const smoke = await runMcpSmokeProbe(ctx.cwd);
        checks.push({ id: 'mcp_smoke', status: smoke.status, detail: smoke.detail });
    }

    const summary = checks.reduce((acc, row) => {
        acc[row.status] = (acc[row.status] || 0) + 1;
        return acc;
    }, { pass: 0, warn: 0, fail: 0 });

    // Support bundle for handing off to support: doctor output + config (with paths
    // anonymized by hash so we don't leak filesystem layout) + recent worker logs +
    // version info, all zipped under TempPath. Far simpler than asking the operator
    // to send 5 separate things over chat.
    if (options.dump) {
        try {
            const dumpResult = await buildSupportDump({ checks, summary, data, gatewayExePath, ctx });
            return {
                exitCode: ctx.EXIT_CODES.OK,
                envelope: {
                    ok: {
                        action: 'doctor.dump',
                        zipPath: dumpResult.zipPath,
                        sizeBytes: dumpResult.sizeBytes,
                        entries: dumpResult.entries,
                        summary
                    },
                    help: ['Attach the zip to your support ticket. Paths inside config.json have been redacted; sensitive values may still appear in worker logs — review before sharing if needed.']
                }
            };
        } catch (err) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(`Failed to build doctor dump: ${err && err.message ? err.message : 'unknown error'}`, ctx.EXIT_CODES.ERROR)
            };
        }
    }

    const defaultFields = ['id', 'status', 'detail'];
    const allowedFields = ['id', 'status', 'detail'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'doctor', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }
    const selectedFields = fieldSelection.selectedFields || defaultFields;
    const limited = checks.slice(0, options.limit).map((row) => pickFields(row, selectedFields));

    const help = [];
    if (checks.length > options.limit) {
        help.push(`Run 'genexus-mcp doctor --limit ${checks.length}' for all checks.`);
    }
    if (!options.mcpSmoke) {
        help.push('Run `genexus-mcp doctor --mcp-smoke` to execute MCP protocol smoke checks.');
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                summary,
                checks: limited,
                returned: limited.length,
                total: checks.length
            },
            help,
            meta: { fields: selectedFields }
        }
    };
}

async function handleToolsList(options, ctx) {
    const toolDefPath = getToolDefinitionsPath();
    if (!fs.existsSync(toolDefPath)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('tool_definitions.json not found.', ctx.EXIT_CODES.ERROR, ['Run `genexus-mcp doctor` to inspect installation health.'])
        };
    }

    let parsed;
    try {
        parsed = JSON.parse(fs.readFileSync(toolDefPath, 'utf8'));
    } catch {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('Failed to parse tool_definitions.json.', ctx.EXIT_CODES.ERROR, ['Validate the JSON file and rerun tools list.'])
        };
    }

    if (!Array.isArray(parsed)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('tool_definitions.json is not an array.', ctx.EXIT_CODES.ERROR)
        };
    }

    const query = (options.query || '').toLowerCase();
    const rowsAll = parsed.map((tool) => {
        const description = typeof tool.description === 'string' ? tool.description : '';
        const truncated = !options.full && description.length > 160;
        return {
            name: tool.name || 'unknown',
            status: 'available',
            category: resolveToolCategory(tool.name || ''),
            required: Array.isArray(tool.inputSchema && tool.inputSchema.required) ? tool.inputSchema.required.length : 0,
            description: truncated ? `${description.slice(0, 160)}...` : description,
            descriptionChars: description.length,
            truncated
        };
    });

    const rowsFiltered = query
        ? rowsAll.filter((row) => row.name.toLowerCase().includes(query) || row.description.toLowerCase().includes(query))
        : rowsAll;

    const totals = rowsFiltered.reduce((acc, row) => {
        acc[row.category] = (acc[row.category] || 0) + 1;
        return acc;
    }, {});

    const defaultFields = ['name', 'status', 'required'];
    const allowedFields = ['name', 'status', 'category', 'required', 'description', 'descriptionChars', 'truncated'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'tools list', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }
    const selectedFields = fieldSelection.selectedFields || defaultFields;
    const rows = rowsFiltered.slice(0, options.limit).map((row) => pickFields(row, selectedFields));
    const includesDescription = selectedFields.includes('description');
    const returnedRows = rowsFiltered.slice(0, options.limit);
    const anyReturnedTruncated = returnedRows.some((row) => row.truncated);

    const help = [];
    if (rowsFiltered.length === 0) {
        help.push('No tools matched the current filter. Try `genexus-mcp tools list --fields name,status` without --query.');
    }
    if (rowsFiltered.length > options.limit) {
        help.push(`Run 'genexus-mcp tools list --limit ${rowsFiltered.length}${query ? ` --query ${query}` : ''}' for all matching items.`);
    }
    if (includesDescription && anyReturnedTruncated && !options.full) {
        help.push('Run `genexus-mcp tools list --full --fields name,description` to view full descriptions.');
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                tools: rows,
                returned: rows.length,
                total: rowsFiltered.length,
                empty: rowsFiltered.length === 0
            },
            help,
            meta: {
                fields: selectedFields,
                query: options.query || null,
                totalByCategory: totals,
                truncated: includesDescription ? anyReturnedTruncated : false
            }
        }
    };
}

async function handleConfigShow(options, ctx) {
    const configPath = resolveConfigPathNoMutate(ctx.cwd);
    if (!configPath) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('No config.json was found. Set GX_CONFIG_PATH or place config.json in current directory.', ctx.EXIT_CODES.ERROR, ['Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to create one.'])
        };
    }

    const config = readJsonFileSafe(configPath);
    if (config === null) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('config.json exists but is not valid JSON.', ctx.EXIT_CODES.ERROR, ['Fix config.json and rerun `genexus-mcp config show`.'])
        };
    }

    const raw = fs.readFileSync(configPath, 'utf8');
    const rawChars = raw.length;
    const truncateLimit = 1200;
    const truncated = !options.full && rawChars > truncateLimit;

    const compact = {
        path: configPath,
        kbPath: config.Environment && config.Environment.KBPath ? config.Environment.KBPath : null,
        gxPath: config.GeneXus && config.GeneXus.InstallationPath ? config.GeneXus.InstallationPath : null,
        httpPort: config.Server && config.Server.HttpPort ? config.Server.HttpPort : null,
        mcpStdio: config.Server && typeof config.Server.McpStdio === 'boolean' ? config.Server.McpStdio : null,
        raw: truncated ? `${raw.slice(0, truncateLimit)}\n... (truncated, ${rawChars} chars total)` : raw
    };

    const allowedFields = ['path', 'kbPath', 'gxPath', 'httpPort', 'mcpStdio', 'raw'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'config show', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }
    const selectedFields = fieldSelection.selectedFields;
    const payload = selectedFields ? pickFields(compact, selectedFields) : compact;
    const includesRaw = selectedFields ? selectedFields.includes('raw') : true;
    const effectiveTruncated = includesRaw ? truncated : false;

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: payload,
            help: effectiveTruncated ? ['Run `genexus-mcp config show --full` to view complete config content.'] : [],
            meta: { truncated: effectiveTruncated, rawChars }
        }
    };
}

async function runLayoutAutomation(payload, cwd) {
    const scriptPath = path.join(__dirname, '..', '..', 'scripts', 'gx_layout_uia.ps1');
    if (!fs.existsSync(scriptPath)) {
        return { ok: false, error: `Layout automation script not found at ${scriptPath}.` };
    }

    const shell = process.platform === 'win32' ? 'powershell' : 'pwsh';
    const args = process.platform === 'win32'
        ? ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath, '-Payload', JSON.stringify(payload)]
        : ['-NoProfile', '-File', scriptPath, '-Payload', JSON.stringify(payload)];

    return await new Promise((resolve) => {
        let stdout = '';
        let stderr = '';
        let resolved = false;
        let timer = null;
        const timeoutMs = 30000; // 30 second timeout

        const finish = (payload) => {
            if (resolved) return;
            resolved = true;
            if (timer) clearTimeout(timer);
            resolve(payload);
        };

        try {
            const child = spawn(shell, args, {
                cwd,
                stdio: ['ignore', 'pipe', 'pipe'],
                windowsHide: true,
                env: process.env
            });

            child.stdout.on('data', (chunk) => {
                stdout += chunk.toString();
            });

            child.stderr.on('data', (chunk) => {
                stderr += chunk.toString();
            });

            child.on('error', (err) => {
                finish({ ok: false, error: `Failed to launch layout automation: ${err.message}` });
            });

            child.on('exit', (code) => {
                const output = (stdout || '').trim();
                if (code !== 0) {
                    const detail = sanitizeOperationalMessage((stderr || output || '').trim(), 'Layout automation failed.');
                    finish({ ok: false, error: detail });
                    return;
                }

                try {
                    const parsed = output ? JSON.parse(output) : {};
                    finish({ ok: true, data: parsed });
                } catch {
                    finish({
                        ok: false,
                        error: sanitizeOperationalMessage(`Layout automation returned invalid JSON: ${output || stderr}`, 'Invalid layout automation response.')
                    });
                }
            });

            timer = setTimeout(() => {
                try {
                    child.kill();
                } catch {}
                finish({ ok: false, error: `Layout automation timed out after ${timeoutMs}ms` });
            }, timeoutMs);

        } catch (err) {
            finish({ ok: false, error: `Layout automation crashed before launch: ${err.message}` });
        }
    });
}

async function handleLayout(subcommand, options, ctx) {
    if (subcommand === 'status') {
        const outcome = await runLayoutAutomation({ action: 'status', title: options.title || null }, ctx.cwd);
        if (!outcome.ok) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(outcome.error, ctx.EXIT_CODES.ERROR, [
                    'Run `genexus-mcp layout status --format json` to inspect raw status.',
                    'Open GeneXus and focus an object with the Layout tab visible.'
                ])
            };
        }

        const data = outcome.data && typeof outcome.data === 'object' ? outcome.data : {};
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    running: !!data.running,
                    focused: !!data.focused,
                    pid: data.pid || null,
                    title: data.title || null,
                    layoutTabDetected: !!data.layoutTabDetected
                },
                help: data.running
                    ? ['Run `genexus-mcp layout run --action activate-layout` to focus the Layout tab.']
                    : ['Open GeneXus and rerun `genexus-mcp layout status`.']
            }
        };
    }

    if (subcommand === 'run') {
        if (!options.action) {
            return {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope('layout run requires --action. Supported: focus, activate-layout, activate-tab, send-keys, type-text, click.', ctx.EXIT_CODES.USAGE)
            };
        }

        const payload = {
            action: options.action,
            title: options.title || null,
            tab: options.tab || null,
            keys: options.keys || null,
            text: options.text || null,
            x: Number.isFinite(options.x) ? options.x : null,
            y: Number.isFinite(options.y) ? options.y : null
        };

        const outcome = await runLayoutAutomation(payload, ctx.cwd);
        if (!outcome.ok) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(outcome.error, ctx.EXIT_CODES.ERROR, [
                    'Validate GeneXus window is visible and not blocked by modal dialogs.',
                    'Use `genexus-mcp layout status` before retrying.'
                ])
            };
        }

        const data = outcome.data && typeof outcome.data === 'object' ? outcome.data : {};
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: data.action || options.action,
                    success: data.success !== false,
                    title: data.title || null,
                    pid: data.pid || null,
                    tab: data.tab || options.tab || null,
                    detail: data.detail || null
                },
                help: [
                    'Run `genexus-mcp layout run --action send-keys --keys "^{S}"` to trigger save.',
                    'Run `genexus-mcp layout run --action click --x <screenX> --y <screenY>` for deterministic designer clicks.'
                ]
            }
        };
    }

    if (subcommand === 'inspect') {
        const payload = {
            action: 'inspect',
            title: options.title || null,
            tab: options.tab || 'Layout',
            limit: options.limit
        };

        const outcome = await runLayoutAutomation(payload, ctx.cwd);
        if (!outcome.ok) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(outcome.error, ctx.EXIT_CODES.ERROR, [
                    'Validate GeneXus window is visible and object tab strip is rendered.',
                    'Try `genexus-mcp layout inspect --tab Layout --format json` after focusing the target object.'
                ])
            };
        }

        const data = outcome.data && typeof outcome.data === 'object' ? outcome.data : {};
        const controlsRaw = Array.isArray(data.controls) ? data.controls : [];
        const controls = controlsRaw.map((row) => {
            if (options.full) return row;
            return pickFields(row, ['name', 'controlType', 'automationId', 'bounds']);
        });

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    running: data.running !== false,
                    pid: data.pid || null,
                    title: data.title || null,
                    tab: data.tab || (options.tab || 'Layout'),
                    tabActivated: data.tabActivated !== false,
                    returned: controls.length,
                    total: Number.isFinite(data.total) ? data.total : controls.length,
                    controls
                },
                help: controls.length === 0
                    ? ['No controls found. Try `genexus-mcp layout inspect --tab Layout --full --format json`.']
                    : ['Run `genexus-mcp layout inspect --full --limit 300 --format json` for full control metadata.']
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.USAGE,
        envelope: usageEnvelope('layout requires subcommand `status`, `run`, or `inspect`.', ctx.EXIT_CODES.USAGE)
    };
}

function buildClientLauncherHelp(patchResult) {
    const help = [];
    const patched = patchResult && Array.isArray(patchResult.patched) ? patchResult.patched : [];
    if (patched.includes('Antigravity') && process.platform === 'win32' && !process.env.GENEXUS_MCP_GATEWAY_EXE) {
        help.push('Antigravity uses the gateway executable bundled with this npm package when available; re-run `npx genexus-mcp@latest clients add --clients antigravity` after an upgrade if its package path is stale.');
    }
    if (patched.length > 0 && process.platform === 'win32' && !process.env.GENEXUS_MCP_GATEWAY_EXE) {
        help.push('Windows launcher paths may resolve under the npm cache and be blocked by AppLocker/SRP. Use scripts/install.ps1 for a stable whitelisted path.');
    }
    return help;
}

function buildInteractiveInitHelp(patchResult) {
    const help = [];
    if (patchResult.failed && patchResult.failed.some((f) => f.reason && f.reason.includes('third-party or HTTP MCP server'))) {
        help.push('A collision with an existing MCP server (e.g. official GeneXus MCP) was detected. Re-run init with --server-name=<customName> (e.g. --server-name Gx18byLennix) or pass --force.');
    }
    if (patchResult.patched.length === 0) {
        help.push('Set `GX_CONFIG_PATH` in your MCP client env to the generated config path.');
    }
    help.push(...buildClientLauncherHelp(patchResult));
    return help;
}

async function runInteractiveInit(ctx) {
    const defaultGx = discoverGeneXusInstallation() || 'C:\\Program Files (x86)\\GeneXus\\GeneXus18';

    if (!ctx.options.quiet) {
        ctx.stderr.write('GeneXus MCP setup wizard\n\n');
    }

    const rl = readline.createInterface({ input: process.stdin, output: ctx.stderr });
    const question = (text) => new Promise((resolve) => rl.question(text, (answer) => resolve(answer)));

    try {
        const kbAnswer = await question(`1) Knowledge Base folder path (default: ${ctx.cwd}):\n> `);
        const finalKb = String(kbAnswer || '').trim() || ctx.cwd;

        const gxAnswer = await question(`\n2) GeneXus installation path (default: ${defaultGx}):\n> `);
        const finalGx = String(gxAnswer || '').trim() || defaultGx;

        if (!fs.existsSync(path.join(finalGx, 'genexus.exe'))) {
            const suggested = discoverGeneXusInstallation();
            const help = [`Path checked: ${finalGx}`];
            if (suggested && suggested.toLowerCase() !== finalGx.toLowerCase()) {
                help.push(`Detected a working GeneXus install at: ${suggested}`);
                help.push('Re-run `genexus-mcp init --interactive` and accept the detected path, or pass --gx explicitly.');
            }
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(
                    `GeneXus path does not contain genexus.exe. Aborted before writing config to avoid silent worker crashes.`,
                    ctx.EXIT_CODES.ERROR,
                    help
                )
            };
        }

        if (!fs.existsSync(finalKb)) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(
                    `KB path does not exist on disk. Aborted before writing config.`,
                    ctx.EXIT_CODES.ERROR,
                    [`Path checked: ${finalKb}`, 'Create the KB in GeneXus first, then re-run init.']
                )
            };
        }

        const allTargets = getClientConfigTargets();
        const platformTargets = filterClientTargets(allTargets, { platform: process.platform });
        ctx.stderr.write('\n3) Select AI agents to register (y/N per agent; Enter accepts default):\n');
        const selectedIds = [];
        for (const target of platformTargets) {
            const detection = detectClientInstalled(target);
            const defaultYes = detection.installed;
            const tag = detection.installed ? 'detected' : 'not detected';
            // When not detected, show where we looked so the user understands why
            // (and can still type `y` to register a freshly-installed agent).
            let hint = '';
            if (!detection.installed && detection.markersChecked.length) {
                hint = ` — looked in ${detection.markersChecked[0]}`;
            }
            const prompt = `   - ${target.name} [${tag}${hint}] (${defaultYes ? 'Y/n' : 'y/N'}): `;
            const ans = (await question(prompt)).trim().toLowerCase();
            const yes = ans === '' ? defaultYes : (ans === 'y' || ans === 'yes');
            if (yes) selectedIds.push(target.id);
        }

        const created = createConfigFile(finalKb, finalGx);
        const serverName = ctx.options && ctx.options.serverName ? ctx.options.serverName : DEFAULT_MCP_SERVER_NAME;
        const force = Boolean(ctx.options && ctx.options.force);
        const patchResult = selectedIds.length
            ? patchClientConfig(created.targetConfigPath, { ids: selectedIds, onlyExisting: false, serverName, force })
            : { patched: [], failed: [], skipped: [] };

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'init',
                    mode: 'interactive',
                    configPath: created.targetConfigPath,
                    noOp: !created.changed,
                    clientsPatchedCount: patchResult.patched.length
                },
                help: buildInteractiveInitHelp(patchResult),
                meta: {
                    patchedClients: patchResult.patched,
                    failedClients: patchResult.failed,
                    skippedClients: patchResult.skipped || []
                }
            }
        };
    } catch (err) {
        if (err && err.code === 'GATEWAY_EXE_MISSING') {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(
                    'GENEXUS_MCP_GATEWAY_EXE points to a path that does not exist. Client configs were NOT modified.',
                    ctx.EXIT_CODES.ERROR,
                    [
                        `Path checked: ${process.env.GENEXUS_MCP_GATEWAY_EXE}`,
                        'Either unset GENEXUS_MCP_GATEWAY_EXE (then init writes the npx-based launcher) or re-run scripts/install.ps1 to materialize the exe at that path.'
                    ]
                )
            };
        }
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(
                sanitizeOperationalMessage(`Interactive init failed: ${err && err.message ? err.message : 'unknown error'}`),
                ctx.EXIT_CODES.ERROR
            )
        };
    } finally {
        rl.close();
    }
}

async function handleInit(options, ctx) {
    if (options.interactive) {
        return runInteractiveInit({ ...ctx, options });
    }

    const resolution = {
        kb: { value: options.kb || null, source: options.kb ? 'flag' : null },
        gx: { value: options.gx || null, source: options.gx ? 'flag' : null }
    };

    if (!resolution.kb.value) {
        const fromCwd = discoverKnowledgeBase(ctx.cwd);
        if (fromCwd) {
            resolution.kb.value = fromCwd;
            resolution.kb.source = 'cwd';
        }
    }

    // Broaden the search: walk-up from cwd and scan common KB roots. If exactly one
    // candidate exists, use it; if many, surface them so the operator can pass --kb
    // explicitly instead of seeing the bare "missing --kb" error.
    let kbCandidates = null;
    if (!resolution.kb.value) {
        kbCandidates = discoverKnowledgeBases(ctx.cwd);
        if (kbCandidates.length === 1) {
            resolution.kb.value = kbCandidates[0].path;
            resolution.kb.source = `auto-discovery:${kbCandidates[0].source}`;
        }
    }

    if (!resolution.gx.value) {
        const fromDisco = discoverGeneXusInstallation();
        if (fromDisco) {
            resolution.gx.value = fromDisco;
            resolution.gx.source = 'auto-discovery';
        }
    }

    if (!resolution.kb.value || !resolution.gx.value) {
        const missing = [];
        const help = [];
        if (!resolution.kb.value) {
            missing.push('--kb (and current directory is not a GeneXus KB)');
            if (kbCandidates && kbCandidates.length > 1) {
                help.push(`Found ${kbCandidates.length} candidate KB folder${kbCandidates.length === 1 ? '' : 's'}. Pick one and pass it as --kb:`);
                for (const c of kbCandidates.slice(0, 10)) {
                    help.push(`  --kb "${c.path}"   (${c.source})`);
                }
                if (kbCandidates.length > 10) {
                    help.push(`  ... and ${kbCandidates.length - 10} more.`);
                }
            }
        }
        if (!resolution.gx.value) missing.push('--gx (and no GeneXus installation was auto-discovered)');
        return {
            exitCode: ctx.EXIT_CODES.USAGE,
            envelope: {
                error: { code: 'usage_error', message: `Cannot resolve required paths: ${missing.join('; ')}. Pass flags explicitly or run from inside a KB folder.` },
                help: help.length ? help : [
                    'Run `genexus-mcp help` for command reference.',
                    'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` for non-interactive setup.'
                ],
                meta: { exitCode: ctx.EXIT_CODES.USAGE }
            }
        };
    }

    // Validate the supplied GeneXus path before we commit it to disk. A non-default
    // install (e.g. C:\...\GeneXus18u7 vs the GeneXus18 default) used to slip through
    // — init wrote the config, then the worker crashed on first MCP call with no
    // useful signal back to the operator.
    if (!fs.existsSync(path.join(resolution.gx.value, 'genexus.exe'))) {
        const help = [
            `Path checked: ${resolution.gx.value}`,
            `Source: --${resolution.gx.source === 'flag' ? 'gx flag' : resolution.gx.source}`
        ];
        const suggested = resolution.gx.source === 'flag' ? discoverGeneXusInstallation() : null;
        if (suggested && suggested.toLowerCase() !== resolution.gx.value.toLowerCase()) {
            help.push(`Detected a working GeneXus install at: ${suggested}`);
            help.push(`Re-run: genexus-mcp init --kb "${resolution.kb.value}" --gx "${suggested}"`);
        } else {
            help.push('Omit --gx to let init auto-discover via registry / Program Files (matches GeneXus18, GeneXus18u7, etc.).');
        }
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(
                `Configured GeneXus path does not contain genexus.exe. Init aborted before writing config to avoid silent worker crashes.`,
                ctx.EXIT_CODES.ERROR,
                help
            )
        };
    }

    if (!fs.existsSync(resolution.kb.value)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(
                `Configured KB path does not exist on disk. Init aborted.`,
                ctx.EXIT_CODES.ERROR,
                [
                    `Path checked: ${resolution.kb.value}`,
                    'Create the KB in GeneXus first, then re-run init pointing at its folder.'
                ]
            )
        };
    }

    try {
        const created = createConfigFile(resolution.kb.value, resolution.gx.value);
        const kbName = path.basename(resolution.kb.value);
        addKbToConfig(created.targetConfigPath, kbName, resolution.kb.value);

        let patchResult = { patched: [], failed: [], skipped: [] };
        // issue #32 item 3: register the client(s) by default. Previously init wrote only
        // config.json and returned clientsPatchedCount:0 unless --write-clients was passed —
        // non-obvious, so users had to `claude mcp add` manually. Now non-interactive init
        // patches DETECTED (already-installed) clients unless --no-write-clients is given.
        const shouldWriteClients = !options.noWriteClients;
        if (shouldWriteClients) {
            const ids = resolveClientIds(options);
            const validation = validateClientIds(ids);
            if (!validation.ok) {
                return {
                    exitCode: ctx.EXIT_CODES.USAGE,
                    envelope: usageEnvelope(validation.message, ctx.EXIT_CODES.USAGE)
                };
            }
            patchResult = patchClientConfig(created.targetConfigPath, {
                ids,
                onlyExisting: !options.allClients,
                serverName: options.serverName || DEFAULT_MCP_SERVER_NAME,
                force: options.force
            });
        }

        const verification = await runPostInitVerification({
            cwd: resolution.kb.value,
            configPath: created.targetConfigPath,
            includeSmoke: !options.noSmoke,
            ctx
        });

        let warm = null;
        if (options.warm) {
            warm = await warmGateway({ configPath: created.targetConfigPath });
        }

        const help = [];
        if (patchResult.failed.some((f) => f.reason && f.reason.includes('third-party or HTTP MCP server'))) {
            help.push('A collision with an existing MCP server (e.g. official GeneXus MCP) was detected on one or more clients.');
            help.push('Re-run init with --server-name=<customName> (e.g. --server-name Gx18byLennix), pass --force, or pass --no-write-clients.');
        }
        if (patchResult.patched.length === 0 && shouldWriteClients) {
            help.push('No installed AI client config was found to patch. Pass --all-clients to write configs for all known clients, or register manually.');
            help.push(`For a GLOBAL (all-projects) registration that does not depend on the current directory, point your client at this config via the GX_CONFIG_PATH env var, e.g.: claude mcp add genexus -e GX_CONFIG_PATH="${created.targetConfigPath}" -- <launcher>`);
        } else if (patchResult.patched.length === 0 && !shouldWriteClients) {
            help.push('Client patching was skipped (--no-write-clients). Register manually, or set GX_CONFIG_PATH to this config for global use.');
        }
        help.push(...buildClientLauncherHelp(patchResult));
        if (verification.summary.fail > 0) {
            const failedIds = verification.checks
                .filter((c) => c.status === 'fail')
                .map((c) => c.id)
                .join(', ');
            help.push(`Verification failed (${verification.summary.fail} check${verification.summary.fail === 1 ? '' : 's'}: ${failedIds}). The config was written but the MCP will not work until these are fixed.`);
            help.push('Run `genexus-mcp doctor --mcp-smoke` for full details.');
        } else if (verification.summary.warn > 0) {
            help.push('Some verification checks emitted warnings. Run `genexus-mcp doctor --mcp-smoke` for details.');
        }
        if (options.noSmoke) {
            help.push('MCP protocol smoke was skipped (--no-smoke). Re-run `genexus-mcp doctor --mcp-smoke` to validate end-to-end.');
        }

        // A non-zero exit when any critical check failed gives the caller (install.ps1,
        // CI, AI client) something to react to. Previously init always returned OK and
        // the failure surfaced later as a generic worker crash.
        const initExitCode = verification.summary.fail > 0 ? ctx.EXIT_CODES.ERROR : ctx.EXIT_CODES.OK;

        return {
            exitCode: initExitCode,
            envelope: {
                ok: {
                    action: 'init',
                    mode: 'non_interactive',
                    configPath: created.targetConfigPath,
                    configFound: true,
                    noOp: !created.changed,
                    clientsPatchedCount: patchResult.patched.length,
                    resolved: {
                        kb: { path: resolution.kb.value, source: resolution.kb.source },
                        gx: { path: resolution.gx.value, source: resolution.gx.source }
                    },
                    verification: {
                        summary: verification.summary,
                        checks: verification.checks
                    },
                    warm: warm || null
                },
                help,
                meta: {
                    patchedClients: patchResult.patched,
                    failedClients: patchResult.failed,
                    skippedClients: patchResult.skipped || [],
                    smokeSkipped: !!options.noSmoke,
                    warmed: !!warm && warm.status === 'pass'
                }
            }
        };
    } catch (err) {
        if (err && err.code === 'GATEWAY_EXE_MISSING') {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(
                    'GENEXUS_MCP_GATEWAY_EXE points to a path that does not exist. Client configs were NOT modified.',
                    ctx.EXIT_CODES.ERROR,
                    [
                        `Path checked: ${process.env.GENEXUS_MCP_GATEWAY_EXE}`,
                        'Either unset GENEXUS_MCP_GATEWAY_EXE (then init writes the npx-based launcher) or re-run scripts/install.ps1 to materialize the exe at that path.'
                    ]
                )
            };
        }
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(
                sanitizeOperationalMessage(`Failed to write configuration: ${err && err.message ? err.message : 'unknown error'}`),
                ctx.EXIT_CODES.ERROR,
                ['Check write permissions on the target directory; on Windows, run from a path outside protected areas.']
            )
        };
    }
}

async function warmGateway({ configPath }) {
    return spawnGatewayProbe({
        env: { ...process.env, GX_CONFIG_PATH: configPath },
        spawnHoldMs: 1500,
        timeoutMs: 12000,
        label: 'Warm spawn',
        successDetail: 'Gateway warmed (worker process kicked).'
    });
}

async function runPostInitVerification({ cwd, configPath, includeSmoke, ctx }) {
    // Doctor inspects buildStatusData(cwd), which only finds config.json if it sits
    // at cwd or if GX_CONFIG_PATH is exported. Init runs from wherever the operator
    // typed `npx genexus-mcp init` (often C:\windows\system32) — so we point doctor
    // at the freshly-written config explicitly.
    const priorEnv = process.env.GX_CONFIG_PATH;
    if (configPath) process.env.GX_CONFIG_PATH = configPath;
    try {
        const doctorResult = await handleDoctor(
            { full: false, mcpSmoke: !!includeSmoke, fields: null, limit: 100 },
            { cwd, EXIT_CODES: ctx.EXIT_CODES }
        );

        const { checks, summary } = doctorResult.envelope.ok;

        let workerSmoke = null;
        if (includeSmoke && configPath) {
            workerSmoke = await probeWorkerStartup({ configPath });
            checks.push({ id: 'worker_startup_smoke', status: workerSmoke.status, detail: workerSmoke.detail });
            summary[workerSmoke.status] = (summary[workerSmoke.status] || 0) + 1;
        }

        return { checks, summary };
    } finally {
        if (priorEnv === undefined) delete process.env.GX_CONFIG_PATH;
        else process.env.GX_CONFIG_PATH = priorEnv;
    }
}

// Spawn the gateway with the resolved config and watch for an early crash.
// If the worker can't load the KB (bad GX path, missing genexus.exe, KB lock,
// AppLocker block, etc.), the process exits fast with a non-zero code. Without
// this probe init prints "ok" and the failure only surfaces later on the first
// MCP call, with a generic "Worker crashed/exited" — exactly what bit the user
// who had GeneXus18u7 instead of GeneXus18.
async function probeWorkerStartup({ configPath, observeMs = 2500 }) {
    const gatewayExePath = getGatewayExePath();
    if (!fs.existsSync(gatewayExePath)) {
        return { status: 'warn', detail: 'Worker smoke skipped: gateway exe not found.' };
    }

    return await new Promise((resolve) => {
        let stderr = '';
        let stdout = '';
        let resolved = false;
        const finish = (payload) => {
            if (resolved) return;
            resolved = true;
            resolve(payload);
        };

        let child;
        try {
            child = spawn(gatewayExePath, [], {
                stdio: ['ignore', 'pipe', 'pipe'],
                windowsHide: true,
                env: { ...process.env, GX_CONFIG_PATH: configPath }
            });
        } catch (err) {
            return finish({ status: 'fail', detail: `Worker smoke: failed to launch gateway: ${err.message}` });
        }

        child.stdout.on('data', (chunk) => { stdout += chunk.toString(); });
        child.stderr.on('data', (chunk) => { stderr += chunk.toString(); });

        child.once('error', (err) => {
            finish({ status: 'fail', detail: `Worker smoke: gateway spawn error: ${err.message}` });
        });

        child.once('exit', (code) => {
            if (code === 0) {
                finish({ status: 'pass', detail: 'Worker smoke: gateway exited cleanly during observation window.' });
            } else {
                const preview = sanitizeOperationalMessage((stderr || stdout || '').trim(), '');
                finish({
                    status: 'fail',
                    detail: preview
                        ? `Worker smoke: gateway crashed (exit ${code}): ${preview}`
                        : `Worker smoke: gateway crashed (exit ${code}) with no stderr. Run \`genexus-mcp doctor --mcp-smoke\` for details.`
                });
            }
        });

        setTimeout(() => {
            try { child.kill(); } catch { }
            // Still alive after observeMs → worker bootstrapped without crashing.
            finish({ status: 'pass', detail: `Worker smoke: gateway stayed alive for ${observeMs}ms with KB and GX configured.` });
        }, observeMs);
    });
}

async function handleWhoami(options, ctx) {
    const data = buildStatusData(ctx.cwd);

    if (!data.configFound) {
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    connected: false,
                    reason: 'No GeneXus MCP configuration was found in the current context.'
                },
                help: [
                    'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to configure.',
                    'Or run `genexus-mcp init --interactive` for a guided setup.'
                ]
            }
        };
    }

    const kbPath = data.kbPath;
    const gxPath = data.gxPath;
    const kbName = kbPath ? path.basename(kbPath) : null;
    const kbExists = !!(kbPath && fs.existsSync(kbPath));
    const kbValid = data.kbLooksValid;
    const gxVersion = readGeneXusVersionFromInstall(gxPath);

    const ok = {
        connected: true,
        kb: {
            name: kbName,
            path: kbPath,
            exists: kbExists,
            looksValid: kbValid
        },
        geneXus: {
            installationPath: gxPath,
            version: gxVersion
        },
        config: {
            path: data.configPath,
            source: data.configSource
        }
    };

    const help = [];
    if (!kbExists) help.push('Configured KB path does not exist on disk.');
    if (kbExists && !kbValid) help.push('KB path exists but does not look like a GeneXus KB (no `.gxw` or `KnowledgeBase.Connection`).');
    if (!gxVersion) help.push('Could not read GeneXus version from installation folder (no version.txt detected).');

    return { exitCode: ctx.EXIT_CODES.OK, envelope: { ok, help } };
}

async function promptYesNo(question) {
    return new Promise((resolve) => {
        const rl = readline.createInterface({ input: process.stdin, output: process.stderr });
        rl.question(`${question} [y/N]: `, (answer) => {
            rl.close();
            const trimmed = (answer || '').trim().toLowerCase();
            resolve(trimmed === 'y' || trimmed === 'yes');
        });
    });
}

async function handleUninstall(options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const cacheDir = getLocalAppDataCacheDir();
    const cacheDirExists = !!(cacheDir && fs.existsSync(cacheDir));
    const localConfigPath = data.configPath;
    const hasLocalConfig = !!localConfigPath && fs.existsSync(localConfigPath);

    const plan = {
        clientEntries: 'Remove `mcpServers.genexus` from any detected AI client config.',
        cacheDir: cacheDirExists ? `Delete ${cacheDir}` : 'No cache directory present.',
        localConfig: hasLocalConfig ? `Delete ${localConfigPath}` : 'No local config.json detected.'
    };

    if (!options.yes) {
        process.stderr.write('\n[genexus-mcp uninstall] The following actions will be performed:\n');
        process.stderr.write(`  - ${plan.clientEntries}\n`);
        process.stderr.write(`  - ${plan.cacheDir}\n`);
        process.stderr.write(`  - ${plan.localConfig}\n\n`);
        const confirmed = await promptYesNo('Proceed?');
        if (!confirmed) {
            return {
                exitCode: ctx.EXIT_CODES.OK,
                envelope: {
                    ok: { action: 'uninstall', cancelled: true, plan },
                    help: ['Pass --yes to skip the interactive confirmation.']
                }
            };
        }
    }

    const unpatch = unpatchClientConfig();

    let cacheRemoved = false;
    let cacheError = null;
    if (cacheDirExists) {
        try {
            fs.rmSync(cacheDir, { recursive: true, force: true });
            cacheRemoved = true;
        } catch (err) {
            cacheError = err && err.message ? err.message : 'Unknown error removing cache dir';
        }
    }

    let configRemoved = false;
    let configError = null;
    if (hasLocalConfig) {
        try {
            fs.unlinkSync(localConfigPath);
            configRemoved = true;
        } catch (err) {
            configError = err && err.message ? err.message : 'Unknown error removing config.json';
        }
    }

    const help = [];
    if (cacheError) help.push(`Cache removal failed: ${cacheError}`);
    if (configError) help.push(`Local config removal failed: ${configError}`);
    if (unpatch.failed.length > 0) help.push(`Some client configs could not be updated (see meta.failedClients).`);
    if (unpatch.removed.length > 0) help.push('Restart your AI clients to release any stale MCP connections.');

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                action: 'uninstall',
                cancelled: false,
                removedClients: unpatch.removed,
                cacheRemoved,
                cacheDir: cacheDir || null,
                configRemoved,
                configPath: localConfigPath || null
            },
            help,
            meta: {
                skippedClients: unpatch.skipped,
                failedClients: unpatch.failed
            }
        }
    };
}

async function handleClients(subcommand, options, ctx) {
    const sub = subcommand || 'list';
    const serverName = options.serverName || DEFAULT_MCP_SERVER_NAME;

    if (sub === 'list') {
        const rows = clientsStatus({ serverName });
        const installedCount = rows.filter((r) => r.installed).length;
        const registeredCount = rows.filter((r) => r.registered).length;
        const help = [];
        const installedUnregistered = rows.filter((r) => r.installed && !r.registered && r.writeSupported);
        if (installedUnregistered.length > 0) {
            help.push(`Register installed-but-unregistered agents: genexus-mcp clients add --clients ${installedUnregistered.map((r) => r.id).join(',')}${serverName !== DEFAULT_MCP_SERVER_NAME ? ` --server-name ${serverName}` : ''}`);
        }
        const stale = rows.filter((r) => r.commandStale);
        if (stale.length > 0) {
            help.push(`These clients point at a missing gateway exe (will fail to connect) — re-register: genexus-mcp clients add --clients ${stale.map((r) => r.id).join(',')}${serverName !== DEFAULT_MCP_SERVER_NAME ? ` --server-name ${serverName}` : ''}`);
        }
        for (const r of rows) {
            if (r.installed && !r.writeSupported && r.note) help.push(`${r.name}: ${r.note}`);
        }
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    clients: rows,
                    summary: { total: rows.length, installed: installedCount, registered: registeredCount, serverName }
                },
                help
            }
        };
    }

    if (sub === 'add' || sub === 'remove') {
        const ids = resolveClientIds(options);
        if (sub === 'add' && (!ids || ids.length === 0)) {
            return {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope('`clients add` requires --clients <csv> (e.g. --clients antigravity,vscode).', ctx.EXIT_CODES.USAGE)
            };
        }
        const validation = validateClientIds(ids);
        if (!validation.ok) {
            return { exitCode: ctx.EXIT_CODES.USAGE, envelope: usageEnvelope(validation.message, ctx.EXIT_CODES.USAGE) };
        }

        if (sub === 'add') {
            const configPath = resolveConfigPathNoMutate(ctx.cwd);
            if (!configPath) {
                return {
                    exitCode: ctx.EXIT_CODES.ERROR,
                    envelope: operationalErrorEnvelope(
                        'No config.json found to point the clients at. Run `genexus-mcp init` first (or run from a KB folder).',
                        ctx.EXIT_CODES.ERROR
                    )
                };
            }
            let patch;
            try {
                // Explicit add: write even if install markers are absent (the user asked for it).
                patch = patchClientConfig(configPath, {
                    ids,
                    onlyExisting: false,
                    serverName,
                    force: options.force
                });
            } catch (err) {
                return {
                    exitCode: ctx.EXIT_CODES.ERROR,
                    envelope: operationalErrorEnvelope(
                        sanitizeOperationalMessage(`Client registration failed: ${err && err.message ? err.message : 'unknown error'}`),
                        ctx.EXIT_CODES.ERROR
                    )
                };
            }
            const help = [];
            if (patch.patched.length > 0) help.push('Restart the affected AI client(s) to load the new MCP config.');
            if (patch.skipped.some((entry) => entry.registrationMode === 'manual')) {
                help.push('Some selected clients require manual setup; use meta.skippedClients[].manualSetup for the exact command and environment.');
            }
            if (patch.failed.some((f) => f.reason && f.reason.includes('third-party or HTTP MCP server'))) {
                help.push('A collision with an existing MCP server (e.g. official GeneXus MCP) was detected.');
                help.push('Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or pass --force.');
            } else if (patch.failed.length > 0) {
                help.push('Some clients failed (see meta.failedClients).');
            }
            return {
                exitCode: ctx.EXIT_CODES.OK,
                envelope: {
                    ok: { action: 'clients.add', configPath, serverName, patchedClients: patch.patched, patchedCount: patch.patched.length },
                    help,
                    meta: { failedClients: patch.failed, skippedClients: patch.skipped }
                }
            };
        }

        // remove
        const unpatch = unpatchClientConfig({
            ...(ids ? { ids } : {}),
            serverName
        });
        const help = [];
        if (unpatch.removed.length > 0) help.push('Restart the affected AI client(s) to drop the stale MCP connection.');
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: { action: 'clients.remove', serverName, removedClients: unpatch.removed, removedCount: unpatch.removed.length },
                help,
                meta: { skippedClients: unpatch.skipped, failedClients: unpatch.failed }
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.USAGE,
        envelope: usageEnvelope('clients supports subcommands `list`, `add`, `remove`.', ctx.EXIT_CODES.USAGE)
    };
}

async function handleKb(subcommand, options, ctx) {
    const data = buildStatusData(ctx.cwd);
    if (!data.configPath) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(
                'No GeneXus MCP config.json found. Run `genexus-mcp init` first.',
                ctx.EXIT_CODES.ERROR,
                ['Run `genexus-mcp init --kb "<path>" --gx "<path>"` to create one.']
            )
        };
    }

    const configPath = data.configPath;

    if (subcommand === 'list') {
        const catalog = readKbCatalog(configPath);
        const entries = Object.entries(catalog.kbs).map(([name, p]) => ({
            name,
            path: p,
            active: name === catalog.activeKb,
            exists: fs.existsSync(p)
        }));
        if (entries.length === 0 && catalog.kbPath) {
            entries.push({ name: '(legacy)', path: catalog.kbPath, active: true, exists: fs.existsSync(catalog.kbPath) });
        }
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    activeKb: catalog.activeKb,
                    kbs: entries,
                    returned: entries.length,
                    total: entries.length
                },
                help: entries.length === 0
                    ? ['No KBs registered. Run `genexus-mcp kb add --name <name> --kb <path>` to register one.']
                    : []
            }
        };
    }

    if (subcommand === 'add') {
        if (!options.name || !options.kb) {
            return {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope('`kb add` requires --name and --kb.', ctx.EXIT_CODES.USAGE)
            };
        }
        const catalog = addKbToConfig(configPath, options.name, options.kb);
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'kb.add',
                    name: options.name,
                    path: options.kb,
                    activeKb: catalog.activeKb,
                    registeredCount: Object.keys(catalog.kbs).length
                },
                help: catalog.activeKb === options.name
                    ? ['Restart your AI client to pick up the new active KB.']
                    : [`KB registered. Run \`genexus-mcp kb switch --name ${options.name}\` to make it active.`]
            }
        };
    }

    if (subcommand === 'remove') {
        if (!options.name) {
            return {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope('`kb remove` requires --name.', ctx.EXIT_CODES.USAGE)
            };
        }
        const { catalog, removed } = removeKbFromConfig(configPath, options.name);
        if (!removed) {
            return {
                exitCode: ctx.EXIT_CODES.OK,
                envelope: {
                    ok: { action: 'kb.remove', name: options.name, removed: false },
                    help: [`KB '${options.name}' was not registered. Run \`genexus-mcp kb list\` to see available names.`]
                }
            };
        }
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'kb.remove',
                    name: options.name,
                    removed: true,
                    activeKb: catalog.activeKb,
                    registeredCount: Object.keys(catalog.kbs).length
                },
                help: ['Restart your AI client to apply changes.']
            }
        };
    }

    if (subcommand === 'switch') {
        const result = switchActiveKb(configPath, { name: options.name, path: options.kb });
        if (!result.ok) {
            return {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope(result.reason, ctx.EXIT_CODES.USAGE)
            };
        }
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'kb.switch',
                    activeKb: result.switchedTo.name,
                    kbPath: result.switchedTo.path
                },
                help: [
                    'Restart your AI client (or run `genexus_lifecycle action=stop-worker` via MCP) so the worker reloads with the new KB.'
                ]
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.USAGE,
        envelope: usageEnvelope(
            `Unknown kb subcommand '${subcommand}'. Use list, add, remove, or switch.`,
            ctx.EXIT_CODES.USAGE
        )
    };
}

function commandHelpMap() {
    return {
        axi: {
            usage: 'genexus-mcp axi home [--format toon|json|text]',
            examples: ['genexus-mcp axi home', 'genexus-mcp axi home --format json']
        },
        home: {
            usage: 'genexus-mcp home [--format toon|json|text] OR genexus-mcp axi home [--format toon|json|text]',
            examples: ['genexus-mcp home', 'genexus-mcp axi home --format json']
        },
        status: {
            usage: 'genexus-mcp status [--full] [--format toon|json|text] [--quiet] [--no-color]',
            examples: ['genexus-mcp status', 'genexus-mcp status --full --format json']
        },
        doctor: {
            usage: 'genexus-mcp doctor [--full] [--mcp-smoke] [--dump] [--fields f1,f2] [--limit N] [--format toon|json|text]',
            examples: ['genexus-mcp doctor', 'genexus-mcp doctor --full --mcp-smoke --format json', 'genexus-mcp doctor --dump   # build a support bundle zip']
        },
        tools: {
            usage: 'genexus-mcp tools list [--query text] [--fields f1,f2] [--limit N] [--full] [--format ...]',
            examples: ['genexus-mcp tools list', 'genexus-mcp tools list --query read --fields name,category --format json']
        },
        config: {
            usage: 'genexus-mcp config show [--full] [--fields f1,f2] [--format ...]',
            examples: ['genexus-mcp config show', 'genexus-mcp config show --full --format json']
        },
        init: {
            usage: 'genexus-mcp init [--kb <path>] [--gx <path>] [--server-name <name>] [--force] [--no-write-clients] [--clients <csv>] [--all-clients] [--no-smoke] [--warm] [--format ...] OR genexus-mcp init --interactive',
            examples: [
                'genexus-mcp init   # zero-config: auto-discovers GX + KB, and registers detected AI clients',
                'genexus-mcp init --kb "C:\\KBs\\MyKB" --gx "C:\\Program Files (x86)\\GeneXus\\GeneXus18"',
                'genexus-mcp init --server-name Gx18byLennix   # register with custom MCP server key (coexists with official GeneXus MCP)',
                'genexus-mcp init --interactive   # prompts per detected agent (Claude Desktop/Code, Gemini CLI, Cursor, Codex CLI, OpenCode, ...)',
                'genexus-mcp init --kb <path> --gx <path> --clients claude-code,gemini-cli,cursor   # register only these',
                'genexus-mcp init --kb <path> --gx <path> --no-write-clients   # write config.json only; register clients yourself',
                'genexus-mcp init --kb <path> --gx <path> --all-clients   # register every known client, installed or not'
            ]
        },
        whoami: {
            usage: 'genexus-mcp whoami [--format toon|json|text]',
            examples: ['genexus-mcp whoami', 'genexus-mcp whoami --format json']
        },
        uninstall: {
            usage: 'genexus-mcp uninstall [--yes] [--format toon|json|text]',
            examples: ['genexus-mcp uninstall', 'genexus-mcp uninstall --yes --format json']
        },
        kb: {
            usage: 'genexus-mcp kb <list|add|remove|switch> [--name <name>] [--kb <path>] [--format ...]',
            examples: [
                'genexus-mcp kb list',
                'genexus-mcp kb add --name sales --kb "C:\\KBs\\SalesProd"',
                'genexus-mcp kb switch --name sales',
                'genexus-mcp kb switch --kb "C:\\KBs\\NewKB"   # auto-registers by folder name',
                'genexus-mcp kb remove --name sales'
            ]
        },
        clients: {
            usage: 'genexus-mcp clients [list] [--server-name <name>] [--format ...] OR genexus-mcp clients add --clients <csv> [--server-name <name>] [--force] OR genexus-mcp clients remove [--clients <csv>] [--server-name <name>]',
            examples: [
                'genexus-mcp clients              # show every AI agent: installed? registered? where?',
                'genexus-mcp clients --format json',
                'genexus-mcp clients add --clients antigravity,vscode',
                'genexus-mcp clients add --clients cursor --server-name Gx18byLennix',
                'genexus-mcp clients remove --clients cursor',
                'genexus-mcp clients remove --clients cursor --server-name Gx18byLennix'
            ]
        },
        llm: {
            usage: 'genexus-mcp llm help [--full] [--fields f1,f2] [--format toon|json|text]',
            examples: ['genexus-mcp llm help --format json', 'genexus-mcp llm help --full --format json']
        },
        update: {
            usage: 'genexus-mcp update [--apply] [--yes] [--channel latest|next] [--format toon|json|text]',
            examples: [
                'genexus-mcp update                  # check; reports your install method + the right upgrade step',
                'genexus-mcp update --apply          # perform the upgrade for your install method (confirms first)',
                'genexus-mcp update --apply --yes    # unattended (CI/automation)',
                'genexus-mcp update --channel next   # check the @next dist-tag'
            ]
        },
        layout: {
            usage: 'genexus-mcp layout status [--title "GeneXus"] [--format ...] OR genexus-mcp layout run --action <focus|activate-layout|activate-tab|send-keys|type-text|click> [--tab "Layout"] [--keys "..."] [--text "..."] [--x N --y N] [--title "..."] [--format ...] OR genexus-mcp layout inspect [--tab "Layout"] [--limit N] [--full] [--title "..."] [--format ...]',
            examples: ['genexus-mcp layout status --format json', 'genexus-mcp layout run --action activate-tab --tab "Layout" --format json', 'genexus-mcp layout inspect --tab Layout --format json']
        }
    };
}

function collapseHome(absPath) {
    const home = require('os').homedir();
    if (!absPath || !home) return absPath;
    if (absPath.toLowerCase().startsWith(home.toLowerCase())) {
        return `~${absPath.slice(home.length)}`;
    }
    return absPath;
}

async function handleHome(_options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const binPath = collapseHome(process.argv[1] || process.execPath);
    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                bin: binPath,
                description: 'GeneXus MCP launcher and AXI-oriented utility CLI',
                ready: data.ready,
                next: data.ready
                    ? ['genexus-mcp status', 'genexus-mcp clients', 'genexus-mcp doctor --mcp-smoke', 'genexus-mcp tools list --limit 10', 'genexus-mcp layout status']
                    : ['genexus-mcp status', 'genexus-mcp clients', 'genexus-mcp doctor --full', 'genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"']
            },
            help: []
        }
    };
}

async function handleHelp(targetCommand, ctx) {
    const binPath = collapseHome(process.argv[1] || process.execPath);
    const map = commandHelpMap();
    if (targetCommand && map[targetCommand]) {
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    bin: binPath,
                    command: targetCommand,
                    usage: map[targetCommand].usage,
                    examples: map[targetCommand].examples,
                    defaults: {
                        format: 'toon',
                        limit: 100
                    }
                },
                help: []
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                bin: binPath,
                command: 'genexus-mcp',
                description: 'GeneXus MCP launcher and AXI-oriented utility CLI',
                commands: ['home', 'axi home', 'status', 'doctor', 'tools list', 'config show', 'layout status', 'layout run', 'layout inspect', 'init', 'whoami', 'uninstall', 'kb list', 'kb add', 'kb remove', 'kb switch', 'llm help', 'update', 'help'],
                defaults: { format: 'toon', limit: 100 }
            },
            help: [
                'Run `genexus-mcp <command> --help` for subcommand help.',
                'Without AXI subcommands, CLI works as MCP launcher passthrough.'
            ]
        }
    };
}

function tryReadLlmPlaybookMarkdown() {
    const candidates = [
        path.join(__dirname, '..', '..', 'docs', 'llm_cli_mcp_playbook.md'),
        path.join(process.cwd(), 'docs', 'llm_cli_mcp_playbook.md')
    ];

    for (const candidate of candidates) {
        try {
            if (fs.existsSync(candidate)) {
                return fs.readFileSync(candidate, 'utf8');
            }
        } catch {
        }
    }

    return null;
}

async function handleLlmHelp(options, ctx) {
    const allowedFields = ['objective', 'interfaceSelection', 'cli', 'mcp', 'timeouts', 'bestPractices', 'examples', 'resources'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'llm help', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }

    const payload = {
        objective: 'Use AXI CLI for environment/bootstrap checks and MCP for KB operations with deterministic, token-efficient flows.',
        interfaceSelection: {
            cli: ['home', 'status', 'doctor --mcp-smoke', 'tools list', 'config show'],
            mcp: ['genexus_query', 'genexus_list_objects', 'genexus_read', 'genexus_edit', 'genexus_lifecycle']
        },
        cli: {
            parseStdoutOnly: true,
            expectedMeta: ['schemaVersion=axi-cli/1', 'command=<normalized-command>'],
            exitCodes: { ok: 0, error: 1, usage: 2 }
        },
        mcp: {
            parsePath: 'result.content[0].text',
            expectedMeta: ['_meta.schemaVersion=mcp-axi/2', '_meta.tool=<tool-name>'],
            listHelpers: ['returned', 'total', 'empty', 'hasMore', 'nextOffset'],
            shaping: ['fields=<csv|array>', 'axiCompact=true (query/list_objects)']
        },
        timeouts: {
            rule: 'If result.isError=true and operationId is present, treat as running operation, not terminal failure.',
            followUp: [
                "genexus_lifecycle(action='status', target='op:<operationId>')",
                "genexus_lifecycle(action='result', target='op:<operationId>')"
            ]
        },
        bestPractices: [
            'Always set limit/offset for list and read flows.',
            'Prefer parentPath over parent for disambiguation.',
            'Use patch mode dryRun before persistent edits.',
            'Prefer batch_read for multi-object context gathering.'
        ],
        examples: [
            'genexus-mcp home --format json',
            'genexus-mcp doctor --mcp-smoke --format json',
            "tools/call genexus_list_objects { parentPath, limit, offset, axiCompact:true }",
            "tools/call genexus_query { query:'@quick', limit:20, fields:'name,type,path' }"
        ],
        resources: ['genexus://kb/llm-playbook', 'genexus://kb/agent-playbook', 'prompt: gx_bootstrap_llm']
    };

    const selectedFields = fieldSelection.selectedFields || null;
    const ok = selectedFields ? pickFields(payload, selectedFields) : payload;
    const envelope = {
        ok,
        help: [
            'Use `genexus://kb/llm-playbook` through MCP resources/read for protocol-native guidance.',
            'Use `genexus-mcp llm help --full --format json` for embedded markdown when available.'
        ]
    };

    if (options.full) {
        const markdown = tryReadLlmPlaybookMarkdown();
        if (markdown) {
            envelope.ok.markdown = markdown;
        }
    }

    return { exitCode: ctx.EXIT_CODES.OK, envelope };
}

module.exports = {
    parseFieldSelection,
    pickFields,
    handleStatus,
    handleDoctor,
    handleToolsList,
    handleConfigShow,
    handleInit,
    handleWhoami,
    handleUninstall,
    handleKb,
    handleClients,
    handleHome,
    handleLlmHelp,
    handleLayout,
    handleHelp,
    usageEnvelope,
    operationalErrorEnvelope,
    commandHelpMap
};
