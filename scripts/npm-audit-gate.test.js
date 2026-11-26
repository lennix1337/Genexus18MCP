'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { checkAudit } = require('./npm-audit-gate');
const entry = { ghsa: 'GHSA-aaaa-bbbb-cccc', package: 'demo', tree: 'nexus', version: '1.0.0', reason: 'test', owner: 'test', expiresOn: '2099-01-01' };
const report = () => ({ metadata: { vulnerabilities: {} }, vulnerabilities: { demo: { severity: 'moderate', nodes: ['node_modules/demo'], via: [{ url: `https://github.com/advisories/${entry.ghsa}`, severity: 'moderate' }], fixAvailable: { name: 'demo', version: '2', isSemVerMajor: true } }, parent: { severity: 'moderate', via: ['demo'], fixAvailable: false } } });
const lock = { packages: { 'node_modules/demo': { version: '1.0.0' } } };
const check = (audit = report(), entries = [entry]) => checkAudit(audit, lock, entries, 'nexus', '2026-01-01');
test('accepts current moderate and string via regardless of fixAvailable shape', () => { for (const fix of [true, false, { name: 'demo' }]) { const a = report(); a.vulnerabilities.demo.fixAvailable = fix; assert.deepEqual(check(a), []); } });
test('rejects high and critical even when allowlisted', () => { for (const severity of ['high', 'critical']) { const a = report(); a.vulnerabilities.demo.severity = severity; a.vulnerabilities.demo.via[0].severity = severity; assert.ok(check(a).length); } });
test('rejects high and critical on the package alone, and on the advisory alone', () => {
  for (const severity of ['high', 'critical']) {
    const pkg = report(); pkg.vulnerabilities.demo.severity = severity;
    assert.deepEqual(check(pkg), [`demo: ${severity}`]);
    const adv = report(); adv.vulnerabilities.demo.via[0].severity = severity;
    assert.deepEqual(check(adv), [`demo: ${entry.ghsa} ${severity}`]);
  }
});
test('rejects expired entries', () => assert.match(check(report(), [{ ...entry, expiresOn: '2025-12-31' }]).join(), /Expired/));
test('rejects absent entries and changed versions', () => { const a = report(); delete a.vulnerabilities.demo; assert.match(check(a).join(), /absent/); assert.match(check(report(), [{ ...entry, version: '0.9' }]).join(), /absent/); });
test('rejects unlisted moderates and malformed reports', () => { assert.match(check(report(), []).join(), /Unlisted/); assert.match(check(null).join(), /Invalid/); });
