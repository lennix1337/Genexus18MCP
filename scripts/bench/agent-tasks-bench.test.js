'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const zlib = require('node:zlib');

const {
  assertDisposableKbPath,
  parseConnectionXml,
  decodeUdmBlobHex,
  extractTokenDataList,
  parseMcpToolResult,
  parseSqlcmdRows,
  summarizeSamples,
  aggregateBenchmark,
  formatMarkdownReport,
  parseCliArgs,
  buildSqlQueries,
} = require('./agent-tasks-bench');

function makeUdmBlobHex(text, { compressed = true, overrideDeclaredLen = null, flag = null } = {}) {
  const utf8 = Buffer.from(text, 'utf8');
  const header = Buffer.from([0x01, 0x02, 0x03, 0x04, 0x00, 0x01]);
  if (compressed) {
    const gz = zlib.gzipSync(utf8);
    const lenBuf = Buffer.alloc(4);
    lenBuf.writeUInt32LE(overrideDeclaredLen == null ? utf8.length : overrideDeclaredLen, 0);
    return Buffer.concat([header, Buffer.from([flag == null ? 0x01 : flag]), lenBuf, gz]).toString('hex').toUpperCase();
  }
  return Buffer.concat([header, Buffer.from([flag == null ? 0x02 : flag]), utf8]).toString('hex').toUpperCase();
}

test('assertDisposableKbPath accepts disposable KBs under C:\\KBs\\*teste* and rejects all others', () => {
  const identity = (p) => p;
  assert.equal(assertDisposableKbPath('C:\\KBs\\KBTeste', { realpathSync: identity }), 'C:\\KBs\\KBTeste');
  assert.equal(assertDisposableKbPath('c:\\kbs\\kbteste963', { realpathSync: identity }), 'c:\\kbs\\kbteste963');
  assert.equal(assertDisposableKbPath('C:\\KBs\\my-teste-kb\\', { realpathSync: identity }), 'C:\\KBs\\my-teste-kb');

  for (const bad of [
    '',
    '   ',
    'C:\\KBs\\ProdKB',
    'C:\\KBs\\ClientApp',
    'D:\\KBs\\KBTeste',
    'C:\\Temp\\KBTeste',
    'C:\\KBs\\KBTeste\\..\\ProdKB',
    'C:\\KBs\\ProdKB\\subteste',
  ]) {
    assert.throws(() => assertDisposableKbPath(bad, { realpathSync: identity }), /disposable/i, `Expected rejection for ${bad}`);
  }

  // Symlink / junction escape guard: canonical realpath must also be inside C:\KBs\*teste*.
  assert.throws(
    () => assertDisposableKbPath('C:\\KBs\\KBTeste', { realpathSync: () => 'C:\\KBs\\ProdKB' }),
    /disposable/i,
  );
});

test('parseConnectionXml extracts ServerInstance, DBName and IntegratedSecurity and rejects missing fields', () => {
  const xml = `<ConnectionInformation>
    <DBName>GX_KB_KBTeste</DBName>
    <IntegratedSecurity>True</IntegratedSecurity>
    <ServerInstance>(LocalDB)\\MSSQLLocalDB</ServerInstance>
  </ConnectionInformation>`;
  assert.deepEqual(parseConnectionXml(xml), {
    serverInstance: '(LocalDB)\\MSSQLLocalDB',
    dbName: 'GX_KB_KBTeste',
    integratedSecurity: true,
  });

  assert.throws(() => parseConnectionXml('<ConnectionInformation><DBName>X</DBName></ConnectionInformation>'), /ServerInstance/i);
  assert.throws(() => parseConnectionXml('<ConnectionInformation><ServerInstance>S</ServerInstance></ConnectionInformation>'), /DBName/i);
});

test('decodeUdmBlobHex decodes gzip (01) and raw UTF-8 (02) blobs and fails closed on corruption', () => {
  const sample = '<TokenDataList><TokenData><Word>Event</Word></TokenData></TokenDataList>';
  assert.equal(decodeUdmBlobHex(makeUdmBlobHex(sample, { compressed: true })), sample);
  assert.equal(decodeUdmBlobHex(makeUdmBlobHex(sample, { compressed: false })), sample);

  // Odd hex length simulates sqlcmd truncation bug and must throw.
  const truncated = makeUdmBlobHex(sample, { compressed: true }).slice(0, -1);
  assert.throws(() => decodeUdmBlobHex(truncated), /odd|truncated/i);

  // Wrong magic header.
  assert.throws(() => decodeUdmBlobHex('000000000001024142'), /magic/i);

  // Unknown compression flag.
  assert.throws(() => decodeUdmBlobHex(makeUdmBlobHex(sample, { compressed: false, flag: 0x09 })), /flag/i);

  // Declared uncompressed length mismatch.
  assert.throws(
    () => decodeUdmBlobHex(makeUdmBlobHex(sample, { compressed: true, overrideDeclaredLen: sample.length + 5 })),
    /length/i,
  );
});

test('extractTokenDataList reconstructs GeneXus source and unescapes XML entities', () => {
  const xml = [
    '<TokenDataList>',
    '<TokenData><Token>109</Token><Word>If</Word><Id>2</Id></TokenData>',
    '<TokenData><Token>25</Token><Word> </Word><Id>0</Id></TokenData>',
    '<TokenData><Token>23</Token><Word>&amp;Total &lt;= 10 &amp;&amp; &quot;ok&quot; &gt; &apos;a&apos;</Word><Id>0</Id></TokenData>',
    '<TokenData><Token>25</Token><Word>&#10;\tEndIf</Word><Id>0</Id></TokenData>',
    '</TokenDataList>',
  ].join('');

  const parsed = extractTokenDataList(xml);
  assert.equal(parsed.tokenized, true);
  assert.equal(parsed.text, 'If &Total <= 10 && "ok" > \'a\'\n\tEndIf');

  const plain = extractTokenDataList('plain text without tokens');
  assert.equal(plain.tokenized, false);
  assert.equal(plain.text, 'plain text without tokens');
});

test('parseMcpToolResult measures wireBytes and LLM-visible contentBytes and rejects error envelopes', () => {
  const envelope = {
    jsonrpc: '2.0',
    id: 7,
    result: {
      content: [
        { type: 'text', text: '{"name":"WWTrn353","type":"WebPanel"}' },
      ],
    },
  };
  const rawLine = JSON.stringify(envelope);
  const parsed = parseMcpToolResult(envelope, rawLine);
  assert.equal(parsed.text, '{"name":"WWTrn353","type":"WebPanel"}');
  assert.equal(parsed.contentBytes, Buffer.byteLength(parsed.text, 'utf8'));
  assert.equal(parsed.wireBytes, Buffer.byteLength(rawLine, 'utf8'));
  assert.equal(parsed.estTokens, Math.round(parsed.contentBytes / 4));

  assert.throws(() => parseMcpToolResult({ jsonrpc: '2.0', id: 1, error: { code: -32000, message: 'boom' } }), /boom/);
  assert.throws(() => parseMcpToolResult({ jsonrpc: '2.0', id: 2, result: { isError: true, content: [{ type: 'text', text: 'failed' }] } }), /failed/);
  assert.throws(() => parseMcpToolResult({ jsonrpc: '2.0', id: 3, result: {} }), /content/i);
});

test('parseSqlcmdRows strips blank lines, dash separators and row-count footers', () => {
  const out = [
    '',
    'WebPanel\tWWTrn353\t66',
    '--------\t--------\t--',
    'Transaction\tTrn353\t40',
    '(2 rows affected)',
    '',
  ].join('\r\n');
  assert.deepEqual(parseSqlcmdRows(out, '\t'), [
    ['WebPanel', 'WWTrn353', '66'],
    ['Transaction', 'Trn353', '40'],
  ]);
});

test('summarizeSamples computes exact medians, p95 and rejects empty or failed samples', () => {
  const summary = summarizeSamples([
    { calls: 1, latencyMs: 10, responseBytes: 400, ok: true, hits: 1 },
    { calls: 1, latencyMs: 20, responseBytes: 420, ok: true, hits: 1 },
    { calls: 1, latencyMs: 30, responseBytes: 400, ok: true, hits: 1 },
    { calls: 1, latencyMs: 100, responseBytes: 400, ok: true, hits: 1 },
  ]);
  assert.equal(summary.iterations, 4);
  assert.equal(summary.calls, 1);
  assert.equal(summary.responseBytes, 400);
  assert.equal(summary.estTokens, 100);
  assert.equal(summary.latencyMs.min, 10);
  assert.equal(summary.latencyMs.p50, 25);
  assert.equal(summary.latencyMs.p95, 100);
  assert.equal(summary.latencyMs.max, 100);
  assert.equal(summary.latencyMs.mean, 40);
  assert.equal(summary.hits, 1);

  assert.throws(() => summarizeSamples([]), /at least one/i);
  assert.throws(() => summarizeSamples([{ calls: 1, latencyMs: 5, responseBytes: 10, ok: false, error: 'timeout' }]), /timeout/);
});

test('aggregateBenchmark computes per-task rows and safe ratios without NaN or Infinity', () => {
  const agg = aggregateBenchmark([
    {
      task: 'read_source',
      label: 'Read source (WWTrn353 Events)',
      approach: 'mcp',
      fidelity: 'GeneXus Events source',
      samples: [
        { calls: 1, latencyMs: 12, responseBytes: 1600, ok: true },
        { calls: 1, latencyMs: 14, responseBytes: 1600, ok: true },
      ],
    },
    {
      task: 'read_source',
      label: 'Read source (WWTrn353 Events)',
      approach: 'plain-sql',
      fidelity: 'GeneXus Events source (UDM blob decoded)',
      samples: [
        { calls: 2, latencyMs: 180, responseBytes: 3200, ok: true },
        { calls: 2, latencyMs: 200, responseBytes: 3200, ok: true },
      ],
    },
    {
      task: 'read_source',
      label: 'Read source (WWTrn353 Events)',
      approach: 'plain-fs',
      fidelity: 'Generated C# only (not GeneXus source)',
      samples: [
        { calls: 1, latencyMs: 2, responseBytes: 80000, ok: true },
        { calls: 1, latencyMs: 2, responseBytes: 80000, ok: true },
      ],
    },
  ]);

  assert.equal(agg.tasks.length, 1);
  const t = agg.tasks[0];
  assert.equal(t.task, 'read_source');
  assert.equal(t.approaches.mcp.responseBytes, 1600);
  assert.equal(t.approaches['plain-sql'].calls, 2);
  assert.equal(t.ratios.plainSqlToMcpBytes, 2);
  assert.equal(t.ratios.plainFsToMcpBytes, 50);
  assert.ok(Number.isFinite(t.ratios.plainSqlToMcpLatency));
});

test('formatMarkdownReport produces a reproducible comparison table and caveats', () => {
  const aggregated = aggregateBenchmark([
    {
      task: 'find_object',
      label: 'Find object',
      approach: 'mcp',
      fidelity: 'KB metadata',
      firstCallMs: 12.5,
      samples: [{ calls: 1, latencyMs: 5, responseBytes: 400, ok: true }],
    },
    {
      task: 'find_object',
      label: 'Find object',
      approach: 'plain-sql',
      fidelity: 'UDM table row',
      firstCallMs: 95.1,
      samples: [{ calls: 1, latencyMs: 90, responseBytes: 200, ok: true }],
    },
    {
      task: 'find_object',
      label: 'Find object',
      approach: 'plain-fs',
      fidelity: 'Generated files only',
      firstCallMs: 28.4,
      samples: [{ calls: 1, latencyMs: 25, responseBytes: 120, ok: true }],
    },
  ]);

  const md = formatMarkdownReport({
    date: '2026-11-24',
    kbPath: 'C:\\KBs\\KBTeste',
    iterations: 5,
    warmup: 1,
    gatewayVersion: '4.7.3',
    gatewayCommit: '513e72d5',
    toolProfile: 'standard',
    startup: { mcpColdStartMs: 4200, sqlConnectCheckMs: 95 },
    aggregated,
  });

  assert.match(md, /2026-11-24/);
  assert.match(md, /node scripts\/bench\/agent-tasks-bench\.js/);
  assert.match(md, /\| Task \| Approach \| Calls \| Response bytes \| Est\. tokens \| 1st call \(ms\) \| Warm p50 \(ms\) \| Warm p95 \(ms\) \| Fidelity \/ Scope \|/);
  assert.match(md, /12\.50/);
  assert.match(md, /Find object/);
  assert.match(md, /plain-sql/);
  assert.match(md, /plain-fs/);
  assert.match(md, /4200|4,200|4\.200/);
  assert.match(md, /What this does not measure|O que isto/i);
});

test('parseCliArgs parses defaults, overrides, and rejects invalid arguments', () => {
  const defaults = parseCliArgs([]);
  assert.equal(defaults.kbPath, 'C:\\KBs\\KBTeste');
  assert.equal(defaults.iterations, 5);
  assert.equal(defaults.warmup, 1);

  const custom = parseCliArgs(['--kb', 'C:\\KBs\\KBTeste963', '--iterations', '7', '--warmup', '2', '--ref-type', 'WebPanel', '--out-md', 'docs/benchmarks/out.md']);
  assert.equal(custom.kbPath, 'C:\\KBs\\KBTeste963');
  assert.equal(custom.iterations, 7);
  assert.equal(custom.warmup, 2);
  assert.equal(custom.refType, 'WebPanel');
  assert.equal(custom.outMd, 'docs/benchmarks/out.md');

  assert.throws(() => parseCliArgs(['--iterations', '0']), /iterations/i);
  assert.throws(() => parseCliArgs(['--unknown-flag']), /unknown/i);
});

test('buildSqlQueries scopes reference lookup by refType and rejects unsafe identifiers', () => {
  const q = buildSqlQueries({
    targetObject: 'WWTrn353',
    targetPart: 'Events',
    refTarget: 'Trn353',
    refType: 'Transaction',
  });
  assert.match(q.listReferences, /et\.EntityTypeName = 'Transaction'/);
  assert.throws(
    () => buildSqlQueries({ targetObject: "Trn353'; DROP TABLE Entity;--", targetPart: 'Events', refTarget: 'Trn353' }),
    /Invalid targetObject/i,
  );
});
