import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxUriParser } from "../utils/GxUriParser";
import { escapeHtml } from "../utils/htmlEscape";
import { createNonce, scriptNonceCsp } from "../utils/webviewHtml";

export class IndexView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(uri: vscode.Uri, provider: GxFileSystemProvider) {
    const info = GxUriParser.parse(uri);
    if (!info) return;

    const { name: objName, type: typeStr } = info;
    const uriKey = uri.toString() + ":VisualIndexes";
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    if (this.panels.has(uriKey)) {
      this.panels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxVisualIndexes",
      `${objName} - Indexes`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, localResourceRoots: [] }
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => this.panels.delete(uriKey));

    panel.webview.html = this.getHtml(objName, panel.webview.cspSource);

    try {
      const result = await provider.callMcpTool("genexus_structure", {
        action: "get_indexes",
        name: target,
      });
      if (result && !result.error) {
        panel.webview.postMessage({ type: "update", data: result });
      } else {
        panel.webview.postMessage({
          type: "error",
          message: result?.error || "Unknown error",
        });
      }
    } catch (e) {
      panel.webview.postMessage({ type: "error", message: String(e) });
    }
  }

  /** Exported for testing. */
  static getHtml(objName: string, cspSource: string, nonce = createNonce()): string {
    return `
      <!DOCTYPE html>
      <html>
      <head>
        <meta http-equiv="Content-Security-Policy" content="${scriptNonceCsp(cspSource, nonce)}">
        <style>
          :root {
            --bg: var(--vscode-editor-background);
            --fg: var(--vscode-editor-foreground);
            --header-bg: var(--vscode-editorWidget-background);
            --border: var(--vscode-panel-border);
            --hover-bg: var(--vscode-list-hoverBackground);
            --accent: var(--vscode-focusBorder);
          }
          body { font-family: var(--vscode-font-family); padding: 0; margin: 0; background-color: var(--bg); font-size: 13px; color: var(--fg); overflow: hidden; }
          .toolbar { background: var(--header-bg); padding: 10px 15px; border-bottom: 1px solid var(--border); font-weight: 600; }
          
          .table-container { overflow: auto; height: calc(100vh - 40px); }
          .tree-table { width: 100%; border-collapse: collapse; }
          .tree-table th { 
            text-align: left; background: var(--header-bg); padding: 10px; 
            border-bottom: 1px solid var(--border); border-right: 1px solid var(--border);
            font-weight: 600; position: sticky; top: 0; z-index: 10;
          }
          .tree-table td { padding: 10px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border); vertical-align: top; }
          .tree-table tr:hover { background-color: var(--hover-bg); }
          
          .index-name { font-weight: bold; display: flex; align-items: center; gap: 8px; }
          .primary-key { color: #d1b100; }
          .attr-list { margin: 0; padding: 0; list-style: none; }
          .attr-item { padding: 4px 0; border-bottom: 1px solid var(--border); display: flex; justify-content: space-between; }
          .attr-item:last-child { border-bottom: none; }
          .order-tag { font-size: 10px; opacity: 0.6; background: var(--hover-bg); padding: 1px 4px; border-radius: 2px; }
          .prop-tag { display: inline-block; font-size: 11px; padding: 2px 6px; border-radius: 10px; background: var(--accent); color: #fff; margin-right: 4px; }
        </style>
      </head>
      <body>
        <div class="toolbar">Indexes for ${escapeHtml(objName)}</div>
        <div class="table-container">
          <div id="content">Loading Indexes...</div>
        </div>

        <script nonce="${nonce}">
          function esc(v) {
            return String(v == null ? '' : v)
              .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
          }

          window.addEventListener('message', event => {
            const data = event.data;
            if (data.type === 'update') {
              renderIndexes(data.data);
            } else if (data.type === 'error') {
              document.getElementById('content').innerHTML = '<h2 style="color:#f44; padding: 20px;">Error: ' + esc(data.message) + '</h2>';
            }
          });

          const icons = {
            key: '<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M11.5 1a3.49 3.49 0 0 0-3.3 2.33l-.1.3L1 11.06V15h3.94l1.37-1.37.13-.53-.53-.13L5 13.06V12h1v-1H5v-1h1V9h1v1h1.06l3.11-3.11.3-.1A3.5 3.5 0 1 0 11.5 1zm0 5a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z"/></svg>',
            file: '<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path fill-rule="evenodd" clip-rule="evenodd" d="M13.71 4.29l-3-3L10 1H4l-.5.5v13l.5.5h8l.5-.5V5l-.29-.71zM10 5V2.41L12.59 5H10zM4 14V2h5v4h4v8H4z"/></svg>'
          };

          function renderIndexes(data) {
            if (!data.indexes || data.indexes.length === 0) {
              document.getElementById('content').innerHTML = '<p style="padding: 20px;">No indexes found.</p>';
              return;
            }

            let html = '<table class="tree-table"><thead><tr>';
            html += '<th style="width: 30%">Name</th><th style="width: 40%">Attributes</th><th>Properties</th>';
            html += '</tr></thead><tbody>';

            data.indexes.forEach(idx => {
              const rowClass = idx.isPrimary ? 'primary-key' : '';
              html += '<tr>';
              html += '<td class="index-name ' + rowClass + '">' + (idx.isPrimary ? icons.key : icons.file) + ' ' + esc(idx.name) + '</td>';

              html += '<td><ul class="attr-list">';
              idx.attributes.forEach(attr => {
                html += '<li class="attr-item"><span>' + esc(attr.name) + '</span> <span class="order-tag">' + (attr.isAscending ? 'ASC' : 'DESC') + '</span></li>';
              });
              html += '</ul></td>';

              html += '<td>';
              if (idx.isPrimary) html += '<span class="prop-tag">Primary</span>';
              if (idx.isUnique) html += '<span class="prop-tag" style="background: #2da44e">Unique</span>';
              html += '</td>';

              html += '</tr>';
            });

            html += '</tbody></table>';
            document.getElementById('content').innerHTML = html;
          }
        </script>
      </body>
      </html>
    `;
  }
}
