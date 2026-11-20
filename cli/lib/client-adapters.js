const fs = require('fs');

class BaseClientAdapter {
    apply(_client, _launcher, _targetConfigPath, _opts = {}) {
        throw new Error('apply() must be implemented by subclass');
    }
    remove(_client, _opts = {}) {
        throw new Error('remove() must be implemented by subclass');
    }
    read(_client, _serverName) {
        throw new Error('read() must be implemented by subclass');
    }
}

class McpServersJsonAdapter extends BaseClientAdapter {
    constructor(configModule) {
        super();
        this.config = configModule || require('./config');
    }
    apply(client, launcher, targetConfigPath, opts = {}) {
        return this.config.applyMcpServersJson(client.path, launcher, targetConfigPath, opts);
    }
    remove(client, opts = {}) {
        return this.config.removeMcpServersJson(client.path, opts);
    }
    read(client, serverName) {
        if (!fs.existsSync(client.path)) return null;
        const parsed = this.config.readJsonFileSafe(client.path);
        if (!parsed || typeof parsed !== 'object') return null;
        let entry = parsed.mcpServers && parsed.mcpServers[serverName];
        if (!entry && serverName === 'genexus18mcp') {
            const legacyGx = parsed.mcpServers && parsed.mcpServers.genexus;
            if (legacyGx && !this.config.isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
            else if (parsed.mcpServers && parsed.mcpServers.genexus18) entry = parsed.mcpServers.genexus18;
        }
        if (!entry) return null;
        return {
            command: entry.command,
            args: Array.isArray(entry.args) ? entry.args : [],
            env: (entry.env && typeof entry.env === 'object') ? entry.env : {}
        };
    }
}

class VsCodeServersAdapter extends BaseClientAdapter {
    constructor(configModule) {
        super();
        this.config = configModule || require('./config');
    }
    apply(client, launcher, targetConfigPath, opts = {}) {
        return this.config.applyVsCodeServersJson(client.path, launcher, targetConfigPath, opts);
    }
    remove(client, opts = {}) {
        return this.config.removeVsCodeServersJson(client.path, opts);
    }
    read(client, serverName) {
        if (!fs.existsSync(client.path)) return null;
        const parsed = this.config.readJsonFileSafe(client.path);
        if (!parsed || typeof parsed !== 'object') return null;
        let entry = parsed.servers && parsed.servers[serverName];
        if (!entry && serverName === 'genexus18mcp') {
            const legacyGx = parsed.servers && parsed.servers.genexus;
            if (legacyGx && !this.config.isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
            else if (parsed.servers && parsed.servers.genexus18) entry = parsed.servers.genexus18;
        }
        if (!entry) return null;
        return {
            command: entry.command,
            args: Array.isArray(entry.args) ? entry.args : [],
            env: (entry.env && typeof entry.env === 'object') ? entry.env : {}
        };
    }
}

class OpenCodeJsoncAdapter extends BaseClientAdapter {
    constructor(configModule) {
        super();
        this.config = configModule || require('./config');
    }
    apply(client, launcher, targetConfigPath, opts = {}) {
        return this.config.applyOpenCodeJson(client.path, launcher, targetConfigPath, {
            ...opts,
            alternatePaths: client.alternatePaths || []
        });
    }
    remove(client, opts = {}) {
        return this.config.removeOpenCodeJson(client.path, {
            ...opts,
            alternatePaths: client.alternatePaths || []
        });
    }
    read(client, serverName) {
        if (!fs.existsSync(client.path)) return null;
        const parsed = this.config.readJsonFileSafe(client.path);
        if (!parsed || typeof parsed !== 'object' || !parsed.mcp || typeof parsed.mcp !== 'object') return null;
        const nested = parsed.mcp.servers && typeof parsed.mcp.servers === 'object' && !Array.isArray(parsed.mcp.servers);
        const container = nested ? parsed.mcp.servers : parsed.mcp;
        let entry = container[serverName];
        if (!entry && serverName === 'genexus18mcp') {
            const legacyGx = container.genexus;
            if (legacyGx && !this.config.isThirdPartyMcpEntry(legacyGx)) entry = legacyGx;
            else if (container.genexus18) entry = container.genexus18;
        }
        if (!entry) return null;
        const cmd = Array.isArray(entry.command) ? entry.command[0] : entry.command;
        const args = Array.isArray(entry.command) ? entry.command.slice(1) : (Array.isArray(entry.args) ? entry.args : []);
        const env = (entry.environment && typeof entry.environment === 'object') ? entry.environment
            : ((entry.env && typeof entry.env === 'object') ? entry.env : {});
        return { command: cmd, args, env };
    }
}

class CodexTomlAdapter extends BaseClientAdapter {
    constructor(configModule) {
        super();
        this.config = configModule || require('./config');
    }
    apply(client, launcher, targetConfigPath, opts = {}) {
        return this.config.applyCodexToml(client.path, launcher, targetConfigPath, opts);
    }
    remove(client, opts = {}) {
        return this.config.removeCodexToml(client.path, opts);
    }
    read(client, serverName) {
        if (!fs.existsSync(client.path)) return null;
        let content;
        try {
            content = fs.readFileSync(client.path, 'utf8');
        } catch {
            return null;
        }
        let parsed = this.config.extractCodexTomlEntry(content, serverName);
        if (!parsed && serverName === 'genexus18mcp') {
            const legacyGx = this.config.extractCodexTomlEntry(content, 'genexus');
            if (legacyGx && !this.config.isThirdPartyMcpEntry(legacyGx)) parsed = legacyGx;
            else {
                const legacyGx18 = this.config.extractCodexTomlEntry(content, 'genexus18');
                if (legacyGx18) parsed = legacyGx18;
            }
        }
        if (!parsed) return null;
        return {
            command: parsed.command,
            args: Array.isArray(parsed.args) ? parsed.args : [],
            env: (parsed.env && typeof parsed.env === 'object') ? parsed.env : {}
        };
    }
}

const adapters = {
    'mcpServers': new McpServersJsonAdapter(),
    'vscode-servers': new VsCodeServersAdapter(),
    'opencode': new OpenCodeJsoncAdapter(),
    'codex-toml': new CodexTomlAdapter()
};

function getClientAdapter(format) {
    const adapter = adapters[format];
    if (!adapter) {
        throw new Error(`Unknown client format: ${format}`);
    }
    return adapter;
}

class ClientConfigManager {
    constructor(configModule) {
        this.config = configModule || require('./config');
    }

    apply(client, launcher, targetConfigPath, opts = {}) {
        const adapter = getClientAdapter(client.format);
        return adapter.apply(client, launcher, targetConfigPath, opts);
    }

    remove(client, opts = {}) {
        const adapter = getClientAdapter(client.format);
        return adapter.remove(client, opts);
    }

    read(client, serverName) {
        if (client.writeSupported === false) return null;
        const adapter = getClientAdapter(client.format);
        return adapter.read(client, serverName);
    }
}

module.exports = {
    BaseClientAdapter,
    McpServersJsonAdapter,
    VsCodeServersAdapter,
    OpenCodeJsoncAdapter,
    CodexTomlAdapter,
    getClientAdapter,
    ClientConfigManager
};
