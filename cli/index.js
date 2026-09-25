const { spawn } = require('child_process');
const {
    getGatewayExePath,
    applyLauncherConfigOrExit,
    isPathLikelyAppLockerBlocked
} = require('./lib/config');
const {
    SUPPORTED_FORMATS,
    writeStructured,
    renderOutput,
    formatToonObject
} = require('./lib/output');
const {
    handleStatus,
    handleDoctor,
    handleToolsList,
    handleConfigShow,
    handleConfigCreate,
    handleConfigMigrate,
    handleInit,
    handleWhoami,
    handleUninstall,
    handleKb,
    handleClients,
    handleHome,
    handleLlmHelp,
    handleLayout,
    handleHelp,
    handleVersion,
    usageEnvelope,
    commandHelpMap
} = require('./commands/axi');
const { startBackgroundUpdateCheck, handleUpdate } = require('./lib/update-check');
const { createStderrTail, writeLastStdioError } = require('./lib/stdio-diagnostics');

const EXIT_CODES = {
    OK: 0,
    ERROR: 1,
    USAGE: 2
};

const GLOBAL_DEFAULTS = {
    format: 'toon',
    full: false,
    fields: null,
    interactive: false,
    writeClients: false,
    clients: null,
    allClients: false,
    serverName: null,
    force: false,
    mcpSmoke: false,
    dump: false,
    noSmoke: false,
    warm: false,
    yes: false,
    apply: false,
    channel: null,
    name: null,
    limit: 100,
    query: null,
    quiet: false,
    noColor: false,
    help: false
};

// Single source of truth for command routing: cli/run.js imports both sets so the
// AXI-vs-passthrough decision (stdout vs stderr for unhandled errors) cannot drift
// from the parser again — issue #207 was caused by two hand-synced copies.
const KNOWN_COMMANDS = new Set(['status', 'doctor', 'tools', 'config', 'init', 'setup', 'whoami', 'uninstall', 'kb', 'clients', 'help', 'home', 'axi', 'llm', 'layout', 'update', 'version']);

// Version query aliases. `-v` deliberately is NOT an alias of `--help`/`-h`: those
// return immediately, while the version aliases are command tokens so that remaining
// flags (`-v --format json`) are still parsed and honored (issue #207).
const VERSION_ALIASES = new Set(['version', '-v', '--version']);

function isKnownCommandToken(token) {
    return KNOWN_COMMANDS.has(token) || VERSION_ALIASES.has(token);
}

function parseArgs(argv) {
    const result = {
        command: null,
        subcommand: null,
        options: { ...GLOBAL_DEFAULTS },
        passthroughArgs: [...argv],
        unknownFlags: [],
        positional: []
    };

    const tokens = [...argv];
    if (tokens.length === 0) return result;

    const first = tokens[0];
    const versionIntent = VERSION_ALIASES.has(first);
    if (!versionIntent && !KNOWN_COMMANDS.has(first) && !first.startsWith('--')) {
        return result;
    }

    if (first === '--help' || first === '-h') {
        result.command = 'help';
        result.options.help = true;
        return result;
    }

    if (versionIntent) {
        // Treat the alias as a consumed command token and keep parsing the remaining
        // flags, so `version --format json`, `-v --format json` and `--version --format json`
        // all reach the format validation instead of falling through to passthrough.
        result.command = 'version';
        tokens.shift();
    } else if (KNOWN_COMMANDS.has(first)) {
        result.command = first === 'setup' ? 'init' : first;
        tokens.shift();
    }

    if (result.command === 'tools' && tokens[0] === 'list') {
        result.subcommand = 'list';
        tokens.shift();
    }

    if (result.command === 'config' && ['show', 'create', 'migrate'].includes(tokens[0])) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    if (result.command === 'axi' && tokens[0] === 'home') {
        result.subcommand = 'home';
        tokens.shift();
    }

    if (result.command === 'home') {
        result.subcommand = 'home';
    }

    if (result.command === 'llm' && tokens[0] === 'help') {
        result.subcommand = 'help';
        tokens.shift();
    }

    if (result.command === 'kb' && ['list', 'add', 'remove', 'switch'].includes(tokens[0])) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    if (result.command === 'clients' && ['list', 'add', 'remove'].includes(tokens[0])) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    if (result.command === 'layout' && (tokens[0] === 'status' || tokens[0] === 'run' || tokens[0] === 'inspect')) {
        result.subcommand = tokens[0];
        tokens.shift();
    }

    for (let i = 0; i < tokens.length; i += 1) {
        const token = tokens[i];

        if (!token.startsWith('--')) {
            result.positional.push(token);
            continue;
        }

        const [rawKey, inlineValue] = token.split('=', 2);
        const key = rawKey.slice(2);
        const next = tokens[i + 1];

        const takeValue = () => {
            if (inlineValue !== undefined) return inlineValue;
            if (!next || next.startsWith('--')) return null;
            i += 1;
            return next;
        };

        switch (key) {
            case 'format': {
                const val = takeValue();
                if (val) result.options.format = val;
                else result.unknownFlags.push('--format requires a value');
                break;
            }
            case 'fields': {
                const val = takeValue();
                if (val) result.options.fields = val;
                else result.unknownFlags.push('--fields requires a value');
                break;
            }
            case 'kb': {
                const val = takeValue();
                if (val) result.options.kb = val;
                else result.unknownFlags.push('--kb requires a value');
                break;
            }
            case 'gx': {
                const val = takeValue();
                if (val) result.options.gx = val;
                else result.unknownFlags.push('--gx requires a value');
                break;
            }
            case 'from': {
                const val = takeValue();
                if (val) result.options.fromPath = val;
                else result.unknownFlags.push('--from requires a value');
                break;
            }
            case 'reject-non-migratable':
                result.options.rejectNonMigratable = true;
                break;
            case 'output': {
                const val = takeValue();
                if (val) result.options.output = val;
                else result.unknownFlags.push('--output requires a value');
                break;
            }
            case 'worker': {
                const val = takeValue();
                if (val) result.options.worker = val;
                else result.unknownFlags.push('--worker requires a value');
                break;
            }
            case 'config-scope': {
                const val = takeValue();
                if (val) result.options.configScope = val;
                else result.unknownFlags.push('--config-scope requires a value');
                break;
            }
            case 'gateway-mode': {
                const val = takeValue();
                if (val) result.options.gatewayMode = val;
                else result.unknownFlags.push('--gateway-mode requires a value');
                break;
            }
            case 'resolution-policy': {
                const val = takeValue();
                if (val) result.options.resolutionPolicy = val;
                else result.unknownFlags.push('--resolution-policy requires a value');
                break;
            }
            case 'name': {
                const val = takeValue();
                if (val) result.options.name = val;
                else result.unknownFlags.push('--name requires a value');
                break;
            }
            case 'limit': {
                const val = takeValue();
                if (!val) {
                    result.unknownFlags.push('--limit requires a value');
                    break;
                }
                const parsed = Number.parseInt(val, 10);
                if (!Number.isFinite(parsed) || parsed <= 0) {
                    result.unknownFlags.push('--limit must be a positive integer');
                    break;
                }
                result.options.limit = parsed;
                break;
            }
            case 'query': {
                const val = takeValue();
                if (val) result.options.query = val;
                else result.unknownFlags.push('--query requires a value');
                break;
            }
            case 'clients': {
                const val = takeValue();
                if (val) result.options.clients = val;
                else result.unknownFlags.push('--clients requires a value');
                break;
            }
            case 'all-clients':
                result.options.allClients = true;
                break;
            case 'server-name': {
                const val = takeValue();
                if (val) {
                    if (!/^[a-zA-Z0-9_-]+$/.test(val)) {
                        result.unknownFlags.push(`--server-name must be alphanumeric (letters, digits, _, -), got: "${val}"`);
                    } else {
                        result.options.serverName = val;
                    }
                } else {
                    result.unknownFlags.push('--server-name requires a value');
                }
                break;
            }
            case 'force':
                result.options.force = true;
                break;
            case 'action': {
                const val = takeValue();
                if (val) result.options.action = val;
                else result.unknownFlags.push('--action requires a value');
                break;
            }
            case 'title': {
                const val = takeValue();
                if (val) result.options.title = val;
                else result.unknownFlags.push('--title requires a value');
                break;
            }
            case 'tab': {
                const val = takeValue();
                if (val) result.options.tab = val;
                else result.unknownFlags.push('--tab requires a value');
                break;
            }
            case 'keys': {
                const val = takeValue();
                if (val) result.options.keys = val;
                else result.unknownFlags.push('--keys requires a value');
                break;
            }
            case 'text': {
                const val = takeValue();
                if (val) result.options.text = val;
                else result.unknownFlags.push('--text requires a value');
                break;
            }
            case 'x': {
                const val = takeValue();
                if (!val) {
                    result.unknownFlags.push('--x requires a value');
                    break;
                }
                const parsed = Number.parseInt(val, 10);
                if (!Number.isFinite(parsed)) {
                    result.unknownFlags.push('--x must be an integer');
                    break;
                }
                result.options.x = parsed;
                break;
            }
            case 'y': {
                const val = takeValue();
                if (!val) {
                    result.unknownFlags.push('--y requires a value');
                    break;
                }
                const parsed = Number.parseInt(val, 10);
                if (!Number.isFinite(parsed)) {
                    result.unknownFlags.push('--y must be an integer');
                    break;
                }
                result.options.y = parsed;
                break;
            }
            case 'full':
                result.options.full = true;
                break;
            case 'interactive':
                result.options.interactive = true;
                break;
            case 'global-config':
                result.options.globalConfig = true;
                break;
            case 'neutral':
                result.options.neutral = true;
                break;
            case 'write-clients':
                result.options.writeClients = true;
                break;
            // issue #32 item 3: init now patches detected clients by default; this opts out.
            case 'no-write-clients':
                result.options.noWriteClients = true;
                break;
            case 'mcp-smoke':
                result.options.mcpSmoke = true;
                break;
            case 'dump':
                result.options.dump = true;
                break;
            case 'no-smoke':
                result.options.noSmoke = true;
                break;
            case 'warm':
                result.options.warm = true;
                break;
            case 'yes':
                result.options.yes = true;
                break;
            case 'apply':
                result.options.apply = true;
                break;
            case 'channel': {
                const val = takeValue();
                if (val) result.options.channel = val;
                else result.unknownFlags.push('--channel requires a value');
                break;
            }
            case 'quiet':
                result.options.quiet = true;
                break;
            case 'no-color':
                result.options.noColor = true;
                break;
            case 'help':
                result.options.help = true;
                break;
            default:
                result.unknownFlags.push(`Unknown flag: --${key}`);
                break;
        }
    }

    return result;
}

function writeAppLockerHint(stderr, gatewayExePath) {
    const riskyZone = isPathLikelyAppLockerBlocked(gatewayExePath);
    stderr.write('[genexus-mcp] Likely cause: Windows AppLocker / SRP is blocking execution from this path.\n');
    if (riskyZone) {
        stderr.write(`[genexus-mcp] The gateway exe lives under %${riskyZone}% (typically restricted by corporate policy):\n[genexus-mcp]   ${gatewayExePath}\n`);
    }
    stderr.write('[genexus-mcp] Remediation: install to a whitelisted path with:\n');
    stderr.write('[genexus-mcp]   iex (irm https://raw.githubusercontent.com/lennix1337/Genexus18MCP/main/scripts/install.ps1)\n');
    stderr.write('[genexus-mcp] Or copy the package `publish/` folder to a path outside %APPDATA%/%LOCALAPPDATA% and point the MCP client at that exe.\n');
}

async function launchGateway(passthroughArgs, options) {
    const stderrTail = createStderrTail();
    const launcherStderr = {
        write(chunk) {
            stderrTail.append(chunk);
            if (!options.quiet) process.stderr.write(chunk);
            return true;
        }
    };
    const recordStdioFailure = ({ gatewayExePath, exitCode = null, signal = null, error = null } = {}) => {
        const logPath = writeLastStdioError({
            gatewayExePath,
            exitCode,
            signal,
            error,
            stderrTail: stderrTail.toString()
        });
        if (logPath && !options.quiet) {
            process.stderr.write(`[genexus-mcp] Last stdio error saved to ${logPath}\n`);
        }
    };

    const setup = applyLauncherConfigOrExit({
        cwd: process.cwd(),
        stderr: launcherStderr,
        quiet: options.quiet
    });

    if (!setup.ok) {
        recordStdioFailure({ gatewayExePath: getGatewayExePath(), exitCode: EXIT_CODES.ERROR, error: 'Launcher setup failed.' });
        return EXIT_CODES.ERROR;
    }

    let gatewayExePath = getGatewayExePath();
    try {
        const { ensureStagedGateway } = require('./lib/runtime-stager');
        const staged = ensureStagedGateway();
        gatewayExePath = staged.gatewayExePath;
        const cleanup = staged.cleanup;
        if (cleanup && (cleanup.skipped?.length || cleanup.failed?.length || cleanup.processProbeAvailable === false)) {
            const summary = [
                cleanup.processProbeAvailable === false ? 'process probe unavailable' : null,
                cleanup.skipped?.length ? `${cleanup.skipped.length} skipped` : null,
                cleanup.failed?.length ? `${cleanup.failed.length} failed` : null
            ].filter(Boolean).join(', ');
            if (!options.quiet) launcherStderr.write(`[genexus-mcp] Runtime cleanup incomplete: ${summary}.\n`);
        }
    } catch (stageErr) {
        if (!options.quiet) {
            launcherStderr.write(`[genexus-mcp] Warning: Runtime staging failed (${stageErr.message}), falling back to direct binary.\n`);
        }
    }
    if (!require('fs').existsSync(gatewayExePath)) {
        const message = `[genexus-mcp] ERROR: Gateway executable not found at ${gatewayExePath}`;
        launcherStderr.write(`${message}\n`);
        recordStdioFailure({ gatewayExePath, exitCode: EXIT_CODES.ERROR, error: 'Gateway executable not found.' });
        return EXIT_CODES.ERROR;
    }

    return await new Promise((resolve) => {
        let settled = false;
        const finish = (code) => {
            if (settled) return;
            settled = true;
            resolve(code);
        };
        const handleSpawnError = (err) => {
            const message = `[genexus-mcp] ERROR: Failed to start gateway process: ${err.message}`;
            launcherStderr.write(`${message}\n`);
            const code = err && (err.code || err.errno);
            const accessDenied = code === 'EACCES' || code === 'EPERM' || /access is denied|access denied|acesso negado/i.test(err.message || '');
            if (accessDenied) writeAppLockerHint(launcherStderr, gatewayExePath);
            recordStdioFailure({ gatewayExePath, error: err.message || 'Failed to start gateway process.' });
            finish(EXIT_CODES.ERROR);
        };

        let child;
        try {
            child = spawn(gatewayExePath, passthroughArgs, {
                // Keep stdout attached to the MCP protocol and tee stderr so a
                // failed bootstrap remains available after a client drops it.
                stdio: ['inherit', 'inherit', 'pipe'],
                env: process.env,
                windowsHide: true
            });
        } catch (err) {
            handleSpawnError(err);
            return;
        }

        if (child.stderr) {
            child.stderr.on('data', (chunk) => {
                stderrTail.append(chunk);
                process.stderr.write(chunk);
            });
        }

        child.once('error', handleSpawnError);

        child.once('close', (code, signal) => {
            if (signal || code !== 0) {
                recordStdioFailure({ gatewayExePath, exitCode: code, signal });
            }
            finish(signal ? EXIT_CODES.ERROR : (code === null || code === undefined ? EXIT_CODES.ERROR : code));
        });
    });
}

function commandFromHelpIntent(parsed) {
    if (!parsed.options.help) return null;
    if (parsed.command === 'axi' && parsed.subcommand === 'home') return 'home';
    if (parsed.command && parsed.command !== 'help') return parsed.command;
    if (parsed.positional.length > 0) {
        const candidate = parsed.positional[0];
        if (commandHelpMap()[candidate]) return candidate;
    }
    return null;
}

function withCommandMeta(envelope, commandName) {
    const safe = envelope && typeof envelope === 'object' ? envelope : {};
    const meta = safe.meta && typeof safe.meta === 'object' ? safe.meta : {};
    return {
        ...safe,
        meta: {
            command: commandName,
            ...meta
        }
    };
}

function resolveMetaCommand(parsed, targetHelp) {
    if (targetHelp || parsed.command === 'help') return 'help';
    if (parsed.command === 'tools') return 'tools.list';
    if (parsed.command === 'config') return parsed.subcommand ? `config.${parsed.subcommand}` : 'config';
    if (parsed.command === 'axi' || parsed.command === 'home') return 'home';
    if (parsed.command === 'llm') return 'llm.help';
    if (parsed.command === 'layout') {
        if (parsed.subcommand === 'run') return 'layout.run';
        if (parsed.subcommand === 'inspect') return 'layout.inspect';
        return 'layout.status';
    }
    if (parsed.command === 'kb') {
        return parsed.subcommand ? `kb.${parsed.subcommand}` : 'kb';
    }
    if (parsed.command === 'clients') {
        return parsed.subcommand ? `clients.${parsed.subcommand}` : 'clients.list';
    }
    if (parsed.command === 'update') return 'update';
    return parsed.command || 'unknown';
}

async function main(argv) {
    const parsed = parseArgs(argv);

    // `version` is a quiet query: no update-check banner (it would corrupt the raw
    // version string scripts read) and no launcher config side effects.
    if (parsed.command !== 'update' && parsed.command !== 'version') {
        startBackgroundUpdateCheck({ quiet: parsed.options.quiet });
    }

    if (!parsed.command) {
        return launchGateway(argv, parsed.options);
    }

    if (parsed.unknownFlags.length > 0) {
        const envelope = usageEnvelope(parsed.unknownFlags.join('; '), EXIT_CODES.USAGE);
        writeStructured(process.stdout, withCommandMeta(envelope, parsed.command || 'usage'), parsed.options.format);
        return EXIT_CODES.USAGE;
    }

    if (!SUPPORTED_FORMATS.has(parsed.options.format)) {
        const envelope = usageEnvelope(`Invalid --format '${parsed.options.format}'. Use toon|json|text.`, EXIT_CODES.USAGE);
        writeStructured(process.stdout, withCommandMeta(envelope, parsed.command || 'usage'), 'toon');
        return EXIT_CODES.USAGE;
    }

    const ctx = {
        cwd: process.cwd(),
        stdout: process.stdout,
        stderr: process.stderr,
        EXIT_CODES
    };

    const targetHelp = commandFromHelpIntent(parsed);
    if (targetHelp || parsed.command === 'help') {
        const helpResult = await handleHelp(targetHelp, ctx);
        writeStructured(process.stdout, withCommandMeta(helpResult.envelope, resolveMetaCommand(parsed, targetHelp)), parsed.options.format);
        return helpResult.exitCode;
    }

    if (parsed.command === 'version') {
        const versionResult = await handleVersion(parsed.options, ctx);
        // Default formats print the bare version so `genexus-mcp --version` is usable
        // from scripts/CI; only --format json opts into the axi-cli/1 envelope.
        if (versionResult.exitCode === EXIT_CODES.OK
            && (parsed.options.format === 'toon' || parsed.options.format === 'text')) {
            process.stdout.write(`${versionResult.envelope.ok.version}\n`);
        } else {
            writeStructured(process.stdout, withCommandMeta(versionResult.envelope, 'version'), parsed.options.format);
        }
        return versionResult.exitCode;
    }

    let result;

    switch (parsed.command) {
        case 'status':
            result = await handleStatus(parsed.options, ctx);
            break;
        case 'doctor':
            result = await handleDoctor(parsed.options, ctx);
            break;
        case 'home':
            result = await handleHome(parsed.options, ctx);
            break;
        case 'axi':
            if (parsed.subcommand && parsed.subcommand !== 'home') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('axi supports only subcommand `home`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleHome(parsed.options, ctx);
            break;
        case 'tools':
            if (parsed.subcommand !== 'list') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('tools requires subcommand `list`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleToolsList(parsed.options, ctx);
            break;
        case 'config':
            if (!['show', 'create', 'migrate'].includes(parsed.subcommand)) {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('config requires subcommand `show`, `create`, or `migrate`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = parsed.subcommand === 'create'
                ? await handleConfigCreate(parsed.options, ctx)
                : parsed.subcommand === 'migrate'
                    ? await handleConfigMigrate(parsed.options, ctx)
                : await handleConfigShow(parsed.options, ctx);
            break;
        case 'llm':
            if (parsed.subcommand && parsed.subcommand !== 'help') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('llm supports only subcommand `help`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleLlmHelp(parsed.options, ctx);
            break;
        case 'layout':
            if (parsed.subcommand !== 'status' && parsed.subcommand !== 'run' && parsed.subcommand !== 'inspect') {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('layout requires subcommand `status`, `run`, or `inspect`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleLayout(parsed.subcommand, parsed.options, ctx);
            break;
        case 'init':
            result = await handleInit(parsed.options, ctx);
            break;
        case 'whoami':
            result = await handleWhoami(parsed.options, ctx);
            break;
        case 'uninstall':
            result = await handleUninstall(parsed.options, ctx);
            break;
        case 'kb':
            if (!parsed.subcommand || !['list', 'add', 'remove', 'switch'].includes(parsed.subcommand)) {
                writeStructured(
                    process.stdout,
                    withCommandMeta(usageEnvelope('kb requires subcommand `list`, `add`, `remove`, or `switch`.', EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                    parsed.options.format
                );
                return EXIT_CODES.USAGE;
            }
            result = await handleKb(parsed.subcommand, parsed.options, ctx);
            break;
        case 'clients':
            result = await handleClients(parsed.subcommand, parsed.options, ctx);
            break;
        case 'update':
            result = await handleUpdate(parsed.options, ctx);
            break;
        default:
            writeStructured(
                process.stdout,
                withCommandMeta(usageEnvelope(`Unsupported command '${parsed.command}'.`, EXIT_CODES.USAGE), resolveMetaCommand(parsed)),
                parsed.options.format
            );
            return EXIT_CODES.USAGE;
    }

    writeStructured(process.stdout, withCommandMeta(result.envelope, resolveMetaCommand(parsed)), parsed.options.format);
    return result.exitCode;
}

module.exports = {
    main,
    parseArgs,
    EXIT_CODES,
    KNOWN_COMMANDS,
    VERSION_ALIASES,
    isKnownCommandToken,
    renderOutput,
    formatToonObject
};
