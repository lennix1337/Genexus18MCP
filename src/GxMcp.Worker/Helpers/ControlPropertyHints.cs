using System;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #830: the Conditions of a grid (or of a Dynamic Combo Box) are the <c>ControlWhere</c>
    /// property of its layout control. A caller who reads the IDE tries Condition, Conditions or Where;
    /// only Conditions (and, as of this change, Condition) reaches the typed route, and the closest spelling a
    /// refusal used to offer ("collection") pointed away from the property that works.
    /// </summary>
    internal static class ControlPropertyHints
    {
        internal const string ConditionsProperty = GxMcp.Worker.Services.ControlConditionsService.PropertyName;

        // Names a client tries for a grid's Conditions. ControlWhere and Conditions are listed too: a request
        // that arrives through propertyNames[], a query or a batch is not routed to the typed Conditions read.
        private static readonly string[] ConditionsNames =
            { "condition", "conditions", "where", "controlwhere", "gridcondition", "gridconditions", "gridwhere" };

        internal static bool IsConditionsName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string normalized = name.Trim().Replace("_", string.Empty).Replace(" ", string.Empty);
            return ConditionsNames.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What a caller can pass as <c>control</c>, said where the lookup fails. A GUID id is only as stable as the
        /// layout that holds it: a layout that a pattern generates (WorkWithPlus) is rebuilt on every read with new ids,
        /// so an id the caller just read there no longer names a control.
        /// </summary>
        internal static string ControlNotFoundHint(string control)
            => "Use genexus_inspect include=controls to list the controls of this object's layout, and pass control as the control name, "
               + "the attribute name behind it, att:<id>, its GUID id (bare, or as <element>:<guid> with the control's own element) or &Variable."
               + (LayoutFormModel.TryGetControlGuid(control, out _)
                   ? " The ids of a layout that a pattern generates (WorkWithPlus) change on every read: address those controls by name."
                   : string.Empty);

        /// <summary>What to say when a Conditions-like name is not a property of a layout control that lists ControlWhere.</summary>
        internal static string ControlWhereHint(string control)
            => "The Conditions of a grid or a Dynamic Combo Box are its " + ConditionsProperty + " property. As a single propertyName, Conditions and Condition "
               + "reach it too; Where, propertyNames[] and query do not. "
               + "genexus_properties action=get control=" + control + " propertyName=" + ConditionsProperty + " reads them with a versionToken; "
               + "action=set with value=<expression>, expectedVersion=<versionToken> and rollbackOnFailure=true writes them. "
               + "A <condition> child or attribute in the WebForm XML is not part of the layout format and is dropped.";
    }
}
