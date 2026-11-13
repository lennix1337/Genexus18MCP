'use strict';

// Opt-in launcher phase timing. With GENEXUS_MCP_TRACE_STARTUP=1 the launcher
// records `performance.now()` marks (ms since the Node process started) and
// prints them as one JSON line on stderr once the Gateway is spawned. Stdout is
// never touched: in passthrough mode it belongs to the MCP protocol stream.
// Without the env var every call is a cheap no-op.
const enabled = process.env.GENEXUS_MCP_TRACE_STARTUP === '1';
const marks = [];

function mark(name) {
    // Required only when tracing, so the default path loads nothing extra.
    if (enabled) marks.push([name, require('node:perf_hooks').performance.now()]);
}

function flush(stream = process.stderr) {
    if (!enabled || marks.length === 0) return;
    const phases = {};
    for (const [name, at] of marks) phases[name] = Math.round(at * 10) / 10;
    marks.length = 0;
    stream.write(`[genexus-mcp] startup-timing ${JSON.stringify(phases)}\n`);
}

module.exports = { mark, flush };
