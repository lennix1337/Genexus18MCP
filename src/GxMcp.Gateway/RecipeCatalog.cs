using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Named playbooks the LLM can pull in a single tool call instead of
    // exploring/inspecting first. Each recipe is a structured step list:
    // - goal: one-line summary of the outcome
    // - prereq: things to verify BEFORE the first mutating call
    // - steps: ordered [{ tool, args, why }]
    // - pitfalls: short list of common mistakes ("don't use create_object for WWP")
    //
    // Keep entries tight (≤ ~600 tokens each). Full prose lives in
    // ToolHelpCatalog; this is the routing layer.
    internal static class RecipeCatalog
    {
        // Registry: key → (description, example, version, builder).
        // Item 60: recipes are versioned. The catalog stores one entry per
        // (key, version); `Get(name)` resolves the latest version when no
        // pin is given, and `Get(name@v1)` resolves a specific one. All
        // current recipes are "v1" — no breaking change.
        private record RecipeMeta(string Description, string Example, string Version, Func<JObject> Build);

        // Crystallized user macros: discovered from <configRoot>/recipes/user-macros/*.json.
        // Built-in recipes win on name collision. Set via ConfigureUserMacroDirectory + Refresh*.
        private static string? _userMacroDirectory;
        private static readonly Dictionary<string, RecipeMeta> _userMacros
            = new Dictionary<string, RecipeMeta>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _userMacroLock = new object();

        public static void ConfigureUserMacroDirectory(string directory)
        {
            lock (_userMacroLock)
            {
                _userMacroDirectory = directory;
            }
            RefreshUserMacros();
        }

        // Reload all user-macro JSON files from the configured directory.
        // Called after Crystallize() so freshly-written recipes are reachable
        // without a gateway restart. Built-ins always win on key collision.
        public static void RefreshUserMacros()
        {
            string? dir;
            lock (_userMacroLock) { dir = _userMacroDirectory; }
            if (string.IsNullOrEmpty(dir)) return;

            var fresh = new Dictionary<string, RecipeMeta>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        try
                        {
                            string raw = File.ReadAllText(file);
                            var parsed = JObject.Parse(raw);
                            string name = parsed["name"]?.ToString() ?? Path.GetFileNameWithoutExtension(file);
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            // Built-ins win — skip if registry already has this key.
                            if (RecipeRegistry.ContainsKey(name)) continue;
                            string desc = parsed["description"]?.ToString() ?? "User macro.";
                            string version = parsed["version"]?.ToString() ?? "v1";
                            string example = "genexus_recipe { name: '" + name + "' }";
                            JObject snapshot = parsed; // captured by builder
                            fresh[name] = new RecipeMeta(desc, example, version, () => (JObject)snapshot.DeepClone());
                        }
                        catch
                        {
                            // Ignore malformed user macros — surface as missing rather than crash the gateway.
                        }
                    }
                }
            }
            catch
            {
                // Ignore IO errors — user macros are best-effort.
            }

            lock (_userMacroLock)
            {
                _userMacros.Clear();
                foreach (var kvp in fresh) _userMacros[kvp.Key] = kvp.Value;
            }
        }

        private static IEnumerable<KeyValuePair<string, RecipeMeta>> AllRecipes()
        {
            foreach (var kvp in RecipeRegistry) yield return kvp;
            lock (_userMacroLock)
            {
                foreach (var kvp in _userMacros)
                {
                    if (RecipeRegistry.ContainsKey(kvp.Key)) continue; // built-in wins
                    yield return kvp;
                }
            }
        }

        public static JObject Get(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Error("Recipe name is required.", "Pass name='list' to enumerate recipes.");

            string raw = name.Trim();
            string keyPart = raw;
            string? requestedVersion = null;
            int atIdx = raw.IndexOf('@');
            if (atIdx >= 0)
            {
                keyPart = raw.Substring(0, atIdx).Trim();
                requestedVersion = raw.Substring(atIdx + 1).Trim();
                if (string.IsNullOrEmpty(requestedVersion)) requestedVersion = null;
            }
            string key = keyPart.ToLowerInvariant();

            if (key == "list" || key == "index")
            {
                var arr = new JArray();
                // Group by recipe key (case-insensitive) to compute availableVersions.
                var grouped = new Dictionary<string, List<RecipeMeta>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in AllRecipes())
                {
                    string baseName = StripVersion(kvp.Key);
                    if (!grouped.TryGetValue(baseName, out var list))
                    {
                        list = new List<RecipeMeta>();
                        grouped[baseName] = list;
                    }
                    list.Add(kvp.Value);
                }
                foreach (var pair in grouped)
                {
                    var versions = new JArray();
                    string? latestVersion = null;
                    string? latestDesc = null;
                    string? latestExample = null;
                    foreach (var m in pair.Value)
                    {
                        versions.Add(m.Version);
                        // "Latest" = lexicographically max version string ("v2" > "v1").
                        if (latestVersion == null || string.CompareOrdinal(m.Version, latestVersion) > 0)
                        {
                            latestVersion = m.Version;
                            latestDesc = m.Description;
                            latestExample = m.Example;
                        }
                    }
                    arr.Add(new JObject
                    {
                        ["name"] = pair.Key,
                        ["description"] = latestDesc,
                        ["example"] = latestExample,
                        ["latestVersion"] = latestVersion,
                        ["availableVersions"] = versions
                    });
                }
                return new JObject
                {
                    ["recipes"] = arr,
                    ["hint"] = "Call genexus_recipe { name: '<recipeName>' } for latest, or 'name@v1' to pin."
                };
            }

            // Resolve (key, version) → entry. Without an explicit version,
            // pick the lexicographically max version among matching entries.
            RecipeMeta? resolved = null;
            string? resolvedVersion = null;
            foreach (var kvp in AllRecipes())
            {
                string baseName = StripVersion(kvp.Key);
                if (!string.Equals(baseName, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (requestedVersion != null)
                {
                    if (string.Equals(kvp.Value.Version, requestedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = kvp.Value;
                        resolvedVersion = kvp.Value.Version;
                        break;
                    }
                }
                else
                {
                    if (resolvedVersion == null || string.CompareOrdinal(kvp.Value.Version, resolvedVersion) > 0)
                    {
                        resolved = kvp.Value;
                        resolvedVersion = kvp.Value.Version;
                    }
                }
            }

            if (resolved != null)
            {
                JObject body = resolved.Build();
                body["version"] = resolvedVersion;
                return body;
            }

            if (requestedVersion != null)
            {
                return Error($"Unknown recipe version '{name}'.",
                    "Try the latest with name='" + keyPart + "', or check availableVersions in 'list'.");
            }

            return Error($"Unknown recipe '{name}'.",
                "Try one of: " + string.Join(", ", RecipeNames()));
        }

        // Item 47 — dispatch for the new genexus_recipes (plural) tool.
        //   action=list (default) → name, description, example, 1-line step preview.
        //   action=describe       → full playbook (same shape as genexus_recipe).
        public static JObject Dispatch(string? action, string? name)
        {
            string a = (action ?? string.Empty).Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(a) || a == "list" || a == "index")
            {
                var arr = new JArray();
                foreach (var kvp in AllRecipes())
                {
                    JObject? body = null;
                    try { body = kvp.Value.Build(); } catch { }
                    var stepSummaries = new JArray();
                    if (body?["steps"] is JArray steps)
                    {
                        foreach (var s in steps)
                        {
                            stepSummaries.Add((s?["tool"]?.ToString() ?? string.Empty) +
                                              ": " + (s?["why"]?.ToString() ?? string.Empty));
                        }
                    }

                    arr.Add(new JObject
                    {
                        ["name"] = kvp.Key,
                        ["description"] = kvp.Value.Description,
                        ["example"] = kvp.Value.Example,
                        ["goal"] = body?["goal"]?.ToString(),
                        ["steps"] = stepSummaries,
                        // Item 60: versioned recipes.
                        ["latestVersion"] = kvp.Value.Version,
                        ["availableVersions"] = new JArray(kvp.Value.Version)
                    });
                }
                return new JObject
                {
                    ["recipes"] = arr,
                    ["hint"] = "Full playbook: genexus_recipes { action: 'describe', name: '<recipeName>' }."
                };
            }

            if (a == "describe")
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Error("name is required for action='describe'.",
                                 "Try: genexus_recipes { action: 'describe', name: 'wwp_on_webpanel' }.");
                return Get(name);
            }

            return Error($"Unknown action '{action}'.",
                         "Supported actions: list, describe.");
        }

        private static string StripVersion(string key)
        {
            // Internal registry keys do not embed version (versions are on the
            // RecipeMeta). This hook lets future variants register the same
            // logical name twice (e.g. 'wwp_on_webpanel' v1 and v2).
            return key;
        }

        private static IEnumerable<string> RecipeNames()
        {
            return RecipeRegistry.Keys;
        }

        private static JArray RecipeNamesArray()
        {
            var arr = new JArray();
            foreach (var k in RecipeRegistry.Keys) arr.Add(k);
            return arr;
        }

        private static JObject Error(string message, string hint)
        {
            return new JObject
            {
                ["error"] = message,
                ["hint"] = hint,
                ["availableRecipes"] = RecipeNamesArray()
            };
        }

        private static readonly Dictionary<string, RecipeMeta> RecipeRegistry
            = new Dictionary<string, RecipeMeta>(StringComparer.OrdinalIgnoreCase)
            {
                ["wwp_on_transaction"] = new RecipeMeta(
                    "Generate the full WorkWithPlus screen family for a Transaction.",
                    "genexus_recipe { name: 'wwp_on_transaction' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Generate the full WorkWithPlus screen family (WW<Trn> + View + Export*) for a Transaction.",
                        ["prereq"] = new JArray("Object exists and is of type Transaction (verify with genexus_inspect.metadata)."),
                        ["steps"] = new JArray(
                            Step("genexus_inspect", new JObject { ["name"] = "<Trn>", ["include"] = new JArray("metadata") },
                                 "Confirm parentType=Transaction. If WebPanel/WebComponent/SDPanel, switch to recipe 'wwp_on_webpanel'."),
                            Step("genexus_apply_pattern", new JObject { ["name"] = "<Trn>", ["pattern"] = "WorkWithPlus" },
                                 "Engine generates WorkWithPlus<Trn> (host) + WW<Trn> + View<Trn> + Export* siblings. No template needed."),
                            Step("genexus_edit", new JObject { ["name"] = "WorkWithPlus<Trn>", ["part"] = "PatternInstance", ["mode"] = "patch", ["context"] = "<unique XML anchor>", ["operation"] = "Insert_After", ["content"] = "<new XML node>" },
                                 "Shape the screen by editing the host's PatternInstance. Edits auto-project to the WebForm.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Do NOT use genexus_create_object for WWP — apply_pattern is the only correct entry.",
                            "Do NOT edit WW<Trn>.WebForm directly; the next reapply will overwrite it. Edit WorkWithPlus<Trn>.PatternInstance instead."
                        )
                    }),

                ["wwp_on_webpanel"] = new RecipeMeta(
                    "Direct-attach a WorkWithPlus host onto an existing WebPanel/WebComponent/SDPanel.",
                    "genexus_recipe { name: 'wwp_on_webpanel' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Direct-attach a WorkWithPlus host onto an existing WebPanel/WebComponent/SDPanel (no transaction family).",
                        ["prereq"] = new JArray(
                            "Object exists and is of type WebPanel, WebComponent or SDPanel (verify with genexus_inspect.metadata).",
                            "Know which `WorkWithPlus for Web Template` to use; common ones: MatIsoTemplate, TransactionResp2, PopoverEmpty, TransactionPopUp. If unsure, omit settings.template — the MCP auto-discovers and returns availableTemplates."
                        ),
                        ["steps"] = new JArray(
                            Step("genexus_inspect", new JObject { ["name"] = "<WebPanel>", ["include"] = new JArray("metadata") },
                                 "ABSOLUTELY CHECK PARENT TYPE FIRST. If it returns 'Transaction', switch to recipe 'wwp_on_transaction'. Misrouting was the #1 reported bug."),
                            Step("genexus_apply_pattern", new JObject { ["name"] = "<WebPanel>", ["pattern"] = "WorkWithPlus", ["settings"] = new JObject { ["template"] = "<TemplateName>" } },
                                 "Direct-attach via CreatePatternInstanceWithTemplate. Response carries parentType + bindingMode + patternHost so you can verify what got wired."),
                            Step("genexus_edit", new JObject { ["name"] = "WorkWithPlus<WebPanel>", ["part"] = "PatternInstance", ["mode"] = "patch" },
                                 "Edits to the host auto-project onto the WebPanel's WebForm via UpdateParentObject.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Direct-attach parents (WebPanel/WebComponent/SDPanel) and Transaction take DIFFERENT apply paths. Never assume — always inspect first.",
                            "settings.template must match a registered `WorkWithPlus for Web Template` object; pass empty to let MCP auto-discover.",
                            "Other types (Procedure, SDT, Domain, …) are rejected upfront with parentType in the error envelope."
                        )
                    }),

                ["create_popup"] = new RecipeMeta(
                    "Create a popup WebPanel with editable form bindings in ONE call.",
                    "genexus_recipe { name: 'create_popup' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Create a popup WebPanel with editable form bindings (radio/combo/text + buttons) in ONE call.",
                        ["prereq"] = new JArray("Decide the spec: title, inputs, buttons, in/out parms."),
                        ["steps"] = new JArray(
                            Step("genexus_create_popup", new JObject {
                                ["name"] = "<PopupName>",
                                ["spec"] = new JObject {
                                    ["title"] = "<Title>",
                                    ["inputs"] = new JArray(new JObject { ["type"] = "radio", ["varName"] = "Opcao", ["label"] = "Opção", ["options"] = new JArray(new JObject { ["value"] = "S", ["label"] = "Sim" }, new JObject { ["value"] = "N", ["label"] = "Não" }) }),
                                    ["buttons"] = new JArray(new JObject { ["caption"] = "OK", ["event"] = "Confirm" }),
                                    ["inParms"] = new JArray("ItemId:Numeric(10)"),
                                    ["outParms"] = new JArray("Opcao:Character(1)")
                                }
                            }, "Sets Form type='layout' so bindings render editable, plus Variables, Events and Rules in a single round-trip.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Do NOT create the WebPanel then add inputs piecemeal — Form type defaults to 'free style' and radio/combo become read-only.",
                            "Use callerCode `<Popup>.Popup(...)` from the parent object. The `description` of the popup is the title-bar text."
                        )
                    }),

                ["edit_pattern_instance"] = new RecipeMeta(
                    "Surgically edit a WWP host's PatternInstance XML without destroying surrounding state.",
                    "genexus_recipe { name: 'edit_pattern_instance' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Surgically edit a WWP host's PatternInstance XML without destroying surrounding state.",
                        ["prereq"] = new JArray(
                            "Host object name is `WorkWithPlus<Parent>` (verify it exists; if not, apply_pattern first).",
                            "Read the current PatternInstance to find a unique anchor."
                        ),
                        // Issue #352: this recipe promised a reconciliation report that the
                        // write path never emits, and its "persist" step was a raw
                        // operation=Insert_After example, which the structural preflight
                        // rejects for PatternInstance. Both are corrected here.
                        ["steps"] = new JArray(
                            Step("genexus_read", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance" },
                                 "Find a unique line near the edit site (e.g. an existing <standardAction name='Trn_Delete'> or attribute id)."),
                            Step("genexus_edit", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance", ["mode"] = "patch", ["context"] = "<anchor line>", ["operation"] = "Replace", ["content"] = "<anchor line with one existing property value changed>", ["dryRun"] = true },
                                 "ALWAYS dryRun first to see the projected diff. Change an existing property value; do not add or move nodes."),
                            Step("genexus_edit", new JObject { ["same as above without dryRun"] = true },
                                 "Persist the same property change. The response reports the persisted PatternInstance and its versionToken. A WorkWithPlus write reports `commitCompleted`, `persisted`, `verified` and the rollback evidence after an independent re-read of the host and its parent WebForm; other patterns report `persistenceState` (`requested`, `unchanged`, `partial` or `unknown`) from an independent re-read. It does not include a child-order reconciliation report: this build does not reconcile child order on save."),
                            Step("genexus_read", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance" },
                                 "Verify the property value and confirm adjacent structure remains unchanged.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Do NOT touch `childrenOrderedList` attributes by hand. It is SDK-owned metadata and a change to it is rejected as PatternMetadataChangeUnsupported; the MCP does not rebuild it from your XML child order on save. To reorder, move the child elements themselves and leave the attribute as it is.",
                            "Raw PatternInstance XML edits accept property changes on existing elements plus narrow leaf-level structure: one leaf element (textBlock, attribute, variable, userAction, ...) added per container, several leaf removals, a leaf moved between containers, siblings reordered by identity, and a <prompt> under an <attribute>. Identity changes are rejected as PatternMetadataChangeUnsupported; container-valued or nested additions and a reorder next to a sibling without a name/controlName are rejected as PatternStructureChangeUnsupported — use genexus_workwith for standard WorkWith filter/action structure, genexus_wwp for supported WorkWithPlus operations, or edit in the GeneXus IDE for other structures.",
                            "Use genexus_wwp action=add_layout for variables, tables and actions, after declaring variables first; preview then persist with the same expectedVersion.",
                            "Saving the instance does not regenerate the pattern's generated objects. Apply the pattern in the GeneXus IDE for that.",
                            "Avoid mode='full' unless you really intend a whole-tree rewrite; patch keeps surrounding state safe."
                        )
                    }),

                ["popup_blocking_with_reload"] = new RecipeMeta(
                    "Open a popup synchronously from a parent WebPanel and force a Refresh once it closes.",
                    "genexus_recipe { name: 'popup_blocking_with_reload' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Parent WebPanel opens a popup, locks the screen until the user finishes the gate condition, and reloads on close. Mitigates the AUTO_REFRESH=VARS_CHANGE not firing after .Popup() (see playbook popup_call_async).",
                        ["prereq"] = new JArray(
                            "Parent is a WebPanel; popup is a WebPanel with Form type='layout' and an out-param matching the gate variable.",
                            "Gate condition (e.g. &Aluno.NumRegProf.IsEmpty()) is expressible at Start subroutine time."
                        ),
                        ["steps"] = new JArray(
                            Step("genexus_inspect", new JObject { ["name"] = "<ParentWebPanel>", ["include"] = new JArray("metadata", "variables") },
                                 "Confirm parent is a WebPanel and the gate variable exists."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<ParentWebPanel>",
                                ["part"] = "Events",
                                ["mode"] = "patch",
                                ["operation"] = "Insert_After",
                                ["context"] = "Sub 'Start'",
                                ["content"] = "    if <gate_condition>\n        UnnamedGroup1.Visible = 0\n        <Popup>.Popup(<inParms>, &Out1)\n    endif"
                            }, "Hide blocking group + invoke popup synchronously. .Popup() returns immediately — out-param arrives via the Refresh."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<ParentWebPanel>",
                                ["part"] = "Events",
                                ["mode"] = "patch",
                                ["operation"] = "Insert_After",
                                ["context"] = "Sub 'Refresh'",
                                ["content"] = "    if not <gate_condition>\n        UnnamedGroup1.Visible = 1\n    endif"
                            }, "Restore visibility in Refresh once out-params populate the gate variable."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<ParentWebPanel>",
                                ["part"] = "WebForm",
                                ["mode"] = "patch",
                                ["operation"] = "Replace",
                                ["context"] = "<body",
                                ["content"] = "<body onmousedown=\"if(!window.__gx_reloaded){window.__gx_reloaded=true;window.location.reload();}\""
                            }, "First user mousedown after popup close → page reload. AUTO_REFRESH=VARS_CHANGE is unreliable across KBs.")
                        ),
                        ["pitfalls"] = new JArray(
                            ".Popup() is asynchronous — out-params are EMPTY on the line right after. Always handle them in Refresh.",
                            "Do NOT emit <script> for the reload hook inside gxTextBlock Format=\"HTML\" — the sanitizer escapes it. The <body onmousedown> route is preserved (see playbook html_form_inline_js).",
                            "If the popup itself can be triggered before Start, gate the .Popup() call with a flag variable to avoid double-open."
                        )
                    }),

                ["radio_group_show_hide"] = new RecipeMeta(
                    "Build a radio-group whose selection toggles visibility of dependent controls.",
                    "genexus_recipe { name: 'radio_group_show_hide' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Render a radio group as raw HTML inside a Format=\"HTML\" gxTextBlock and route onclick handlers to a hidden gxAttribute carrying the selected value. Dependent controls toggle visibility via inline onclick.",
                        ["prereq"] = new JArray(
                            "Target WebPanel/popup with Form type='layout'.",
                            "Hidden gxAttribute backing variable (e.g. &Opcao Character(1)) declared in Variables.",
                            "Dependent control IDs known (use genexus_inspect with include=['runtimeIds'] after a build)."
                        ),
                        ["steps"] = new JArray(
                            Step("genexus_add_variable", new JObject { ["name"] = "<WebPanel>", ["variable"] = new JObject { ["name"] = "Opcao", ["type"] = "Character", ["length"] = 1 } },
                                 "Backing variable for the selected radio."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<WebPanel>",
                                ["part"] = "WebForm",
                                ["mode"] = "patch",
                                ["operation"] = "Insert_After",
                                ["context"] = "<!-- radio_group anchor -->",
                                ["content"] =
                                    "<gxTextBlock Format=\"HTML\" Width=\"100%\"><![CDATA[\n" +
                                    "<input type='radio' name='r' value='A' onclick=\"document.getElementById('vOPCAO').value='A';document.getElementById('GRPDETAILA').style.display='';document.getElementById('GRPDETAILB').style.display='none';\"> Opção A\n" +
                                    "<input type='radio' name='r' value='B' onclick=\"document.getElementById('vOPCAO').value='B';document.getElementById('GRPDETAILA').style.display='none';document.getElementById('GRPDETAILB').style.display='';\"> Opção B\n" +
                                    "]]></gxTextBlock>"
                            }, "Raw HTML radios are preserved by the sanitizer (inline event-attrs ARE kept; <script> is not). htmlIds come from runtimeIds (uppercase of design id)."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<WebPanel>",
                                ["part"] = "WebForm",
                                ["mode"] = "patch",
                                ["operation"] = "Insert_After",
                                ["context"] = "<gxAttribute id=\"Opcao\"",
                                ["content"] = " Visible=\"false\""
                            }, "Hide the backing gxAttribute; the JS writes to its value via the document.getElementById('vOPCAO').value = '...' bridge.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Radio inputs inside a gxAttribute ControlType=\"Radio\" become read-only on Form type='free style'. Use raw HTML inside Format=\"HTML\" + a hidden gxAttribute, OR switch Form type to 'layout'.",
                            "htmlIds are UPPERCASE in v18 runtime. genexus_inspect include=['runtimeIds'] returns the design→html mapping.",
                            "Use semicolons between statements inside onclick — newlines inside the HTML attribute do NOT separate JS statements."
                        )
                    }),

                ["extract_to_procedure"] = new RecipeMeta(
                    "Move a WebPanel Events block that writes attributes (would hit spc0150) into a Procedure.",
                    "genexus_recipe { name: 'extract_to_procedure' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Fix spc0150 (\"Attribute cannot be assigned in this context\") by extracting an attribute-writing For each block from a WebPanel Events part into a Procedure. Receives the same in/out variables and is called from the original spot.",
                        ["prereq"] = new JArray(
                            "Build returned spc0150, OR the genexus_edit PreflightSpc0150 warning fired.",
                            "Know the variable scope used by the offending block (the parm signature for the new Procedure)."
                        ),
                        ["steps"] = new JArray(
                            Step("genexus_create_object", new JObject {
                                ["type"] = "Procedure",
                                ["name"] = "<ParentWebPanel>WriteHelper",
                                ["description"] = "Extracted from <ParentWebPanel> Events to satisfy spc0150."
                            }, "New Procedure that owns the database mutation."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<ParentWebPanel>WriteHelper",
                                ["part"] = "Rules",
                                ["mode"] = "full",
                                ["content"] = "parm(in:&InVar1, in:&InVar2, ...);"
                            }, "Declare parm signature matching the variables the block reads/writes."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<ParentWebPanel>WriteHelper",
                                ["part"] = "Source",
                                ["mode"] = "full",
                                ["content"] = "For each <Table>\n    where <key_var_equals_attr>\n    <Attr> = &Value\nendfor"
                            }, "Body matches the original For each, but Procedure Source DOES allow attribute writes."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "<ParentWebPanel>",
                                ["part"] = "Events",
                                ["mode"] = "patch",
                                ["context"] = "<original For each block>",
                                ["operation"] = "Replace",
                                ["content"] = "<ParentWebPanel>WriteHelper.Call(&InVar1, &InVar2, ...)"
                            }, "Replace the original block with a call to the new Procedure."),
                            Step("genexus_lifecycle", new JObject { ["action"] = "build", ["target"] = "<ParentWebPanel>WriteHelper" },
                                 "Build the new Procedure to validate the schema before rebuilding the WebPanel.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Procedures Allow attribute writes; WebPanel Events DO NOT. Don't try to keep the For each in the WebPanel.",
                            "Pass variables by value (no `out:`) unless you actually need them mutated back — simpler signature, easier to reason about.",
                            "If the original block had a `commit` rule, add `Rules: commit;` to the new Procedure so transactional semantics carry over."
                        )
                    }),

                ["feature_scaffold"] = new RecipeMeta(
                    "Scaffold a full feature (Transaction + WWP screens + Procedures) from a structured spec.",
                    "genexus_recipe { name: 'feature_scaffold' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "End-to-end scaffold: parse a structured spec (entity + attributes + ui flags + procedure list), then drive create_object/apply_pattern/create_object in sequence. The agent supplies the spec (already markdown-parsed); the recipe runs it via FeatureScaffoldService. Supports dryRun for plan-only mode.",
                        ["prereq"] = new JArray(
                            "spec.entity.type must be 'Transaction' (other roots are not supported by this scaffold).",
                            "spec.entity.attributes must be non-empty AND at least one attribute has isKey=true.",
                            "Each spec.procedures[i].parms entry must be 'in|out|inout:Name:Type' (e.g. 'in:Course:Character(40)').",
                            "Run with dryRun=true FIRST to confirm the plan before mutating the KB."
                        ),
                        ["steps"] = new JArray(
                            Step("genexus_scaffold_feature", new JObject {
                                ["dryRun"] = true,
                                ["spec"] = new JObject {
                                    ["name"] = "CourseEnrollment",
                                    ["entity"] = new JObject {
                                        ["type"] = "Transaction",
                                        ["name"] = "Enrollment",
                                        ["attributes"] = new JArray(
                                            new JObject { ["name"] = "EnrId", ["type"] = "Numeric(8)", ["isKey"] = true },
                                            new JObject { ["name"] = "EnrStudent", ["type"] = "Character(60)" }
                                        )
                                    },
                                    ["ui"] = new JObject { ["list"] = true, ["edit"] = true, ["summary"] = true },
                                    ["procedures"] = new JArray(
                                        new JObject { ["name"] = "GetEnrollmentsByCourse", ["parms"] = new JArray("in:Course:Character(40)", "out:list:Enrollment[]") }
                                    ),
                                    ["tests"] = false
                                }
                            }, "DryRun: get the full plan (sequence of tool calls with args) without touching the KB. Returns status='DryRun' + plan[]."),
                            Step("genexus_scaffold_feature", new JObject {
                                ["dryRun"] = false,
                                ["spec"] = new JObject { ["...same as above..."] = true }
                            }, "Execute: runs create_object → apply_pattern(WorkWithPlus) → create_object per procedure (+ optional Test stubs if spec.tests=true). On any failure returns status='PartialFailure' with completedSteps[] + failedStep — call genexus_undo to roll back if needed.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Validation runs FIRST and is structural only — it does NOT check that types resolve in the KB (e.g. 'Enrollment[]' as an SDT collection must already exist for the procedure parm to bind at build time). Expect a follow-up build to surface those.",
                            "WorkWithPlus only applies to Transactions; if you set ui.list/edit/summary, entity.type MUST be 'Transaction'.",
                            "PartialFailure does NOT auto-rollback. The agent is responsible for calling genexus_undo if it wants to revert prior steps.",
                            "Procedure parm rules use 'in|out|inout:Name:Type' shape; the scaffold reduces that to 'parm(in:&Name, ...)' for the Rules part. Variable declarations with full types are a later concern (genexus_add_variable)."
                        )
                    }),

                ["add_custom_button"] = new RecipeMeta(
                    "Add a custom action button to a WWP grid/toolbar.",
                    "genexus_recipe { name: 'add_custom_button' }",
                    "v1",
                    () => new JObject
                    {
                        ["goal"] = "Add a custom action button to a WWP grid/toolbar.",
                        ["steps"] = new JArray(
                            Step("genexus_read", new JObject { ["name"] = "WorkWithPlus<X>", ["part"] = "PatternInstance" }, "Locate the `<standardAction name='Trn_Delete' />` (or similar) anchor."),
                            Step("genexus_edit", new JObject {
                                ["name"] = "WorkWithPlus<X>",
                                ["part"] = "PatternInstance",
                                ["mode"] = "patch",
                                ["operation"] = "Insert_After",
                                ["context"] = "<standardAction name=\"Trn_Delete\"",
                                ["content"] = "<userAction caption=\"Auditar\" name=\"Auditar\" buttonClass=\"btn ButtonGreen\" confirm=\"False\" />"
                            }, "Insert userAction next to existing standardActions. buttonClass values vary per theme — see ToolHelpCatalog for tokens like btn ButtonGreen/ButtonRed/ButtonCinza.")
                        ),
                        ["pitfalls"] = new JArray(
                            "Do NOT add the button to the parent Transaction — buttons live on the WWP host."
                        )
                    })
            };

        private static JObject Step(string tool, JObject args, string why)
        {
            return new JObject { ["tool"] = tool, ["args"] = args, ["why"] = why };
        }
    }
}
