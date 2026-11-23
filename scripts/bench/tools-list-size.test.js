const test = require('node:test');
const assert = require('node:assert');
const { profiles } = require('./tools-list-size');

test('every profile fixture is measured and core is smaller than full', () => {
  const byName = Object.fromEntries(profiles().map((p) => [p.profile, p]));
  for (const name of ['core', 'standard', 'full']) assert.ok(byName[name], name);
  assert.ok(byName.core.bytes > 0);
  assert.ok(byName.core.bytes < byName.standard.bytes);
  assert.ok(byName.standard.bytes < byName.full.bytes);
});
