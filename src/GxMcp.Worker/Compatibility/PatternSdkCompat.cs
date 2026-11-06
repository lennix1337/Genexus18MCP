#if GXMCP_SDK_EV3
using Artech.Packages.Patterns.Objects;
using Artech.Packages.Patterns.Specification;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Pattern specification members that GeneXus 16-18 expose publicly and GeneXus X
    /// Evolution 3 keeps internal. Compiled only for the evolution3 profile; on newer SDKs
    /// the instance members of the same name are used.
    /// </summary>
    internal static class PatternSdkCompat
    {
        /// <summary>Same lookup as the newer SDK's SpecificationAttribute.GetValueString.</summary>
        internal static string GetValueString(this SpecificationAttribute attribute, PatternInstanceElement element)
        {
            return element.Attributes.GetPropertyValueString(attribute.PropertyDescriptorName);
        }

        internal static SpecificationChildElement TryFindChildElement(this SpecificationType type, string name)
        {
            return type.HasChildElement(name) ? type.FindChildElement(name) : null;
        }
    }
}
#endif
