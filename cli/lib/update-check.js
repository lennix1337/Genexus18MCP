const fs = require('fs');
const os = require('os');
const path = require('path');
const https = require('https');
const { spawn } = require('child_process');
const {
    getGatewayExePath,
    clientsStatus,
    normalizeExePath
} = require('./config');

const REPO = 'lennix1337/Genexus18MCP';
const NPM_PACKAGE = 'genexus-mcp';
const CACHE_TTL_MS = 24 * 60 * 60 * 1000;
const FETCH_TIMEOUT_MS = 2500;
const INSTALL_ONE_LINER = 'iex (irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1)';

function getPackageVersion() {
    try {
        const pkg = require('../../package.json');
        return typeof pkg.version === 'string' ? pkg.version : null;
    } catch {
        return null;
    }
}

function getCacheFile() {
    // Share the cache with the gateway (UpdateNotifier.cs) so a check by either
    // side serves the other. On Windows that's %LOCALAPPDATA%\GenexusMCP; on other
    // platforms fall back to ~/.genexus-mcp (the gateway is Windows-only).
    if (process.platform === 'win32') {
        const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
        return path.join(base, 'GenexusMCP', 'update-check.json');
    }
    return path.join(os.homedir(), '.genexus-mcp', 'update-check.json');
}

function releaseUrlForVersion(version) {
    const v = stripV(version);
    return v ? `https://github.com/${REPO}/releases/tag/v${v}` : null;
}

function readCache() {
    try {
        const raw = fs.readFileSync(getCacheFile(), 'utf8');
        const data = JSON.parse(raw);
        if (data && typeof data === 'object'
            && (data.package === undefined || data.package === NPM_PACKAGE)
            && (data.channel === undefined || validateChannel(data.channel))
            && parseSemver(data.latestVersion)) return data;
    } catch {
    }
    return null;
}

function writeCache(data) {
    try {
        const file = getCacheFile();
        fs.mkdirSync(path.dirname(file), { recursive: true });
        const tmp = `${file}.tmp-${process.pid}-${Date.now()}`;
        fs.writeFileSync(tmp, JSON.stringify({ package: NPM_PACKAGE, channel: 'latest', ...data }), 'utf8');
        fs.renameSync(tmp, file);
    } catch {
    }
}

function stripV(v) {
    return typeof v === 'string' ? v.replace(/^v/i, '').trim() : '';
}

function parseSemver(v) {
    const s = stripV(v);
    const m = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/.exec(s);
    if (!m) return null;
    return { valid: true, major: Number(m[1]), minor: Number(m[2]), patch: Number(m[3]),
        prerelease: m[4] ? m[4].split('.') : [], build: m[5] ? m[5].split('.') : [] };
}

function compareSemver(a, b) {
    const pa = parseSemver(a);
    const pb = parseSemver(b);
    if (!pa || !pb) return null;
    for (const key of ['major', 'minor', 'patch']) {
        if (pa[key] > pb[key]) return 1;
        if (pa[key] < pb[key]) return -1;
    }
    if (pa.prerelease.length === 0 && pb.prerelease.length > 0) return 1;
    if (pa.prerelease.length > 0 && pb.prerelease.length === 0) return -1;
    for (let i = 0; i < Math.max(pa.prerelease.length, pb.prerelease.length); i += 1) {
        if (i >= pa.prerelease.length) return -1;
        if (i >= pb.prerelease.length) return 1;
        const left = pa.prerelease[i];
        const right = pb.prerelease[i];
        if (left === right) continue;
        const leftNum = /^\d+$/.test(left);
        const rightNum = /^\d+$/.test(right);
        if (leftNum && rightNum) return Number(left) > Number(right) ? 1 : -1;
        if (leftNum !== rightNum) return leftNum ? -1 : 1;
        return left > right ? 1 : -1;
    }
    return 0;
}

function validateChannel(channel) {
    const value = typeof channel === 'string' ? channel.trim() : '';
    return /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(value) ? value : null;
}

function httpGetJson(url) {
    return new Promise((resolve) => {
        let req;
        try {
            req = https.request(url, { method: 'GET', headers: { 'User-Agent': `${NPM_PACKAGE}-cli`, Accept: 'application/json' } }, (res) => {
                if (res.statusCode !== 200) { res.resume(); resolve(null); return; }
                let body = '';
                res.setEncoding('utf8');
                res.on('data', (c) => { body += c; });
                res.on('end', () => {
                    try { resolve(JSON.parse(body)); } catch { resolve(null); }
                });
            });
        } catch {
            resolve(null);
            return;
        }
        req.on('error', () => resolve(null));
        req.setTimeout(FETCH_TIMEOUT_MS, () => { req.destroy(); resolve(null); });
        req.end();
        if (typeof req.unref === 'function') req.unref();
    });
}

// Resolve the latest version for a dist-tag channel. Authority is the npm
// registry — that's exactly what `npm install -g <pkg>@<channel>` resolves, so
// we never advertise a version npm can't install yet (the GitHub-release-before-
// npm-publish window) and we work on networks that allow npm but block
// api.github.com. Falls back to the GitHub releases API only for the default
// channel. The release URL is derived from the version (no API call needed).
async function fetchLatestRelease(opts = {}) {
    const channel = validateChannel((opts && opts.channel) || 'latest');
    if (!channel) return null;

    // 1. npm registry dist-tags (lightweight: just the tag → version map).
    const tags = await httpGetJson(`https://registry.npmjs.org/-/package/${NPM_PACKAGE}/dist-tags`);
    if (tags && typeof tags === 'object') {
        const v = stripV(tags[channel] || '');
        if (parseSemver(v)) return { latestVersion: v, releaseUrl: releaseUrlForVersion(v), source: 'npm' };
        // Channel not found on npm — for non-default channels, that's a definitive "no".
        if (channel !== 'latest') return null;
    }

    // 2. GitHub releases fallback (default channel only).
    if (channel === 'latest') {
        const rel = await httpGetJson(`https://api.github.com/repos/${REPO}/releases/latest`);
        if (rel && typeof rel === 'object') {
            const tag = stripV(rel.tag_name || '');
            if (parseSemver(tag)) {
                const url = typeof rel.html_url === 'string' ? rel.html_url : releaseUrlForVersion(tag);
                return { latestVersion: tag, releaseUrl: url, source: 'github' };
            }
        }
    }
    return null;
}

function formatBanner(current, latest, releaseUrl) {
    const lines = [
        `[genexus-mcp] update available: v${current} -> v${latest}`,
        `[genexus-mcp] run: npm install -g ${NPM_PACKAGE}@latest`
    ];
    if (releaseUrl) lines.push(`[genexus-mcp] release: ${releaseUrl}`);
    return lines.join('\n') + '\n';
}

function isDisabled(opts) {
    if (process.env.GENEXUS_MCP_NO_UPDATE_CHECK === '1') return true;
    if (opts && opts.quiet) return true;
    if (!process.stderr || !process.stderr.isTTY) return true;
    return false;
}

function maybePrintCachedBanner(_opts) {
    const current = getPackageVersion();
    if (!current) return;
    const cache = readCache();
    if (!cache || !cache.latestVersion || (cache.channel && cache.channel !== 'latest')) return;
    if (compareSemver(cache.latestVersion, current) > 0) {
        try {
            process.stderr.write(formatBanner(current, cache.latestVersion, cache.releaseUrl || null));
        } catch {
        }
    }
}

function scheduleBackgroundFetch() {
    const cache = readCache();
    const now = Date.now();
    if (cache && typeof cache.checkedAt === 'number' && (now - cache.checkedAt) < CACHE_TTL_MS) {
        return;
    }

    fetchLatestRelease().then((result) => {
        if (!result) return;
        writeCache({
            checkedAt: Date.now(),
            latestVersion: result.latestVersion,
            releaseUrl: result.releaseUrl,
            source: result.source || null
        });
    }).catch(() => {});
}

function startBackgroundUpdateCheck(opts) {
    if (isDisabled(opts)) return;
    maybePrintCachedBanner(opts);
    scheduleBackgroundFetch();
}

// Drift = a client points at a gateway launcher (exe/bat/...) that is NOT this
// npm package's gateway, so `npm install -g` won't update it. Reuses the
// broadened command resolution from config.js rather than re-implementing the
// (formerly .exe-only) match.
function detectClientExeDrift() {
    try {
        const packageNorm = normalizeExePath(getGatewayExePath());
        const mismatches = [];
        for (const c of clientsStatus()) {
            if (!c.registered || !c.command) continue;
            const cmd = c.command;
            // npx/node/genexus-mcp shims resolve via npm at runtime — not drift.
            if (/(^|[\\/])(npx|npx\.cmd|node|node\.exe|genexus-mcp|genexus-mcp\.cmd)$/i.test(cmd)) continue;
            // Only an explicit-path launcher can "drift" from the package exe.
            if (!/[\\/]/.test(cmd)) continue;
            if (normalizeExePath(cmd) !== packageNorm) {
                mismatches.push({ client: c.name, configured: cmd });
            }
        }
        return mismatches;
    } catch {
        return [];
    }
}

// How is the gateway actually launched by the registered clients? That decides
// the right upgrade action (npx@latest auto-updates on restart; npm-global needs
// `npm i -g`; a fixed exe path needs the installer). Returns the dominant method
// plus per-method evidence.
function detectInstallMethod() {
    // Corporate/fixed-path env override wins — the gateway runs from a pinned exe.
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) {
        return { method: 'fixed-path', detail: process.env.GENEXUS_MCP_GATEWAY_EXE, evidence: [] };
    }
    const counts = { 'npx-latest': 0, 'npm-global': 0, 'fixed-path': 0, 'package-direct': 0 };
    const evidence = [];
    try {
        for (const c of clientsStatus()) {
            if (!c.registered || !c.command) continue;
            const cmd = String(c.command);
            let m;
            if (/(^|[\\/])npx(\.cmd)?$/i.test(cmd)) m = 'npx-latest';
            else if (/(^|[\\/])genexus-mcp(\.cmd)?$/i.test(cmd)) m = 'npm-global';
            else if (c.id === 'antigravity' && /GxMcp\.Gateway\.exe$/i.test(cmd)) m = 'package-direct';
            else if (/[\\/]/.test(cmd)) m = 'fixed-path';
            else m = 'npm-global';
            counts[m] = (counts[m] || 0) + 1;
            evidence.push({ client: c.name, method: m, command: cmd });
        }
    } catch { /* ignore */ }
    // Pick the most common; default to npx-latest (the recommended/auto path).
    let method = 'npx-latest';
    let best = -1;
    for (const k of Object.keys(counts)) {
        if (counts[k] > best) { best = counts[k]; method = k; }
    }
    if (best <= 0) method = 'npx-latest';
    return { method, counts, evidence };
}

// The method-appropriate upgrade plan. `auto` means no manual install step is
// needed (the npx launcher fetches @latest on the next client start).
function upgradePlanFor(method, channel) {
    const resolvedChannel = channel || 'latest';
    const tag = resolvedChannel !== 'latest' ? `@${resolvedChannel}` : '@latest';
    if (method === 'npx-latest') {
        return {
            method,
            channel: resolvedChannel,
            auto: true,
            steps: [
                `Your clients launch via \`npx ${NPM_PACKAGE}${tag}\`, which fetches the newest ${resolvedChannel} version on each start.`,
                'Just fully restart your AI client — it will pick up the selected channel automatically.'
            ],
            applyCommand: null,
            restartRequired: true
        };
    }
    if (method === 'fixed-path') {
        return {
            method,
            channel: resolvedChannel,
            auto: false,
            steps: [
                `Your install runs the gateway from a fixed path (corporate install); npm ${tag} will not update that artifact.`,
                `Re-run the fixed-path installer for the resolved ${resolvedChannel} release artifact${resolvedChannel === 'latest' ? '' : ` from channel \`${resolvedChannel}\``}: ${INSTALL_ONE_LINER}`,
                'Then fully restart your AI client.'
            ],
            applyCommand: null, // self-stage is a future enhancement; installer is the path
            restartRequired: true
        };
    }
    if (method === 'package-direct') {
        const tag = channel && channel !== 'latest' ? `@${channel}` : '@latest';
        return {
            method,
            channel: resolvedChannel,
            auto: false,
            steps: [
                'Antigravity launches the gateway executable bundled with the npm package, so each MCP handshake skips npx.',
                `Refresh its pinned package path with: npx -y ${NPM_PACKAGE}${tag} clients add --clients antigravity`,
                'Then fully restart Antigravity.'
            ],
            applyCommand: null,
            restartRequired: true
        };
    }
    // npm-global
    return {
        method: 'npm-global',
        channel: resolvedChannel,
        auto: false,
        steps: [
            `Run: npm install -g ${NPM_PACKAGE}${tag}`,
            'Then fully restart your AI client.'
        ],
        applyCommand: { exe: process.platform === 'win32' ? 'npm.cmd' : 'npm', args: ['install', '-g', `${NPM_PACKAGE}${tag}`] },
        restartRequired: true
    };
}

function runCommand(exe, args, options = {}) {
    return new Promise((resolve) => {
        let child;
        let settled = false;
        const finish = (result) => { if (!settled) { settled = true; resolve(result); } };
        try {
            child = spawn(exe, args, { stdio: 'inherit', windowsHide: true });
        } catch (err) {
            finish({ ok: false, code: null, error: err && err.message ? err.message : 'spawn failed' });
            return;
        }
        let timedOut = false;
        const timeoutMs = Number.isFinite(options.timeoutMs) ? Math.max(1, options.timeoutMs) : 120000;
        const timer = setTimeout(() => {
            timedOut = true;
            try { child.kill(); } catch { /* process may have exited */ }
        }, timeoutMs);
        child.on('error', (err) => { clearTimeout(timer); finish({ ok: false, code: null, error: err && err.message ? err.message : 'spawn failed', timedOut }); });
        child.on('exit', (code, signal) => {
            clearTimeout(timer);
            finish({ ok: !timedOut && code === 0, code: timedOut ? -1 : code, signal: signal || null, timedOut });
        });
    });
}

async function handleUpdate(options, ctx) {
    const opts = options || {};
    const channel = validateChannel(opts.channel || 'latest');
    if (!channel) {
        return { exitCode: ctx.EXIT_CODES.USAGE, envelope: { error: { code: 'invalid_channel', message: 'Channel must contain only letters, numbers, dots, underscores, or hyphens.' } } };
    }
    const current = getPackageVersion();
    const result = await fetchLatestRelease({ channel });
    const mismatches = detectClientExeDrift();
    const install = detectInstallMethod();

    const driftHelp = mismatches.length
        ? [`WARNING: ${mismatches.length} AI client(s) point at a gateway launcher that is NOT this npm package — updating npm will NOT update them. Mismatches: ${mismatches.map((m) => `${m.client} -> ${m.configured}`).join('; ')}. Re-run scripts/install.ps1 (or genexus-mcp clients add) to resync.`]
        : [];

    if (!result) {
        const reason = channel !== 'latest'
            ? `No '${channel}' version found — the npm dist-tag '${channel}' is absent (or the registry is unreachable).`
            : 'Could not resolve the latest version (npm registry + GitHub both unreachable). Check connectivity/proxy or retry later.';
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: { current, latest: null, channel, updateAvailable: false, fetched: false, installMethod: install.method, clientDrift: mismatches },
                help: [reason, ...driftHelp]
            }
        };
    }

    writeCache({ checkedAt: Date.now(), latestVersion: result.latestVersion, releaseUrl: result.releaseUrl, source: result.source || null, channel });

    const currentSemver = parseSemver(current || '0.0.0');
    const latestSemver = parseSemver(result.latestVersion);
    if (!latestSemver || !currentSemver) {
        return { exitCode: ctx.EXIT_CODES.OK, envelope: { ok: { current, latest: result.latestVersion, channel, updateAvailable: false, invalidVersion: true }, help: ['Could not compare versions because the installed or published version is invalid semver.'] } };
    }
    const updateAvailable = compareSemver(result.latestVersion, current || '0.0.0') > 0;
    const plan = upgradePlanFor(install.method, channel);

    // --apply: actually perform the method-appropriate upgrade.
    let applied = null;
    if (opts.apply && updateAvailable) {
        if (!plan.applyCommand) {
            applied = { ran: false, reason: 'No automatic apply for this install method; follow the steps above.' };
        } else if (!opts.yes && (!process.stderr || !process.stderr.isTTY)) {
            applied = { ran: false, reason: 'Refusing to run an unattended install without --yes (no interactive terminal).' };
        } else {
            if (!opts.yes) {
                ctx.stderr.write(`\n[genexus-mcp update] About to run: ${plan.applyCommand.exe} ${plan.applyCommand.args.join(' ')}\n`);
            }
            const proceed = opts.yes ? true : await confirmTty(ctx, 'Proceed?');
            if (!proceed) {
                applied = { ran: false, reason: 'Cancelled by user.' };
            } else {
                const r = await runCommand(plan.applyCommand.exe, plan.applyCommand.args);
                applied = { ran: true, ok: r.ok, exitCode: r.code, command: `${plan.applyCommand.exe} ${plan.applyCommand.args.join(' ')}`, error: r.error || null };
            }
        }
    }

    const help = [];
    if (updateAvailable) {
        if (plan.auto) help.push('Auto-update path: restart your AI client and it fetches the new version (via npx @latest).');
        help.push(...plan.steps);
        if (result.releaseUrl) help.push(`Release notes: ${result.releaseUrl}`);
        if (!opts.apply && plan.applyCommand) help.push('Or run `genexus-mcp update --apply` to do it now.');
    } else {
        help.push(`Already on the latest ${channel} version.`);
    }
    if (applied && applied.ran && applied.ok) help.push('Update applied. Fully restart your AI client to load it.');
    if (applied && applied.ran && !applied.ok) help.push(`Apply command exited non-zero (${applied.exitCode}). ${applied.error || ''}`.trim());
    if (applied && !applied.ran) help.push(applied.reason);
    help.push(...driftHelp);

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                current,
                latest: result.latestVersion,
                channel,
                releaseUrl: result.releaseUrl,
                updateAvailable,
                source: result.source || null,
                installMethod: install.method,
                autoUpdates: plan.auto,
                installCommand: plan.applyCommand
                    ? `${plan.applyCommand.exe} ${plan.applyCommand.args.join(' ')}`
                    : (install.method === 'package-direct'
                        ? `npx -y ${NPM_PACKAGE}${channel && channel !== 'latest' ? `@${channel}` : '@latest'} clients add --clients antigravity`
                        : INSTALL_ONE_LINER),
                applied,
                fetched: true,
                clientDrift: mismatches
            },
            help
        }
    };
}

function confirmTty(ctx, question) {
    return new Promise((resolve) => {
        if (!process.stdin || !process.stdin.isTTY) { resolve(false); return; }
        const readline = require('readline');
        const rl = readline.createInterface({ input: process.stdin, output: ctx.stderr });
        rl.question(`${question} [y/N]: `, (a) => {
            rl.close();
            const t = (a || '').trim().toLowerCase();
            resolve(t === 'y' || t === 'yes');
        });
    });
}

module.exports = {
    startBackgroundUpdateCheck,
    handleUpdate,
    compareSemver,
    parseSemver,
    getPackageVersion,
    detectInstallMethod,
    upgradePlanFor,
    fetchLatestRelease,
    validateChannel,
    runCommand
};
