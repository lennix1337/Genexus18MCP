import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxTreeItem } from "../gxTreeProvider";
import { GxUriParser } from "../utils/GxUriParser";
import { escapeHtml } from "../utils/htmlEscape";
import { createNonce, messagePage } from "../utils/webviewHtml";

// Vendored from npm `mermaid@12.3.0` into resources/mermaid.min.js; the
// webview never fetches a script from a CDN. The devDependency pin supports
// re-vendoring only; runtime uses the vendored file. To update, npm pack the
// pinned version, verify the tarball SRI and dist/mermaid.min.js SHA-256 against
// vendor/mermaid.vendor.json, then copy the bundle and update the manifest.
const MERMAID_ASSET = "mermaid.min.js";

export class DiagramView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  /**
   * Builds the CSP-hardened HTML for the diagram webview. Exported for testing.
   * The source is HTML-escaped: mermaid reads the <pre> innerHTML and entity-decodes
   * it, so the diagram text round-trips unchanged.
   */
  static buildHtml(
    webview: vscode.Webview,
    extensionUri: vscode.Uri,
    mermaidSource: string,
  ): string {
    const mermaidUri = webview.asWebviewUri(
      vscode.Uri.joinPath(extensionUri, "resources", MERMAID_ASSET),
    );
    const scriptNonce = createNonce();
    return `
            <!DOCTYPE html>
            <html>
            <head>
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data:; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${scriptNonce}' ${webview.cspSource};">
                <script nonce="${scriptNonce}" src="${mermaidUri}"></script>
                <script nonce="${scriptNonce}">mermaid.initialize({ startOnLoad: true, layout: 'dagre', theme: 'default', look: 'classic' });</script>
            </head>
            <body>
                <pre class="mermaid">
                    ${escapeHtml(mermaidSource)}
                </pre>
            </body>
            </html>
        `;
  }

  static async show(
    item: GxTreeItem | undefined,
    provider: GxFileSystemProvider,
    extensionUri: vscode.Uri,
  ) {
    let objName = "";
    if (item && item.gxName) {
      objName = item.gxName;
    } else {
      const editor = vscode.window.activeTextEditor;
      let targetUri = editor?.document.uri;
      if (!targetUri || !GxUriParser.isGeneXusUri(targetUri)) {
        const visibleGxEditor = vscode.window.visibleTextEditors.find(
          (e) => GxUriParser.isGeneXusUri(e.document.uri)
        );
        if (visibleGxEditor) targetUri = visibleGxEditor.document.uri;
      }
      
      if (targetUri && GxUriParser.isGeneXusUri(targetUri)) {
        objName = GxUriParser.getObjectName(targetUri);
      }
    }

    if (!objName) {
      vscode.window.showErrorMessage("Selecione um objeto para gerar o diagrama.");
      return;
    }

    if (this.panels.has(objName)) {
        this.panels.get(objName)!.reveal(vscode.ViewColumn.Beside);
        return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxDiagram",
      `${objName} Diagram`,
      vscode.ViewColumn.Beside,
      {
        enableScripts: true,
        localResourceRoots: [vscode.Uri.joinPath(extensionUri, "resources")],
      }
    );

    this.panels.set(objName, panel);
    panel.onDidDispose(() => this.panels.delete(objName));

    panel.webview.html = messagePage(`Gerando Diagrama para ${objName}...`);

    try {
      const result = await provider.callMcpTool("genexus_doc", {
        action: "visualize",
        target: objName,
      });

      if (result && result.mermaid) {
        panel.webview.html = DiagramView.buildHtml(panel.webview, extensionUri, result.mermaid);
      } else {
        panel.webview.html = messagePage("Não foi possível gerar o diagrama para este objeto.");
      }
    } catch (e) {
      panel.webview.html = messagePage(`Erro: ${e}`);
    }
  }
}
