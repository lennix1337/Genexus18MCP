using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class VariablesTextReceipt
    {
        internal static bool Applies(string part) => string.Equals(part, "Variables", StringComparison.OrdinalIgnoreCase);

        // A DSL comparison cannot certify IDs, descriptions or explicit/default SDK property state.
        internal static void Limit(JObject receipt)
        {
            if (receipt == null) return;
            receipt["verificationScope"] = "variables-dsl";
            receipt["metadataVerified"] = false;
            if (receipt["verified"]?.Type == JTokenType.Boolean)
            {
                if (receipt["textVerified"] == null) receipt["textVerified"] = receipt["verified"].DeepClone();
                receipt["verified"] = false;
            }
            if (receipt["persistedVerified"]?.Type == JTokenType.Boolean)
            {
                if (receipt["persistedTextVerified"] == null) receipt["persistedTextVerified"] = receipt["persistedVerified"].DeepClone();
                receipt["persistedVerified"] = false;
            }
            if (receipt["rollback"] is JObject rollback)
            {
                rollback["verificationScope"] = "variables-dsl";
                if (rollback["textVerified"] == null) rollback["textVerified"] = rollback["verified"]?.DeepClone() ?? new JValue(false);
                rollback["verified"] = false;
                rollback["rolledBack"] = false;
                rollback["atomic"] = false;
            }
            if (receipt["rolledBack"]?.Value<bool?>() == true || receipt["stateRestored"]?.Value<bool?>() == true)
            {
                receipt["textRestored"] = true;
                receipt["rolledBack"] = false;
                receipt["stateRestored"] = false;
                receipt["persisted"] = JValue.CreateNull();
            }
            if (receipt["commitState"]?.ToString() == "RolledBack") receipt["commitState"] = "Unknown";
            if (receipt["persistenceState"]?.ToString() == "Restored") receipt["persistenceState"] = "TextRestoredMetadataUnverified";
            bool noSave = receipt["savePathExercised"]?.Value<bool?>() == false
                || receipt["result"]?["savePathExercised"]?.Value<bool?>() == false;
            if (!noSave && receipt["changed"]?.Value<bool?>() == false)
            {
                receipt["textChanged"] = false;
                receipt["changed"] = JValue.CreateNull();
                receipt.Remove("noChangeReason");
                if (receipt["code"]?.ToString() == "WriteNoChange") receipt["code"] = "WriteApplied";
            }
            if (receipt["result"] is JObject result) Limit(result);
        }
    }
}
