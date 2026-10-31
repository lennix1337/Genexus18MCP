using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The object types genexus_create names when it refuses a type, and the one kind it refuses by name: a WorkWithPlus
    /// template.
    /// </summary>
    internal static class ObjectCreateTypes
    {
        /// <summary>The types a create resolves by name, as the refusal hint lists them.</summary>
        public const string SupportedTypes =
            "Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, " +
            "ExternalObject, Image, Theme, ThemeClass, DesignSystem, ColorPalette, Menu, Menubar, Stencil, UserControl, " +
            "WorkPanel, Report, Dashboard, Query, WorkflowDiagram, ConversationalFlows, TestSuite, API, URLRewrite, MiniApp, " +
            "SuperApp, OfflineDatabase, DataView, Group, Language, TranslationMessage, WorkWithDevices, WorkWithWeb";

        /// <summary>The hint for a type that the create does not resolve.</summary>
        public const string SupportedTypesHint = "Supported types: " + SupportedTypes + ".";

        /// <summary>
        /// The hint for a WorkWithPlus template. Its layout is edited in the GeneXus IDE, and a create would save an empty
        /// object in its place, so the refusal sends the caller to the IDE.
        /// </summary>
        public const string WorkWithPlusTemplateHint =
            "A WorkWithPlus template is created only in the GeneXus IDE (template editor), not by genexus_create. " + SupportedTypesHint;

        /// <summary>
        /// True for a WorkWithPlus template type under any spelling, such as "WorkWithPlus for Web Template" or
        /// "WorkWithPlusforWebTemplate", in any case.
        /// </summary>
        public static bool IsWorkWithPlusTemplate(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;

            string key = type.Replace(" ", string.Empty).ToLowerInvariant();
            return key.StartsWith("workwithplus", StringComparison.Ordinal) && key.Contains("template");
        }
    }
}
