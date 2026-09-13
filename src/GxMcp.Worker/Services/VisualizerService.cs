using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class VisualizerService
    {
        private readonly IndexCacheService _indexCacheService;
        private readonly ArtifactPathResolver _artifactPaths;

        public VisualizerService()
            : this(
                new IndexCacheService(),
                new ArtifactPathResolver(() => Environment.GetEnvironmentVariable("GX_KB_PATH")))
        {
        }

        public VisualizerService(IndexCacheService indexCacheService, ArtifactPathResolver artifactPaths)
        {
            _indexCacheService = indexCacheService ?? throw new ArgumentNullException(nameof(indexCacheService));
            _artifactPaths = artifactPaths ?? throw new ArgumentNullException(nameof(artifactPaths));
        }

        // Safely extract the substring after the first ':' in a "Type:Name" key.
        // Returns null when input is null/empty or has nothing meaningful after the colon.
        private static string ExtractNameAfterColon(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int idx = key.IndexOf(':');
            if (idx < 0) return key;
            if (idx >= key.Length - 1) return null; // trailing colon, e.g. "Type:"
            return key.Substring(idx + 1);
        }

        public string GenerateGraph(string payload)
        {
            try
            {
                string filterDomain = "All";
                string filterTypes = null;
                string filterPrefix = null;
                string filterName = null;

                if (!string.IsNullOrEmpty(payload) && payload.StartsWith("{"))
                {
                    try {
                        var p = JObject.Parse(payload);
                        filterDomain = p["domain"]?.ToString() ?? "All";
                        filterTypes = p["type"]?.ToString();
                        filterPrefix = p["prefix"]?.ToString();
                        filterName = p["name"]?.ToString();
                    } catch { } // Fallback to treating payload as domain if it's not JSON
                }
                else
                {
                    filterDomain = payload ?? "All";
                }
                SearchIndex index = _indexCacheService.GetIndex();
                if (index == null || index.Objects == null || index.Objects.Count == 0)
                {
                    bool missing = _indexCacheService.IsIndexMissing;
                    if (missing)
                    {
                        return McpResponse.Err(
                            code: "SearchIndexMissing",
                            message: "Search index not found.",
                            hint: "Build the search index before generating the dependency graph.",
                            nextSteps: new JArray(McpResponse.NextStep(
                                tool: "genexus_lifecycle",
                                args: new JObject { ["action"] = "index" },
                                why: "Builds the on-disk search index required for the dependency graph.")),
                            retryAfterMs: 10000,
                            target: filterName ?? payload);
                    }

                    return McpResponse.Err(
                        code: "SearchIndexEmpty",
                        message: "Search index is empty.",
                        hint: "The dependency graph cannot be generated until the search index contains objects.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_lifecycle",
                            args: new JObject { ["action"] = "index", ["force"] = true },
                            why: "Forces a full rebuild of the search index on the active KB.")),
                        retryAfterMs: 10000,
                        target: filterName ?? payload);
                }

                var nodes = new List<object>();
                var edges = new List<object>();
                var addedNodes = new HashSet<string>();

                IEnumerable<SearchIndex.IndexEntry> sourceObjects = index.Objects.Values;

                // 1. Apply Multi-Criteria Filters
                var scoredObjects = index.Objects.Values.Select(e => new {
                    Entry = e,
                    Score = CalculateStructuralScore(e)
                });

                if (!string.IsNullOrEmpty(filterDomain) && filterDomain != "All")
                {
                    scoredObjects = scoredObjects.Where(o => string.Equals(o.Entry.BusinessDomain, filterDomain, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(filterTypes))
                {
                    var types = filterTypes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
                    scoredObjects = scoredObjects.Where(o => types.Any(t => string.Equals(o.Entry.Type, t, StringComparison.OrdinalIgnoreCase)));
                }

                if (!string.IsNullOrEmpty(filterPrefix))
                {
                    scoredObjects = scoredObjects.Where(o => {
                        string nameOnly = o.Entry.Name.Contains(":") ? o.Entry.Name.Split(':')[1] : o.Entry.Name;
                        return nameOnly.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase);
                    });
                }

                if (!string.IsNullOrEmpty(filterName))
                {
                    scoredObjects = scoredObjects.Where(o => o.Entry.Name.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                var relevantObjects = scoredObjects
                    .OrderByDescending(o => o.Score)
                    .Take(1000)
                    .ToList();

                var relevantNames = new HashSet<string>(relevantObjects.Select(o => o.Entry.Name), StringComparer.OrdinalIgnoreCase);

                // Build Graph Data
                foreach (var item in relevantObjects)
                {
                    var entry = item.Entry;
                    var score = item.Score;
                    // Node
                    if (!addedNodes.Contains(entry.Name))
                    {
                        nodes.Add(new
                        {
                            data = new
                            {
                                id = entry.Name,
                                label = entry.Name.Contains(":") ? entry.Name.Split(':')[1] : entry.Name,
                                type = entry.Type ?? "Other",
                                domain = entry.BusinessDomain ?? "Unknown",
                                score = score,
                                size = 20 + (int)Math.Sqrt(score * 10)
                            }
                        });
                        addedNodes.Add(entry.Name);
                    }

                    // Edges (Outgoing calls) - Use HashSet for O(1) lookup
                    foreach (var target in entry.Calls)
                    {
                        if (relevantNames.Contains(target))
                        {
                             edges.Add(new 
                             { 
                                 data = new 
                                 { 
                                     source = entry.Name, 
                                     target = target,
                                     id = entry.Name + "->" + target
                                 } 
                             });
                        }
                    }
                }

                var jsonGraph = JsonConvert.SerializeObject(new { nodes, edges });
                string html = GetHtmlTemplate(jsonGraph);
                ArtifactPaths artifactPaths = _artifactPaths.Resolve();
                Directory.CreateDirectory(artifactPaths.HtmlDirectory);
                // Never use a shared graph.html: each generation gets a unique safe name and
                // remains durable for callers that need to open a prior graph.
                string graphName = string.Format(
                    "graph-{0:yyyyMMddHHmmssfff}-{1}.html",
                    DateTime.UtcNow,
                    Guid.NewGuid().ToString("N"));
                string filePath = _artifactPaths.ResolveFile(ArtifactKind.Html, graphName);
                File.WriteAllText(filePath, html);

                // Build Mermaid fallback
                var mermaid = new StringBuilder();
                mermaid.AppendLine("graph TD");
                foreach (var node in nodes) {
                    var n = (dynamic)node;
                    mermaid.AppendLine(string.Format("  {0}[\"{1}\"]", n.data.id.Replace(" ", "_"), n.data.label));
                }
                foreach (var edge in edges) {
                    var e = (dynamic)edge;
                    mermaid.AppendLine(string.Format("  {0} --> {1}", e.data.source.Replace(" ", "_"), e.data.target.Replace(" ", "_")));
                }

                return McpResponse.Ok(
                    code: "GraphGenerated",
                    result: new JObject
                    {
                        ["url"] = filePath.Replace("\\", "/"),
                        ["outputDirectory"] = artifactPaths.HtmlDirectory,
                        ["kbArtifactScope"] = artifactPaths.KbScopeDirectory,
                        ["mermaid"] = mermaid.ToString(),
                        ["nodes"] = nodes.Count,
                        ["edges"] = edges.Count
                    });
            }
            catch (ArtifactPathException ex)
            {
                return McpResponse.Err(
                    code: "ArtifactPathRejected",
                    message: ex.Message,
                    hint: "Configure a writable artifact root; generated filenames are kept within the KB-scoped output directory.",
                    target: payload);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "GraphGenerationFailed",
                    message: ex.Message,
                    hint: "Check that the search index is built and the output directory is writable.");
            }
        }

        private int CalculateStructuralScore(SearchIndex.IndexEntry entry)
        {
            int authority = entry.CalledBy?.Count ?? 0;
            int hubiness = entry.Calls?.Count ?? 0;
            int complexity = entry.Complexity;

            // Structural relevance formula
            int score = (authority * 5) + (hubiness * 2) + Math.Max(0, (complexity - 5) * 3);
            return Math.Max(1, score);
        }

        private string GetHtmlTemplate(string jsonData)
        {
            return @"<!DOCTYPE html>
<html>
<head>
    <title>GeneXus KB Visualizer</title>
    <script src='https://cdnjs.cloudflare.com/ajax/libs/cytoscape/3.28.1/cytoscape.min.js'></script>
    <style>
        body { font-family: 'Segoe UI', sans-serif; margin: 0; padding: 0; display: flex; height: 100vh; overflow: hidden; }
        #sidebar { width: 300px; background: #f4f4f4; border-right: 1px solid #ccc; padding: 20px; box-shadow: 2px 0 5px rgba(0,0,0,0.1); z-index: 10; display: flex; flex-direction: column; }
        #cy { flex-grow: 1; background: #fff; }
        h2 { margin-top: 0; color: #333; }
        .stat { margin-bottom: 10px; font-size: 0.9em; color: #666; }
        #details { margin-top: 20px; padding-top: 20px; border-top: 1px solid #ddd; flex-grow: 1; overflow-y: auto; }
        .tag { display: inline-block; padding: 2px 6px; background: #e0e0e0; border-radius: 4px; font-size: 0.8em; margin-right: 5px; margin-bottom: 5px; }
        .legend { margin-top: auto; padding-top: 10px; font-size: 0.85em; }
        .legend-item { display: flex; align-items: center; margin-bottom: 4px; }
        .dot { width: 12px; height: 12px; border-radius: 50%; margin-right: 8px; }
        
        .type-Trn { background-color: #0074D9; }
        .type-Prc { background-color: #2ECC40; }
        .type-Wbp { background-color: #FF851B; }
        .type-Tbl { background-color: #B10DC9; }
        .type-Folder { background-color: #FFDC00; }
        .type-KBCategory { background-color: #F012BE; }

        .prop-label { font-weight: bold; color: #555; font-size: 0.8em; margin-top: 8px; }
        .prop-val { font-size: 0.9em; word-break: break-all; }
        .semitransp { opacity: 0.1; }
        .highlight { border-width: 3px; border-color: #000; }
    </style>
</head>
<body>
    <div id='sidebar'>
        <h2>KB Visualizer</h2>
        <div class='stat'>Nodes: <span id='nodeCount'>0</span></div>
        <div class='stat'>Edges: <span id='edgeCount'>0</span></div>
        
        <div id='details'>
            <p>Select a node to see dependencies.</p>
        </div>

        <div style='margin-top: 20px;'>
            <div class='prop-label'>Min Score Filter</div>
            <input type='range' id='scoreFilter' min='0' max='500' value='0' style='width: 100%;'>
            <div id='scoreVal' style='font-size: 0.8em; text-align: right;'>0</div>
        </div>

        <div class='legend'>
            <div class='legend-item'><div class='dot' style='background:#0074D9'></div>Transaction</div>
            <div class='legend-item'><div class='dot' style='background:#2ECC40'></div>Procedure</div>
            <div class='legend-item'><div class='dot' style='background:#FF851B'></div>WebPanel</div>
            <div class='legend-item'><div class='dot' style='background:#B10DC9'></div>Table</div>
            <div class='legend-item'><div class='dot' style='background:#FFDC00'></div>Folder</div>
            <div class='legend-item'><div class='dot' style='background:#F012BE'></div>Category</div>
        </div>
    </div>
    <div id='cy'></div>

    <script>
        const graphData = " + jsonData + @";
        
        document.getElementById('nodeCount').innerText = graphData.nodes.length;
        document.getElementById('edgeCount').innerText = graphData.edges.length;

        const originalNodes = [...graphData.nodes];
        const originalEdges = [...graphData.edges];

        const cy = cytoscape({
            container: document.getElementById('cy'),
            elements: graphData,
            style: [
                {
                    selector: 'node',
                    style: {
                        'label': 'data(label)',
                        'text-valign': 'center',
                        'text-halign': 'center',
                        'color': '#fff',
                        'text-outline-width': 2,
                        'text-outline-color': '#555',
                        'background-color': '#999',
                        'width': 'data(size)',
                        'height': 'data(size)',
                        'font-size': '10px',
                        'z-index': 10
                    }
                },
                { selector: 'node[type=""Trn""]', style: { 'background-color': '#0074D9', 'text-outline-color': '#0074D9' } },
                { selector: 'node[type=""Transaction""]', style: { 'background-color': '#0074D9', 'text-outline-color': '#0074D9' } },
                { selector: 'node[type=""Prc""]', style: { 'background-color': '#2ECC40', 'text-outline-color': '#2ECC40' } },
                { selector: 'node[type=""Procedure""]', style: { 'background-color': '#2ECC40', 'text-outline-color': '#2ECC40' } },
                { selector: 'node[type=""Wbp""]', style: { 'background-color': '#FF851B', 'text-outline-color': '#FF851B' } },
                { selector: 'node[type=""WebPanel""]', style: { 'background-color': '#FF851B', 'text-outline-color': '#FF851B' } },
                { selector: 'node[type=""Tbl""]', style: { 'background-color': '#B10DC9', 'text-outline-color': '#B10DC9' } },
                { selector: 'node[type=""Table""]', style: { 'background-color': '#B10DC9', 'text-outline-color': '#B10DC9' } },
                { selector: 'node[type=""Folder""]', style: { 'background-color': '#FFDC00', 'text-outline-color': '#FFDC00' } },
                { selector: 'node[type=""KBCategory""]', style: { 'background-color': '#F012BE', 'text-outline-color': '#F012BE' } },
                {
                    selector: 'edge',
                    style: {
                        'width': 1,
                        'line-color': '#ddd',
                        'target-arrow-color': '#ddd',
                        'target-arrow-shape': 'triangle',
                        'curve-style': 'bezier',
                        'opacity': 0.6
                    }
                },
                {
                    selector: ':selected',
                    style: {
                        'border-width': 4,
                        'border-color': '#333'
                    }
                },
                { selector: '.semitransp', style: { 'opacity': '0.1' } }
            ],
            layout: {
                name: 'cose',
                animate: false,
                idealEdgeLength: 150,
                nodeOverlap: 40,
                refresh: 20,
                fit: true,
                padding: 30,
                randomize: true,
                componentSpacing: 150,
                nodeRepulsion: 800000,
                edgeElasticity: 150,
                nestingFactor: 5,
                gravity: 100,
                numIter: 1000,
                initialTemp: 300,
                coolingFactor: 0.95,
                minTemp: 1.0
            }
        });

        cy.on('tap', 'node', function(evt){
            var node = evt.target;
            var d = node.data();
            
            var html = '<h3>' + d.label + '</h3>';
            html += '<div class=""prop-label"">Full Name</div><div class=""prop-val"">' + d.id + '</div>';
            html += '<div class=""prop-label"">Type</div><div class=""prop-val"">' + d.type + '</div>';
            html += '<div class=""prop-label"">Domain</div><div class=""prop-val"">' + d.domain + '</div>';
            html += '<div class=""prop-label"">Structural Score</div><div class=""prop-val""><b>' + d.score + '</b></div>';
            html += '<div class=""prop-label"">Incoming (References)</div><div class=""prop-val"">' + (node.degree() - node.outdegree()) + '</div>';
            html += '<div class=""prop-label"">Outgoing (Calls)</div><div class=""prop-val"">' + node.outdegree() + '</div>';
            
            document.getElementById('details').innerHTML = html;

            // Highlight connections
            cy.elements().addClass('semitransp');
            node.removeClass('semitransp');
            node.connectedEdges().removeClass('semitransp');
            node.neighborhood().removeClass('semitransp');
        });

        cy.on('tap', function(e){
            if(e.target === cy){
                cy.elements().removeClass('semitransp');
                document.getElementById('details').innerHTML = '<p>Select a node to see dependencies.</p>';
            }
        });

        // Filter Logic
        document.getElementById('scoreFilter').addEventListener('input', function(e) {
            const minScore = parseInt(e.target.value);
            document.getElementById('scoreVal').innerText = minScore;
            
            cy.batch(() => {
                cy.nodes().forEach(node => {
                    if (node.data('score') < minScore) {
                        node.style('display', 'none');
                        node.connectedEdges().style('display', 'none');
                    } else {
                        node.style('display', 'element');
                        // Only show edges if both source and target are visible
                        node.connectedEdges().forEach(edge => {
                             if (edge.source().data('score') >= minScore && edge.target().data('score') >= minScore) {
                                 edge.style('display', 'element');
                             }
                        });
                    }
                });
            });
        });

    </script>
</body>
</html>";
        }
    }
}
