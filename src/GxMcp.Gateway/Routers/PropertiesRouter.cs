using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class PropertiesRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_properties": return ConvertPropertiesToolCall(args);
                default: return null;
            }
        }

        private object? ConvertPropertiesToolCall(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) return null;

            if (action.Equals("set", System.StringComparison.OrdinalIgnoreCase))
            {
                // A TemplateXml patch is one value of the properties map, and the batch ignores a propertyName or value sent
                // with it. Refusing here keeps the call from writing something other than what was asked.
                if (SendsTemplateXmlPatchWithSingleProperty(args))
                    throw new UsageException("InvalidPropertyBatch",
                        "A TemplateXml patch goes inside properties; propertyName and value cannot be sent with it.");
                return new
                {
                    module = "Property",
                    action = "Set",
                    target = args?["name"]?.ToString(),
                    propertyName = args?["propertyName"]?.ToString(),
                    value = args?["value"]?.ToString(),
                    properties = args?["properties"] as JObject,
                    // The ordered parameter list of a Dynamic Combo/List Box control
                    // (propertyName=DataProviderParameters); null for every other set.
                    expressions = args?["expressions"] as JArray,
                    control = args?["control"]?.ToString(),
                    type = args?["type"]?.ToString(),
                    expectedVersion = args?["expectedVersion"]?.ToString() ?? args?["baseVersion"]?.ToString(),
                    dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                    // issue #60 — validationMode="specify" runs the inline Specify pass after
                    // the property write; rollbackOnFailure restores on spec errors.
                    validationMode = args?["validationMode"]?.ToString(),
                    rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true
                };
            }

            if (action.Equals("move", System.StringComparison.OrdinalIgnoreCase))
            {
                // Issue #238: folder/module are first-class aliases of destination (matching
                // genexus_create action=object naming) and carry their kind with them.
                string? targetModule = args?["targetModule"]?.ToString();
                string? explicitDestination = args?["destination"]?.ToString();
                string? folderAlias = args?["folder"]?.ToString();
                string? moduleAlias = args?["module"]?.ToString();
                return new
                {
                    module = "Property",
                    action = "Move",
                    target = args?["name"]?.ToString(),
                    destination = explicitDestination ?? targetModule ?? folderAlias ?? moduleAlias,
                    targetModule,
                    folder = !string.IsNullOrWhiteSpace(folderAlias) ? folderAlias : args?["destModule"]?.ToString(),
                    module_ = moduleAlias,
                    destModule = args?["destModule"]?.ToString(),
                    destKind = args?["destKind"]?.ToString()
                        ?? (!string.IsNullOrWhiteSpace(folderAlias) ? "Folder"
                            : !string.IsNullOrWhiteSpace(moduleAlias) ? "Module"
                            : string.IsNullOrWhiteSpace(explicitDestination)
                            && !string.IsNullOrWhiteSpace(targetModule) ? "Module" : null),
                    dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                    // expectedVersion is the version token of action=set; a move takes either spelling.
                    baseVersion = !string.IsNullOrWhiteSpace(args?["baseVersion"]?.ToString())
                        ? args?["baseVersion"]?.ToString()
                        : args?["expectedVersion"]?.ToString(),
                    rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                    type = args?["type"]?.ToString()
                };
            }

            var propNameToken = args?["propertyName"];
            var propNamesToken = args?["propertyNames"];
            if (propNamesToken == null && propNameToken is JArray)
            {
                propNamesToken = propNameToken;
                propNameToken = null;
            }

            return new
            {
                module = "Property",
                action = "Get",
                target = args?["name"]?.ToString(),
                targets = args?["targets"] as JArray,
                control = args?["control"]?.ToString(),
                type = args?["type"]?.ToString(),
                propertyName = propNameToken?.ToString(),
                propertyNames = propNamesToken,
                properties = args?["properties"],
                projection = args?["projection"]?.ToString(),
                query = args?["query"]?.ToString(),
                reconcileTimedOutWrite = args?["reconcileTimedOutWrite"]?.ToObject<bool?>() ?? false
            };
        }

        private static bool SendsTemplateXmlPatchWithSingleProperty(JObject? args)
        {
            bool singleSent = NamesAProperty(args?["propertyName"]) || IsSent(args?["value"]);
            return singleSent && args?["properties"] is JObject properties
                && properties.Properties().Any(p => p.Value is JObject && IsTemplateXmlName(p.Name));
        }

        // The official name and the SDK name of the template XML of a WorkWithPlus template.
        private static bool IsTemplateXmlName(string name)
            => string.Equals(name.Trim(), "TemplateXml", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Trim(), "WWPTemplate_TemplateXml", System.StringComparison.OrdinalIgnoreCase);

        // A key sent as JSON null is not sent.
        private static bool IsSent(JToken? token) => token != null && token.Type != JTokenType.Null;

        // propertyName names a property only when it has text, as the worker reads it: a blank name is not sent.
        private static bool NamesAProperty(JToken? token)
            => IsSent(token) && !(token!.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.ToString()));
    }
}
