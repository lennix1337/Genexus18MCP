using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed class SemanticOpsService
    {
        public string Apply(string xml, string objectKind, IEnumerable<SemanticOp> ops)
        {
            var doc = XDocument.Parse(xml);
            foreach (var op in ops)
                Dispatch(doc, objectKind, op);
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// v2.6.6 FR#13 — per-op apply that records success/failure per index. In
        /// <c>strict</c> mode the first failure aborts (mirrors legacy
        /// <see cref="Apply(string,string,System.Collections.Generic.IEnumerable{SemanticOp})"/>).
        /// In <c>best-effort</c> mode failing ops are skipped and the doc retains
        /// the successful ones. <c>only</c> mode is identical to <c>best-effort</c>
        /// at this layer — the caller decides not to persist.
        /// </summary>
        public OpsApplyOutcome ApplyWithResults(string xml, string objectKind, IList<SemanticOp> ops, string validate)
        {
            var doc = XDocument.Parse(xml);
            string mode = NormalizeMode(validate);
            var results = ApplyOps(ops, mode, op => Dispatch(doc, objectKind, op), out bool aborted);

            return new OpsApplyOutcome
            {
                Xml = doc.ToString(SaveOptions.DisableFormatting),
                Results = results,
                Aborted = aborted,
                Mode = mode
            };
        }

        /// <summary>
        /// Runs the op list, recording one <see cref="OpResult"/> per op and
        /// deciding when a failure stops the batch.
        ///
        /// Both entry points — the XML form and the Transaction structure DSL form
        /// — ran this loop verbatim, differing only in the delegate they dispatched
        /// through. That loop is where the public contract lives: a
        /// <see cref="UsageException"/> is a caller error and keeps its own code,
        /// anything else is <c>internal_error</c>, a failed op is recorded rather
        /// than thrown, and in <c>strict</c> mode the first failure aborts the rest.
        ///
        /// Duplicated, those four rules were eight places a client could read a
        /// different result ordering, a different abort boundary, or a different
        /// error code depending on which surface it called.
        /// </summary>
        private static List<OpResult> ApplyOps(
            IList<SemanticOp> ops,
            string mode,
            Action<SemanticOp> dispatch,
            out bool aborted)
        {
            var results = new List<OpResult>(ops.Count);
            aborted = false;

            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                try
                {
                    dispatch(op);
                    results.Add(new OpResult { Index = i, Op = op.Op, Ok = true });
                }
                catch (UsageException ux)
                {
                    results.Add(new OpResult { Index = i, Op = op.Op, Ok = false, Reason = ux.Message, Code = ux.Code });
                    if (mode == "strict") { aborted = true; break; }
                }
                catch (Exception ex)
                {
                    results.Add(new OpResult { Index = i, Op = op.Op, Ok = false, Reason = ex.Message, Code = "internal_error" });
                    if (mode == "strict") { aborted = true; break; }
                }
            }

            return results;
        }

        internal static string NormalizeMode(string validate)
        {
            if (string.IsNullOrWhiteSpace(validate)) return "strict";
            string v = validate.Trim().ToLowerInvariant();
            switch (v)
            {
                case "strict":
                case "best-effort":
                case "best_effort":
                case "besteffort":
                    return v == "best_effort" || v == "besteffort" ? "best-effort" : v;
                case "only":
                case "validate-only":
                case "validate_only":
                    return "only";
                default:
                    return "strict";
            }
        }

        public sealed class OpResult
        {
            public int Index;
            public string Op;
            public bool Ok;
            public string Reason;
            public string Code;

            public JObject ToJson()
            {
                var j = new JObject
                {
                    ["index"] = Index,
                    ["op"] = Op,
                    ["ok"] = Ok
                };
                if (!Ok)
                {
                    if (!string.IsNullOrEmpty(Reason)) j["reason"] = Reason;
                    if (!string.IsNullOrEmpty(Code)) j["code"] = Code;
                }
                return j;
            }
        }

        public sealed class OpsApplyOutcome
        {
            public string Xml;
            // issue #34: DSL-text payload for the Transaction-Structure ops path (which
            // manipulates the Structure DSL text, not the part XML). Xml stays null there.
            public string Text;
            public List<OpResult> Results;
            public bool Aborted;
            public string Mode;
        }

        // issue #34: the Transaction Structure part does NOT serialize to XML with a
        // <Structure> root the way the XML-ops handlers assume — that path fails with
        // "<Structure> not found" against a real KB. Attribute ops on a Transaction are
        // instead applied to the Structure DSL text (the same text genexus_read part=Structure
        // returns and the mode=patch path writes), then persisted through the DSL parser.
        private static readonly Regex _dslAttrLine = new Regex(
            @"^(?<indent>\s*)(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<key>\*?)\s*:\s*(?<type>[^/]+?)\s*(?<comment>//.*)?$",
            RegexOptions.Compiled);

        public static bool IsTransactionStructureAttrOp(string op)
            => op == "add_attribute" || op == "set_attribute" || op == "remove_attribute";

        /// <summary>
        /// Bug #1: dryRun capability check. GeneXus refuses removing a key attribute from a
        /// Transaction level (the persist fails, but the text-only dryRun would report ok:true).
        /// Detect the key marker (<c>name*</c>) straight from the Structure DSL so dryRun can
        /// warn about the refusal the real write will hit. Returns true when <paramref name="name"/>
        /// is a key attribute in <paramref name="dsl"/>.
        /// </summary>
        public static bool IsKeyAttributeInDsl(string dsl, string name)
        {
            if (string.IsNullOrEmpty(dsl) || string.IsNullOrEmpty(name)) return false;
            var lines = dsl.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (var line in lines)
            {
                var m = _dslAttrLine.Match(line);
                if (m.Success
                    && string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
                    return m.Groups["key"].Value == "*";
            }
            return false;
        }

        public OpsApplyOutcome ApplyTransactionStructureDsl(string dsl, IList<SemanticOp> ops, string validate)
        {
            string mode = NormalizeMode(validate);
            var lines = (dsl ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n")
                .Split('\n').ToList();
            var results = ApplyOps(ops, mode, op => ApplyDslOp(lines, op), out bool aborted);

            return new OpsApplyOutcome
            {
                Text = string.Join("\n", lines).Trim(),
                Results = results,
                Aborted = aborted,
                Mode = mode
            };
        }

        private static int FindDslAttrLine(List<string> lines, string name)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var m = _dslAttrLine.Match(lines[i]);
                if (m.Success && string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string BuildDslAttrLine(string name, string type, bool key, string description, string formula = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(name);
            if (key) sb.Append('*');
            sb.Append(" : ").Append(type);
            if (!string.IsNullOrEmpty(description))
                sb.Append(" // \"").Append(description).Append('"');
            if (!string.IsNullOrWhiteSpace(formula))
            {
                if (string.IsNullOrEmpty(description)) sb.Append(" // ");
                else sb.Append(", ");
                sb.Append("[Formula: ").Append(formula.Trim()).Append(']');
            }
            return sb.ToString();
        }

        private static int GetLogicalEntryEnd(List<string> lines, int start)
        {
            if (start < 0 || start >= lines.Count) return start;
            var pending = new System.Text.StringBuilder();
            pending.Append(lines[start]);
            int depth = Parsers.TransactionDslParser.FormulaBracketDepth(pending.ToString());
            int end = start;
            while (depth > 0 && end + 1 < lines.Count)
            {
                end++;
                pending.Append(' ').Append(lines[end].Trim());
                depth = Parsers.TransactionDslParser.FormulaBracketDepth(pending.ToString());
            }
            return end;
        }

        private static void ApplyDslOp(List<string> lines, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", op.Op + ": name required");

            switch (op.Op)
            {
                case "add_attribute":
                {
                    string type = op.Args["type"]?.ToString();
                    if (string.IsNullOrEmpty(type))
                        throw new UsageException("usage_error", "add_attribute: type required");
                    if (FindDslAttrLine(lines, name) >= 0)
                        throw new UsageException("usage_error", "attribute '" + name + "' already exists");
                    bool key = op.Args["key"]?.ToObject<bool?>() ?? false;
                    string desc = op.Args["description"]?.ToString();
                    string formula = op.Args["formula"]?.ToString();
                    string after = op.Args["after"]?.ToString();
                    string newLine = BuildDslAttrLine(name, type, key, desc, formula);
                    // Insert after an explicit sibling when requested; otherwise append
                    // after the last root-level attribute. Nested levels stay intact.
                    int insertAt = lines.Count;
                    if (!string.IsNullOrWhiteSpace(after))
                    {
                        int afterIndex = FindDslAttrLine(lines, after);
                        if (afterIndex < 0)
                            throw new UsageException("usage_error", "add_attribute: after attribute '" + after + "' not found");
                        int afterEnd = GetLogicalEntryEnd(lines, afterIndex);
                        insertAt = afterEnd + 1;
                    }
                    else
                    {
                        int lastAttrStart = -1;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var m = _dslAttrLine.Match(lines[i]);
                            if (m.Success && m.Groups["indent"].Value.Length == 0)
                                lastAttrStart = i;
                        }
                        if (lastAttrStart >= 0)
                        {
                            int lastAttrEnd = GetLogicalEntryEnd(lines, lastAttrStart);
                            insertAt = lastAttrEnd + 1;
                        }
                    }
                    lines.Insert(insertAt, newLine);
                    break;
                }
                case "set_attribute":
                {
                    int idx = FindDslAttrLine(lines, name);
                    if (idx < 0)
                        throw new UsageException("usage_error", "attribute '" + name + "' not found");
                    int entryEnd = GetLogicalEntryEnd(lines, idx);
                    var m = _dslAttrLine.Match(lines[idx]);
                    string type = op.Args["type"]?.ToString() ?? m.Groups["type"].Value.Trim();
                    bool key = m.Groups["key"].Value == "*";
                    string desc = op.Args["description"]?.ToString();
                    string formula = op.Args["formula"]?.ToString();
                    string rebuilt;
                    if (desc == null)
                    {
                        // Preserve the existing inline comment when no new description is given.
                        string existingComment = m.Groups["comment"].Value;
                        rebuilt = m.Groups["indent"].Value + BuildDslAttrLine(name, type, key, null, formula);
                        if (!string.IsNullOrEmpty(existingComment)) rebuilt += " " + existingComment;
                    }
                    else
                    {
                        rebuilt = m.Groups["indent"].Value + BuildDslAttrLine(name, type, key, desc, formula);
                    }
                    if (entryEnd > idx)
                    {
                        lines.RemoveRange(idx + 1, entryEnd - idx);
                    }
                    lines[idx] = rebuilt;
                    break;
                }
                case "remove_attribute":
                {
                    int idx = FindDslAttrLine(lines, name);
                    if (idx < 0)
                        throw new UsageException("usage_error", "attribute '" + name + "' not found");
                    int entryEnd = GetLogicalEntryEnd(lines, idx);
                    lines.RemoveRange(idx, entryEnd - idx + 1);
                    break;
                }
                default:
                    throw new UsageException("usage_error",
                        "op '" + op.Op + "' not supported for Transaction Structure");
            }
        }

        private static void Dispatch(XDocument doc, string kind, SemanticOp op)
        {
            switch (op.Op)
            {
                case "set_attribute" when kind == "Transaction":
                    SetAttribute(doc, op);
                    break;
                case "add_attribute" when kind == "Transaction":
                    AddAttribute(doc, op);
                    break;
                case "remove_attribute" when kind == "Transaction":
                    RemoveAttribute(doc, op);
                    break;
                case "add_rule" when kind == "Transaction" || kind == "Procedure" || kind == "WebPanel":
                    AddRule(doc, op);
                    break;
                case "remove_rule" when kind == "Transaction" || kind == "Procedure" || kind == "WebPanel":
                    RemoveRule(doc, op);
                    break;
                case "set_property":
                    SetProperty(doc, op);
                    break;
                default:
                    throw new UsageException("usage_error",
                        "op '" + op.Op + "' not supported for " + kind);
            }
        }

        private static void SetAttribute(XDocument doc, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "set_attribute: name required");

            var attr = doc.Descendants("Attribute")
                .FirstOrDefault(a => (string)a.Element("Name") == name);
            if (attr == null)
                throw new UsageException("usage_error",
                    "attribute '" + name + "' not found");

            string type = op.Args["type"]?.ToString();
            if (type != null)
                attr.SetElementValue("Type", type);
        }

        private static void AddAttribute(XDocument doc, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "add_attribute: name required");
            string type = op.Args["type"]?.ToString();
            if (string.IsNullOrEmpty(type))
                throw new UsageException("usage_error", "add_attribute: type required");

            var structure = doc.Descendants("Structure").FirstOrDefault();
            if (structure == null)
                throw new UsageException("usage_error",
                    "add_attribute: this part's XML has no <Structure> root. For a Transaction, attribute ops are handled by the DSL path (route via genexus_structure or a patch with part=Structure and only add_attribute/set_attribute/remove_attribute ops) — the XML-ops path does not apply here.");

            structure.Add(new XElement("Attribute",
                new XElement("Name", name),
                new XElement("Type", type)));
        }

        private static void RemoveAttribute(XDocument doc, SemanticOp op)
        {
            string name = op.Args["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                throw new UsageException("usage_error", "remove_attribute: name required");

            var attr = doc.Descendants("Attribute")
                .FirstOrDefault(a => (string)a.Element("Name") == name);
            if (attr == null)
                throw new UsageException("usage_error",
                    "attribute '" + name + "' not found");
            attr.Remove();
        }

        private static void AddRule(XDocument doc, SemanticOp op)
        {
            string text = op.Args["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
                throw new UsageException("usage_error", "add_rule: text required");

            var textIssue = TextPayloadGuard.Analyze(text);
            if (textIssue != null)
                throw new UsageException(textIssue.ErrorCode, TextPayloadGuard.BuildMessage("text", textIssue));

            var rules = doc.Descendants("Rules").FirstOrDefault();
            if (rules == null)
            {
                rules = new XElement("Rules");
                doc.Root?.Add(rules);
            }
            rules.Add(new XElement("Rule", new XElement("Text", text)));
        }

        private static void RemoveRule(XDocument doc, SemanticOp op)
        {
            var rulesContainer = doc.Descendants("Rules").FirstOrDefault();
            if (rulesContainer == null)
                throw new UsageException("usage_error", "remove_rule: <Rules> not found");

            var rules = rulesContainer.Elements("Rule").ToList();
            var indexToken = op.Args["index"];
            if (indexToken != null)
            {
                int idx = (int)indexToken;
                if (idx < 0 || idx >= rules.Count)
                    throw new UsageException("usage_error",
                        "remove_rule: index " + idx + " out of range");
                rules[idx].Remove();
                return;
            }

            string match = op.Args["match"]?.ToString();
            if (string.IsNullOrEmpty(match))
                throw new UsageException("usage_error", "remove_rule: match or index required");

            var target = rules.FirstOrDefault(r =>
            {
                string t = (string)r.Element("Text");
                return t != null && t.Contains(match);
            });
            if (target == null)
                throw new UsageException("usage_error",
                    "remove_rule: no rule matching '" + match + "'");
            target.Remove();
        }

        private static void SetProperty(XDocument doc, SemanticOp op)
        {
            string path = op.Args["path"]?.ToString();
            if (string.IsNullOrEmpty(path))
                throw new UsageException("usage_error", "set_property: path required");
            string value = op.Args["value"]?.ToString() ?? "";
            string name = path.TrimStart('/');
            var elem = doc.Root?.Element(name);
            if (elem == null)
                throw new UsageException("usage_error",
                    "set_property: path '" + path + "' not found");
            elem.Value = value;
        }
    }
}
