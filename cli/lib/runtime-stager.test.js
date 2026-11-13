'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');

const {
    resolveDefaultRuntimeRoot,
    shouldStage,
    ensureStagedGateway,
    cleanOldRuntimes,
    verifyManifest,
    classifyProcessRuntime,
    getRuntimeProcessUsage,
    isRuntimeCleanupDue,
    runRuntimeCleanup,
    startRuntimeCleanupInBackground
} = require('./runtime-stager');
const { EventEmitter } = require('events');

function sha256(content) {
    return crypto.createHash('sha256').update(content).digest('hex');
}

test('runtime-stager: resolveDefaultRuntimeRoot uses GENEXUS_MCP_RUNTIME_DIR or LocalAppData', (t) => {
    const orig = process.env.GENEXUS_MCP_RUNTIME_DIR;
    t.after(() => {
        if (orig !== undefined) process.env.GENEXUS_MCP_RUNTIME_DIR = orig;
        else delete process.env.GENEXUS_MCP_RUNTIME_DIR;
    });
    process.env.GENEXUS_MCP_RUNTIME_DIR = 'C:\\custom\\runtime';
    assert.equal(resolveDefaultRuntimeRoot(), 'C:\\custom\\runtime');
});

test('runtime-stager: verifyManifest succeeds on valid files', () => {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-verify-'));
    try {
        const file = path.join(tmp, 'test.txt');
        fs.writeFileSync(file, 'hello');
        const manifest = path.join(tmp, 'manifest.json');
        fs.writeFileSync(manifest, JSON.stringify({
            artifacts: [{ path: 'test.txt', size: 5, sha256: sha256('hello') }]
        }));
        assert.doesNotThrow(() => verifyManifest(tmp, manifest));
    } finally {
        try { fs.rmSync(tmp, { recursive: true, force: true }); } catch { /* ignore */ }
    }
});

test('runtime-stager: shouldStage respects env overrides and checkout markers', (t) => {
    const origEnv = { ...process.env };
    t.after(() => {
        process.env = origEnv;
    });

    const tmpPkg = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-pkg-'));
    t.after(() => {
        try { fs.rmSync(tmpPkg, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    // Case 1: GENEXUS_MCP_GATEWAY_EXE set -> false
    process.env.GENEXUS_MCP_GATEWAY_EXE = 'C:\\custom\\GxMcp.Gateway.exe';
    delete process.env.GENEXUS_MCP_NO_STAGING;
    delete process.env.GENEXUS_MCP_FORCE_STAGING;
    assert.equal(shouldStage(tmpPkg), false);

    // Case 2: GENEXUS_MCP_NO_STAGING === '1' -> false
    delete process.env.GENEXUS_MCP_GATEWAY_EXE;
    process.env.GENEXUS_MCP_NO_STAGING = '1';
    assert.equal(shouldStage(tmpPkg), false);

    // Case 3: In packaged mode (no .git) -> true
    delete process.env.GENEXUS_MCP_NO_STAGING;
    assert.equal(shouldStage(tmpPkg), true);

    // Case 4: With .git marker -> false
    fs.mkdirSync(path.join(tmpPkg, '.git'));
    assert.equal(shouldStage(tmpPkg), false);

    // Case 5: With .git marker but GENEXUS_MCP_FORCE_STAGING === '1' -> true
    process.env.GENEXUS_MCP_FORCE_STAGING = '1';
    assert.equal(shouldStage(tmpPkg), true);
});

test('runtime-stager: ensureStagedGateway stages publish folder, verifies manifest and reuses staged runtime', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-stage-test-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const fakePkg = path.join(tmpRoot, 'package');
    const fakePublish = path.join(fakePkg, 'publish');
    fs.mkdirSync(fakePublish, { recursive: true });

    fs.writeFileSync(path.join(fakePkg, 'package.json'), JSON.stringify({ version: '3.9.0' }));
    const fakeExeContent = 'fake gateway binary';
    fs.writeFileSync(path.join(fakePublish, 'GxMcp.Gateway.exe'), fakeExeContent);

    const manifestContent = JSON.stringify({
        schemaVersion: 'gxmcp-release-manifest/1',
        artifacts: [
            {
                path: 'GxMcp.Gateway.exe',
                size: Buffer.byteLength(fakeExeContent),
                sha256: sha256(fakeExeContent)
            }
        ]
    });
    fs.writeFileSync(path.join(fakePublish, 'gxmcp-manifest.json'), manifestContent);

    const runtimeRoot = path.join(tmpRoot, 'runtime');

    // First run: stages fresh
    const result1 = ensureStagedGateway({
        packageRoot: fakePkg,
        publishDir: fakePublish,
        runtimeRoot,
        forceStaging: true
    });

    assert.equal(result1.staged, true);
    assert.equal(result1.fresh, true);
    assert.equal(fs.existsSync(result1.gatewayExePath), true);
    assert.equal(fs.readFileSync(result1.gatewayExePath, 'utf8'), fakeExeContent);

    // Second run: reuses already staged
    const result2 = ensureStagedGateway({
        packageRoot: fakePkg,
        publishDir: fakePublish,
        runtimeRoot,
        forceStaging: true
    });

    assert.equal(result2.staged, true);
    assert.equal(result2.fresh, false);
    assert.equal(result2.gatewayExePath, result1.gatewayExePath);
});

test('runtime-stager: ensureStagedGateway fails closed and cleans tmp on manifest corruption', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-corrupt-test-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const fakePkg = path.join(tmpRoot, 'package');
    const fakePublish = path.join(fakePkg, 'publish');
    fs.mkdirSync(fakePublish, { recursive: true });

    fs.writeFileSync(path.join(fakePkg, 'package.json'), JSON.stringify({ version: '3.9.0' }));
    const fakeExeContent = 'tampered gateway binary';
    fs.writeFileSync(path.join(fakePublish, 'GxMcp.Gateway.exe'), fakeExeContent);

    const manifestContent = JSON.stringify({
        schemaVersion: 'gxmcp-release-manifest/1',
        artifacts: [
            {
                path: 'GxMcp.Gateway.exe',
                size: 9999, // mismatched size
                sha256: 'deadbeef'
            }
        ]
    });
    fs.writeFileSync(path.join(fakePublish, 'gxmcp-manifest.json'), manifestContent);

    const runtimeRoot = path.join(tmpRoot, 'runtime');

    assert.throws(() => {
        ensureStagedGateway({
            packageRoot: fakePkg,
            publishDir: fakePublish,
            runtimeRoot,
            forceStaging: true,
            getRunningProcesses: () => []
        });
    }, /Manifest verification failed/);

    // Assert that target dir was not created and no tmp dir left behind
    assert.equal(fs.readdirSync(runtimeRoot).length, 0);
});

test('runtime-stager: temporary staging cleanup retains a live process and reports the final-probe skip', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-stage-live-process-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const fakePkg = path.join(tmpRoot, 'package');
    const fakePublish = path.join(fakePkg, 'publish');
    const runtimeRoot = path.join(tmpRoot, 'runtime');
    fs.mkdirSync(fakePublish, { recursive: true });
    fs.writeFileSync(path.join(fakePkg, 'package.json'), JSON.stringify({ version: '3.9.1' }));
    fs.writeFileSync(path.join(fakePublish, 'GxMcp.Gateway.exe'), 'partial gateway');
    fs.writeFileSync(path.join(fakePublish, 'gxmcp-manifest.json'), JSON.stringify({
        artifacts: [{ path: 'GxMcp.Gateway.exe', size: 9999, sha256: 'deadbeef' }]
    }));

    let probeCalls = 0;
    let stagingError;
    try {
        ensureStagedGateway({
            packageRoot: fakePkg,
            publishDir: fakePublish,
            runtimeRoot,
            forceStaging: true,
            getRunningProcesses: () => {
                probeCalls++;
                if (probeCalls === 1) return [];
                const tmpName = fs.readdirSync(runtimeRoot).find((name) => name.includes('.tmp-'));
                return [{
                    pid: 5150,
                    exePath: path.join(runtimeRoot, tmpName, 'worker', 'GxMcp.Worker.exe')
                }];
            }
        });
    } catch (err) {
        stagingError = err;
    }

    assert.ok(stagingError);
    assert.match(stagingError.message, /Manifest verification failed/);
    assert.match(stagingError.message, /Runtime cleanup skipped/);
    assert.equal(stagingError.runtimeCleanup.processProbeAvailable, true);
    assert.equal(stagingError.runtimeCleanup.skipped.length, 1);
    assert.equal(stagingError.runtimeCleanup.skipped[0].reason, 'in-use-before-delete');
    assert.deepEqual(stagingError.runtimeCleanup.skipped[0].pids, [5150]);
    assert.equal(probeCalls, 2);
    assert.equal(
        fs.readdirSync(runtimeRoot).filter((name) => name.includes('.tmp-')).length,
        1
    );
});

test('runtime-stager: cleanOldRuntimes retains current version plus keepCount newest versions', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-test-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const v1 = path.join(tmpRoot, '3.6.0-aaaa1111');
    const v2 = path.join(tmpRoot, '3.7.0-bbbb2222');
    const v3 = path.join(tmpRoot, '3.8.0-cccc3333');
    const v4 = path.join(tmpRoot, '3.9.0-dddd4444');

    fs.mkdirSync(v1);
    fs.utimesSync(v1, 1000, 1000);
    fs.mkdirSync(v2);
    fs.utimesSync(v2, 2000, 2000);
    fs.mkdirSync(v3);
    fs.utimesSync(v3, 3000, 3000);
    fs.mkdirSync(v4);
    fs.utimesSync(v4, 4000, 4000);

    // Current is v4. Keep count = 2 -> keep v4, v3, v2. v1 should be deleted.
    cleanOldRuntimes(tmpRoot, v4, 2, { getRunningProcesses: () => [] });

    assert.equal(fs.existsSync(v4), true, 'current version must be kept');
    assert.equal(fs.existsSync(v3), true, 'newest previous version must be kept');
    assert.equal(fs.existsSync(v2), true, 'second previous version must be kept');
    assert.equal(fs.existsSync(v1), false, 'oldest version beyond keepCount must be deleted');
});

test('runtime-stager: classifyProcessRuntime identifies staged vs npx-cache', () => {
    const runtimeRoot = 'C:\\Users\\user\\AppData\\Local\\GenexusMCP\\runtime';
    assert.equal(
        classifyProcessRuntime('C:\\Users\\user\\AppData\\Local\\GenexusMCP\\runtime\\3.8.0-abcd\\GxMcp.Gateway.exe', runtimeRoot),
        'staged'
    );
    assert.equal(
        classifyProcessRuntime('C:\\Users\\user\\AppData\\Local\\npm-cache\\_npx\\12345\\node_modules\\genexus-mcp\\publish\\GxMcp.Gateway.exe', runtimeRoot),
        'npx-cache'
    );
    assert.equal(
        classifyProcessRuntime('C:\\Projetos\\Genexus18MCP\\publish\\GxMcp.Gateway.exe', runtimeRoot),
        'other'
    );
});

test('runtime-stager: cleanOldRuntimes retains a runtime with a live child process and reports its PID', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-in-use-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const oldRuntime = path.join(tmpRoot, '3.6.0-old');
    const currentRuntime = path.join(tmpRoot, '3.9.0-current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);
    fs.utimesSync(oldRuntime, 1000, 1000);
    fs.utimesSync(currentRuntime, 2000, 2000);

    const result = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => [{
            pid: 4242,
            exePath: path.join(oldRuntime, 'worker', 'GxMcp.Worker.exe')
        }]
    });

    assert.equal(fs.existsSync(oldRuntime), true);
    assert.equal(result.skipped.length, 1);
    assert.equal(result.skipped[0].reason, 'in-use');
    assert.deepEqual(result.skipped[0].pids, [4242]);
});

test('runtime-stager: cleanOldRuntimes renames before removal and leaves the original on rename failure', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-atomic-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    const oldRuntime = path.join(tmpRoot, '3.6.0-atomic');
    const currentRuntime = path.join(tmpRoot, '3.9.0-current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);
    fs.writeFileSync(path.join(oldRuntime, 'keep.txt'), 'payload');
    fs.utimesSync(oldRuntime, 1000, 1000);
    fs.utimesSync(currentRuntime, 2000, 2000);

    const renameCalls = [];
    const result = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => [],
        renameSync: (from, to) => {
            renameCalls.push([from, to]);
            fs.renameSync(from, to);
        }
    });

    assert.equal(renameCalls.length, 1);
    assert.equal(fs.existsSync(oldRuntime), false);
    assert.equal(result.deleted.length, 1);

    const failedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-rename-fail-'));
    t.after(() => {
        try { fs.rmSync(failedRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const failedOld = path.join(failedRoot, '3.6.0-failed');
    const failedCurrent = path.join(failedRoot, '3.9.0-current');
    fs.mkdirSync(failedOld);
    fs.mkdirSync(failedCurrent);
    fs.writeFileSync(path.join(failedOld, 'keep.txt'), 'payload');

    const failed = cleanOldRuntimes(failedRoot, failedCurrent, 0, {
        getRunningProcesses: () => [],
        renameSync: () => { throw new Error('directory is in use'); }
    });
    assert.equal(fs.existsSync(failedOld), true);
    assert.equal(failed.failed.length, 1);
    assert.match(failed.failed[0].error, /directory is in use/);
});

test('runtime-stager: process-probe failure is fail-closed and reported', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-probe-fail-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const oldRuntime = path.join(tmpRoot, 'old');
    const currentRuntime = path.join(tmpRoot, 'current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);

    const result = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => ({ ok: false, processes: [], error: 'access denied' })
    });
    assert.equal(result.processProbeAvailable, false);
    assert.equal(fs.existsSync(oldRuntime), true);
    assert.equal(result.skipped[0].reason, 'process-probe-unavailable');
});

test('runtime-stager: a process appearing during retirement restores the original directory name', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-race-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const oldRuntime = path.join(tmpRoot, 'old');
    const currentRuntime = path.join(tmpRoot, 'current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);
    fs.writeFileSync(path.join(oldRuntime, 'payload.txt'), 'keep');

    let calls = 0;
    const result = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => {
            calls++;
            return calls < 3
                ? []
                : [{ pid: 777, exePath: path.join(oldRuntime, 'GxMcp.Gateway.exe') }];
        }
    });

    assert.equal(fs.existsSync(oldRuntime), true);
    assert.equal(fs.existsSync(path.join(oldRuntime, 'payload.txt')), true);
    assert.equal(result.skipped[0].reason, 'in-use-after-rename');
    assert.equal(result.skipped[0].restored, true);
});

test('runtime-stager: a process appearing at the final delete probe restores the renamed runtime', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-final-race-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const oldRuntime = path.join(tmpRoot, 'old');
    const currentRuntime = path.join(tmpRoot, 'current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);
    fs.writeFileSync(path.join(oldRuntime, 'payload.txt'), 'keep');

    let calls = 0;
    let deletingPath = null;
    const result = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => {
            calls++;
            if (calls < 4 || !deletingPath) return [];
            return [{ pid: 778, exePath: path.join(deletingPath, 'GxMcp.Gateway.exe') }];
        },
        renameSync: (from, to) => {
            deletingPath = to;
            fs.renameSync(from, to);
        }
    });

    assert.equal(fs.existsSync(oldRuntime), true);
    assert.equal(fs.existsSync(path.join(oldRuntime, 'payload.txt')), true);
    assert.equal(result.skipped[0].reason, 'in-use-before-delete');
    assert.equal(result.skipped[0].restored, true);
});

test('runtime-stager: a failed removal leaves a retryable tombstone and the next pass collects it', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-tombstone-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const oldRuntime = path.join(tmpRoot, 'old');
    const currentRuntime = path.join(tmpRoot, 'current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);
    fs.writeFileSync(path.join(oldRuntime, 'payload.txt'), 'keep');

    const failed = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => [],
        rmSync: () => { throw new Error('temporary delete failure'); }
    });
    assert.equal(failed.failed.length, 1);
    assert.equal(fs.existsSync(oldRuntime), false);
    assert.equal(fs.readdirSync(tmpRoot).some((name) => name.includes('.deleting-')), true);

    const retried = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => []
    });
    assert.equal(retried.failed.length, 0);
    assert.equal(fs.readdirSync(tmpRoot).some((name) => name.includes('.deleting-')), false);
});
test('runtime-stager: a failed tombstone restore is retained when the live process still reports the original path', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-tombstone-restore-'));
    t.after(() => {
        try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const oldRuntime = path.join(tmpRoot, 'old');
    const currentRuntime = path.join(tmpRoot, 'current');
    fs.mkdirSync(oldRuntime);
    fs.mkdirSync(currentRuntime);
    fs.writeFileSync(path.join(oldRuntime, 'payload.txt'), 'keep');

    let deletingPath = null;
    const first = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => deletingPath
            ? [{ pid: 779, exePath: path.join(oldRuntime, 'GxMcp.Gateway.exe') }]
            : [],
        renameSync: (from, to) => {
            if (from.includes('.deleting-') && to === oldRuntime) {
                throw new Error('restore failed while process is live');
            }
            deletingPath = to;
            fs.renameSync(from, to);
        }
    });
    assert.equal(first.skipped[0].restored, false);
    assert.equal(fs.existsSync(oldRuntime), false);
    assert.equal(fs.readdirSync(tmpRoot).some((name) => name.includes('.deleting-')), true);

    const second = cleanOldRuntimes(tmpRoot, currentRuntime, 0, {
        getRunningProcesses: () => [{ pid: 779, exePath: path.join(oldRuntime, 'GxMcp.Gateway.exe') }]
    });
    assert.equal(second.skipped.some((entry) => entry.reason === 'in-use'), true);
    assert.equal(fs.readdirSync(tmpRoot).some((name) => name.includes('.deleting-')), true);
});

test('runtime-stager: getRuntimeProcessUsage maps Gateway, broker, and Worker PIDs to their runtime', (t) => {
    const runtimeRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-usage-'));
    t.after(() => {
        try { fs.rmSync(runtimeRoot, { recursive: true, force: true }); } catch { /* ignore */ }
    });
    const runtimeDir = path.join(runtimeRoot, '3.8.0-old');
    const siblingDir = path.join(runtimeRoot, '3.8.0-old-suffix');
    fs.mkdirSync(runtimeDir);
    fs.mkdirSync(siblingDir);
    const gateway = path.join(runtimeDir, 'GxMcp.Gateway.exe');
    const worker = path.join(runtimeDir, 'worker', 'GxMcp.Worker.exe');
    const usage = getRuntimeProcessUsage(runtimeRoot, [
        { pid: 10, exePath: gateway },
        { pid: 11, exePath: worker },
        { pid: 12, exePath: path.join(siblingDir, 'GxMcp.Gateway.exe') }
    ]);

    assert.equal(usage.length, 2);
    const oldUsage = usage.find((entry) => entry.name === '3.8.0-old');
    const siblingUsage = usage.find((entry) => entry.name === '3.8.0-old-suffix');
    assert.deepEqual(oldUsage.pids, [10, 11]);
    assert.deepEqual(siblingUsage.pids, [12]);
});

function makeStagedPackage(tmpRoot, version = '3.9.0') {
    const pkg = path.join(tmpRoot, 'package');
    const publish = path.join(pkg, 'publish');
    fs.mkdirSync(publish, { recursive: true });
    fs.writeFileSync(path.join(pkg, 'package.json'), JSON.stringify({ version }));
    fs.writeFileSync(path.join(publish, 'GxMcp.Gateway.exe'), 'fake gateway binary');
    return { packageRoot: pkg, publishDir: publish, runtimeRoot: path.join(tmpRoot, 'runtime'), forceStaging: true };
}

function makeOldRuntimes(runtimeRoot, names) {
    names.forEach((name, index) => {
        const dir = path.join(runtimeRoot, name);
        fs.mkdirSync(dir, { recursive: true });
        fs.utimesSync(dir, 1000 + index, 1000 + index);
    });
}

test('runtime-stager: deferCleanup starts a warm runtime without probing processes or touching old runtimes', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-defer-test-'));
    t.after(() => { try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ } });
    const options = makeStagedPackage(tmpRoot);
    const first = ensureStagedGateway({ ...options, getRunningProcesses: () => [] });
    makeOldRuntimes(options.runtimeRoot, ['3.5.0-aaaa1111', '3.6.0-bbbb2222', '3.7.0-cccc3333', '3.8.0-dddd4444']);

    let probes = 0;
    const warm = ensureStagedGateway({
        ...options,
        deferCleanup: true,
        getRunningProcesses: () => { probes += 1; return []; }
    });

    assert.equal(warm.fresh, false);
    assert.equal(warm.gatewayExePath, first.gatewayExePath);
    assert.equal(warm.cleanupDeferred, true);
    assert.equal(warm.cleanup, null);
    assert.equal(probes, 0, 'the process probe must not run before the Gateway is spawned');
    assert.equal(fs.existsSync(path.join(options.runtimeRoot, '3.5.0-aaaa1111')), true, 'collection is deferred, not performed');
});

test('runtime-stager: onStaging fires once for the copy and never for a warm start', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-onstaging-test-'));
    t.after(() => { try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ } });
    const options = makeStagedPackage(tmpRoot);
    const staged = [];
    const onStaging = (dir) => staged.push(dir);

    const cold = ensureStagedGateway({ ...options, deferCleanup: true, onStaging });
    const warm = ensureStagedGateway({ ...options, deferCleanup: true, onStaging });

    assert.equal(cold.fresh, true);
    assert.equal(warm.fresh, false);
    assert.deepEqual(staged, [cold.runtimeDir]);
});

test('runtime-stager: isRuntimeCleanupDue honours the interval, the stamp and fresh staging', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-due-test-'));
    t.after(() => { try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ } });
    const hour = 60 * 60 * 1000;
    assert.equal(isRuntimeCleanupDue(tmpRoot), true, 'no stamp yet');
    const stamp = path.join(tmpRoot, '.cleanup-stamp');
    fs.writeFileSync(stamp, 'x');
    const stampedAt = fs.statSync(stamp).mtimeMs;
    assert.equal(isRuntimeCleanupDue(tmpRoot, { now: stampedAt + hour, intervalMs: 6 * hour }), false);
    assert.equal(isRuntimeCleanupDue(tmpRoot, { now: stampedAt + 7 * hour, intervalMs: 6 * hour }), true);
    assert.equal(isRuntimeCleanupDue(tmpRoot, { now: stampedAt + hour, intervalMs: 6 * hour, fresh: true }), true, 'a newly staged version always collects');
});

test('runtime-stager: runRuntimeCleanup probes once per interval and keeps a runtime that is still in use', (t) => {
    const runtimeRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-gc-interval-test-'));
    t.after(() => { try { fs.rmSync(runtimeRoot, { recursive: true, force: true }); } catch { /* ignore */ } });
    const oldest = path.join(runtimeRoot, '3.5.0-aaaa1111');
    makeOldRuntimes(runtimeRoot, ['3.5.0-aaaa1111', '3.6.0-bbbb2222', '3.7.0-cccc3333', '3.8.0-dddd4444']);
    const current = path.join(runtimeRoot, '3.9.0-eeee5555');
    fs.mkdirSync(current);
    let probes = 0;
    const inUse = () => { probes += 1; return [{ pid: 4242, exePath: path.join(oldest, 'GxMcp.Gateway.exe') }]; };

    const first = runRuntimeCleanup({ runtimeRoot, runtimeDir: current }, { getRunningProcesses: inUse });
    assert.equal(first.ran, true);
    assert.equal(first.cleanup.skipped.some((entry) => entry.reason === 'in-use' && entry.path === oldest), true);
    assert.equal(fs.existsSync(oldest), true, 'a runtime used by a running Gateway is never deleted');
    const probesAfterFirst = probes;
    assert.ok(probesAfterFirst > 0);

    const second = runRuntimeCleanup({ runtimeRoot, runtimeDir: current }, { getRunningProcesses: inUse });
    assert.equal(second.ran, false);
    assert.equal(probes, probesAfterFirst, 'no process probe inside the interval');

    const afterFresh = runRuntimeCleanup({ runtimeRoot, runtimeDir: current, fresh: true }, { getRunningProcesses: () => [] });
    assert.equal(afterFresh.ran, true);
    assert.equal(fs.existsSync(oldest), false, 'once nothing uses it, the older runtime is collected');
});

test('runtime-stager: startRuntimeCleanupInBackground only schedules deferred, due work and reports worker failures', (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-bg-test-'));
    t.after(() => { try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ } });
    const calls = [];
    const fakeWorker = () => {
        const worker = new EventEmitter();
        worker.unref = () => {};
        return worker;
    };
    const staged = { staged: true, cleanupDeferred: true, runtimeRoot: tmpRoot, runtimeDir: path.join(tmpRoot, 'v'), fresh: false };
    assert.equal(startRuntimeCleanupInBackground({ ...staged, cleanupDeferred: false }, { createWorker: () => { calls.push('x'); return fakeWorker(); } }), null);
    assert.equal(startRuntimeCleanupInBackground(null, { createWorker: () => { calls.push('x'); return fakeWorker(); } }), null);
    assert.equal(calls.length, 0);

    const results = [];
    let worker;
    const started = startRuntimeCleanupInBackground(staged, {
        onResult: (outcome) => results.push(outcome),
        createWorker: (data) => { calls.push(data); worker = fakeWorker(); return worker; }
    });
    assert.equal(started, worker);
    assert.deepEqual(calls, [{ runtimeRoot: tmpRoot, runtimeDir: staged.runtimeDir, fresh: false }]);
    worker.emit('error', new Error('boom'));
    assert.deepEqual(results, [{ ran: false, error: 'boom' }]);

    fs.writeFileSync(path.join(tmpRoot, '.cleanup-stamp'), 'x');
    assert.equal(startRuntimeCleanupInBackground(staged, { createWorker: () => { calls.push('late'); return fakeWorker(); } }), null, 'stamped inside the interval');
    assert.equal(calls.length, 1);
});

test('runtime-stager: the real cleanup worker runs and reports its result', { timeout: 60000 }, async (t) => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'gx-worker-test-'));
    t.after(() => { try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch { /* ignore */ } });
    const current = path.join(tmpRoot, '3.9.0-eeee5555');
    fs.mkdirSync(current);
    const outcome = await new Promise((resolve, reject) => {
        const worker = startRuntimeCleanupInBackground(
            { staged: true, cleanupDeferred: true, runtimeRoot: tmpRoot, runtimeDir: current, fresh: true },
            { onResult: resolve }
        );
        if (!worker) reject(new Error('worker was not started'));
    });
    assert.equal(outcome.ran, true);
    assert.ok(outcome.cleanup && Array.isArray(outcome.cleanup.deleted));
    assert.equal(fs.existsSync(path.join(tmpRoot, '.cleanup-stamp')), true);
});
