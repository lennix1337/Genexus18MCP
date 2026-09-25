using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class LayoutRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_layout": return ConvertLayoutToolCall(args);
                default: return null;
            }
        }

        private object? ConvertLayoutToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            // P2 #10: control/theme catalog over IUserControlsManagerService — a distinct
            // service, not a Layout mutator, so route it to its own worker module.
            if (string.Equals(action, "list_controls", System.StringComparison.OrdinalIgnoreCase))
                return new { module = "UserControls", action = "Run", @params = args };

            // P3: Design System Object tokens/classes/images over DesignSystemHelper.
            if (string.Equals(action, "design_system", System.StringComparison.OrdinalIgnoreCase))
                return new { module = "DesignSystem", action = "Run", @params = args };

            string? objectName = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(objectName))
            {
                objectName = args?["target"]?.ToString();
            }

            string? mappedAction = action switch
            {
                "get_tree" => "GetTree",
                "set_property" => "SetProperty",
                "find_controls" => "FindControls",
                "set_properties" => "SetProperties",
                "inspect_surface" => "InspectSurface",
                "get_preview" => "GetVisualPreview",
                "scan_mutators" => "ScanMutators",
                "rename_printblock" => "RenamePrintBlock",
                "add_printblock" => "AddPrintBlock",
                "delete_printblock" => "DeletePrintBlock",
                "add_report_control" => "AddReportControl",
                "move_report_control" => "MoveReportControl",
                "remove_report_control" => "RemoveReportControl",
                _ => null
            };

            if (mappedAction == null) return null;

            return new
            {
                module = "Layout",
                action = mappedAction,
                target = objectName,
                control = args?["control"]?.ToString(),
                propertyName = args?["propertyName"]?.ToString(),
                value = args?["value"]?.ToString(),
                query = args?["query"]?.ToString(),
                changes = args?["changes"],
                limit = args?["limit"]?.ToObject<int?>(),
                currentName = args?["currentName"]?.ToString(),
                newName = args?["newName"]?.ToString(),
                printBlockName = args?["printBlockName"]?.ToString(),
                height = args?["height"]?.ToObject<int?>(),
                kind = args?["kind"]?.ToString(),
                controlName = args?["controlName"]?.ToString(),
                controlType = args?["controlType"]?.ToString(),
                binding = args?["binding"]?.ToString(),
                caption = args?["caption"]?.ToString(),
                left = args?["left"]?.ToObject<double?>(),
                top = args?["top"]?.ToObject<double?>(),
                x = args?["x"]?.ToObject<double?>(),
                y = args?["y"]?.ToObject<double?>(),
                width = args?["width"]?.ToObject<double?>(),
                heightControl = args?["height"]?.ToObject<double?>(),
                after = args?["after"]?.ToString(),
                below = args?["below"]?.ToString(),
                font = args?["font"]?.ToString(),
                fontName = args?["fontName"]?.ToString(),
                fontSize = args?["fontSize"]?.ToString(),
                alignment = args?["alignment"]?.ToString(),
                picture = args?["picture"]?.ToString(),
                baseVersion = args?["baseVersion"]?.ToString() ?? args?["expectedVersion"]?.ToString(),
                dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true
            };
        }
    }
}
