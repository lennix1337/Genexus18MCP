using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using Artech.Genexus.Common.Objects;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// SDK calls whose signature differs between GeneXus X Evolution 3 (built with
    /// GxMcpSdkProfile=evolution3) and GeneXus 16-18. Each member keeps the newer
    /// SDK's call on the newer SDK and the closest Evolution 3 equivalent otherwise.
    /// </summary>
    internal static class SdkCompat
    {
#if GXMCP_SDK_EV3
        internal const bool IsEvolution3 = true;
#else
        internal const bool IsEvolution3 = false;
#endif

        /// <summary>
        /// Whether BuildOptions.FailIfReorg exists. Without it a build may reorganize the
        /// database, so every build path that relies on it must refuse instead.
        /// </summary>
        internal static bool HasFailIfReorg => !IsEvolution3;

        internal static Domain GetDomain(KBModel model, QualifiedName name)
        {
#if GXMCP_SDK_EV3
            return Domain.Get(model, name.ToString());
#else
            return Domain.Get(model, name);
#endif
        }

        internal static Domain ResolveDomain(Module fromModule, string name)
        {
#if GXMCP_SDK_EV3
            return KBObject.ResolveName(fromModule, Artech.Genexus.Common.ObjClass.Domain, name) as Domain;
#else
            return Domain.ResolveName(fromModule, name);
#endif
        }

        /// <summary>Module-aware name lookup; Evolution 3 has no ObjectNameHelper, so callers fall back to their direct lookup.</summary>
        internal static KBObject GetByObjectNameHelper(KBModel model, string name)
        {
#if GXMCP_SDK_EV3
            return null;
#else
            return Artech.Architecture.Common.Helpers.ObjectNameHelper.Get(model, name) as KBObject;
#endif
        }

        /// <summary>Whether the object is the interface of an imported module; Evolution 3 has no module interfaces.</summary>
        internal static bool IsModuleInterface(KBObject obj)
        {
#if GXMCP_SDK_EV3
            return false;
#else
            return obj.IsInterface;
#endif
        }

        internal static string TableQualifiedName(Table table)
        {
#if GXMCP_SDK_EV3
            return table.Name;
#else
            return table.QualifiedNameString;
#endif
        }

        internal static string TableOriginalName(Table table)
        {
#if GXMCP_SDK_EV3
            return table.Name;
#else
            return table.OriginalName;
#endif
        }

        /// <summary>
        /// The transaction that owns the table. Evolution 3 has no ranking of associated
        /// transactions, so it answers only when exactly one transaction is associated.
        /// </summary>
        internal static Transaction BestAssociatedTransaction(Table table)
        {
#if GXMCP_SDK_EV3
            var associated = table.AssociatedTransactions.Take(2).ToList();
            return associated.Count == 1 ? associated[0] : null;
#else
            return table.BestAssociatedTransaction;
#endif
        }

        internal static bool AreEqualInContent(IComparerService comparer, KBObject left, KBObject right)
        {
#if GXMCP_SDK_EV3
            return comparer.AreEqualInContent(left, right);
#else
            return comparer.AreEqualInContent(left, right, CompareObjectOptions.Default);
#endif
        }

        internal static bool AreEqualInContent(IComparerService comparer, KBObjectPart left, KBObjectPart right, bool compareVisualContent)
        {
#if GXMCP_SDK_EV3
            return comparer.AreEqualInContent(left, right, compareVisualContent);
#else
            return comparer.AreEqualInContent(left, right, compareVisualContent, CompareObjectOptions.Default);
#endif
        }

        /// <summary>Export options for a non-interactive GeneXus Server send. Evolution 3 has no SilentDefault.</summary>
        internal static ExportOptions SilentExportOptions()
        {
#if GXMCP_SDK_EV3
            return ExportOptions.Default;
#else
            return ExportOptions.SilentDefault;
#endif
        }

        /// <summary>
        /// The user-control definitions the KB can use. Evolution 3 keeps one catalog per
        /// installation (Initialize() without a model and a ControlDefinitionCollection property).
        /// </summary>
        internal static System.Collections.IEnumerable UserControlDefinitions(
            Artech.Genexus.Common.Services.IUserControlsManagerService service, KBModel model)
        {
#if GXMCP_SDK_EV3
            try { service.Initialize(); } catch { /* the collection may already be loaded */ }
            return service.ControlDefinitionCollection;
#else
            try { service.Initialize(model); } catch { /* the getter may work without it */ }
            return service.GetControlDefinitionCollection(model);
#endif
        }

        internal static bool AreEqualInProperties(IComparerService comparer, KBObject left, KBObject right)
        {
#if GXMCP_SDK_EV3
            return comparer.AreEqualInProperties(left, right);
#else
            return comparer.AreEqualInProperties(left, right, ComparePropertiesOptions.Default);
#endif
        }
    }
}
