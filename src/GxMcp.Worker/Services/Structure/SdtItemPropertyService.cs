using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>
    /// genexus_structure get_item_property / set_item_property: read and write one typed property of one SDT member
    /// (leaf item or sub-level) without rebuilding the SDT. The arguments are validated here, the protocol lives in
    /// <see cref="SdtItemPropertyEngine"/> and the SDK calls in <see cref="SdkSdtPropertyStore"/>.
    /// </summary>
    public sealed class SdtItemPropertyService
    {
        private const int MaxItemPathDepth = 32;
        private readonly ObjectService _objectService;

        /// <summary>Creates the store for a request; tests replace it with a double.</summary>
        internal Func<string, string, IList<string>, bool, ISdtPropertyStore> StoreFactory { get; set; }

        public SdtItemPropertyService(ObjectService objectService)
        {
            _objectService = objectService;
            StoreFactory = CreateSdkStore;
        }

        internal SdtItemPropertyService(Func<string, string, IList<string>, bool, ISdtPropertyStore> storeFactory)
        {
            StoreFactory = storeFactory;
        }

        public string GetItemProperty(string target, string typeFilter, JObject args)
        {
            try
            {
                List<string> itemPath;
                string pathError;
                if (!TryReadItemPath(args, out itemPath, out pathError))
                    return McpResponse.Err(code: "InvalidItemPath", message: pathError,
                        hint: "e.g. itemPath=[\"debug\"] or [\"Items\",\"Id\"] (member names from the SDT root down).", target: target);

                ISdtPropertyStore store;
                try { store = StoreFactory(target, typeFilter, itemPath, false); }
                catch (SdtUserFacingException ex) { return ex.ToEnvelope(target); }
                return SdtItemPropertyEngine.Get(store, target, ReadText(args?["property"]));
            }
            catch (Exception ex)
            {
                Logger.Error("get_item_property failed: " + ex);
                return McpResponse.Err(code: "SdtItemPropertyReadFailed", message: ex.Message, target: target);
            }
        }

        public string SetItemProperty(string target, string typeFilter, JObject args)
        {
            try
            {
                List<string> itemPath;
                string pathError;
                if (!TryReadItemPath(args, out itemPath, out pathError))
                    return McpResponse.Err(code: "InvalidItemPath", message: pathError,
                        hint: "e.g. itemPath=[\"debug\"] or [\"Items\",\"Id\"] (member names from the SDT root down).",
                        target: target, extra: new JObject { ["persisted"] = false });

                bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                string expectedVersion = ReadText(args?["expectedVersion"]) ?? ReadText(args?["baseVersion"]);

                ISdtPropertyStore store;
                try { store = StoreFactory(target, typeFilter, itemPath, !dryRun); }
                catch (SdtUserFacingException ex) { return ex.ToEnvelope(target); }
                return SdtItemPropertyEngine.Set(store, target, ReadText(args?["property"]),
                    ReadText(args?["value"]), dryRun, expectedVersion);
            }
            catch (Exception ex)
            {
                Logger.Error("set_item_property failed: " + ex);
                return McpResponse.Err(code: "SdtItemPropertyUpdateFailed", message: ex.Message, target: target,
                    extra: new JObject { ["persisted"] = JValue.CreateNull() });
            }
        }

        internal static bool TryReadItemPath(JObject args, out List<string> itemPath, out string error)
        {
            itemPath = new List<string>();
            error = null;
            JToken token = args?["itemPath"];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = "itemPath is required: the names of the SDT members from the root down to the addressed member.";
                return false;
            }
            var array = token as JArray;
            if (array == null)
            {
                error = "itemPath must be an array of member names.";
                return false;
            }
            if (array.Count == 0)
            {
                error = "itemPath must name at least one member.";
                return false;
            }
            if (array.Count > MaxItemPathDepth)
            {
                error = "itemPath is deeper than " + MaxItemPathDepth + " members.";
                return false;
            }
            foreach (JToken segment in array)
            {
                if (segment.Type != JTokenType.String || string.IsNullOrWhiteSpace(segment.ToString()))
                {
                    error = "itemPath entries must be non-empty member names.";
                    return false;
                }
                itemPath.Add(segment.ToString().Trim());
            }
            return true;
        }

        // A JSON boolean or number is accepted for 'value' and read as its text; null and absent are the same.
        internal static string ReadText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>() ? "true" : "false";
            // A JSON number is text in the invariant culture: under pt-BR a float would otherwise read as "1,5".
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return Convert.ToString(((JValue)token).Value, System.Globalization.CultureInfo.InvariantCulture);
            return token.ToString();
        }

        private ISdtPropertyStore CreateSdkStore(string target, string typeFilter, IList<string> itemPath, bool forWrite)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new SdtUserFacingException("TargetRequired", "name is required: the SDT whose member is addressed.");
            var seed = _objectService.FindObject(target, string.IsNullOrWhiteSpace(typeFilter) ? "SDT" : typeFilter);
            if (seed == null)
                throw new SdtUserFacingException(HealingService.FormatNotFoundError(
                    target, _objectService.GetKbService().GetIndexCache().GetIndex()));
            if (!string.Equals(seed.TypeDescriptor?.Name, "SDT", StringComparison.OrdinalIgnoreCase))
                throw new SdtUserFacingException("NotAnSDT", "Object '" + target + "' is not an SDT.",
                    "Member properties apply to Structured Data Types; use genexus_properties for object properties.");
            return new SdkSdtPropertyStore(_objectService, seed, seed.Name, itemPath, forWrite);
        }
    }
}
