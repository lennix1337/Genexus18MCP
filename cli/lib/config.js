const path = require('path');
const fs = require('fs');
const os = require('os');

function generateConfig(gxPath, kbPath) {
    return {
        GeneXus: { InstallationPath: gxPath },
        Server: {
            HttpPort: 5000,
            McpStdio: true,
            ToolProfile: 'standard',
            SessionIdleTimeoutMinutes: 10,
            WorkerIdleTimeoutMinutes: 5,
            // Lean + terse defaults: the MCP client is an LLM agent that reads
            // content[0].text only. structuredContent duplicates the whole payload
            // (+42-46% bytes) and next_legal_actions/_meta.tokens are UX sugar the
            // agent doesn't need. Measured per-turn win; users who want the full
            // MCP surface flip these back in config.json.
            EmitStructuredContent: false,
            TerseResponses: true
        },
        Environment: { KBPath: kbPath }
    };
}

function generateNeutralConfig(gxPath, { workerPath, gatewayMode = 'stdio-isolated', resolutionPolicy = 'strict' } = {}) {
    return {
        ConfigSchemaVersion: 2,
        GatewayMode: gatewayMode,
        GeneXus: {
            InstallationPath: gxPath,
            WorkerExecutable: workerPath || path.join(path.dirname(getGatewayExePath()), 'worker', 'GxMcp.Worker.exe')
        },
        Server: {
            HttpPort: 0,
            McpStdio: true,
            ToolProfile: 'standard',
            WorkerSharingMode: 'isolated',
            SessionIdleTimeoutMinutes: 10,
            WorkerIdleTimeoutMinutes: 5,
            EmitStructuredContent: false,
            TerseResponses: true
        },
        Environment: {
            ResolutionPolicy: resolutionPolicy
        }
    };
}

function getGatewayExePath() {
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) {
        return process.env.GENEXUS_MCP_GATEWAY_EXE;
    }
    return path.join(__dirname, '..', '..', 'publish', 'GxMcp.Gateway.exe');
}

function getToolDefinitionsPath() {
    // The gateway loads tool_definitions.json from its own exe directory at
    // runtime (see GxMcp.Gateway/McpRouter.cs). The packaged distribution
    // places the file alongside publish/GxMcp.Gateway.exe via the csproj's
    // <Content CopyToPublishDirectory="Always" /> rule. Prior versions hardcoded
    // the dev-tree path, so `genexus-mcp doctor` reported "tool_definitions.json
    // is missing" on every installed copy even though the file was present next
    // to the exe.
    if (process.env.GENEXUS_MCP_TOOL_DEFINITIONS) {
        return process.env.GENEXUS_MCP_TOOL_DEFINITIONS;
    }
    const candidates = [
        // 1. Sibling of the gateway exe (packaged install — the path the gateway itself uses).
        path.join(path.dirname(getGatewayExePath()), 'tool_definitions.json'),
        // 2. Dev-tree source (when running from a git checkout).
        path.join(__dirname, '..', '..', 'src', 'GxMcp.Gateway', 'tool_definitions.json'),
        // 3. Fallback alongside the CLI itself (defensive — for unusual layouts).
        path.join(__dirname, '..', '..', 'publish', 'tool_definitions.json')
    ];
    // An explicit gateway override is usually a fixed-path or checkout install.
    // Do not hide a missing sibling artifact with this source tree's copy: the
    // Gateway loads the file next to its own executable at runtime.
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) return candidates[0];
    for (const candidate of candidates) {
        if (fs.existsSync(candidate)) return candidate;
    }
    // Nothing found — return the canonical packaged path so the doctor's
    // existence check reports the location that SHOULD have the file.
    return candidates[0];
}

function getGeneXusVersionCatalog() {
    const candidates = [
        path.join(__dirname, '..', '..', 'config', 'gx-versions.json'),
        path.join(__dirname, '..', '..', 'publish', 'config', 'gx-versions.json')
    ];
    for (const candidate of candidates) {
        try {
            const catalog = JSON.parse(fs.readFileSync(candidate, 'utf8'));
            if (!catalog || !catalog.primaryMajor || !Array.isArray(catalog.supportedMajors)) continue;
            const entries = catalog.supportedMajors.filter((entry) => entry && /^\d+$/.test(String(entry.major)));
            const legacyEntries = Array.isArray(catalog.legacyMajors)
                ? catalog.legacyMajors.filter((entry) => entry && /^(?:10\.[1-3]|\d+)$/.test(String(entry.major)))
                : [];
            const primary = entries.find((entry) => String(entry.major) === String(catalog.primaryMajor));
            if (entries.length > 0 && primary?.defaultInstallPath) {
                return { ...catalog, supportedMajors: entries, legacyMajors: legacyEntries };
            }
        } catch {
        }
    }
    return {
        primaryMajor: '18',
        supportedMajors: [
            { major: '17', displayName: 'GeneXus 17', defaultInstallPath: 'C:\\Program Files (x86)\\GeneXus\\GeneXus17Trial' },
            { major: '18', displayName: 'GeneXus 18', defaultInstallPath: 'C:\\Program Files (x86)\\GeneXus\\GeneXus18' }
        ],
        legacyMajors: [
            { major: '10.3', displayName: 'GeneXus Evolution 3', defaultInstallPath: 'C:\\Program Files (x86)\\GeneXus\\GeneXusXEv3' },
            { major: '9', displayName: 'GeneXus 9.0', defaultInstallPath: 'C:\\Program Files (x86)\\ARTech\\GeneXus\\GeneXus 9.0' },
            { major: '8', displayName: 'GeneXus 8.0', defaultInstallPath: 'C:\\Program Files (x86)\\ARTech\\GeneXus\\gxw80' }
        ],
        source: 'built-in-fallback'
    };
}

function hasGeneXusExecutable(gxPath) {
    return Boolean(gxPath) && ['genexus.exe', 'gx.exe', 'gxw32.exe']
        .some((name) => fs.existsSync(path.join(gxPath, name)));
}

function getGeneXusCatalogEntries(preferredMajor = null) {
    const catalog = getGeneXusVersionCatalog();
    const all = [...catalog.supportedMajors, ...(catalog.legacyMajors || [])];
    return all.sort((left, right) => {
        const leftPreferred = preferredMajor !== null && String(left.major) === String(preferredMajor);
        const rightPreferred = preferredMajor !== null && String(right.major) === String(preferredMajor);
        if (leftPreferred !== rightPreferred) return Number(rightPreferred) - Number(leftPreferred);
        const leftPrimary = String(left.major) === String(catalog.primaryMajor);
        const rightPrimary = String(right.major) === String(catalog.primaryMajor);
        return Number(rightPrimary) - Number(leftPrimary);
    });
}

function discoverGeneXusFromRegistry(preferredMajor = null) {
    if (process.platform !== 'win32') return null;
    try {
        const { execFileSync } = require('child_process');
        const entries = getGeneXusCatalogEntries(preferredMajor);
        const hives = [
            'HKLM\\SOFTWARE\\WOW6432Node\\Artech',
            'HKLM\\SOFTWARE\\Artech',
            'HKCU\\SOFTWARE\\Artech'
        ];
        for (const hive of hives) {
            for (const entry of entries) {
                const names = Array.isArray(entry.registryNames) && entry.registryNames.length > 0
                    ? entry.registryNames
                    : [`GeneXus ${entry.major}`];
                for (const ver of names) {
                    const key = `${hive}\\${ver}`;
                    try {
                        const out = execFileSync('reg.exe', ['query', key, '/v', 'InstallationDirectory'], {
                            encoding: 'utf8',
                            stdio: ['ignore', 'pipe', 'ignore'],
                            windowsHide: true,
                            timeout: 3000
                        });
                        const match = out.match(/InstallationDirectory\s+REG_SZ\s+(.+?)\r?\n/i);
                        if (match) {
                            const candidate = match[1].trim().replace(/[\\/]+$/, '');
                            if (candidate && hasGeneXusExecutable(candidate) && matchesPreferredGeneXusMajor(candidate, preferredMajor)) {
                                return candidate;
                            }
                        }
                    } catch {
                    }
                }
                // Classic ARTech installers store GX8/GX9 under Setup\\<version>
                // and expose the actual IDE root in the Install value rather
                // than the modern GeneXus <major>\\InstallationDirectory key.
                for (const setupVersion of (entry.legacySetupVersions || [])) {
                    const key = `${hive}\\\\${setupVersion}`;
                    try {
                        const out = execFileSync('reg.exe', ['query', key, '/v', 'Install'], {
                            encoding: 'utf8',
                            stdio: ['ignore', 'pipe', 'ignore'],
                            windowsHide: true,
                            timeout: 3000
                        });
                        const installLine = out.split(String.fromCharCode(10)).map((line) => line.trim()).find((line) => /^Install\s+REG_SZ\s+/i.test(line));
                        const candidate = installLine ? installLine.replace(/^Install\s+REG_SZ\s+/i, '').trim() : null;
                        if (candidate && hasGeneXusExecutable(candidate) && matchesPreferredGeneXusMajor(candidate, preferredMajor)) return candidate;
                    } catch {
                    }
                }
                for (const legacyVersion of (entry.legacyRegistryVersions || [])) {
                    const key = `${hive}\\GeneXus\\${legacyVersion}`;
                    try {
                        const out = execFileSync('reg.exe', ['query', key, '/v', 'InstallPath'], {
                            encoding: 'utf8',
                            stdio: ['ignore', 'pipe', 'ignore'],
                            windowsHide: true,
                            timeout: 3000
                        });
                        const match = out.match(/InstallPath\s+REG_SZ\s+(.+?)\r?\n/i);
                        if (match) {
                            const candidate = match[1].trim().replace(/[\\/]+$/, '');
                            if (candidate && hasGeneXusExecutable(candidate) && matchesPreferredGeneXusMajor(candidate, preferredMajor)) return candidate;
                        }
                    } catch {
                    }
                }
            }
        }
    } catch {
    }
    return null;
}

function discoverGeneXusInstallation(preferredMajor = null) {
    if (process.env.GENEXUS_HOME) {
        const candidate = process.env.GENEXUS_HOME.replace(/[\\/]+$/, '');
        if (hasGeneXusExecutable(candidate) && matchesPreferredGeneXusMajor(candidate, preferredMajor)) return candidate;
    }

    const fromRegistry = discoverGeneXusFromRegistry(preferredMajor);
    if (fromRegistry) return fromRegistry;

    const programDirs = [];
    if (process.env['ProgramFiles(x86)']) programDirs.push(process.env['ProgramFiles(x86)']);
    if (process.env.ProgramFiles) programDirs.push(process.env.ProgramFiles);
    // Cover non-default drives where IT often installs SDKs.
    for (const drive of ['C', 'D', 'E']) {
        programDirs.push(`${drive}:\\Program Files (x86)`);
        programDirs.push(`${drive}:\\Program Files`);
    }

    const entries = getGeneXusCatalogEntries(preferredMajor);
    const seen = new Set();
    for (const base of programDirs) {
        const roots = [
            path.join(base, 'GeneXus'),
            path.join(base, 'Artech', 'GeneXus')
        ];
        for (const root of roots) {
        const key = root.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        for (const entry of entries) {
            const candidateNames = new Set([
                `GeneXus${entry.major}`,
                path.basename(String(entry.defaultInstallPath || ''))
            ]);
            for (const ver of candidateNames) {
                if (!ver) continue;
                const candidate = path.join(root, ver);
                if (hasGeneXusExecutable(candidate) && matchesPreferredGeneXusMajor(candidate, preferredMajor)) {
                    return candidate;
                }
            }
        }
        // Also scan any GeneXus* sibling (e.g. custom-named "GeneXus18 U10").
        try {
            if (fs.existsSync(root)) {
                for (const entry of fs.readdirSync(root)) {
                    if (!/^GeneXus/i.test(entry)) continue;
                    const majorMatch = entry.match(/^GeneXus\s*(\d+)/i);
                    if (majorMatch && !entries.some((item) => String(item.major) === majorMatch[1])) continue;
                    const candidate = path.join(root, entry);
                    if (hasGeneXusExecutable(candidate) && matchesPreferredGeneXusMajor(candidate, preferredMajor)) {
                        return candidate;
                    }
                }
            }
        } catch {
        }
        }
    }

    const fromPath = discoverGeneXusFromPath(preferredMajor);
    if (fromPath) return fromPath;

    return null;
}

// Same-major discovery returns the first install the registry or folder scan
// finds, usually the newest update, and opening a KB with a newer update
// converts it. When the KB's exact build is known, prefer an install of the
// same major.minor.update (sibling of the discovered one or under the standard
// GeneXus roots); otherwise keep the same-major result.
function discoverGeneXusInstallationForKb(kbIdentity) {
    const major = kbIdentity ? kbIdentity.major : null;
    const fallback = discoverGeneXusInstallation(major);
    const wanted = geneXusUpdateKey(kbIdentity && kbIdentity.version);
    if (!wanted) return fallback;
    if (fallback && geneXusUpdateKey(readGeneXusInstallationIdentity(fallback).version) === wanted) return fallback;

    const roots = [];
    if (fallback) roots.push(path.dirname(fallback));
    for (const base of [process.env['ProgramFiles(x86)'], process.env.ProgramFiles]) {
        if (!base) continue;
        roots.push(path.join(base, 'GeneXus'), path.join(base, 'Artech', 'GeneXus'));
    }
    const seen = new Set();
    for (const root of roots) {
        const key = root.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        let entries;
        try {
            entries = fs.readdirSync(root);
        } catch {
            continue;
        }
        for (const entry of entries) {
            if (!/^GeneXus/i.test(entry)) continue;
            const candidate = path.join(root, entry);
            if (!hasGeneXusExecutable(candidate)) continue;
            if (geneXusUpdateKey(readGeneXusInstallationIdentity(candidate).version) === wanted) return candidate;
        }
    }
    return fallback;
}

function geneXusUpdateKey(version) {
    const match = String(version || '').trim().match(/^(\d+)\.(\d+)\.(\d+)/);
    return match ? `${match[1]}.${match[2]}.${match[3]}` : null;
}

function discoverGeneXusFromPath(preferredMajor = null) {
    if (process.platform !== 'win32') return null;
    try {
        const { execFileSync } = require('child_process');
        for (const executable of ['genexus.exe', 'gx.exe', 'gxw32.exe']) {
            let out;
            try {
                out = execFileSync('where.exe', [executable], {
                    encoding: 'utf8',
                    stdio: ['ignore', 'pipe', 'ignore'],
                    windowsHide: true,
                    timeout: 3000
                });
            } catch {
                continue;
            }
            const first = out.split('\n').map((s) => s.trim()).find(Boolean);
            if (first && fs.existsSync(first) && matchesPreferredGeneXusMajor(path.dirname(first), preferredMajor)) {
                return path.dirname(first);
            }
        }
    } catch {
    }
    return null;
}

function discoverKnowledgeBase(cwd) {
    if (!cwd) return null;
    if (directoryLooksLikeKnowledgeBase(cwd)) return cwd;
    return null;
}

// Broader KB discovery: walk up the cwd ancestry, then scan a small set of common
// roots where developers stash KBs. Returns deduped candidates with a `source` tag
// so the caller can explain its pick. Bounded so we never recurse into giant trees.
function discoverKnowledgeBases(cwd, { maxResults = 25, scanDepth = 2 } = {}) {
    const results = [];
    const seen = new Set();
    const push = (dir, source) => {
        if (!dir) return;
        const key = path.resolve(dir).toLowerCase();
        if (seen.has(key)) return;
        if (results.length >= maxResults) return;
        if (directoryLooksLikeKnowledgeBase(dir)) {
            seen.add(key);
            results.push({ path: path.resolve(dir), source });
        }
    };

    // 1. cwd and ancestors (a developer running `npx init` from a KB subdirectory
    //    almost certainly meant that KB).
    if (cwd) {
        let current = path.resolve(cwd);
        let lastParent = null;
        while (current && current !== lastParent && results.length < maxResults) {
            push(current, 'cwd-ancestor');
            lastParent = current;
            current = path.dirname(current);
            if (current === lastParent) break;
        }
    }

    // 2. Common KB roots — drives + user folders. Scan a shallow depth only.
    const roots = [];
    for (const drive of ['C', 'D', 'E']) {
        roots.push(`${drive}:\\KBs`);
        roots.push(`${drive}:\\KB`);
        roots.push(`${drive}:\\GeneXus`);
    }
    if (process.env.USERPROFILE) {
        roots.push(path.join(process.env.USERPROFILE, 'Documents', 'GeneXus'));
        roots.push(path.join(process.env.USERPROFILE, 'KBs'));
        roots.push(path.join(process.env.USERPROFILE, 'source', 'repos'));
    }

    const scanRoot = (root, depth) => {
        if (depth < 0) return;
        if (results.length >= maxResults) return;
        let entries;
        try {
            entries = fs.readdirSync(root, { withFileTypes: true });
        } catch {
            return;
        }
        for (const entry of entries) {
            if (results.length >= maxResults) return;
            if (!entry.isDirectory()) continue;
            const full = path.join(root, entry.name);
            push(full, 'common-root');
            if (depth > 0) scanRoot(full, depth - 1);
        }
    };

    for (const root of roots) {
        try {
            if (fs.existsSync(root)) scanRoot(root, scanDepth);
        } catch {
        }
    }

    return results;
}

function directoryLooksLikeKnowledgeBase(dir) {
    try {
        const files = fs.readdirSync(dir);
        if (files.some((f) => f.toLowerCase().endsWith('.gxw') || f.toLowerCase() === 'knowledgebase.connection')) return true;
        const classicMarkers = new Set(['data001', 'gxspc001', 'kbdata', 'attribut.dat', 'att.xpw', 'objects.dat', 'objects.idx']);
        return files.filter((f) => classicMarkers.has(f.toLowerCase())).length >= 2;
    } catch {
        return false;
    }
}

// Strip // and /* */ comments and trailing commas while respecting string
// literals, so we can parse JSONC configs (VS Code's mcp.json/settings.json and
// OpenCode's opencode.jsonc are JSONC). Comments are NOT preserved on rewrite.
//
// Trailing-comma removal is done INSIDE the scanner (a comma is deferred and only
// emitted once we know the next significant char isn't a closing brace/bracket),
// not by a post-hoc regex — a regex over the whole text would also strip commas
// that live inside string values (e.g. "see foo, ]" -> "see foo ]"). Only `"`
// opens a string: JSON/JSONC has no single-quoted strings.
function stripJsonComments(text) {
    let out = '';
    let inString = false;
    let inLine = false;
    let inBlock = false;
    let pendingComma = false;
    // Resolve a deferred comma: keep it unless the next significant char closes a
    // container (then it was a trailing comma and gets dropped).
    const flushComma = (nextSignificant) => {
        if (pendingComma) {
            if (nextSignificant !== '}' && nextSignificant !== ']') out += ',';
            pendingComma = false;
        }
    };
    for (let i = 0; i < text.length; i += 1) {
        const ch = text[i];
        const next = text[i + 1];
        if (inLine) {
            if (ch === '\n') inLine = false;
            continue;
        }
        if (inBlock) {
            if (ch === '*' && next === '/') { inBlock = false; i += 1; }
            continue;
        }
        if (inString) {
            out += ch;
            if (ch === '\\') { out += next; i += 1; continue; }
            if (ch === '"') inString = false;
            continue;
        }
        // Outside any string/comment.
        if (ch === '/' && next === '/') { inLine = true; i += 1; continue; }
        if (ch === '/' && next === '*') { inBlock = true; i += 1; continue; }
        if (ch === ' ' || ch === '\t' || ch === '\r' || ch === '\n') {
            // Whitespace between a deferred comma and the next token is collapsed
            // (JSON.parse ignores it); otherwise emit it verbatim.
            if (!pendingComma) out += ch;
            continue;
        }
        if (ch === ',') {
            flushComma(',');       // a prior comma followed by another comma is kept as-is
            pendingComma = true;   // defer this one until we see what follows
            continue;
        }
        flushComma(ch);
        out += ch;
        if (ch === '"') inString = true;
    }
    flushComma('');
    return out;
}

function readJsonFileSafe(filePath, fileSystem = fs) {
    try {
        const raw = fileSystem.readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '');
        if (!raw.trim()) return {};
        try {
            return JSON.parse(raw);
        } catch {
            // Fall back to a JSONC-tolerant parse before giving up, so a commented
            // VS Code / OpenCode config isn't treated as corrupt.
            const stripped = stripJsonComments(raw);
            const result = JSON.parse(stripped);
            // Warn: if we ever rewrite this file the comments will be lost.
            process.stderr.write(
                `[genexus-mcp] Warning: ${filePath} contains JSONC comments (// or /* */).\n` +
                `[genexus-mcp] Comments are stripped for reading but will be lost if this file is rewritten by the CLI.\n`
            );
            return result;
        }
    } catch {
        return null;
    }
}

// Atomic write: stage to a temp file then rename over the target, so a crash
// mid-write can never leave a client's config truncated.
function writeFileAtomic(filePath, content, fileSystem = fs) {
    const tmp = `${filePath}.tmp-${process.pid}`;
    fileSystem.writeFileSync(tmp, content);
    try {
        fileSystem.renameSync(tmp, filePath);
    } catch (err) {
        try { fileSystem.rmSync(tmp, { force: true }); } catch { /* ignore */ }
        throw err;
    }
}

// Back up a client config once per process run before the first mutation, so the
// user has a restore point (the old build-from-source install.ps1 did this; the
// CLI now owns it). A failed backup blocks the mutation so the result remains
// recoverable.
// After writing a new backup, prune old .bak files for the same config so at
// most BAK_KEEP_COUNT backups exist (oldest removed first).
const BAK_KEEP_COUNT = 5;
const _backedUpThisRun = new Set();
function backupClientConfigOnce(filePath, fileSystem = fs) {
    if (!fileSystem.existsSync(filePath)) return null;
    // Case-fold the dedupe key only on Windows; lowercasing on a case-sensitive
    // filesystem could merge two genuinely distinct paths.
    const resolved = path.resolve(filePath);
    const key = process.platform === 'win32' ? resolved.toLowerCase() : resolved;
    if (_backedUpThisRun.has(key)) return null;
    const d = new Date();
        const stamp = d.toISOString().replace(/[-:T]/g, '').slice(0, 14);
        let bak = `${filePath}.${stamp}.bak`;
        let suffix = 1;
        while (fileSystem.existsSync(bak)) bak = `${filePath}.${stamp}-${suffix++}.bak`;
        fileSystem.copyFileSync(filePath, bak);
        _backedUpThisRun.add(key);
        // Prune: keep only the BAK_KEEP_COUNT most-recent .bak files for this config.
        try {
            const dir = path.dirname(resolved);
            const base = path.basename(resolved);
            const bakPattern = new RegExp(`^${base.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.\\d{14}\\.bak$`);
            const existing = fileSystem.readdirSync(dir)
                .filter(f => bakPattern.test(f))
                .map(f => path.join(dir, f))
                .sort(); // ISO timestamp stamps sort lexicographically = chronologically
            if (existing.length > BAK_KEEP_COUNT) {
                const toRemove = existing.slice(0, existing.length - BAK_KEEP_COUNT);
                for (const old of toRemove) {
                    try { fileSystem.rmSync(old, { force: true }); } catch { /* best-effort */ }
                }
            }
        } catch { /* pruning is best-effort; never block the backup */ }
    return bak;
}

// Write JSON to a client config: back up, serialize, write atomically.
function writeClientJson(filePath, obj, fileSystem = fs) {
    backupClientConfigOnce(filePath, fileSystem);
    writeFileAtomic(filePath, JSON.stringify(obj, null, 2), fileSystem);
}

// Write raw text to a client config (e.g. Codex TOML): back up + write atomically.
function writeClientText(filePath, content, fileSystem = fs) {
    backupClientConfigOnce(filePath, fileSystem);
    writeFileAtomic(filePath, content, fileSystem);
}

function copyFileAtomic(sourcePath, targetPath, fileSystem = fs) {
    const tmp = `${targetPath}.tmp-${process.pid}`;
    fileSystem.copyFileSync(sourcePath, tmp);
    try {
        fileSystem.renameSync(tmp, targetPath);
    } catch (err) {
        try { fileSystem.rmSync(tmp, { force: true }); } catch { /* ignore */ }
        throw err;
    }
}

function migrateLegacyConfig(sourcePath, targetPath, { rejectNonMigratable = false, fileSystem = fs } = {}) {
    const source = path.resolve(sourcePath);
    const target = path.resolve(targetPath);
    const legacy = readJsonFileSafe(source, fileSystem);
    if (!legacy || typeof legacy !== 'object') {
        const err = new Error(`Legacy config is missing or invalid: ${source}`);
        err.code = 'INVALID_CONFIG';
        throw err;
    }

    const env = legacy.Environment && typeof legacy.Environment === 'object' ? legacy.Environment : {};
    const notMigrated = ['KBPath', 'KBs', 'DefaultKb', 'ActiveKb']
        .filter((key) => Object.prototype.hasOwnProperty.call(env, key))
        .map((key) => `Environment.${key}`);
    if (rejectNonMigratable && notMigrated.length > 0) {
        const err = new Error(`Migration rejected non-migratable fields: ${notMigrated.join(', ')}.`);
        err.code = 'NON_MIGRATABLE_FIELDS';
        err.notMigrated = notMigrated;
        throw err;
    }

    const migrated = {
        ConfigSchemaVersion: 2,
        GatewayMode: legacy.GatewayMode || 'stdio-isolated',
        GeneXus: { ...(legacy.GeneXus && typeof legacy.GeneXus === 'object' ? legacy.GeneXus : {}) },
        Server: { ...(legacy.Server && typeof legacy.Server === 'object' ? legacy.Server : {}) },
        Environment: {
            ResolutionPolicy: env.ResolutionPolicy || 'strict'
        }
    };
    const backupPath = (() => {
        let candidate = `${source}.pre-migrate.bak`;
        let suffix = 1;
        while (fileSystem.existsSync(candidate)) candidate = `${source}.pre-migrate-${suffix++}.bak`;
        return candidate;
    })();
    const targetExisted = fileSystem.existsSync(target);
    const targetBackup = targetExisted ? `${target}.rollback-${process.pid}-${Date.now()}.bak` : null;
    let wroteTarget = false;
    try {
        copyFileAtomic(source, backupPath, fileSystem);
        if (targetExisted) copyFileAtomic(target, targetBackup, fileSystem);
        fileSystem.mkdirSync(path.dirname(target), { recursive: true });
        writeFileAtomic(target, JSON.stringify(migrated, null, 2), fileSystem);
        wroteTarget = true;
        const readBack = process.env.GENEXUS_MCP_MIGRATE_FAIL_READBACK
            ? null
            : readJsonFileSafe(target, fileSystem);
        if (!readBack || readBack.ConfigSchemaVersion !== 2 || JSON.stringify(readBack) !== JSON.stringify(migrated)) {
            throw new Error('Migration read-back verification failed.');
        }
        if (targetBackup) {
            try { fileSystem.rmSync(targetBackup, { force: true }); } catch { /* best effort */ }
        }
        return { sourcePath: source, targetPath: target, backupPath, readBack: true, migrated: Object.keys(migrated), notMigrated, rolledBack: false };
    } catch (err) {
        let rolledBack = false;
        try {
            if (targetExisted && targetBackup) copyFileAtomic(targetBackup, target, fileSystem);
            else if (wroteTarget) fileSystem.rmSync(target, { force: true });
            rolledBack = true;
        } catch { /* report rollback failure */ }
        if (targetBackup) {
            try { fileSystem.rmSync(targetBackup, { force: true }); } catch { /* best effort */ }
        }
        err.rollback = { rolledBack };
        err.backupPath = backupPath;
        err.notMigrated = notMigrated;
        throw err;
    }
}

function resolveConfigPathNoMutate(cwd, options) {
    const cwdConfigPath = path.join(cwd, 'config.json');
    // Match the Gateway: an explicit GX_CONFIG_PATH is authoritative even when
    // the file is missing, so diagnostics do not silently inspect an unrelated
    // cwd config while the runtime fails on the explicit path.
    if (process.env.GX_CONFIG_PATH) {
        return path.resolve(process.env.GX_CONFIG_PATH);
    }
    if (fs.existsSync(cwdConfigPath)) {
        return cwdConfigPath;
    }
    // The neutral user config is the last fallback, matching the Gateway and the
    // zero-config launcher outside a KB folder. It is opt-in because this resolver
    // is also reached from commands that WRITE to the resolved path (`clients add`
    // patches it, `config migrate` reads it as `--from`). Falling back silently there
    // would let a command run from any folder rewrite the shared user profile, so
    // only the read-only diagnostics opt in and a mutating caller keeps resolving to
    // the explicit path or the cwd config.
    if (!options || options.allowUserProfile !== true) {
        return null;
    }
    const userConfigPath = path.join(os.homedir(), '.genexus-mcp', 'config.json');
    if (!directoryLooksLikeKnowledgeBase(cwd) && fs.existsSync(userConfigPath)) {
        return userConfigPath;
    }
    return null;
}

function createConfigFile(kbPath, gxPath) {
    const targetConfigPath = path.join(kbPath, 'config.json');
    const baseConfig = generateConfig(gxPath, kbPath);

    if (!fs.existsSync(kbPath)) {
        fs.mkdirSync(kbPath, { recursive: true });
    }

    const existing = fs.existsSync(targetConfigPath) ? readJsonFileSafe(targetConfigPath) : null;
    const preservedEnv = {};
    if (existing && existing.Environment) {
        if (existing.Environment.KBs) preservedEnv.KBs = existing.Environment.KBs;
        if (existing.Environment.ActiveKb) preservedEnv.ActiveKb = existing.Environment.ActiveKb;
        if (existing.Environment.DefaultKb) preservedEnv.DefaultKb = existing.Environment.DefaultKb;
    }
    const nextConfig = {
        ...baseConfig,
        Server: { ...baseConfig.Server },
        Environment: { ...baseConfig.Environment, ...preservedEnv }
    };

    if (existing) {
        if (existing.Server && Object.prototype.hasOwnProperty.call(existing.Server, 'ToolProfile')) {
            nextConfig.Server.ToolProfile = existing.Server.ToolProfile;
        } else {
            delete nextConfig.Server.ToolProfile;
        }
    }

    const changed = !existing || JSON.stringify(existing) !== JSON.stringify(nextConfig);
    if (changed) {
        writeFileAtomic(targetConfigPath, JSON.stringify(nextConfig, null, 2));
    }

    return {
        targetConfigPath,
        config: nextConfig,
        changed
    };
}

function getLauncher(client = null, gatewayExePath = getGatewayExePath()) {
    // Set by scripts/install.ps1 for fixed-path corporate installs — clients
    // launch the gateway exe directly instead of resolving via the npx cache.
    const directExe = process.env.GENEXUS_MCP_GATEWAY_EXE;
    if (directExe) return { command: directExe, args: [] };

    // Antigravity does not retain child stderr in its Language Server logs. When
    // the packaged gateway is available, point it directly at that executable so
    // each MCP handshake skips the npx bootstrap chain. The fallback keeps
    // source-tree development and incomplete packages on the existing npx path.
    if (client && client.preferDirectGateway && fs.existsSync(gatewayExePath)) {
        return { command: gatewayExePath, args: [] };
    }

    return { command: process.platform === 'win32' ? 'npx.cmd' : 'npx', args: ['-y', 'genexus-mcp@latest'] };
}

const DEFAULT_MCP_SERVER_NAME = 'genexus18mcp';

function escapeRegex(str) {
    return String(str).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// Detect whether an MCP server entry belongs to a third-party or official MCP
// server (e.g. official GeneXus MCP over HTTP, or another tool).
function isThirdPartyMcpEntry(entry) {
    if (!entry || typeof entry !== 'object') return false;
    // Explicit HTTP / SSE / remote URLs are third-party / official GeneXus MCP servers
    if (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote') {
        return true;
    }
    // Environment markers written by genexus-mcp
    const env = entry.env || entry.environment;
    if (env && typeof env === 'object') {
        if (env.GX_CONFIG_PATH || env.GX_PATH || env.GENEXUS_MCP_GATEWAY_EXE) {
            return false;
        }
    }
    // Command string or args mentioning genexus-mcp / GxMcp.Gateway / start_mcp.bat
    if (typeof entry.command === 'string') {
        if (/(^|[\\/])(genexus-mcp(\.cmd)?|GxMcp\.Gateway(\.exe)?|start_mcp\.bat)$/i.test(entry.command)) {
            return false;
        }
        if (/(^|[\\/])(npx|npx\.cmd)$/i.test(entry.command) && (!entry.args || entry.args.length === 0)) {
            return false;
        }
    }
    if (Array.isArray(entry.args) && entry.args.some((a) => typeof a === 'string' && /genexus-mcp/i.test(a))) {
        return false;
    }
    if (Array.isArray(entry.command)) {
        if (entry.command.some((a) => typeof a === 'string' && /(genexus-mcp|GxMcp\.Gateway|start_mcp\.bat)/i.test(a))) {
            return false;
        }
        if (entry.command.length === 1 && /(^|[\\/])(npx|npx\.cmd)$/i.test(entry.command[0])) {
            return false;
        }
    }
    // Any other custom command without our markers is third-party
    return true;
}

function isOurMcpEntry(entry, key) {
    if (!entry || typeof entry !== 'object') return false;
    if (key === 'genexus18' || key === 'genexus18mcp') return true;
    return !isThirdPartyMcpEntry(entry);
}

// Antigravity (Google's agentic IDE) ships its MCP config under ~/.gemini.
// The newer unified location (shared across Antigravity CLI / IDE / SDK) is
// ~/.gemini/config/mcp_config.json; the older IDE-specific one is
// ~/.gemini/antigravity/mcp_config.json. We write to the unified path when its
// parent dir already exists, else fall back to the IDE-specific path.
function resolveAntigravityConfigPath(home) {
    // Only target the unified location when its file already exists (the user has
    // adopted it); otherwise write the IDE-specific path, which is the location
    // Antigravity reliably reads and was confirmed working in the field.
    const unified = path.join(home, '.gemini', 'config', 'mcp_config.json');
    if (fs.existsSync(unified)) return unified;
    return path.join(home, '.gemini', 'antigravity', 'mcp_config.json');
}

// OpenCode CLI accepts either opencode.jsonc or opencode.json. Prefer an
// existing .jsonc so we don't strand the user's commented config, else .json.
function resolveOpenCodeConfigPath(xdgConfig) {
    const jsonc = path.join(xdgConfig, 'opencode', 'opencode.jsonc');
    if (fs.existsSync(jsonc)) return jsonc;
    return path.join(xdgConfig, 'opencode', 'opencode.json');
}

function alternateOpenCodeConfigPath(filePath) {
    return path.extname(filePath).toLowerCase() === '.jsonc'
        ? filePath.slice(0, -1)
        : filePath.replace(/\.json$/i, '.jsonc');
}

// VS Code stores its user profile (and native MCP mcp.json) in a per-platform
// location. `variant` is 'Code' (stable) or 'Code - Insiders'.
function vscodeUserDir(variant, { appData, macAppSupport, xdgConfig }) {
    if (process.platform === 'win32') return path.join(appData, variant, 'User');
    if (process.platform === 'darwin') return path.join(macAppSupport, variant, 'User');
    return path.join(xdgConfig, variant, 'User');
}

function getClientConfigTargets() {
    const home = os.homedir();
    const xdgConfig = process.env.XDG_CONFIG_HOME || path.join(home, '.config');
    const appData = process.env.APPDATA || path.join(home, 'AppData', 'Roaming');
    const localAppData = process.env.LOCALAPPDATA || path.join(home, 'AppData', 'Local');
    const macAppSupport = path.join(home, 'Library', 'Application Support');
    const vscodeStableUser = vscodeUserDir('Code', { appData, macAppSupport, xdgConfig });
    const vscodeInsidersUser = vscodeUserDir('Code - Insiders', { appData, macAppSupport, xdgConfig });

    // `installMarkers` prove the AGENT is installed, independent of whether our
    // MCP config file exists yet. This is the fix for the field report where the
    // wizard showed Antigravity as "not detected": Antigravity does not create
    // ~/.gemini/antigravity/mcp_config.json until the user adds an MCP server, so
    // detecting by config-file presence alone was chicken-and-egg.
    return [
        {
            id: 'claude-desktop-win',
            name: 'Claude Desktop (Windows)',
            format: 'mcpServers',
            path: path.join(home, 'AppData', 'Roaming', 'Claude', 'claude_desktop_config.json'),
            platforms: ['win32'],
            installMarkers: [
                path.join(localAppData, 'AnthropicClaude'),
                path.join(appData, 'Claude')
            ]
        },
        {
            id: 'claude-desktop-mac',
            name: 'Claude Desktop (macOS)',
            format: 'mcpServers',
            path: path.join(home, 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json'),
            platforms: ['darwin'],
            installMarkers: [
                path.join(macAppSupport, 'Claude'),
                '/Applications/Claude.app'
            ]
        },
        {
            id: 'antigravity',
            name: 'Antigravity',
            format: 'mcpServers',
            path: resolveAntigravityConfigPath(home),
            // The npm package includes the gateway exe. Prefer it for this client
            // because Antigravity hides the stderr of an npx child when startup fails.
            preferDirectGateway: true,
            // Unambiguous Antigravity markers only. ~/.gemini/config is NOT a
            // marker — gemini-cli can create ~/.gemini, and we'd false-positive;
            // it's still used as the write path (resolveAntigravityConfigPath)
            // once a real Antigravity install is confirmed by these markers.
            installMarkers: [
                path.join(localAppData, 'Programs', 'Antigravity'),
                path.join(appData, 'Antigravity'),
                path.join(home, '.antigravity'),
                path.join(home, '.gemini', 'antigravity')
            ]
        },
        {
            id: 'claude-code',
            name: 'Claude Code',
            format: 'mcpServers',
            path: path.join(home, '.claude.json'),
            installMarkers: [
                path.join(home, '.claude.json'),
                path.join(home, '.claude')
            ]
        },
        {
            id: 'gemini-cli',
            name: 'Gemini CLI',
            format: 'mcpServers',
            path: path.join(home, '.gemini', 'settings.json'),
            installMarkers: [
                path.join(home, '.gemini', 'settings.json')
            ]
        },
        {
            id: 'cursor',
            name: 'Cursor',
            format: 'mcpServers',
            path: path.join(home, '.cursor', 'mcp.json'),
            installMarkers: [
                path.join(home, '.cursor'),
                path.join(localAppData, 'Programs', 'cursor'),
                '/Applications/Cursor.app'
            ]
        },
        {
            id: 'opencode',
            name: 'OpenCode (CLI)',
            format: 'opencode',
            path: resolveOpenCodeConfigPath(xdgConfig),
            alternatePaths: [alternateOpenCodeConfigPath(resolveOpenCodeConfigPath(xdgConfig))],
            installMarkers: [
                path.join(xdgConfig, 'opencode'),
                path.join(home, '.local', 'share', 'opencode')
            ]
        },
        {
            id: 'codex-cli',
            name: 'Codex CLI',
            format: 'codex-toml',
            path: path.join(home, '.codex', 'config.toml'),
            installMarkers: [
                path.join(home, '.codex')
            ]
        },
        {
            id: 'opencode-desktop',
            name: 'OpenCode Desktop',
            format: 'opencode',
            path: resolveOpenCodeConfigPath(xdgConfig),
            alternatePaths: [alternateOpenCodeConfigPath(resolveOpenCodeConfigPath(xdgConfig))],
            detectByMarkerOnly: true,
            installMarkers: [
                path.join(localAppData, 'Programs', '@opencode-aidesktop'),
                path.join(appData, 'ai.opencode.desktop'),
                '/Applications/OpenCode.app'
            ]
        },
        {
            id: 'vscode',
            name: 'VS Code',
            format: 'vscode-servers',
            path: path.join(vscodeStableUser, 'mcp.json'),
            installMarkers: [vscodeStableUser]
        },
        {
            id: 'vscode-insiders',
            name: 'VS Code Insiders',
            format: 'vscode-servers',
            path: path.join(vscodeInsidersUser, 'mcp.json'),
            installMarkers: [vscodeInsidersUser]
        }
    ];
}

// Decide whether an agent is installed (independent of whether OUR config file
// exists). Returns the installed flag plus diagnostics so the wizard can show
// the user exactly where it looked when an agent is reported "not detected".
function detectClientInstalled(client) {
    const markers = Array.isArray(client.installMarkers) ? client.installMarkers : [];
    const hasConfig = fs.existsSync(client.path);
    let markerHit = null;
    for (const m of markers) {
        if (fs.existsSync(m)) {
            markerHit = m;
            break;
        }
    }
    const installed = client.detectByMarkerOnly ? (markerHit !== null) : (hasConfig || markerHit !== null);
    return {
        installed,
        hasConfig,
        markerHit,
        markersChecked: markers
    };
}

function listSupportedClientIds() {
    return getClientConfigTargets().map((c) => c.id);
}

// The list command is intentionally local: it does not spawn a configured
// launcher or perform an MCP handshake. These states keep registration, path
// presence, and known command semantics separate while retaining commandStale
// as the actionable compatibility flag for a missing/known-invalid launcher.
function normalizeLauncherToken(value) {
    return String(value || '').trim().replace(/^['"]|['"]$/g, '');
}

function launcherBaseName(command) {
    const token = normalizeLauncherToken(command);
    const lastSlash = Math.max(token.lastIndexOf('/'), token.lastIndexOf('\\'));
    return token.slice(lastSlash + 1).toLowerCase();
}

function launcherHasExplicitPath(command) {
    const token = normalizeLauncherToken(command);
    return token.includes('/') || token.includes('\\');
}

function hasKnownNpxPackage(args) {
    return Array.isArray(args) && args.some((arg) =>
        typeof arg === 'string' && /^genexus-mcp(?:@[^\s]+)?$/i.test(arg.trim())
    );
}

function nodeEntrypointInfo(args) {
    if (!Array.isArray(args) || args.length === 0) return { state: 'missing', value: null };

    for (let i = 0; i < args.length; i += 1) {
        const arg = typeof args[i] === 'string' ? args[i].trim() : '';
        if (!arg) continue;
        if (arg === '--') {
            return args[i + 1]
                ? { state: 'entrypoint', value: args[i + 1] }
                : { state: 'missing', value: null };
        }
        if (arg.startsWith('-')) {
            // Inline/evaluated Node programs may start MCP, but their semantics
            // cannot be established without executing them. Keep them unknown.
            if (/^(?:-e|-p|--ev(?:al)(?:=|$)|--print(?:=|$)|--check(?:=|$))/.test(arg)) {
                return { state: 'indeterminate', value: null };
            }
            continue;
        }
        return { state: 'entrypoint', value: arg };
    }

    return { state: 'missing', value: null };
}

function isKnownNodeEntrypoint(entrypoint) {
    const normalized = normalizeLauncherToken(entrypoint).replace(/\\/g, '/').toLowerCase();
    return normalized === 'cli/run.js' || normalized.endsWith('/cli/run.js');
}

function clientCommandHealth(entry, client = null, { fs: fileSystem = fs } = {}) {
    const unknown = {
        stale: false,
        reason: null,
        structuralState: 'indeterminate',
        semanticState: 'unknown',
        semanticReason: null,
        pathDrift: false,
        pathDriftReason: null
    };
    const invalid = (reason, structuralState = 'present') => ({
        stale: true,
        reason,
        structuralState,
        semanticState: 'invalid',
        semanticReason: reason,
        pathDrift: false,
        pathDriftReason: null
    });
    const valid = (structuralState = 'present') => ({
        stale: false,
        reason: null,
        structuralState,
        semanticState: 'valid',
        semanticReason: null,
        pathDrift: false,
        pathDriftReason: null
    });
    const missingLauncherReason = 'configured launcher does not exist on disk';

    if (entry === null || entry === undefined) {
        return {
            stale: false,
            reason: null,
            structuralState: 'not-registered',
            semanticState: 'not-applicable',
            semanticReason: null,
            pathDrift: false,
            pathDriftReason: null
        };
    }
    if (typeof entry !== 'object') {
        return invalid('configured local MCP entry has no command or URL', 'invalid');
    }
    if (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote') {
        return {
            stale: false,
            reason: null,
            structuralState: 'not-applicable',
            semanticState: 'not-applicable',
            semanticReason: null,
            pathDrift: false,
            pathDriftReason: null
        };
    }

    const command = normalizeLauncherToken(entry.command);
    if (!command) return invalid('configured local MCP entry has no command', 'invalid');

    const args = Array.isArray(entry.args) ? entry.args : [];
    const baseName = launcherBaseName(command);
    const explicitPath = launcherHasExplicitPath(command);
    const exists = explicitPath ? fileSystem.existsSync(command) : null;
    const knownRuntimeLauncher = baseName === 'npx'
        || baseName === 'npx.cmd'
        || baseName === 'node'
        || baseName === 'node.exe'
        || baseName === 'genexus-mcp'
        || baseName === 'genexus-mcp.cmd'
        || baseName === 'start_mcp.bat'
        || baseName === 'gxmcp.gateway.exe';
    const structuralState = explicitPath
        ? (exists ? 'present' : 'missing')
        : (knownRuntimeLauncher ? 'present' : 'indeterminate');

    if (baseName === 'gxmcp.gateway.exe') {
        if (!explicitPath) {
            return {
                ...unknown,
                semanticReason: 'direct Gateway launcher must use an explicit path that can be checked locally'
            };
        }
        if (!exists) return invalid(missingLauncherReason, 'missing');
        // A gateway exe that exists at a different absolute path is a *different,
        // working* install — a local checkout's publish/ copy, a fixed-path install,
        // or another package cache — not a broken registration. Reporting it as
        // `stale` made `npx genexus-mcp clients` tell a checkout operator to
        // re-register, which rewrote the harness away from the checkout gateway
        // (Issue #210). The drift is still surfaced (as `pathDrift`, never `stale`)
        // and doctor's `client_config_sync` check keeps the "not this CLI's packaged
        // gateway" warning with its own remediation.
        if (client && client.preferDirectGateway) {
            const currentPackageGateway = getGatewayExePath();
            if (fileSystem.existsSync(currentPackageGateway) && normalizeExePath(command) !== normalizeExePath(currentPackageGateway)) {
                return {
                    ...valid('present'),
                    pathDrift: true,
                    pathDriftReason: `configured gateway differs from this CLI's gateway (${currentPackageGateway})`
                };
            }
        }
        return valid('present');
    }

    if (baseName === 'npx' || baseName === 'npx.cmd') {
        if (!exists && explicitPath) return invalid(missingLauncherReason, 'missing');
        if (hasKnownNpxPackage(args)) return valid(structuralState);
        if (args.length === 0) {
            return invalid('npx launcher requires the genexus-mcp package in args (for example -y genexus-mcp@latest)', structuralState);
        }
        return {
            ...unknown,
            structuralState,
            semanticReason: 'npx launcher package is not recognized as genexus-mcp'
        };
    }

    if (baseName === 'node' || baseName === 'node.exe') {
        if (!exists && explicitPath) return invalid(missingLauncherReason, 'missing');
        const entrypoint = nodeEntrypointInfo(args);
        if (entrypoint.state === 'missing') {
            return invalid('node launcher requires a CLI entrypoint in args (for example cli/run.js)', structuralState);
        }
        if (entrypoint.state === 'indeterminate') {
            return {
                ...unknown,
                structuralState,
                semanticReason: 'node launcher uses an evaluated entrypoint that cannot be verified locally'
            };
        }
        if (!isKnownNodeEntrypoint(entrypoint.value)) {
            return {
                ...unknown,
                structuralState,
                semanticReason: 'node entrypoint is not recognized as the genexus-mcp CLI'
            };
        }
        if (!fileSystem.existsSync(normalizeLauncherToken(entrypoint.value))) {
            return invalid(`configured node entrypoint does not exist on disk: ${entrypoint.value}`, structuralState);
        }
        return valid(structuralState);
    }

    if (baseName === 'genexus-mcp' || baseName === 'genexus-mcp.cmd') {
        if (!exists && explicitPath) return invalid(missingLauncherReason, 'missing');
        return valid(structuralState);
    }
    if (baseName === 'start_mcp.bat' && explicitPath) {
        if (!exists) return invalid(missingLauncherReason, 'missing');
        return valid('present');
    }

    if (explicitPath && !exists) {
        return {
            ...unknown,
            stale: true,
            reason: missingLauncherReason,
            structuralState: 'missing',
            semanticReason: 'launcher command is not recognized locally; run doctor for runtime validation'
        };
    }
    if (explicitPath) {
        return {
            ...unknown,
            structuralState: 'present',
            semanticReason: 'launcher command is not recognized locally; run doctor for runtime validation'
        };
    }
    return {
        ...unknown,
        structuralState,
        semanticReason: 'launcher command cannot be classified without a known local command shape'
    };
}
function buildManualClientSetup(client, targetConfigPath = null) {
    return {
        mode: 'manual',
        managedConfigPath: client.path,
        serverName: DEFAULT_MCP_SERVER_NAME,
        transport: 'local',
        command: process.platform === 'win32' ? 'npx.cmd' : 'npx',
        args: ['-y', 'genexus-mcp@latest'],
        environment: {
            GX_CONFIG_PATH: targetConfigPath || '<config.json path printed by genexus-mcp init>'
        },
        steps: [
            'Open OpenCode Desktop settings and select MCP.',
            'Choose Add server, select Local, and enter the server name genexus18mcp.',
            'Set the command, arguments, and GX_CONFIG_PATH environment value shown above.',
            'Save the server and fully restart OpenCode Desktop so it reloads MCP configuration.',
            'Call genexus_whoami to verify the GeneXus server and selected KB.'
        ]
    };
}

// Read-only report of every supported agent on this platform: is it installed,
// is genexus registered, where, what launcher command it points at, and whether
// that command is stale. Backs the `genexus-mcp clients` command.
function clientsStatus(opts = {}) {
    const serverName = opts.serverName || DEFAULT_MCP_SERVER_NAME;
    const targets = filterClientTargets(getClientConfigTargets(), {
        ids: opts.ids,
        platform: process.platform
    });
    return targets.map((client) => {
        const det = detectClientInstalled(client);
        const entry = readClientCommandEntry(client, serverName);
        const health = clientCommandHealth(entry, client);
        const isThirdParty = entry && !isOurMcpEntry(entry.raw || entry, serverName);
        return {
            id: client.id,
            name: client.name,
            installed: det.installed,
            registered: entry !== null,
            registrationMode: client.writeSupported === false ? 'manual' : 'automatic',
            serverName,
            isThirdParty: Boolean(isThirdParty),
            writeSupported: client.writeSupported !== false,
            configPath: entry && entry.configPath ? entry.configPath : client.path,
            configPaths: entry && entry.configPath ? [entry.configPath] : [client.path],
            command: entry && entry.command ? entry.command : null,
            args: entry && Array.isArray(entry.args) ? entry.args : [],
            url: entry && entry.url ? entry.url : null,
            launcherStructuralState: health.structuralState,
            launcherSemanticState: health.semanticState,
            launcherSemanticReason: health.semanticReason,
            launcherPathDrift: health.pathDrift,
            launcherPathDriftReason: health.pathDriftReason,
            commandStale: health.stale,
            commandStaleReason: health.reason || (isThirdParty ? 'configured as third-party / HTTP MCP server (e.g. official GeneXus MCP)' : null),
            detectedAt: det.markerHit || (det.hasConfig ? client.path : null),
            note: client.writeSupported === false ? (client.manualNote || null) : null,
            manualSetup: client.writeSupported === false ? buildManualClientSetup(client) : null
        };
    });
}

function filterClientTargets(targets, opts = {}) {
    const { ids, onlyExisting, platform } = opts;
    let out = targets;
    if (platform) out = out.filter((c) => !c.platforms || c.platforms.includes(platform));
    if (ids && ids.length) {
        const set = new Set(ids);
        out = out.filter((c) => set.has(c.id));
    }
    if (onlyExisting) out = out.filter((c) => fs.existsSync(c.path));
    return out;
}

function patchClientConfig(targetConfigPath, opts = {}) {
    // Validate corporate-install env var before we write it into N client configs.
    // Otherwise we silently propagate a broken path to every AI client and the
    // user only finds out when each one fails with "Failed to connect".
    const serverName = opts.serverName || DEFAULT_MCP_SERVER_NAME;
    const force = Boolean(opts.force);
    const fileSystem = opts.fs || fs;
    const onlyExisting = opts.onlyExisting !== false;
    const candidates = filterClientTargets(getClientConfigTargets(), {
        ids: opts.ids,
        platform: process.platform
    });

    // A direct gateway path only matters to clients that can be written. Any
    // detect-only client must still receive manual setup guidance when not writable.
    const writableCandidates = candidates.filter((client) =>
        client.writeSupported !== false
        && (!onlyExisting || detectClientInstalled(client).installed)
    );
    if (writableCandidates.length > 0
        && process.env.GENEXUS_MCP_GATEWAY_EXE
        && !fs.existsSync(process.env.GENEXUS_MCP_GATEWAY_EXE)) {
        const err = new Error(
            `GENEXUS_MCP_GATEWAY_EXE points to a path that does not exist: ${process.env.GENEXUS_MCP_GATEWAY_EXE}. ` +
            `Refusing to write this into client configs. Unset the env var (to use the npx launcher) or re-run scripts/install.ps1 to materialize the exe.`
        );
        err.code = 'GATEWAY_EXE_MISSING';
        throw err;
    }

    const patched = [];
    const failed = [];
    const skipped = [];
    const verified = [];

    for (const client of candidates) {
        // Detect-only agents can't be auto-written; surface
        // the manual step instead of pretending we registered them.
        if (client.writeSupported === false) {
            const detection = detectClientInstalled(client);
            if (onlyExisting && !detection.installed) {
                skipped.push({ client: client.name, reason: 'not installed' });
                continue;
            }
            skipped.push({
                client: client.name,
                reason: client.manualNote || 'manual setup required',
                registrationMode: 'manual',
                installed: detection.installed,
                detectedAt: detection.markerHit || (detection.hasConfig ? client.path : null),
                manualSetup: buildManualClientSetup(client, targetConfigPath)
            });
            continue;
        }
        // "Installed" keys off install markers (the agent itself is present), not
        // just our config file — otherwise agents that don't pre-create their MCP
        // config (e.g. Antigravity) are wrongly skipped as "not installed".
        if (onlyExisting && !detectClientInstalled(client).installed) {
            skipped.push({ client: client.name, reason: 'not installed' });
            continue;
        }
        try {
            fileSystem.mkdirSync(path.dirname(client.path), { recursive: true });
            const launcher = getLauncher(client);
            applyClientEntry(client, launcher, targetConfigPath, {
                serverName,
                force,
                globalConfig: Boolean(opts.globalConfig),
                fs: fileSystem
            });
            // Read-back: confirm the entry is actually present, the file still
            // parses, and the generated command is a known valid local launcher.
            const writtenEntry = readClientCommandEntry(client, serverName, { fs: fileSystem });
            if (!writtenEntry) {
                throw new Error(`post-write verification failed (${serverName} entry not found after write)`);
            }
            const health = clientCommandHealth(writtenEntry, client, { fs: fileSystem });
            if (health.semanticState !== 'valid') {
                const reason = health.semanticReason || health.reason || `launcher semantic state is ${health.semanticState}`;
                throw new Error(`post-write verification failed (${reason})`);
            }
            verified.push({
                client: client.name,
                command: writtenEntry.command || null,
                args: Array.isArray(writtenEntry.args) ? writtenEntry.args : [],
                launcherStructuralState: health.structuralState,
                launcherSemanticState: health.semanticState
            });
            patched.push(client.name);
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { patched, failed, skipped, verified };
}

function unpatchClientConfig(opts = {}) {
    const serverName = opts.serverName || DEFAULT_MCP_SERVER_NAME;
    const targets = filterClientTargets(getClientConfigTargets(), {
        ids: opts.ids,
        onlyExisting: true,
        platform: process.platform
    });
    const removed = [];
    const skipped = [];
    const failed = [];

    for (const client of targets) {
        // Detect-only agents were never written by us — nothing to remove.
        if (client.writeSupported === false) {
            skipped.push({ client: client.name, reason: 'manual setup (not managed by genexus-mcp)' });
            continue;
        }
        try {
            const wasRemoved = removeClientEntry(client, { serverName });
            if (wasRemoved) removed.push(client.name);
            else skipped.push({ client: client.name, reason: `no ${serverName} entry` });
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { removed, skipped, failed };
}

function applyClientEntry(client, launcher, targetConfigPath, opts = {}) {
    const { getClientAdapter } = require('./client-adapters');
    return getClientAdapter(client.format).apply(client, launcher, targetConfigPath, opts);
}

function removeClientEntry(client, opts = {}) {
    const { getClientAdapter } = require('./client-adapters');
    return getClientAdapter(client.format).remove(client, opts);
}

function applyMcpServersJson(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false, globalConfig = false, fs: fileSystem = fs } = {}) {
    const parsed = fileSystem.existsSync(filePath) ? readJsonFileSafe(filePath, fileSystem) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    cfgObj.mcpServers = cfgObj.mcpServers || {};
    const existing = cfgObj.mcpServers[serverName];
    if (existing && !force && isThirdPartyMcpEntry(existing)) {
        const err = new Error(`Existing '${serverName}' entry is a third-party or HTTP MCP server (e.g. official GeneXus MCP). Refusing to overwrite. Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or --no-write-clients.`);
        err.code = 'MCP_SERVER_COLLISION';
        throw err;
    }
    const serverEntry = { ...launcher };
    if (globalConfig && targetConfigPath) {
        serverEntry.env = { ...(serverEntry.env || {}), GX_CONFIG_PATH: targetConfigPath };
    }
    cfgObj.mcpServers[serverName] = serverEntry;
    // If registering default genexus18mcp, clean up legacy genexus/genexus18 entries only if they are not foreign HTTP servers
    if (serverName === DEFAULT_MCP_SERVER_NAME) {
        if (cfgObj.mcpServers.genexus && !isThirdPartyMcpEntry(cfgObj.mcpServers.genexus)) {
            delete cfgObj.mcpServers.genexus;
        }
        if (cfgObj.mcpServers.genexus18 && !isThirdPartyMcpEntry(cfgObj.mcpServers.genexus18)) {
            delete cfgObj.mcpServers.genexus18;
        }
    } else if (serverName === 'genexus') {
        if (cfgObj.mcpServers.genexus18 && !isThirdPartyMcpEntry(cfgObj.mcpServers.genexus18)) {
            delete cfgObj.mcpServers.genexus18;
        }
    }
    writeClientJson(filePath, cfgObj, fileSystem);
}

function removeMcpServersJson(filePath, { serverName = DEFAULT_MCP_SERVER_NAME, fs: fileSystem = fs } = {}) {
    const parsed = readJsonFileSafe(filePath);
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    if (!cfgObj.mcpServers) return false;
    let removedAny = false;
    const keysToRemove = serverName === DEFAULT_MCP_SERVER_NAME
        ? [DEFAULT_MCP_SERVER_NAME, 'genexus', 'genexus18']
        : (serverName === 'genexus' ? ['genexus', 'genexus18'] : [serverName]);
    for (const key of keysToRemove) {
        const entry = cfgObj.mcpServers[key];
        if (entry) {
            if (key === 'genexus' && (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote')) {
                continue;
            }
            delete cfgObj.mcpServers[key];
            removedAny = true;
        }
    }
    if (!removedAny) return false;
    writeClientJson(filePath, cfgObj, fileSystem);
    return true;
}

// VS Code native MCP lives in User\mcp.json and uses a top-level `servers` map
// with `type: "stdio"` (distinct from the `mcpServers` shape Claude/Cursor use).
function applyVsCodeServersJson(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false, globalConfig = false, fs: fileSystem = fs } = {}) {
    const parsed = fileSystem.existsSync(filePath) ? readJsonFileSafe(filePath, fileSystem) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    cfgObj.servers = cfgObj.servers || {};
    const existing = cfgObj.servers[serverName];
    if (existing && !force && isThirdPartyMcpEntry(existing)) {
        const err = new Error(`Existing '${serverName}' entry is a third-party or HTTP MCP server (e.g. official GeneXus MCP). Refusing to overwrite. Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or --no-write-clients.`);
        err.code = 'MCP_SERVER_COLLISION';
        throw err;
    }
    const serverEntry = {
        type: 'stdio',
        ...launcher
    };
    if (globalConfig && targetConfigPath) {
        serverEntry.env = { ...(serverEntry.env || {}), GX_CONFIG_PATH: targetConfigPath };
    }
    cfgObj.servers[serverName] = serverEntry;
    if (serverName === DEFAULT_MCP_SERVER_NAME) {
        if (cfgObj.servers.genexus && !isThirdPartyMcpEntry(cfgObj.servers.genexus)) {
            delete cfgObj.servers.genexus;
        }
        if (cfgObj.servers.genexus18 && !isThirdPartyMcpEntry(cfgObj.servers.genexus18)) {
            delete cfgObj.servers.genexus18;
        }
    } else if (serverName === 'genexus' && cfgObj.servers.genexus18 && !isThirdPartyMcpEntry(cfgObj.servers.genexus18)) {
        delete cfgObj.servers.genexus18;
    }
    writeClientJson(filePath, cfgObj, fileSystem);
}

function removeVsCodeServersJson(filePath, { serverName = DEFAULT_MCP_SERVER_NAME, fs: fileSystem = fs } = {}) {
    const parsed = readJsonFileSafe(filePath);
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    if (!cfgObj.servers) return false;
    let removedAny = false;
    const keysToRemove = serverName === DEFAULT_MCP_SERVER_NAME
        ? [DEFAULT_MCP_SERVER_NAME, 'genexus', 'genexus18']
        : (serverName === 'genexus' ? ['genexus', 'genexus18'] : [serverName]);
    for (const key of keysToRemove) {
        const entry = cfgObj.servers[key];
        if (entry) {
            if (key === 'genexus' && (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote')) {
                continue;
            }
            delete cfgObj.servers[key];
            removedAny = true;
        }
    }
    if (!removedAny) return false;
    writeClientJson(filePath, cfgObj, fileSystem);
    return true;
}

// OpenCode 1.x uses `mcp.<name>`, while the current v2 config nests servers under
// `mcp.servers.<name>`. Keep the shape already present in the user's config so an
// upgrade does not silently move or disable their other MCP servers.
function getOpenCodeMcpContainer(cfgObj) {
    if (!cfgObj.mcp || typeof cfgObj.mcp !== 'object' || Array.isArray(cfgObj.mcp)) {
        cfgObj.mcp = {};
    }
    const nested = cfgObj.mcp.servers
        && typeof cfgObj.mcp.servers === 'object'
        && !Array.isArray(cfgObj.mcp.servers);
    return {
        mcp: cfgObj.mcp,
        servers: nested ? cfgObj.mcp.servers : cfgObj.mcp,
        nested: Boolean(nested)
    };
}

function applyOpenCodeJson(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false, globalConfig = false, alternatePaths = [], fs: fileSystem = fs } = {}) {
    const parsed = fileSystem.existsSync(filePath) ? readJsonFileSafe(filePath, fileSystem) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    // OpenCode configs carry a top-level $schema for editor validation; set it when
    // absent (new file or a config that never had one) without clobbering a custom one.
    if (!cfgObj.$schema) cfgObj.$schema = 'https://opencode.ai/config.json';
    const { mcp, servers, nested } = getOpenCodeMcpContainer(cfgObj);
    const existing = servers[serverName] || (nested ? mcp[serverName] : null);
    if (existing && !force && isThirdPartyMcpEntry(existing)) {
        const err = new Error(`Existing '${serverName}' entry is a third-party or HTTP MCP server (e.g. official GeneXus MCP). Refusing to overwrite. Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or --no-write-clients.`);
        err.code = 'MCP_SERVER_COLLISION';
        throw err;
    }
    const serverEntry = {
        type: 'local',
        command: [launcher.command, ...(launcher.args || [])],
        ...(nested ? { disabled: false } : { enabled: true })
    };
    if (globalConfig && targetConfigPath) {
        serverEntry.environment = { GX_CONFIG_PATH: targetConfigPath };
    }
    servers[serverName] = serverEntry;
    if (serverName === DEFAULT_MCP_SERVER_NAME) {
        if (servers.genexus && !isThirdPartyMcpEntry(servers.genexus)) delete servers.genexus;
        if (servers.genexus18 && !isThirdPartyMcpEntry(servers.genexus18)) delete servers.genexus18;
    } else if (serverName === 'genexus' && servers.genexus18 && !isThirdPartyMcpEntry(servers.genexus18)) {
        delete servers.genexus18;
    }
    // If a config was migrated manually and contains both shapes, leave unrelated
    // servers alone but remove our duplicate legacy entry.
    if (nested) {
        if (serverName === DEFAULT_MCP_SERVER_NAME) {
            if (mcp[DEFAULT_MCP_SERVER_NAME] && !isThirdPartyMcpEntry(mcp[DEFAULT_MCP_SERVER_NAME])) delete mcp[DEFAULT_MCP_SERVER_NAME];
            if (mcp.genexus && !isThirdPartyMcpEntry(mcp.genexus)) delete mcp.genexus;
            if (mcp.genexus18 && !isThirdPartyMcpEntry(mcp.genexus18)) delete mcp.genexus18;
        } else if (serverName === 'genexus') {
            if (mcp.genexus && !isThirdPartyMcpEntry(mcp.genexus)) delete mcp.genexus;
            if (mcp.genexus18 && !isThirdPartyMcpEntry(mcp.genexus18)) delete mcp.genexus18;
        } else {
            if (mcp[serverName] && !isThirdPartyMcpEntry(mcp[serverName])) delete mcp[serverName];
        }
    }
    writeClientJson(filePath, cfgObj, fileSystem);
    for (const alternatePath of alternatePaths || []) {
        if (!fileSystem.existsSync(alternatePath)) continue;
        applyOpenCodeJson(alternatePath, launcher, targetConfigPath, {
            serverName,
            force,
            globalConfig,
            alternatePaths: [],
            fs: fileSystem
        });
    }
}

function removeOpenCodeJson(filePath, { serverName = DEFAULT_MCP_SERVER_NAME, alternatePaths = [], fs: fileSystem = fs } = {}) {
    const parsed = readJsonFileSafe(filePath, fileSystem);
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    if (!cfgObj.mcp || typeof cfgObj.mcp !== 'object') return false;
    let removedAny = false;
    const keysToRemove = serverName === DEFAULT_MCP_SERVER_NAME
        ? [DEFAULT_MCP_SERVER_NAME, 'genexus', 'genexus18']
        : (serverName === 'genexus' ? ['genexus', 'genexus18'] : [serverName]);
    for (const key of keysToRemove) {
        const entry = cfgObj.mcp[key];
        if (entry) {
            if (key === 'genexus' && (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote')) {
                continue;
            }
            delete cfgObj.mcp[key];
            removedAny = true;
        }
    }
    if (cfgObj.mcp.servers && typeof cfgObj.mcp.servers === 'object' && !Array.isArray(cfgObj.mcp.servers)) {
        for (const key of keysToRemove) {
            const entry = cfgObj.mcp.servers[key];
            if (entry) {
                if (key === 'genexus' && (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote')) {
                    continue;
                }
                delete cfgObj.mcp.servers[key];
                removedAny = true;
            }
        }
    }
    if (removedAny) writeClientJson(filePath, cfgObj, fileSystem);
    let removedAlternate = false;
    for (const alternatePath of alternatePaths || []) {
        if (!fileSystem.existsSync(alternatePath)) continue;
        removedAlternate = removeOpenCodeJson(alternatePath, {
            serverName,
            alternatePaths: [],
            fs: fileSystem
        }) || removedAlternate;
    }
    return removedAny || removedAlternate;
}

function extractCodexTomlEntry(content, serverName = DEFAULT_MCP_SERVER_NAME) {
    if (!content) return null;
    const escaped = escapeRegex(serverName);
    const blockRe = new RegExp(`\\[mcp_servers\\.${escaped}\\]([\\s\\S]*?)(?=\\n\\[|$)`);
    const m = content.match(blockRe);
    if (!m) return null;
    const cmdMatch = m[1].match(/^\s*command\s*=\s*"((?:[^"\\]|\\.)*)"/m);
    const urlMatch = m[1].match(/^\s*url\s*=\s*"((?:[^"\\]|\\.)*)"/m);
    const argsMatch = m[1].match(/^\s*args\s*=\s*\[([\s\S]*?)\]/m);
    const envMatch = content.match(new RegExp(`\\[mcp_servers\\.${escaped}\\.env\\][\\s\\S]*?GX_CONFIG_PATH\\s*=\\s*"((?:[^"\\\\]|\\\\.)*)"`));
    const command = cmdMatch ? cmdMatch[1].replace(/\\\\/g, '\\').replace(/\\"/g, '"') : null;
    const url = urlMatch ? urlMatch[1].replace(/\\\\/g, '\\').replace(/\\"/g, '"') : null;
    let args = [];
    if (argsMatch && argsMatch[1]) {
        const rawArgs = argsMatch[1].match(/"((?:[^"\\]|\\.)*)"/g);
        if (rawArgs) {
            args = rawArgs.map((a) => a.slice(1, -1).replace(/\\\\/g, '\\').replace(/\\"/g, '"'));
        }
    }
    const gxConfig = envMatch ? envMatch[1].replace(/\\\\/g, '\\').replace(/\\"/g, '"') : null;
    return {
        command,
        args,
        url,
        env: gxConfig ? { GX_CONFIG_PATH: gxConfig } : null
    };
}

function stripCodexServerBlocks(content, serverName) {
    if (!content) return '';
    const lines = content.split('\n');
    const out = [];
    const headerRe = /^\[\[?[A-Za-z0-9_."'-]+/;
    const ourRe = new RegExp(`^\\[\\[?mcp_servers\\.${escapeRegex(serverName)}(\\.[A-Za-z0-9_."'-]+)?\\]\\]?\\s*$`);
    let inOurs = false;
    for (const line of lines) {
        if (headerRe.test(line)) {
            inOurs = ourRe.test(line);
            if (inOurs) continue;
        }
        if (inOurs) continue;
        out.push(line);
    }
    return out.join('\n').replace(/\n{3,}/g, '\n\n');
}

// Codex CLI uses TOML. We do a minimal text-merge: strip any existing
// [mcp_servers.genexus*] blocks and append fresh ones. Brittle on hand-edited
// files that put other keys after our blocks without a blank line, but
// adequate for the typical machine-managed config.
function applyCodexToml(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false, globalConfig = false } = {}) {
    let existing = '';
    if (fs.existsSync(filePath)) existing = fs.readFileSync(filePath, 'utf8');
    const existingEntry = extractCodexTomlEntry(existing, serverName);
    if (existingEntry && !force && isThirdPartyMcpEntry(existingEntry)) {
        const err = new Error(`Existing '${serverName}' entry is a third-party or HTTP MCP server (e.g. official GeneXus MCP). Refusing to overwrite. Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or --no-write-clients.`);
        err.code = 'MCP_SERVER_COLLISION';
        throw err;
    }
    let stripped = stripCodexServerBlocks(existing, serverName);
    if (serverName === DEFAULT_MCP_SERVER_NAME) {
        const legacyGx = extractCodexTomlEntry(stripped, 'genexus');
        if (legacyGx && !isThirdPartyMcpEntry(legacyGx)) {
            stripped = stripCodexServerBlocks(stripped, 'genexus');
        }
        const legacyGx18 = extractCodexTomlEntry(stripped, 'genexus18');
        if (legacyGx18 && !isThirdPartyMcpEntry(legacyGx18)) {
            stripped = stripCodexServerBlocks(stripped, 'genexus18');
        }
    } else if (serverName === 'genexus') {
        const legacyEntry = extractCodexTomlEntry(stripped, 'genexus18');
        if (legacyEntry && !isThirdPartyMcpEntry(legacyEntry)) {
            stripped = stripCodexServerBlocks(stripped, 'genexus18');
        }
    }
    const args = launcher.args || [];
    const lines = [];
    if (stripped.length && !stripped.endsWith('\n')) lines.push('');
    if (stripped.length) lines.push('');
    lines.push(`[mcp_servers.${serverName}]`);
    lines.push(`command = ${tomlString(launcher.command)}`);
    lines.push(`args = [${args.map(tomlString).join(', ')}]`);
    if (globalConfig && targetConfigPath) {
        lines.push('');
        lines.push(`[mcp_servers.${serverName}.env]`);
        lines.push(`GX_CONFIG_PATH = ${tomlString(targetConfigPath)}`);
    }
    lines.push('');
    writeClientText(filePath, stripped + lines.join('\n'));
}

function removeCodexToml(filePath, { serverName = DEFAULT_MCP_SERVER_NAME } = {}) {
    if (!fs.existsSync(filePath)) return false;
    const existing = fs.readFileSync(filePath, 'utf8');
    const keysToRemove = serverName === DEFAULT_MCP_SERVER_NAME
        ? [DEFAULT_MCP_SERVER_NAME, 'genexus', 'genexus18']
        : (serverName === 'genexus' ? ['genexus', 'genexus18'] : [serverName]);
    let stripped = existing;
    for (const key of keysToRemove) {
        const entry = extractCodexTomlEntry(stripped, key);
        if (entry) {
            if (key === 'genexus' && (entry.url || entry.type === 'http' || entry.type === 'sse' || entry.type === 'remote')) {
                continue;
            }
            stripped = stripCodexServerBlocks(stripped, key);
        }
    }
    if (stripped === existing) return false;
    writeClientText(filePath, stripped);
    return true;
}

function tomlString(value) {
    const s = String(value);
    return `"${s.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

function isPathLikelyAppLockerBlocked(exePath) {
    if (process.platform !== 'win32' || !exePath) return null;
    const norm = String(exePath).toLowerCase().replace(/\\/g, '/');
    const candidates = [
        { name: 'APPDATA', base: process.env.APPDATA },
        { name: 'LOCALAPPDATA', base: process.env.LOCALAPPDATA },
        { name: 'TEMP', base: process.env.TEMP },
        { name: 'TMP', base: process.env.TMP }
    ];
    for (const { name, base } of candidates) {
        if (!base) continue;
        const baseNorm = base.toLowerCase().replace(/\\/g, '/').replace(/\/$/, '');
        if (norm.startsWith(baseNorm + '/')) return name;
    }
    return null;
}

function normalizeExePath(p) {
    if (!p) return '';
    let s = String(p).trim().replace(/^"|"$/g, '');
    s = s.replace(/\\/g, '/').replace(/\/+$/, '');
    if (process.platform === 'win32') s = s.toLowerCase();
    return s;
}

function readOpenCodeCommandEntry(filePath, serverName, fileSystem) {
    if (!fileSystem.existsSync(filePath)) return null;
    const parsed = readJsonFileSafe(filePath, fileSystem);
    if (!parsed || typeof parsed !== 'object') return null;
    let entry = parsed.mcp?.servers?.[serverName] || parsed.mcp?.[serverName];
    if (!entry && serverName === DEFAULT_MCP_SERVER_NAME) {
        const legacyGx = parsed.mcp?.servers?.genexus || parsed.mcp?.genexus;
        if (legacyGx && !isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
        else entry = parsed.mcp?.servers?.genexus18 || parsed.mcp?.genexus18;
    }
    if (!entry) return null;
    if (Array.isArray(entry.command) && entry.command.length > 0) {
        return {
            command: entry.command[0],
            args: entry.command.slice(1),
            url: entry.url || null,
            type: entry.type || null,
            environment: entry.environment || null,
            raw: entry,
            configPath: filePath
        };
    }
    return {
        command: null,
        args: [],
        url: entry.url || null,
        type: entry.type || null,
        environment: entry.environment || null,
        raw: entry,
        configPath: filePath
    };
}

function readClientCommandEntry(client, serverName = DEFAULT_MCP_SERVER_NAME, { fs: fileSystem = fs } = {}) {
    if (client.writeSupported === false) return null;
    if (client.format === 'opencode') {
        const paths = [client.path, ...(client.alternatePaths || [])];
        for (const configPath of paths) {
            const entry = readOpenCodeCommandEntry(configPath, serverName, fileSystem);
            if (entry) return entry;
        }
        return null;
    }
    if (!fileSystem.existsSync(client.path)) return null;
    try {
        if (client.format === 'mcpServers') {
            const parsed = readJsonFileSafe(client.path, fileSystem);
            if (!parsed || typeof parsed !== 'object') return null;
            let entry = parsed.mcpServers && parsed.mcpServers[serverName];
            if (!entry && serverName === DEFAULT_MCP_SERVER_NAME) {
                const legacyGx = parsed.mcpServers && parsed.mcpServers.genexus;
                if (legacyGx && !isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
                else if (parsed.mcpServers && parsed.mcpServers.genexus18) entry = parsed.mcpServers.genexus18;
            }
            if (!entry) return null;
            return {
                command: entry.command || null,
                args: Array.isArray(entry.args) ? entry.args : [],
                url: entry.url || null,
                type: entry.type || null,
                env: entry.env || null,
                raw: entry
            };
        }
        if (client.format === 'opencode') {
            const parsed = readJsonFileSafe(client.path, fileSystem);
            if (!parsed || typeof parsed !== 'object') return null;
            let entry = parsed.mcp?.servers?.[serverName] || parsed.mcp?.[serverName];
            if (!entry && serverName === DEFAULT_MCP_SERVER_NAME) {
                const legacyGx = parsed.mcp?.servers?.genexus || parsed.mcp?.genexus;
                if (legacyGx && !isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
                else entry = parsed.mcp?.servers?.genexus18 || parsed.mcp?.genexus18;
            }
            if (!entry) return null;
            if (Array.isArray(entry.command) && entry.command.length > 0) {
                return {
                    command: entry.command[0],
                    args: entry.command.slice(1),
                    url: entry.url || null,
                    type: entry.type || null,
                    environment: entry.environment || null,
                    raw: entry
                };
            }
            return {
                command: null,
                args: [],
                url: entry.url || null,
                type: entry.type || null,
                environment: entry.environment || null,
                raw: entry
            };
        }
        if (client.format === 'vscode-servers') {
            const parsed = readJsonFileSafe(client.path, fileSystem);
            if (!parsed || typeof parsed !== 'object') return null;
            let entry = parsed.servers && parsed.servers[serverName];
            if (!entry && serverName === DEFAULT_MCP_SERVER_NAME) {
                const legacyGx = parsed.servers && parsed.servers.genexus;
                if (legacyGx && !isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
                else if (parsed.servers && parsed.servers.genexus18) entry = parsed.servers.genexus18;
            }
            if (!entry) return null;
            return {
                command: entry.command || null,
                args: Array.isArray(entry.args) ? entry.args : [],
                url: entry.url || null,
                type: entry.type || null,
                env: entry.env || null,
                raw: entry
            };
        }
        if (client.format === 'codex-toml') {
            const raw = fs.readFileSync(client.path, 'utf8');
            let entry = extractCodexTomlEntry(raw, serverName);
            if (!entry && serverName === DEFAULT_MCP_SERVER_NAME) {
                const legacyGx = extractCodexTomlEntry(raw, 'genexus');
                if (legacyGx && !isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
                else entry = extractCodexTomlEntry(raw, 'genexus18');
            }
            if (!entry) return null;
            return {
                command: entry.command || null,
                args: entry.args || [],
                url: entry.url || null,
                type: entry.type || null,
                env: entry.env || null,
                raw: entry
            };
        }
    } catch {
        return null;
    }
    return null;
}

function getLocalAppDataCacheDir() {
    if (process.platform !== 'win32') return null;
    const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(base, 'GenexusMCP');
}

function getGeneXusMajor(version) {
    const match = String(version || '').match(/^\s*(10\.[1-3](?!\d)|\d+)/);
    return match ? match[1] : null;
}

function matchesPreferredGeneXusMajor(gxPath, preferredMajor) {
    if (preferredMajor === null || preferredMajor === undefined || String(preferredMajor).trim() === '') return true;
    const identity = readGeneXusInstallationIdentity(gxPath);
    return String(identity.major || '') === String(preferredMajor);
}

function readExecutableProductVersion(exePath) {
    if (process.platform !== 'win32' || !exePath || !fs.existsSync(exePath)) return null;
    try {
        const { execFileSync } = require('child_process');
        const output = execFileSync('powershell.exe', [
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            '$versionInfo = (Get-Item -LiteralPath $env:GXMCP_VERSION_EXE -ErrorAction Stop).VersionInfo; if ($versionInfo.ProductVersion) { $versionInfo.ProductVersion } else { $versionInfo.FileVersion }'
        ], {
            encoding: 'utf8',
            stdio: ['ignore', 'pipe', 'ignore'],
            windowsHide: true,
            timeout: 3000,
            env: { ...process.env, GXMCP_VERSION_EXE: exePath }
        });
        return output.split(/\r?\n/).map((line) => line.trim()).find(Boolean) || null;
    } catch {
        return null;
    }
}

function readGeneXusInstallationIdentity(gxPath, options = {}) {
    if (!gxPath) return { version: null, major: null, source: 'unavailable' };

    let unresolvedVersionFile = null;
    const candidates = [
        path.join(gxPath, 'version.txt'),
        path.join(gxPath, 'Version.txt'),
        path.join(gxPath, 'GeneXus.version')
    ];
    for (const candidate of candidates) {
        try {
            const raw = fs.readFileSync(candidate, 'utf8').trim();
            const version = raw.split(/\r?\n/)[0].trim();
            if (version) {
                const major = getGeneXusMajor(version);
                if (major) return { version, major, source: 'version-file' };
                unresolvedVersionFile = unresolvedVersionFile || version;
            }
        } catch {
        }
    }

    const readExecutableVersion = typeof options.readExecutableVersion === 'function'
        ? options.readExecutableVersion
        : readExecutableProductVersion;
    let executableVersion = readExecutableVersion(path.join(gxPath, 'GeneXus.exe'));
    if (!executableVersion) {
        executableVersion = readExecutableVersion(path.join(gxPath, 'gx.exe'));
    }
    if (!executableVersion) {
        executableVersion = readExecutableVersion(path.join(gxPath, 'gxw32.exe'));
    }
    if (executableVersion) {
        return {
            version: executableVersion,
            major: getGeneXusMajor(executableVersion),
            source: 'executable-metadata'
        };
    }

    const pathMatch = path.basename(String(gxPath)).match(/^GeneXus\s*(\d+)/i);
    if (pathMatch && fs.existsSync(gxPath)) return { version: null, major: pathMatch[1], source: 'path-name' };
    if (unresolvedVersionFile) return { version: unresolvedVersionFile, major: null, source: 'version-file' };
    return { version: null, major: null, source: 'unavailable' };
}

function readGeneXusVersionFromInstall(gxPath, options) {
    return readGeneXusInstallationIdentity(gxPath, options).version;
}

function isWellFormedXml(source) {
    const xml = String(source || '').replace(/^\uFEFF/, '');
    const stack = [];
    let rootCount = 0;
    let index = 0;
    while (true) {
        const open = xml.indexOf('<', index);
        if (open < 0) break;
        if (xml.startsWith('<!--', open)) {
            const endComment = xml.indexOf('-->', open + 4);
            if (endComment < 0) return false;
            index = endComment + 3;
            continue;
        }
        if (xml.startsWith('<![CDATA[', open)) {
            const endCdata = xml.indexOf(']]>', open + 9);
            if (endCdata < 0) return false;
            index = endCdata + 3;
            continue;
        }
        if (xml.startsWith('<?', open)) {
            const endInstruction = xml.indexOf('?>', open + 2);
            if (endInstruction < 0) return false;
            index = endInstruction + 2;
            continue;
        }

        let end = open + 1;
        let quote = null;
        for (; end < xml.length; end++) {
            const character = xml[end];
            if (quote) {
                if (character === quote) quote = null;
            } else if (character === '"' || character === "'") {
                quote = character;
            } else if (character === '>') {
                break;
            }
        }
        if (end >= xml.length || quote) return false;

        const body = xml.slice(open + 1, end).trim();
        if (!body || body.startsWith('!')) {
            index = end + 1;
            continue;
        }
        if (body.startsWith('/')) {
            const closing = body.slice(1).trim();
            const closingMatch = closing.match(/^([A-Za-z_][A-Za-z0-9_.:-]*)\s*$/);
            if (!closingMatch || stack.length === 0 || stack[stack.length - 1] !== closingMatch[1]) return false;
            stack.pop();
        } else {
            const opening = body.replace(/\/\s*$/, '').trim();
            const openingMatch = opening.match(/^([A-Za-z_][A-Za-z0-9_.:-]*)\b/);
            if (!openingMatch) return false;
            if (stack.length === 0) rootCount++;
            if (!body.endsWith('/')) stack.push(openingMatch[1]);
        }
        index = end + 1;
    }
    return rootCount === 1 && stack.length === 0;
}

function readGeneXusKbIdentity(kbPath) {
    if (!kbPath) return { version: null, major: null, source: 'unavailable', reason: 'missing-kb-path' };

    let gxwFiles;
    let gxiFiles = [];
    let classicMarkerCount = 0;
    try {
        const allFiles = fs.readdirSync(kbPath);
        gxwFiles = allFiles
            .filter((fileName) => fileName.toLowerCase().endsWith('.gxw'))
            .map((fileName) => path.join(kbPath, fileName));
        gxiFiles = allFiles
            .filter((fileName) => fileName.toLowerCase().endsWith('.gxi'))
            .map((fileName) => path.join(kbPath, fileName));
        const classicMarkers = new Set(['data001', 'gxspc001', 'kbdata', 'attribut.dat', 'att.xpw', 'objects.dat', 'objects.idx']);
        classicMarkerCount = allFiles.filter((fileName) => classicMarkers.has(fileName.toLowerCase())).length;
    } catch {
        return { version: null, major: null, source: 'unavailable', reason: 'unreadable-kb-path' };
    }
    if (gxwFiles.length === 0) {
        if (gxiFiles.length > 0) {
            return { version: '9.0', major: '9', source: 'gxi-classic', reason: null };
        }
        if (classicMarkerCount >= 2) {
            return { version: null, major: null, source: 'classic-dat', reason: 'classic-generation-requires-provider' };
        }
        return { version: null, major: null, source: 'unavailable', reason: 'no-gxw' };
    }
    if (gxwFiles.length > 1) return { version: null, major: null, source: 'unavailable', reason: 'multiple-gxw' };

    try {
        const source = fs.readFileSync(gxwFiles[0], 'utf8');
        if (!source.trim()) return { version: null, major: null, source: 'unavailable', reason: 'empty-gxw' };
        if (!isWellFormedXml(source)) return { version: null, major: null, source: 'unavailable', reason: 'malformed-gxw' };
        const raw = source.replace(/<!--[\s\S]*?-->/g, '');
        for (const field of ['VersionNumber', 'FriendlyVersion']) {
            const pattern = new RegExp(`<(?:(?:[\\w.-]+):)?${field}\\b[^>]*>([\\s\\S]*?)<\\/(?:(?:[\\w.-]+):)?${field}\\s*>`, 'i');
            const match = raw.match(pattern);
            const version = match && match[1] ? match[1].replace(/<[^>]+>/g, '').trim() : '';
            if (version) return { version, major: getGeneXusMajor(version), source: 'gxw-version' };
        }
        return { version: null, major: null, source: 'unavailable', reason: 'missing-gxw-version' };
    } catch {
        return { version: null, major: null, source: 'unavailable', reason: 'unreadable-gxw' };
    }
}

function compareGeneXusKbAndInstallation(kbPath, gxPath, options = {}) {
    const kb = readGeneXusKbIdentity(kbPath);
    const gx = readGeneXusInstallationIdentity(gxPath, options);
    let status = 'unresolved';
    // A classic .gxi is shared by the GX8/GX9 family and does not carry a
    // reliable generation marker. Do not turn the historical default of 9
    // into a false GX8-vs-GX9 mismatch; the provider open is the authority.
    if ((kb.source === 'gxi-classic' || kb.source === 'classic-dat') && (gx.major === '8' || gx.major === '9')) {
        status = 'unresolved';
    } else if (kb.major && gx.major) {
        status = kb.major === gx.major ? 'match' : 'mismatch';
    }
    return { status, kb, gx };
}

function normalizeKbCatalog(raw) {
    const normalized = {};
    const entries = {};
    const add = (name, value) => {
        if (typeof name !== 'string' || !name) return;
        if (typeof value === 'string' && value) {
            normalized[name] = value;
            return;
        }
        if (!value || typeof value !== 'object') return;
        const kbPath = typeof value.path === 'string' ? value.path : value.Path;
        if (typeof kbPath !== 'string' || !kbPath) return;
        normalized[name] = kbPath;
        const metadata = { alias: name, path: kbPath };
        let hasLegacyMetadata = false;
        for (const [source, target] of [['driver', 'driver'], ['Driver', 'driver'], ['installationPath', 'installationPath'], ['InstallationPath', 'installationPath'], ['major', 'major'], ['Major', 'major']]) {
            if (typeof value[source] === 'string' && value[source]) {
                metadata[target] = value[source];
                hasLegacyMetadata = true;
            }
        }
        if (hasLegacyMetadata) entries[name] = metadata;
    };

    if (Array.isArray(raw)) {
        for (const entry of raw) {
            if (!entry || typeof entry !== 'object') continue;
            const name = typeof entry.alias === 'string' ? entry.alias : entry.Alias;
            add(name, entry);
        }
        Object.defineProperty(normalized, '__entries', { value: entries, enumerable: false, writable: true });
        return normalized;
    }

    if (raw && typeof raw === 'object') {
        for (const [name, value] of Object.entries(raw)) add(name, value);
    }
    Object.defineProperty(normalized, '__entries', { value: entries, enumerable: false, writable: true });
    return normalized;
}

function readKbCatalog(configPath, configOverride = undefined) {
    if (!configPath && configOverride === undefined) return { kbs: {}, activeKb: null, kbPath: null };
    const cfg = configOverride === undefined ? readJsonFileSafe(configPath) : configOverride;
    if (!cfg) return { kbs: {}, activeKb: null, kbPath: null };
    const env = cfg.Environment || {};
    const activeKb = typeof env.ActiveKb === 'string' && env.ActiveKb
        ? env.ActiveKb
        : (typeof env.DefaultKb === 'string' && env.DefaultKb ? env.DefaultKb : null);
    return {
        kbs: normalizeKbCatalog(env.KBs),
        activeKb,
        kbPath: typeof env.KBPath === 'string' ? env.KBPath : null
    };
}

function writeKbCatalog(configPath, { kbs, activeKb, kbPath }) {
    const cfg = readJsonFileSafe(configPath) || {};
    cfg.Environment = cfg.Environment || {};
    const serializedKbs = {};
    for (const [name, kbPathValue] of Object.entries(kbs || {})) {
        const metadata = kbs.__entries && kbs.__entries[name];
        serializedKbs[name] = metadata
            ? {
                Path: kbPathValue,
                ...(metadata.driver ? { Driver: metadata.driver } : {}),
                ...(metadata.installationPath ? { InstallationPath: metadata.installationPath } : {}),
                ...(metadata.major ? { Major: metadata.major } : {})
            }
            : kbPathValue;
    }
    cfg.Environment.KBs = serializedKbs;
    if (activeKb) {
        cfg.Environment.ActiveKb = activeKb;
        cfg.Environment.DefaultKb = activeKb;
    } else {
        delete cfg.Environment.ActiveKb;
        delete cfg.Environment.DefaultKb;
    }
    if (kbPath) cfg.Environment.KBPath = kbPath;
    else delete cfg.Environment.KBPath;
    writeFileAtomic(configPath, JSON.stringify(cfg, null, 2));
}

function addKbToConfig(configPath, name, kbPath) {
    const catalog = readKbCatalog(configPath);
    const alreadyRegistered = catalog.kbs[name] === kbPath;
    const willBecomeActive = !catalog.activeKb;
    if (alreadyRegistered && !willBecomeActive) {
        return catalog;
    }
    catalog.kbs[name] = kbPath;
    if (willBecomeActive) {
        catalog.activeKb = name;
        catalog.kbPath = kbPath;
    }
    writeKbCatalog(configPath, catalog);
    return catalog;
}

function removeKbFromConfig(configPath, name) {
    const catalog = readKbCatalog(configPath);
    if (!(name in catalog.kbs)) return { catalog, removed: false };
    delete catalog.kbs[name];
    if (catalog.activeKb === name) {
        const remainingNames = Object.keys(catalog.kbs);
        catalog.activeKb = remainingNames[0] || null;
        catalog.kbPath = catalog.activeKb ? catalog.kbs[catalog.activeKb] : null;
    }
    writeKbCatalog(configPath, catalog);
    return { catalog, removed: true };
}

function switchActiveKb(configPath, { name, path: explicitPath }) {
    const catalog = readKbCatalog(configPath);

    let targetName = name;
    let targetPath = explicitPath;

    if (explicitPath && !name) {
        const existing = Object.entries(catalog.kbs).find(([, p]) => p === explicitPath);
        if (existing) {
            targetName = existing[0];
        } else {
            targetName = path.basename(explicitPath);
            if (catalog.kbs[targetName] && catalog.kbs[targetName] !== explicitPath) {
                return {
                    ok: false,
                    reason: `Name '${targetName}' is already registered to a different path (${catalog.kbs[targetName]}). Pass --name explicitly to disambiguate.`
                };
            }
            catalog.kbs[targetName] = explicitPath;
        }
    } else if (name) {
        if (!(name in catalog.kbs)) {
            return { ok: false, reason: `KB '${name}' is not registered. Use \`genexus-mcp kb add\` first or pass --path.` };
        }
        targetPath = catalog.kbs[name];
    } else {
        return { ok: false, reason: 'Either --name or --path is required.' };
    }

    catalog.activeKb = targetName;
    catalog.kbPath = targetPath;
    writeKbCatalog(configPath, catalog);
    return { ok: true, catalog, switchedTo: { name: targetName, path: targetPath } };
}

function applyLauncherConfigOrExit({ cwd, stderr, quiet }) {
    const log = (msg) => {
        if (!quiet) stderr.write(`${msg}\n`);
    };

    const cwdConfigPath = path.join(cwd, 'config.json');

    if (process.env.GX_CONFIG_PATH) {
        return { ok: true };
    }

    if (fs.existsSync(cwdConfigPath)) {
        process.env.GX_CONFIG_PATH = cwdConfigPath;
        return { ok: true };
    }

    const discoveredGxPath = discoverGeneXusInstallation();
    if (!discoveredGxPath) {
        log('[genexus-mcp] ERROR: No config.json found and GeneXus installation auto-discovery failed.');
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    const userMcpDir = path.join(os.homedir(), '.genexus-mcp');
    const userMcpConfigPath = path.join(userMcpDir, 'config.json');


    if (!directoryLooksLikeKnowledgeBase(cwd)) {
        if (fs.existsSync(userMcpConfigPath)) {
            process.env.GX_CONFIG_PATH = userMcpConfigPath;
            return { ok: true };
        }
        log(`[genexus-mcp] Auto-discovered GeneXus at: ${discoveredGxPath}`);
        log(`[genexus-mcp] Current directory is not a GeneXus KB. Generating neutral user config at: ${userMcpConfigPath}`);
        fs.mkdirSync(userMcpDir, { recursive: true });
        const neutralConfig = generateNeutralConfig(discoveredGxPath);
        writeFileAtomic(userMcpConfigPath, JSON.stringify(neutralConfig, null, 2));
        process.env.GX_CONFIG_PATH = userMcpConfigPath;
        return { ok: true };
    }

    const kbIdentity = readGeneXusKbIdentity(cwd);
    if (!kbIdentity.major) {
        log('[genexus-mcp] ERROR: Zero-config could not determine the KB GeneXus major safely.');
        log('[genexus-mcp] Fix with: npx genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"');
        return { ok: false };
    }

    const foundGxPath = discoverGeneXusInstallationForKb(kbIdentity);
    if (!foundGxPath) {
        log('[genexus-mcp] ERROR: No GeneXus installation matching the KB major was auto-discovered.');
        log(`[genexus-mcp] KB major: ${kbIdentity.major}`);
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    const compatibility = compareGeneXusKbAndInstallation(cwd, foundGxPath);
    if (compatibility.status === 'mismatch') {
        log(`[genexus-mcp] ERROR: Auto-discovered SDK major ${compatibility.gx.major} does not match KB major ${compatibility.kb.major}.`);
        log('[genexus-mcp] Fix with: npx genexus-mcp init --kb "<kbPath>" --gx "<matching GeneXus path>"');
        return { ok: false };
    }
    if (compatibility.status !== 'match') {
        log('[genexus-mcp] ERROR: Zero-config could not verify that the discovered GeneXus SDK matches the KB major.');
        log(`[genexus-mcp] KB major: ${compatibility.kb.major}; SDK detection: ${compatibility.gx.source}`);
        log('[genexus-mcp] Fix with: npx genexus-mcp init --kb "<kbPath>" --gx "<matching GeneXus path>"');
        return { ok: false };
    }

    log(`[genexus-mcp] Auto-discovered GeneXus ${kbIdentity.major} at: ${foundGxPath}`);
    log(`[genexus-mcp] Generating default config.json for KB at: ${cwd}`);

    const defaultConfig = generateConfig(foundGxPath, cwd);
    writeFileAtomic(cwdConfigPath, JSON.stringify(defaultConfig, null, 2));
    process.env.GX_CONFIG_PATH = cwdConfigPath;

    return { ok: true };
}

module.exports = {
    generateConfig,
    generateNeutralConfig,
    getGatewayExePath,
    getToolDefinitionsPath,
    getGeneXusVersionCatalog,
    getGeneXusCatalogEntries,
    discoverGeneXusInstallation,
    discoverGeneXusInstallationForKb,
    discoverGeneXusFromRegistry,
    discoverKnowledgeBase,
    discoverKnowledgeBases,
    directoryLooksLikeKnowledgeBase,
    readJsonFileSafe,
    resolveConfigPathNoMutate,
    migrateLegacyConfig,
    createConfigFile,
    patchClientConfig,
    unpatchClientConfig,
    getClientConfigTargets,
    detectClientInstalled,
    clientsStatus,
    listSupportedClientIds,
    filterClientTargets,
    getLocalAppDataCacheDir,
    getGeneXusMajor,
    readGeneXusInstallationIdentity,
    readGeneXusVersionFromInstall,
    readGeneXusKbIdentity,
    compareGeneXusKbAndInstallation,
    readKbCatalog,
    addKbToConfig,
    removeKbFromConfig,
    switchActiveKb,
    applyLauncherConfigOrExit,
    isPathLikelyAppLockerBlocked,
    normalizeExePath,
    getLauncher,
    readClientCommandEntry,
    isOurMcpEntry,
    DEFAULT_MCP_SERVER_NAME,
    applyMcpServersJson,
    removeMcpServersJson,
    applyVsCodeServersJson,
    removeVsCodeServersJson,
    applyOpenCodeJson,
    removeOpenCodeJson,
    applyCodexToml,
    removeCodexToml,
    extractCodexTomlEntry,
    isThirdPartyMcpEntry,
    applyClientEntry,
    removeClientEntry
};
