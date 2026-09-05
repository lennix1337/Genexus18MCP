using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Keeps the three published views of umbrella actions aligned:
    /// tool_definitions.json, OperationClassifier, and the human/machine-readable
    /// action table in docs/mcp_capabilities_inventory.md. This protects the
    /// follow-up contract from #131 while retaining the #65 placement and #34
    /// homonym-routing semantics documented alongside the inventory.
    /// </summary>
    public class ToolActionContractTests
    {
        private static string FindUp(params string[] relativeSegments)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(new[] { dir }.Concat(relativeSegments).ToArray());
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException(
                "Could not locate " + string.Join("/", relativeSegments) + " from " + AppContext.BaseDirectory);
        }

        private static JArray LoadToolDefinitions()
        {
            var path = FindUp("src", "GxMcp.Gateway", "tool_definitions.json");
            return JArray.Parse(File.ReadAllText(path));
        }

        private static Dictionary<string, (HashSet<string> ReadOnly, HashSet<string> Mutating)> LoadInventory()
        {
            var path = FindUp("docs", "mcp_capabilities_inventory.md");
            var lines = File.ReadAllLines(path);
            var rows = new Dictionary<string, (HashSet<string>, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
            bool inActionTable = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    inActionTable = line.StartsWith("## Action contract", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inActionTable || !line.StartsWith("|", StringComparison.Ordinal)) continue;

                var cells = line.Split('|').Select(cell => cell.Trim()).ToArray();
                if (cells.Length < 4 || string.Equals(cells[1], "Tool", StringComparison.OrdinalIgnoreCase)) continue;

                string? tool = CodeToken(cells[1]);
                if (string.IsNullOrWhiteSpace(tool)) continue;
                if (rows.ContainsKey(tool))
                    throw new InvalidDataException("Duplicate action inventory row for " + tool);
                rows[tool] = (ActionTokens(cells[2]), ActionTokens(cells[3]));
            }

            return rows;
        }

        [Fact]
        public void EverySchemaActionHasAnExplicitClassifierEntry()
        {
            var schemaTools = LoadToolDefinitions()
                .Where(tool => tool["inputSchema"]?["properties"]?["action"]?["enum"] is JArray)
                .ToDictionary(tool => tool["name"]!.ToString(), StringComparer.OrdinalIgnoreCase);

            Assert.Equal(
                schemaTools.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                OperationClassifier.ActionTools.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

            foreach (var pair in schemaTools)
            {
                var actions = (JArray)pair.Value["inputSchema"]!["properties"]!["action"]!["enum"]!;
                foreach (var actionToken in actions)
                {
                    var action = actionToken.ToString();
                    var kind = OperationClassifier.ClassifyAction(pair.Key, action);
                    Assert.NotEqual(OperationClassifier.OperationKind.Unknown, kind);
                }
            }
        }

        [Fact]
        public void EveryActionToolHasHelpAndInventoryCoverage()
        {
            var inventory = LoadInventory();
            var schemaTools = LoadToolDefinitions()
                .Where(tool => tool["inputSchema"]?["properties"]?["action"]?["enum"] is JArray)
                .Select(tool => tool["name"]!.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(schemaTools.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                inventory.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            foreach (var tool in schemaTools)
            {
                var help = ToolHelpCatalog.Get(tool);
                Assert.False(string.IsNullOrWhiteSpace(help), $"No help text for {tool}");
                Assert.True(help!.Length >= 200, $"Help for {tool} should describe its action contract");
                Assert.True(inventory.ContainsKey(tool), $"No action inventory row for {tool}");
                Assert.Contains("## Action contract", help, StringComparison.Ordinal);
                Assert.Contains("Read-only actions:", help, StringComparison.Ordinal);
                Assert.Contains("Mutating actions:", help, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void HelpDocumentsEverySchemaActionToken()
        {
            foreach (var tool in LoadToolDefinitions()
                .Where(tool => tool["inputSchema"]?["properties"]?["action"]?["enum"] is JArray))
            {
                string toolName = tool["name"]!.ToString();
                string help = ToolHelpCatalog.Get(toolName) ?? string.Empty;
                foreach (var action in (JArray)tool["inputSchema"]!["properties"]!["action"]!["enum"]!)
                {
                    Assert.Contains("`" + action + "`", help, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public void InventoryActionsMatchTheSchemaAndTheirClassifier()
        {
            var inventory = LoadInventory();
            var schemaTools = LoadToolDefinitions()
                .Where(tool => tool["inputSchema"]?["properties"]?["action"]?["enum"] is JArray)
                .ToDictionary(tool => tool["name"]!.ToString(), StringComparer.OrdinalIgnoreCase);

            foreach (var pair in schemaTools)
            {
                Assert.True(inventory.TryGetValue(pair.Key, out var row), $"No action inventory row for {pair.Key}");
                var actions = ((JArray)pair.Value["inputSchema"]!["properties"]!["action"]!["enum"]!)
                    .Select(token => token.ToString())
                    .ToHashSet(StringComparer.Ordinal);
                var documented = row.ReadOnly.Concat(row.Mutating).ToHashSet(StringComparer.Ordinal);

                Assert.True(actions.SetEquals(documented),
                    $"Inventory actions for {pair.Key} differ from schema. Schema: " +
                    string.Join(", ", actions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) +
                    "; inventory: " +
                    string.Join(", ", documented.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));

                Assert.Empty(row.ReadOnly.Intersect(row.Mutating, StringComparer.Ordinal));
                foreach (var action in row.ReadOnly)
                    Assert.Equal(OperationClassifier.OperationKind.ReadOnly,
                        OperationClassifier.ClassifyAction(pair.Key, action));
                foreach (var action in row.Mutating)
                    Assert.Equal(OperationClassifier.OperationKind.Mutating,
                        OperationClassifier.ClassifyAction(pair.Key, action));
            }
        }

        private static string? CodeToken(string cell)
        {
            var match = Regex.Match(cell, "`([^`]+)`", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static HashSet<string> ActionTokens(string cell)
            => Regex.Matches(cell, "`([^`]+)`", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
    }
}
