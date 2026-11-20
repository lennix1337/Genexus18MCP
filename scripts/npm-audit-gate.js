'use strict';

const fs = require('node:fs');

function checkAudit(audit, lock, entries, tree, today = new Date().toISOString().slice(0, 10)) {
  const errors = [];
  if (!audit || !audit.vulnerabilities || !audit.metadata?.vulnerabilities) return ['Invalid npm audit report'];
  const seen = new Set();
  for (const entry of entries.filter(e => e.tree === tree)) {
    if (!/^GHSA-[\w-]+$/.test(entry.ghsa) || !entry.package || !entry.version || !entry.reason || !entry.owner || !/^\d{4}-\d{2}-\d{2}$/.test(entry.expiresOn) || Number.isNaN(Date.parse(`${entry.expiresOn}T00:00:00Z`)) || new Date(`${entry.expiresOn}T00:00:00Z`).toISOString().slice(0, 10) !== entry.expiresOn) errors.push(`Invalid allowlist entry: ${entry.ghsa}`);
    else if (entry.expiresOn < today) errors.push(`Expired: ${entry.ghsa}`);
  }
  for (const [name, vulnerability] of Object.entries(audit.vulnerabilities)) {
    if (['high', 'critical'].includes(vulnerability.severity)) errors.push(`${name}: ${vulnerability.severity}`);
    for (const via of vulnerability.via || []) {
      if (typeof via === 'string') continue; // indirect dependency; its own entry holds the advisory
      const ghsa = /GHSA-[\w-]+/.exec(via.url || '')?.[0];
      if (!ghsa) { errors.push(`${name}: advisory without GHSA`); continue; }
      const versions = (vulnerability.nodes || []).map(node => lock.packages?.[node]?.version);
      for (const entry of entries.filter(e => e.tree === tree && e.package === name && e.ghsa === ghsa)) {
        if (versions.includes(entry.version)) seen.add(entry);
      }
      if (via.severity === 'moderate' && (!versions.length || versions.some(version => !entries.some(e => e.tree === tree && e.package === name && e.ghsa === ghsa && e.version === version)))) errors.push(`Unlisted moderate: ${name} ${ghsa}`);
      if (['high', 'critical'].includes(via.severity)) errors.push(`${name}: ${ghsa} ${via.severity}`);
    }
  }
  for (const entry of entries.filter(e => e.tree === tree)) if (!seen.has(entry)) errors.push(`Allowlisted advisory absent: ${entry.ghsa} ${entry.package}@${entry.version}`);
  return errors;
}

if (require.main === module) {
  try {
    const [tree, report, lockfile, allowfile, exitCode] = process.argv.slice(2);
    if (!['root', 'nexus'].includes(tree) || !/^[01]$/.test(exitCode || '')) throw new Error('Usage: node npm-audit-gate.js root|nexus report.json package-lock.json allowlist.json audit-exit-code (0 or 1)');
    const load = file => JSON.parse(fs.readFileSync(file, 'utf8'));
    const audit = load(report);
    const errors = checkAudit(audit, load(lockfile), load(allowfile), tree);
    if ((Number(exitCode) === 1) !== ((audit.metadata?.vulnerabilities?.high || 0) + (audit.metadata?.vulnerabilities?.critical || 0) > 0)) errors.push('Audit exit code inconsistent with high/critical counts');
    if (errors.length) { console.error(errors.join('\n')); process.exitCode = 1; }
    else console.log(`${tree}: audit gate passed`);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { checkAudit };
