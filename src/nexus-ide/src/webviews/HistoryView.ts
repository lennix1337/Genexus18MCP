import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { formatMcpErrorMessage } from "../utils/McpErrorFormatter";
import { GxUriParser } from "../utils/GxUriParser";
import { escapeHtml } from "../utils/htmlEscape";
import { createNonce, messagePage, scriptNonceCsp } from "../utils/webviewHtml";

/**
 * Validates a message posted by the history webview. Returns the revision to
 * diff, or undefined for anything that is not a well-formed viewDiff request
 * for the object this panel was opened for.
 */
export function parseViewDiffMessage(message: unknown, panelObjName: string): number | undefined {
  if (typeof message !== "object" || message === null) return undefined;
  const m = message as { command?: unknown; versionId?: unknown; objName?: unknown };
  if (m.command !== "viewDiff" || m.objName !== panelObjName) return undefined;
  if (typeof m.versionId !== "number" || !Number.isSafeInteger(m.versionId) || m.versionId < 0) {
    return undefined;
  }
  return m.versionId;
}

export class HistoryView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(
    uri: vscode.Uri | undefined,
    provider: GxFileSystemProvider,
    historyProvider: any,
  ) {
    const targetUri = uri || GxUriParser.getActiveGxUri();
    const info = targetUri ? GxUriParser.parse(targetUri) : null;
    const objName = info?.name || "";

    if (!objName) {
      vscode.window.showErrorMessage(
        "Abra ou selecione um objeto para ver o historico.",
      );
      return;
    }

    const uriKey = targetUri?.toString() || objName;
    if (this.panels.has(uriKey)) {
      this.panels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxHistory",
      `Historico: ${objName}`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, localResourceRoots: [] },
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => {
      this.panels.delete(uriKey);
      historyProvider.clear(objName);
    });

    panel.webview.html = messagePage(`Carregando historico de ${objName}...`);

    try {
      const result = await provider.callMcpTool("genexus_history", {
        action: "list",
        name: objName,
      });

      if (result && result.history) {
        let rows = "";
        if (Array.isArray(result.history)) {
          const sortedHistory = [...result.history].reverse();
          rows = sortedHistory
            .map(
              (rev: any) => `
                <tr>
                    <td style="padding: 10px; border-bottom: 1px solid #333; font-weight: bold; color: #007acc;">#${escapeHtml(rev.version || rev.Id || "")}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333; white-space: nowrap;">${escapeHtml(rev.date || rev.Date || "")}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333;">${escapeHtml(rev.user || rev.User || "")}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333; font-style: italic; color: #aaa;">${rev.comment || rev.Comment ? escapeHtml(rev.comment || rev.Comment) : '<span style="opacity: 0.5;">Sem comentario</span>'}</td>
                    <td style="padding: 10px; border-bottom: 1px solid #333; text-align: center;">
                        <button class="diff-btn" data-version="${Number(rev.version ?? rev.Id) || 0}" style="background: #007acc; color: white; border: none; padding: 4px 8px; cursor: pointer; border-radius: 2px;">Comparar (Diff)</button>
                    </td>
                </tr>
            `,
            )
            .join("");
        }

        panel.webview.html = this.getHtml(objName, rows, panel.webview.cspSource);

        panel.webview.onDidReceiveMessage(async (message) => {
          const versionId = parseViewDiffMessage(message, objName);
          if (versionId !== undefined) {
            vscode.window.setStatusBarMessage(
              `$(sync~spin) Buscando revisao #${versionId} para diff...`,
              3000,
            );
            try {
              const codeResult = await provider.callMcpTool("genexus_history", {
                action: "get_source",
                name: objName,
                versionId,
              });

              if (codeResult && codeResult.source) {
                const historicalContent = Buffer.from(
                  codeResult.source,
                  "base64",
                ).toString("utf8");
                const historyUri = vscode.Uri.parse(
                  `gx-history:/v${versionId}/${objName}.gx`,
                );
                historyProvider.update(historyUri, historicalContent);

                await vscode.commands.executeCommand(
                  "vscode.diff",
                  historyUri,
                  targetUri!,
                  `${objName} (Revisao #${versionId}) <-> (Atual)`,
                );
              } else {
                vscode.window.showErrorMessage(
                  formatMcpErrorMessage(
                    "Historico:",
                    codeResult ||
                      "A revisao selecionada nao retornou codigo-fonte para diff.",
                  ),
                );
              }
            } catch (e) {
              vscode.window.showErrorMessage(
                formatMcpErrorMessage("Erro ao buscar versao:", e),
              );
            }
          }
        });
      } else {
        panel.webview.html = messagePage(
          formatMcpErrorMessage("Historico:", result || "Historico nao encontrado."),
        );
      }
    } catch (e) {
      panel.webview.html = messagePage(formatMcpErrorMessage("Erro critico:", e));
    }
  }

  /** Exported for testing. */
  static getHtml(objName: string, rows: string, cspSource: string, nonce = createNonce()): string {
    return `
      <!DOCTYPE html>
      <html>
      <head>
          <meta http-equiv="Content-Security-Policy" content="${scriptNonceCsp(cspSource, nonce)}">
          <style>
              body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; padding: 20px; background-color: #1e1e1e; color: #ccc; }
              table { width: 100%; border-collapse: collapse; margin-top: 20px; background: #252526; box-shadow: 0 4px 6px rgba(0,0,0,0.3); border-radius: 4px; overflow: hidden; }
              th { background-color: #333333; color: #fff; text-align: left; padding: 12px 10px; font-size: 0.9em; text-transform: uppercase; letter-spacing: 1px; }
              tr:hover { background-color: #2d2d2d; }
              h2 { color: #fff; border-bottom: 1px solid #444; padding-bottom: 10px; }
              .badge { background: #007acc; color: white; padding: 2px 6px; border-radius: 3px; font-size: 0.8em; margin-left: 10px; }
          </style>
      </head>
      <body>
          <h2>Historico de revisoes: ${escapeHtml(objName)} <span class="badge">SDK Nativo</span></h2>
          <table>
              <thead>
                  <tr>
                      <th>Rev</th>
                      <th>Data / Hora</th>
                      <th>Autor</th>
                      <th>Comentario de Commit</th>
                      <th>Acoes</th>
                  </tr>
              </thead>
              <tbody>${rows || '<tr><td colspan="5" style="padding: 20px; text-align: center;">Nenhuma revisao encontrada.</td></tr>'}</tbody>
          </table>
          <script nonce="${nonce}">
              const vscode = acquireVsCodeApi();
              const objName = ${JSON.stringify(objName).replace(/</g, "\\u003c")};
              document.addEventListener('click', (event) => {
                  const btn = event.target.closest && event.target.closest('.diff-btn');
                  if (!btn) return;
                  vscode.postMessage({ command: 'viewDiff', versionId: Number(btn.dataset.version), objName });
              });
          </script>
      </body>
      </html>
    `;
  }
}
