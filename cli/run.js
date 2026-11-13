#!/usr/bin/env node
const startupTiming = require('./lib/startup-timing');
startupTiming.mark('run.js:start');
const { main, EXIT_CODES, isKnownCommandToken } = require('./index');
const { writeStructured } = require('./lib/output');
const { writeLastStdioError } = require('./lib/stdio-diagnostics');

// Detect gateway-passthrough mode: no subcommand means we're forwarding stdio
// as the MCP JSON-RPC channel. Writing a JSON envelope to stdout there would
// corrupt the protocol stream. Route errors to stderr instead.
startupTiming.mark('run.js:modules-loaded');
const argv = process.argv.slice(2);
// Issue #207: share the command table with the parser instead of keeping a second
// hand-synced copy here.
const isInteractiveCommand = argv.length > 0 && isKnownCommandToken(argv[0]);
const errorOut = isInteractiveCommand ? process.stdout : process.stderr;

main(argv)
    .then((code) => process.exit(code))
    .catch((err) => {
        if (!isInteractiveCommand) {
            writeLastStdioError({
                gatewayExePath: process.env.GENEXUS_MCP_GATEWAY_EXE || process.argv[1],
                exitCode: EXIT_CODES.ERROR,
                error: err && err.message ? err.message : 'Unhandled CLI failure.',
                stderrTail: err && err.stack ? err.stack : ''
            });
        }
        const { operationalErrorEnvelope } = require('./commands/axi');
        const envelope = operationalErrorEnvelope('Unhandled CLI failure.', EXIT_CODES.ERROR);
        envelope.meta = { ...(envelope.meta || {}), command: 'runtime' };
        writeStructured(errorOut, envelope, 'toon');
        process.exit(EXIT_CODES.ERROR);
    });
