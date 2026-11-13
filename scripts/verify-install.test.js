'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

test('postinstall accepts a linked git worktree as a development checkout', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gxmcp-verify-install-'));
  try {
    fs.mkdirSync(path.join(root, 'scripts'), { recursive: true });
    fs.copyFileSync(path.join(__dirname, 'verify-install.js'), path.join(root, 'scripts', 'verify-install.js'));
    fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ version: '3.5.3' }));
    fs.writeFileSync(path.join(root, '.git'), 'gitdir: C:/repo/.git/worktrees/test\n');

    const result = spawnSync(process.execPath, [path.join(root, 'scripts', 'verify-install.js')], {
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stderr, /dev checkout/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

function runInstalledPackage(version, files) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'gxmcp-verify-install-pkg-'));
  try {
    fs.mkdirSync(path.join(root, 'scripts'), { recursive: true });
    fs.copyFileSync(path.join(__dirname, 'verify-install.js'), path.join(root, 'scripts', 'verify-install.js'));
    fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ version }));
    for (const rel of files) {
      fs.mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
      fs.writeFileSync(path.join(root, rel), 'x');
    }
    return spawnSync(process.execPath, [path.join(root, 'scripts', 'verify-install.js')], { encoding: 'utf8' });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

const baseFiles = [
  'publish/GxMcp.Gateway.exe',
  'publish/worker/GxMcp.Worker.exe',
  'publish/tool_definitions.json',
  'publish/gxmcp-manifest.json',
];
const engineFiles = ['publish/gx-engine.exe', 'publish/gxwire.exe'];

test('postinstall still accepts a published 4.6.0 package without the engine binaries', () => {
  const result = runInstalledPackage('4.6.0', baseFiles);
  assert.equal(result.status, 0, result.stderr);
});

test('postinstall rejects a package above 4.6.0 that lacks the engine binaries', () => {
  for (const version of ['4.6.1', '4.7.0', '5.0.0']) {
    const result = runInstalledPackage(version, baseFiles);
    assert.equal(result.status, 1, `${version}: ${result.stdout}`);
    assert.match(result.stderr, /publish\/gx-engine\.exe/);
    assert.match(result.stderr, /publish\/gxwire\.exe/);
  }
});

test('postinstall accepts a package above 4.6.0 that ships the engine binaries', () => {
  const result = runInstalledPackage('4.6.1', [...baseFiles, ...engineFiles]);
  assert.equal(result.status, 0, result.stderr);
});
