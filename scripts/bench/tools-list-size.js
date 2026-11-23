const fs = require('fs');
const path = require('path');

const DIR = path.join(__dirname, '..', '..', 'src', 'GxMcp.Gateway.Tests', 'Fixtures', 'Contract', 'Discovery');
const BYTES_PER_TOKEN = 4;

function measure(file) {
  const parsed = JSON.parse(fs.readFileSync(path.join(DIR, file), 'utf8'));
  const tools = (parsed.result || parsed).tools || [];
  const visible = tools.map((t) => ({ name: t.name, description: t.description, inputSchema: t.inputSchema }));
  const bytes = Buffer.byteLength(JSON.stringify(visible));
  return { tools: tools.length, bytes, estTokens: Math.round(bytes / BYTES_PER_TOKEN) };
}

function profiles() {
  return fs
    .readdirSync(DIR)
    .map((f) => /^tools-list(?:\.([a-z]+))?\.response\.json$/.exec(f))
    .filter(Boolean)
    .map((m) => ({ profile: m[1] || 'full', file: m[0] }))
    .map(({ profile, file }) => ({ profile, ...measure(file) }))
    .sort((a, b) => a.bytes - b.bytes);
}

if (require.main === module) {
  console.log('profile\ttools\tbytes\test_tokens');
  for (const p of profiles()) console.log([p.profile, p.tools, p.bytes, p.estTokens].join('\t'));
}

module.exports = { measure, profiles };
