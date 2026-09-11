using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Packages.Patterns.Objects;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal static class EventsSaveIsolation
    {
        // Only the inspected U16 implementation is eligible. Broker suppression
        // cannot disable virtual methods or native Entity.BeforeSave delegates.
        private static readonly Dictionary<string, string> AuditedAssemblies = new Dictionary<string, string>
        {
            ["Artech.Architecture.Common"] = "5de4b9fa160b2aca8fa08be8e070870c00286415d7ecdfad816aa5bdc406f283",
            ["Artech.Genexus.Common"] = "9e1faca2f240141794c6627f22e71e4d8acd8f1a8a9daaea3b2d4254515e95f0",
            ["Artech.Udm.Framework"] = "ba47b86818aff828a5172b44c0ea83353b4a565fec076105480a995535bc9251",
            ["Artech.Common.Properties"] = "615ca8e522142748feb4a467081efdcd57d962ad71382dc2d77e2639f91a990a",
            ["Artech.Packages.Patterns"] = "36f99d7d76c3c7b54dded1249d09a02234834c9d38483c68afc9d79bd8538ace",
            ["DVelop.Patterns.WorkWithPlus"] = "16e15bcb8eaef779abd5b8ef9d33c724f58406979496612a253473688b52fb02"
        };
        private static readonly HashSet<Type> Parts = new HashSet<Type>
        {
            typeof(EventsPart), typeof(WebFormPart), typeof(VariablesPart), typeof(RulesPart),
            typeof(ConditionsPart), typeof(HelpPart), typeof(DocumentationPart),
            typeof(PatternVirtualPart)
        };

        internal static JObject Preflight(KBObject obj)
        {
            CheckTarget(obj);
            var fresh = Fresh(obj.KB, obj.Guid);
            var report = CheckTarget(fresh);
            report["brokers"] = SdkEventSuppressionScope.VerifyBroker(fresh.KB);
            report["freshInstanceVerified"] = true;
            report["verified"] = true;
            return report;
        }

        internal static JObject CheckTarget(KBObject obj)
        {
            if (obj == null || obj.GetType() != typeof(WebPanel))
                throw new InvalidOperationException("Isolated Events save supports only the exact audited SDK WebPanel type.");
            if (!WriteDestinationGuard.IsConfigured)
                throw new InvalidOperationException("Isolated Events save requires both profile-owned KB and version pins.");
            var kb = obj.KB;
            var version = KBVersion.GetActive(kb);
            string destinationError = WriteDestinationGuard.Check(
                Environment.GetEnvironmentVariable(WriteDestinationGuard.PathVariable),
                Environment.GetEnvironmentVariable(WriteDestinationGuard.VersionVariable),
                kb.Location, version?.Name, version == null ? (bool?)null : version.IsFrozen, false);
            if (destinationError != null) throw new InvalidOperationException(destinationError);
            foreach (var expected in AuditedAssemblies)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == expected.Key);
                // WorkWithPlus is loaded only when the target has that package
                // installed/attached. If loaded, its exact U16 bytes are still
                // mandatory; an unknown loaded implementation is never trusted.
                if (assembly == null && expected.Key == "DVelop.Patterns.WorkWithPlus") continue;
                if (assembly == null || !string.Equals(Hash(File.ReadAllBytes(assembly.Location)), expected.Value, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unverified SDK implementation: " + expected.Key);
            }
            var partTypes = obj.Parts.Cast<KBObjectPart>().Select(p => p.GetType()).ToArray();
            if (!partTypes.Contains(typeof(EventsPart)) || partTypes.Any(p => !Parts.Contains(p)))
                throw new InvalidOperationException("The object contains an unverified SDK or extension part: "
                    + string.Join(", ", partTypes.Where(p => !Parts.Contains(p)).Select(p => p.FullName)));
            JObject report = new JObject();
            var callbacks = new JArray();
            InspectDirectCallbacks(obj, callbacks);
            foreach (KBObjectPart part in obj.Parts) InspectDirectCallbacks(part, callbacks);
            report["directCallbacks"] = callbacks;
            report["sdkFingerprintVerified"] = true;
            report["objectGuid"] = obj.Guid.ToString();
            report["parts"] = new JArray(partTypes.Select(p => p.FullName));
            report["patternPart"] = partTypes.Contains(typeof(PatternVirtualPart));
            report["skipApplyPatternSupported"] = true;
            report["wwpAssemblyLoaded"] = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "DVelop.Patterns.WorkWithPlus");
            report["scope"] = "audited-u16-webpanel-with-pattern-save-isolation";
            return report;
        }

        internal static bool IsAllowedPartType(Type type) => Parts.Contains(type);
        internal static bool IsAllowedPartTypeName(string fullName)
            => Parts.Any(type => type.FullName == fullName);

        internal static bool IsExpectedPatternProjection(ObjectMoveSnapshot.Comparison comparison)
        {
            if (comparison == null || comparison.Equal) return true;
            foreach (var changed in comparison.ChangedParts)
            {
                string name = changed?.ToString();
                if (!string.Equals(name, "Rules", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "Conditions", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return comparison.ChangedParts.Count > 0;
        }

        internal static void InspectDirectCallbacks(object instance, JArray report)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                    var value = field.GetValue(instance) as Delegate;
                    if (value == null) continue;
                    foreach (var callback in value.GetInvocationList())
                    {
                        string declaringAssembly = callback.Method.DeclaringType?.Assembly.GetName().Name;
                        string targetAssembly = callback.Target?.GetType().Assembly.GetName().Name;
                        string handler = callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;
                        if (declaringAssembly == null || !AuditedAssemblies.ContainsKey(declaringAssembly)
                            || (targetAssembly != null && !AuditedAssemblies.ContainsKey(targetAssembly)))
                            throw new InvalidOperationException("An unverified direct SDK callback is attached to "
                                + instance.GetType().FullName + ": " + handler + " (target assembly: " + targetAssembly + ").");
                        report.Add(new JObject { ["ownerType"] = instance.GetType().FullName, ["handler"] = handler });
                    }
                }
            }
        }

        internal static KBObject Fresh(KnowledgeBase kb, Guid guid)
        {
            var seed = kb.DesignModel.Objects.Get(guid) ?? throw new InvalidOperationException("Object not found by GUID.");
            var cache = kb.DesignModel.Objects as IKBModelObjectsCacheConfiguration
                ?? throw new InvalidOperationException("SDK public object-cache configuration is unavailable.");
            cache.Invalidate(seed);
            cache.RemoveFromCaches(seed);
            var fresh = kb.DesignModel.Objects.Get(seed.Key) ?? throw new InvalidOperationException("SDK fresh object lookup failed.");
            if (fresh.Guid != guid || ReferenceEquals(seed, fresh))
                throw new InvalidOperationException("SDK did not return a new instance of the requested GUID after cache removal.");
            return fresh;
        }

        internal static string Source(KBObject obj)
        {
            var part = GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "Events") as ISource;
            return part?.Source ?? string.Empty;
        }

        internal static string Token(KBObject obj)
            => WriteService.ComputeContentVersionToken(obj, Source(obj));

        internal static string ContentHash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static bool SourceEquivalent(string left, string right)
            => NormalizeSource(left) == NormalizeSource(right);

        private static string NormalizeSource(string value)
            => (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

        internal static string Placement(KBObject obj) => obj.Parent?.Guid.ToString() ?? "root";

        internal static Dictionary<Guid, string> Inventory(KnowledgeBase kb)
        {
            var ids = kb.DesignModel.Objects.Cast<KBObject>().Select(o => o.Guid).ToArray();
            var result = new Dictionary<Guid, string>();
            foreach (Guid guid in ids)
            {
                var obj = Fresh(kb, guid);
                result.Add(guid, obj.VersionId.ToString(CultureInfo.InvariantCulture) + ":"
                    + obj.LastUpdate.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + ":"
                    + obj.Parent?.Guid.ToString() + ":" + obj.Name + ":" + obj.TypeDescriptor.Id);
            }
            return result;
        }

        internal static JArray ChangedOthers(Dictionary<Guid, string> before, Dictionary<Guid, string> after, Guid target)
            => new JArray(before.Keys.Concat(after.Keys).Distinct().Where(g => g != target
                && (!before.ContainsKey(g) || !after.ContainsKey(g) || before[g] != after[g])).Select(g => g.ToString()));

        private static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }
    }
}
