using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;
using GxAttribute = Artech.Genexus.Common.Objects.Attribute;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Read-only projection of the public GeneXus SDK Data Selector model.
    /// This service never saves, specifies, generates, builds, or mutates a KB object.
    /// </summary>
    public static class DataSelectorReadService
    {
        private static readonly string[] DefaultParts =
        {
            "parameters", "conditions", "orders", "projection", "definedBy",
            "baseTransaction", "baseTable", "joins", "structure"
        };

        private static readonly HashSet<string> KnownParts =
            new HashSet<string>(DefaultParts, StringComparer.OrdinalIgnoreCase);

        public static bool IsDataSelector(KBObject obj)
        {
            return obj is DataSelector;
        }

        public static bool IsVirtualPart(string partName)
        {
            return !string.IsNullOrWhiteSpace(partName) && KnownParts.Contains(partName.Trim());
        }

        public static string Read(DataSelector selector, IEnumerable<string> requestedParts)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));

            string[] parts = NormalizeParts(requestedParts);
            Snapshot snapshot = Capture(selector);
            return BuildResponse(snapshot, parts).ToString();
        }

        public static JObject BuildResponse(Snapshot snapshot, IEnumerable<string> requestedParts)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            string[] parts = NormalizeParts(requestedParts);
            var response = new JObject
            {
                ["name"] = snapshot.Name,
                ["type"] = "DataSelector",
                ["persisted"] = true,
                ["readOnly"] = true,
                ["versionToken"] = snapshot.VersionToken,
                ["requestedParts"] = new JArray(parts),
                ["availableParts"] = new JArray(DefaultParts),
                ["implicitOperations"] = new JArray()
            };
            var unsupported = new JArray();

            foreach (string part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "parameters":
                        response["parameters"] = new JArray(snapshot.Parameters.Select(ToJson));
                        break;
                    case "conditions":
                        response["conditions"] = new JArray(snapshot.Conditions.Select(ToJson));
                        // Editable text form for genexus_edit part=Conditions (one condition per line).
                        if (parts.Length == 1)
                            response["source"] = DataSelectorConditionsEditor.Join(snapshot.Conditions.Select(c => c.Expression));
                        break;
                    case "orders":
                        response["orders"] = new JArray(snapshot.Orders.Select(ToJson));
                        break;
                    case "definedby":
                        response["definedBy"] = new JArray(snapshot.DefinedBy);
                        break;
                    case "basetable":
                        response["baseTable"] = ToJson(snapshot.BaseTable, snapshot.BaseResolution);
                        break;
                    case "basetransaction":
                        response["baseTransaction"] = ToJson(snapshot.BaseTransaction, snapshot.BaseTable, snapshot.BaseResolution);
                        break;
                    case "structure":
                        response["structure"] = new JObject
                        {
                            ["expressionKind"] = "semanticProjection",
                            ["expression"] = snapshot.StructureExpression,
                            ["parameters"] = new JArray(snapshot.Parameters.Select(ToJson)),
                            ["conditions"] = new JArray(snapshot.Conditions.Select(ToJson)),
                            ["orders"] = new JArray(snapshot.Orders.Select(ToJson)),
                            ["definedBy"] = new JArray(snapshot.DefinedBy),
                            ["attributes"] = new JArray(snapshot.ReferencedAttributes)
                        };
                        break;
                    case "projection":
                        unsupported.Add(Unsupported(
                            "projection",
                            "GeneXus 18 U16 DataSelectorStructurePart exposes referenced attributes, but no projected-attribute collection. Data Selectors constrain an extended table and do not define a projection through this SDK API."));
                        break;
                    case "joins":
                        unsupported.Add(Unsupported(
                            "joins",
                            "The GeneXus 18 U16 public SDK does not expose resolved joins for a Data Selector. Resolving navigation would require Specify, which this read never runs."));
                        break;
                    default:
                        unsupported.Add(Unsupported(
                            part,
                            "Unknown Data Selector inspection part. Use one of the names in availableParts."));
                        break;
                }
            }

            response["unsupportedParts"] = unsupported;
            return response;
        }

        private static Snapshot Capture(DataSelector selector)
        {
            DataSelectorStructurePart structure = selector.DataSelectorStructure;
            var snapshot = new Snapshot
            {
                Name = selector.Name,
                VersionToken = SafeVersionToken(selector)
            };

            if (structure == null)
            {
                snapshot.BaseResolution = "The persisted Data Selector structure is unavailable through the SDK.";
                return snapshot;
            }

            int ordinal = 0;
            foreach (DataSelectorParameter parameter in structure.Parameters)
            {
                object content = parameter.Content;
                snapshot.Parameters.Add(new ParameterSnapshot
                {
                    Name = parameter.Name ?? string.Empty,
                    Type = ResolveParameterType(content),
                    ContentKind = ResolveContentKind(content),
                    Direction = "in",
                    Ordinal = ++ordinal,
                    Description = parameter.Description ?? string.Empty
                });
            }

            ordinal = 0;
            foreach (DataSelectorCondition condition in structure.GetConditions() ?? Enumerable.Empty<DataSelectorCondition>())
            {
                snapshot.Conditions.Add(new ExpressionSnapshot
                {
                    Expression = condition.Source?.Source ?? condition.ToString() ?? string.Empty,
                    Ordinal = ++ordinal
                });
            }

            ordinal = 0;
            foreach (DataSelectorOrderItem orderItem in structure.GetOrders() ?? Enumerable.Empty<DataSelectorOrderItem>())
            {
                var order = new OrderSnapshot
                {
                    Expression = orderItem.ToString() ?? string.Empty,
                    Ordinal = ++ordinal,
                    Condition = orderItem.Order?.ConditionSource ?? string.Empty
                };
                if (orderItem.Order?.OrderedItems != null)
                {
                    foreach (DataSelectorOrderedItem item in orderItem.Order.OrderedItems)
                    {
                        order.Items.Add(new OrderMemberSnapshot
                        {
                            Name = item.Name ?? item.OrderedItem?.ToString() ?? string.Empty,
                            Direction = item.Type.ToString()
                        });
                    }
                }
                order.Direction = order.Items.Count == 1 ? order.Items[0].Direction :
                    order.Items.Count == 0 ? string.Empty : "Mixed";
                snapshot.Orders.Add(order);
            }

            var referenced = new List<GxAttribute>();
            var definedByAttributes = new List<GxAttribute>();
            foreach (DataSelectorAttribute item in structure.GetAttributes() ?? Enumerable.Empty<DataSelectorAttribute>())
            {
                AddAttribute(referenced, item.Attribute);
            }

            if (structure.Root?.DefinedByAttributes != null)
            {
                foreach (DataSelectorAttribute item in structure.Root.DefinedByAttributes)
                {
                    if (item.Attribute != null)
                    {
                        snapshot.DefinedBy.Add(item.Attribute.Name ?? item.Name ?? string.Empty);
                        AddAttribute(referenced, item.Attribute);
                        AddAttribute(definedByAttributes, item.Attribute);
                    }
                    else
                    {
                        snapshot.DefinedBy.Add(item.Name ?? item.ToString() ?? string.Empty);
                    }
                }
            }

            snapshot.ReferencedAttributes.AddRange(referenced
                .Select(a => a.Name ?? string.Empty)
                .Where(n => n.Length > 0));
            snapshot.StructureExpression = BuildSemanticExpression(snapshot);
            ResolveBase(selector, definedByAttributes, referenced, snapshot);
            return snapshot;
        }

        private static string BuildSemanticExpression(Snapshot snapshot)
        {
            var text = new StringBuilder();
            AppendSection(text, "Parameters", snapshot.Parameters.Select(p =>
                string.Format("{0}. {1} : {2}", p.Ordinal, p.Name, p.Type)));
            AppendSection(text, "Conditions", snapshot.Conditions.Select(c =>
                string.Format("{0}. {1}", c.Ordinal, c.Expression)));
            AppendSection(text, "Orders", snapshot.Orders.Select(o =>
                string.Format("{0}. {1}", o.Ordinal, o.Expression)));
            AppendSection(text, "Defined By", snapshot.DefinedBy.Select((name, index) =>
                string.Format("{0}. {1}", index + 1, name)));
            return text.ToString();
        }

        private static void AppendSection(StringBuilder text, string title, IEnumerable<string> items)
        {
            string[] values = items.ToArray();
            if (values.Length == 0) return;
            text.Append(title).Append(":\n");
            foreach (string value in values)
            {
                text.Append(value ?? string.Empty);
                if (string.IsNullOrEmpty(value) || !value.EndsWith("\n", StringComparison.Ordinal))
                    text.Append('\n');
            }
        }

        private static void ResolveBase(
            DataSelector selector,
            IList<GxAttribute> definedBy,
            IList<GxAttribute> referenced,
            Snapshot snapshot)
        {
            if (referenced.Count == 0)
            {
                snapshot.BaseResolution = "No referenced attribute was exposed by the SDK, so the base table cannot be resolved safely.";
                return;
            }

            IList<GxAttribute> anchors = definedBy.Count > 0 ? definedBy : referenced;
            List<Table> candidates;
            try
            {
                candidates = Table.GetAll(selector.Model)
                    .Where(t => t?.TableStructure != null && anchors.All(a => t.TableStructure.GetAttribute(a) != null))
                    .OrderBy(t => Compatibility.SdkCompat.TableQualifiedName(t) ?? t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                snapshot.BaseResolution = "Base-table lookup failed through the SDK: " + ex.Message;
                return;
            }

            if (candidates.Count != 1)
            {
                snapshot.BaseResolution = candidates.Count == 0
                    ? "No table contains every base-resolution attribute exposed by the Data Selector. The base cannot be inferred without navigation analysis."
                    : "More than one table contains every base-resolution attribute; the SDK model is ambiguous without navigation analysis: "
                        + string.Join(", ", candidates.Select(t => t.Name)) + ".";
                return;
            }

            Table table = candidates[0];
            snapshot.BaseResolution = definedBy.Count > 0
                ? "Resolved uniquely from complete Defined By attribute coverage."
                : "Resolved uniquely from complete referenced-attribute coverage.";
            snapshot.BaseTable = CaptureTable(table);
            Transaction transaction = null;
            try { transaction = Compatibility.SdkCompat.BestAssociatedTransaction(table); } catch { }
            if (transaction != null)
            {
                snapshot.BaseTransaction = transaction.Name;
            }
        }

        private static TableSnapshot CaptureTable(Table table)
        {
            var result = new TableSnapshot
            {
                Name = table.Name ?? string.Empty,
                OriginalName = Compatibility.SdkCompat.TableOriginalName(table) ?? string.Empty
            };
            try
            {
                dynamic indexesPart = table.TableIndexes;
                if (indexesPart?.Indexes != null)
                {
                    foreach (dynamic tableIndex in indexesPart.Indexes)
                    {
                        dynamic index = tableIndex.Index;
                        if (index == null) continue;
                        var item = new IndexSnapshot
                        {
                            Name = Convert.ToString(index.Name) ?? string.Empty,
                            Type = index.IndexType != null ? Convert.ToString(index.IndexType) : string.Empty
                        };
                        if (index.IndexStructure?.Members != null)
                        {
                            foreach (dynamic member in index.IndexStructure.Members)
                            {
                                item.Attributes.Add(new IndexAttributeSnapshot
                                {
                                    Name = member.Attribute != null ? Convert.ToString(member.Attribute.Name) : Convert.ToString(member.Name),
                                    Direction = member.Order != null ? Convert.ToString(member.Order) : string.Empty
                                });
                            }
                        }
                        result.Indexes.Add(item);
                    }
                }
            }
            catch { }
            return result;
        }

        private static string[] NormalizeParts(IEnumerable<string> requestedParts)
        {
            string[] parts = requestedParts?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(CanonicalPart)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return parts == null || parts.Length == 0 ? DefaultParts.ToArray() : parts;
        }

        private static string CanonicalPart(string part)
        {
            string value = part.Trim();
            if (value.Equals("definedby", StringComparison.OrdinalIgnoreCase)) return "definedBy";
            if (value.Equals("basetransaction", StringComparison.OrdinalIgnoreCase)) return "baseTransaction";
            if (value.Equals("basetable", StringComparison.OrdinalIgnoreCase)) return "baseTable";
            return value.ToLowerInvariant();
        }

        private static string SafeVersionToken(DataSelector selector)
        {
            try { return WriteService.ComputeVersionToken(selector) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static void AddAttribute(ICollection<GxAttribute> attributes, GxAttribute candidate)
        {
            if (candidate == null || attributes.Any(a => a.Guid == candidate.Guid)) return;
            attributes.Add(candidate);
        }

        private static string ResolveContentKind(object content)
        {
            if (content is GxAttribute) return "attribute";
            if (content is Variable) return "variable";
            return content?.GetType().Name ?? string.Empty;
        }

        private static string ResolveParameterType(object content)
        {
            if (content is GxAttribute attribute)
            {
                return ResolveType(attribute.DomainBasedOn?.Name, attribute.Type.ToString(), attribute.Length, attribute.Decimals);
            }
            if (content is Variable variable)
            {
                string basedOn = variable.DomainBasedOn?.Name ?? variable.AttributeBasedOn?.DomainBasedOn?.Name;
                return ResolveType(basedOn, variable.Type.ToString(), variable.Length, variable.Decimals);
            }
            return content?.GetType().Name ?? string.Empty;
        }

        private static string ResolveType(string basedOn, string primitive, int length, int decimals)
        {
            if (!string.IsNullOrWhiteSpace(basedOn)) return basedOn;
            if (length <= 0) return primitive ?? string.Empty;
            return decimals > 0
                ? string.Format("{0}({1},{2})", primitive, length, decimals)
                : string.Format("{0}({1})", primitive, length);
        }

        private static JObject ToJson(ParameterSnapshot item) => new JObject
        {
            ["name"] = item.Name,
            ["type"] = item.Type,
            ["direction"] = item.Direction,
            ["ordinal"] = item.Ordinal,
            ["contentKind"] = item.ContentKind,
            ["description"] = item.Description
        };

        private static JObject ToJson(ExpressionSnapshot item) => new JObject
        {
            ["expression"] = item.Expression,
            ["ordinal"] = item.Ordinal
        };

        private static JObject ToJson(OrderSnapshot item) => new JObject
        {
            ["expression"] = item.Expression,
            ["direction"] = item.Direction,
            ["ordinal"] = item.Ordinal,
            ["condition"] = item.Condition,
            ["items"] = new JArray(item.Items.Select(i => new JObject
            {
                ["name"] = i.Name,
                ["direction"] = i.Direction
            }))
        };

        private static JToken ToJson(TableSnapshot table, string resolution)
        {
            if (table == null)
            {
                return new JObject { ["resolved"] = false, ["reason"] = resolution ?? string.Empty };
            }
            return new JObject
            {
                ["resolved"] = true,
                ["name"] = table.Name,
                ["physicalName"] = table.Name,
                ["originalName"] = table.OriginalName,
                ["resolution"] = resolution,
                ["indexes"] = new JArray(table.Indexes.Select(i => new JObject
                {
                    ["name"] = i.Name,
                    ["type"] = i.Type,
                    ["attributes"] = new JArray(i.Attributes.Select(a => new JObject
                    {
                        ["name"] = a.Name,
                        ["direction"] = a.Direction
                    }))
                })),
                ["indexUsage"] = "Declared indexes only; the used index is not determined because this read does not run Specify."
            };
        }

        private static JToken ToJson(string transaction, TableSnapshot table, string resolution)
        {
            if (string.IsNullOrWhiteSpace(transaction))
            {
                return new JObject { ["resolved"] = false, ["reason"] = resolution ?? string.Empty };
            }
            return new JObject
            {
                ["resolved"] = true,
                ["name"] = transaction,
                ["table"] = table?.Name ?? string.Empty,
                ["resolution"] = resolution
            };
        }

        private static JObject Unsupported(string part, string reason) => new JObject
        {
            ["part"] = part,
            ["reason"] = reason
        };

        public sealed class Snapshot
        {
            public string Name { get; set; } = string.Empty;
            public string VersionToken { get; set; } = string.Empty;
            public string StructureExpression { get; set; } = string.Empty;
            public string BaseTransaction { get; set; } = string.Empty;
            public string BaseResolution { get; set; } = string.Empty;
            public TableSnapshot BaseTable { get; set; }
            public List<ParameterSnapshot> Parameters { get; } = new List<ParameterSnapshot>();
            public List<ExpressionSnapshot> Conditions { get; } = new List<ExpressionSnapshot>();
            public List<OrderSnapshot> Orders { get; } = new List<OrderSnapshot>();
            public List<string> DefinedBy { get; } = new List<string>();
            public List<string> ReferencedAttributes { get; } = new List<string>();
        }

        public sealed class ParameterSnapshot
        {
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string Direction { get; set; } = "in";
            public string ContentKind { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public int Ordinal { get; set; }
        }

        public class ExpressionSnapshot
        {
            public string Expression { get; set; } = string.Empty;
            public int Ordinal { get; set; }
        }

        public sealed class OrderSnapshot : ExpressionSnapshot
        {
            public string Direction { get; set; } = string.Empty;
            public string Condition { get; set; } = string.Empty;
            public List<OrderMemberSnapshot> Items { get; } = new List<OrderMemberSnapshot>();
        }

        public sealed class OrderMemberSnapshot
        {
            public string Name { get; set; } = string.Empty;
            public string Direction { get; set; } = string.Empty;
        }

        public sealed class TableSnapshot
        {
            public string Name { get; set; } = string.Empty;
            public string OriginalName { get; set; } = string.Empty;
            public List<IndexSnapshot> Indexes { get; } = new List<IndexSnapshot>();
        }

        public sealed class IndexSnapshot
        {
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public List<IndexAttributeSnapshot> Attributes { get; } = new List<IndexAttributeSnapshot>();
        }

        public sealed class IndexAttributeSnapshot
        {
            public string Name { get; set; } = string.Empty;
            public string Direction { get; set; } = string.Empty;
        }
    }
}
