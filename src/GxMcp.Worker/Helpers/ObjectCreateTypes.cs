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
            "Transaction, Procedure, WebPanel, MasterPage, SDPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, " +
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

        /// <summary>The type name a Master Page is created under.</summary>
        public const string MasterPageType = "MasterPage";

        /// <summary>
        /// The object type the SDK builds a Master Page from: a Master Page is a WebPanel whose WEB_COMP property is
        /// <see cref="MasterPageWebComp"/>, with the same entity type GUID. It is not in the typed-descriptor table on purpose:
        /// a lookup by "MasterPage" must keep matching the object's descriptor name, not every WebPanel.
        /// </summary>
        public const string MasterPageBaseType = "WebPanel";

        /// <summary>The value of the WEB_COMP property that makes a WebPanel a Master Page.</summary>
        public const string MasterPageWebComp = "Master";

        /// <summary>
        /// The form a new Master Page is saved with. The SDK refuses a Master Page without a content place holder and refuses
        /// the place holder outside a Master Page, so the form and WEB_COMP are given to the object together, before its
        /// first save. The SDK generates the ids of the containers; PanelType is left out because the SDK drops it.
        /// </summary>
        public const string MasterPageMinimalForm =
            "<GxMultiForm rootId=\"2\" version=\"html:15.0.0;layout:17.11.0\"><Form id=\"2\" type=\"layout\"><detail><layout>" +
            "<table controlName=\"MainTable\" tableType=\"Responsive\"><row><cell>" +
            "<contentholder controlName=\"ContentHolder\" /></cell></row></table></layout></detail></Form></GxMultiForm>";

        /// <summary>True when a WEB_COMP value makes the WebPanel a Master Page.</summary>
        public static bool IsMasterWebComp(string webComp)
            => string.Equals(webComp, MasterPageWebComp, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for the Master Page type, whatever the spelling: "MasterPage", "Master Page", "Web Master Panel",
        /// "WebMasterPanel" (the name the SDK gives the object in an XPZ), in any case and with any white space.
        /// </summary>
        public static bool IsMasterPage(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;

            var key = new StringBuilder(type.Length);
            foreach (char c in type)
            {
                if (!char.IsWhiteSpace(c) && c != '_' && c != '-') key.Append(char.ToLowerInvariant(c));
            }

            string name = key.ToString();
            return name == "masterpage" || name == "webmasterpanel" || name == "masterpanel";
        }

        /// <summary>
        /// The text of the warning a create carries when the object was created but not placed where the caller asked, or
        /// null when there is nothing to warn about (no placement was asked for, or the move succeeded).
        /// <paramref name="moveResponse"/> is the answer of the move that failed. A kind mismatch names the parameter to use
        /// instead: module holds a Module and folder a Folder.
        /// </summary>
        public static string BuildPlacementWarning(string requestedPlacement, string requestedKind, Newtonsoft.Json.Linq.JObject moveResponse)
        {
            if (string.IsNullOrWhiteSpace(requestedPlacement)) return null;
            if (string.Equals(moveResponse?["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) return null;

            var error = moveResponse?["error"] as Newtonsoft.Json.Linq.JObject;
            string code = error?["code"]?.ToString();
            string reason = error?["message"]?.ToString();
            var text = new StringBuilder();
            text.Append("The object was created in the Root Module, NOT in '").Append(requestedPlacement).Append("'");
            if (!string.IsNullOrWhiteSpace(reason)) text.Append(": ").Append(reason.Trim().TrimEnd('.')).Append('.');
            else text.Append('.');

            string resolvedKind = (error?["placement"] as Newtonsoft.Json.Linq.JObject)?["resolvedKind"]?.ToString();
            if (string.Equals(code, "DestinationKindMismatch", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(resolvedKind))
            {
                string wanted = string.Equals(resolvedKind, "Folder", StringComparison.OrdinalIgnoreCase) ? "folder" : "module";
                string given = string.Equals(requestedKind, "Folder", StringComparison.OrdinalIgnoreCase) ? "folder" : "module";
                text.Append(" Pass it as ").Append(wanted).Append('=').Append(requestedPlacement)
                    .Append(" (it is a ").Append(resolvedKind).Append("), not as ").Append(given).Append('=').Append(requestedPlacement).Append('.');
            }
            text.Append(" Move it with genexus_properties action=move, or delete it and repeat the create with the right destination.");
            return text.ToString();
        }

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
