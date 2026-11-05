using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class StructureRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_structure": return ConvertStructureToolCall(args);
                default: return null;
            }
        }

        private object? ConvertStructureToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            string? mappedAction = action switch
            {
                "get_visual" => "GetVisualStructure",
                "update_visual" => "UpdateVisualStructure",
                "get_indexes" => "GetVisualIndexes",
                "create_index" => "CreateIndex",
                "drop_index" => "DropIndex",
                "set_attribute" => "SetAttributeProperties",
                "set_level" => "SetLevelProperties",
                "set_domain" => "SetDomainProperties",
                "get_logic" => "GetLogicStructure",
                "update_group" => "UpdateGroupStructure",
                "move_attribute" => "MoveAttribute",
                // Typed read / write of one property of one SDT member without rebuilding the SDT.
                "get_item_property" => "GetSdtItemProperty",
                "set_item_property" => "SetSdtItemProperty",
                // Issue #97: native TransactionLevel.Items attribute removal (lets agents
                // drop + re-add a misclassified subtype attribute to force re-derivation)
                // and the subtype-classification guard-rail.
                "remove_attribute" => "RemoveAttribute",
                "check_subtypes" => "CheckSubtypes",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Structure",
                action = mappedAction,
                target = args?["name"]?.ToString(),
                type = args?["type"]?.ToString(),
                payload = args?["payload"]?.ToString(),
                transactionModule = args?["module"]?.ToString(),
                attribute = args?["attribute"]?.ToString(),
                before = args?["before"]?.ToString(),
                after = args?["after"]?.ToString(),
                position = args?["position"]?.ToObject<int?>(),
                level = args?["level"]?.ToString(),
                levelPath = args?["levelPath"],
                // get_item_property / set_item_property: the SDT member path, the property and its new value.
                itemPath = args?["itemPath"],
                property = args?["property"]?.ToString(),
                value = args?["value"],
                dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                baseVersion = args?["baseVersion"]?.ToString(),
                expectedVersion = args?["expectedVersion"]?.ToString(),
                // issue #60 — validationMode="specify" runs the inline Specify pass after a
                // structure write; rollbackOnFailure restores the pre-write state on spec errors.
                validationMode = args?["validationMode"]?.ToString(),
                rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>()
                    ?? (string.Equals(action, "create_index", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(action, "update_visual", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(action, "move_attribute", StringComparison.OrdinalIgnoreCase))
            };
        }
    }
}
