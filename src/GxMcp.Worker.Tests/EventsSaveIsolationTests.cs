using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class EventsSaveIsolationTests
    {
        private static Dictionary<string, string> NewWwpBundle() => new Dictionary<string, string>
        {
            ["Artech.Architecture.Common"] = "978fab2afad4daaa28aecffd5cd327367b1bb9c0a8f027cfda74911a294eac12",
            ["Artech.Genexus.Common"] = "cb7676857b75b817f5717edb37c4e19fbc06b2bb07feed10e64d74cb9bb159da",
            ["Artech.Udm.Framework"] = "9ba54b1a27a320c10cefab53a204b9e9ed9b027f6f025b7e0122224b5a71e86c",
            ["Artech.Common.Properties"] = "89cf957262dd5eee40d76b281abc5bb978881bb99959578d11a912fc7c069ae6",
            ["Artech.Packages.Patterns"] = "1f5baabe79d15279f0e1759053b512cc880cf695553459dd860a01120896aba8",
            ["DVelop.Patterns.WorkWithPlus"] = "3c2ccea639862d7773ed10210aa9283d3c55949dbdb930aeb557c4c3c8441f38",
            ["DVelop.Extensions.WWPPackagesCommon"] = "cb343ab63adf5a41289bbb24d243616582cf7a8b7b89c4df24e04d231c7a4794",
            ["DVelop.Extensions.UserControls"] = "3bafe1786151c1389173281b4bc68013216f11aa8d31f3b85d3f238bf3836bc2",
            ["DVelop.Extensions.WWPPackages"] = "c61c891602067adfd5e1d8cf9f93d279fce6be2bc75ed580b8c099d323f5400f"
        };

        [Fact]
        public void NewWwpBundle_RequiresEveryExactAssembly_RejectsMixtures()
        {
            var bundle = NewWwpBundle();
            Assert.Equal("18.0.16.189595+wwp16.2.0.8386", EventsSaveIsolation.MatchAuditedBuild(bundle));
            foreach (var name in new List<string>(bundle.Keys))
            {
                var missing = NewWwpBundle(); missing.Remove(name);
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(missing));
                var changed = NewWwpBundle(); changed[name] = "changed";
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(changed));
            }
            bundle["DVelop.Patterns.WorkWithPlus"] = "16e15bcb8eaef779abd5b8ef9d33c724f58406979496612a253473688b52fb02";
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(bundle));
        }

        // GeneXus 18 U16 build 189647 with the same WorkWithPlus 16.2.0.8386 (the four DVelop hashes equal those of NewWwpBundle).
        private static Dictionary<string, string> NewWwpBundleOnBuild189647() => new Dictionary<string, string>
        {
            ["Artech.Architecture.Common"] = "160b59afdab14c160711de46f60e6644a2ec88f420a1cf2b86d97219fa3464c6",
            ["Artech.Genexus.Common"] = "f5be031ba5b656713fc759936ef7550e8698124ceeaebf676f65ad2c1cbbe996",
            ["Artech.Udm.Framework"] = "1e449aa0b8f1f2c80b081f635e43f7430df22f5d12ec2cb510f430f0d39127a2",
            ["Artech.Common.Properties"] = "35c62b0eab14b6948c6606fc56b968fb7330e5f9063161191ee0a16359ad9dc7",
            ["Artech.Packages.Patterns"] = "10154641bc0fa9a1019f3981e2680a59130c82ada2391254166221a69dfce991",
            ["DVelop.Patterns.WorkWithPlus"] = "3c2ccea639862d7773ed10210aa9283d3c55949dbdb930aeb557c4c3c8441f38",
            ["DVelop.Extensions.WWPPackagesCommon"] = "cb343ab63adf5a41289bbb24d243616582cf7a8b7b89c4df24e04d231c7a4794",
            ["DVelop.Extensions.UserControls"] = "3bafe1786151c1389173281b4bc68013216f11aa8d31f3b85d3f238bf3836bc2",
            ["DVelop.Extensions.WWPPackages"] = "c61c891602067adfd5e1d8cf9f93d279fce6be2bc75ed580b8c099d323f5400f"
        };

        [Fact]
        public void NewWwpBundleOnBuild189647_RequiresEveryExactAssembly_RejectsMixturesWithTheEarlierBuild()
        {
            var bundle = NewWwpBundleOnBuild189647();
            Assert.Equal("18.0.16.189647+wwp16.2.0.8386", EventsSaveIsolation.MatchAuditedBuild(bundle));
            Assert.Contains("18.0.16.189647+wwp16.2.0.8386", EventsSaveIsolation.AuditedBundleIds);
            foreach (var name in new List<string>(bundle.Keys))
            {
                var missing = NewWwpBundleOnBuild189647(); missing.Remove(name);
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(missing));
                var changed = NewWwpBundleOnBuild189647(); changed[name] = "changed";
                Assert.Null(EventsSaveIsolation.MatchAuditedBuild(changed));
                // One SDK assembly of the earlier build in the newer bundle is neither build.
                var earlier = NewWwpBundle();
                if (earlier[name] != bundle[name])
                {
                    var mixed = NewWwpBundleOnBuild189647(); mixed[name] = earlier[name];
                    Assert.Null(EventsSaveIsolation.MatchAuditedBuild(mixed));
                }
            }
            // The earlier build keeps being recognized as itself, never as the newer one.
            Assert.Equal("18.0.16.189595+wwp16.2.0.8386", EventsSaveIsolation.MatchAuditedBuild(NewWwpBundle()));
        }

        [Fact]
        public void CompleteBundle_PendingProofCannotAuthorizeBusinessObjectsOrUnknownStatus()
        {
            var pending = JObject.Parse("{status:'pending',syntheticObject:'SyntheticAuditOnly'}");
            Assert.True(EventsU16BundleAudit.MaySaveTarget(pending, "SyntheticAuditOnly"));
            Assert.False(EventsU16BundleAudit.MaySaveTarget(pending, "BusinessObject"));
            Assert.False(EventsU16BundleAudit.MaySaveTarget(pending, ""));
            Assert.False(EventsU16BundleAudit.MaySaveTarget(null, "SyntheticAuditOnly"));
            Assert.False(EventsU16BundleAudit.MaySaveTarget(JObject.Parse("{status:'unknown'}"), "BusinessObject"));
            Assert.True(EventsU16BundleAudit.MaySaveTarget(JObject.Parse("{status:'verified'}"), "BusinessObject"));
            Assert.Throws<InvalidOperationException>(() => EventsU16BundleAudit.RequireExactHash("audited", "changed", "SDK"));
        }

        [Theory]
        [InlineData("AuditedAssemblies", "18.0.16.189550")]
        [InlineData("UpdatedU16Assemblies", "18.0.16.189595")]
        public void AuditedBuild_RequiresOneCompleteBundle(string field, string build)
        {
            var hashes = new Dictionary<string, string>((Dictionary<string, string>)typeof(EventsSaveIsolation)
                .GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            Assert.Equal(build, EventsSaveIsolation.MatchAuditedBuild(hashes));
            hashes.Remove("DVelop.Patterns.WorkWithPlus");
            Assert.Equal(build, EventsSaveIsolation.MatchAuditedBuild(hashes));
            hashes.Remove("Artech.Architecture.Common");
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(hashes));
        }

        [Fact]
        public void AuditedBuild_RejectsMixedBuildAndUnknownExtension()
        {
            var hashes = new Dictionary<string, string>((Dictionary<string, string>)typeof(EventsSaveIsolation)
                .GetField("UpdatedU16Assemblies", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            hashes["Artech.Architecture.Common"] = "5de4b9fa160b2aca8fa08be8e070870c00286415d7ecdfad816aa5bdc406f283";
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(hashes));
            hashes["Artech.Architecture.Common"] = "978fab2afad4daaa28aecffd5cd327367b1bb9c0a8f027cfda74911a294eac12";
            hashes["DVelop.Patterns.WorkWithPlus"] = "unknown";
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(hashes));
        }

        [Fact]
        public void PinDiagnostics_DoNotExposePathsOrClaimPersistence()
        {
            var report = EventsSaveIsolation.DescribeConfiguredPins();
            Assert.Equal("isolation-preflight", report["phase"]);
            Assert.Equal(2, ((JArray)report["requiredPins"]).Count);
            Assert.False(report["persisted"].Value<bool>());
            Assert.False(report["saved"].Value<bool>());
            Assert.Empty((JArray)report["implicitOperations"]);
            Assert.Null(report.SelectToken("effectivePins.kbPath"));
        }

        [Theory]
        [InlineData("{}", false)]
        [InlineData("{writeAttempted:false}", false)]
        [InlineData("{writeAttempted:true, rollbackVerified:true, persistenceUncertain:true}", false)]
        [InlineData("{writeAttempted:true, persistenceUncertain:true}", true)]
        [InlineData("{writeAttempted:true, rollbackError:'failed'}", true)]
        [InlineData("{writeAttempted:true, objectSaved:true, sourceChanged:true, partPersisted:true}", true)]
        [InlineData("{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:true, otherObjectMetadataIntact:true, sourceChanged:false}", false)]
        [InlineData("{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:true, otherObjectMetadataIntact:true, sourceChanged:false, persistenceUncertain:true}", true)]
        [InlineData("{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:false, otherObjectMetadataIntact:true, sourceChanged:false}", true)]
        public void DirtyTracking_UsesPersistenceOutcome(string json, bool expected)
            => Assert.Equal(expected, WriteService.ShouldMarkIsolatedEventsDirty(JObject.Parse(json)));

        [Fact]
        public void TransactionOutcome_InvalidatesAllReadLayersAndIdentityAliasesWithoutSdk()
        {
            string name = "IsolationCache_" + Guid.NewGuid().ToString("N");
            string guid = Guid.NewGuid().ToString();
            var reader = new ObjectReader(null);
            var nameRequest = new ObjectReadRequest { Target = name, PartName = "Events", ClientFormat = "mcp" };
            var guidRequest = new ObjectReadRequest { Target = guid, PartName = "Events", ClientFormat = "mcp" };
            var readerCache = PopulateCache(typeof(ObjectReader), "_cache", ObjectReader.BuildCacheKey(nameRequest));
            PopulateCache(typeof(ObjectReader), "_cache", ObjectReader.BuildCacheKey(guidRequest));
            var patchCache = PopulateCache(typeof(PatchService), "_sourceCache", "WebPanel|" + name + "|Events");
            PopulateCache(typeof(PatchService), "_sourceCache", "WebPanel|" + guid + "|Events");
            var sourceCache = PopulateCache(typeof(ObjectService), "_readCache", guid.Replace("-", "") + "|events|raw");
            PopulateCache(typeof(ObjectService), "_readCache", guid.Replace("-", "") + "|full|all");
            Assert.True(reader.TryGetCached(name, "Events", null, null, "mcp", out _));
            Assert.True(reader.TryGetCached(guid, "Events", null, null, "mcp", out _));

            WriteService.InvalidateIsolatedEventsReadCaches();

            Assert.False(reader.TryGetCached(name, "Events", null, null, "mcp", out _));
            Assert.False(reader.TryGetCached(guid, "Events", null, null, "mcp", out _));
            Assert.Empty(readerCache);
            Assert.Empty(patchCache);
            Assert.Empty(sourceCache);
        }

        private static IDictionary PopulateCache(Type owner, string field, string key)
        {
            var cache = (IDictionary)owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Type entryType = cache.GetType().GetGenericArguments()[1];
            object entry = Activator.CreateInstance(entryType, nonPublic: true);
            foreach (var property in entryType.GetProperties())
            {
                if (property.PropertyType == typeof(string)) property.SetValue(entry, "stale persisted source");
                if (property.PropertyType == typeof(DateTime)) property.SetValue(entry, DateTime.UtcNow);
            }
            cache[key] = entry;
            return cache;
        }

        private class SyntheticBase
        {
            private readonly Action _onSave;
            public int Calls;
            public SyntheticBase() { _onSave = () => Calls++; }
        }
        private sealed class SyntheticChild : SyntheticBase { }

        [Fact]
        public void UnknownDirectCallbackInBaseClass_IsRejectedWithoutCallingIt()
        {
            var instance = new SyntheticChild();
            Assert.Throws<InvalidOperationException>(() => EventsSaveIsolation.InspectDirectCallbacks(instance, new JArray()));
            Assert.Equal(0, instance.Calls);
        }

        [Fact]
        public void MissingObject_IsRejectedBeforeSdkAccess()
        {
            // The refusal is an InvalidOperationException that also carries its named finding.
            var refusal = Assert.ThrowsAny<InvalidOperationException>(() => EventsSaveIsolation.Preflight(null));
            var typed = Assert.IsType<EventsIsolationRefusalException>(refusal);
            Assert.Equal("IsolationTargetTypeUnsupported", Assert.Single(typed.Findings).Code);
        }

        [Fact]
        public void PatternVirtualPart_IsTheOnlyCertifiedVirtualPart()
        {
            Assert.True(EventsSaveIsolation.IsAllowedPartTypeName(
                "Artech.Packages.Patterns.Objects.PatternVirtualPart"));
            Assert.False(EventsSaveIsolation.IsAllowedPartType(typeof(KBObjectPart)));
        }

        [Fact]
        public void SourceComparison_AllowsSdkEolNormalizationOnly()
        {
            Assert.True(EventsSaveIsolation.SourceEquivalent("a" + Environment.NewLine + "b" + Environment.NewLine, "a" + (char)10 + "b" + (char)10));
            Assert.False(EventsSaveIsolation.SourceEquivalent("a\nb", "a\nc"));
        }

        [Fact]
        public void PatternProjection_DoesNotTrustPartNamesWithoutExactEquality()
        {
            Assert.True(EventsSaveIsolation.IsExpectedPatternProjection(
                ObjectMoveSnapshot.Comparison.Verified("same")));
            Assert.False(EventsSaveIsolation.IsExpectedPatternProjection(
                ObjectMoveSnapshot.Comparison.Failed(new[] { "Rules" }, "different")));
            Assert.False(EventsSaveIsolation.IsExpectedPatternProjection(
                ObjectMoveSnapshot.Comparison.Failed(new[] { "Conditions" }, "different")));
        }

        [Fact]
        public void CountPatternInstances_HandlesNullObjectsWithoutThrowing()
        {
            var objects = new KBObject[] { null, null };
            int count = EventsSaveIsolation.CountPatternInstances(objects, o => o?.TypeDescriptor?.Name);
            Assert.Equal(0, count);

            int nullCollectionCount = EventsSaveIsolation.CountPatternInstances((IEnumerable<KBObject>)null);
            Assert.Equal(0, nullCollectionCount);
        }

        [Fact]
        public void MetadataAudit_DetectsOtherObjectsChangedAddedRemoved_ExcludesOnlyTarget()
        {
            var target = Guid.NewGuid(); var changed = Guid.NewGuid();
            var removed = Guid.NewGuid(); var added = Guid.NewGuid(); var unchanged = Guid.NewGuid();
            var before = new Dictionary<Guid, string> { [target] = "1", [changed] = "1", [removed] = "1", [unchanged] = "1" };
            var after = new Dictionary<Guid, string> { [target] = "2", [changed] = "2", [added] = "1", [unchanged] = "1" };
            var result = EventsSaveIsolation.ChangedOthers(before, after, target);
            Assert.Equal(3, result.Count);
            Assert.Contains(changed.ToString(), result.ToObject<string[]>());
            Assert.Contains(removed.ToString(), result.ToObject<string[]>());
            Assert.Contains(added.ToString(), result.ToObject<string[]>());
        }
    }
}
