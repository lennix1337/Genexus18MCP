import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { GxUriParser } from "../../utils/GxUriParser";
import { Logger } from "../../utils/Logger";

suite("GxUriParser.loadMirrorIndex corrupt-index handling", () => {
  class FakeSink {
    lines: string[] = [];
    appendLine(value: string): void {
      this.lines.push(value);
    }
  }

  let shadowRoot: string;

  setup(() => {
    shadowRoot = fs.mkdtempSync(path.join(os.tmpdir(), "gx-uri-parser-test-"));
  });

  teardown(() => {
    GxUriParser.configureShadowRoot(undefined);
    GxUriParser.clearMirrorIndex();
    fs.rmSync(shadowRoot, { recursive: true, force: true });
  });

  test("logs a warning and clears the index when .gx_index.json is corrupt", () => {
    const indexPath = path.join(shadowRoot, ".gx_index.json");
    fs.writeFileSync(indexPath, "{ this is not valid json");

    const sink = new FakeSink();
    Logger.configureForTest(sink, "debug");

    GxUriParser.configureShadowRoot(shadowRoot);
    GxUriParser.loadMirrorIndex();

    const warnLine = sink.lines.find((line) => line.includes("[WARN]"));
    assert.ok(warnLine, "expected a [WARN] line to be logged for the corrupt index");
    assert.ok(warnLine!.includes(indexPath), "warning should mention the corrupt index path");

    assert.strictEqual(GxUriParser.findMirrorPath("Transaction", "Anything"), null);
  });
});

suite("GxUriParser.resolveWithinRoot containment", () => {
  const root = path.join("C:", "shadow-root");

  test("joins a simple segment and stays under root", () => {
    const result = GxUriParser.resolveWithinRoot(root, "Procedure", "MyProc.gx");
    assert.ok(result, "expected a non-null resolved path");
    assert.ok(
      result!.toLowerCase().startsWith(path.resolve(root).toLowerCase()),
      "resolved path should start with the root",
    );
  });

  test("allows legitimate nested module segments", () => {
    const result = GxUriParser.resolveWithinRoot(root, "Module1", "Module2", "X.gx");
    assert.ok(result, "expected nested segments to be allowed");
    assert.ok(
      result!.toLowerCase().startsWith(path.resolve(root).toLowerCase()),
      "resolved nested path should stay under the root",
    );
  });

  test("rejects a traversal escape via '..' segments", () => {
    const result = GxUriParser.resolveWithinRoot(root, "..", "..", "X.gx");
    assert.strictEqual(result, null);
  });

  test("rejects an absolute segment that would replace the root", () => {
    const outsideAbsolute = path.join(path.parse(path.resolve(root)).root, "outside-root");
    const result = GxUriParser.resolveWithinRoot(root, outsideAbsolute, "x");
    assert.strictEqual(result, null);
  });
});

suite("GxUriParser.parse gxkb18-scheme containment", () => {
  test("rejects a gxkb18 URI whose type segment is a traversal marker", () => {
    // The split-based parser only ever inspects the segment immediately
    // preceding the filename as `type` (earlier segments, e.g. a leading
    // '../../', are discarded and never reach a path.join downstream) - so
    // the traversal marker has to land in that exact slot to be a real risk.
    const uri = vscode.Uri.parse("gxkb18:/../Name.gx");
    const info = GxUriParser.parse(uri);
    assert.strictEqual(info, null);
  });

  test("still parses a normal gxkb18 URI (no regression)", () => {
    const uri = vscode.Uri.parse("gxkb18:/Procedure/MyProc.gx");
    const info = GxUriParser.parse(uri);
    assert.ok(info, "expected a normal URI to parse");
    assert.strictEqual(info!.type, "Procedure");
    assert.strictEqual(info!.name, "MyProc");
  });
});
