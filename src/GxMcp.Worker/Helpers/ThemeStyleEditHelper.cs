using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Reflection bridge for the SDK's two style authoring surfaces:
    /// Theme.ThemeStyles (ThemeStylesPart) and the regular DesignStylesPart
    /// used by objects that expose a StyleSheet. The public SDK types changed
    /// namespace/shape between GeneXus updates, while these methods have stayed
    /// stable, so the bridge avoids a version-specific compile dependency.
    /// </summary>
    public static class ThemeStyleEditHelper
    {
        // The SDK lexer can terminate the x86 Worker on long inline data URIs.
        // Keep these below the observed failure range; larger assets belong in Image objects.
        private const int MaxInlineDataUriLength = 1024;

        public static bool Applies(KBObject obj, string partName, out object part)
        {
            part = null;
            if (obj == null) return false;
            try { part = PartAccessor.GetPart(obj, partName); } catch { }

            bool themePart = IsThemeStylesPart(part);
            bool designPart = IsDesignStylesPart(part);
            bool themeName = ThemeInspector.IsTheme(obj)
                && (string.Equals(partName, "ThemeStyles", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partName, "Theme", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(partName, "StyleSheet", StringComparison.OrdinalIgnoreCase));
            return themePart || designPart || themeName;
        }

        public static string ReadText(object part)
        {
            if (part == null) return null;
            if (IsThemeStylesPart(part))
            {
                string css = InvokeString(part, "GetCssSource");
                if (css != null) return css;
                css = ReadStringProperty(part, "Source");
                return css ?? string.Empty;
            }
            if (IsDesignStylesPart(part))
                return ReadStringProperty(part, "Source") ?? string.Empty;
            return ReadStringProperty(part, "Source") ?? ReadStringProperty(part, "Content");
        }

        /// <summary>
        /// Returns null when the request is structurally valid, otherwise an
        /// actionable validation message. This is pure and is used by unit tests.
        /// </summary>
        public static string ValidateRequest(string content)
        {
            if (content == null) return "Style content is required.";
            if (content.IndexOf('\0') >= 0) return "Style content contains a NUL character.";

            JObject request = TryParseStructured(content, out string parseError);
            if (parseError != null) return parseError;
            string css = request?["css"]?.ToString() ?? request?["source"]?.ToString();
            if (css != null) return ValidateCss(css);
            if (request != null && JsonUtil.IsSupplied(request["properties"]) && request["properties"].Type != JTokenType.Object)
                return "Style properties must be a JSON object.";
            return request == null ? ValidateCss(content) : null;
        }

        /// <summary>
        /// Applies either raw CSS/source or a structured class edit to an SDK part.
        /// No Save is performed here; the caller owns the transaction and object save.
        /// <paramref name="imageExists"/> answers whether an Image object of that name exists in the Knowledge Base
        /// (null when it cannot tell). It is consulted only after the SDK refuses a source, to name the gx-image()
        /// references that cannot be resolved; without it the refusal keeps the SDK's own words.
        /// </summary>
        public static bool TryApply(object part, string content, out JObject details, out string error, Func<string, bool?> imageExists = null)
        {
            details = new JObject();
            error = ValidateRequest(content);
            if (error != null) return false;
            if (part == null) { error = "Style part is not available on the object."; return false; }

            JObject request = TryParseStructured(content, out _);
            bool isThemeStyles = IsThemeStylesPart(part);
            bool isDesignStyles = IsDesignStylesPart(part);

            if (request != null && (request["className"] != null || request["name"] != null))
            {
                if (!isThemeStyles)
                {
                    error = "Structured class edits are supported only for a ThemeStylesPart; use source/css for a StyleSheet.";
                    return false;
                }

                string className = request["className"]?.ToString() ?? request["name"]?.ToString();
                object style = Invoke(part, "GetStyle", new object[] { className });
                if (style == null)
                {
                    error = "Theme class not found: " + className;
                    return false;
                }

                int propertiesApplied = 0;
                JObject properties = request["properties"] as JObject;
                if (properties != null)
                {
                    object data = ReadProperty(style, "Data");
                    if (data == null)
                    {
                        error = "Theme class does not expose editable style data: " + className;
                        return false;
                    }
                    foreach (var property in properties.Properties())
                    {
                        if (!TrySetPropertyValue(data, property.Name, property.Value, out error)) return false;
                        propertiesApplied++;
                    }
                }

                if (request["description"] != null && !TrySetMember(style, "Description", request["description"].ToString(), out error)) return false;
                if (request["baseClass"] != null && !TrySetMember(style, "BaseClass", request["baseClass"].ToString(), out error)) return false;
                if (request["css"] != null)
                {
                    if (!TryInvokeTextImport(style, request["css"].ToString(), details, out error))
                    {
                        error = ExplainRejection(details, error, request["css"].ToString(), imageExists);
                        return false;
                    }
                }

                details["mode"] = "class";
                details["className"] = className;
                details["propertiesApplied"] = propertiesApplied;
                return true;
            }

            string text = request?["css"]?.ToString() ?? request?["source"]?.ToString() ?? content;
            if (isDesignStyles)
            {
                if (!TryValidateNewSource(part, text, details, out error))
                {
                    error = ExplainRejection(details, error, text, imageExists);
                    return false;
                }
                PropertyInfo source = part.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance);
                if (source == null || !source.CanWrite)
                {
                    error = "The StyleSheet part does not expose a writable Source property.";
                    return false;
                }
                source.SetValue(part, text, null);
                details["mode"] = "stylesheet";
                details["sourceLength"] = text?.Length ?? 0;
                return true;
            }

            if (isThemeStyles)
            {
                if (!TryInvokeTextImport(part, text, details, out error))
                {
                    error = ExplainRejection(details, error, text, imageExists);
                    return false;
                }
                details["mode"] = "themeCss";
                details["sourceLength"] = text?.Length ?? 0;
                return true;
            }

            error = "The resolved part is not a supported ThemeStylesPart or DesignStylesPart.";
            return false;
        }

        public static string ValidateCss(string css)
        {
            if (css == null) return "Style content is required.";
            string payloadError = ValidateInlineDataUris(css);
            if (payloadError != null) return payloadError;
            int depth = 0;
            bool quoted = false;
            char quote = '\0';
            bool escaped = false;
            for (int i = 0; i < css.Length; i++)
            {
                char c = css[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == quote) quoted = false;
                    continue;
                }
                if (c == '\'' || c == '"') { quoted = true; quote = c; continue; }
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0) return "Style content contains an unmatched closing brace.";
                }
            }
            return depth == 0 ? null : "Style content contains an unmatched opening brace.";
        }

        internal static bool IsStylePartName(string partName) =>
            string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "StyleSheet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "ThemeStyles", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partName, "Theme", StringComparison.OrdinalIgnoreCase);

        internal static string ValidateInlineDataUris(string css)
        {
            if (css == null) return null;
            for (int start = css.IndexOf("data:", StringComparison.OrdinalIgnoreCase); start >= 0;)
            {
                if (start > 0 && (char.IsLetterOrDigit(css[start - 1]) || css[start - 1] == '-' || css[start - 1] == '_'))
                {
                    start = css.IndexOf("data:", start + 5, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                bool quotedValue = start > 0 && (css[start - 1] == '\'' || css[start - 1] == '"');
                bool payload = false;
                int end = start;
                while (end < css.Length && !char.IsWhiteSpace(css[end])
                    && css[end] != '\'' && css[end] != '"' && css[end] != ')'
                    && css[end] != '{' && css[end] != '}'
                    && (css[end] != ';' || !payload || quotedValue))
                {
                    if (css[end] == ',') payload = true;
                    if (++end - start > MaxInlineDataUriLength)
                        return "Inline data URIs longer than " + MaxInlineDataUriLength
                            + " characters are not sent to the GeneXus style parser because they can terminate the Worker. Use a GeneXus Image object or an external asset URL.";
                }
                start = css.IndexOf("data:", end, StringComparison.OrdinalIgnoreCase);
            }
            return null;
        }

        private static JObject TryParseStructured(string content, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(content) || content.TrimStart()[0] != '{') return null;
            try { return JObject.Parse(content); }
            catch (Exception ex) { error = "Structured style request is not valid JSON: " + ex.Message; return null; }
        }

        private static bool TryValidateNewSource(object part, string source, JObject details, out string error)
        {
            error = null;
            try
            {
                MethodInfo method = part.GetType().GetMethod("ValidateNewSource", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (method == null) return true;
                object result = method.Invoke(part, new object[] { source });
                if (result is bool && !(bool)result)
                {
                    error = "The GeneXus SDK rejected the StyleSheet source during validation.";
                    // ValidateNewSource validates into an OutputMessages it never returns, so the reason is lost to the caller.
                    JArray messages = CaptureSdkValidationMessages(part, source);
                    if (messages.Count > 0)
                    {
                        details["sdkMessages"] = messages;
                        error += " SDK messages: " + SummarizeMessages(messages);
                    }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "StyleSheet validation failed: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// Asks the SDK again the question <c>ValidateNewSource</c> just answered "no" to, this time keeping its answer.
        /// <c>DesignStylesPart.ValidateNewSource(source)</c> is <c>Validate(source, InheritedClasses, ChildrenDesignSystemNames,
        /// new OutputMessages())</c>: the same private method with a collector it throws away, in every GeneXus 18 update
        /// checked (U11, U12, U15, U16). Calling it with a collector of our own returns what the SDK said (level and text)
        /// and leaves the part alone - the public <c>ValidateData</c> would validate the part's current Source instead, which
        /// means replacing the Source (marking the object modified) and making the SDK drop its parsed members. Empty when
        /// the part has no such method or the SDK says nothing.
        /// </summary>
        private static JArray CaptureSdkValidationMessages(object part, string source)
        {
            var captured = new JArray();
            try
            {
                MethodInfo validate = part.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Validate" && IsSourceValidatorSignature(m.GetParameters()));
                if (validate == null) return captured;

                object inheritedClasses = ReadPropertyOrThrow(part, "InheritedClasses");
                object childrenDesignSystems = ReadPropertyOrThrow(part, "ChildrenDesignSystemNames");
                object output = Activator.CreateInstance(validate.GetParameters()[3].ParameterType);
                validate.Invoke(part, new[] { source, inheritedClasses, childrenDesignSystems, output });

                // The collector enumerates every message. Its OnlyMessages member is the informational ones alone, which
                // would leave out exactly the errors and warnings that explain a refusal.
                var messages = output as IEnumerable;
                if (messages == null) return captured;
                foreach (object message in messages)
                {
                    if (message == null) continue;
                    Type messageType = message.GetType();
                    string text = SdkDiagnosticsHelper.ReadStringMember(message, messageType, "Text")
                        ?? SdkDiagnosticsHelper.ReadStringMember(message, messageType, "Description")
                        ?? SdkDiagnosticsHelper.ReadStringMember(message, messageType, "Message")
                        ?? message.ToString();
                    var entry = new JObject
                    {
                        ["level"] = SdkDiagnosticsHelper.ReadMember(message, messageType, "Level")?.ToString() ?? "Error",
                        ["text"] = text
                    };
                    string code = SdkDiagnosticsHelper.ReadStringMember(message, messageType, "ErrorCode");
                    if (code != null) entry["code"] = code;
                    captured.Add(entry);
                    if (captured.Count >= MaxSdkMessages) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[THEME-WRITE] SDK validation messages not captured: " + (ex.InnerException ?? ex).Message);
            }
            return captured;
        }

        // Validate(string source, IEnumerable<DesignSystemClass> inheritedClasses, IEnumerable<string> childrenDSOs, OutputMessages output)
        private static bool IsSourceValidatorSignature(ParameterInfo[] parameters) =>
            parameters.Length == 4
            && parameters[0].ParameterType == typeof(string)
            && parameters[3].ParameterType.Name == "OutputMessages";

        private static object ReadPropertyOrThrow(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new MissingMemberException(target.GetType().Name, name);
            return property.GetValue(target, null);
        }

        private const int MaxSdkMessages = 20;
        private const int MaxSummarizedMessages = 5;

        private static string SummarizeMessages(JArray messages)
        {
            var parts = new List<string>();
            foreach (JToken message in messages.Take(MaxSummarizedMessages))
            {
                string text = (message["text"]?.ToString() ?? string.Empty).Trim();
                parts.Add(text.Length > 240 ? text.Substring(0, 240) + "..." : text);
            }
            string summary = string.Join("; ", parts);
            return messages.Count > MaxSummarizedMessages
                ? summary + " (+" + (messages.Count - MaxSummarizedMessages) + " more in sdkMessages)"
                : summary;
        }

        /// <summary>
        /// The gx-image() references of a style source that name no Image object in the Knowledge Base, in order of
        /// appearance. A reference whose existence cannot be determined (the probe answers null or throws) is not listed.
        /// </summary>
        internal static IReadOnlyList<string> FindUnresolvedImageReferences(string source, Func<string, bool?> imageExists)
        {
            var unresolved = new List<string>();
            if (imageExists == null || string.IsNullOrEmpty(source)) return unresolved;
            foreach (string name in DesignSystemSourceParser.ExtractImageNames(source))
            {
                bool? exists;
                try { exists = imageExists(name); }
                catch { exists = null; }
                if (exists == false) unresolved.Add(name);
            }
            return unresolved;
        }

        /// <summary>
        /// After the SDK refused a style source: when the source points at Image objects that do not exist, say so. The
        /// SDK refuses such a source and its public validation keeps the reason to itself, so the generic answer sends the
        /// caller off to rewrite a source that is fine. The names always go to <c>details.unresolvedImageReferences</c>.
        /// They are named as the CAUSE (<c>details.rejectionCause</c>, the code <c>ThemeStyleUnresolvedReferences</c>) only
        /// when an SDK message confirms it - the message says that one of them is not there. Without that confirmation (the
        /// SDK said something else, a syntax error which it reports before it looks at any reference, or nothing the helper
        /// could read) the missing images are a fact about the source, not a proven reason for this refusal: they are only
        /// listed beside the SDK's own words and the generic code stays. The SDK words for a missing image are
        /// "&lt;Name&gt; not found" (Error, no code; GeneXus 18 U16, live), and only for the first one it meets. The residual
        /// case is a message about something else that cites the same whole name (a class called like the missing image):
        /// the image is missing all the same, so the worst outcome is a second refusal after it is created.
        /// </summary>
        private static string ExplainRejection(JObject details, string sdkError, string source, Func<string, bool?> imageExists)
        {
            IReadOnlyList<string> unresolved = FindUnresolvedImageReferences(source, imageExists);
            if (unresolved.Count == 0) return sdkError;

            var names = new JArray();
            foreach (string name in unresolved) names.Add(name);
            details["unresolvedImageReferences"] = names;
            bool one = unresolved.Count == 1;
            string missing = unresolved.Count + (one ? " Image object that does" : " Image objects that do")
                + " not exist in this Knowledge Base: " + string.Join(", ", unresolved);

            var messages = details["sdkMessages"] as JArray;
            bool confirmed = messages != null && messages.Any(m => ConfirmsMissingImage((string)m["text"], unresolved));
            if (!confirmed)
                return sdkError + " The source also refers to " + missing + ".";

            details["rejectionCause"] = UnresolvedImagesCause;
            return "The style source refers to " + missing
                + ". The GeneXus SDK refuses a style source while a gx-image() reference cannot be resolved. Create or import "
                + (one ? "this Image object" : "these Image objects") + " first, then write the style again. SDK result: " + sdkError;
        }

        private const string UnresolvedImagesCause = "unresolvedImageReferences";

        // The grammar's own complaints about the shape of the source ("mismatched input '"Logo"' expecting ..."). They quote the
        // offending token, so a message that merely contains an image name can be about its spelling, not about its absence.
        private static readonly Regex ParserSyntaxMessage = new Regex(
            @"\b(mismatched input|extraneous input|no viable alternative|token recognition error|missing .{1,40} at|expecting)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // True when the SDK's message names one of the missing images, as a whole name, and is not a complaint about syntax:
        // it says the image is not there. A name that only occurs inside a longer one ("Log" in "SynthLogo") does not count.
        private static bool ConfirmsMissingImage(string message, IReadOnlyList<string> unresolved)
        {
            if (string.IsNullOrWhiteSpace(message) || ParserSyntaxMessage.IsMatch(message)) return false;
            return unresolved.Any(name => Regex.IsMatch(message, @"(?<![\w.])" + Regex.Escape(name) + @"(?!\w)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        /// <summary>
        /// The error code of a refused style write: a source refused because of Image objects that do not exist has a code
        /// of its own; any other refusal keeps the generic one.
        /// </summary>
        internal static string FailureCode(JObject details)
            => string.Equals(details?["rejectionCause"]?.ToString(), UnresolvedImagesCause, StringComparison.Ordinal)
                ? "ThemeStyleUnresolvedReferences"
                : "ThemeStyleApplyFailed";

        private const string GenericFailureGuidance =
            "Read the style part first; use raw CSS/source for StyleSheet or {className, properties} for a Theme class.";

        /// <summary>
        /// The message and the hint of a refused style write, from its error code and the details <see cref="TryApply"/>
        /// produced. Without anything new to say (no SDK messages, no unresolved images) both are the historical generic
        /// ones; the hint only ever gains sentences. "Nothing was saved" because the structured class edit sets properties
        /// on the in-memory object before the SDK refuses the CSS, so "changed" would not always be true.
        /// </summary>
        internal static void DescribeFailure(string code, JObject details, out string message, out string hint)
        {
            if (string.Equals(code, "ThemeStyleUnresolvedReferences", StringComparison.Ordinal))
            {
                message = "Theme/StyleSheet write refused: the style source refers to Image objects that do not exist in the Knowledge Base.";
                hint = "Create or import the Image objects listed in unresolvedImageReferences (genexus_create type=Image, or genexus_transfer import), then repeat the write. Nothing was saved.";
                return;
            }

            message = "Theme/StyleSheet write failed.";
            hint = details?["sdkMessages"] != null
                ? "The SDK's own validation messages are in sdkMessages; correct what they report in the style source and write again. If they do not explain it: "
                    + char.ToLowerInvariant(GenericFailureGuidance[0]) + GenericFailureGuidance.Substring(1)
                : GenericFailureGuidance;
            if (details?["unresolvedImageReferences"] != null)
                hint += " The Image objects listed in unresolvedImageReferences do not exist in the Knowledge Base either: create or import them too.";
        }

        private static bool TryInvokeTextImport(object target, string text, JObject details, out string error)
        {
            error = null;
            try
            {
                MethodInfo method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => (m.Name == "ImportCss" || m.Name == "ImportCSS")
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (method == null) { error = "The SDK style part does not expose ImportCss(string)."; return false; }
                object result = method.Invoke(target, new object[] { text });
                if (result is bool && !(bool)result)
                {
                    error = "The GeneXus SDK rejected the CSS source.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                // The SDK's ImportCss returns nothing: a refusal is the exception it throws, and that text is the SDK's own reason.
                string reason = (ex.InnerException ?? ex).Message;
                error = "The GeneXus SDK rejected the CSS source: " + reason;
                AddSdkMessage(details, reason);
                return false;
            }
        }

        private static void AddSdkMessage(JObject details, string text)
        {
            if (details == null || string.IsNullOrWhiteSpace(text)) return;
            var list = details["sdkMessages"] as JArray;
            if (list == null)
            {
                list = new JArray();
                details["sdkMessages"] = list;
            }
            list.Add(new JObject { ["level"] = "Error", ["text"] = text });
        }

        private static bool TrySetPropertyValue(object data, string name, JToken value, out string error)
        {
            error = null;
            try
            {
                MethodInfo method = data.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetPropertyValue" && m.GetParameters().Length == 2);
                if (method == null) { error = "Theme style data does not expose SetPropertyValue for '" + name + "'."; return false; }
                Type targetType = method.GetParameters()[1].ParameterType;
                object converted = ConvertToken(value, targetType);
                method.Invoke(data, new[] { (object)name, converted });
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not set theme property '" + name + "': " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static object ConvertToken(JToken value, Type targetType)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            if (targetType == typeof(string) || targetType == typeof(object)) return value.ToString();
            if (targetType.IsEnum) return Enum.Parse(targetType, value.ToString(), true);
            return Convert.ChangeType(value.ToObject<object>(), targetType, CultureInfo.InvariantCulture);
        }

        private static bool TrySetMember(object target, string name, string value, out string error)
        {
            error = null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite)
                {
                    error = "Theme class does not expose a writable '" + name + "' property.";
                    return false;
                }
                if (property.PropertyType == typeof(string)) property.SetValue(target, value, null);
                else property.SetValue(target, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture), null);
                return true;
            }
            catch (Exception ex) { error = "Could not set theme member '" + name + "': " + ex.Message; return false; }
        }

        private static object ReadProperty(object target, string name)
        {
            try { return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target, null); }
            catch { return null; }
        }

        private static string ReadStringProperty(object target, string name)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property?.GetValue(target, null)?.ToString();
            }
            catch { return null; }
        }

        private static string InvokeString(object target, string name)
        {
            try
            {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method?.Invoke(target, null)?.ToString();
            }
            catch { return null; }
        }

        private static bool IsThemeStylesPart(object part)
        {
            return IsPartType(part, "ThemeStylesPart");
        }

        private static bool IsDesignStylesPart(object part)
        {
            return IsPartType(part, "DesignStylesPart");
        }

        private static bool IsPartType(object part, string typeName)
        {
            return part != null
                && part.GetType().Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object Invoke(object target, string name, object[] args)
        {
            try
            {
                foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != name || method.GetParameters().Length != (args?.Length ?? 0)) continue;
                    try { return method.Invoke(target, args); } catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
