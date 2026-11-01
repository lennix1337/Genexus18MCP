using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class DsoValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; }

        public DsoValidationResult()
        {
            IsValid = true;
            Errors = new List<string>();
        }
    }

    /// <summary>
    /// genexus_layout action=design_system — read a Design System Object's (DSO) tokens,
    /// theme classes, images and referenced DSOs. Uses the native helper when available
    /// and source-part compatibility fallbacks for SDKs whose helper surface is smaller.
    /// </summary>
    public class DesignSystemService
    {
        private static readonly Regex QuotedStringsRegex = new Regex(
            @"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex BlockCommentsRegex = new Regex(
            @"/\*.*?\*/",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex LineCommentsRegex = new Regex(
            @"//.*?$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public DesignSystemService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            string action = args?["action"]?.ToString()?.ToLowerInvariant();
            if (action == "validate")
            {
                string source = args?["source"]?.ToString() ?? args?["content"]?.ToString();
                var validation = ValidateDso(source);
                return McpResponse.Ok(
                    code: "DsoValidated",
                    result: new JObject
                    {
                        ["isValid"] = validation.IsValid,
                        ["errors"] = new JArray(validation.Errors)
                    });
            }

            string name = args?["name"]?.ToString();
            KBObject dso = null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                try { dso = _objects?.FindObject(name, "DesignSystem"); } catch { }
                if (dso == null)
                {
                    try
                    {
                        foreach (KBObject o in model.Objects.GetAll())
                        {
                            if (string.Equals(o?.TypeDescriptor?.Name, "DesignSystem", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                dso = o;
                                if (dso != null) break;
                            }
                        }
                    }
                    catch { }
                }
                if (dso == null)
                    return McpResponse.Err("ObjectNotFound", "Design System Object '" + name + "' not found.", "Check the name (genexus_query type:DesignSystem), or omit name to use the first DSO.", target: name);
            }
            else
            {
                // Fast path: search index bucket
                try
                {
                    var index = _objects?.GetLoadedIndexOrNull();
                    if (index?.TypeIndex != null && index.Objects != null
                        && index.TypeIndex.TryGetValue("DesignSystem", out var dsKeys))
                    {
                        string firstKey = null;
                        lock (dsKeys) { foreach (var k in dsKeys) { firstKey = k; break; } }
                        if (firstKey != null && index.Objects.TryGetValue(firstKey, out var entry)
                            && !string.IsNullOrEmpty(entry?.Name))
                        {
                            dso = _objects.FindObject(entry.Name, "DesignSystem");
                            if (dso != null) name = entry.Name;
                        }
                    }
                }
                catch { }

                if (dso == null)
                {
                    try
                    {
                        foreach (KBObject o in model.Objects.GetAll())
                        {
                            if (string.Equals(o?.TypeDescriptor?.Name, "DesignSystem", StringComparison.OrdinalIgnoreCase))
                            { dso = o; if (dso != null) { name = o.Name; break; } }
                        }
                    }
                    catch { }
                }

                if (dso == null)
                    return McpResponse.Err("NoDesignSystem", "This KB has no Design System Object.", "Create one with genexus_create type=DesignSystem, or in the GeneXus IDE, then read it again.");
            }

            try
            {
                DesignSystemReadResult read = DesignSystemSdkAdapter.Read(dso);

                return McpResponse.Ok(
                    code: "DesignSystemRetrieved",
                    result: new JObject
                    {
                        ["designSystem"] = name,
                        ["tokenGroups"] = read.TokenGroups,
                        ["classes"] = read.Classes,
                        ["images"] = read.Images,
                        ["referencedDSOs"] = read.ReferencedDSOs,
                        ["source"] = read.Source,
                        ["compatibility"] = read.Compatibility,
                        ["warnings"] = read.Warnings
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("DesignSystemReadFailed", ex.Message, "Check the worker log for the full stack trace.");
            }
        }

        public static Dictionary<string, JObject> ParseDsoTokens(string dsoTokens)
        {
            return DesignSystemSourceParser.ParseTokens(dsoTokens);
        }

        public static Dictionary<string, JObject> ParseDsoClasses(string dsoStyles)
        {
            return DesignSystemSourceParser.ParseClasses(dsoStyles);
        }

        public static DsoValidationResult ValidateDso(string dsoCombined)
        {
            var res = new DsoValidationResult();
            if (string.IsNullOrWhiteSpace(dsoCombined))
            {
                res.IsValid = false;
                res.Errors.Add("Design System source is empty.");
                return res;
            }

            // Strip comments and string literals before validating brace structure to avoid false positives
            string sanitized = StripCommentsAndStrings(dsoCombined);

            int openBraces = 0;
            for (int i = 0; i < sanitized.Length; i++)
            {
                char c = sanitized[i];
                if (c == '{') openBraces++;
                else if (c == '}') openBraces--;

                if (openBraces < 0)
                {
                    res.IsValid = false;
                    res.Errors.Add($"Closing brace '}}' without matching opening brace.");
                    return res;
                }
            }

            if (openBraces != 0)
            {
                res.IsValid = false;
                res.Errors.Add($"Mismatched braces: {openBraces} opening brace(s) '{{' were not closed.");
            }

            return res;
        }

        private static string StripCommentsAndStrings(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // Replace quoted strings "..." and '...' with ""
            string withoutStrings = QuotedStringsRegex.Replace(input, "\"\"");
            // Replace block comments /* ... */ with space
            string withoutBlockComments = BlockCommentsRegex.Replace(withoutStrings, " ");
            // Replace line comments // ... with newline
            string withoutLineComments = LineCommentsRegex.Replace(withoutBlockComments, "");

            return withoutLineComments;
        }

    }
}
