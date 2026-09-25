'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const { execFileSync } = require('child_process');

function resolveDefaultRuntimeRoot() {
    if (process.env.GENEXUS_MCP_RUNTIME_DIR) {
        return process.env.GENEXUS_MCP_RUNTIME_DIR;
    }
    const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(localAppData, 'GenexusMCP', 'runtime');
}

function isCheckoutMode(packageRoot) {
    try {
        const gitMarker = fs.statSync(path.join(packageRoot, '.git'));
        return gitMarker.isDirectory() || gitMarker.isFile();
    } catch {
        return false;
    }
}

function shouldStage(packageRoot = path.resolve(__dirname, '..', '..')) {
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) {
        return false;
    }
    if (process.env.GENEXUS_MCP_NO_STAGING === '1') {
        return false;
    }
    if (process.env.GENEXUS_MCP_FORCE_STAGING === '1') {
        return true;
    }
    return !isCheckoutMode(packageRoot);
}

function computeFileSha256(filePath) {
    const hash = crypto.createHash('sha256');
    const buffer = fs.readFileSync(filePath);
    hash.update(buffer);
    return hash.digest('hex');
}

function computePublishFingerprint(publishDir) {
    const manifestPath = path.join(publishDir, 'gxmcp-manifest.json');
    if (fs.existsSync(manifestPath)) {
        try {
            return computeFileSha256(manifestPath).slice(0, 8);
        } catch {
            // fallback
        }
    }
    const gatewayExe = path.join(publishDir, 'GxMcp.Gateway.exe');
    if (fs.existsSync(gatewayExe)) {
        try {
            return computeFileSha256(gatewayExe).slice(0, 8);
        } catch {
            // fallback
        }
    }
    return '00000000';
}

function verifyManifest(stagedDir, manifestPath) {
    if (!fs.existsSync(manifestPath)) {
        return;
    }
    let manifest;
    try {
        manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    } catch (err) {
        throw new Error(`Failed to parse manifest at ${manifestPath}: ${err.message}`);
    }

    const artifacts = Array.isArray(manifest.artifacts) ? manifest.artifacts : [];
    for (const artifact of artifacts) {
        if (!artifact || !artifact.path) continue;
        const fullPath = path.join(stagedDir, artifact.path);
        if (!fs.existsSync(fullPath)) {
            throw new Error(`Manifest verification failed: missing artifact '${artifact.path}'`);
        }
        if (typeof artifact.size === 'number') {
            const stat = fs.statSync(fullPath);
            if (stat.size !== artifact.size) {
                throw new Error(`Manifest verification failed: size mismatch for '${artifact.path}' (expected ${artifact.size}, got ${stat.size})`);
            }
        }
        if (artifact.sha256) {
            const actualSha = computeFileSha256(fullPath);
            if (actualSha.toLowerCase() !== artifact.sha256.toLowerCase()) {
                throw new Error(`Manifest verification failed: sha256 mismatch for '${artifact.path}'`);
            }
        }
    }
}

function isPathWithin(candidatePath, rootPath) {
    if (!candidatePath || !rootPath) return false;
    const relative = path.relative(path.resolve(rootPath), path.resolve(candidatePath));
    return relative === '' || (
        relative !== '..'
        && !relative.startsWith(`..${path.sep}`)
        && !path.isAbsolute(relative)
    );
}

function getRuntimeProcessUsage(runtimeRoot, processes, runtimeDirectories = null) {
    const runtimes = Array.isArray(runtimeDirectories)
        ? runtimeDirectories
        : listStagedRuntimes(runtimeRoot);
    const usage = new Map();

    for (const runtime of runtimes) {
        const pids = (Array.isArray(processes) ? processes : [])
            .filter((proc) => proc && isPathWithin(proc.exePath, runtime.path))
            .map((proc) => Number(proc.pid))
            .filter((pid) => Number.isInteger(pid) && pid > 0)
            .sort((a, b) => a - b);
        if (pids.length > 0) {
            usage.set(path.resolve(runtime.path).toLowerCase(), {
                name: runtime.name,
                path: runtime.path,
                pids
            });
        }
    }

    return Array.from(usage.values()).sort((a, b) => a.name.localeCompare(b.name));
}

function createRuntimeCleanupResult() {
    return {
        deleted: [],
        skipped: [],
        failed: [],
        processProbeAvailable: true
    };
}

function readRuntimeProcessSnapshot(processProvider) {
    let snapshot = processProvider();
    if (snapshot && !Array.isArray(snapshot) && Array.isArray(snapshot.processes)) {
        if (snapshot.ok === false) throw new Error(snapshot.error || 'runtime process probe failed');
        snapshot = snapshot.processes;
    }
    if (!Array.isArray(snapshot)) throw new Error('runtime process probe returned no process list');
    if (snapshot.some((proc) => !proc || !proc.exePath)) {
        throw new Error('runtime process probe returned a process without an executable path');
    }
    return snapshot;
}

function processPidsUnder(directory, processes) {
    return (Array.isArray(processes) ? processes : [])
        .filter((proc) => proc && isPathWithin(proc.exePath, directory))
        .map((proc) => Number(proc.pid))
        .filter((pid) => Number.isInteger(pid) && pid > 0)
        .sort((a, b) => a - b);
}

function processPidsUnderAny(directories, processes) {
    const paths = Array.isArray(directories) ? directories.filter(Boolean) : [directories].filter(Boolean);
    return [...new Set(paths.flatMap((directory) => processPidsUnder(directory, processes)))]
        .sort((a, b) => a - b);
}

function originalPathForTombstone(tombstonePath) {
    if (typeof tombstonePath !== 'string') return null;
    const marker = tombstonePath.lastIndexOf('.deleting-');
    return marker > 0 ? tombstonePath.slice(0, marker) : null;
}

function cleanupRuntimeDirectory(directory, options = {}) {
    const result = createRuntimeCleanupResult();
    if (!directory || !fs.existsSync(directory)) return result;

    const processProvider = typeof options.getRunningProcesses === 'function'
        ? options.getRunningProcesses
        : probeRunningGxMcpProcesses;
    const rmSync = typeof options.rmSync === 'function' ? options.rmSync : fs.rmSync;
    const removeOptions = { recursive: true, force: true, maxRetries: 3, retryDelay: 100 };
    const probePaths = [
        directory,
        ...(Array.isArray(options.additionalProbePaths) ? options.additionalProbePaths : [])
    ].filter(Boolean);

    let initialSnapshot;
    try {
        initialSnapshot = readRuntimeProcessSnapshot(processProvider);
    } catch (err) {
        result.processProbeAvailable = false;
        result.skipped.push({
            path: directory,
            reason: 'process-probe-unavailable',
            pids: [],
            error: err && err.message ? err.message : String(err)
        });
        return result;
    }

    const initialPids = processPidsUnderAny(probePaths, initialSnapshot);
    if (initialPids.length > 0) {
        result.skipped.push({ path: directory, reason: 'in-use', pids: initialPids });
        return result;
    }

    let finalSnapshot;
    try {
        finalSnapshot = readRuntimeProcessSnapshot(processProvider);
    } catch (err) {
        result.processProbeAvailable = false;
        result.skipped.push({
            path: directory,
            reason: 'process-probe-unavailable-before-delete',
            pids: [],
            error: err && err.message ? err.message : String(err)
        });
        return result;
    }

    const finalPids = processPidsUnderAny(probePaths, finalSnapshot);
    if (finalPids.length > 0) {
        result.skipped.push({ path: directory, reason: 'in-use-before-delete', pids: finalPids });
        return result;
    }

    try {
        rmSync(directory, removeOptions);
        result.deleted.push(directory);
    } catch (err) {
        result.failed.push({ path: directory, error: err && err.message ? err.message : String(err) });
    }
    return result;
}

function mergeRuntimeCleanupResults(target, additional) {
    if (!additional) return target;
    target.deleted.push(...additional.deleted);
    target.skipped.push(...additional.skipped);
    target.failed.push(...additional.failed);
    target.processProbeAvailable = target.processProbeAvailable && additional.processProbeAvailable;
    return target;
}

function cleanOldRuntimes(runtimeRoot, currentTargetDir, keepCount = 2, options = {}) {
    const result = createRuntimeCleanupResult();

    const processProvider = typeof options.getRunningProcesses === 'function'
        ? options.getRunningProcesses
        : probeRunningGxMcpProcesses;
    const renameSync = typeof options.renameSync === 'function' ? options.renameSync : fs.renameSync;
    const rmSync = typeof options.rmSync === 'function' ? options.rmSync : fs.rmSync;
    const removeOptions = { recursive: true, force: true, maxRetries: 3, retryDelay: 100 };

    const readProcessSnapshot = () => readRuntimeProcessSnapshot(processProvider);

    const probeImmediatelyBeforeDelete = (directory) => {
        try {
            const snapshot = readProcessSnapshot();
            return { ok: true, pids: processPidsUnder(directory, snapshot) };
        } catch (err) {
            return { ok: false, pids: [], error: err && err.message ? err.message : String(err) };
        }
    };

    try {
        if (!fs.existsSync(runtimeRoot)) return result;
        const entries = fs.readdirSync(runtimeRoot, { withFileTypes: true });
        const now = Date.now();
        const versionDirs = [];
        const tmpDirs = [];
        const tombstoneDirs = [];

        for (const entry of entries) {
            if (!entry.isDirectory()) continue;
            const fullPath = path.join(runtimeRoot, entry.name);
            if (entry.name.includes('.tmp-')) {
                tmpDirs.push(fullPath);
                continue;
            }
            if (entry.name.includes('.deleting-')) {
                tombstoneDirs.push(fullPath);
                continue;
            }
            try {
                const stat = fs.statSync(fullPath);
                versionDirs.push({ name: entry.name, path: fullPath, mtimeMs: stat.mtimeMs });
            } catch {
                // Ignore an entry that disappeared during enumeration.
            }
        }

        versionDirs.sort((a, b) => b.mtimeMs - a.mtimeMs);
        const normalizedCurrent = path.resolve(currentTargetDir).toLowerCase();
        const keep = Number.isFinite(Number(keepCount)) ? Math.max(0, Number(keepCount)) : 2;
        let kept = 0;
        const toDelete = [];
        for (const vdir of versionDirs) {
            if (path.resolve(vdir.path).toLowerCase() === normalizedCurrent) continue;
            if (kept < keep) kept++;
            else toDelete.push(vdir.path);
        }

        const staleTmpDirs = tmpDirs.filter((dir) => {
            try { return now - fs.statSync(dir).mtimeMs > 10 * 60 * 1000; } catch { return false; }
        });
        if (toDelete.length === 0 && staleTmpDirs.length === 0 && tombstoneDirs.length === 0) {
            return result;
        }

        // Finish tombstones and stale staging directories with the same
        // fail-closed process protection used by immediate staging cleanup.
        for (const tombstone of tombstoneDirs) {
            const originalPath = originalPathForTombstone(tombstone);
            mergeRuntimeCleanupResults(result, cleanupRuntimeDirectory(tombstone, {
                ...options,
                additionalProbePaths: originalPath ? [originalPath] : []
            }));
        }
        for (const tmpDir of staleTmpDirs) {
            mergeRuntimeCleanupResults(result, cleanupRuntimeDirectory(tmpDir, options));
        }

        if (toDelete.length === 0) return result;
        let processSnapshot;
        try {
            processSnapshot = readProcessSnapshot();
        } catch (err) {
            result.processProbeAvailable = false;
            const error = err && err.message ? err.message : String(err);
            for (const dir of toDelete) {
                result.skipped.push({ path: dir, reason: 'process-probe-unavailable', pids: [], error });
            }
            return result;
        }

        const initialUsage = new Map(
            getRuntimeProcessUsage(runtimeRoot, processSnapshot, versionDirs)
                .map((entry) => [path.resolve(entry.path).toLowerCase(), entry])
        );

        for (const dirToDelete of toDelete) {
            let freshSnapshot;
            try {
                freshSnapshot = readProcessSnapshot();
            } catch (err) {
                result.skipped.push({
                    path: dirToDelete,
                    reason: 'process-probe-unavailable',
                    pids: [],
                    error: err && err.message ? err.message : String(err)
                });
                continue;
            }

            const freshUsage = getRuntimeProcessUsage(runtimeRoot, freshSnapshot, versionDirs)
                .find((entry) => path.resolve(entry.path).toLowerCase() === path.resolve(dirToDelete).toLowerCase());
            if (freshUsage || initialUsage.has(path.resolve(dirToDelete).toLowerCase())) {
                const usage = freshUsage || initialUsage.get(path.resolve(dirToDelete).toLowerCase());
                result.skipped.push({ path: dirToDelete, reason: 'in-use', pids: usage.pids });
                continue;
            }

            // Rename first. The post-rename probe below closes the normal
            // TOCTOU window; a process that appears while retiring is restored
            // to its original name before any recursive removal is attempted.
            const deletingPath = `${dirToDelete}.deleting-${process.pid}-${Date.now()}-${crypto.randomBytes(4).toString('hex')}`;
            try {
                renameSync(dirToDelete, deletingPath);
            } catch (err) {
                result.failed.push({ path: dirToDelete, error: err && err.message ? err.message : String(err) });
                continue;
            }

            let postSnapshot;
            try {
                postSnapshot = readProcessSnapshot();
            } catch (err) {
                try { renameSync(deletingPath, dirToDelete); } catch { }
                result.skipped.push({
                    path: dirToDelete,
                    reason: 'process-probe-unavailable-after-rename',
                    pids: [],
                    error: err && err.message ? err.message : String(err)
                });
                continue;
            }
            const postPids = [
                ...processPidsUnder(dirToDelete, postSnapshot),
                ...processPidsUnder(deletingPath, postSnapshot)
            ].filter((pid, index, all) => all.indexOf(pid) === index).sort((a, b) => a - b);
            if (postPids.length > 0) {
                let restored = true;
                try { renameSync(deletingPath, dirToDelete); } catch { restored = false; }
                result.skipped.push({
                    path: dirToDelete,
                    reason: 'in-use-after-rename',
                    pids: postPids,
                    restored
                });
                continue;
            }
            const finalDeletingProbe = probeImmediatelyBeforeDelete(deletingPath);
            if (!finalDeletingProbe.ok) {
                let restored = true;
                try { renameSync(deletingPath, dirToDelete); } catch { restored = false; }
                result.skipped.push({
                    path: dirToDelete,
                    reason: 'process-probe-unavailable-before-delete',
                    pids: [],
                    error: finalDeletingProbe.error,
                    restored
                });
                continue;
            }
            if (finalDeletingProbe.pids.length > 0) {
                let restored = true;
                try { renameSync(deletingPath, dirToDelete); } catch { restored = false; }
                result.skipped.push({
                    path: dirToDelete,
                    reason: 'in-use-before-delete',
                    pids: finalDeletingProbe.pids,
                    restored
                });
                continue;
            }

            try {
                rmSync(deletingPath, removeOptions);
                result.deleted.push(dirToDelete);
            } catch (err) {
                result.failed.push({
                    path: dirToDelete,
                    deletingPath,
                    error: err && err.message ? err.message : String(err)
                });
            }
        }
    } catch (err) {
        result.failed.push({ path: runtimeRoot, error: err && err.message ? err.message : String(err) });
    }

    return result;
}

function ensureStagedGateway(options = {}) {
    const packageRoot = options.packageRoot || path.resolve(__dirname, '..', '..');
    const publishDir = options.publishDir || path.join(packageRoot, 'publish');
    const defaultGatewayExe = options.defaultGatewayExe || path.join(publishDir, 'GxMcp.Gateway.exe');

    if (!options.forceStaging && !shouldStage(packageRoot)) {
        return {
            staged: false,
            gatewayExePath: process.env.GENEXUS_MCP_GATEWAY_EXE || defaultGatewayExe,
            runtimeDir: path.dirname(process.env.GENEXUS_MCP_GATEWAY_EXE || defaultGatewayExe),
            reason: process.env.GENEXUS_MCP_GATEWAY_EXE ? 'GENEXUS_MCP_GATEWAY_EXE' : (process.env.GENEXUS_MCP_NO_STAGING === '1' ? 'NO_STAGING' : 'checkout'),
            cleanup: null
        };
    }

    let version = '0.0.0';
    try {
        const pkg = JSON.parse(fs.readFileSync(path.join(packageRoot, 'package.json'), 'utf8'));
        if (pkg.version) version = pkg.version;
    } catch {
        // Fallback
    }

    const sha8 = computePublishFingerprint(publishDir);
    const runtimeRoot = options.runtimeRoot || resolveDefaultRuntimeRoot();
    const targetDir = path.join(runtimeRoot, `${version}-${sha8}`);
    const stagedGatewayExe = path.join(targetDir, 'GxMcp.Gateway.exe');

    if (fs.existsSync(stagedGatewayExe)) {
        const cleanup = cleanOldRuntimes(runtimeRoot, targetDir, 2, options);
        return {
            staged: true,
            gatewayExePath: stagedGatewayExe,
            runtimeDir: targetDir,
            fresh: false,
            cleanup
        };
    }

    if (!fs.existsSync(publishDir)) {
        throw new Error(`Cannot stage runtime: publish directory not found at ${publishDir}`);
    }

    fs.mkdirSync(runtimeRoot, { recursive: true });
    const tmpDir = `${targetDir}.tmp-${process.pid}-${Date.now()}`;
    fs.mkdirSync(tmpDir, { recursive: true });
    let temporaryCleanup = null;

    try {
        fs.cpSync(publishDir, tmpDir, { recursive: true });
        const manifestPath = path.join(tmpDir, 'gxmcp-manifest.json');
        verifyManifest(tmpDir, manifestPath);

        try {
            fs.renameSync(tmpDir, targetDir);
        } catch (renameErr) {
            // Concurrent launcher might have completed the rename first
            if (fs.existsSync(stagedGatewayExe)) {
                temporaryCleanup = cleanupRuntimeDirectory(tmpDir, options);
            } else {
                throw renameErr;
            }
        }
    } catch (err) {
        const failure = err instanceof Error ? err : new Error(String(err));
        const cleanup = cleanupRuntimeDirectory(tmpDir, options);
        failure.runtimeCleanup = cleanup;
        const notes = [];
        if (cleanup.skipped.length > 0) {
            const reasons = cleanup.skipped
                .map((entry) => `${entry.reason}${entry.pids?.length ? ` (PIDs: ${entry.pids.join(', ')})` : ''}`)
                .join(', ');
            notes.push(`skipped ${cleanup.skipped.length} path(s): ${reasons}`);
        }
        if (cleanup.failed.length > 0) {
            notes.push(`failed ${cleanup.failed.length} path(s)`);
        }
        if (!cleanup.processProbeAvailable) {
            notes.push('process probe unavailable');
        }
        if (notes.length > 0) {
            failure.message = `${failure.message} Runtime cleanup ${notes.join('; ')}.`;
        }
        throw failure;
    }

    const cleanup = mergeRuntimeCleanupResults(
        cleanOldRuntimes(runtimeRoot, targetDir, 2, options),
        temporaryCleanup
    );

    return {
        staged: true,
        gatewayExePath: stagedGatewayExe,
        runtimeDir: targetDir,
        fresh: true,
        cleanup
    };
}

function listStagedRuntimes(runtimeRoot = resolveDefaultRuntimeRoot()) {
    if (!fs.existsSync(runtimeRoot)) return [];
    try {
        const entries = fs.readdirSync(runtimeRoot, { withFileTypes: true });
        return entries
            .filter((e) => e.isDirectory() && !e.name.includes('.tmp-') && !e.name.includes('.deleting-'))
            .map((e) => {
                const dirPath = path.join(runtimeRoot, e.name);
                let stat = null;
                try {
                    stat = fs.statSync(dirPath);
                } catch {
                    // Ignore
                }
                return {
                    name: e.name,
                    path: dirPath,
                    hasGateway: fs.existsSync(path.join(dirPath, 'GxMcp.Gateway.exe')),
                    mtime: stat ? stat.mtime : null
                };
            });
    } catch {
        return [];
    }
}

function probeRunningGxMcpProcesses() {
    if (process.platform !== 'win32') {
        return { ok: true, processes: [] };
    }
    try {
        // Query CIM Win32_Process for GxMcp processes. Keep the probe result
        // distinguishable from a successful empty result: GC must fail closed
        // when Windows denies the query.
        const output = execFileSync('powershell.exe', [
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            `Get-CimInstance Win32_Process -Filter "name like 'GxMcp%'" | Select-Object ProcessId, ExecutablePath, CommandLine | ConvertTo-Json -Compress`
        ], { encoding: 'utf8', timeout: 3000, stdio: ['ignore', 'pipe', 'ignore'] }).trim();

        if (!output) return { ok: true, processes: [] };
        let data;
        try {
            data = JSON.parse(output);
        } catch (err) {
            return { ok: false, processes: [], error: `invalid process probe JSON: ${err.message}` };
        }
        const rows = Array.isArray(data) ? data : [data];
        const processes = rows.filter(Boolean).map((r) => ({
            pid: r.ProcessId,
            exePath: r.ExecutablePath || '',
            commandLine: r.CommandLine || ''
        }));
        const unresolved = processes.filter((proc) => !proc.exePath);
        if (unresolved.length > 0) {
            return {
                ok: false,
                processes,
                error: `${unresolved.length} GxMcp process record(s) had no executable path; runtime GC is fail-closed.`
            };
        }
        return { ok: true, processes };
    } catch (err) {
        return { ok: false, processes: [], error: err && err.message ? err.message : String(err) };
    }
}

function getRunningGxMcpProcesses() {
    return probeRunningGxMcpProcesses().processes;
}

function classifyProcessRuntime(exePath, runtimeRoot = resolveDefaultRuntimeRoot()) {
    if (!exePath) return 'unknown';
    if (isPathWithin(exePath, runtimeRoot)) {
        return 'staged';
    }
    const norm = path.resolve(exePath).toLowerCase();
    if (norm.includes('npm-cache') || norm.includes('_npx') || norm.includes('node_modules')) {
        return 'npx-cache';
    }
    return 'other';
}

module.exports = {
    resolveDefaultRuntimeRoot,
    isCheckoutMode,
    shouldStage,
    computePublishFingerprint,
    verifyManifest,
    cleanOldRuntimes,
    ensureStagedGateway,
    listStagedRuntimes,
    getRunningGxMcpProcesses,
    probeRunningGxMcpProcesses,
    getRuntimeProcessUsage,
    classifyProcessRuntime
};
