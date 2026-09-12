using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Keep the index readiness gate limited to reads and analyses whose
        // result is backed by the search index. SDK mutations and builds have
        // their own source/object validation and remain usable during rebuilds.
        internal static bool IsIndexDependentToolForTest(string? toolName)
            => IsIndexDependentTool(toolName);

        private static bool IsIndexDependentTool(string? toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            return toolName.ToLowerInvariant() switch
            {
                "genexus_list_objects" => true,
                "genexus_query" => true,
                "genexus_inspect" => true,
                "genexus_read" => true,
                "genexus_search_source" => true,
                "genexus_analyze" => true,
                "genexus_explain" => true,
                "genexus_types" => true,
                "genexus_navigation" => true,
                "genexus_kb_explorer" => true,
                "genexus_diff_generated" => true,
                "genexus_what_if" => true,
                "genexus_db_drift" => true,
                "genexus_orient" => true,
                "genexus_security" => true,
                _ => false
            };
        }

        internal static bool IsTransientResponseForCacheForTest(JObject? response)
            => IsTransientResponseForCache(response);

        private static bool IsTransientResponseForCache(JObject? response)
        {
            if (response == null) return false;

            var status = response["status"]?.ToString();
            var code = response["code"]?.ToString();
            return string.Equals(status, "Reindexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "IndexCold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Indexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
                // Canonical worker envelopes use status="ok" plus a transient code.
                // Inspect both fields or IndexCold can enter the semantic cache.
                || string.Equals(code, "IndexNotReady", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Reindexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "IndexCold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Indexing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "BuildPlanTooLarge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "Running", StringComparison.OrdinalIgnoreCase);
        }
    }
}
