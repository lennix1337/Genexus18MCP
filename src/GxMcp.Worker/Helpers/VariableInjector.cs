using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Services;
using Artech.Genexus.Common.Objects;
using Artech.Common.Collections;
using Artech.Genexus.Common;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    public static class VariableInjector
    {
        private static readonly HashSet<string> StandardVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Pgmname", "Pgmdesc", "Today", "Time", "Mode", "Message", "EventName", "CtlName"
        };

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }

        public static void InjectVariables(KBObject obj, string code, Models.SearchIndex index = null)
        {
            var variablesPart = GxMcp.Worker.Structure.PartAccessor.GetVariablesPart(obj) ?? obj.Parts.Get<VariablesPart>();
            if (variablesPart == null) return;

            // Scan for &-tokens on source with string literals and comments blanked out, so an
            // ampersand that is DATA rather than a variable — a URL query ("...&status=paid"),
            // an HTML entity ("&nbsp;"), a commented-out line — no longer auto-declares a spurious
            // VARCHAR(100) variable (issue #45). The literal text itself is untouched on save; only
            // the name-extraction view is masked.
            string scanCode = StripLiteralsAndComments(code);

            var varNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var varNamesList = new List<string>();
            var sdtCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int n = scanCode.Length;
            int i = 0;
            while (i < n)
            {
                if (scanCode[i] == '&')
                {
                    int start = i + 1;
                    int j = start;
                    while (j < n && IsWordChar(scanCode[j]))
                    {
                        j++;
                    }

                    if (j > start)
                    {
                        string name = scanCode.Substring(start, j - start);
                        if (varNamesSet.Add(name))
                        {
                            varNamesList.Add(name);
                        }

                        if (j < n && scanCode[j] == '.')
                        {
                            sdtCandidates.Add(name);
                        }

                        i = j;
                        continue;
                    }
                }
                i++;
            }

            var existingVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in variablesPart.Variables)
            {
                if (!string.IsNullOrEmpty(v.Name)) existingVars.Add(v.Name);
            }

            bool injectedAny = false;
            foreach (var varName in varNamesList)
            {
                if (!existingVars.Contains(varName))
                {
                    global::Artech.Genexus.Common.Variable v = CreateVariable(variablesPart, varName, index, sdtCandidates.Contains(varName));
                    if (v != null)
                    {
                        variablesPart.Variables.Add(v);
                        existingVars.Add(varName);
                        injectedAny = true;
                        Logger.Info($"Injected variable: {varName} into {obj.Name}");
                    }
                }
            }

            if (injectedAny)
            {
                try
                {
                    var pType = variablesPart.GetType();
                    var pDirtyProp = pType.GetProperty("Dirty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                  ?? pType.GetProperty("IsDirty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (pDirtyProp != null && pDirtyProp.CanWrite)
                    {
                        pDirtyProp.SetValue(variablesPart, true);
                    }
                }
                catch { /* best-effort */ }
            }
        }

        // Blank out string-literal contents and comments in GeneXus source, preserving length and
        // newlines, so only ampersands in CODE are seen by the auto-declare scanner. Handles GeneXus
        // doubled-quote escaping inside strings (""), both string delimiters, and // and /* */
        // comments. issue #45. Used only for &-token extraction — never for what gets persisted.
        internal static string StripLiteralsAndComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return code ?? string.Empty;
            var sb = new System.Text.StringBuilder(code.Length);
            int i = 0, n = code.Length;
            while (i < n)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    char delim = c;
                    sb.Append(' '); i++;
                    while (i < n)
                    {
                        char d = code[i];
                        if (d == delim)
                        {
                            // Doubled delimiter inside a string is an escaped quote — stay in-string.
                            if (i + 1 < n && code[i + 1] == delim) { sb.Append("  "); i += 2; continue; }
                            sb.Append(' '); i++; break;
                        }
                        sb.Append(d == '\n' || d == '\r' ? d : ' ');
                        i++;
                    }
                    continue;
                }
                if (c == '/' && i + 1 < n && code[i + 1] == '/')
                {
                    while (i < n && code[i] != '\n' && code[i] != '\r') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < n && code[i + 1] == '*')
                {
                    sb.Append("  "); i += 2;
                    while (i < n && !(code[i] == '*' && i + 1 < n && code[i + 1] == '/'))
                    {
                        sb.Append(code[i] == '\n' || code[i] == '\r' ? code[i] : ' ');
                        i++;
                    }
                    if (i < n) { sb.Append("  "); i += 2; }
                    continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        internal static global::Artech.Genexus.Common.Variable CreateVariable(VariablesPart part, string name, Models.SearchIndex index = null, bool sdtMemberAccessHint = false)
        {
            global::Artech.Genexus.Common.Variable v = new global::Artech.Genexus.Common.Variable(part);
            v.Name = name;

            // 0. SDT/BC heuristic: name starts with Sdt/SDT or used as &var.Field — try resolving as SDT first
            bool sdtNamePrefix = name.StartsWith("Sdt", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SDT", StringComparison.Ordinal);
            if (sdtMemberAccessHint || sdtNamePrefix)
            {
                // Try direct match by the variable name
                var tryNames = new List<string> { name };
                // Strip Sdt/SDT prefix and try suffix-only too
                if (name.StartsWith("Sdt", StringComparison.OrdinalIgnoreCase) && name.Length > 3) tryNames.Add(name.Substring(3));

                foreach (var candidateName in tryNames)
                {
                    var sdtObj = ResolveTypeObject(part.Model, candidateName);
                    if (sdtObj != null && sdtObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    {
                        // A failed bind must not return a half-bound variable: fall
                        // through to attribute/primitive inference instead.
                        if (BindVariableToSdt(v, sdtObj, out _))
                        {
                            Logger.Info($"Injected variable {name} bound to SDT {sdtObj.Name} (heuristic: prefix={sdtNamePrefix}, memberAccess={sdtMemberAccessHint})");
                            return v;
                        }
                    }
                    if (sdtObj is Transaction bc && bc.IsBusinessComponent)
                    {
                        BindVariableToBC(v, sdtObj);
                        Logger.Info($"Injected variable {name} bound to BC {sdtObj.Name}");
                        return v;
                    }
                }
                // Friction-report #3: when the source uses &Var.Field, the variable is structurally
                // an SDT/BC reference. Falling through to the VARCHAR(100) default poisons subsequent
                // validation with confusing "VARCHAR has no member 'Field'" errors. Skip the injection
                // entirely so the agent gets a single clear "variable not declared" signal and can
                // call genexus_add_variable with the correct SDT typeName.
                if (sdtMemberAccessHint)
                {
                    Logger.Warn($"Variable {name} used with .Field access but no matching SDT/BC found in KB. Skipping auto-inject (will surface as undeclared variable; declare via genexus_add_variable typeName=<SDT>).");
                    return null;
                }
            }

            // 1. Inherit from Attribute (FAST INDEX LOOKUP)
            if (index != null)
            {
                string key = "Attribute:" + name;
                if (index.Objects.TryGetValue(key, out var entry))
                {
                    if (TryParseDbType(entry.DataType, out var itype))
                    {
                        v.Type = itype;
                        v.Length = entry.Length;
                        v.Decimals = entry.Decimals;
                        // issue #281: the index gives only the resolved shape. Try to
                        // bind the live Attribute object so VarBasedOn/picture survive.
                        try
                        {
                            var indexedAttr = FindAttribute(part.Model, name);
                            if (indexedAttr != null)
                            {
                                v.Type = indexedAttr.Type;
                                v.Length = indexedAttr.Length;
                                v.Decimals = indexedAttr.Decimals;
                                v.Signed = indexedAttr.Signed;
                                try { DomainPropertyApplier.ApplyAttributeBasedOn((object)v, (object)indexedAttr); } catch { }
                                try { DomainPropertyApplier.ClearDomainBasedOn((object)v); } catch { }
                                try { v.SetPropertyValue("DataTypeString", "Attribute:" + indexedAttr.Name); } catch { }
                            }
                        }
                        catch { }
                        Logger.Info($"Injected variable {name} inheriting from INDEXED attribute {name}");
                        return v;
                    }
                }
            }

            // Fallback (SDK lookup - only if not in index or index not provided)
            var attribute = FindAttribute(part.Model, name);
            if (attribute != null)
            {
                v.Type = attribute.Type;
                v.Length = attribute.Length;
                v.Decimals = attribute.Decimals;
                v.Signed = attribute.Signed;
                // issue #281: preserve the Attribute: binding itself, not just the
                // resolved primitive shape. A copy of Type/Length alone flattens
                // VarBasedOn/DataTypeString to Numeric(10) and changes ATT_PICTURE
                // (9999999999 -> ZZZZZZZZZ9). Setting AttributeBasedOn keeps the
                // picture/semantics and makes untyped add a working recovery path.
                try { DomainPropertyApplier.ApplyAttributeBasedOn((object)v, (object)attribute); } catch { }
                try { DomainPropertyApplier.ClearDomainBasedOn((object)v); } catch { }
                try { v.SetPropertyValue("DataTypeString", "Attribute:" + attribute.Name); } catch { }
                Logger.Info($"Injected variable {name} inheriting from SDK attribute {attribute.Name}");
                return v;
            }

            // 2. Naming Heuristics
            string lowerName = name.ToLower();

            // Boolean
            if (lowerName.StartsWith("is") || lowerName.StartsWith("has") || lowerName.StartsWith("flg") || 
                lowerName.Contains("ativo") || lowerName.Contains("pode") || lowerName.EndsWith("ok"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.Boolean;
                Logger.Info($"Injected Boolean variable: {name}");
                return v;
            }

            // Date / DateTime
            if (lowerName.EndsWith("data") || lowerName.EndsWith("dt") || lowerName.Contains("emissao") || lowerName.Contains("vencimento"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.DATE;
                Logger.Info($"Injected Date variable: {name}");
                return v;
            }
            if (lowerName.Contains("hora") || lowerName.Contains("timestamp") || lowerName.Contains("moment"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.DATETIME;
                Logger.Info($"Injected DateTime variable: {name}");
                return v;
            }

            // Numeric
            if (lowerName.EndsWith("id") || lowerName.EndsWith("seq") || lowerName.EndsWith("qtd") || 
                lowerName.Contains("valor") || lowerName.Contains("preco") || lowerName.Contains("total"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.NUMERIC;
                v.Length = 10;
                v.Decimals = lowerName.Contains("valor") || lowerName.Contains("preco") || lowerName.Contains("total") ? 2 : 0;
                Logger.Info($"Injected Numeric variable: {name} ({v.Length},{v.Decimals})");
                return v;
            }

            // 3. Fallback: VarChar(100)
            v.Type = global::Artech.Genexus.Common.eDBType.VARCHAR;
            v.Length = 100;
            Logger.Info($"Injected Default VarChar variable: {name}");

            return v;
        }

        // FR#1 + FR#13 (friction-report 2026-05-14) + FR#3 (2026-05-19): Layout XML uses
        // AttID="var:N" — the SDK's stable layout id is the C# instance property
        // Variable.Id. Accessed via reflection because the SDK assembly doesn't expose it
        // as a typed interface member, and GetPropertyValue("Id") returns null (the
        // Properties bag doesn't carry Id). Live-probe confirmed: TotalHorasCredito.Id=22
        // matches AttID="var:22" in ListaAtiCPAlunoUniGra layout XML, SaldoHoras.Id=33
        // matches "var:33", etc. System vars Today/Time/Pgmname/Pgmdesc get ids 1-4
        // (WWP creates them first), and deleted variables leave gaps in the ID sequence
        // — so enumeration-position fallback is wrong for any non-trivial object.
        public static int? GetVariableInternalId(global::Artech.Genexus.Common.Variable v, int fallbackIndex)
        {
            if (v == null) return null;

            // Primary: Variable.Id via C# reflection.
            try
            {
                var idProp = v.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                if (idProp != null && idProp.CanRead)
                {
                    object raw = idProp.GetValue(v);
                    if (raw is int i && i > 0) return i;
                    if (raw is short s && s > 0) return s;
                    if (raw is long l && l > 0 && l <= int.MaxValue) return (int)l;
                }
            }
            catch { }

            // Fallback for resilience across SDK versions: try the Properties bag with names
            // that might carry the id in builds where Variable.Id is renamed/missing.
            foreach (var prop in new[] { "InternalId", "VariableId", "VarId", "Index", "AttId", "Number" })
            {
                try
                {
                    object raw = v.GetPropertyValue(prop);
                    if (raw is int i && i > 0) return i;
                    if (raw is short s && s > 0) return s;
                    if (raw is long l && l > 0 && l <= int.MaxValue) return (int)l;
                    if (raw is string ss && int.TryParse(ss, out var parsed) && parsed > 0) return parsed;
                }
                catch { }
            }

            // Last resort. Almost always WRONG for objects touched by WorkWithPlus patterns
            // since those inject system vars early — kept only so behavior is defined.
            return fallbackIndex;
        }

        public static int? GetVariableInternalId(object v, int fallbackIndex)
        {
            if (v == null) return null;
            if (v is global::Artech.Genexus.Common.Variable typed)
                return GetVariableInternalId(typed, fallbackIndex);
            try
            {
                var idProp = v.GetType().GetProperty("Id",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                object raw = idProp?.GetValue(v, null);
                if (raw is int i && i > 0) return i;
                if (raw is short s && s > 0) return s;
                if (raw is long l && l > 0 && l <= int.MaxValue) return (int)l;
                if (raw is string text && int.TryParse(text, out var parsed) && parsed > 0) return parsed;
            }
            catch { }
            return fallbackIndex;
        }

        public static string GetVariablesAsText(KBObject obj)
        {
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart == null) return string.Empty;
            return GetVariablesAsText(varPart);
        }

        public static string GetVariablesAsText(VariablesPart varPart)
        {
            var sb = new System.Text.StringBuilder();
            foreach (global::Artech.Genexus.Common.Variable v in varPart.Variables)
            {
                string typeRepr = ResolveTypeRepresentation(varPart.Model, v);
                string arraySuffix = VariableDimensionSupport.FormatDeclarationSuffix(v);
                string collectionSuffix = v.IsCollection ? " Collection" : "";
                sb.AppendLine(string.Format("&{0}{1} : {2}{3}", v.Name, arraySuffix, typeRepr, collectionSuffix));
            }
            return sb.ToString();
        }

        private static bool _loggedVarProps = false;

        private static string TryResolveBoundObjectName(global::Artech.Genexus.Common.Variable v, KBModel model)
        {
            if (model == null) return null;

            // One-time diagnostic dump to discover the actual property name carrying SDT/BC binding
            if (!_loggedVarProps && v.Type == global::Artech.Genexus.Common.eDBType.GX_SDT)
            {
                try
                {
                    var dump = new System.Text.StringBuilder();
                    var propsProp = v.GetType().GetProperty("Properties");
                    if (propsProp != null)
                    {
                        var coll = propsProp.GetValue(v) as System.Collections.IEnumerable;
                        if (coll != null)
                        {
                            foreach (object p in coll)
                            {
                                try
                                {
                                    string name = (string)p.GetType().GetProperty("Name")?.GetValue(p);
                                    object val = p.GetType().GetProperty("Value")?.GetValue(p);
                                    if (val != null) dump.Append(name + "=" + val.GetType().Name + "[" + val.ToString() + "]; ");
                                }
                                catch { }
                            }
                        }
                    }
                    Logger.Info("[VAR INNER PROPS] " + v.Name + ": " + dump.ToString());
                    _loggedVarProps = true;
                }
                catch { _loggedVarProps = true; }
            }

            // Fast path: GX18 stores the SDT/BC name directly in DataTypeString
            try
            {
                object dts = v.GetPropertyValue("DataTypeString");
                if (dts is string dtsStr && !string.IsNullOrEmpty(dtsStr)) return dtsStr;
            }
            catch { }

            // Friction-report #4: BindVariableToSdt stores the structural reference in ATTCUSTOMTYPE
            // (AttCustomType.Guid actually carries the SDT *name*, per the constructor comment).
            // When DataTypeString isn't persisted (older KBs or read-only setter), this is the
            // authoritative source — without it we serialize the bound SDT variable as "GX_SDT(4)".
            try
            {
                object custom = v.GetPropertyValue("ATTCUSTOMTYPE");
                if (custom != null)
                {
                    var ct = custom.GetType();
                    string guidVal = ct.GetProperty("Guid")?.GetValue(custom) as string
                                  ?? ct.GetField("Guid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(custom) as string
                                  ?? ct.GetField("m_guid", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(custom) as string;
                    if (!string.IsNullOrEmpty(guidVal))
                    {
                        // First try as object name (the convention this codebase uses).
                        if (model != null)
                        {
                            try
                            {
                                foreach (var candidate in model.Objects.GetByName(null, null, guidVal))
                                {
                                    if (candidate != null) return candidate.Name;
                                }
                            }
                            catch { }
                            // Could also be a stringified guid — fall through to TryGetObjectFromKey.
                            var byKey = TryGetObjectFromKey(model, guidVal);
                            if (byKey != null) return byKey.Name;
                        }
                        // No model lookup possible — surface the raw token rather than GX_SDT(4).
                        return guidVal;
                    }
                }
            }
            catch { }

            // Fallback: try known key-bearing properties
            string[] candidateProps = { "DataType", "DataTypeKey", "ItemType", "BasedOn", "BasedOnKey", "TypeKey", "ObjectKey", "DataItemTypeName" };
            foreach (var prop in candidateProps)
            {
                object value = null;
                try { value = v.GetPropertyValue(prop); } catch { continue; }
                if (value == null) continue;

                KBObject obj = TryGetObjectFromKey(model, value);
                if (obj != null) return obj.Name;
            }

            // Fallback: enumerate all properties of the variable and look for any value
            // that resolves to an object in the model
            try
            {
                foreach (var prop in v.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    object value;
                    try { value = prop.GetValue(v); } catch { continue; }
                    if (value == null) continue;
                    KBObject obj = TryGetObjectFromKey(model, value);
                    if (obj != null && (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)
                                      || (obj is Transaction trn && trn.IsBusinessComponent)))
                        return obj.Name;
                }
            }
            catch { }

            return null;
        }

        private static KBObject TryGetObjectFromKey(KBModel model, object key)
        {
            try
            {
                if (key is global::Artech.Udm.Framework.EntityKey ek) return model.Objects.Get(ek);
                if (key is Guid g) return model.Objects.Get(g);
                if (key is string s && Guid.TryParse(s, out var gp)) return model.Objects.Get(gp);
            }
            catch { }
            return null;
        }

        private static string ResolveTypeRepresentation(KBModel model, global::Artech.Genexus.Common.Variable v)
        {
            // issue #281: an attribute-based variable carries a primitive Type plus
            // AttributeBasedOn. Without this check both intact (Attribute:CttCar)
            // and flattened (Numeric(10)) variables serialize as NUMERIC(10),
            // hiding the picture/semantics loss. Surface the binding first.
            try
            {
                string attrBasedOn = DomainPropertyApplier.GetAttributeBasedOnName((object)v);
                if (!string.IsNullOrEmpty(attrBasedOn)) return "Attribute:" + attrBasedOn;
            }
            catch { }
            // Domain binding wins over raw type
            try
            {
                if (v.DomainBasedOn != null) return v.DomainBasedOn.Name;
            }
            catch { }

            // SDT or BC binding via DataType property (stored as object Key)
            if (v.Type == global::Artech.Genexus.Common.eDBType.GX_SDT || v.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
            {
                string boundName = TryResolveBoundObjectName(v, model);
                if (!string.IsNullOrEmpty(boundName)) return boundName;
                // Fallback: emit raw enum + length so user sees something is up
                return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
            }

            // Built-in GeneXus data type (WebSession, HttpClient, ...): DataTypeString holds the type
            // name. Without this a WebSession variable serializes as "GX_USRDEFTYP(4)" and round-tripping
            // through an edit would drop the type (issue #33). GX_EXTERNAL_OBJECT is covered too so any
            // external-object-category built-in reads back by name rather than "GX_EXTERNAL_OBJECT(4)"
            // (issue #45).
            if (v.Type == global::Artech.Genexus.Common.eDBType.GX_USRDEFTYP
                || v.Type == global::Artech.Genexus.Common.eDBType.GX_EXTERNAL_OBJECT)
            {
                try
                {
                    if (v.GetPropertyValue("DataTypeString") is string dts && !string.IsNullOrEmpty(dts)) return dts;
                }
                catch { }
                return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
            }

            return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
        }

        public static void SetVariablesFromText(VariablesPart part, string text)
        {
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                // Format: &Name : Type(Length,Decimals) [Collection]
                // issue #281: allow ':' in the type token so "Attribute:<name>"
                // round-trips instead of being truncated to "Attribute".
                if (VariableDeclarationParser.TryParse(line, out var declaration))
                {
                    string name = declaration.Name;
                    string typeStr = declaration.TypeName;
                    int length = declaration.Length;
                    int decimals = declaration.Decimals;
                    bool isCollection = declaration.IsCollection;

                    seenVars.Add(name);

                    var v = part.Variables.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (v == null)
                    {
                        v = new global::Artech.Genexus.Common.Variable(part);
                        v.Name = name;
                        part.Variables.Add(v);
                    }

                    // issue #281: attribute-based variables serialize as
                    // "Attribute:<name>". Bind the live Attribute so picture and
                    // VarBasedOn survive a text round-trip.
                    if (TryParseAttributeReference(typeStr, out string textAttrName))
                    {
                        var textAttr = FindAttribute(part.Model, textAttrName);
                        if (textAttr == null)
                            throw new InvalidOperationException("Attribute '" + textAttrName + "' not found in KB.");
                        if (!BindVariableToAttribute(v, textAttr, out var textAttrFailure))
                            throw new InvalidOperationException("VariableTypeNotPersisted: " + textAttrFailure);
                        Logger.Info($"Resolved variable {name} type to Attribute:{textAttr.Name}");
                    }
                    // 1. Map string type to eDBType (including Aliases)
                    else if (TryParseDbType(typeStr, out var dbType))
                    {
                        v.Type = dbType;
                        v.Length = length;
                        v.DomainBasedOn = null;
                        try { DomainPropertyApplier.ClearAttributeBasedOn((object)v); } catch { }
                        v.SetPropertyValue("DataType", null); // Reset user type if it was set
                    }
                    else if (ResolveDomain(part.Model, typeStr, part.KBObject?.Module) is global::Artech.Genexus.Common.Objects.Domain textDomain)
                    {
                        // Resolve Domains before the generic type registry. The registry represents
                        // a Domain as GX_DOM_REF/ATTCUSTOMTYPE (displayed as dom:<name>), which is a
                        // parser/display token and is not a valid persisted variable reference.
                        if (!BindVariableToDomain(v, textDomain, out var domainFailure))
                            throw new InvalidOperationException("VariableTypeNotPersisted: " + domainFailure);
                        Logger.Info($"Resolved variable {name} type to Domain: {textDomain.QualifiedName}");
                    }
                    else if (TryBindGenexusDataType(v, typeStr))
                    {
                        // Built-in GeneXus data types (HttpClient, WebSession, Location, ...) via the
                        // SDK type registry (issue #45). Without this the DSL silently left the var at
                        // its default NUMERIC(4) type for any non-primitive built-in.
                        Logger.Info($"Resolved variable {name} type to GeneXus data type {typeStr}");
                    }
                    else if (TryBindBuiltinUserDefinedType(v, typeStr))
                    {
                        // Legacy hardcoded WebSession fallback (issue #33).
                        Logger.Info($"Resolved variable {name} type to built-in user-defined type {typeStr}");
                    }
                    else
                    {
                        // 2. Resolve as Domain, SDT, or BC
                        var targetObj = ResolveTypeObject(part.Model, typeStr);
                        if (targetObj != null)
                        {
                            if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                            {
                                if (!BindVariableToDomain(v, dom, out var domainFailure))
                                    throw new InvalidOperationException("VariableTypeNotPersisted: " + domainFailure);
                            }
                            else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!BindVariableToSdt(v, targetObj, out var sdtFailure, typeStr))
                                    throw new InvalidOperationException("VariableTypeNotPersisted: " + sdtFailure);
                            }
                            else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                            {
                                BindVariableToBC(v, targetObj);
                            }
                            Logger.Info($"Resolved variable {name} type to {targetObj.TypeDescriptor.Name}: {targetObj.Name}");
                        }
                        else if (VariableDeclarationParser.TrySplitModuleQualifiedTypeName(typeStr, out _, out string moduleName))
                        {
                            throw new InvalidOperationException("Module-qualified type '" + typeStr + "' was not found in module '" + moduleName + "'.");
                        }
                    }

                    // Collection and fixed-size dimensions are different GeneXus
                    // concepts. Apply the array metadata after the type/binding
                    // setters (which can reset property-bag state), then keep the
                    // collection flag last for the existing type-reset workaround.
                    if (declaration.Dimensions > 0 && isCollection)
                        throw new InvalidOperationException("Variable '" + name + "' cannot be both a collection and a fixed-size array.");

                    // Collection flag LAST: setting v.Type (above) resets IsCollection in the SDK,
                    // so assigning it before the type silently produced a non-collection variable
                    // (its .Count failed as "unknown function"). Only the typed add path — which
                    // sets collection after the type — yielded a real collection until now (issue #45).
                    try { v.IsCollection = isCollection; } catch { /* not all types are collectible */ }
                    if (declaration.Dimensions > 0)
                    {
                        var dimensionSizes = new JArray(declaration.DimensionSizes.Cast<object>().ToArray());
                        if (!VariableDimensionSupport.TryApply(v, declaration.Dimensions, dimensionSizes, out var dimensionFailure))
                            throw new InvalidOperationException("Variable '" + name + "' dimensions could not be applied: " + dimensionFailure);
                    }
                    else if (!VariableDimensionSupport.TryClear(v, out var clearDimensionFailure))
                    {
                        throw new InvalidOperationException("Variable '" + name + "' dimensions could not be cleared: " + clearDimensionFailure);
                    }
                }
            }

            // Remove variables not in the text, except standard variables
            var toRemove = part.Variables
                .Where(v => !seenVars.Contains(v.Name) && !StandardVariables.Contains(v.Name))
                .ToList();

            foreach (var v in toRemove)
            {
                part.Variables.Remove(v);
                Logger.Info($"Removed variable {v.Name} (no longer in text)");
            }
        }

        public static bool BindVariableToSdt(global::Artech.Genexus.Common.Variable v, KBObject sdtObj, out string failure, string typeName = null)
        {
            failure = null;
            if (v == null || sdtObj == null)
            {
                failure = "Variable or SDT is null.";
                return false;
            }
            Logger.Info($"[BindVariableToSdt] Binding {v.Name} -> SDT {sdtObj.Name} (Guid={sdtObj.Guid})");

            // GeneXus stores the actual structural type reference in ATTCUSTOMTYPE. For an SDT the
            // custom type carries the object NAME as its guid string with dataType 254 (the SDT
            // category); the SDK resolves that to the StructureTypeReference at save time. The
            // "Reference X by name can't be saved" error surfaces if a real guid string is used here.
            // Build the structural reference BEFORE mutating: a null construction must fail
            // closed without touching (and half-clearing) the variable's existing bindings.
            var inst = BuildAttCustomType(sdtObj.GetType().Assembly, sdtObj.Name, 254, null);
            if (inst == null)
            {
                failure = "Could not construct the native SDT type reference for '" + sdtObj.Name + "'.";
                Logger.Error("[BindVariableToSdt] " + failure);
                return false;
            }

            try
            {
                // A variable retyped onto an SDT must not keep stale Domain or
                // Attribute bindings: they would conflict with the structural
                // reference and the persisted type becomes build-dependent.
                try { DomainPropertyApplier.ClearDomainBasedOn((object)v); } catch { }
                try { v.DomainBasedOn = null; } catch { }
                try { v.DomainKey = null; } catch { }
                try { DomainPropertyApplier.ClearAttributeBasedOn((object)v); } catch { }
                v.Type = global::Artech.Genexus.Common.eDBType.GX_SDT;
                v.SetPropertyValue("DataType", sdtObj.Key);
                string typeText = string.IsNullOrWhiteSpace(typeName) ? sdtObj.Name : typeName.Trim();
                try { v.SetPropertyValue("DataTypeString", typeText); } catch (Exception ex) { Logger.Warn("DataTypeString set failed: " + ex.Message); }
                try { v.SetPropertyValue("ATTCUSTOMTYPE", inst); }
                catch (Exception ex)
                {
                    failure = "SetPropertyValue ATTCUSTOMTYPE failed: " + ex.Message;
                    Logger.Error("[BindVariableToSdt] " + failure);
                    return false;
                }
            }
            catch (Exception ex)
            {
                failure = ex.InnerException?.Message ?? ex.Message;
                Logger.Warn("[BindVariableToSdt] " + failure);
                return false;
            }
            return true;
        }

        // Bind a variable to a Domain using both SDK references.
        //
        // DomainBasedOn is the IDE-facing relationship, while DomainKey is the stable
        // entity identity required by some GX18 builds. The generic data-type provider
        // returns a GX_DOM_REF AttCustomType whose parser/display form is dom:<name>;
        // persisting that token in ATTCUSTOMTYPE produces a non-importable XPZ.
        public static bool BindVariableToDomain(global::Artech.Genexus.Common.Variable v,
            global::Artech.Genexus.Common.Objects.Domain domain, out string failure)
        {
            failure = null;
            if (v == null || domain == null)
            {
                failure = "Variable or Domain is null.";
                return false;
            }

            try
            {
                // A variable retyped onto a Domain must not keep a stale
                // AttributeBasedOn: the two bindings would conflict and the
                // persisted picture/semantics become build-dependent. Fresh
                // variables are unaffected (nothing to clear). Mirrors the
                // Domain-clearing inside BindVariableToAttribute.
                try { DomainPropertyApplier.ClearAttributeBasedOn((object)v); } catch { }
                v.DomainBasedOn = domain;
                v.DomainKey = domain.Key;
                string customTypeToken = null;
                try { customTypeToken = v.GetPropertyValue("ATTCUSTOMTYPE")?.ToString(); } catch { }

                if (!IsNativeDomainReference(domain.Key, v.DomainKey, customTypeToken, out failure))
                {
                    try { v.DomainBasedOn = null; } catch { }
                    try { v.DomainKey = null; } catch { }
                    return false;
                }

                Logger.Info($"[BindVariableToDomain] Bound {v.Name} -> {domain.QualifiedName} by EntityKey {domain.Key}");
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.InnerException?.Message ?? ex.Message;
                Logger.Warn("[BindVariableToDomain] " + failure);
                try { v.DomainBasedOn = null; } catch { }
                try { v.DomainKey = null; } catch { }
                return false;
            }
        }

        // Bind a variable to an Attribute, preserving VarBasedOn/DataTypeString
        // ("Attribute:<name>") and the attribute's picture/semantics. A copy of
        // Type/Length alone flattens to a primitive (issue #281: 9999999999 ->
        // ZZZZZZZZZ9). Mirrors BindVariableToDomain but for AttributeBasedOn.
        public static bool BindVariableToAttribute(global::Artech.Genexus.Common.Variable v,
            global::Artech.Genexus.Common.Objects.Attribute attribute, out string failure)
        {
            failure = null;
            if (v == null || attribute == null)
            {
                failure = "Variable or Attribute is null.";
                return false;
            }

            try
            {
                try { DomainPropertyApplier.ClearDomainBasedOn((object)v); } catch { }
                try { v.DomainBasedOn = null; } catch { }
                try { v.DomainKey = null; } catch { }
                try
                {
                    v.Type = attribute.Type;
                    v.Length = attribute.Length;
                    v.Decimals = attribute.Decimals;
                    v.Signed = attribute.Signed;
                }
                catch (Exception ex)
                {
                    failure = "Could not copy attribute shape: " + (ex.InnerException?.Message ?? ex.Message);
                    return false;
                }
                if (!DomainPropertyApplier.ApplyAttributeBasedOn((object)v, (object)attribute))
                {
                    failure = "The SDK did not accept AttributeBasedOn='" + attribute.Name + "'.";
                    return false;
                }
                try { v.SetPropertyValue("DataTypeString", "Attribute:" + attribute.Name); } catch { }
                Logger.Info($"[BindVariableToAttribute] Bound {v.Name} -> Attribute:{attribute.Name}");
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.InnerException?.Message ?? ex.Message;
                Logger.Warn("[BindVariableToAttribute] " + failure);
                try { DomainPropertyApplier.ClearAttributeBasedOn((object)v); } catch { }
                return false;
            }
        }

        // Pure name+key seam for SDT / Business Component post-save checks.
        // The persisted reference must point at the requested object GUID with the
        // requested kind ("SDT" or "BusinessComponent"); anything else — a dropped
        // binding flattened to a primitive, or a retargeted object — fails closed.
        internal static bool IsNativeObjectBindingParts(
            Guid expectedGuid, string expectedKind,
            Guid? actualGuid, string actualKind,
            out string failure)
        {
            if (!actualGuid.HasValue || actualGuid.Value != expectedGuid)
            {
                failure = "The persisted variable references object '"
                    + (actualGuid?.ToString("D") ?? "")
                    + "', not the requested '" + expectedGuid.ToString("D") + "'.";
                return false;
            }
            if (!string.Equals(actualKind ?? string.Empty, expectedKind ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                failure = "The persisted reference kind '" + (actualKind ?? "")
                    + "' does not match requested '" + (expectedKind ?? "") + "'.";
                return false;
            }
            failure = null;
            return true;
        }

        // Pure helper: split an "Attribute:<name>" (or bare attribute via explicit
        // basedOnAttribute) request into the attribute name. Returns false when the
        // input is not an attribute reference so callers fall through to Domain/SDT.
        public static bool TryParseAttributeReference(string input, out string attributeName)
        {
            attributeName = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string trimmed = input.Trim().TrimStart('&');
            const string prefix = "Attribute:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string name = trimmed.Substring(prefix.Length).Trim();
                if (string.IsNullOrEmpty(name)) return false;
                attributeName = name;
                return true;
            }
            return false;
        }

        // Domain resolution is intentionally separate from ResolveTypeObject and from the generic
        // DataTypeProvider. Domain.ResolveName applies GeneXus module visibility rules and accepts
        // qualified names; model.Objects.GetByName alone can miss a Domain in a named Module.
        public static global::Artech.Genexus.Common.Objects.Domain ResolveDomain(
            KBModel model, string domainName, global::Artech.Architecture.Common.Objects.Module contextModule = null)
        {
            if (model == null || string.IsNullOrWhiteSpace(domainName)) return null;
            string name = domainName.Trim();
            try
            {
                var fromModule = contextModule ?? model.RootModule;
                var resolved = global::Artech.Genexus.Common.Objects.Domain.ResolveName(fromModule, name);
                if (resolved != null) return resolved;
            }
            catch { /* fall through to the root/object lookup for SDK-version resilience */ }

            if (contextModule != model.RootModule)
            {
                try
                {
                    var resolved = global::Artech.Genexus.Common.Objects.Domain.ResolveName(model.RootModule, name);
                    if (resolved != null) return resolved;
                }
                catch { }
            }

            try { return ResolveTypeObject(model, name) as global::Artech.Genexus.Common.Objects.Domain; }
            catch { return null; }
        }

        // Pure comparison seam used by regression tests and by the post-save verifier.
        // A matching key is authoritative; a friendly dom:/domain: token in the raw
        // custom type is always rejected even if the formatted Variables view looks OK.
        internal static bool IsNativeDomainReference(
            global::Artech.Udm.Framework.EntityKey expectedKey,
            global::Artech.Udm.Framework.EntityKey actualKey,
            string customTypeToken,
            out string failure)
        {
            return IsNativeDomainReferenceParts(
                expectedKey?.Type, expectedKey?.Id,
                actualKey?.Type, actualKey?.Id,
                customTypeToken, out failure);
        }

        internal static bool IsNativeDomainReferenceParts(
            Guid? expectedType, int? expectedId,
            Guid? actualType, int? actualId,
            string customTypeToken,
            out string failure)
        {
            if (!expectedType.HasValue || !expectedId.HasValue
                || !actualType.HasValue || !actualId.HasValue
                || expectedType.Value != actualType.Value || expectedId.Value != actualId.Value)
            {
                failure = "The persisted DomainKey does not match the requested Domain entity.";
                return false;
            }

            string token = (customTypeToken ?? string.Empty).Trim();
            if (token.StartsWith("dom:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
            {
                failure = "ATTCUSTOMTYPE contains a display-only Domain token ('" + token + "') instead of a native SDK reference.";
                return false;
            }

            failure = null;
            return true;
        }

        // Pure name+key seam for the light post-save check (VerifyPersistedVariable).
        // The name must match; when BOTH keys are available they must also match, so
        // a homonymous Domain from another Module cannot pass as the requested one.
        // A missing key on either side keeps the legacy name-only behavior instead of
        // failing closed on builds that do not populate DomainKey.
        internal static bool IsNativeDomainBindingParts(
            string expectedName,
            Guid? expectedType, int? expectedId,
            string actualName,
            Guid? actualType, int? actualId,
            out string failure)
        {
            if (!string.Equals(actualName ?? string.Empty, expectedName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                failure = "Persisted DomainBasedOn='" + (actualName ?? "") + "' does not match requested Domain '" + (expectedName ?? "") + "'.";
                return false;
            }
            if (expectedType.HasValue && expectedId.HasValue && actualType.HasValue && actualId.HasValue
                && (expectedType.Value != actualType.Value || expectedId.Value != actualId.Value))
            {
                failure = "Persisted DomainKey does not match the requested Domain entity (homonymous Domain in another Module?).";
                return false;
            }
            failure = null;
            return true;
        }

        // Built-in GeneXus "user-defined" effective types that live in eDBType.GX_USRDEFTYP,
        // keyed by their AttCustomType subtype id (category 255). Confirmed live for issue #33: a
        // WebSession variable persists as AttCustomType{ dataType=255, guid="31", description=
        // "WebSession" } (ToString "255:31"). The subtype id is a GeneXus built-in constant per GX
        // version; add other externally-backed types (e.g. HttpRequest) here as they're verified.
        private static readonly Dictionary<string, int> BuiltinUserDefinedTypes =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "WebSession", 31 },
            };

        public static bool IsBuiltinUserDefinedType(string typeName)
            => !string.IsNullOrWhiteSpace(typeName) && BuiltinUserDefinedTypes.ContainsKey(typeName.Trim());

        // Returns the canonical name (correct casing) when typeName is a known built-in
        // user-defined type, else null.
        public static string CanonicalUserDefinedTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            foreach (var key in BuiltinUserDefinedTypes.Keys)
                if (key.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase)) return key;
            return null;
        }

        // Atomically type a variable as a built-in user-defined type (e.g. WebSession) so that
        // &var.Get/.Set validate. Returns false when typeName isn't a known built-in.
        public static bool TryBindBuiltinUserDefinedType(global::Artech.Genexus.Common.Variable v, string typeName)
        {
            var canonical = CanonicalUserDefinedTypeName(typeName);
            if (canonical == null) return false;
            BindVariableToExternalObject(v, canonical, BuiltinUserDefinedTypes[canonical]);
            return true;
        }

        // issue #45 — bind a variable to ANY built-in GeneXus data type (HttpClient, HttpRequest,
        // HttpResponse, WebSession, Location, MailMessage, ExcelDocument, …) by resolving the name
        // through the SDK's own type registry, exactly as the IDE's variable "Type" picker does.
        // DataTypeProvider.GetTypeByName returns the AttCustomType the SDK wants persisted — carrying
        // the correct effective-type category (Variable.Type) and the runtime subtype guid — so we
        // don't hardcode a per-type id table (the WebSession=31 map only covered one of ~137 types).
        // Returns false when the name isn't a known GeneXus data type, so callers still surface
        // UnknownType. Setting DataTypeString mirrors the name for read-back round-tripping.
        public static bool TryBindGenexusDataType(global::Artech.Genexus.Common.Variable v, string typeName)
        {
            if (v == null || string.IsNullOrWhiteSpace(typeName)) return false;
            try
            {
                var model = v.Model;
                if (model == null) return false;
                var provider = Artech.Genexus.Common.Types.DataTypeProvider.GetProvider(model);
                if (provider == null) return false;
                var att = provider.GetTypeByName(typeName.Trim(), model);
                if (att == null) return false;
                // A Domain is not a generic custom data type for Variables. Persisting this
                // GX_DOM_REF AttCustomType writes the friendly dom:<name> token into
                // ATTCUSTOMTYPE, producing an XPZ the GeneXus importer cannot read. Callers must
                // resolve the Domain entity and assign Variable.DomainKey instead.
                if (att.DataType == (int)global::Artech.Genexus.Common.eDBType.GX_DOM_REF)
                {
                    Logger.Warn($"[TryBindGenexusDataType] Refusing display-only Domain token for '{typeName}'; bind by DomainKey instead.");
                    return false;
                }
                v.Type = (global::Artech.Genexus.Common.eDBType)att.DataType;
                try { v.SetPropertyValue("ATTCUSTOMTYPE", att); }
                catch (Exception ex) { Logger.Warn("[TryBindGenexusDataType] SetPropertyValue ATTCUSTOMTYPE failed: " + ex.Message); return false; }
                try { v.SetPropertyValue("DataTypeString", typeName.Trim()); } catch { /* read-back mirror; best-effort */ }
                Logger.Info($"[TryBindGenexusDataType] Bound {v.Name} -> {typeName} (category {att.DataType}, guid {att.Guid})");
                return true;
            }
            catch (Exception ex) { Logger.Warn("[TryBindGenexusDataType] " + ex.Message); return false; }
        }

        // Bind a variable to an SDT ITEM / level type such as "Messages.Message" — a single element
        // of a collection SDT — rather than the whole SDT. BindVariableToSdt references the SDT
        // object (category 254), which for a Collection SDT yields the COLLECTION, so both
        // "Messages" and "Messages.Message" collapsed to the same collection variable (the reported
        // "&Message insists on becoming Messages" bug). The dotted item form is exactly what the
        // IDE's variable "Type" field accepts, and DataTypeProvider.GetTypeByName resolves it to the
        // SDTItemTypeInfo's AttCustomType — a reference to the item level, distinct from the root.
        // Only attempts dotted names; returns false otherwise so the caller falls back to the
        // whole-SDT / Domain / BC path. DataTypeString is set to the dotted name so read-back
        // round-trips the item form instead of the bare SDT name.
        public static bool TryBindSdtItemType(global::Artech.Genexus.Common.Variable v, string typeName)
        {
            if (v == null || string.IsNullOrWhiteSpace(typeName) || typeName.IndexOf('.') <= 0) return false;
            bool ok = TryBindGenexusDataType(v, typeName);
            if (ok) Logger.Info($"[TryBindSdtItemType] Bound {v.Name} -> SDT item type {typeName}");
            return ok;
        }

        // GX_USRDEFTYP = user-defined effective type (AttCustomType category 255). The concrete
        // type (WebSession, ...) is identified by ATTCUSTOMTYPE where guid=<subtype id> and
        // description=<type name>; DataTypeString mirrors the name for read-back.
        public static void BindVariableToExternalObject(global::Artech.Genexus.Common.Variable v, string typeName, int subtype)
        {
            Logger.Info($"[BindVariableToExternalObject] Binding {v.Name} -> {typeName} (255:{subtype})");
            v.Type = global::Artech.Genexus.Common.eDBType.GX_USRDEFTYP;
            try { v.SetPropertyValue("DataTypeString", typeName); } catch (Exception ex) { Logger.Warn("[ExtObj] DataTypeString set failed: " + ex.Message); }
            var inst = BuildAttCustomType(v.GetType().Assembly, subtype.ToString(), 255, typeName);
            if (inst != null)
            {
                try { v.SetPropertyValue("ATTCUSTOMTYPE", inst); }
                catch (Exception ex) { Logger.Error("[ExtObj] SetPropertyValue ATTCUSTOMTYPE failed: " + ex.Message); }
            }
            else Logger.Error("[ExtObj] Could not construct AttCustomType for " + typeName);
        }

        // Type an SDT structure member as a reference to another SDT (item.Type=GX_SDT +
        // ATTCUSTOMTYPE carrying the SDT name / category 254). Mirrors BindVariableToSdt but on an
        // SDTItem reached via reflection (the parser holds it as a dynamic). Returns false on failure.
        public static bool BindSdtItemToSdt(object item, KBObject sdtObj)
        {
            if (item == null || sdtObj == null) return false;
            try
            {
                var itemType = item.GetType();
                var eDBTypeT = itemType.Assembly.GetType("Artech.Genexus.Common.eDBType");
                var setType = itemType.GetProperty("Type");
                if (eDBTypeT != null && setType != null && setType.CanWrite)
                    setType.SetValue(item, Enum.Parse(eDBTypeT, "GX_SDT"), null);

                var inst = BuildAttCustomType(itemType.Assembly, sdtObj.Name, 254, null);
                if (inst == null) { Logger.Error("[BindSdtItemToSdt] Could not construct AttCustomType for " + sdtObj.Name); return false; }

                var setProp = itemType.GetMethod("SetPropertyValue", new[] { typeof(string), typeof(object) });
                if (setProp == null) { Logger.Error("[BindSdtItemToSdt] SetPropertyValue(string,object) not found on " + itemType.FullName); return false; }
                setProp.Invoke(item, new object[] { "ATTCUSTOMTYPE", inst });
                Logger.Info($"[BindSdtItemToSdt] Bound structure member -> SDT {sdtObj.Name}");
                return true;
            }
            catch (Exception ex) { Logger.Error("[BindSdtItemToSdt] failed: " + (ex.InnerException?.Message ?? ex.Message)); return false; }
        }

        // Construct an Artech AttCustomType via reflection (the class isn't statically referenced).
        // Ctors seen on GX18: (), (string guid, int dataType), (string,int,string), (string,int,string,string).
        // dataType is the type category (254=SDT, 255=user-defined). Returns null on failure.
        public static object BuildAttCustomType(System.Reflection.Assembly probeAsm, string guid, int dataType, string description)
        {
            try
            {
                Type customTypeT = null;
                foreach (var loadedAsm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in loadedAsm.GetTypes())
                            if (t.Name.Equals("AttCustomType", StringComparison.Ordinal)) { customTypeT = t; break; }
                    }
                    catch { }
                    if (customTypeT != null) break;
                }
                if (customTypeT == null) { Logger.Warn("[BuildAttCustomType] AttCustomType type not found"); return null; }

                object inst = null;
                if (!string.IsNullOrEmpty(description))
                {
                    var ctor3 = customTypeT.GetConstructor(new[] { typeof(string), typeof(int), typeof(string) });
                    if (ctor3 != null) { try { inst = ctor3.Invoke(new object[] { guid, dataType, description }); } catch (Exception ex) { Logger.Warn("[BuildAttCustomType] ctor(str,int,str) failed: " + ex.Message); } }
                }
                if (inst == null)
                {
                    var ctor2 = customTypeT.GetConstructor(new[] { typeof(string), typeof(int) });
                    if (ctor2 != null) { try { inst = ctor2.Invoke(new object[] { guid, dataType }); } catch (Exception ex) { Logger.Warn("[BuildAttCustomType] ctor(str,int) failed: " + ex.Message); } }
                }
                if (inst == null)
                {
                    var ctor0 = customTypeT.GetConstructor(Type.EmptyTypes);
                    if (ctor0 != null)
                    {
                        try
                        {
                            inst = ctor0.Invoke(null);
                            customTypeT.GetProperty("Guid")?.SetValue(inst, guid);
                            customTypeT.GetProperty("DataType")?.SetValue(inst, dataType);
                        }
                        catch (Exception ex) { Logger.Warn("[BuildAttCustomType] ctor() failed: " + ex.Message); }
                    }
                }
                // Ensure the description lands even when only a shorter ctor was available.
                if (inst != null && !string.IsNullOrEmpty(description))
                {
                    try
                    {
                        var descField = customTypeT.GetField("m_description", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (descField != null && string.IsNullOrEmpty(descField.GetValue(inst) as string))
                            descField.SetValue(inst, description);
                    }
                    catch { }
                }
                if (inst == null) Logger.Error("[BuildAttCustomType] Could not construct AttCustomType (guid=" + guid + ", dataType=" + dataType + ")");
                return inst;
            }
            catch (Exception ex) { Logger.Warn("[BuildAttCustomType] failed: " + ex.Message); return null; }
        }

        public static void BindVariableToBC(global::Artech.Genexus.Common.Variable v, KBObject bcObj)
        {
            string module = null;
            try { module = bcObj.Module?.Name; } catch { }
            bool root = string.IsNullOrWhiteSpace(module)
                || string.Equals(module, "Root Module", StringComparison.OrdinalIgnoreCase);
            string displayName = root ? bcObj.Name : bcObj.Name + ", " + module;
            string qualifiedName = root ? bcObj.Name : module + "." + bcObj.Name;

            var provider = Artech.Genexus.Common.Types.DataTypeProvider.GetProvider(v.Model)
                ?? throw new InvalidOperationException("The GeneXus data type provider is unavailable.");
            global::Artech.Genexus.Common.CustomTypes.AttCustomType nativeType = null;
            foreach (string candidate in new[] { displayName, qualifiedName, bcObj.Name }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var resolved = provider.GetTypeByName(candidate, v.Model);
                    if (resolved != null
                        && resolved.DataType == (int)global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
                    {
                        nativeType = resolved;
                        displayName = candidate;
                        break;
                    }
                }
                catch { }
            }
            if (nativeType == null)
                throw new InvalidOperationException("The GeneXus type provider did not expose the requested Business Component.");

            // Clear stale bindings only AFTER the native type resolved: the throws
            // above must leave an existing variable untouched. A variable retyped
            // onto a BC must not keep stale Domain or Attribute bindings.
            try { DomainPropertyApplier.ClearDomainBasedOn((object)v); } catch { }
            try { v.DomainBasedOn = null; } catch { }
            try { v.DomainKey = null; } catch { }
            try { DomainPropertyApplier.ClearAttributeBasedOn((object)v); } catch { }
            v.Type = global::Artech.Genexus.Common.eDBType.GX_BUSCOMP;
            v.SetPropertyValue("DataType", bcObj.Key);
            v.SetPropertyValue("ATTCUSTOMTYPE", nativeType);
            try { v.SetPropertyValue("DataTypeString", displayName); } catch { }
        }

        /// <summary>
        /// Resolves a Business Component by native Transaction identity.  The module is
        /// matched separately instead of being folded into a display type string, because
        /// <c>GetByName</c> does not resolve <c>Module.Object</c> as an object name.
        /// </summary>
        public static Transaction ResolveBusinessComponent(KBModel model, string objectName,
            string moduleName, out string error)
        {
            error = null;
            if (model == null || string.IsNullOrWhiteSpace(objectName))
            {
                error = "objectName is required.";
                return null;
            }

            if (!TryNormalizeBusinessComponentReference(objectName, moduleName,
                out string simpleName, out string requestedModule, out error)) return null;

            var matches = new List<Transaction>();
            try
            {
                foreach (var candidate in model.Objects.GetByName(null, null, simpleName))
                {
                    var trn = candidate as Transaction;
                    if (trn == null || !trn.IsBusinessComponent) continue;
                    string actualModule = null;
                    try { actualModule = trn.Module?.Name; } catch { }
                    if (requestedModule == null
                        || string.Equals(actualModule, requestedModule, StringComparison.OrdinalIgnoreCase))
                        matches.Add(trn);
                }
            }
            catch (Exception ex)
            {
                error = "Business Component lookup failed: " + ex.Message;
                return null;
            }

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0)
            {
                error = requestedModule == null
                    ? "Business Component '" + simpleName + "' was not found."
                    : "Business Component '" + requestedModule + "." + simpleName + "' was not found.";
                return null;
            }

            error = "Business Component name is ambiguous; pass module explicitly.";
            return null;
        }

        internal static bool TryNormalizeBusinessComponentReference(string objectName, string moduleName,
            out string simpleName, out string requestedModule, out string error)
        {
            simpleName = objectName?.Trim();
            requestedModule = string.IsNullOrWhiteSpace(moduleName) ? null : moduleName.Trim();
            error = null;
            if (string.IsNullOrWhiteSpace(simpleName))
            {
                error = "objectName is required.";
                return false;
            }
            int separator = simpleName.LastIndexOf('.');
            if (separator <= 0) return true;

            string qualifiedModule = simpleName.Substring(0, separator);
            string qualifiedName = simpleName.Substring(separator + 1);
            if (requestedModule != null
                && !string.Equals(requestedModule, qualifiedModule, StringComparison.OrdinalIgnoreCase))
            {
                error = "module conflicts with the module-qualified objectName.";
                return false;
            }
            requestedModule = qualifiedModule;
            simpleName = qualifiedName;
            return true;
        }

        /// <summary>Returns the KB object referenced by a persisted structural variable.</summary>
        public static KBObject ResolveBoundTypeObject(global::Artech.Genexus.Common.Variable variable, KBModel model)
        {
            if (variable == null || model == null) return null;
            string[] keyProperties = { "DataType", "DataTypeKey", "BasedOnKey", "TypeKey", "ObjectKey" };
            foreach (string property in keyProperties)
            {
                try
                {
                    var resolved = TryGetObjectFromKey(model, variable.GetPropertyValue(property));
                    if (resolved != null) return resolved;
                }
                catch { }
            }
            try
            {
                object custom = variable.GetPropertyValue("ATTCUSTOMTYPE");
                if (custom != null)
                {
                    string token = custom.GetType().GetProperty("Guid")?.GetValue(custom) as string
                        ?? custom.GetType().GetField("m_guid", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(custom) as string;
                    var resolved = TryGetObjectFromKey(model, token);
                    if (resolved != null) return resolved;
                }
            }
            catch { }

            // GX18 U16 persists modular BC variables as GX_BUSCOMP plus DataTypeString;
            // ATTCUSTOMTYPE intentionally has no object key. Resolve the name in the
            // variable owner's module, which is the same native scope used by GeneXus.
            try
            {
                string typeName = variable.GetPropertyValue("DataTypeString") as string;
                string ownerModule = variable.KBObject?.Module?.Name;
                int comma = typeName?.LastIndexOf(',') ?? -1;
                if (comma > 0)
                {
                    ownerModule = typeName.Substring(comma + 1).Trim();
                    typeName = typeName.Substring(0, comma).Trim();
                }
                else if (typeName?.IndexOf('.') >= 0) ownerModule = null;
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    var resolved = ResolveBusinessComponent(model, typeName, ownerModule, out _);
                    if (resolved != null) return resolved;
                }
            }
            catch { }
            return null;
        }

        public static bool TryParseDbType(string typeStr, out global::Artech.Genexus.Common.eDBType type)
        {
            // Type Aliases mapping. NOTE: 'Character' is NOT mapped to VARCHAR — it must
            // round-trip to eDBType.CHARACTER via the case-insensitive Enum.TryParse below.
            // Aliasing CHARACTER → VARCHAR makes Variables-DSL patch verification fail because
            // GetVariablesAsText emits the original eDBType.CHARACTER string while
            // SetVariablesFromText would persist VARCHAR (friction-report #5 write side).
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "VarChar", "VARCHAR" },
                { "Numeric", "NUMERIC" },
                { "Boolean", "Boolean" },
                { "Date", "DATE" },
                { "DateTime", "DATETIME" },
                // issue #34: eDBType has no BLOB/IMAGE member — a blob is BINARY, an image
                // is BITMAP. The old "BLOB"/"IMAGE" aliases failed Enum.TryParse and the
                // variable silently kept its default NUMERIC(4) type.
                { "Blob", "BINARY" },
                { "Binary", "BINARY" },
                { "Image", "BITMAP" },
                { "Bitmap", "BITMAP" },
                { "Audio", "AUDIO" },
                { "Video", "VIDEO" },
                { "GUID", "GUID" },
                { "Geography", "GEOGRAPHY" }
            };

            if (aliases.TryGetValue(typeStr, out var mappedType))
            {
                typeStr = mappedType;
            }

            return Enum.TryParse<global::Artech.Genexus.Common.eDBType>(typeStr, true, out type);
        }

        public static KBObject ResolveTypeObject(KBModel model, string typeName)
        {
            try
            {
                bool hasModuleQualifier = VariableDeclarationParser.TrySplitModuleQualifiedTypeName(typeName, out string qualifiedObjectName, out string moduleName);
                string objectName = hasModuleQualifier ? qualifiedObjectName : typeName;
                foreach (var obj in model.Objects.GetByName(null, null, objectName))
                {
                    if (hasModuleQualifier && !string.Equals(obj.Module?.Name, moduleName, StringComparison.OrdinalIgnoreCase)) continue;

                    // Check for Domain
                    if (obj is global::Artech.Genexus.Common.Objects.Domain) return obj;

                    // Check for SDT
                    if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return obj;

                    // Check for Transaction (as BC)
                    if (obj is Transaction trn && trn.IsBusinessComponent) return obj;
                }

                // issue #43 #7 — nested SDT member/item type, e.g. "SdtCandUNIEDU.SdtCandUNIEDUItem".
                // This dotted form is exactly what the SDK emits on READ for a variable bound to a
                // Collection SDT as a single item (VariableTypeResolver already flags "SdtFoo.Item" as
                // expected here). The whole dotted string is not a KB object name — only the parent SDT
                // is — so split off the parent and resolve it. BindVariableToSdt(newVar, parentSdt) then
                // produces the "<Sdt>.<Sdt>Item" item type, since the SDK derives the ".Item" suffix
                // from the SDT's own collection-ness (IsCollection left false), matching the read form.
                if (objectName != null && objectName.IndexOf('.') > 0)
                {
                    string parentName = objectName.Substring(0, objectName.IndexOf('.'));
                    foreach (var obj in model.Objects.GetByName(null, null, parentName))
                    {
                        if (hasModuleQualifier && !string.Equals(obj.Module?.Name, moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return obj;
                    }
                }
            }
            catch { /* Ignore model errors */ }
            return null;
        }

        public static global::Artech.Genexus.Common.Objects.Attribute FindAttribute(global::Artech.Architecture.Common.Objects.KBModel model, string name)
        {
            if (model == null || string.IsNullOrWhiteSpace(name)) return null;
            string clean = name.Trim();
            if (clean.StartsWith("Attribute:", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring("Attribute:".Length).Trim();
            if (clean.StartsWith("&"))
                clean = clean.TrimStart('&');
            try
            {
                foreach (var result in model.Objects.GetByName(null, null, clean))
                {
                    if (result is global::Artech.Genexus.Common.Objects.Attribute attr) return attr;
                }
            }
            catch { /* Object not found or model access error */ }
            return null;
        }
    }
}
