#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const zlib = require('node:zlib');
const { spawn, execFileSync } = require('node:child_process');
const { performance } = require('node:perf_hooks');

const BYTES_PER_TOKEN = 4;
const UDM_MAGIC = Buffer.from([0x01, 0x02, 0x03, 0x04]);
const DEFAULT_GX_PATH = 'C:\\Program Files (x86)\\GeneXus\\GeneXus18';

function assertDisposableKbPath(kbPath, { realpathSync = fs.realpathSync } = {}) {
  if (typeof kbPath !== 'string' || kbPath.trim().length === 0) {
    throw new Error('Refused: KB path must be a non-empty disposable path under C:\\KBs\\*teste*.');
  }
  const normalizedInput = path.win32.resolve(kbPath.trim()).replace(/[\\/]+$/, '');
  const canonical = resolveCanonicalPath(normalizedInput, realpathSync).replace(/[\\/]+$/, '');

  for (const candidate of [normalizedInput, canonical]) {
    const match = /^c:\\kbs\\([^\\]+)$/i.exec(candidate);
    if (!match || !/teste/i.test(match[1])) {
      throw new Error(
        `Refused: "${kbPath}" (resolved "${canonical}") is outside the disposable KB boundary C:\\KBs\\*teste*.`,
      );
    }
  }
  return canonical;
}

function resolveCanonicalPath(targetPath, realpathSync) {
  try {
    return path.win32.resolve(realpathSync(targetPath));
  } catch {
    return targetPath;
  }
}

function parseConnectionXml(xml) {
  if (typeof xml !== 'string' || xml.trim().length === 0) {
    throw new Error('knowledgebase.connection content is empty.');
  }
  const readTag = (tag) => {
    const m = new RegExp(`<${tag}>([^<]+)</${tag}>`, 'i').exec(xml);
    return m ? m[1].trim() : null;
  };
  const serverInstance = readTag('ServerInstance');
  const dbName = readTag('DBName');
  if (!serverInstance) {
    throw new Error('knowledgebase.connection is missing <ServerInstance>.');
  }
  if (!dbName) {
    throw new Error('knowledgebase.connection is missing <DBName>.');
  }
  const integratedRaw = readTag('IntegratedSecurity');
  const integratedSecurity = integratedRaw == null ? true : /^true$/i.test(integratedRaw);
  return { serverInstance, dbName, integratedSecurity };
}

function decodeUdmBlobHex(hexText) {
  if (typeof hexText !== 'string') {
    throw new Error('UDM blob hex input must be a string.');
  }
  const compact = hexText.replace(/\s+/g, '');
  if (compact.length === 0) {
    throw new Error('UDM blob hex payload is empty.');
  }
  if (!/^[0-9a-fA-F]+$/.test(compact)) {
    throw new Error('UDM blob hex payload contains non-hexadecimal characters.');
  }
  if (compact.length % 2 !== 0) {
    throw new Error(`UDM blob hex payload has odd length (${compact.length}); possible sqlcmd truncation.`);
  }
  const buf = Buffer.from(compact, 'hex');
  if (buf.length < 7) {
    throw new Error(`UDM blob too short (${buf.length} bytes).`);
  }
  if (!buf.subarray(0, 4).equals(UDM_MAGIC)) {
    throw new Error(`UDM blob invalid magic header: ${buf.subarray(0, 4).toString('hex')}.`);
  }
  const flag = buf[6];
  if (flag === 0x02) {
    return buf.subarray(7).toString('utf8');
  }
  if (flag === 0x01) {
    if (buf.length < 11) {
      throw new Error(`UDM gzip blob missing uncompressed length header (${buf.length} bytes).`);
    }
    const declaredLength = buf.readUInt32LE(7);
    const inflated = zlib.gunzipSync(buf.subarray(11));
    if (inflated.length !== declaredLength) {
      throw new Error(
        `UDM blob uncompressed length mismatch: declared ${declaredLength}, got ${inflated.length}.`,
      );
    }
    return inflated.toString('utf8');
  }
  throw new Error(`UDM blob unsupported compression flag: 0x${flag.toString(16).padStart(2, '0')}.`);
}

function decodeXmlEntities(text) {
  return text.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|amp|lt|gt|quot|apos);/g, (_, entity) => {
    if (entity === 'amp') return '&';
    if (entity === 'lt') return '<';
    if (entity === 'gt') return '>';
    if (entity === 'quot') return '"';
    if (entity === 'apos') return '\'';
    if (entity.startsWith('#x') || entity.startsWith('#X')) {
      return String.fromCodePoint(parseInt(entity.slice(2), 16));
    }
    return String.fromCodePoint(parseInt(entity.slice(1), 10));
  });
}

function extractTokenDataList(xml) {
  if (typeof xml !== 'string') {
    throw new Error('TokenDataList input must be a string.');
  }
  if (!/<TokenDataList\b/i.test(xml)) {
    return { tokenized: false, text: xml };
  }
  const words = [];
  const pattern = /<Word>([\s\S]*?)<\/Word>/g;
  let match;
  while ((match = pattern.exec(xml)) !== null) {
    words.push(decodeXmlEntities(match[1]));
  }
  return { tokenized: true, text: words.join('') };
}

function parseMcpToolResult(envelope, rawLine = null) {
  if (!envelope || typeof envelope !== 'object') {
    throw new Error('MCP response envelope is missing or invalid.');
  }
  if (envelope.error) {
    const code = envelope.error.code != null ? ` (${envelope.error.code})` : '';
    throw new Error(`MCP JSON-RPC error${code}: ${envelope.error.message || JSON.stringify(envelope.error)}`);
  }
  const result = envelope.result;
  if (!result || !Array.isArray(result.content)) {
    throw new Error('MCP tool response is missing result.content array.');
  }
  const texts = result.content
    .filter((block) => block && typeof block.text === 'string')
    .map((block) => block.text);
  const text = texts.join('\n');
  if (result.isError) {
    throw new Error(`MCP tool returned isError=true: ${text || 'unknown error'}`);
  }
  const contentBytes = Buffer.byteLength(text, 'utf8');
  const wireBytes = rawLine != null
    ? Buffer.byteLength(rawLine, 'utf8')
    : Buffer.byteLength(JSON.stringify(envelope), 'utf8');
  return {
    text,
    contentBytes,
    wireBytes,
    estTokens: Math.round(contentBytes / BYTES_PER_TOKEN),
  };
}

function parseSqlcmdRows(stdout, delimiter = '\t') {
  if (typeof stdout !== 'string') return [];
  const rows = [];
  for (const rawLine of stdout.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) continue;
    if (/^\(\d+\s+rows?\s+affected\)$/i.test(line)) continue;
    if (/^[-\s|,\t]+$/.test(line)) continue;
    rows.push(rawLine.split(delimiter).map((col) => col.trim()));
  }
  return rows;
}

function percentile(sortedValues, p) {
  if (sortedValues.length === 0) return 0;
  if (sortedValues.length === 1) return sortedValues[0];
  if (p === 0.5 && sortedValues.length % 2 === 0) {
    const mid = sortedValues.length / 2;
    return (sortedValues[mid - 1] + sortedValues[mid]) / 2;
  }
  const idx = Math.min(sortedValues.length - 1, Math.max(0, Math.ceil(p * sortedValues.length) - 1));
  return sortedValues[idx];
}

function round2(n) {
  return Math.round(n * 100) / 100;
}

function summarizeSamples(samples) {
  if (!Array.isArray(samples) || samples.length === 0) {
    throw new Error('summarizeSamples requires at least one sample.');
  }
  for (const s of samples) {
    if (!s || s.ok === false) {
      throw new Error(`Benchmark sample failed: ${s && s.error ? s.error : 'ok=false'}`);
    }
  }
  const latencies = samples.map((s) => Number(s.latencyMs)).sort((a, b) => a - b);
  const bytesList = samples.map((s) => Number(s.responseBytes)).sort((a, b) => a - b);
  const callsList = samples.map((s) => Number(s.calls)).sort((a, b) => a - b);
  const medianBytes = Math.round(percentile(bytesList, 0.5));
  const meanLatency = latencies.reduce((acc, v) => acc + v, 0) / latencies.length;

  const summary = {
    iterations: samples.length,
    calls: Math.round(percentile(callsList, 0.5)),
    responseBytes: medianBytes,
    estTokens: Math.round(medianBytes / BYTES_PER_TOKEN),
    latencyMs: {
      min: round2(latencies[0]),
      p50: round2(percentile(latencies, 0.5)),
      p95: round2(percentile(latencies, 0.95)),
      max: round2(latencies[latencies.length - 1]),
      mean: round2(meanLatency),
    },
  };

  if (samples.every((s) => typeof s.hits === 'number')) {
    const hitsSorted = samples.map((s) => s.hits).sort((a, b) => a - b);
    summary.hits = Math.round(percentile(hitsSorted, 0.5));
  }
  if (samples.every((s) => typeof s.rawTransferBytes === 'number')) {
    const rawSorted = samples.map((s) => s.rawTransferBytes).sort((a, b) => a - b);
    summary.rawTransferBytes = Math.round(percentile(rawSorted, 0.5));
  }
  return summary;
}

function safeRatio(numerator, denominator) {
  if (typeof numerator !== 'number' || typeof denominator !== 'number' || denominator <= 0) {
    return null;
  }
  return round2(numerator / denominator);
}

function aggregateBenchmark(rawRuns) {
  const byTask = new Map();
  for (const entry of rawRuns) {
    if (!byTask.has(entry.task)) {
      byTask.set(entry.task, {
        task: entry.task,
        label: entry.label || entry.task,
        approaches: Object.create(null),
      });
    }
    const bucket = byTask.get(entry.task);
    const firstCallMs = typeof entry.firstCallMs === 'number' ? round2(entry.firstCallMs) : null;
    bucket.approaches[entry.approach] = {
      ...summarizeSamples(entry.samples),
      firstCallMs,
      fidelity: entry.fidelity || '',
      commandSummary: entry.commandSummary || '',
    };
  }

  const tasks = [];
  for (const item of byTask.values()) {
    const mcp = item.approaches.mcp || null;
    const plainSql = item.approaches['plain-sql'] || null;
    const plainFs = item.approaches['plain-fs'] || null;
    tasks.push({
      task: item.task,
      label: item.label,
      approaches: item.approaches,
      ratios: {
        plainSqlToMcpBytes: mcp && plainSql ? safeRatio(plainSql.responseBytes, mcp.responseBytes) : null,
        plainFsToMcpBytes: mcp && plainFs ? safeRatio(plainFs.responseBytes, mcp.responseBytes) : null,
        plainSqlToMcpLatency: mcp && plainSql ? safeRatio(plainSql.latencyMs.p50, mcp.latencyMs.p50) : null,
        plainFsToMcpLatency: mcp && plainFs ? safeRatio(plainFs.latencyMs.p50, mcp.latencyMs.p50) : null,
      },
    });
  }
  return { tasks };
}

function fmtInt(n) {
  return Number(n).toLocaleString('en-US');
}

function fmtMs(n) {
  return Number(n).toFixed(2);
}

function formatMarkdownReport(report) {
  const {
    date,
    kbPath,
    iterations,
    warmup,
    gatewayVersion,
    gatewayCommit,
    sdkVersion,
    kbObjectCount,
    toolProfile,
    targets = {},
    startup = {},
    aggregated,
  } = report;

  const lines = [];
  lines.push(`# Agent tasks benchmark: Genexus18MCP vs plain approaches (${date})`);
  lines.push('');
  lines.push('Read-only benchmark comparing **Genexus18MCP** (isolated stdio Gateway + Worker) against two plain non-MCP approaches on the disposable Knowledge Base:');
  lines.push('- **`mcp`**: Genexus18MCP tools (`genexus_query`, `genexus_read`, `genexus_impact`, `genexus_search_source`) over one warm MCP stdio session (`EmitStructuredContent: false`, `TerseResponses: true`).');
  lines.push('- **`plain-sql`**: Direct read-only `sqlcmd` queries against the KB LocalDB (`GX_KB_KBTeste`, `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED`) plus local GZip/`<TokenDataList>` decoding of `EntityVersionData` blobs.');
  lines.push('- **`plain-fs`**: Direct filesystem inspection (`rg` / `fs.readFileSync`) over the KB folder on disk (`NetModel\\Web\\*.cs` generated artifacts).');
  lines.push('');
  lines.push('## Environment and reproducibility');
  lines.push('');
  lines.push('| Parameter | Value |');
  lines.push('|---|---|');
  lines.push(`| Date | ${date} |`);
  lines.push(`| Disposable KB | \`${kbPath}\` (read-only${kbObjectCount != null ? `, ${fmtInt(kbObjectCount)} indexed objects` : ''}) |`);
  if (sdkVersion) {
    lines.push(`| GeneXus SDK | ${sdkVersion} |`);
  }
  lines.push(`| Gateway version / commit | ${gatewayVersion || 'unknown'} (${gatewayCommit || 'unknown'}) |`);
  lines.push(`| Tool profile | \`${toolProfile || 'standard'}\` |`);
  lines.push(`| Iterations (after warmup) | ${iterations} (warmup: ${warmup}) |`);
  if (targets.targetObject) {
    const refLabelTarget = targets.refType ? `${targets.refType} ${targets.refTarget || targets.targetObject}` : (targets.refTarget || targets.targetObject);
    lines.push(`| Targets | object \`${targets.targetObject}\` (${targets.targetPart || 'Events'}), refs \`${refLabelTarget}\`, text \`${targets.searchText || targets.refTarget || ''}\` |`);
  }
  lines.push('');
  lines.push('Reproduce:');
  lines.push('');
  lines.push('```powershell');
  lines.push(`node scripts/bench/agent-tasks-bench.js --kb "${kbPath}" --iterations ${iterations} --warmup ${warmup}`);
  lines.push('```');
  lines.push('');
  lines.push('## Results (median per task, warm session)');
  lines.push('');
  lines.push('Response bytes measure the UTF-8 bytes returned to the agent context (`content[*].text` for MCP; command/decoded output for plain approaches). Tokens are estimated at 4 bytes per token.');
  lines.push('');
  lines.push('| Task | Approach | Calls | Response bytes | Est. tokens | 1st call (ms) | Warm p50 (ms) | Warm p95 (ms) | Fidelity / Scope |');
  lines.push('|---|---|---:|---:|---:|---:|---:|---:|---|');

  const approachOrder = ['mcp', 'plain-sql', 'plain-fs'];
  for (const task of aggregated.tasks) {
    for (const key of approachOrder) {
      const stats = task.approaches[key];
      if (!stats) continue;
      const extraTransfer = typeof stats.rawTransferBytes === 'number'
        ? ` (SQL raw transfer: ${fmtInt(stats.rawTransferBytes)} B)`
        : '';
      const firstCallStr = typeof stats.firstCallMs === 'number' ? fmtMs(stats.firstCallMs) : '—';
      lines.push(
        `| ${task.label} | \`${key}\` | ${stats.calls} | ${fmtInt(stats.responseBytes)} | ~${fmtInt(stats.estTokens)} | ${firstCallStr} | ${fmtMs(stats.latencyMs.p50)} | ${fmtMs(stats.latencyMs.p95)} | ${stats.fidelity}${extraTransfer} |`,
      );
    }
  }

  lines.push('');
  lines.push('## Session startup overhead (measured once per session, outside per-task medians)');
  lines.push('');
  lines.push('| Setup step | Latency (ms) | Notes |');
  lines.push('|---|---:|---|');
  if (typeof startup.mcpColdStartMs === 'number') {
    lines.push(`| MCP Gateway spawn + \`initialize\` + \`genexus_kb open\` + index Ready | ${fmtMs(startup.mcpColdStartMs)} | Paid once per session before steady-state calls |`);
  }
  if (typeof startup.sqlConnectCheckMs === 'number') {
    lines.push(`| \`plain-sql\` read \`knowledgebase.connection\` + initial \`sqlcmd\` probe | ${fmtMs(startup.sqlConnectCheckMs)} | Paid once per session; each subsequent \`sqlcmd\` spawns a new process |`);
  }
  lines.push('');
  lines.push('## What this does not measure');
  lines.push('');
  lines.push('- Fixed session `tools/list` schema overhead (measured separately in `docs/benchmarks/2026-11-23-tools-list-size.md`).');
  lines.push('- End-to-end LLM reasoning turns, tool-selection accuracy, or provider tokenizers (tokens here are estimated as `bytes / 4`).');
  lines.push('- Write/edit, build, navigation, or pattern-instance workflows; every operation in this benchmark is strictly read-only.');
  lines.push('- Large production KBs (40,000+ objects); `C:\\KBs\\KBTeste` is a small disposable GeneXus 18 KB, so full-table blob scans in `plain-sql` scale much worse on real production KBs.');
  lines.push('- Objects without generated artifacts on disk: `plain-fs` only sees generated C#/JS under `NetModel\\Web` and misses ungenerated objects (e.g. `PCalculaDesconto`) and GeneXus-only constructs such as `For Each` (compiled away into C# cursor code).');
  lines.push('');

  return `${lines.join('\n')}`;
}

function parseCliArgs(argv) {
  const opts = {
    kbPath: 'C:\\KBs\\KBTeste',
    gatewayExe: null,
    gxPath: DEFAULT_GX_PATH,
    toolProfile: 'standard',
    iterations: 5,
    warmup: 1,
    timeoutMs: 60000,
    targetObject: 'WWTrn353',
    targetPart: 'Events',
    refTarget: 'Trn353',
    refType: 'Transaction',
    searchText: 'Trn353',
    outJson: null,
    outMd: null,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`Missing value for ${arg}`);
      i += 1;
      return argv[i];
    };
    if (arg === '--help' || arg === '-h') {
      opts.help = true;
    } else if (arg === '--kb') {
      opts.kbPath = next();
    } else if (arg === '--gateway') {
      opts.gatewayExe = next();
    } else if (arg === '--gx-path') {
      opts.gxPath = next();
    } else if (arg === '--profile') {
      opts.toolProfile = next();
    } else if (arg === '--iterations') {
      const n = Number.parseInt(next(), 10);
      if (!Number.isInteger(n) || n < 1 || n > 100) {
        throw new Error(`Invalid --iterations value: expected integer 1..100.`);
      }
      opts.iterations = n;
    } else if (arg === '--warmup') {
      const n = Number.parseInt(next(), 10);
      if (!Number.isInteger(n) || n < 0 || n > 20) {
        throw new Error(`Invalid --warmup value: expected integer 0..20.`);
      }
      opts.warmup = n;
    } else if (arg === '--timeout-ms') {
      const n = Number.parseInt(next(), 10);
      if (!Number.isInteger(n) || n < 1000) {
        throw new Error(`Invalid --timeout-ms value: expected integer >= 1000.`);
      }
      opts.timeoutMs = n;
    } else if (arg === '--target-object') {
      opts.targetObject = next();
    } else if (arg === '--target-part') {
      opts.targetPart = next();
    } else if (arg === '--ref-target') {
      opts.refTarget = next();
    } else if (arg === '--ref-type') {
      opts.refType = next();
    } else if (arg === '--search-text') {
      opts.searchText = next();
    } else if (arg === '--out-json') {
      opts.outJson = next();
    } else if (arg === '--out-md') {
      opts.outMd = next();
    } else {
      throw new Error(`Unknown flag: ${arg}`);
    }
  }
  return opts;
}

function resolveGatewayExecutable(explicitPath) {
  const candidates = [];
  if (explicitPath) candidates.push(explicitPath);
  if (process.env.GENEXUS_MCP_GATEWAY_EXE) candidates.push(process.env.GENEXUS_MCP_GATEWAY_EXE);
  const repoRoot = path.resolve(__dirname, '..', '..');
  candidates.push(path.join(repoRoot, 'publish', 'GxMcp.Gateway.exe'));

  try {
    const gitCommonDir = execFileSync('git', ['rev-parse', '--git-common-dir'], {
      cwd: repoRoot,
      encoding: 'utf8',
      timeout: 5000,
    }).trim();
    const resolvedCommon = path.resolve(repoRoot, gitCommonDir);
    candidates.push(path.join(path.dirname(resolvedCommon), 'publish', 'GxMcp.Gateway.exe'));
  } catch {
    // Not in a git worktree or git unavailable; stick to explicit/local candidates.
  }

  for (const candidate of candidates) {
    if (candidate && fs.existsSync(candidate)) {
      return path.resolve(candidate);
    }
  }
  throw new Error(
    `GxMcp.Gateway.exe not found. Checked: ${candidates.join(', ')}. Pass --gateway or run .\\build.ps1.`,
  );
}

function readGatewayMetadata(gatewayExe) {
  const manifestPath = path.join(path.dirname(gatewayExe), 'gxmcp-manifest.json');
  try {
    const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    return {
      version: manifest.version || 'unknown',
      commit: typeof manifest.sourceCommit === 'string' ? manifest.sourceCommit.slice(0, 8) : 'unknown',
    };
  } catch {
    return { version: 'unknown', commit: 'unknown' };
  }
}

function assertSafeSqlIdentifier(value, label) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_]+$/.test(value)) {
    throw new Error(`Invalid ${label} "${value}": only alphanumeric and underscore characters are allowed.`);
  }
  return value;
}

function runSqlcmd(conn, query, { rawMax = false, separator = '\t', timeoutMs = 30000 } = {}) {
  const args = ['-S', conn.serverInstance, '-d', conn.dbName];
  if (conn.integratedSecurity) {
    args.push('-E');
  }
  if (rawMax) {
    // sqlcmd on Windows forbids combining -y 0 with -h -1 or -W; parse fields directly.
    args.push('-y', '0', '-s', separator, '-Q', query);
  } else {
    args.push('-h', '-1', '-W', '-s', separator, '-Q', query);
  }
  const stdout = execFileSync('sqlcmd', args, {
    encoding: 'utf8',
    timeout: timeoutMs,
    maxBuffer: 64 * 1024 * 1024,
  });
  return stdout;
}

function runRg(args, { timeoutMs = 30000 } = {}) {
  try {
    return execFileSync('rg', args, {
      encoding: 'utf8',
      timeout: timeoutMs,
      maxBuffer: 64 * 1024 * 1024,
    });
  } catch (err) {
    // ripgrep exits 1 when there are zero matches (not an execution error).
    if (err && err.status === 1 && typeof err.stdout === 'string') {
      return err.stdout;
    }
    throw err;
  }
}

class McpStdioClient {
  constructor(gatewayExe, { gxPath, toolProfile, timeoutMs }) {
    this.gatewayExe = gatewayExe;
    this.gxPath = gxPath;
    this.toolProfile = toolProfile;
    this.timeoutMs = timeoutMs;
    this.proc = null;
    this.tempDir = null;
    this.nextId = 1;
    this.pending = new Map();
    this.buffer = '';
    this.stderrLines = [];
  }

  start() {
    this.tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'gxmcp-agent-bench-'));
    const logDir = path.join(this.tempDir, 'logs');
    fs.mkdirSync(logDir, { recursive: true });
    const workerExe = path.join(path.dirname(this.gatewayExe), 'worker', 'GxMcp.Worker.exe');
    const configPath = path.join(this.tempDir, 'config.json');
    const config = {
      ConfigSchemaVersion: 2,
      GatewayMode: 'stdio-isolated',
      GeneXus: {
        InstallationPath: this.gxPath,
        WorkerExecutable: workerExe,
      },
      Server: {
        HttpPort: 0,
        McpStdio: true,
        ToolProfile: this.toolProfile,
        WorkerSharingMode: 'isolated',
        EmitStructuredContent: false,
        TerseResponses: true,
        SessionIdleTimeoutMinutes: 10,
        WorkerIdleTimeoutMinutes: 5,
      },
      Environment: {
        ResolutionPolicy: 'strict',
      },
    };
    fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, 'utf8');

    this.proc = spawn(this.gatewayExe, [], {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      env: {
        ...process.env,
        GX_CONFIG_PATH: configPath,
        GX_MCP_STDIO: 'true',
        GXMCP_LOG_DIR: logDir,
        GENEXUS_MCP_NO_UPDATE_CHECK: '1',
      },
    });

    this.proc.stdout.setEncoding('utf8');
    this.proc.stdout.on('data', (chunk) => this._onStdout(chunk));
    this.proc.stderr.setEncoding('utf8');
    this.proc.stderr.on('data', (chunk) => {
      for (const line of chunk.split(/\r?\n/)) {
        if (line.trim()) {
          this.stderrLines.push(line);
          if (this.stderrLines.length > 40) this.stderrLines.shift();
        }
      }
    });
    this.proc.on('exit', (code, signal) => {
      const tail = this.stderrLines.slice(-8).join(' | ');
      const err = new Error(`Gateway exited unexpectedly (code=${code}, signal=${signal}): ${tail}`);
      for (const entry of this.pending.values()) {
        clearTimeout(entry.timer);
        entry.reject(err);
      }
      this.pending.clear();
    });
  }

  _onStdout(chunk) {
    this.buffer += chunk;
    let newlineIdx;
    while ((newlineIdx = this.buffer.indexOf('\n')) !== -1) {
      const rawLine = this.buffer.slice(0, newlineIdx).replace(/\r$/, '');
      this.buffer = this.buffer.slice(newlineIdx + 1);
      const trimmed = rawLine.trim();
      if (!trimmed.startsWith('{')) continue;
      let msg;
      try {
        msg = JSON.parse(trimmed);
      } catch {
        continue;
      }
      if (msg && Object.prototype.hasOwnProperty.call(msg, 'id') && this.pending.has(msg.id)) {
        const entry = this.pending.get(msg.id);
        this.pending.delete(msg.id);
        clearTimeout(entry.timer);
        entry.resolve({ envelope: msg, rawLine: trimmed });
      }
    }
  }

  request(method, params, timeoutMs = this.timeoutMs) {
    const id = this.nextId;
    this.nextId += 1;
    const payload = JSON.stringify({ jsonrpc: '2.0', id, method, params });
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`MCP request "${method}" (id=${id}) timed out after ${timeoutMs}ms.`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.proc.stdin.write(`${payload}\n`);
    });
  }

  notify(method, params) {
    const payload = JSON.stringify({ jsonrpc: '2.0', method, params });
    this.proc.stdin.write(`${payload}\n`);
  }

  async callTool(name, args, timeoutMs = this.timeoutMs) {
    const t0 = performance.now();
    const { envelope, rawLine } = await this.request('tools/call', { name, arguments: args }, timeoutMs);
    const latencyMs = performance.now() - t0;
    const parsed = parseMcpToolResult(envelope, rawLine);
    return { ...parsed, latencyMs };
  }

  async openAndWaitReady(kbPath, alias = 'kbteste') {
    const t0 = performance.now();
    await this.request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'agent-tasks-bench', version: '1.0.0' },
    }, 30000);
    this.notify('notifications/initialized', {});

    await this.callTool('genexus_kb', { action: 'open', path: kbPath, alias }, 90000);

    const deadline = Date.now() + 90000;
    while (Date.now() < deadline) {
      try {
        const probe = await this.callTool('genexus_whoami', { kb: alias }, 30000);
        const body = JSON.parse(probe.text);
        const indexStatus = body?.index?.status || body?.kb?.indexStatus || body?.status;
        if (String(indexStatus).toLowerCase() === 'ready') {
          return {
            coldStartMs: performance.now() - t0,
            sdkVersion: body?.geneXus?.version || body?.sdk?.version || null,
            kbObjectCount: typeof body?.index?.objectCount === 'number'
              ? body.index.objectCount
              : (typeof body?.kb?.objectCount === 'number' ? body.kb.objectCount : null),
          };
        }
      } catch {
        // Retry until deadline while Worker finishes opening/indexing.
      }
      await new Promise((r) => setTimeout(r, 500));
    }
    throw new Error(`Timed out waiting for KB "${alias}" index to reach Ready state.`);
  }

  async close(alias = 'kbteste') {
    if (this.proc && !this.proc.killed && this.proc.exitCode === null) {
      try {
        await this.callTool('genexus_kb', { action: 'close', kb: alias }, 5000);
      } catch {
        // Best-effort graceful close before process termination.
      }
      try {
        this.proc.stdin.end();
      } catch {
        // Ignore pipe errors during shutdown.
      }
      await new Promise((r) => setTimeout(r, 300));
      if (this.proc.exitCode === null) {
        try {
          execFileSync('taskkill', ['/PID', String(this.proc.pid), '/T', '/F'], {
            stdio: 'ignore',
            timeout: 10000,
          });
        } catch {
          try {
            this.proc.kill('SIGKILL');
          } catch {
            // Already exited.
          }
        }
      }
    }
    if (this.tempDir && fs.existsSync(this.tempDir)) {
      fs.rmSync(this.tempDir, { recursive: true, force: true });
    }
  }
}

async function measureIterations(warmup, iterations, fn) {
  let firstCallMs = null;
  for (let i = 0; i < warmup; i += 1) {
    const w = await fn();
    if (i === 0) firstCallMs = w.latencyMs;
  }
  const samples = [];
  for (let i = 0; i < iterations; i += 1) {
    const res = await fn();
    if (firstCallMs === null && i === 0) firstCallMs = res.latencyMs;
    samples.push({ ok: true, ...res });
  }
  return { firstCallMs, samples };
}

function buildSqlQueries({ targetObject, targetPart, refTarget, refType = 'Transaction' }) {
  const safeObj = assertSafeSqlIdentifier(targetObject, 'targetObject');
  const safePart = assertSafeSqlIdentifier(targetPart, 'targetPart');
  const safeRef = assertSafeSqlIdentifier(refTarget, 'refTarget');
  const safeRefType = assertSafeSqlIdentifier(refType, 'refType');

  return {
    findObject: [
      'SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;',
      'SELECT et.EntityTypeName, ev.EntityVersionName, mev.EntityId, mev.EntityVersionId',
      'FROM ModelEntityVersion mev',
      'JOIN EntityType et ON et.EntityTypeId = mev.EntityTypeId',
      'JOIN EntityVersion ev ON ev.EntityTypeId = mev.EntityTypeId AND ev.EntityId = mev.EntityId AND ev.EntityVersionId = mev.EntityVersionId',
      `WHERE mev.ModelId = 1 AND ev.EntityVersionName = '${safeObj}'`,
      "AND et.EntityTypeNamespace IN ('Objects','Attributes','Domains','PhysicalModel','DataViews','Themes');",
    ].join(' '),

    findParts: [
      'SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;',
      'SELECT c.ComponentEntityTypeId, c.ComponentEntityId, c.ComponentEntityVersionId, pet.EntityTypeName',
      'FROM ModelEntityVersion mev',
      'JOIN EntityType et ON et.EntityTypeId = mev.EntityTypeId',
      'JOIN EntityVersion ev ON ev.EntityTypeId = mev.EntityTypeId AND ev.EntityId = mev.EntityId AND ev.EntityVersionId = mev.EntityVersionId',
      'JOIN EntityVersionComposition c ON c.CompoundEntityTypeId = mev.EntityTypeId AND c.CompoundEntityId = mev.EntityId AND c.CompoundEntityVersionId = mev.EntityVersionId',
      'JOIN EntityType pet ON pet.EntityTypeId = c.ComponentEntityTypeId',
      `WHERE mev.ModelId = 1 AND ev.EntityVersionName = '${safeObj}' AND pet.EntityTypeName = '${safePart}';`,
    ].join(' '),

    readPartBlob: (typeId, entityId, versionId) => [
      'SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;',
      'SELECT CONVERT(VARCHAR(MAX), EntityVersionData, 2)',
      'FROM EntityVersion',
      `WHERE EntityTypeId = ${Number(typeId)} AND EntityId = ${Number(entityId)} AND EntityVersionId = ${Number(versionId)};`,
    ].join(' '),

    listReferences: [
      'SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;',
      'WITH Target AS (',
      '  SELECT mev.EntityTypeId, mev.EntityId',
      '  FROM ModelEntityVersion mev',
      '  JOIN EntityType et ON et.EntityTypeId = mev.EntityTypeId',
      '  JOIN EntityVersion ev ON ev.EntityTypeId = mev.EntityTypeId AND ev.EntityId = mev.EntityId AND ev.EntityVersionId = mev.EntityVersionId',
      `  WHERE mev.ModelId = 1 AND ev.EntityVersionName = '${safeRef}' AND et.EntityTypeName = '${safeRefType}' AND et.EntityTypeNamespace = 'Objects'`,
      ')',
      "SELECT 'OUT' AS Dir, et2.EntityTypeName, ev2.EntityVersionName, r.ReferenceType",
      'FROM Target t',
      'JOIN ModelCrossReference r ON r.ModelId = 1 AND r.FromEntityTypeId = t.EntityTypeId AND r.FromEntityId = t.EntityId',
      'JOIN ModelEntityVersion m2 ON m2.ModelId = 1 AND m2.EntityTypeId = r.ToEntityTypeId AND m2.EntityId = r.ToEntityId',
      'JOIN EntityType et2 ON et2.EntityTypeId = m2.EntityTypeId',
      'JOIN EntityVersion ev2 ON ev2.EntityTypeId = m2.EntityTypeId AND ev2.EntityId = m2.EntityId AND ev2.EntityVersionId = m2.EntityVersionId',
      'UNION ALL',
      "SELECT 'IN' AS Dir, et1.EntityTypeName, ev1.EntityVersionName, r.ReferenceType",
      'FROM Target t',
      'JOIN ModelCrossReference r ON r.ModelId = 1 AND r.ToEntityTypeId = t.EntityTypeId AND r.ToEntityId = t.EntityId',
      'JOIN ModelEntityVersion m1 ON m1.ModelId = 1 AND m1.EntityTypeId = r.FromEntityTypeId AND m1.EntityId = r.FromEntityId',
      'JOIN EntityType et1 ON et1.EntityTypeId = m1.EntityTypeId',
      'JOIN EntityVersion ev1 ON ev1.EntityTypeId = m1.EntityTypeId AND ev1.EntityId = m1.EntityId AND ev1.EntityVersionId = m1.EntityVersionId;',
    ].join(' '),

    dumpSourceBlobs: [
      'SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;',
      'SELECT et.EntityTypeName, ev.EntityVersionName, pet.EntityTypeName, CONVERT(VARCHAR(MAX), pev.EntityVersionData, 2)',
      'FROM ModelEntityVersion mev',
      'JOIN EntityType et ON et.EntityTypeId = mev.EntityTypeId',
      'JOIN EntityVersion ev ON ev.EntityTypeId = mev.EntityTypeId AND ev.EntityId = mev.EntityId AND ev.EntityVersionId = mev.EntityVersionId',
      'JOIN EntityVersionComposition c ON c.CompoundEntityTypeId = mev.EntityTypeId AND c.CompoundEntityId = mev.EntityId AND c.CompoundEntityVersionId = mev.EntityVersionId',
      'JOIN EntityType pet ON pet.EntityTypeId = c.ComponentEntityTypeId',
      'JOIN EntityVersion pev ON pev.EntityTypeId = c.ComponentEntityTypeId AND pev.EntityId = c.ComponentEntityId AND pev.EntityVersionId = c.ComponentEntityVersionId',
      "WHERE mev.ModelId = 1 AND pet.EntityTypeName IN ('Procedure','Events','Rules','Conditions') AND DATALENGTH(pev.EntityVersionData) > 0;",
    ].join(' '),
  };
}

async function runBenchmark(opts) {
  const kbPath = assertDisposableKbPath(opts.kbPath);
  const gatewayExe = resolveGatewayExecutable(opts.gatewayExe);
  const gwMeta = readGatewayMetadata(gatewayExe);
  const alias = 'kbteste';

  const connStart = performance.now();
  const connXml = fs.readFileSync(path.join(kbPath, 'knowledgebase.connection'), 'utf8');
  const conn = parseConnectionXml(connXml);
  runSqlcmd(conn, 'SET NOCOUNT ON; SELECT 1;', { timeoutMs: opts.timeoutMs });
  const sqlConnectCheckMs = performance.now() - connStart;

  const queries = buildSqlQueries(opts);
  const netModelWeb = path.join(kbPath, 'NetModel', 'Web');
  const generatedCsPath = path.join(netModelWeb, `${opts.targetObject.toLowerCase()}.cs`);
  if (!fs.existsSync(generatedCsPath)) {
    throw new Error(`Expected generated file for plain-fs baseline not found: ${generatedCsPath}`);
  }

  const mcpClient = new McpStdioClient(gatewayExe, {
    gxPath: opts.gxPath,
    toolProfile: opts.toolProfile,
    timeoutMs: opts.timeoutMs,
  });

  const rawRuns = [];
  let openInfo;

  try {
    mcpClient.start();
    openInfo = await mcpClient.openAndWaitReady(kbPath, alias);

    // Task 1: Find an object
    const findLabel = `Find object (\`${opts.targetObject}\`)`;
    rawRuns.push({
      task: 'find_object',
      label: findLabel,
      approach: 'mcp',
      fidelity: 'Exact KB object metadata (type, GUID, path)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const r = await mcpClient.callTool('genexus_query', {
          query: opts.targetObject,
          exactMatch: true,
          kb: alias,
        });
        return { calls: 1, latencyMs: r.latencyMs, responseBytes: r.contentBytes };
      })),
    });

    rawRuns.push({
      task: 'find_object',
      label: findLabel,
      approach: 'plain-sql',
      fidelity: 'UDM table row (requires knowing ModelId + namespace filter)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const out = runSqlcmd(conn, queries.findObject, { timeoutMs: opts.timeoutMs });
        const latencyMs = performance.now() - t0;
        const rows = parseSqlcmdRows(out);
        if (rows.length === 0) throw new Error(`plain-sql find_object returned 0 rows for ${opts.targetObject}`);
        return { calls: 1, latencyMs, responseBytes: Buffer.byteLength(out, 'utf8'), hits: rows.length };
      })),
    });

    rawRuns.push({
      task: 'find_object',
      label: findLabel,
      approach: 'plain-fs',
      fidelity: 'Generated disk files only (misses ungenerated KB objects)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const out = runRg(['--files', '--iglob', `*${opts.targetObject}*`, kbPath], { timeoutMs: opts.timeoutMs });
        const latencyMs = performance.now() - t0;
        const lines = out.split(/\r?\n/).filter(Boolean);
        return { calls: 1, latencyMs, responseBytes: Buffer.byteLength(out, 'utf8'), hits: lines.length };
      })),
    });

    // Task 2: Read source
    const readLabel = `Read source (\`${opts.targetObject}\` ${opts.targetPart})`;
    rawRuns.push({
      task: 'read_source',
      label: readLabel,
      approach: 'mcp',
      fidelity: `Native GeneXus ${opts.targetPart} source`,
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const r = await mcpClient.callTool('genexus_read', {
          name: opts.targetObject,
          part: opts.targetPart,
          kb: alias,
        });
        return { calls: 1, latencyMs: r.latencyMs, responseBytes: r.contentBytes };
      })),
    });

    rawRuns.push({
      task: 'read_source',
      label: readLabel,
      approach: 'plain-sql',
      fidelity: `Decoded GeneXus ${opts.targetPart} source (2 queries + gzip/XML decoder)`,
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const partsOut = runSqlcmd(conn, queries.findParts, { timeoutMs: opts.timeoutMs });
        const rows = parseSqlcmdRows(partsOut);
        if (rows.length === 0) throw new Error(`plain-sql read_source found no ${opts.targetPart} part for ${opts.targetObject}`);
        const [typeId, entityId, versionId] = rows[0];
        const blobOut = runSqlcmd(conn, queries.readPartBlob(typeId, entityId, versionId), {
          rawMax: true,
          timeoutMs: opts.timeoutMs,
        });
        const hexLine = parseSqlcmdRows(blobOut).map((r) => r.join('')).join('');
        const xml = decodeUdmBlobHex(hexLine);
        const decoded = extractTokenDataList(xml).text;
        const latencyMs = performance.now() - t0;
        const rawTransferBytes = Buffer.byteLength(partsOut, 'utf8') + Buffer.byteLength(blobOut, 'utf8');
        const responseBytes = Buffer.byteLength(partsOut, 'utf8') + Buffer.byteLength(decoded, 'utf8');
        return { calls: 2, latencyMs, responseBytes, rawTransferBytes };
      })),
    });

    rawRuns.push({
      task: 'read_source',
      label: readLabel,
      approach: 'plain-fs',
      fidelity: 'Generated C# only (not GeneXus source; absent if ungenerated)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const content = fs.readFileSync(generatedCsPath, 'utf8');
        const latencyMs = performance.now() - t0;
        return { calls: 1, latencyMs, responseBytes: Buffer.byteLength(content, 'utf8') };
      })),
    });

    // Task 3: List references
    const refLabel = `List references (\`${opts.refType} ${opts.refTarget}\`)`;
    rawRuns.push({
      task: 'list_references',
      label: refLabel,
      approach: 'mcp',
      fidelity: 'Semantic callers + callees graph (depth 1)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const r = await mcpClient.callTool('genexus_impact', {
          name: opts.refTarget,
          type: opts.refType,
          direction: 'both',
          maxDepth: 1,
          kb: alias,
        });
        return { calls: 1, latencyMs: r.latencyMs, responseBytes: r.contentBytes };
      })),
    });

    rawRuns.push({
      task: 'list_references',
      label: refLabel,
      approach: 'plain-sql',
      fidelity: 'Raw ModelCrossReference rows (includes internal parts/attributes)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const out = runSqlcmd(conn, queries.listReferences, { timeoutMs: opts.timeoutMs });
        const latencyMs = performance.now() - t0;
        const rows = parseSqlcmdRows(out);
        if (rows.length === 0) throw new Error(`plain-sql list_references returned 0 rows for ${opts.refTarget}`);
        return { calls: 1, latencyMs, responseBytes: Buffer.byteLength(out, 'utf8'), hits: rows.length };
      })),
    });

    rawRuns.push({
      task: 'list_references',
      label: refLabel,
      approach: 'plain-fs',
      fidelity: 'Text grep in generated *.cs only (noisy, misses metadata refs)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const out = runRg(
          ['--no-heading', '--line-number', '-i', '--glob', '*.cs', opts.refTarget, netModelWeb],
          { timeoutMs: opts.timeoutMs },
        );
        const latencyMs = performance.now() - t0;
        const lines = out.split(/\r?\n/).filter(Boolean);
        return { calls: 1, latencyMs, responseBytes: Buffer.byteLength(out, 'utf8'), hits: lines.length };
      })),
    });

    // Task 4: Search by text
    const searchLabel = `Search by text (\`${opts.searchText}\`)`;
    rawRuns.push({
      task: 'search_text',
      label: searchLabel,
      approach: 'mcp',
      fidelity: 'Indexed GeneXus source/events/rules matches with line context',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const r = await mcpClient.callTool('genexus_search_source', {
          pattern: opts.searchText,
          kb: alias,
        });
        return { calls: 1, latencyMs: r.latencyMs, responseBytes: r.contentBytes };
      })),
    });

    rawRuns.push({
      task: 'search_text',
      label: searchLabel,
      approach: 'plain-sql',
      fidelity: 'Full-table UDM blob dump + gzip/XML decode + scan (SQL LIKE cannot read blobs)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const dumpOut = runSqlcmd(conn, queries.dumpSourceBlobs, {
          rawMax: true,
          separator: '|',
          timeoutMs: opts.timeoutMs,
        });
        const needle = opts.searchText.toLowerCase();
        const matchedLines = [];
        for (const row of parseSqlcmdRows(dumpOut, '|')) {
          if (row.length < 4) continue;
          const [objType, objName, partType, hex] = row;
          if (!hex) continue;
          const xml = decodeUdmBlobHex(hex);
          const source = extractTokenDataList(xml).text;
          const srcLines = source.split(/\r?\n/);
          for (let idx = 0; idx < srcLines.length; idx += 1) {
            if (srcLines[idx].toLowerCase().includes(needle)) {
              matchedLines.push(`${objType}/${objName}.${partType}:${idx + 1}: ${srcLines[idx].trim()}`);
            }
          }
        }
        const latencyMs = performance.now() - t0;
        const formatted = matchedLines.join('\n');
        return {
          calls: 1,
          latencyMs,
          responseBytes: Buffer.byteLength(formatted, 'utf8'),
          rawTransferBytes: Buffer.byteLength(dumpOut, 'utf8'),
          hits: matchedLines.length,
        };
      })),
    });

    rawRuns.push({
      task: 'search_text',
      label: searchLabel,
      approach: 'plain-fs',
      fidelity: 'ripgrep over generated *.cs only (misses GeneXus syntax and ungenerated objects)',
      ...(await measureIterations(opts.warmup, opts.iterations, async () => {
        const t0 = performance.now();
        const out = runRg(
          ['--no-heading', '--line-number', '-i', '--glob', '*.cs', opts.searchText, netModelWeb],
          { timeoutMs: opts.timeoutMs },
        );
        const latencyMs = performance.now() - t0;
        const lines = out.split(/\r?\n/).filter(Boolean);
        return { calls: 1, latencyMs, responseBytes: Buffer.byteLength(out, 'utf8'), hits: lines.length };
      })),
    });
  } finally {
    await mcpClient.close(alias);
  }

  const now = new Date();
  const date = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  const aggregated = aggregateBenchmark(rawRuns);
  return {
    schemaVersion: 'gxmcp-agent-tasks-bench/1',
    date,
    kbPath,
    iterations: opts.iterations,
    warmup: opts.warmup,
    gatewayVersion: gwMeta.version,
    gatewayCommit: gwMeta.commit,
    sdkVersion: openInfo.sdkVersion,
    kbObjectCount: openInfo.kbObjectCount,
    toolProfile: opts.toolProfile,
    targets: {
      targetObject: opts.targetObject,
      targetPart: opts.targetPart,
      refTarget: opts.refTarget,
      refType: opts.refType,
      searchText: opts.searchText,
    },
    startup: {
      mcpColdStartMs: round2(openInfo.coldStartMs),
      sqlConnectCheckMs: round2(sqlConnectCheckMs),
    },
    aggregated,
    rawRuns,
  };
}

async function main() {
  const opts = parseCliArgs(process.argv.slice(2));
  if (opts.help) {
    console.log(
      'Usage: node scripts/bench/agent-tasks-bench.js [--kb C:\\KBs\\KBTeste] [--gateway <exe>] [--iterations 5] [--warmup 1] [--out-json <file>] [--out-md <file>]',
    );
    return;
  }

  const report = await runBenchmark(opts);
  const markdown = formatMarkdownReport(report);

  if (opts.outJson) {
    fs.mkdirSync(path.dirname(path.resolve(opts.outJson)), { recursive: true });
    fs.writeFileSync(opts.outJson, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  }
  if (opts.outMd) {
    fs.mkdirSync(path.dirname(path.resolve(opts.outMd)), { recursive: true });
    fs.writeFileSync(opts.outMd, markdown, 'utf8');
  }

  console.log('task\tapproach\tcalls\tbytes\test_tokens\tfirst_ms\tp50_ms\tp95_ms');
  for (const task of report.aggregated.tasks) {
    for (const key of ['mcp', 'plain-sql', 'plain-fs']) {
      const s = task.approaches[key];
      if (!s) continue;
      console.log([task.task, key, s.calls, s.responseBytes, s.estTokens, s.firstCallMs ?? '', s.latencyMs.p50, s.latencyMs.p95].join('\t'));
    }
  }
}

if (require.main === module) {
  main().catch((err) => {
    console.error(err && err.stack ? err.stack : String(err));
    process.exit(1);
  });
}

module.exports = {
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
  resolveGatewayExecutable,
  buildSqlQueries,
};
