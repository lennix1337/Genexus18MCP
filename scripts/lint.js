#!/usr/bin/env node
'use strict';

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const SETUP_ERROR = 'GXMCP_LINT_SETUP_ERROR:';
const dependencies = [
  {
    name: 'eslint',
    purpose: 'é o executável chamado por npm run lint e analisa estaticamente o código JavaScript em busca de problemas, sem executar o MCP.',
    executable: true,
  },
  {
    name: '@eslint/js',
    purpose: 'é importado pelo eslint.config.js e fornece as regras recomendadas usadas pela configuração.',
    executable: false,
  },
];

function packageRoot() {
  return process.env.GXMCP_LINT_ROOT || path.join(__dirname, '..');
}

function dependencyStatus(root) {
  const missing = [];
  for (const dependency of dependencies) {
    if (dependency.executable) {
      const bin = path.join(root, 'node_modules', '.bin', process.platform === 'win32' ? 'eslint.cmd' : 'eslint');
      if (!fs.existsSync(bin)) missing.push(dependency);
    } else {
      try {
        require.resolve(dependency.name, { paths: [root] });
      } catch {
        missing.push(dependency);
      }
    }
  }
  return missing;
}

function setupMessage(missing) {
  return [
    ...missing.map((dependency) => `A dependência local "${dependency.name}" não está instalada.\n${dependency.purpose}`),
    'O comando "npm ci" instalará as versões do "package-lock.json" e poderá acessar o registry do npm.',
  ].join('\n');
}

function fail(message) {
  process.stderr.write(`${SETUP_ERROR} ${message}\n`);
  process.exit(1);
}

function isCi() {
  return Boolean(process.env.CI || process.env.GITHUB_ACTIONS);
}

function confirmInstall() {
  process.stderr.write(`${setupMessage(dependencyStatus(packageRoot()))}\nDeseja executar "npm ci" para instalar as dependências do projeto? [s/N] `);
  const buffer = Buffer.alloc(64);
  let bytes;
  try {
    bytes = fs.readSync(0, buffer, 0, buffer.length, null);
  } catch {
    return false;
  }
  return /^s(?:im)?\s*$/i.test(buffer.toString('utf8', 0, bytes).trim());
}

function ensureDependencies(root) {
  const missing = dependencyStatus(root);
  if (missing.length === 0) return true;

  if (!process.stdin.isTTY || isCi()) {
    fail(`${setupMessage(missing)}\nExecute "npm ci" na raiz do projeto e tente novamente. Nenhuma instalação foi executada.`);
  }
  if (!confirmInstall()) fail('Instalação cancelada. Execute "npm ci" na raiz do projeto quando quiser preparar o ambiente.');

  // npm ci can remove node_modules while the parent npm run lint process is alive.
  // A first install has nothing to replace; an existing tree is left to an
  // explicit command outside this process.
  if (process.platform === 'win32' && fs.existsSync(path.join(root, 'node_modules'))) {
    fail('No Windows, não é seguro substituir node_modules enquanto npm run lint ainda está ativo. Feche este processo e execute "npm ci" na raiz do projeto; depois rode "npm run lint" novamente.');
  }

  const result = spawnSync('npm', ['ci'], { cwd: root, stdio: 'inherit' });
  if (result.error || result.status !== 0) {
    fail(`"npm ci" falhou${result.status == null ? `: ${result.error?.message || 'erro desconhecido'}` : ` com código ${result.status}`}. Execute "npm ci" na raiz do projeto e tente novamente.`);
  }
  return true;
}

function run() {
  const root = packageRoot();
  ensureDependencies(root);
  const executable = path.join(root, 'node_modules', '.bin', process.platform === 'win32' ? 'eslint.cmd' : 'eslint');
  const result = spawnSync(executable, ['cli', 'scripts', 'eslint.config.js', '--max-warnings=0'], {
    cwd: root,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  });
  if (result.error) process.exit(1);
  process.exit(result.status === null ? 1 : result.status);
}

if (require.main === module) run();

module.exports = { dependencyStatus, ensureDependencies, isCi, packageRoot, setupMessage };
