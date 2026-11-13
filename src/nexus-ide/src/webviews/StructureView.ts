import * as vscode from "vscode";
import { GxFileSystemProvider } from "../gxFileSystem";
import { GxUriParser } from "../utils/GxUriParser";
import { createNonce, messagePage, scriptNonceCsp } from "../utils/webviewHtml";

function isStructureItem(item: unknown): boolean {
  if (typeof item !== "object" || item === null) return false;
  const { name, children } = item as { name?: unknown; children?: unknown };
  if (typeof name !== "string") return false;
  return children === undefined || (Array.isArray(children) && children.every(isStructureItem));
}

/**
 * Validates a message posted by the structure webview. Returns the structure to
 * save, or undefined if the message is not a well-formed save request (a tree
 * of items with string names under a top-level "children" array).
 */
export function parseSaveMessage(message: unknown): { children: unknown[] } | undefined {
  if (typeof message !== "object" || message === null) return undefined;
  const m = message as { command?: unknown; structure?: unknown };
  if (m.command !== "save") return undefined;
  const structure = m.structure as { children?: unknown } | null | undefined;
  if (typeof structure !== "object" || structure === null) return undefined;
  if (!Array.isArray(structure.children) || !structure.children.every(isStructureItem)) {
    return undefined;
  }
  return structure as { children: unknown[] };
}

export class StructureView {
  private static panels = new Map<string, vscode.WebviewPanel>();

  static async show(uri: vscode.Uri, provider: GxFileSystemProvider) {
    const info = GxUriParser.parse(uri);
    if (!info) return;

    const { name: objName, type: typeStr } = info;
    const uriKey = uri.toString() + ":VisualStructure";
    const target = typeStr ? `${typeStr}:${objName}` : objName;

    // Apenas Table é Read-Only. Transaction permite edição.
    const isReadOnly = typeStr === "Table";

    if (this.panels.has(uriKey)) {
      this.panels.get(uriKey)!.reveal(vscode.ViewColumn.Beside);
      return;
    }

    const panel = vscode.window.createWebviewPanel(
      "gxVisualStructure",
      `${objName} - Structure`,
      vscode.ViewColumn.Beside,
      { enableScripts: true, localResourceRoots: [] }
    );

    this.panels.set(uriKey, panel);
    panel.onDidDispose(() => this.panels.delete(uriKey));

    panel.webview.html = this.getHtml(objName, isReadOnly, panel.webview.cspSource);

    try {
      const result = await provider.callMcpTool("genexus_structure", {
        action: "get_visual",
        name: target,
      });
      if (result && !result.error) {
        panel.webview.postMessage({ type: "update", structure: result });
      } else {
        panel.webview.html = messagePage(`Error: ${result?.error || "Unknown error"}`);
      }

      panel.webview.onDidReceiveMessage(async (message) => {
        const structure = isReadOnly ? undefined : parseSaveMessage(message);
        if (structure) {
          try {
            const saveResult = await provider.callMcpTool(
              "genexus_structure",
              {
                action: "update_visual",
                name: target,
                payload: structure,
              },
              180000
            );
            if (saveResult && saveResult.status === "Success") {
              panel.webview.postMessage({ type: "success" });
              vscode.window.setStatusBarMessage(`$(check) Structure saved for ${objName}`, 3000);
            } else {
              panel.webview.postMessage({
                type: "error",
                message: saveResult?.error || "Save failed",
              });
            }
          } catch (e) {
            panel.webview.postMessage({ type: "error", message: String(e) });
          }
        }
      });
    } catch (e) {
      panel.webview.html = messagePage(`Critical Error: ${String(e)}`);
    }
  }

  /** Exported for testing. */
  static getHtml(objName: string, isReadOnly: boolean, cspSource: string, nonce = createNonce()): string {
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
            --input-bg: var(--vscode-input-background);
            --input-fg: var(--vscode-input-foreground);
            --tree-indent: 20px;
          }
          body { font-family: var(--vscode-font-family); padding: 0; margin: 0; background-color: var(--bg); font-size: 13px; color: var(--fg); overflow: hidden; }
          .toolbar { 
            background: var(--header-bg); padding: 8px 12px; border-bottom: 1px solid var(--border); 
            display: flex; gap: 8px; align-items: center; 
          }
          .toolbar button { 
            background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground);
            border: none; padding: 4px 10px; cursor: pointer; border-radius: 2px; 
            display: flex; align-items: center; gap: 6px; font-size: 12px;
          }
          .toolbar button:hover { background: var(--vscode-button-secondaryHoverBackground); }
          .toolbar button.primary { background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
          .toolbar button.primary:hover { background: var(--vscode-button-hoverBackground); }
          
          .search-container { position: relative; flex: 1; max-width: 300px; }
          .search-input {
            width: 100%; background: var(--input-bg); color: var(--input-fg); border: 1px solid var(--border);
            padding: 4px 8px; border-radius: 2px; outline: none; font-size: 12px;
          }
          .search-input:focus { border-color: var(--accent); }

          .table-container { overflow: auto; height: calc(100vh - 42px); }
          .tree-table { width: 100%; border-collapse: collapse; table-layout: fixed; }
          .tree-table th { 
            text-align: left; background: var(--header-bg); padding: 8px 10px; 
            border-bottom: 1px solid var(--border); border-right: 1px solid var(--border);
            font-weight: 600; color: var(--fg); position: sticky; top: 0; z-index: 20; 
          }
          .tree-table td { 
            padding: 4px 10px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border);
            vertical-align: middle; white-space: nowrap; outline: none; overflow: hidden; text-overflow: ellipsis;
          }
          .tree-table tr:hover { background-color: var(--hover-bg); }
          .tree-table tr.selected { background-color: var(--vscode-list-activeSelectionBackground); color: var(--vscode-list-activeSelectionForeground); }
          
          .item-name-cell { display: flex; align-items: center; position: relative; }
          .indent { display: inline-block; width: var(--tree-indent); flex-shrink: 0; }
          .icon { margin-right: 6px; width: 16px; height: 16px; flex-shrink: 0; display: flex; align-items: center; justify-content: center; }
          
          .key-icon { color: #d1b100; cursor: pointer; }
          .attr-icon { color: #51a1ff; }
          .level-icon { color: #cccccc; }
          
          .level-row { font-weight: 600; background: var(--vscode-sideBar-background); }
          .formula-text { color: #4ec9b0; font-style: italic; }
          
          .actions-cell { width: 30px; text-align: center; border-right: none !important; }
          .btn-del { 
            cursor: pointer; color: var(--fg); opacity: 0.4; font-size: 16px; border: none; 
            background: transparent; padding: 2px; border-radius: 2px;
          }
          .btn-del:hover { opacity: 1; background: var(--vscode-toolbar-hoverBackground); color: #f44; }
          
          #status { margin-left: auto; font-size: 11px; opacity: 0.8; }
          #status.error { color: #f44; }
          #status.success { color: #4ec9b0; }
          
          .editable { cursor: text; transition: background 0.1s; }
          .editable:focus { background: var(--input-bg) !important; box-shadow: inset 0 0 0 1px var(--accent); }
          
          select.nullable-select { 
             width: 100%; border: none; background: transparent; font-family: inherit; font-size: inherit; color: inherit; appearance: none;
             cursor: pointer; outline: none;
          }
          .type-input {
            width: 100%; border: none; background: transparent; font-family: inherit; font-size: inherit; color: inherit; outline: none;
          }
          .readonly-badge { 
            background: var(--vscode-badge-background); color: var(--vscode-badge-foreground);
            padding: 2px 6px; border-radius: 10px; font-size: 10px; margin-left: 10px;
          }
        </style>
      </head>
      <body>
        <div class="toolbar">
          ${
            !isReadOnly
              ? '<button id="btn-add-attr"><span>➕</span> Attr</button><button id="btn-add-level"><span>📁</span> Level</button>'
              : '<span class="readonly-badge">READ-ONLY (Edit in Transaction)</span>'
          }
          <div class="search-container">
            <input type="text" class="search-input" placeholder="Filter..." id="filter-input">
          </div>
          ${!isReadOnly ? '<button class="primary" id="btn-save"><span>💾</span> Save</button>' : ""}
          <div id="status">Ready</div>
        </div>
        
        <datalist id="gx-types">
          <option value="NUMERIC"></option><option value="CHARACTER"></option><option value="VARCHAR"></option>
          <option value="LONGVARCHAR"></option><option value="DATE"></option><option value="DATETIME"></option>
          <option value="BOOLEAN"></option><option value="GUID"></option><option value="IMAGE"></option>
          <option value="BLOB"></option><option value="VIDEO"></option><option value="AUDIO"></option>
        </datalist>

        <div class="table-container">
          <div id="content">Loading Structure...</div>
        </div>

        <script nonce="${nonce}">
          const vscode = acquireVsCodeApi();
          const isReadOnly = ${isReadOnly};
          let currentData = null;
          let filterText = "";

          function esc(v) {
            return String(v == null ? '' : v)
              .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
          }
          
          window.onerror = function(msg, url, line, col, error) {
            const errStr = msg + " at " + line + ":" + col;
            document.getElementById('status').innerText = "JS Error: " + errStr;
            document.getElementById('status').className = 'error';
            console.error(errStr, error);
            return false;
          };

          function setStatus(text, type = '') {
            const el = document.getElementById('status');
            el.innerText = text;
            el.className = type;
          }

          window.addEventListener('message', event => {
            const data = event.data;
            if (data.type === 'update') {
              currentData = data.structure;
              renderStructure();
              setStatus('Loaded', 'success');
            } else if (data.type === 'error') {
              setStatus('Error', 'error');
              console.error('Error:', data.message);
            } else if (data.type === 'success') {
              setStatus('Saved', 'success');
            }
          });

          // Handlers are wired here (the CSP forbids inline onclick/oninput/...).
          // Row events are delegated because the table is re-rendered on change.
          const contentEl = document.getElementById('content');
          function rowIdOf(el) {
            const row = el.closest('tr[data-id]');
            return row ? row.dataset.id : null;
          }
          function isFormControl(el) {
            return el.tagName === 'INPUT' || el.tagName === 'SELECT';
          }
          contentEl.addEventListener('click', event => {
            const el = event.target.closest('[data-action]');
            const id = el ? rowIdOf(el) : null;
            if (id === null) return;
            if (el.dataset.action === 'toggle-key') toggleKey(id);
            else if (el.dataset.action === 'delete-row') deleteRow(id);
          });
          // contenteditable cells: focusout bubbles (blur does not).
          contentEl.addEventListener('focusout', event => {
            const el = event.target;
            if (!el.dataset || !el.dataset.field || isFormControl(el)) return;
            const id = rowIdOf(el);
            if (id !== null) updateLocalData(id, el.dataset.field, el.innerText);
          });
          contentEl.addEventListener('change', event => {
            const el = event.target;
            if (!el.dataset || !el.dataset.field || !isFormControl(el)) return;
            const id = rowIdOf(el);
            if (id !== null) updateLocalData(id, el.dataset.field, el.value);
          });
          const addAttrBtn = document.getElementById('btn-add-attr');
          if (addAttrBtn) addAttrBtn.addEventListener('click', () => addRow(false));
          const addLevelBtn = document.getElementById('btn-add-level');
          if (addLevelBtn) addLevelBtn.addEventListener('click', () => addRow(true));
          const saveBtn = document.getElementById('btn-save');
          if (saveBtn) saveBtn.addEventListener('click', requestSave);
          document.getElementById('filter-input').addEventListener('input', event => filterRows(event.target.value));

          function filterRows(text) {
            filterText = text.toLowerCase();
            renderStructure();
          }

          const icons = {
            key: '<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M11.5 1a3.49 3.49 0 0 0-3.3 2.33l-.1.3L1 11.06V15h3.94l1.37-1.37.13-.53-.53-.13L5 13.06V12h1v-1H5v-1h1V9h1v1h1.06l3.11-3.11.3-.1A3.5 3.5 0 1 0 11.5 1zm0 5a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z"/></svg>',
            attr: '<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M1 2v12h14V2H1zm13 11H2V3h12v10zM4 5h8v1H4V5zm0 3h8v1H4V8zm0 3h5v1H4v-1z"/></svg>',
            level: '<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><path d="M14.5 3H7.71l-2-2H1.5l-.5.5v11l.5.5h13l.5-.5v-9l-.5-.5zM14 12H2V2h3.29l2 2H14v8z"/></svg>'
          };

          function renderStructure() {
            if (!currentData) return;
            if (!currentData.children) {
              document.getElementById('content').innerHTML = "<h1>No structure data available.</h1>";
              return;
            }
            let html = '<table class="tree-table"><thead><tr>';
            html += '<th style="width: 40%">Name</th><th style="width: 20%">Type</th><th style="width: 20%">Description</th><th style="width: 15%">Formula</th><th style="width: 80px">Null</th><th class="actions-cell"></th>';
            html += '</tr></thead><tbody>';
            
            function renderLevel(items, levelNum, parentId) {
              items.forEach((item, index) => {
                const id = (parentId ? parentId + '-' : '') + index;
                if (filterText && !item.name.toLowerCase().includes(filterText)) {
                   if (item.children) renderLevel(item.children, levelNum + 1, id);
                   return;
                }

                const indentSpace = '<span class="indent"></span>'.repeat(levelNum);
                const rowClass = item.isLevel ? 'level-row' : '';
                
                html += '<tr class="' + rowClass + '" data-id="' + id + '">';
                
                let iconHtml = item.isLevel ? icons.level : (item.isKey ? icons.key : icons.attr);
                let iconClass = item.isLevel ? 'level-icon' : (item.isKey ? 'key-icon' : 'attr-icon');

                const nameEditable = !isReadOnly ? 'contenteditable="true"' : '';
                html += \`<td class="item-name-cell">\${indentSpace}<span class="icon \${iconClass}" data-action="toggle-key">\${iconHtml}</span><span class="editable" data-field="name" \${nameEditable}>\${esc(item.name)}</span></td>\`;

                const typeDisabled = isReadOnly || item.isLevel ? "readonly" : "";
                html += \`<td style="position: relative"><input type="text" list="gx-types" class="type-input" value="\${esc(item.type || '')}" data-field="type"\${typeDisabled} autocomplete="off"/></td>\`;

                const descEditable = !isReadOnly && !item.isLevel;
                html += '<td class="editable" data-field="description" contenteditable="' + descEditable + '">' + esc(item.description || '') + '</td>';

                const formulaEditable = !isReadOnly && !item.isLevel;
                html += '<td class="editable formula-text" data-field="formula" contenteditable="' + formulaEditable + '">' + esc(item.formula || '') + '</td>';
                
                if (item.isLevel) {
                  html += '<td></td>';
                } else {
                  const val = item.nullable || 'No';
                  html += '<td><select class="nullable-select" ' + (isReadOnly ? 'disabled' : '') + ' data-field="nullable">' +
                      '<option value="No" ' + (val === 'No' ? 'selected' : '') + '>No</option>' +
                      '<option value="Yes" ' + (val === 'Yes' ? 'selected' : '') + '>Yes</option>' +
                      '<option value="Managed" ' + (val === 'Managed' || val === 'Compatible' ? 'selected' : '') + '>Mng</option>' +
                    '</select></td>';
                }
                
                html += '<td class="actions-cell">' + (!isReadOnly ? '<button class="btn-del" data-action="delete-row">×</button>' : '') + '</td>';
                html += '</tr>';
                
                if (item.children && item.children.length > 0) {
                  renderLevel(item.children, levelNum + 1, id);
                }
              });
            }

            renderLevel(currentData.children, 0, "");
            html += '</tbody></table>';
            document.getElementById('content').innerHTML = html;
          }

          function getItemById(id) {
            const parts = id.split('-').map(Number);
            let current = currentData.children;
            let target = null;
            for (let i = 0; i < parts.length; i++) {
              target = current[parts[i]];
              if (i < parts.length - 1) current = target.children;
            }
            return target;
          }

          function updateLocalData(id, field, value) {
            if (isReadOnly) return;
            const item = getItemById(id);
            if (item) item[field] = value;
          }

          function toggleKey(id) {
            if (isReadOnly) return;
            const item = getItemById(id);
            if (item && !item.isLevel) {
              item.isKey = !item.isKey;
              renderStructure();
            }
          }

          function addRow(isLevel) {
            if (isReadOnly) return;
            const newItem = {
              name: isLevel ? 'NewLevel' : 'NewAttribute',
              isLevel: isLevel, isKey: false,
              type: isLevel ? '' : 'Character(20)',
              description: '', formula: '', nullable: 'No', children: []
            };
            currentData.children.push(newItem);
            renderStructure();
          }

          function deleteRow(id) {
            if (isReadOnly) return;
            const parts = id.split('-').map(Number);
            let current = currentData.children;
            for (let i = 0; i < parts.length - 1; i++) {
              current = current[parts[i]].children;
            }
            current.splice(parts[parts.length - 1], 1);
            renderStructure();
          }

          function requestSave() {
            if (isReadOnly) return;
            setStatus('Saving...', '');
            vscode.postMessage({ command: 'save', structure: currentData });
          }
        </script>
      </body>
      </html>
    `;
  }
}
