using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Utils;

namespace GxMcp.Worker.Services
{
    public class WikiService
    {
        private readonly ObjectService _objectService;
        private readonly SearchService _searchService;
        private readonly ArtifactPathResolver _artifactPaths;

        public WikiService(ObjectService objectService, SearchService searchService)
            : this(objectService, searchService, new ArtifactPathResolver(() => Environment.GetEnvironmentVariable("GX_KB_PATH")))
        {
        }

        public WikiService(ObjectService objectService, SearchService searchService, ArtifactPathResolver artifactPaths)
        {
            _objectService = objectService;
            _searchService = searchService;
            _artifactPaths = artifactPaths ?? throw new ArgumentNullException(nameof(artifactPaths));
        }

        public string Generate(string target)
        {
            try
            {
                KBObject obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Err(code: "ObjectNotFound", message: "Object not found.", hint: "Verify the object name with genexus_query.", nextSteps: new JArray(Models.McpResponse.NextStep("genexus_query", new JObject { ["name"] = target }, "Search for the object by name.")), target: target);

                // Build markdown
                var md = new StringBuilder();
                md.AppendLine($"# {obj.Name}");
                md.AppendLine($"**Type:** {obj.TypeDescriptor.Name}");
                md.AppendLine($"**Description:** {obj.Description}");
                md.AppendLine($"**Updated:** {obj.LastUpdate:yyyy-MM-dd HH:mm}");
                md.AppendLine();

                // 1. Business Intent & Role
                md.AppendLine("## Business Role");
                string role = InferRole(obj);
                md.AppendLine(role);
                md.AppendLine();

                // 2. Call Graph (Direct Dependencies)
                md.AppendLine("## Dependencies (Calls)");
                md.AppendLine("```mermaid");
                md.AppendLine("graph TD");
                string selfNode = obj.Name.Replace(":", "_");
                md.AppendLine($"  {selfNode}[{obj.Name}]");
                
                var references = new List<string>();
                foreach(var refLink in obj.GetReferences())
                {
                    try {
                        var targetObj = refLink.To != null ? obj.Model.Objects.Get(refLink.To) : null;
                        if (targetObj != null && IsInteresting(targetObj))
                        {
                            string targetName = targetObj.Name;
                            string targetNode = targetName.Replace(":", "_");
                            md.AppendLine($"  {selfNode} --> {targetNode}[{targetName}]");
                            references.Add(targetName);
                        }
                    } catch {}
                }
                md.AppendLine("```");
                md.AppendLine();

                // 3. Inverse Dependencies (Used By)
                md.AppendLine("## Impact Analysis (Used By)");
                string searchRes = _searchService.Search($"usedby:\"{obj.Name}\"", null, null, 10);
                var searchJson = JObject.Parse(searchRes);
                var usedByList = searchJson["results"] as JArray;
                if (usedByList != null && usedByList.Count > 0)
                {
                    md.AppendLine("The following objects depend on this one:");
                    foreach (var item in usedByList)
                    {
                        md.AppendLine($"- **{item["name"]}** ({item["type"]})");
                    }
                }
                else
                {
                    md.AppendLine("No direct upstream dependencies found (Potential Entry Point).");
                }
                md.AppendLine();

                // 4. Entity Relationship (for Data-Heavy objects)
                if (obj is Transaction trn)
                {
                    md.AppendLine("## Entity Structure");
                    md.AppendLine("```mermaid");
                    md.AppendLine("erDiagram");
                    md.AppendLine($"  {trn.Name} {{");
                    foreach(var attr in trn.Structure.Root.Attributes)
                    {
                        string keyMarker = attr.IsKey ? "PK" : "";
                        md.AppendLine($"    {attr.Name} {keyMarker}");
                    }
                    md.AppendLine("  }");
                    md.AppendLine("```");
                    md.AppendLine();
                }

                // 5. Logic Highlights
                md.AppendLine("## Logic Highlights");
                string source = GetSource(target);
                var highlights = AnalyzeLogic(source);
                foreach(var h in highlights) md.AppendLine($"- {h}");
                if (highlights.Count == 0) md.AppendLine("Simple logic or no highlights detected.");
                md.AppendLine();

                // 6. Source Code
                md.AppendLine("## Source Code Snippet");
                md.AppendLine("```genexus");
                md.AppendLine(source.Length > 3000 ? source.Substring(0, 3000) + "\n// ... (truncated for brevity)" : source);
                md.AppendLine("```");

                // Save outside the Worker installation. The configured/default root is scoped by
                // the resolved KB identity, and the filename is a single validated component.
                ArtifactPaths artifactPaths = _artifactPaths.Resolve();
                Directory.CreateDirectory(artifactPaths.DocumentationDirectory);
                string filePath = _artifactPaths.ResolveFile(ArtifactKind.Documentation, obj.Name.Replace(":", "_") + ".md");
                File.WriteAllText(filePath, md.ToString(), Encoding.UTF8);

                return Models.McpResponse.Ok(target: target, code: "WikiGenerated", result: new JObject
                {
                    ["file"] = filePath,
                    ["outputDirectory"] = artifactPaths.DocumentationDirectory,
                    ["kbArtifactScope"] = artifactPaths.KbScopeDirectory,
                    ["dependencies"] = new JArray(references.Distinct().ToList()),
                    ["markdown"] = md.ToString()
                });
            }
            catch (ArtifactPathException ex)
            {
                return Models.McpResponse.Err(
                    code: "ArtifactPathRejected",
                    message: ex.Message,
                    hint: "Configure a writable artifact root and use an object name without path separators.",
                    target: target);
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"message\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string GetSource(string target)
        {
            string sourceJson = _objectService.ReadObjectSource(target, "Source", null, null, "mcp");
            var json = JObject.Parse(sourceJson);
            return json["source"] != null ? json["source"].ToString() : "";
        }

        private bool IsInteresting(KBObject obj)
        {
            return obj is Procedure || obj is Transaction || obj is WebPanel || obj is Table || obj is DataProvider;
        }

        private string InferRole(KBObject obj)
        {
            if (obj is Transaction) return "Core Data Entity. Defines the database schema and basic validation rules.";
            if (obj is Procedure) {
                string src = GetSource(obj.Name).ToUpper();
                if (src.Contains("FOR EACH")) return "Data Processor (Batch). Performs bulk operations on the database.";
                if (src.Contains("REPORT") || src.Contains("OUTPUT")) return "Reporting Service. Generates documents or data outputs.";
                return "Business Logic Service. Encapsulates a specific calculation or rule.";
            }
            if (obj is WebPanel) return "User Interface. Handles user interaction and data display.";
            return "General GeneXus Object.";
        }

        private List<string> AnalyzeLogic(string source)
        {
            var highlights = new List<string>();
            string s = source.ToUpper();

            if (s.Contains("FOR EACH")) highlights.Add("Contains **database loops** (For Each). Potentially high impact on performance.");
            if (s.Contains("COMMIT")) highlights.Add("Performs explicit **Database Commits**.");
            if (s.Contains("DELETE") && s.Contains("FOR EACH")) highlights.Add("Performs **Bulk Deletions**.");
            if (s.Contains("UDP(")) highlights.Add("Calls **Data Providers** for data retrieval.");
            if (s.Contains("CALL(")) highlights.Add("Orchestrates multiple external procedures.");
            if (s.Contains("MSGBAR(") || s.Contains("CONFIRM(")) highlights.Add("Interactive logic with **User UI notifications**.");
            
            // Detect WWP
            if (source.Contains("DVelop Work With Plus Pattern")) highlights.Add("Object is managed by **WorkWithPlus Pattern**.");

            return highlights;
        }
    }
}
