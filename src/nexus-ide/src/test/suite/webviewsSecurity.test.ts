import * as assert from "assert";
import * as vscode from "vscode";
import { DiagramView } from "../../webviews/DiagramView";
import { HistoryView, parseViewDiffMessage } from "../../webviews/HistoryView";
import { IndexView } from "../../webviews/IndexView";
import { PropertiesView, parseSetPropertyMessage } from "../../webviews/PropertiesView";
import { StructureView, parseSaveMessage } from "../../webviews/StructureView";
import { messagePage } from "../../utils/webviewHtml";

const CSP_SOURCE = "vscode-webview://test";
const NONCE = "TESTNONCE123==";
const PAYLOAD = `<img src=x onerror=alert(1)></h1><script>alert(1)</script>`;

const pages: Array<[string, string]> = [
  ["HistoryView", HistoryView.getHtml(PAYLOAD, "", CSP_SOURCE, NONCE)],
  ["IndexView", IndexView.getHtml(PAYLOAD, CSP_SOURCE, NONCE)],
  ["PropertiesView", PropertiesView.getHtml(PAYLOAD, PAYLOAD, CSP_SOURCE, NONCE)],
  ["StructureView (editable)", StructureView.getHtml(PAYLOAD, false, CSP_SOURCE, NONCE)],
  ["StructureView (read-only)", StructureView.getHtml(PAYLOAD, true, CSP_SOURCE, NONCE)],
];

function directive(html: string, name: string): string {
  const csp = /Content-Security-Policy"\s+content="([^"]*)"/.exec(html)?.[1] ?? "";
  return csp.split(";").map((d) => d.trim()).find((d) => d.startsWith(`${name} `)) ?? "";
}

suite("Webviews - script CSP, escaping and message validation", () => {
  for (const [name, html] of pages) {
    test(`${name}: script-src is nonce-only, no inline handlers, names escaped`, () => {
      const scriptSrc = directive(html, "script-src");
      assert.strictEqual(scriptSrc, `script-src 'nonce-${NONCE}'`);
      assert.ok(!/script-src[^;]*unsafe-inline/.test(html), "script-src must not allow 'unsafe-inline'");

      const scriptTags = html.match(/<script\b[^>]*>/g) ?? [];
      assert.ok(scriptTags.length > 0, "expected an inline script");
      for (const tag of scriptTags) {
        assert.ok(tag.includes(`nonce="${NONCE}"`), `script tag without nonce: ${tag}`);
      }

      assert.ok(!/\son[a-z]+\s*=\s*["']/i.test(html), "inline event-handler attribute found");
      assert.ok(!html.includes(PAYLOAD), "object name must not be interpolated unescaped");
      assert.ok(!html.includes("<img src=x"), "object name must not inject markup");
    });
  }

  test("HistoryView: row buttons carry data-version and the script wires the click", () => {
    const html = HistoryView.getHtml(
      "OBJ",
      `<button class="diff-btn" data-version="3">x</button>`,
      CSP_SOURCE,
      NONCE,
    );
    assert.ok(html.includes('data-version="3"'));
    assert.ok(/addEventListener\('click'/.test(html));
  });

  test("HistoryView: object name embedded in the script cannot close the script tag", () => {
    const html = HistoryView.getHtml("</script><b>", "", CSP_SOURCE, NONCE);
    assert.strictEqual((html.match(/<\/script>/g) ?? []).length, 1);
  });

  test("DiagramView: mermaid source is escaped and round-trips through entity decoding", () => {
    const panel = vscode.window.createWebviewPanel(
      "gxDiagramEscTest",
      "Diagram Escape Test",
      vscode.ViewColumn.Beside,
      { enableScripts: true },
    );
    try {
      const source = `graph TD; A["</pre><script>alert(1)</script>"]-->B;`;
      const html = DiagramView.buildHtml(panel.webview, vscode.Uri.file(__dirname), source);
      const body = /<pre class="mermaid">([\s\S]*?)<\/pre>/.exec(html)?.[1] ?? "";
      assert.ok(!body.includes("<script>") && !body.includes("</pre>"), "markup must be escaped");
      const decoded = body
        .replace(/&lt;/g, "<")
        .replace(/&gt;/g, ">")
        .replace(/&quot;/g, '"')
        .replace(/&#39;/g, "'")
        .replace(/&amp;/g, "&");
      assert.strictEqual(decoded.trim(), source);
      assert.ok(!/script-src[^;]*unsafe-inline/.test(html));
    } finally {
      panel.dispose();
    }
  });

  test("messagePage: escapes text and carries a CSP that blocks scripts", () => {
    const html = messagePage(PAYLOAD);
    assert.ok(!html.includes("<img"));
    assert.ok(!html.includes("<script>"));
    assert.ok(/default-src 'none'/.test(html));
    assert.strictEqual(directive(html, "script-src"), "");
  });

  test("parseViewDiffMessage accepts only a viewDiff for the panel's object with an integer version", () => {
    assert.strictEqual(parseViewDiffMessage({ command: "viewDiff", versionId: 4, objName: "A" }, "A"), 4);
    assert.strictEqual(parseViewDiffMessage({ command: "viewDiff", versionId: 0, objName: "A" }, "A"), 0);
    assert.strictEqual(parseViewDiffMessage({ command: "viewDiff", versionId: 4, objName: "B" }, "A"), undefined);
    assert.strictEqual(parseViewDiffMessage({ command: "other", versionId: 4, objName: "A" }, "A"), undefined);
    assert.strictEqual(parseViewDiffMessage({ command: "viewDiff", versionId: "4", objName: "A" }, "A"), undefined);
    assert.strictEqual(parseViewDiffMessage({ command: "viewDiff", versionId: -1, objName: "A" }, "A"), undefined);
    assert.strictEqual(parseViewDiffMessage({ command: "viewDiff", versionId: 1.5, objName: "A" }, "A"), undefined);
    assert.strictEqual(parseViewDiffMessage(null, "A"), undefined);
    assert.strictEqual(parseViewDiffMessage("viewDiff", "A"), undefined);
  });

  test("parseSetPropertyMessage accepts only known property names with string values", () => {
    const known = new Set(["Width", "Caption"]);
    assert.deepStrictEqual(
      parseSetPropertyMessage({ command: "setProperty", name: "Width", value: "10" }, known),
      { name: "Width", value: "10" },
    );
    assert.strictEqual(parseSetPropertyMessage({ command: "setProperty", name: "Evil", value: "1" }, known), undefined);
    assert.strictEqual(parseSetPropertyMessage({ command: "setProperty", name: "Width", value: 10 }, known), undefined);
    assert.strictEqual(parseSetPropertyMessage({ command: "x", name: "Width", value: "1" }, known), undefined);
    assert.strictEqual(parseSetPropertyMessage(undefined, known), undefined);
  });

  test("parseSaveMessage accepts only a save with a well-formed structure tree", () => {
    const ok = { command: "save", structure: { children: [{ name: "A", children: [{ name: "B" }] }] } };
    assert.ok(parseSaveMessage(ok));
    assert.strictEqual(parseSaveMessage({ command: "save" }), undefined);
    assert.strictEqual(parseSaveMessage({ command: "save", structure: { children: "x" } }), undefined);
    assert.strictEqual(parseSaveMessage({ command: "save", structure: { children: [{ name: 1 }] } }), undefined);
    assert.strictEqual(parseSaveMessage({ command: "nope", structure: { children: [] } }), undefined);
    assert.strictEqual(parseSaveMessage(null), undefined);
  });
});
