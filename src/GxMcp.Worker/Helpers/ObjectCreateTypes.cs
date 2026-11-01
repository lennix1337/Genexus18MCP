using System;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The object types genexus_create names when it refuses a type, and the one kind it refuses by name: a WorkWithPlus
    /// template.
    /// </summary>
    internal static class ObjectCreateTypes
    {
        /// <summary>
        /// The types a create resolves by name, as the refusal hint lists them. A test pins the list to the typed-descriptor
        /// table of the resolver and to the ObjClass lookup.
        /// </summary>
        public const string SupportedTypes =
            "Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, " +
            "ExternalObject, Image, Theme, ThemeClass, ThemeColor, ThemeTransformation, DesignSystem, DesignSystemClass, " +
            "ColorPalette, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, Dashboard, Query, QueryDashboard, " +
            "WorkflowDiagram, ConversationalFlows, TestSuite, API, URLRewrite, MiniApp, SuperApp, OfflineDatabase, DataView, " +
            "Group, Language, TranslationMessage, WikiPageKBObject, DataStoreCategory, GeneratorCategory, " +
            "DeploymentUnitCategory, WorkWithDevices, WorkWithWeb, Module, Folder";

        /// <summary>The hint for a type that the create does not resolve.</summary>
        public const string SupportedTypesHint = "Supported types: " + SupportedTypes + ".";

        /// <summary>
        /// The hint for a WorkWithPlus template. The template is created and edited in the SDK's WorkWithPlus Settings Editor,
        /// so genexus_create refuses it for both creating and updating.
        /// </summary>
        public const string WorkWithPlusTemplateHint =
            "A WorkWithPlus template is created and edited in the GeneXus IDE (WorkWithPlus Settings Editor); " +
            "genexus_create neither creates nor updates it. " + SupportedTypesHint;

        /// <summary>
        /// True for a WorkWithPlus template type, whatever the spelling: "WorkWithPlus for Web Template",
        /// "WorkWithPlusforWebTemplate", "WWPTemplate", in any case and with any white space. A qualified class name counts
        /// by its last segment, as in "DVelop.Extensions.WWPPackagesCommon.Model.WWPTemplate".
        /// </summary>
        public static bool IsWorkWithPlusTemplate(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;

            int dot = type.LastIndexOf('.');
            string simpleName = dot >= 0 ? type.Substring(dot + 1) : type;

            var key = new StringBuilder(simpleName.Length);
            foreach (char c in simpleName)
            {
                if (!char.IsWhiteSpace(c)) key.Append(char.ToLowerInvariant(c));
            }

            string name = key.ToString();
            return name.EndsWith("template", StringComparison.Ordinal)
                && (name.Contains("workwithplus") || name.StartsWith("wwp", StringComparison.Ordinal));
        }
    }
}
