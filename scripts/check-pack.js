#!/usr/bin/env node
// Fails when the npm tarball would contain test files. `.npmignore` cannot
// override `files` in package.json, so the exclusions live there as negated
// patterns; this check proves they hold.
//
// CI: node scripts/check-pack.js
const { spawnSync } = require('child_process');
const path = require('path');

const root = path.resolve(__dirname, '..');
const FORBIDDEN = [/\.test\.[cm]?[jt]s$/i, /(^|\/)test-support(\/|$)/i];

function findForbiddenFiles(files) {
    return files
        .map((file) => String(file.path).replace(/\\/g, '/'))
        .filter((filePath) => FORBIDDEN.some((pattern) => pattern.test(filePath)));
}

function readPackManifest(runner = spawnSync) {
    // --ignore-scripts: no prepack side effects and no lifecycle noise next to the JSON.
    const result = runner('npm pack --dry-run --json --ignore-scripts', {
        cwd: root,
        encoding: 'utf8',
        // npm is npm.cmd on Windows, which spawn only accepts through a shell.
        shell: true,
        maxBuffer: 64 * 1024 * 1024
    });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`npm pack --dry-run failed (exit ${result.status}): ${result.stderr}`);
    const parsed = JSON.parse(result.stdout);
    const manifest = Array.isArray(parsed) ? parsed[0] : parsed;
    if (!manifest || !Array.isArray(manifest.files)) throw new Error('npm pack --json returned no file list.');
    return manifest;
}

function main() {
    const manifest = readPackManifest();
    const offenders = findForbiddenFiles(manifest.files);
    console.log(`[check-pack] ${manifest.files.length} files, ${manifest.unpackedSize} bytes unpacked`);
    if (offenders.length > 0) {
        console.error('[check-pack] FAIL: test files would be published:');
        for (const offender of offenders) console.error(`  ${offender}`);
        return 1;
    }
    console.log('[check-pack] OK: no test files in the package');
    return 0;
}

if (require.main === module) {
    try {
        process.exitCode = main();
    } catch (error) {
        console.error(`[check-pack] ERROR: ${error.message}`);
        process.exitCode = 1;
    }
}

module.exports = { findForbiddenFiles, readPackManifest };
