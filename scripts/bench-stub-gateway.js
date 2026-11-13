'use strict';

// Preload (NODE_OPTIONS=--require) that turns a copy of node.exe named
// GxMcp.Gateway.exe into a stub MCP Gateway for scripts/bench-launcher-startup.js.
// It does nothing in any other process. The stub reports its own start and the
// first `initialize` on stderr with epoch milliseconds, and answers `initialize`
// on stdout like the real Gateway would.
const path = require('path');

if (path.basename(process.execPath).toLowerCase() === 'gxmcp.gateway.exe') {
    const input = process.stdin;
    // Node would otherwise read stdin as a script to evaluate: hand it a stream that never produces data.
    Object.defineProperty(process, 'stdin', { value: new (require('stream').PassThrough)(), configurable: true });
    process.stderr.write(`STUB_UP ${Date.now()}\n`);
    let buffered = '';
    input.setEncoding('utf8');
    input.on('data', (chunk) => {
        buffered += chunk;
        let newline;
        while ((newline = buffered.indexOf('\n')) >= 0) {
            const line = buffered.slice(0, newline).trim();
            buffered = buffered.slice(newline + 1);
            if (!line) continue;
            let message;
            try { message = JSON.parse(line); } catch { continue; }
            if (message.method === 'initialize') {
                process.stderr.write(`STUB_INITIALIZE ${Date.now()}\n`);
                process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { protocolVersion: '2024-11-05', capabilities: {}, serverInfo: { name: 'bench-stub', version: '0' } } })}\n`);
            }
        }
    });
    input.on('end', () => process.exit(0));
}
