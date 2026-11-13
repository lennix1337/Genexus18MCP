using System;
using System.Collections.Generic;

namespace GxMcp.Gateway
{
    public sealed class RemovedToolInfo
    {
        public RemovedToolInfo(string replacedBy, string argHint)
        {
            ReplacedBy = replacedBy;
            ArgHint = argHint;
        }

        public string ReplacedBy { get; }
        public string ArgHint { get; }
    }

    public static class RemovedToolsRegistry
    {
        public static readonly IReadOnlyDictionary<string, RemovedToolInfo> Map =
            new Dictionary<string, RemovedToolInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_batch_read"] = new RemovedToolInfo("genexus_read", "use targets[] (array of {name, part})"),
                ["genexus_batch_edit"] = new RemovedToolInfo("genexus_edit", "use targets[] (array of edit requests, each {name, mode, content|ops|patch, ...})"),
                ["genexus_open_kb"] = new RemovedToolInfo("genexus_kb", "use action=open with path (and optional alias)"),
                ["genexus_get_sql"] = new RemovedToolInfo("genexus_db", "use action=sql_ddl with name (and includeSubordinated)"),
                ["genexus_get_sql_for_navigation"] = new RemovedToolInfo("genexus_db", "use action=sql_navigation with name and levelNumber"),
                ["genexus_summarize"] = new RemovedToolInfo("genexus_analyze", "use mode=summary with name"),
                ["genexus_explain_code"] = new RemovedToolInfo("genexus_analyze", "use mode=explain with name and code")
            };
    }
}
