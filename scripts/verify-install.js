#!/usr/bin/env node
'use strict';

// issue #112 — a fresh npm/npx install can land with an empty publish/worker/ folder
// (incomplete extraction into the npm cache). Every KB tool call then fails with
// "Worker NOT FOUND" far from the install step. Verify the shipped binaries right
// after extraction and fail the install loudly with the exact remediation instead.

const fs = require('fs');
const path = require('path');

const pkgRoot = path.join(__dirname, '..');

const packageVersion = (() => {
  try {
    return require(path.join(pkgRoot, 'package.json')).version || '0.0.0';
  } catch {
    return '0.0.0';
  }
})();
const packageMajor = Number.parseInt(packageVersion.split('.')[0], 10) || 0;
// Engine.Backend defaults to gxengine: packages above 4.6.0 must ship the Rust
// binaries. Earlier releases (<= 4.6.0) never did, so they keep verifying.
const ENGINE_FLOOR = [4, 6, 0];
const requiresEngine = (() => {
  const match = packageVersion.match(/^(\d+)\.(\d+)\.(\d+)/);
  if (!match) return false;
  for (let i = 0; i < 3; i += 1) {
    const part = Number(match[i + 1]);
    if (part !== ENGINE_FLOOR[i]) return part > ENGINE_FLOOR[i];
  }
  return false;
})();

const required = [
  'publish/GxMcp.Gateway.exe',
  'publish/worker/GxMcp.Worker.exe',
  'publish/tool_definitions.json',
];

if (packageMajor >= 3) {
  required.push('publish/gxmcp-manifest.json');
}

if (requiresEngine) {
  required.push('publish/gx-engine.exe', 'publish/gxwire.exe');
}

const missing = required.filter((rel) => {
  try {
    return !fs.statSync(path.join(pkgRoot, rel)).isFile();
  } catch {
    return true;
  }
});

if (missing.length === 0) {
  console.log(`genexus-mcp: Gateway, Worker, schema${packageMajor >= 3 ? ', and v3 manifest' : ''} verified.`);
  process.exit(0);
}

// Dev checkout (this repository) has no publish/ until build.ps1 runs — warn, don't
// break `npm install`. Only a real package install (npm -g / npx extraction) fails.
const isDevCheckout = (() => {
  try {
    const gitMarker = fs.statSync(path.join(pkgRoot, '.git'));
    return gitMarker.isDirectory() || gitMarker.isFile();
  } catch {
    return false;
  }
})();

if (isDevCheckout) {
  console.warn(
    'genexus-mcp: publish/ binaries missing in dev checkout — run .\\build.ps1 before using the server.'
  );
  process.exit(0);
}


console.error(
  [
    'genexus-mcp: INCOMPLETE INSTALL — required binaries are missing:',
    ...missing.map((rel) => `  - ${rel}`),
    '',
    'This is an npm cache/extraction failure, not a usage error. Fix with:',
    '  npm cache clean --force',
    '  npm uninstall -g genexus-mcp   (or remove the broken npx cache entry)',
    '  npm install -g genexus-mcp@latest',
  ].join('\n')
);
process.exit(1);
