using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// The <c>fields</c> argument of the query tools as the caller meant it. A client whose schema does
    /// not declare <c>fields</c> as an array sends the array as a string (<c>["name","type"]</c>), which
    /// a comma split would cut into <c>["name"</c> and <c>"type"]</c>.
    /// </summary>
    internal static class FieldsArgument
    {
        /// <summary>
        /// A string holding a JSON array literal is read as that array. Any other token is returned unchanged.
        /// </summary>
        internal static JToken? Normalize(JToken? token)
        {
            if (token == null || token.Type != JTokenType.String) return token;
            string text = token.ToString().Trim();
            if (!text.StartsWith("[", StringComparison.Ordinal) || !text.EndsWith("]", StringComparison.Ordinal)) return token;
            try
            {
                return JToken.Parse(text) is JArray array ? array : token;
            }
            catch (JsonReaderException)
            {
                return token;
            }
        }
    }
}
