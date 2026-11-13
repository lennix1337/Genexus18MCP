import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { formatMcpErrorMessage } from "../utils/McpErrorFormatter";
import { escapeHtml } from "../utils/htmlEscape";
import { createNonce, messagePage, scriptNonceCsp } from "../utils/webviewHtml";

/**
 * Validates a message posted by the properties webview. Returns the property
 * update to forward, or undefined if the message is malformed or names a
 * property the panel was not shown.
 */
export function parseSetPropertyMessage(
  message: unknown,
  knownProperties: ReadonlySet<string>,
): { name: string; value: string } | undefined {
  if (typeof message !== "object" || message === null) return undefined;
  const m = message as { command?: unknown; name?: unknown; value?: unknown };
  if (m.command !== "setProperty") return undefined;
  if (typeof m.name !== "string" || !knownProperties.has(m.name)) return undefined;
  if (typeof m.value !== "string") return undefined;
  return { name: m.name, value: m.value };
}

export class PropertiesView {
  private static currentPanel: vscode.WebviewPanel | undefined;
  private static messageSubscription: vscode.Disposable | undefined;

  static async show(target: string, controlName: string | null, provider: GxFileSystemProvider) {
    const column = vscode.window.activeTextEditor ? vscode.window.activeTextEditor.viewColumn : undefined;

    if (this.currentPanel) {
      this.currentPanel.reveal(column);
    } else {
      this.currentPanel = vscode.window.createWebviewPanel(
        "gxProperties",
        "GX Properties",
        vscode.ViewColumn.Three,
        { enableScripts: true, localResourceRoots: [] }
      );
      this.currentPanel.onDidDispose(() => {
        this.currentPanel = undefined;
        this.messageSubscription?.dispose();
        this.messageSubscription = undefined;
      });
    }

    this.currentPanel.webview.html = this.getHtml(
      target,
      controlName,
      this.currentPanel.webview.cspSource,
    );

    try {
      const result = await provider.callMcpTool("genexus_properties", {
        action: "get",
        name: target,
        control: controlName
      });
      if (result && !result.error) {
        const properties: unknown[] = Array.isArray(result.properties) ? result.properties : [];
        const knownProperties = new Set<string>();
        for (const p of properties) {
          const n = (p as { name?: unknown } | null)?.name;
          if (typeof n === "string") knownProperties.add(n);
        }
        this.currentPanel.webview.postMessage({ type: "update", properties });

        // One handler per show(): the panel is reused, so drop the previous
        // handler (bound to a previous target) before registering this one.
        this.messageSubscription?.dispose();
        this.messageSubscription = this.currentPanel.webview.onDidReceiveMessage(async (message) => {
          const update = parseSetPropertyMessage(message, knownProperties);
          if (!update) return;
          try {
            const setResult = await provider.callMcpTool("genexus_properties", {
              action: "set",
              name: target,
              propertyName: update.name,
              value: update.value,
              control: controlName
            });
            if (setResult?.error || setResult?.status === "Error") {
              throw setResult;
            }
          } catch (e) {
            vscode.window.showErrorMessage(formatMcpErrorMessage("Failed to set property:", e));
          }
        });
      } else {
        this.currentPanel.webview.html = messagePage(formatMcpErrorMessage("Properties Error:", result));
      }
    } catch (e) {
      this.currentPanel.webview.html = messagePage(formatMcpErrorMessage("Critical Error:", e));
    }
  }

  /** Exported for testing. */
  static getHtml(target: string, controlName: string | null, cspSource: string, nonce = createNonce()): string {
    return `
      <!DOCTYPE html>
      <html>
      <head>
        <meta http-equiv="Content-Security-Policy" content="${scriptNonceCsp(cspSource, nonce)}">
        <style>
          body { font-family: sans-serif; padding: 10px; background-color: var(--vscode-editor-background); color: var(--vscode-editor-foreground); }
          .prop-row { display: flex; padding: 4px 0; border-bottom: 1px solid #333; align-items: center; }
          .prop-name { flex: 1; font-weight: bold; font-size: 11px; }
          .prop-value { flex: 1; }
          input, select { width: 100%; background: #333; color: white; border: 1px solid #555; font-size: 11px; }
        </style>
      </head>
      <body>
        <h3>${escapeHtml(controlName || target)}</h3>
        <div id="props">Loading...</div>
        <script nonce="${nonce}">
          const vscode = acquireVsCodeApi();
          window.addEventListener('message', event => {
            const data = event.data;
            if (data.type === 'update') {
              const container = document.getElementById('props');
              container.innerHTML = '';
              data.properties.forEach(p => {
                const row = document.createElement('div');
                row.className = 'prop-row';
                
                const name = document.createElement('div');
                name.className = 'prop-name';
                name.innerText = p.name;
                
                const valContainer = document.createElement('div');
                valContainer.className = 'prop-value';
                
                if (p.options) {
                  const select = document.createElement('select');
                  p.options.forEach(opt => {
                    const o = document.createElement('option');
                    o.value = opt;
                    o.innerText = opt;
                    if (opt === p.value) o.selected = true;
                    select.appendChild(o);
                  });
                  select.addEventListener('change', () => vscode.postMessage({ command: 'setProperty', name: p.name, value: select.value }));
                  valContainer.appendChild(select);
                } else {
                  const input = document.createElement('input');
                  input.value = p.value;
                  input.addEventListener('blur', () => vscode.postMessage({ command: 'setProperty', name: p.name, value: input.value }));
                  valContainer.appendChild(input);
                }
                
                row.appendChild(name);
                row.appendChild(valContainer);
                container.appendChild(row);
              });
            }
          });
        </script>
      </body>
      </html>
    `;
  }
}
