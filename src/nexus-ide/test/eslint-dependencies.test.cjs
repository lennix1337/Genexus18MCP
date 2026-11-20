"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const localModules = fs.realpathSync(path.join(__dirname, "../node_modules"));

function localPackage(name) {
  const entry = fs.realpathSync(require.resolve(name));
  const packageDir = fs.realpathSync(path.join(localModules, name));
  assert.ok(entry.startsWith(packageDir + path.sep), `${name} must resolve inside ${packageDir}`);
  return JSON.parse(fs.readFileSync(path.join(packageDir, "package.json"), "utf8"));
}

test("@eslint/js resolves locally and matches the eslint major", () => {
  const js = localPackage("@eslint/js");
  const eslint = localPackage("eslint");
  assert.equal(js.version.split(".")[0], eslint.version.split(".")[0]);
});
