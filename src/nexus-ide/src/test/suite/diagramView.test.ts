import * as assert from "assert";
import * as vscode from "vscode";
import { createHash } from "crypto";
import { readFileSync } from "fs";
import { DiagramView } from "../../webviews/DiagramView";

suite("DiagramView - CSP + local mermaid", () => {
  test("generated HTML has a Content-Security-Policy meta and no remote script src", () => {
    const panel = vscode.window.createWebviewPanel(
      "gxDiagramTest",
      "Diagram Test",
      vscode.ViewColumn.Beside,
      { enableScripts: true },
    );

    try {
      const extensionUri = vscode.Uri.joinPath(vscode.Uri.file(__dirname), "..", "..", "..");
      const asset = vscode.Uri.joinPath(extensionUri, "resources", "mermaid.min.js");
      const hash = createHash("sha256").update(readFileSync(asset.fsPath)).digest("hex");
      assert.strictEqual(hash, "336df803d87a38b70a01e736e666c8f7601d2ce42f5e47c8fafe5d721074b9a3");
      const html = DiagramView.buildHtml(panel.webview, extensionUri, "graph TD; A-->B;");

      assert.ok(
        /Content-Security-Policy/.test(html),
        "expected a Content-Security-Policy meta tag in the generated HTML",
      );
      assert.ok(!/cdn\.jsdelivr\.net/.test(html), "expected no jsdelivr CDN reference");
      assert.ok(/mermaid\.min\.js/.test(html), "expected the vendored local mermaid asset to be referenced");
      assert.ok(/nonce-/.test(html), "expected a per-render CSP nonce");
      assert.ok(/graph TD; A--&gt;B;/.test(html), "expected the mermaid diagram source to be inlined (HTML-escaped)");
    } finally {
      panel.dispose();
    }
  });
});
