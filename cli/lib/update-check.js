const fs = require('fs');
const os = require('os');
const path = require('path');
const https = require('https');

const REPO = 'lennix1337/Genexus18MCP';
const NPM_PACKAGE = 'genexus-mcp';
const CACHE_TTL_MS = 24 * 60 * 60 * 1000;
const FETCH_TIMEOUT_MS = 2000;

function getPackageVersion() {
    try {
        const pkg = require('../../package.json');
        return typeof pkg.version === 'string' ? pkg.version : null;
    } catch {
        return null;
    }
}

function getCacheFile() {
    return path.join(os.homedir(), '.genexus-mcp', 'update-check.json');
}

function readCache() {
    try {
        const raw = fs.readFileSync(getCacheFile(), 'utf8');
        const data = JSON.parse(raw);
        if (data && typeof data === 'object') return data;
    } catch {
    }
    return null;
}

function writeCache(data) {
    try {
        const file = getCacheFile();
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, JSON.stringify(data), 'utf8');
    } catch {
    }
}

function stripV(v) {
    return typeof v === 'string' ? v.replace(/^v/i, '').trim() : '';
}

function parseSemver(v) {
    const s = stripV(v);
    const m = /^(\d+)\.(\d+)\.(\d+)/.exec(s);
    if (!m) return null;
    return [Number(m[1]), Number(m[2]), Number(m[3])];
}

function compareSemver(a, b) {
    const pa = parseSemver(a);
    const pb = parseSemver(b);
    if (!pa || !pb) return 0;
    for (let i = 0; i < 3; i += 1) {
        if (pa[i] > pb[i]) return 1;
        if (pa[i] < pb[i]) return -1;
    }
    return 0;
}

function fetchLatestRelease() {
    return new Promise((resolve) => {
        const options = {
            hostname: 'api.github.com',
            path: `/repos/${REPO}/releases/latest`,
            method: 'GET',
            headers: {
                'User-Agent': `${NPM_PACKAGE}-cli`,
                'Accept': 'application/vnd.github+json'
            }
        };

        const req = https.request(options, (res) => {
            if (res.statusCode !== 200) {
                res.resume();
                resolve(null);
                return;
            }
            let body = '';
            res.setEncoding('utf8');
            res.on('data', (chunk) => { body += chunk; });
            res.on('end', () => {
                try {
                    const json = JSON.parse(body);
                    const tag = stripV(json.tag_name || '');
                    const url = typeof json.html_url === 'string' ? json.html_url : null;
                    if (!tag) { resolve(null); return; }
                    resolve({ latestVersion: tag, releaseUrl: url });
                } catch {
                    resolve(null);
                }
            });
        });

        req.on('error', () => resolve(null));
        req.setTimeout(FETCH_TIMEOUT_MS, () => {
            req.destroy();
            resolve(null);
        });

        req.end();
        if (typeof req.unref === 'function') req.unref();
    });
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

function maybePrintCachedBanner(opts) {
    const current = getPackageVersion();
    if (!current) return;
    const cache = readCache();
    if (!cache || !cache.latestVersion) return;
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
            releaseUrl: result.releaseUrl
        });
    }).catch(() => {});
}

function startBackgroundUpdateCheck(opts) {
    if (isDisabled(opts)) return;
    maybePrintCachedBanner(opts);
    scheduleBackgroundFetch();
}

async function handleUpdate(_options, ctx) {
    const current = getPackageVersion();
    const result = await fetchLatestRelease();

    if (!result) {
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    current,
                    latest: null,
                    updateAvailable: false,
                    fetched: false
                },
                help: ['Could not reach GitHub releases API. Check connectivity or retry later.']
            }
        };
    }

    writeCache({
        checkedAt: Date.now(),
        latestVersion: result.latestVersion,
        releaseUrl: result.releaseUrl
    });

    const updateAvailable = compareSemver(result.latestVersion, current || '0.0.0') > 0;
    const help = updateAvailable
        ? [`Run: npm install -g ${NPM_PACKAGE}@latest`, result.releaseUrl ? `Release: ${result.releaseUrl}` : null].filter(Boolean)
        : ['Already on latest version.'];

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                current,
                latest: result.latestVersion,
                releaseUrl: result.releaseUrl,
                updateAvailable,
                installCommand: `npm install -g ${NPM_PACKAGE}@latest`,
                fetched: true
            },
            help
        }
    };
}

module.exports = {
    startBackgroundUpdateCheck,
    handleUpdate,
    compareSemver,
    parseSemver,
    getPackageVersion
};
