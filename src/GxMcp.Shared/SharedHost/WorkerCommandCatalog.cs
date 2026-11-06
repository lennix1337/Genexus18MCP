#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GxMcp.Common.SharedHost
{
    /// <summary>
    /// Whether a Worker command can change the KB (Requirement 4 criterion 6).
    /// </summary>
    internal enum OperationClass
    {
        /// <summary>The command changes no KB object, object part, object property or KB property.</summary>
        Read,

        /// <summary>The command can create, modify, rename or delete any of those, or it is not provably read-only.</summary>
        Write,
    }

    /// <summary>
    /// The single Operation_Class table of Worker commands (Requirement 4 criteria 4 to 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entries are keyed by the lower-case dispatcher <c>method</c> plus the <c>action</c>
    /// literal the dispatcher compares; an empty action means the command carries no
    /// action (its handler compares no action literal). The table decides only the
    /// <c>retryable</c> value of a <c>delivered-unanswered</c> error (Requirement 12
    /// criterion 4). It never routes, queues or reorders a request (Requirement 4
    /// criterion 7), and it is distinct from the Gateway's tool-level
    /// <c>OperationClassifier</c>, which classifies MCP tool names.
    /// </para>
    /// <para>
    /// A command is Read only when it provably changes nothing in the KB. A command
    /// whose dry run or validation saves inside a KB transaction that it rolls back, or
    /// that rewrites KB metadata, is Write. Anything absent from the table, including a
    /// known method with an action it does not list, is Write (criterion 5).
    /// </para>
    /// <para>
    /// The method is matched case-insensitively, as the dispatcher's command table does.
    /// The action is matched ordinally: several handlers compare it with <c>==</c> and
    /// send an unmatched action to a write path (for example <c>write</c> treats any
    /// other action as a part name to write), so a case variant must not inherit a
    /// Read entry.
    /// </para>
    /// </remarks>
    internal static class WorkerCommandCatalog
    {
        private static readonly (string Method, string Action, OperationClass Class)[] Table =
        {
            R("ping", ""),
            R("system", "GetSdkBusyStatus"),
            R("control", "Cancel"),

            // OpenKB rewrites the KB's .gxw version metadata when it differs from the installation.
            W("kb", "Open"),
            R("kb", "BulkIndex"),
            W("kb", "SelfTest"),
            R("kb", "GetDatabaseInfo"),
            R("kb", "GetActiveEnvironment"),
            R("kb", "ListEnvironments"),
            W("kb", "SetActiveEnvironment"),
            R("kb", "GetLauncherObject"),
            R("kb", "GetStartupObject"),
            W("kb", "SetStartupObject"),
            R("kb", "GetIndexStatus"),
            R("kb", "GetIndexState"),
            R("kb", "GetNameTypeMap"),
            R("kb", "ValidateConditions"),
            R("kb", "ListPatternSnapshots"),
            W("kb", "RestorePatternSnapshot"),

            R("batch", "BatchRead"),
            W("batch", "BatchEdit"),
            W("batch", "MultiEdit"),
            W("batch", "Process"),

            R("search", "Query"),
            R("search", "SearchSource"),
            R("list", "Objects"),

            R("read", "CertifyRecovery"),
            R("read", "ExtractFullObject"),
            R("read", "ExtractSource"),
            R("read", "ExtractParts"),
            R("read", "GetVariables"),
            R("read", "GetAttribute"),

            R("object", "Read"),
            W("object", "Create"),
            W("object", "Delete"),
            W("object", "SaveAs"),
            W("object", "WorkerReload"),
            R("object", "ReadLogs"),
            R("object", "ExportText"),
            R("object", "ReadBlob"),
            W("object", "ImportText"),
            R("object", "ExportTextBatch"),
            W("object", "ImportTextBatch"),
            // Validates each file through a dry-run import, which runs the SDK write path.
            W("object", "ValidateTextBatch"),
            W("object", "DeleteTextBatch"),
            R("object", "ValidateTextInMemory"),
            R("object", "ListTextInMemory"),

            W("atomiccreate", ""),
            W("dataview", ""),
            W("generatorreference", ""),

            W("write", "AddVariable"),
            W("write", "DeleteVariable"),
            W("write", "ModifyVariable"),
            R("write", "ValidatePayload"),
            W("write", "Bulk"),
            W("write", "ApplyTemplate"),
            R("write", "ListTemplates"),

            W("mutation", "ChangeSet"),
            W("editandbuild", "Orchestrate"),
            W("semanticops", "Apply"),
            W("jsonpatch", "Apply"),
            W("patch", "Apply"),

            R("analyze", "GetCodeMetrics"),
            R("analyze", "GetNavigation"),
            R("analyze", "GetSqlForNavigation"),
            R("analyze", "GenerateSampleData"),
            R("analyze", "QueryRecords"),
            W("analyze", "InsertRecord"),
            W("analyze", "UpdateRecords"),
            W("analyze", "TranslationsImport"),
            R("analyze", "GetParameters"),
            R("analyze", "Get360Context"),
            R("analyze", "GetContext"),
            R("analyze", "GetHierarchy"),
            R("analyze", "GetDataContext"),
            R("analyze", "GetConversionContext"),
            R("analyze", "GetPatternMetadata"),
            R("analyze", "Summarize"),
            R("analyze", "GetSQL"),
            R("analyze", "ExplainCode"),
            R("analyze", "ParentContext"),
            R("analyze", "CrossPlatformImpact"),
            R("analyze", "FindCallerSites"),
            R("analyze", "GetEventFlow"),
            R("analyze", "DependencyHeatmap"),
            R("analyze", "ImpactAnalysis"),
            R("analyze", "InjectContext"),

            R("buildplan", "Generate"),
            R("future", "Deferred"),
            W("linter", ""),
            R("diff", ""),
            R("export", "Unified"),
            R("export", "Full"),
            W("forge", "Scaffold"),
            W("conversion", "TranslateTo"),

            R("pattern", "GetSample"),
            W("pattern", "Apply"),
            R("pattern", "Diagnose"),
            W("pattern", "ManageActions"),

            W("atomicauthoring", "Run"),
            R("sdkprobe", "Capabilities"),
            W("sdkprobe", "Run"),
            R("ui", "GetUIContext"),

            R("layout", "GetTree"),
            R("layout", "FindControls"),
            W("layout", "SetProperty"),
            W("layout", "SetProperties"),
            R("layout", "InspectSurface"),
            R("layout", "GetVisualPreview"),
            R("layout", "ScanMutators"),
            W("layout", "RenamePrintBlock"),
            W("layout", "AddPrintBlock"),
            W("layout", "DeletePrintBlock"),
            W("layout", "AddReportControl"),
            W("layout", "MoveReportControl"),
            W("layout", "RemoveReportControl"),

            W("structure", "SetAttributeProperties"),
            W("structure", "SetLevelProperties"),
            W("structure", "SetDomainProperties"),
            R("structure", "GetSdtItemProperty"),
            W("structure", "SetSdtItemProperty"),
            W("structure", "UpdateGroupStructure"),
            R("structure", "GetVisualStructure"),
            W("structure", "UpdateVisualStructure"),
            R("structure", "GetVisualIndexes"),
            W("structure", "CreateIndex"),
            W("structure", "DropIndex"),
            R("structure", "GetLogicStructure"),
            W("structure", "MoveAttribute"),
            W("structure", "RemoveAttribute"),
            R("structure", "CheckSubtypes"),

            W("authoring", "AddExternalMethod"),
            W("authoring", "AddExternalProperty"),
            W("authoring", "AddMenuOption"),
            W("authoring", "AddDataSelectorCondition"),

            R("build", "Status"),
            R("build", "Result"),
            // ValidateCode saves the part inside a KB transaction it then rolls back.
            W("validation", ""),
            W("test", ""),
            W("wiki", ""),
            R("visualizer", ""),
            R("health", ""),
            R("history", "diff"),
            W("undo", ""),

            R("security", "audit_gam"),
            R("security", "scan_secrets"),
            R("security", "scan_native"),
            R("security", "audit_object"),
            R("orient", "Welcome"),

            R("property", "Get"),
            W("property", "Set"),
            W("property", "SetMultiple"),
            W("property", "Move"),

            R("asset", "Find"),
            R("asset", "Read"),
            W("asset", "Write"),
            R("formatting", "Format"),
            W("refactor", ""),
            W("popup", "Create"),

            R("dbdrift", "Check"),
            R("dbdrift", "Report"),
            R("dboptimize", "Analyze"),
            R("dboptimize", "SuggestIndexes"),
            R("dboptimize", "Report"),
            W("webformedit", ""),
            R("runobject", "Resolve"),
            R("explain", "Explain"),
            R("generateddiff", "Diff"),
            W("kbreadme", "Generate"),
            R("ocr", "Run"),
            R("prdescription", "Generate"),
            W("screenshotpublish", "Publish"),
            W("frictionlog", "Append"),
            R("frictionlog", "Tail"),

            W("memory", "save"),
            R("memory", "recall"),
            R("memory", "list"),
            W("memory", "forget"),
            W("memory", "promote"),
            W("memory", "consolidate"),

            R("wcagcheck", "Check"),
            R("learning", "Report"),
            W("gxserver", ""),
            R("compare", ""),
            W("module", ""),
            W("gam", ""),
            W("merge", ""),
            R("kbversion", "ActiveModel"),
            W("transfer", ""),
            W("deploy", ""),
            R("reorgimpact", "Preview"),
            R("kbstats", ""),
            R("tablerelations", ""),
            R("dfd", ""),
            R("impact", ""),
            R("objectcontext", ""),
            W("wire", ""),
            R("themeclasses", ""),
            R("usercontrols", ""),
            W("wwpaction", ""),
            W("workwith", ""),
            W("k2bdesigner", ""),
            W("curlproc", ""),
            R("designsystem", ""),
            W("sdpanel", ""),
            W("multiagentlock", ""),
            R("whatif", "Simulate"),
            R("tutorial", "Step"),
            R("playbook", "Read"),
            R("doctor", "WwpTemplates"),
            W("api", ""),
            R("profile", ""),
            R("types", ""),
            W("github", "CreatePr"),
            R("aicomplete", "Complete"),
            W("timetravel", "Recover"),
            R("voice", "Intent"),
            W("autotest", "Generate"),
            R("reversepattern", "Infer"),
            R("crossbrowser", "Run"),
            W("preview", "Render"),
            W("preview", "Run"),
            R("kbexplorer", "Locate"),
            R("navigation", "View"),
            R("blame", "Get"),
            R("browser_capture", "Capture"),
            R("smoke_test", "Run"),
            R("a11y_audit", "Audit"),
        };

        private static readonly Dictionary<string, OperationClass> ByKey = BuildIndex();

        /// <summary>Every entry of the table, in dispatcher registration order.</summary>
        internal static IReadOnlyCollection<(string Method, string Action, OperationClass Class)> Entries { get; }
            = new ReadOnlyCollection<(string Method, string Action, OperationClass Class)>(Table);

        /// <summary>
        /// The Operation_Class of a Worker command. A null or empty action means no action.
        /// A null or empty method, or any command absent from the table, is Write.
        /// </summary>
        internal static OperationClass Classify(string? method, string? action)
        {
            if (string.IsNullOrEmpty(method)) return OperationClass.Write;
            return ByKey.TryGetValue(Key(method!.ToLowerInvariant(), action ?? string.Empty), out OperationClass value)
                ? value
                : OperationClass.Write;
        }

        private static Dictionary<string, OperationClass> BuildIndex()
        {
            var index = new Dictionary<string, OperationClass>(Table.Length, StringComparer.Ordinal);
            foreach (var entry in Table)
            {
                // A duplicate key is a table defect; fail at type initialisation rather
                // than let one entry silently shadow the other.
                index.Add(Key(entry.Method, entry.Action), entry.Class);
            }
            return index;
        }

        private static string Key(string method, string action) => method + "\u0000" + action;

        private static (string Method, string Action, OperationClass Class) R(string method, string action)
            => (method, action, OperationClass.Read);

        private static (string Method, string Action, OperationClass Class) W(string method, string action)
            => (method, action, OperationClass.Write);
    }
}
