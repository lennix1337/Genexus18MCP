const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { findForbiddenFiles, readPackManifest } = require('./check-pack');

test('findForbiddenFiles flags test files and test-support, on either separator', () => {
    const files = [
        { path: 'cli/index.js' },
        { path: 'cli/run.test.js' },
        { path: 'cli\\lib\\config.test.js' },
        { path: 'cli/test-support/home-write-guard.js' },
        { path: 'scripts/verify-install.js' }
    ];
    assert.deepEqual(findForbiddenFiles(files), [
        'cli/run.test.js',
        'cli/lib/config.test.js',
        'cli/test-support/home-write-guard.js'
    ]);
});

test('findForbiddenFiles accepts a clean package', () => {
    assert.deepEqual(findForbiddenFiles([{ path: 'cli/run.js' }, { path: 'package.json' }]), []);
});

test('the real package contains no test files', () => {
    const result = spawnSync(process.execPath, [path.join(__dirname, 'check-pack.js')], { encoding: 'utf8', timeout: 120000 });
    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
    assert.match(result.stdout, /\d+ files, \d+ bytes unpacked/);
});

test('readPackManifest accepts the npm 12 shape, an object keyed by package name', () => {
    const files = [{ path: 'cli/run.js' }];
    const runner = () => ({ status: 0, stdout: JSON.stringify({ 'genexus-mcp': { files } }) });
    assert.deepEqual(readPackManifest(runner).files, files);
});

test('readPackManifest still accepts the older array shape', () => {
    const files = [{ path: 'cli/run.js' }];
    const runner = () => ({ status: 0, stdout: JSON.stringify([{ files }]) });
    assert.deepEqual(readPackManifest(runner).files, files);
});
