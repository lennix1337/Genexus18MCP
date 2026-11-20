const test = require('node:test');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');

const root = join(__dirname, '..');
const read = (path) => readFileSync(join(root, path));
const json = (path) => JSON.parse(read(path));

test('vendored Mermaid matches its manifest, pin and webview comment', () => {
  const manifest = json('src/nexus-ide/vendor/mermaid.vendor.json');
  const pkg = json('src/nexus-ide/package.json');
  const comment = read('src/nexus-ide/src/webviews/DiagramView.ts').toString();
  const hash = createHash('sha256').update(read('src/nexus-ide/resources/mermaid.min.js')).digest('hex');
  assert.equal(manifest.version, '11.16.1');
  assert.equal(manifest.sha256, '18327bef70d96fb505fe7287d9f6a7362ebf07ff6576ddfaffb1a06f3e1a2954');
  assert.equal(hash, manifest.sha256);
  assert.equal(pkg.devDependencies.mermaid, manifest.version);
  assert.ok(comment.includes('`mermaid@' + manifest.version + '`'));
});
