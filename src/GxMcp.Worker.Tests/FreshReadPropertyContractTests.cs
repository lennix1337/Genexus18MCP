using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #733: what a property call that cannot obtain an independent read of the persisted
    /// object tells the caller, and which calls need that read.
    /// </summary>
    public class FreshReadPropertyContractTests
    {
        private static string PropertySource()
            => RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));

        private static string ObjectSource()
            => RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectService.cs"));

        [Fact]
        public void TheErrorCarriesTheSpecificReasonInTheFirstLineOfItsMessage()
        {
            JObject diagnostic = FreshObjectReload.Describe(FreshReloadOutcome.SameInstanceRetained, "UserControl");

            var envelope = JObject.Parse(PropertyService.FreshReadUnavailableEnvelope(
                "SyntheticControl",
                "The current persisted version could not be read; no property write was attempted.",
                diagnostic));

            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("SyntheticControl", envelope["target"]?.ToString());
            Assert.Equal("FreshReadUnavailable", envelope["error"]?["code"]?.ToString());
            string message = envelope["error"]?["message"]?.ToString();
            Assert.StartsWith("The current persisted version could not be read; no property write was attempted. ", message);
            Assert.Contains("same in-memory UserControl instance", message);
            Assert.DoesNotContain("\n", message);
            Assert.Equal(diagnostic["hint"]?.ToString(), envelope["error"]?["hint"]?.ToString());
            Assert.Equal("SameInstanceRetained", envelope["error"]?["reason"]?.ToString());
        }

        [Fact]
        public void WithoutADiagnosticTheHeadlineStandsAlone()
        {
            var envelope = JObject.Parse(PropertyService.FreshReadUnavailableEnvelope(
                "SyntheticControl", "The object could not be refreshed inside the property transaction; no write was committed.", null));

            Assert.Equal("FreshReadUnavailable", envelope["error"]?["code"]?.ToString());
            Assert.Equal("The object could not be refreshed inside the property transaction; no write was committed.",
                envelope["error"]?["message"]?.ToString());
        }

        [Fact]
        public void EveryFreshReadFailureOfAPropertyCallGoesThroughTheSharedEnvelope()
        {
            string src = PropertySource();

            // Three sites read the object independently: the reconciliation read, the preview /
            // version-checked write, and the refresh inside the write transaction.
            Assert.Equal(3, SourceAssert.Count(src, "return FreshReadUnavailableEnvelope(target,"));
            Assert.Equal(3, SourceAssert.Count(src, "_objectService.GetLastResolutionDiagnostic()"));
            Assert.DoesNotContain("code: \"FreshReadUnavailable\"", src);
        }

        [Fact]
        public void TheReconciliationReadNoLongerClaimsAFenceItCannotSee()
        {
            string src = PropertySource();

            // The Worker does not hold the recovery fences; the Gateway's journal does. Saying that a
            // fence "remains in place" was a statement about state this read never looked at.
            Assert.DoesNotContain("fence remains in place", src);
            Assert.Contains("no pending timeout recovery fence was cleared", src);
            Assert.Contains("genexus_connection_recover action=journal_status", src);
        }

        [Fact]
        public void APreviewNeedsTheSameIndependentReadAsTheWrite()
        {
            string body = SourceAssert.MethodBody(PropertySource(), "public string SetProperties(string target, JObject properties");

            int precondition = body.IndexOf("if (dryRun || patching || !string.IsNullOrWhiteSpace(expectedVersion))", StringComparison.Ordinal);
            Assert.True(precondition >= 0, "the independent read must cover dryRun, a template patch and expectedVersion");

            int firstRead = body.IndexOf("FindFreshTarget(obj)", StringComparison.Ordinal);
            int preview = body.IndexOf("if (dryRun)", StringComparison.Ordinal);
            int transaction = body.IndexOf("BeginTransaction()", StringComparison.Ordinal);
            Assert.True(precondition < firstRead, "the read belongs inside the precondition");
            Assert.True(firstRead < preview, "the preview may only answer after the independent read succeeded");
            Assert.True(preview < transaction, "the preview returns before the write transaction");
            Assert.Contains("[\"independentReadVerified\"] = true", body);
        }

        [Fact]
        public void TheWriteTransactionStillRefreshesTheObjectBeforeItChangesAnything()
        {
            string body = SourceAssert.MethodBody(PropertySource(), "public string SetProperties(string target, JObject properties");

            int transaction = body.IndexOf("BeginTransaction()", StringComparison.Ordinal);
            int refresh = body.IndexOf("var transactionObject = _objectService.FindObjectFreshByIdentity(obj);", transaction, StringComparison.Ordinal);
            int firstApply = body.IndexOf("ApplyPropertyValue(", transaction, StringComparison.Ordinal);
            Assert.True(refresh > transaction, "the transaction must refresh the object");
            Assert.True(refresh < firstApply, "the refresh must precede the first property change");
        }

        [Fact]
        public void TheIdentityReloadEvictsTheRetainedInstanceThroughTheSharedReload()
        {
            string objects = ObjectSource();
            string body = SourceAssert.MethodBody(objects, "internal KBObject FindObjectFreshByIdentity(KBObject expected)");

            // Issue #733: invalidating alone left UserControl, Image, ThemeClass and DesignSystem
            // pointing at the same instance. The release step is what evicts it.
            Assert.Contains("FreshObjectReload.Reload(", body);
            Assert.Contains("InvalidateAllReadCaches();", body);
            Assert.Contains("EvictRetainedInstance(candidate);", body);
            Assert.Contains("FreshObjectReload.Describe(outcome, type)", body);

            string eviction = SourceAssert.MethodBody(objects, "private static void EvictRetainedInstance(KBObject obj)");
            Assert.Contains("(obj.Model.Objects as IKBModelObjectsCacheConfiguration)?.RemoveFromCaches(obj)", eviction);

            // The by-name variant shares the same eviction so the two cannot drift again.
            string byTarget = SourceAssert.MethodBody(objects, "internal KBObject FindObjectFresh(string target");
            Assert.Contains("EvictRetainedInstance(seed);", byTarget);
        }
    }
}
