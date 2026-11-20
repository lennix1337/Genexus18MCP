'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const manifest = path.join(__dirname, '../src/nexus-ide/package.json');
function checkManifest(pkg) {
  assert.equal(Object.hasOwn(pkg, 'dependencies'), false);
  assert.equal(Object.hasOwn(pkg, 'optionalDependencies'), false);
}
function checkFiles(files) {
  for (const file of ['extension/resources/mermaid.min.js', 'extension/resources/THIRD-PARTY-NOTICES.txt']) assert.ok(files.includes(file), `missing ${file}`);
  assert.equal(files.some(file => /^extension\/vendor\//.test(file)), false);
  assert.equal(files.some(file => /(^|\/)node_modules(\/|$)/.test(file)), false);
}
test('extension has no unpackaged runtime dependencies', () => {
  checkManifest(JSON.parse(fs.readFileSync(manifest, 'utf8')));
  for (const key of ['dependencies', 'optionalDependencies']) assert.throws(() => checkManifest({ [key]: {} }));
});
test('VSIX content guard rejects missing and forbidden entries', () => {
  const files = ['extension/resources/mermaid.min.js', 'extension/resources/THIRD-PARTY-NOTICES.txt'];
  checkFiles(files);
  for (const file of files) assert.throws(() => checkFiles(files.filter(f => f !== file)));
  for (const file of ['extension/vendor/x', 'extension/node_modules/x']) assert.throws(() => checkFiles([...files, file]));
});
test('VSIX contains vendored resources and no dependencies', { skip: !process.env.NEXUS_VSIX }, () => {
  checkFiles(execFileSync('unzip', ['-Z1', process.env.NEXUS_VSIX], { encoding: 'utf8' }).split(/\r?\n/));
});
