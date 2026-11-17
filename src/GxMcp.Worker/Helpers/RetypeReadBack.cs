using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #1037: an in-place retype is confirmed only by the Variables part read back
    /// from the KB after the save. A pattern instance (WorkWithPlus) can regenerate the
    /// variable on save, so the object in memory says VARCHAR(256) while the KB keeps
    /// VARCHAR(40); the answer must follow the KB.
    /// </summary>
    internal static class RetypeReadBack
    {
        internal static JObject Enforce(JObject receipt, string varName)
        {
            if (receipt == null || !string.Equals((string)receipt["status"], "ok", StringComparison.OrdinalIgnoreCase)) return receipt;
            if (!(receipt["result"] is JObject result) || result["retyped"]?.Type != JTokenType.Boolean || !(bool)result["retyped"]) return receipt;
            string persistedType = (string)result["persistedType"];
            if (string.IsNullOrWhiteSpace(persistedType) || string.IsNullOrWhiteSpace(varName)) return receipt;

            string source = (string)receipt["source"];
            bool reReadConfirmed = receipt["postSaveVerification"]?["reReadConfirmed"]?.Type == JTokenType.Boolean
                && (bool)receipt["postSaveVerification"]["reReadConfirmed"];
            if (!reReadConfirmed || source == null)
            {
                result["persistedState"] = "unverified";
                return receipt;
            }

            if (!VariableDeclarationParser.TryParse("&" + varName + " : " + persistedType, out var expected)) return receipt;
            VariableDeclaration actual = Find(source, varName);
            if (actual != null && Matches(expected, actual, persistedType.Contains("("))) return receipt;

            string readBack = actual == null ? null : Format(actual);
            receipt["status"] = "error";
            receipt["code"] = "WriteNotPersisted";
            receipt["message"] = actual == null
                ? $"Variable '&{varName}' was reported retyped to '{persistedType}', but it is missing from the Variables part read back after the save."
                : $"Variable '&{varName}' was reported retyped to '{persistedType}', but the Variables part read back after the save still declares it as '{readBack}'. The change was not persisted.";
            receipt["hint"] = "The object may regenerate this variable on save (a pattern instance such as WorkWithPlus owns its variables). Change it where the pattern defines it, then read the Variables part again.";
            result["retyped"] = false;
            result["persistedState"] = "notPersisted";
            result["readBackType"] = readBack;
            return receipt;
        }

        internal static string EnforceJson(string response, string varName)
        {
            JObject receipt;
            try { receipt = JObject.Parse(response); }
            catch (Exception) { return response; }
            return Enforce(receipt, varName).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static VariableDeclaration Find(string source, string varName)
        {
            foreach (string line in source.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (VariableDeclarationParser.TryParse(line, out var declaration)
                    && string.Equals(declaration.Name, varName, StringComparison.OrdinalIgnoreCase))
                    return declaration;
            }
            return null;
        }

        private static bool Matches(VariableDeclaration expected, VariableDeclaration actual, bool sized)
        {
            bool sameType = string.Equals(expected.TypeName, actual.TypeName, StringComparison.OrdinalIgnoreCase);
            if (VariableInjector.TryParseDbType(expected.TypeName, out var expectedType)
                && VariableInjector.TryParseDbType(actual.TypeName, out var actualType))
                sameType = expectedType == actualType;
            if (!sameType) return false;
            return !sized || (expected.Length == actual.Length && expected.Decimals == actual.Decimals);
        }

        private static string Format(VariableDeclaration declaration) =>
            declaration.TypeName + (declaration.Length > 0
                ? "(" + declaration.Length + (declaration.Decimals > 0 ? "," + declaration.Decimals : "") + ")"
                : "");
    }
}
