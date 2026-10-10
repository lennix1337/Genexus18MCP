using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The MCP <c>instructions</c> string returned by <c>initialize</c> and
    /// <c>server/discover</c>. Clients place it in the model's context, so it is the
    /// one place where an agent learns which tool answers which intent before it
    /// has called anything. Every fragment names the tools it routes to and is
    /// emitted only when the active tool profile exposes all of them, so the text
    /// never points an agent at a tool it cannot call.
    /// </summary>
    internal static class ServerInstructions
    {
        private sealed record Fragment(string Text, params string[] Tools);

        private const string Header =
            "GeneXus Knowledge Base MCP server. Reads and writes go through the GeneXus SDK (or the catalogued legacy driver), never file parsing. Route by intent:";

        private const string Footer =
            "Pass kb=<alias> when more than one KB is open. Full guidance and schema for any tool: resources/read genexus://kb/tool-help/<tool>.";

        private static readonly Fragment[][] Lines =
        {
            new[]
            {
                new Fragment("Start every session with genexus_whoami (KB context, version, health, next steps).", "genexus_whoami"),
                new Fragment("No KB open: genexus_kb action=open path=<KB root> (starts its Worker). Already open or declared: action=list, then action=select (session only).", "genexus_kb")
            },
            new[]
            {
                new Fragment("Find objects: genexus_query (name:\"X\" exact, type:, usedby:, parent:).", "genexus_query"),
                new Fragment("Page through every object: genexus_list_objects.", "genexus_list_objects"),
                new Fragment("Find code text: genexus_search_source.", "genexus_search_source")
            },
            new[]
            {
                new Fragment("Read: genexus_read without part returns the complete object in one call. Before a version-checked edit, read the part you will change (part=Source, Rules, ...) and pass its versionToken as baseVersion.", "genexus_read"),
                new Fragment("Compact snapshot: genexus_inspect.", "genexus_inspect")
            },
            new[]
            {
                new Fragment("Understand before changing: genexus_analyze mode=context (360-degree task context in one call),", "genexus_analyze"),
                new Fragment("genexus_object_context (callers, callees, data model),", "genexus_object_context"),
                new Fragment("genexus_impact (affected set and rebuild order).", "genexus_impact")
            },
            new[]
            {
                new Fragment("Change code: genexus_edit with dryRun=true first, then the same call without it.", "genexus_edit"),
                new Fragment("Variables: genexus_variable.", "genexus_variable"),
                new Fragment("Structure of Transactions/SDTs: genexus_structure.", "genexus_structure"),
                new Fragment("New objects: genexus_create.", "genexus_create"),
                new Fragment("WorkWithPlus: genexus_apply_pattern.", "genexus_apply_pattern")
            },
            new[]
            {
                new Fragment("Build or validate: genexus_lifecycle; long operations return an operationId, poll with action=status target=op:<id>.", "genexus_lifecycle")
            },
            new[]
            {
                new Fragment("Something looks wrong: genexus_doctor.", "genexus_doctor")
            }
        };

        internal static IReadOnlyCollection<string> ReferencedTools =>
            Lines.SelectMany(line => line).SelectMany(fragment => fragment.Tools)
                .Distinct(StringComparer.Ordinal).ToArray();

        internal static string Build(string? profile)
        {
            var builder = new StringBuilder(Header);
            foreach (var line in Lines)
            {
                var visible = line.Where(fragment => fragment.Tools.All(tool => ToolProfileFilter.IsToolExposed(profile, tool)))
                    .Select(fragment => fragment.Text)
                    .ToList();
                if (visible.Count == 0) continue;

                string text = string.Join(" ", visible);
                if (text.EndsWith(",", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1) + ".";
                builder.Append("\n- ").Append(text);
            }
            builder.Append('\n').Append(Footer);
            return builder.ToString();
        }

        internal static string ForActiveProfile() =>
            Build(ToolProfileFilter.ResolveActiveProfile(Program.ActiveConfig?.Server?.ToolProfile));
    }
}
