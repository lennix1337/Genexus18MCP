const path = require('path');
const fs = require('fs');
const os = require('os');

function generateConfig(gxPath, kbPath) {
    return {
        GeneXus: { InstallationPath: gxPath },
        Server: {
            HttpPort: 5000,
            McpStdio: true,
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
    for (const candidate of candidates) {
        if (fs.existsSync(candidate)) return candidate;
    }
    // Nothing found — return the canonical packaged path so the doctor's
    // existence check reports the location that SHOULD have the file.
    return candidates[0];
}

function discoverGeneXusFromRegistry() {
    if (process.platform !== 'win32') return null;
    try {
        const { execFileSync } = require('child_process');
        const versions = ['GeneXus 18', 'GeneXus 17', 'GeneXus 16'];
        const hives = [
            'HKLM\\SOFTWARE\\WOW6432Node\\Artech',
            'HKLM\\SOFTWARE\\Artech',
            'HKCU\\SOFTWARE\\Artech'
        ];
        for (const hive of hives) {
            for (const ver of versions) {
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
                        if (candidate && fs.existsSync(path.join(candidate, 'genexus.exe'))) {
                            return candidate;
                        }
                    }
                } catch {
                }
            }
        }
    } catch {
    }
    return null;
}

function discoverGeneXusInstallation() {
    if (process.env.GENEXUS_HOME) {
        const candidate = process.env.GENEXUS_HOME.replace(/[\\/]+$/, '');
        if (fs.existsSync(path.join(candidate, 'genexus.exe'))) return candidate;
    }

    const fromRegistry = discoverGeneXusFromRegistry();
    if (fromRegistry) return fromRegistry;

    const programDirs = [];
    if (process.env['ProgramFiles(x86)']) programDirs.push(process.env['ProgramFiles(x86)']);
    if (process.env.ProgramFiles) programDirs.push(process.env.ProgramFiles);
    // Cover non-default drives where IT often installs SDKs.
    for (const drive of ['C', 'D', 'E']) {
        programDirs.push(`${drive}:\\Program Files (x86)`);
        programDirs.push(`${drive}:\\Program Files`);
    }

    const versions = ['GeneXus18', 'GeneXus17', 'GeneXus16'];
    const seen = new Set();
    for (const base of programDirs) {
        const root = path.join(base, 'GeneXus');
        const key = root.toLowerCase();
        if (seen.has(key)) continue;
        seen.add(key);
        for (const ver of versions) {
            const candidate = path.join(root, ver);
            if (fs.existsSync(path.join(candidate, 'genexus.exe'))) {
                return candidate;
            }
        }
        // Also scan any GeneXus* sibling (e.g. custom-named "GeneXus18 U10").
        try {
            if (fs.existsSync(root)) {
                for (const entry of fs.readdirSync(root)) {
                    if (!/^GeneXus/i.test(entry)) continue;
                    const candidate = path.join(root, entry);
                    if (fs.existsSync(path.join(candidate, 'genexus.exe'))) {
                        return candidate;
                    }
                }
            }
        } catch {
        }
    }

    const fromPath = discoverGeneXusFromPath();
    if (fromPath) return fromPath;

    return null;
}

function discoverGeneXusFromPath() {
    if (process.platform !== 'win32') return null;
    try {
        const { execFileSync } = require('child_process');
        const out = execFileSync('where.exe', ['genexus.exe'], {
            encoding: 'utf8',
            stdio: ['ignore', 'pipe', 'ignore'],
            windowsHide: true,
            timeout: 3000
        });
        const first = out.split(/\r?\n/).map((s) => s.trim()).find(Boolean);
        if (first && fs.existsSync(first)) {
            return path.dirname(first);
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
        return files.some((f) => f.toLowerCase().endsWith('.gxw') || f.toLowerCase() === 'knowledgebase.connection');
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

function readJsonFileSafe(filePath) {
    try {
        const raw = fs.readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '');
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
function writeFileAtomic(filePath, content) {
    const tmp = `${filePath}.tmp-${process.pid}`;
    fs.writeFileSync(tmp, content);
    try {
        fs.renameSync(tmp, filePath);
    } catch (err) {
        try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
        throw err;
    }
}

// Back up a client config once per process run before the first mutation, so the
// user has a restore point (the old build-from-source install.ps1 did this; the
// CLI now owns it). Best-effort \u2014 a failed backup never blocks the write.
// After writing a new backup, prune old .bak files for the same config so at
// most BAK_KEEP_COUNT backups exist (oldest removed first).
const BAK_KEEP_COUNT = 5;
const _backedUpThisRun = new Set();
function backupClientConfigOnce(filePath) {
    if (!fs.existsSync(filePath)) return null;
    // Case-fold the dedupe key only on Windows; lowercasing on a case-sensitive
    // filesystem could merge two genuinely distinct paths.
    const resolved = path.resolve(filePath);
    const key = process.platform === 'win32' ? resolved.toLowerCase() : resolved;
    if (_backedUpThisRun.has(key)) return null;
    try {
        const d = new Date();
        const stamp = d.toISOString().replace(/[-:T]/g, '').slice(0, 14);
        const bak = `${filePath}.${stamp}.bak`;
        fs.copyFileSync(filePath, bak);
        _backedUpThisRun.add(key);
        // Prune: keep only the BAK_KEEP_COUNT most-recent .bak files for this config.
        try {
            const dir = path.dirname(resolved);
            const base = path.basename(resolved);
            const bakPattern = new RegExp(`^${base.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.\\d{14}\\.bak$`);
            const existing = fs.readdirSync(dir)
                .filter(f => bakPattern.test(f))
                .map(f => path.join(dir, f))
                .sort(); // ISO timestamp stamps sort lexicographically = chronologically
            if (existing.length > BAK_KEEP_COUNT) {
                const toRemove = existing.slice(0, existing.length - BAK_KEEP_COUNT);
                for (const old of toRemove) {
                    try { fs.rmSync(old, { force: true }); } catch { /* best-effort */ }
                }
            }
        } catch { /* pruning is best-effort; never block the backup */ }
        return bak;
    } catch {
        return null;
    }
}

// Write JSON to a client config: back up, serialize, write atomically.
function writeClientJson(filePath, obj) {
    backupClientConfigOnce(filePath);
    writeFileAtomic(filePath, JSON.stringify(obj, null, 2));
}

// Write raw text to a client config (e.g. Codex TOML): back up + write atomically.
function writeClientText(filePath, content) {
    backupClientConfigOnce(filePath);
    writeFileAtomic(filePath, content);
}

function resolveConfigPathNoMutate(cwd) {
    const cwdConfigPath = path.join(cwd, 'config.json');
    if (process.env.GX_CONFIG_PATH && fs.existsSync(process.env.GX_CONFIG_PATH)) {
        return process.env.GX_CONFIG_PATH;
    }
    if (fs.existsSync(cwdConfigPath)) {
        return cwdConfigPath;
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
        Environment: { ...baseConfig.Environment, ...preservedEnv }
    };

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
            // Detect-only: the Desktop app's MCP config schema differs from the CLI
            // and isn't auto-written yet. We report it so the user knows it's there
            // and how to wire it up, but never mutate its config blindly.
            format: 'manual',
            writeSupported: false,
            manualNote: 'OpenCode Desktop manual setup: Settings > MCP > Add server > Local; name genexus18mcp; command npx.cmd on Windows (npx elsewhere); args -y genexus-mcp@latest; environment GX_CONFIG_PATH=<config.json path printed by init>; save, fully restart the app, then call genexus_whoami. The CLI does not write the app-managed Desktop mcp.json.',
            path: path.join(appData, 'ai.opencode.desktop', 'mcp.json'),
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
    return {
        installed: hasConfig || markerHit !== null,
        hasConfig,
        markerHit,
        markersChecked: markers
    };
}

function listSupportedClientIds() {
    return getClientConfigTargets().map((c) => c.id);
}

// Judge whether a registered launcher command is healthy. npx/node/genexus-mcp
// shims resolve at runtime so we can't fault them; any other launcher referenced
// by an explicit path (a separator in the command) that no longer exists on disk
// is the classic "Failed to connect / still on old version" cause after an
// install dir moved or was cleaned — covers .exe, .bat, .cmd, .sh, extensionless.
function clientCommandHealth(entry, client = null) {
    if (!entry || !entry.command) return { stale: false, reason: null };
    const cmd = String(entry.command);
    if (/(^|[\\/])(npx|npx\.cmd|node|node\.exe|genexus-mcp|genexus-mcp\.cmd)$/i.test(cmd)) {
        return { stale: false, reason: null };
    }
    if (/[\\/]/.test(cmd) && !fs.existsSync(cmd)) {
        return { stale: true, reason: 'configured launcher does not exist on disk' };
    }
    if (client && client.preferDirectGateway && /GxMcp\.Gateway\.exe$/i.test(cmd)) {
        const currentPackageGateway = getGatewayExePath();
        if (fs.existsSync(currentPackageGateway) && normalizeExePath(cmd) !== normalizeExePath(currentPackageGateway)) {
            return { stale: true, reason: 'configured launcher points at a different package gateway; re-register the client' };
        }
    }
    return { stale: false, reason: null };
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
            configPath: client.path,
            command: entry && entry.command ? entry.command : null,
            url: entry && entry.url ? entry.url : null,
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
    const onlyExisting = opts.onlyExisting !== false;
    const candidates = filterClientTargets(getClientConfigTargets(), {
        ids: opts.ids,
        platform: process.platform
    });

    // A direct gateway path only matters to clients that can be written. A
    // detect-only client such as OpenCode Desktop must still receive manual
    // setup guidance when its app-managed config is not detectable.
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

    for (const client of candidates) {
        // Detect-only agents (e.g. OpenCode Desktop) can't be auto-written; surface
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
            fs.mkdirSync(path.dirname(client.path), { recursive: true });
            applyClientEntry(client, getLauncher(client), targetConfigPath, { serverName, force });
            // Read-back: confirm the entry is actually present and the file still
            // parses, so a silently-corrupted write is reported as a failure.
            if (!readClientCommandEntry(client, serverName)) {
                throw new Error(`post-write verification failed (${serverName} entry not found after write)`);
            }
            patched.push(client.name);
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { patched, failed, skipped };
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

function applyMcpServersJson(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false } = {}) {
    const parsed = fs.existsSync(filePath) ? readJsonFileSafe(filePath) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    cfgObj.mcpServers = cfgObj.mcpServers || {};
    const existing = cfgObj.mcpServers[serverName];
    if (existing && !force && isThirdPartyMcpEntry(existing)) {
        const err = new Error(`Existing '${serverName}' entry is a third-party or HTTP MCP server (e.g. official GeneXus MCP). Refusing to overwrite. Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or --no-write-clients.`);
        err.code = 'MCP_SERVER_COLLISION';
        throw err;
    }
    cfgObj.mcpServers[serverName] = { ...launcher, env: { GX_CONFIG_PATH: targetConfigPath } };
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
    writeClientJson(filePath, cfgObj);
}

function removeMcpServersJson(filePath, { serverName = DEFAULT_MCP_SERVER_NAME } = {}) {
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
    writeClientJson(filePath, cfgObj);
    return true;
}

// VS Code native MCP lives in User\mcp.json and uses a top-level `servers` map
// with `type: "stdio"` (distinct from the `mcpServers` shape Claude/Cursor use).
function applyVsCodeServersJson(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false } = {}) {
    const parsed = fs.existsSync(filePath) ? readJsonFileSafe(filePath) : {};
    if (parsed === null) throw new Error('Invalid JSON');
    const cfgObj = parsed || {};
    cfgObj.servers = cfgObj.servers || {};
    const existing = cfgObj.servers[serverName];
    if (existing && !force && isThirdPartyMcpEntry(existing)) {
        const err = new Error(`Existing '${serverName}' entry is a third-party or HTTP MCP server (e.g. official GeneXus MCP). Refusing to overwrite. Use --server-name=<customName> (e.g. --server-name Gx18byLennix) or --no-write-clients.`);
        err.code = 'MCP_SERVER_COLLISION';
        throw err;
    }
    cfgObj.servers[serverName] = {
        type: 'stdio',
        ...launcher,
        env: { GX_CONFIG_PATH: targetConfigPath }
    };
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
    writeClientJson(filePath, cfgObj);
}

function removeVsCodeServersJson(filePath, { serverName = DEFAULT_MCP_SERVER_NAME } = {}) {
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
    writeClientJson(filePath, cfgObj);
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

function applyOpenCodeJson(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false } = {}) {
    const parsed = fs.existsSync(filePath) ? readJsonFileSafe(filePath) : {};
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
    servers[serverName] = {
        type: 'local',
        command: [launcher.command, ...(launcher.args || [])],
        environment: { GX_CONFIG_PATH: targetConfigPath },
        ...(nested ? { disabled: false } : { enabled: true })
    };
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
    writeClientJson(filePath, cfgObj);
}

function removeOpenCodeJson(filePath, { serverName = DEFAULT_MCP_SERVER_NAME } = {}) {
    const parsed = readJsonFileSafe(filePath);
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
    if (!removedAny) return false;
    writeClientJson(filePath, cfgObj);
    return true;
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
function applyCodexToml(filePath, launcher, targetConfigPath, { serverName = DEFAULT_MCP_SERVER_NAME, force = false } = {}) {
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
    lines.push('');
    lines.push(`[mcp_servers.${serverName}.env]`);
    lines.push(`GX_CONFIG_PATH = ${tomlString(targetConfigPath)}`);
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

function readClientCommandEntry(client, serverName = DEFAULT_MCP_SERVER_NAME) {
    if (client.writeSupported === false) return null;
    if (!fs.existsSync(client.path)) return null;
    try {
        if (client.format === 'mcpServers') {
            const parsed = readJsonFileSafe(client.path);
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
            const parsed = readJsonFileSafe(client.path);
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
            const parsed = readJsonFileSafe(client.path);
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

function readGeneXusVersionFromInstall(gxPath) {
    if (!gxPath) return null;
    const candidates = [
        path.join(gxPath, 'version.txt'),
        path.join(gxPath, 'Version.txt'),
        path.join(gxPath, 'GeneXus.version')
    ];
    for (const candidate of candidates) {
        try {
            const raw = fs.readFileSync(candidate, 'utf8').trim();
            if (raw) return raw.split(/\r?\n/)[0].trim();
        } catch {
        }
    }
    return null;
}

function normalizeKbCatalog(raw) {
    if (Array.isArray(raw)) {
        const normalized = {};
        for (const entry of raw) {
            if (!entry || typeof entry !== 'object') continue;
            const name = typeof entry.alias === 'string' ? entry.alias : entry.Alias;
            const kbPath = typeof entry.path === 'string' ? entry.path : entry.Path;
            if (typeof name === 'string' && name && typeof kbPath === 'string' && kbPath) {
                normalized[name] = kbPath;
            }
        }
        return normalized;
    }

    return raw && typeof raw === 'object' ? raw : {};
}

function readKbCatalog(configPath) {
    if (!configPath) return { kbs: {}, activeKb: null, kbPath: null };
    const cfg = readJsonFileSafe(configPath);
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
    cfg.Environment.KBs = kbs;
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

    const foundGxPath = discoverGeneXusInstallation();
    if (!foundGxPath) {
        log('[genexus-mcp] ERROR: No config.json found and GeneXus installation auto-discovery failed.');
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    if (!directoryLooksLikeKnowledgeBase(cwd)) {
        log('[genexus-mcp] ERROR: Zero-config failed because current directory is not a GeneXus KB.');
        log(`[genexus-mcp] CWD: ${cwd}`);
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    log(`[genexus-mcp] Auto-discovered GeneXus at: ${foundGxPath}`);
    log(`[genexus-mcp] Generating default config.json for KB at: ${cwd}`);

    const defaultConfig = generateConfig(foundGxPath, cwd);
    writeFileAtomic(cwdConfigPath, JSON.stringify(defaultConfig, null, 2));
    process.env.GX_CONFIG_PATH = cwdConfigPath;

    return { ok: true };
}

module.exports = {
    generateConfig,
    getGatewayExePath,
    getToolDefinitionsPath,
    discoverGeneXusInstallation,
    discoverGeneXusFromRegistry,
    discoverKnowledgeBase,
    discoverKnowledgeBases,
    directoryLooksLikeKnowledgeBase,
    readJsonFileSafe,
    resolveConfigPathNoMutate,
    createConfigFile,
    patchClientConfig,
    unpatchClientConfig,
    getClientConfigTargets,
    detectClientInstalled,
    clientsStatus,
    listSupportedClientIds,
    filterClientTargets,
    getLocalAppDataCacheDir,
    readGeneXusVersionFromInstall,
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
