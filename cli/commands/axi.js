const { spawn } = require('child_process');
const path = require('path');
const readline = require('readline');
const fs = require('fs');
const {
    getGatewayExePath,
    getToolDefinitionsPath,
    resolveConfigPathNoMutate,
    readJsonFileSafe,
    directoryLooksLikeKnowledgeBase,
    createConfigFile,
    patchClientConfig,
    discoverGeneXusInstallation
} = require('../lib/config');

function parseFieldSelection(raw) {
    if (!raw) return null;
    return raw.split(',').map((v) => v.trim()).filter(Boolean);
}

function sanitizeOperationalMessage(message, fallback = 'Operation failed.') {
    const raw = typeof message === 'string' ? message : '';
    const singleLine = raw.replace(/\r?\n/g, ' ').trim();
    if (!singleLine) return fallback;
    if (singleLine.length > 220) return `${singleLine.slice(0, 217)}...`;
    return singleLine;
}

function validateFieldSelection(raw, allowed, commandName, ctx) {
    const selected = parseFieldSelection(raw);

    if (!raw) {
        return { selectedFields: null };
    }

    if (!selected || selected.length === 0) {
        return {
            errorResult: {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope(`--fields for ${commandName} cannot be empty. Allowed: ${allowed.join(', ')}.`, ctx.EXIT_CODES.USAGE)
            }
        };
    }

    const invalid = selected.filter((field) => !allowed.includes(field));
    if (invalid.length > 0) {
        return {
            errorResult: {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope(
                    `Invalid --fields for ${commandName}: ${invalid.join(', ')}. Allowed: ${allowed.join(', ')}.`,
                    ctx.EXIT_CODES.USAGE
                )
            }
        };
    }

    return { selectedFields: selected };
}

function pickFields(obj, selectedFields) {
    if (!selectedFields || selectedFields.length === 0) return obj;
    const out = {};
    for (const f of selectedFields) {
        if (Object.prototype.hasOwnProperty.call(obj, f)) {
            out[f] = obj[f];
        }
    }
    return out;
}

function resolveToolCategory(name) {
    const n = (name || '').toLowerCase();
    if (n.includes('read') || n.includes('list') || n.includes('query') || n.includes('inspect')) return 'read';
    if (n.includes('edit') || n.includes('write') || n.includes('create') || n.includes('refactor') || n.includes('add_variable')) return 'write';
    if (n.includes('analyze') || n.includes('summarize') || n.includes('explain') || n.includes('doc')) return 'analysis';
    if (n.includes('lifecycle') || n.includes('test') || n.includes('format') || n.includes('build')) return 'lifecycle';
    return 'other';
}

function usageEnvelope(message, exitCode) {
    return {
        error: { code: 'usage_error', message },
        help: [
            'Run `genexus-mcp help` for command reference.',
            'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` for non-interactive setup.'
        ],
        meta: { exitCode }
    };
}

function operationalErrorEnvelope(message, exitCode, help = []) {
    return {
        error: { code: 'operation_error', message: sanitizeOperationalMessage(message) },
        help,
        meta: { exitCode }
    };
}

function buildStatusData(cwd) {
    const configPath = resolveConfigPathNoMutate(cwd);
    const gatewayExePath = getGatewayExePath();
    const gatewayExeFound = fs.existsSync(gatewayExePath);
    const configFound = !!configPath;

    let kbLooksValid = false;
    let kbPath = null;
    let gxPath = null;
    let configSource = null;

    if (process.env.GX_CONFIG_PATH && fs.existsSync(process.env.GX_CONFIG_PATH)) {
        configSource = 'env';
    } else if (configPath) {
        configSource = 'cwd';
    }

    if (configPath) {
        const cfg = readJsonFileSafe(configPath);
        if (cfg) {
            kbPath = cfg.Environment && cfg.Environment.KBPath ? cfg.Environment.KBPath : null;
            gxPath = cfg.GeneXus && cfg.GeneXus.InstallationPath ? cfg.GeneXus.InstallationPath : null;
            if (kbPath) kbLooksValid = directoryLooksLikeKnowledgeBase(kbPath);
        }
    }

    const ready = configFound && gatewayExeFound;
    return { ready, configFound, gatewayExeFound, kbLooksValid, configPath, gatewayExePath, kbPath, gxPath, configSource };
}

async function probeGatewaySpawn(gatewayExePath) {
    if (!fs.existsSync(gatewayExePath)) {
        return { status: 'fail', detail: 'Gateway executable does not exist for spawn probe.' };
    }

    return await new Promise((resolve) => {
        let done = false;
        const finish = (result) => {
            if (done) return;
            done = true;
            resolve(result);
        };

        try {
            const child = spawn(gatewayExePath, ['--axi-spawn-probe'], {
                stdio: 'ignore',
                windowsHide: true,
                env: process.env
            });

            child.once('error', (err) => {
                finish({ status: 'fail', detail: `Spawn probe failed: ${err.message}` });
            });

            child.once('spawn', () => {
                setTimeout(() => {
                    try {
                        child.kill();
                    } catch {
                    }
                    finish({ status: 'pass', detail: 'Gateway process can be spawned (probe launched and terminated).' });
                }, 180);
            });

            setTimeout(() => {
                if (!done) {
                    try {
                        child.kill();
                    } catch {
                    }
                    finish({ status: 'warn', detail: 'Spawn probe timed out; process was force-stopped.' });
                }
            }, 900);
        } catch (err) {
            finish({ status: 'fail', detail: `Spawn probe threw: ${err.message}` });
        }
    });
}

function resolveMcpBaseUrl(cwd) {
    const configPath = resolveConfigPathNoMutate(cwd);
    const fallback = 'http://127.0.0.1:5000/mcp';
    if (!configPath) return fallback;

    const cfg = readJsonFileSafe(configPath);
    if (!cfg || typeof cfg !== 'object') return fallback;

    const server = cfg.Server && typeof cfg.Server === 'object' ? cfg.Server : {};
    const host = server.BindAddress && typeof server.BindAddress === 'string'
        ? server.BindAddress
        : '127.0.0.1';
    const parsedPort = Number.parseInt(String(server.HttpPort || ''), 10);
    const port = Number.isFinite(parsedPort) && parsedPort > 0 ? parsedPort : 5000;
    return `http://${host}:${port}/mcp`;
}

async function runMcpSmokeProbe(cwd) {
    const scriptPath = path.join(__dirname, '..', '..', 'scripts', 'mcp_smoke.ps1');
    if (!fs.existsSync(scriptPath)) {
        return { status: 'warn', detail: 'MCP smoke script is missing.' };
    }

    const baseUrl = resolveMcpBaseUrl(cwd);
    const shell = process.platform === 'win32' ? 'powershell' : 'pwsh';
    const args = process.platform === 'win32'
        ? ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath, '-BaseUrl', baseUrl]
        : ['-NoProfile', '-File', scriptPath, '-BaseUrl', baseUrl];

    return await new Promise((resolve) => {
        let stdout = '';
        let stderr = '';
        let resolved = false;

        const finish = (payload) => {
            if (resolved) return;
            resolved = true;
            resolve(payload);
        };

        try {
            const child = spawn(shell, args, {
                cwd,
                stdio: ['ignore', 'pipe', 'pipe'],
                windowsHide: true,
                env: process.env
            });

            child.stdout.on('data', (chunk) => {
                stdout += chunk.toString();
            });

            child.stderr.on('data', (chunk) => {
                stderr += chunk.toString();
            });

            child.on('error', (err) => {
                finish({
                    status: 'warn',
                    detail: sanitizeOperationalMessage(`Unable to run MCP smoke probe: ${err.message}.`)
                });
            });

            child.on('exit', (code) => {
                if (code === 0) {
                    finish({ status: 'pass', detail: `MCP smoke succeeded at ${baseUrl}.` });
                    return;
                }

                const preview = sanitizeOperationalMessage((stderr || stdout || '').trim(), '');
                finish({
                    status: 'fail',
                    detail: preview
                        ? `MCP smoke failed at ${baseUrl}: ${preview}`
                        : `MCP smoke failed at ${baseUrl}.`
                });
            });

            setTimeout(() => {
                if (resolved) return;
                try {
                    child.kill();
                } catch {
                }
                finish({ status: 'warn', detail: `MCP smoke timed out at ${baseUrl}.` });
            }, 30000);
        } catch (err) {
            finish({
                status: 'warn',
                detail: sanitizeOperationalMessage(`MCP smoke launch failed: ${err.message}.`)
            });
        }
    });
}

async function handleStatus(options, ctx) {
    const data = buildStatusData(ctx.cwd);

    const ok = options.full
        ? {
            ready: data.ready,
            configFound: data.configFound,
            gatewayExeFound: data.gatewayExeFound,
            kbLooksValid: data.kbLooksValid,
            configSource: data.configSource,
            configPath: data.configPath,
            gatewayExePath: data.gatewayExePath,
            kbPath: data.kbPath,
            gxPath: data.gxPath
        }
        : {
            ready: data.ready,
            configFound: data.configFound,
            gatewayExeFound: data.gatewayExeFound,
            kbLooksValid: data.kbLooksValid
        };

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok,
            help: data.ready
                ? ['Run `genexus-mcp tools list --limit 10` to inspect available MCP tools.']
                : [
                    'Run `genexus-mcp doctor --full` for expanded checks.',
                    'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to create config.'
                ]
        }
    };
}

async function handleDoctor(options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const toolDefPath = getToolDefinitionsPath();
    const toolDefsExists = fs.existsSync(toolDefPath);
    const gatewayExePath = getGatewayExePath();

    let toolCount = 0;
    if (toolDefsExists) {
        try {
            const parsed = JSON.parse(fs.readFileSync(toolDefPath, 'utf8'));
            if (Array.isArray(parsed)) toolCount = parsed.length;
        } catch {
            toolCount = 0;
        }
    }

    const kbPath = data.kbPath;
    const gxPath = data.gxPath;
    const kbExists = !!(kbPath && fs.existsSync(kbPath));
    const gxExeExists = !!(gxPath && fs.existsSync(path.join(gxPath, 'genexus.exe')));

    const checks = [
        { id: 'config_file', status: data.configFound ? 'pass' : 'fail', detail: data.configFound ? 'GX config file was found.' : 'GX config file is missing.' },
        { id: 'gateway_exe', status: data.gatewayExeFound ? 'pass' : 'fail', detail: data.gatewayExeFound ? 'Gateway executable is available.' : 'Gateway executable is missing.' },
        { id: 'kb_path_exists', status: kbExists ? 'pass' : 'warn', detail: kbExists ? 'Configured KB path exists.' : 'Configured KB path does not exist.' },
        { id: 'kb_shape', status: data.kbLooksValid ? 'pass' : 'warn', detail: data.kbLooksValid ? 'KB folder shape looks valid.' : 'KB markers were not found in configured KB path.' },
        { id: 'gx_installation', status: gxExeExists ? 'pass' : 'warn', detail: gxExeExists ? 'GeneXus installation has genexus.exe.' : 'Configured GeneXus installation is missing genexus.exe.' },
        { id: 'tool_definitions', status: toolDefsExists ? 'pass' : 'warn', detail: toolDefsExists ? `Tool definition file found (${toolCount} tools).` : 'tool_definitions.json is missing.' },
        { id: 'gx_env', status: process.env.GX_CONFIG_PATH ? 'pass' : 'warn', detail: process.env.GX_CONFIG_PATH ? 'GX_CONFIG_PATH env var is set.' : 'GX_CONFIG_PATH env var is not set for this process.' }
    ];

    if (options.full) {
        const probe = await probeGatewaySpawn(gatewayExePath);
        checks.push({ id: 'gateway_spawn_probe', status: probe.status, detail: probe.detail });
    } else {
        checks.push({ id: 'gateway_spawn_probe', status: 'warn', detail: 'Spawn probe skipped by default. Run doctor with --full.' });
    }

    if (options.mcpSmoke) {
        const smoke = await runMcpSmokeProbe(ctx.cwd);
        checks.push({ id: 'mcp_smoke', status: smoke.status, detail: smoke.detail });
    }

    const summary = checks.reduce((acc, row) => {
        acc[row.status] = (acc[row.status] || 0) + 1;
        return acc;
    }, { pass: 0, warn: 0, fail: 0 });

    const defaultFields = ['id', 'status', 'detail'];
    const allowedFields = ['id', 'status', 'detail'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'doctor', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }
    const selectedFields = fieldSelection.selectedFields || defaultFields;
    const limited = checks.slice(0, options.limit).map((row) => pickFields(row, selectedFields));

    const help = [];
    if (checks.length > options.limit) {
        help.push(`Run 'genexus-mcp doctor --limit ${checks.length}' for all checks.`);
    }
    if (!options.full) {
        help.push('Run `genexus-mcp doctor --full` to include runtime spawn probe.');
    }
    if (!options.mcpSmoke) {
        help.push('Run `genexus-mcp doctor --mcp-smoke` to execute MCP protocol smoke checks.');
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                summary,
                checks: limited,
                returned: limited.length,
                total: checks.length
            },
            help,
            meta: { fields: selectedFields }
        }
    };
}

async function handleToolsList(options, ctx) {
    const toolDefPath = getToolDefinitionsPath();
    if (!fs.existsSync(toolDefPath)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('tool_definitions.json not found.', ctx.EXIT_CODES.ERROR, ['Run `genexus-mcp doctor` to inspect installation health.'])
        };
    }

    let parsed;
    try {
        parsed = JSON.parse(fs.readFileSync(toolDefPath, 'utf8'));
    } catch {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('Failed to parse tool_definitions.json.', ctx.EXIT_CODES.ERROR, ['Validate the JSON file and rerun tools list.'])
        };
    }

    if (!Array.isArray(parsed)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('tool_definitions.json is not an array.', ctx.EXIT_CODES.ERROR)
        };
    }

    const query = (options.query || '').toLowerCase();
    const rowsAll = parsed.map((tool) => {
        const description = typeof tool.description === 'string' ? tool.description : '';
        const truncated = !options.full && description.length > 160;
        return {
            name: tool.name || 'unknown',
            status: 'available',
            category: resolveToolCategory(tool.name || ''),
            required: Array.isArray(tool.inputSchema && tool.inputSchema.required) ? tool.inputSchema.required.length : 0,
            description: truncated ? `${description.slice(0, 160)}...` : description,
            descriptionChars: description.length,
            truncated
        };
    });

    const rowsFiltered = query
        ? rowsAll.filter((row) => row.name.toLowerCase().includes(query) || row.description.toLowerCase().includes(query))
        : rowsAll;

    const totals = rowsFiltered.reduce((acc, row) => {
        acc[row.category] = (acc[row.category] || 0) + 1;
        return acc;
    }, {});

    const defaultFields = ['name', 'status', 'required'];
    const allowedFields = ['name', 'status', 'category', 'required', 'description', 'descriptionChars', 'truncated'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'tools list', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }
    const selectedFields = fieldSelection.selectedFields || defaultFields;
    const rows = rowsFiltered.slice(0, options.limit).map((row) => pickFields(row, selectedFields));
    const includesDescription = selectedFields.includes('description');
    const returnedRows = rowsFiltered.slice(0, options.limit);
    const anyReturnedTruncated = returnedRows.some((row) => row.truncated);

    const help = [];
    if (rowsFiltered.length === 0) {
        help.push('No tools matched the current filter. Try `genexus-mcp tools list --fields name,status` without --query.');
    }
    if (rowsFiltered.length > options.limit) {
        help.push(`Run 'genexus-mcp tools list --limit ${rowsFiltered.length}${query ? ` --query ${query}` : ''}' for all matching items.`);
    }
    if (includesDescription && anyReturnedTruncated && !options.full) {
        help.push('Run `genexus-mcp tools list --full --fields name,description` to view full descriptions.');
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                tools: rows,
                returned: rows.length,
                total: rowsFiltered.length,
                empty: rowsFiltered.length === 0
            },
            help,
            meta: {
                fields: selectedFields,
                query: options.query || null,
                totalByCategory: totals,
                truncated: includesDescription ? anyReturnedTruncated : false
            }
        }
    };
}

async function handleConfigShow(options, ctx) {
    const configPath = resolveConfigPathNoMutate(ctx.cwd);
    if (!configPath) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('No config.json was found. Set GX_CONFIG_PATH or place config.json in current directory.', ctx.EXIT_CODES.ERROR, ['Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to create one.'])
        };
    }

    const config = readJsonFileSafe(configPath);
    if (config === null) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('config.json exists but is not valid JSON.', ctx.EXIT_CODES.ERROR, ['Fix config.json and rerun `genexus-mcp config show`.'])
        };
    }

    const raw = fs.readFileSync(configPath, 'utf8');
    const rawChars = raw.length;
    const truncateLimit = 1200;
    const truncated = !options.full && rawChars > truncateLimit;

    const compact = {
        path: configPath,
        kbPath: config.Environment && config.Environment.KBPath ? config.Environment.KBPath : null,
        gxPath: config.GeneXus && config.GeneXus.InstallationPath ? config.GeneXus.InstallationPath : null,
        httpPort: config.Server && config.Server.HttpPort ? config.Server.HttpPort : null,
        mcpStdio: config.Server && typeof config.Server.McpStdio === 'boolean' ? config.Server.McpStdio : null,
        raw: truncated ? `${raw.slice(0, truncateLimit)}\n... (truncated, ${rawChars} chars total)` : raw
    };

    const allowedFields = ['path', 'kbPath', 'gxPath', 'httpPort', 'mcpStdio', 'raw'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'config show', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }
    const selectedFields = fieldSelection.selectedFields;
    const payload = selectedFields ? pickFields(compact, selectedFields) : compact;
    const includesRaw = selectedFields ? selectedFields.includes('raw') : true;
    const effectiveTruncated = includesRaw ? truncated : false;

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: payload,
            help: effectiveTruncated ? ['Run `genexus-mcp config show --full` to view complete config content.'] : [],
            meta: { truncated: effectiveTruncated, rawChars }
        }
    };
}

async function runLayoutAutomation(payload, cwd) {
    const scriptPath = path.join(__dirname, '..', '..', 'scripts', 'gx_layout_uia.ps1');
    if (!fs.existsSync(scriptPath)) {
        return { ok: false, error: `Layout automation script not found at ${scriptPath}.` };
    }

    const shell = process.platform === 'win32' ? 'powershell' : 'pwsh';
    const args = process.platform === 'win32'
        ? ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath, '-Payload', JSON.stringify(payload)]
        : ['-NoProfile', '-File', scriptPath, '-Payload', JSON.stringify(payload)];

    return await new Promise((resolve) => {
        let stdout = '';
        let stderr = '';
        let resolved = false;
        let timer = null;
        const timeoutMs = 30000; // 30 second timeout

        const finish = (payload) => {
            if (resolved) return;
            resolved = true;
            if (timer) clearTimeout(timer);
            resolve(payload);
        };

        try {
            const child = spawn(shell, args, {
                cwd,
                stdio: ['ignore', 'pipe', 'pipe'],
                windowsHide: true,
                env: process.env
            });

            child.stdout.on('data', (chunk) => {
                stdout += chunk.toString();
            });

            child.stderr.on('data', (chunk) => {
                stderr += chunk.toString();
            });

            child.on('error', (err) => {
                finish({ ok: false, error: `Failed to launch layout automation: ${err.message}` });
            });

            child.on('exit', (code) => {
                const output = (stdout || '').trim();
                if (code !== 0) {
                    const detail = sanitizeOperationalMessage((stderr || output || '').trim(), 'Layout automation failed.');
                    finish({ ok: false, error: detail });
                    return;
                }

                try {
                    const parsed = output ? JSON.parse(output) : {};
                    finish({ ok: true, data: parsed });
                } catch {
                    finish({
                        ok: false,
                        error: sanitizeOperationalMessage(`Layout automation returned invalid JSON: ${output || stderr}`, 'Invalid layout automation response.')
                    });
                }
            });

            timer = setTimeout(() => {
                try {
                    child.kill();
                } catch (e) {}
                finish({ ok: false, error: `Layout automation timed out after ${timeoutMs}ms` });
            }, timeoutMs);

        } catch (err) {
            finish({ ok: false, error: `Layout automation crashed before launch: ${err.message}` });
        }
    });
}

async function handleLayout(subcommand, options, ctx) {
    if (subcommand === 'status') {
        const outcome = await runLayoutAutomation({ action: 'status', title: options.title || null }, ctx.cwd);
        if (!outcome.ok) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(outcome.error, ctx.EXIT_CODES.ERROR, [
                    'Run `genexus-mcp layout status --format json` to inspect raw status.',
                    'Open GeneXus and focus an object with the Layout tab visible.'
                ])
            };
        }

        const data = outcome.data && typeof outcome.data === 'object' ? outcome.data : {};
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    running: !!data.running,
                    focused: !!data.focused,
                    pid: data.pid || null,
                    title: data.title || null,
                    layoutTabDetected: !!data.layoutTabDetected
                },
                help: data.running
                    ? ['Run `genexus-mcp layout run --action activate-layout` to focus the Layout tab.']
                    : ['Open GeneXus and rerun `genexus-mcp layout status`.']
            }
        };
    }

    if (subcommand === 'run') {
        if (!options.action) {
            return {
                exitCode: ctx.EXIT_CODES.USAGE,
                envelope: usageEnvelope('layout run requires --action. Supported: focus, activate-layout, activate-tab, send-keys, type-text, click.', ctx.EXIT_CODES.USAGE)
            };
        }

        const payload = {
            action: options.action,
            title: options.title || null,
            tab: options.tab || null,
            keys: options.keys || null,
            text: options.text || null,
            x: Number.isFinite(options.x) ? options.x : null,
            y: Number.isFinite(options.y) ? options.y : null
        };

        const outcome = await runLayoutAutomation(payload, ctx.cwd);
        if (!outcome.ok) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(outcome.error, ctx.EXIT_CODES.ERROR, [
                    'Validate GeneXus window is visible and not blocked by modal dialogs.',
                    'Use `genexus-mcp layout status` before retrying.'
                ])
            };
        }

        const data = outcome.data && typeof outcome.data === 'object' ? outcome.data : {};
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: data.action || options.action,
                    success: data.success !== false,
                    title: data.title || null,
                    pid: data.pid || null,
                    tab: data.tab || options.tab || null,
                    detail: data.detail || null
                },
                help: [
                    'Run `genexus-mcp layout run --action send-keys --keys "^{S}"` to trigger save.',
                    'Run `genexus-mcp layout run --action click --x <screenX> --y <screenY>` for deterministic designer clicks.'
                ]
            }
        };
    }

    if (subcommand === 'inspect') {
        const payload = {
            action: 'inspect',
            title: options.title || null,
            tab: options.tab || 'Layout',
            limit: options.limit
        };

        const outcome = await runLayoutAutomation(payload, ctx.cwd);
        if (!outcome.ok) {
            return {
                exitCode: ctx.EXIT_CODES.ERROR,
                envelope: operationalErrorEnvelope(outcome.error, ctx.EXIT_CODES.ERROR, [
                    'Validate GeneXus window is visible and object tab strip is rendered.',
                    'Try `genexus-mcp layout inspect --tab Layout --format json` after focusing the target object.'
                ])
            };
        }

        const data = outcome.data && typeof outcome.data === 'object' ? outcome.data : {};
        const controlsRaw = Array.isArray(data.controls) ? data.controls : [];
        const controls = controlsRaw.map((row) => {
            if (options.full) return row;
            return pickFields(row, ['name', 'controlType', 'automationId', 'bounds']);
        });

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    running: data.running !== false,
                    pid: data.pid || null,
                    title: data.title || null,
                    tab: data.tab || (options.tab || 'Layout'),
                    tabActivated: data.tabActivated !== false,
                    returned: controls.length,
                    total: Number.isFinite(data.total) ? data.total : controls.length,
                    controls
                },
                help: controls.length === 0
                    ? ['No controls found. Try `genexus-mcp layout inspect --tab Layout --full --format json`.']
                    : ['Run `genexus-mcp layout inspect --full --limit 300 --format json` for full control metadata.']
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.USAGE,
        envelope: usageEnvelope('layout requires subcommand `status`, `run`, or `inspect`.', ctx.EXIT_CODES.USAGE)
    };
}

async function runInteractiveInit(ctx) {
    const defaultGx = discoverGeneXusInstallation() || 'C:\\Program Files (x86)\\GeneXus\\GeneXus18';

    if (!ctx.options.quiet) {
        ctx.stderr.write('GeneXus MCP setup wizard\n\n');
    }

    const rl = readline.createInterface({ input: process.stdin, output: ctx.stderr });
    const question = (text) => new Promise((resolve) => rl.question(text, (answer) => resolve(answer)));

    try {
        const kbAnswer = await question(`1) Knowledge Base folder path (default: ${ctx.cwd}):\n> `);
        const finalKb = String(kbAnswer || '').trim() || ctx.cwd;

        const gxAnswer = await question(`\n2) GeneXus installation path (default: ${defaultGx}):\n> `);
        const finalGx = String(gxAnswer || '').trim() || defaultGx;

        const created = createConfigFile(finalKb, finalGx);
        const patchResult = patchClientConfig(created.targetConfigPath);

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'init',
                    mode: 'interactive',
                    configPath: created.targetConfigPath,
                    noOp: !created.changed,
                    clientsPatchedCount: patchResult.patched.length
                },
                help: patchResult.patched.length === 0 ? ['Set `GX_CONFIG_PATH` in your MCP client env to the generated config path.'] : [],
                meta: {
                    patchedClients: patchResult.patched,
                    failedClients: patchResult.failed
                }
            }
        };
    } catch {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('Interactive init failed.', ctx.EXIT_CODES.ERROR)
        };
    } finally {
        rl.close();
    }
}

async function handleInit(options, ctx) {
    if (options.interactive) {
        return runInteractiveInit({ ...ctx, options });
    }

    if (!options.kb || !options.gx) {
        return {
            exitCode: ctx.EXIT_CODES.USAGE,
            envelope: usageEnvelope('Missing required flags for non-interactive init. Use --kb and --gx.', ctx.EXIT_CODES.USAGE)
        };
    }

    try {
        const created = createConfigFile(options.kb, options.gx);
        let patchResult = { patched: [], failed: [] };
        if (options.writeClients) {
            patchResult = patchClientConfig(created.targetConfigPath);
        }

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'init',
                    mode: 'non_interactive',
                    configPath: created.targetConfigPath,
                    configFound: true,
                    noOp: !created.changed,
                    clientsPatchedCount: patchResult.patched.length
                },
                help: patchResult.patched.length === 0 && !options.writeClients
                    ? ['Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>" --write-clients` to patch supported clients.']
                    : [],
                meta: {
                    patchedClients: patchResult.patched,
                    failedClients: patchResult.failed
                }
            }
        };
    } catch {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('Failed to write configuration.', ctx.EXIT_CODES.ERROR)
        };
    }
}

function commandHelpMap() {
    return {
        axi: {
            usage: 'genexus-mcp axi home [--format toon|json|text]',
            examples: ['genexus-mcp axi home', 'genexus-mcp axi home --format json']
        },
        home: {
            usage: 'genexus-mcp home [--format toon|json|text] OR genexus-mcp axi home [--format toon|json|text]',
            examples: ['genexus-mcp home', 'genexus-mcp axi home --format json']
        },
        status: {
            usage: 'genexus-mcp status [--full] [--format toon|json|text] [--quiet] [--no-color]',
            examples: ['genexus-mcp status', 'genexus-mcp status --full --format json']
        },
        doctor: {
            usage: 'genexus-mcp doctor [--full] [--mcp-smoke] [--fields f1,f2] [--limit N] [--format toon|json|text]',
            examples: ['genexus-mcp doctor', 'genexus-mcp doctor --full --mcp-smoke --format json']
        },
        tools: {
            usage: 'genexus-mcp tools list [--query text] [--fields f1,f2] [--limit N] [--full] [--format ...]',
            examples: ['genexus-mcp tools list', 'genexus-mcp tools list --query read --fields name,category --format json']
        },
        config: {
            usage: 'genexus-mcp config show [--full] [--fields f1,f2] [--format ...]',
            examples: ['genexus-mcp config show', 'genexus-mcp config show --full --format json']
        },
        init: {
            usage: 'genexus-mcp init --kb <path> --gx <path> [--write-clients] [--format ...] OR genexus-mcp init --interactive',
            examples: ['genexus-mcp init --kb "C:\\KBs\\MyKB" --gx "C:\\Program Files (x86)\\GeneXus\\GeneXus18"', 'genexus-mcp init --interactive']
        },
        llm: {
            usage: 'genexus-mcp llm help [--full] [--fields f1,f2] [--format toon|json|text]',
            examples: ['genexus-mcp llm help --format json', 'genexus-mcp llm help --full --format json']
        },
        update: {
            usage: 'genexus-mcp update [--format toon|json|text]',
            examples: ['genexus-mcp update', 'genexus-mcp update --format json']
        },
        layout: {
            usage: 'genexus-mcp layout status [--title "GeneXus"] [--format ...] OR genexus-mcp layout run --action <focus|activate-layout|activate-tab|send-keys|type-text|click> [--tab "Layout"] [--keys "..."] [--text "..."] [--x N --y N] [--title "..."] [--format ...] OR genexus-mcp layout inspect [--tab "Layout"] [--limit N] [--full] [--title "..."] [--format ...]',
            examples: ['genexus-mcp layout status --format json', 'genexus-mcp layout run --action activate-tab --tab "Layout" --format json', 'genexus-mcp layout inspect --tab Layout --format json']
        }
    };
}

function collapseHome(absPath) {
    const home = require('os').homedir();
    if (!absPath || !home) return absPath;
    if (absPath.toLowerCase().startsWith(home.toLowerCase())) {
        return `~${absPath.slice(home.length)}`;
    }
    return absPath;
}

async function handleHome(_options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const binPath = collapseHome(process.argv[1] || process.execPath);
    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                bin: binPath,
                description: 'GeneXus MCP launcher and AXI-oriented utility CLI',
                ready: data.ready,
                next: data.ready
                    ? ['genexus-mcp status', 'genexus-mcp doctor --mcp-smoke', 'genexus-mcp tools list --limit 10', 'genexus-mcp layout status', 'genexus-mcp layout inspect --tab Layout']
                    : ['genexus-mcp status', 'genexus-mcp doctor --full', 'genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"']
            },
            help: []
        }
    };
}

async function handleHelp(targetCommand, ctx) {
    const binPath = collapseHome(process.argv[1] || process.execPath);
    const map = commandHelpMap();
    if (targetCommand && map[targetCommand]) {
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    bin: binPath,
                    command: targetCommand,
                    usage: map[targetCommand].usage,
                    examples: map[targetCommand].examples,
                    defaults: {
                        format: 'toon',
                        limit: 100
                    }
                },
                help: []
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                bin: binPath,
                command: 'genexus-mcp',
                description: 'GeneXus MCP launcher and AXI-oriented utility CLI',
                commands: ['home', 'axi home', 'status', 'doctor', 'tools list', 'config show', 'layout status', 'layout run', 'layout inspect', 'init', 'llm help', 'update', 'help'],
                defaults: { format: 'toon', limit: 100 }
            },
            help: [
                'Run `genexus-mcp <command> --help` for subcommand help.',
                'Without AXI subcommands, CLI works as MCP launcher passthrough.'
            ]
        }
    };
}

function tryReadLlmPlaybookMarkdown() {
    const candidates = [
        path.join(__dirname, '..', '..', 'docs', 'llm_cli_mcp_playbook.md'),
        path.join(process.cwd(), 'docs', 'llm_cli_mcp_playbook.md')
    ];

    for (const candidate of candidates) {
        try {
            if (fs.existsSync(candidate)) {
                return fs.readFileSync(candidate, 'utf8');
            }
        } catch {
        }
    }

    return null;
}

async function handleLlmHelp(options, ctx) {
    const allowedFields = ['objective', 'interfaceSelection', 'cli', 'mcp', 'timeouts', 'bestPractices', 'examples', 'resources'];
    const fieldSelection = validateFieldSelection(options.fields, allowedFields, 'llm help', ctx);
    if (fieldSelection.errorResult) {
        return fieldSelection.errorResult;
    }

    const payload = {
        objective: 'Use AXI CLI for environment/bootstrap checks and MCP for KB operations with deterministic, token-efficient flows.',
        interfaceSelection: {
            cli: ['home', 'status', 'doctor --mcp-smoke', 'tools list', 'config show'],
            mcp: ['genexus_query', 'genexus_list_objects', 'genexus_read', 'genexus_edit', 'genexus_lifecycle']
        },
        cli: {
            parseStdoutOnly: true,
            expectedMeta: ['schemaVersion=axi-cli/1', 'command=<normalized-command>'],
            exitCodes: { ok: 0, error: 1, usage: 2 }
        },
        mcp: {
            parsePath: 'result.content[0].text',
            expectedMeta: ['schemaVersion=mcp-axi/1', 'tool=<tool-name>'],
            listHelpers: ['returned', 'total', 'empty', 'hasMore', 'nextOffset'],
            shaping: ['fields=<csv|array>', 'axiCompact=true (query/list_objects)']
        },
        timeouts: {
            rule: 'If result.isError=true and operationId is present, treat as running operation, not terminal failure.',
            followUp: [
                "genexus_lifecycle(action='status', target='op:<operationId>')",
                "genexus_lifecycle(action='result', target='op:<operationId>')"
            ]
        },
        bestPractices: [
            'Always set limit/offset for list and read flows.',
            'Prefer parentPath over parent for disambiguation.',
            'Use patch mode dryRun before persistent edits.',
            'Prefer batch_read for multi-object context gathering.'
        ],
        examples: [
            'genexus-mcp home --format json',
            'genexus-mcp doctor --mcp-smoke --format json',
            "tools/call genexus_list_objects { parentPath, limit, offset, axiCompact:true }",
            "tools/call genexus_query { query:'@quick', limit:20, fields:'name,type,path' }"
        ],
        resources: ['genexus://kb/llm-playbook', 'genexus://kb/agent-playbook', 'prompt: gx_bootstrap_llm']
    };

    const selectedFields = fieldSelection.selectedFields || null;
    const ok = selectedFields ? pickFields(payload, selectedFields) : payload;
    const envelope = {
        ok,
        help: [
            'Use `genexus://kb/llm-playbook` through MCP resources/read for protocol-native guidance.',
            'Use `genexus-mcp llm help --full --format json` for embedded markdown when available.'
        ]
    };

    if (options.full) {
        const markdown = tryReadLlmPlaybookMarkdown();
        if (markdown) {
            envelope.ok.markdown = markdown;
        }
    }

    return { exitCode: ctx.EXIT_CODES.OK, envelope };
}

module.exports = {
    parseFieldSelection,
    pickFields,
    handleStatus,
    handleDoctor,
    handleToolsList,
    handleConfigShow,
    handleInit,
    handleHome,
    handleLlmHelp,
    handleLayout,
    handleHelp,
    usageEnvelope,
    operationalErrorEnvelope,
    commandHelpMap
};
