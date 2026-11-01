using System;
using System.Collections.Generic;
using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The receipt of a batch property write that needs a rollback, read from the production method. Two paths need no Knowledge Base:
    /// the refused rollback (the object cannot be read fresh) and the rollback whose first step throws. Both are called through reflection,
    /// so a change in the production branch fails here. The paths that need the SDK, and the simple route, are pinned by shape, the way
    /// PropertyRollbackRestoreTests pins SetProperties.
    /// </summary>
    public class PropertyRollbackProductionTests
    {
        private const string Restore = "RollbackPropertySnapshot";

        private static string Rollback(PropertyService service, PropertyRollbackRestore.PersistenceProof proof)
        {
            MethodInfo method = typeof(PropertyService).GetMethod(Restore, BindingFlags.NonPublic | BindingFlags.Instance);
            Type contextType = typeof(PropertyService).GetNestedType("PropertyRollbackContext", BindingFlags.NonPublic);
            object context = Activator.CreateInstance(contextType, nonPublic: true);
            Set(context, "RequestedNames", new List<string> { "MasterPage" });
            Set(context, "PreviousValues", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MasterPage"] = "SynthMasterA" });
            Set(context, "NativeNames", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MasterPage"] = "MasterPage" });
            return (string)method.Invoke(service, new object[]
            {
                "SynthHost", null, null, context, "The saved property 'MasterPage' could not be confirmed.", proof
            });
        }

        private static void Set(object target, string property, object value)
        {
            target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, value);
        }

        private static JObject Refused(PropertyRollbackRestore.PersistenceProof proof)
        {
            // An ObjectService without a Knowledge Base cannot read the object fresh: the refusal branch runs.
            return JObject.Parse(Rollback(new PropertyService(new ObjectService(null, null)), proof));
        }

        [Fact]
        public void ARefusedRollbackAfterAVerifiedChangedPartsReportsPersistedTrue()
        {
            JObject envelope = Refused(PropertyRollbackRestore.PersistenceProof.Verified);

            Assert.Equal("PropertyRollbackUnsafe", (string)envelope["error"]["code"]);
            Assert.True((bool)envelope["committed"]);
            Assert.Equal(JTokenType.Boolean, envelope["persisted"].Type);
            Assert.True((bool)envelope["persisted"]);
            Assert.True((bool)envelope["persistenceStateKnown"]);
        }

        [Fact]
        public void ARefusedRollbackAfterAMismatchReportsPersistedFalseWithAKnownState()
        {
            // The read-back found the previous value: the requested one is not in the object, and the receipt must not say it is.
            JObject envelope = Refused(PropertyRollbackRestore.PersistenceProof.NotPersisted);

            Assert.Equal("PropertyRollbackUnsafe", (string)envelope["error"]["code"]);
            Assert.False((bool)envelope["persisted"]);
            Assert.True((bool)envelope["persistenceStateKnown"]);
        }

        [Fact]
        public void ARefusedRollbackWithoutAReadReportsAnUnknownStateAsABoolean()
        {
            JObject envelope = Refused(PropertyRollbackRestore.PersistenceProof.Unknown);

            Assert.Equal(JTokenType.Boolean, envelope["persisted"].Type);
            Assert.False((bool)envelope["persisted"]);
            Assert.False((bool)envelope["persistenceStateKnown"]);
        }

        [Fact]
        public void ARollbackThatFailsBeforeItsCommitKeepsTheEarlierProof()
        {
            // A service without an ObjectService throws in the first step: the failure branch runs before anything is restored.
            var failing = new PropertyService(null);

            JObject verified = JObject.Parse(Rollback(failing, PropertyRollbackRestore.PersistenceProof.Verified));
            JObject mismatch = JObject.Parse(Rollback(failing, PropertyRollbackRestore.PersistenceProof.NotPersisted));

            Assert.Equal("PropertyRollbackFailed", (string)verified["error"]["code"]);
            Assert.True((bool)verified["persisted"]);
            Assert.True((bool)verified["persistenceStateKnown"]);
            Assert.False((bool)mismatch["persisted"]);
            Assert.True((bool)mismatch["persistenceStateKnown"]);
        }

        [Fact]
        public void TheSimpleRouteReceiptSaysNotPersistedOnlyForAVerificationThatFoundAnotherValue()
        {
            MethodInfo receipt = typeof(PropertyService).GetMethod("CommittedReceipt", BindingFlags.NonPublic | BindingFlags.Static);

            JObject notPersisted = JObject.Parse((string)receipt.Invoke(null, new object[]
            {
                "{\"status\":\"error\",\"error\":{\"code\":\"PropertyNotPersisted\",\"message\":\"x\"}}"
            }));
            JObject writeFailed = JObject.Parse((string)receipt.Invoke(null, new object[]
            {
                "{\"status\":\"error\",\"error\":{\"code\":\"PropertyWriteFailed\",\"message\":\"x\"}}"
            }));

            Assert.True((bool)notPersisted["committed"]);
            Assert.False((bool)notPersisted["persisted"]);
            Assert.True((bool)notPersisted["persistenceStateKnown"]);
            Assert.False((bool)writeFailed["persisted"]);
            Assert.False((bool)writeFailed["persistenceStateKnown"]);
        }

        [Fact]
        public void ASimpleRouteSuccessReachesTheCallerAsItWasVerified()
        {
            // A verified write is a success. The receipt marks only errors, so the reply keeps its values and its verification.
            MethodInfo receipt = typeof(PropertyService).GetMethod("CommittedReceipt", BindingFlags.NonPublic | BindingFlags.Static);
            string verified = "{\"status\":\"ok\",\"code\":\"PropertyApplied\",\"result\":{\"property\":\"MasterPage\",\"value\":\"SynthMasterB\","
                + "\"persisted\":\"SynthMasterB\",\"persistedVerified\":true}}";

            Assert.Equal(verified, (string)receipt.Invoke(null, new object[] { verified }));
        }

        [Fact]
        public void TheBatchWriteMarksTheCommitBeforeAnyPostCommitExit()
        {
            string body = SourceAssert.MethodBody(
                RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs")),
                "public string SetProperties(string target, JObject properties,");

            int commit = body.IndexOf("trans.Commit();", StringComparison.Ordinal);
            int marked = body.IndexOf("transactionCommitted = true;", StringComparison.Ordinal);

            Assert.True(commit > 0 && marked > commit, "the commit flag must be set right after the batch transaction commits");
            Assert.Contains("if (!transactionCommitted) return failure;", body);
        }

        [Fact]
        public void EveryRollbackExitWritesTheCommitAndAPersistenceProof()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));
            string rollback = SourceAssert.MethodBody(src, "private string RollbackPropertySnapshot(");
            string batch = SourceAssert.MethodBody(src, "public string SetProperties(string target, JObject properties,");

            // Refused, verified-restore and failed-restore exits each write the receipt. The four callers of the rollback each pass a proof:
            // three pick it through a function of PropertyRollbackRestore, which the mapping tests cover; the first read-back failure passes
            // a literal, and the batch's own failure exit after the commit passes one more. A proof argument removed from a caller changes this count.
            Assert.Equal(3, SourceAssert.Count(rollback, "PropertyRollbackRestore.MarkCommitted("));
            Assert.Equal(4, SourceAssert.Count(batch, "RollbackPropertySnapshot(target, obj, snapshot, rollback,"));
            Assert.Equal(2, SourceAssert.Count(batch, "PersistenceProof."));
        }

        [Fact]
        public void EverySimpleRouteExitThatFollowsACommitWritesTheReceipt()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));

            foreach (string method in new[]
            {
                "public string SetProperty(string target, string propName, string value,",
                "private string SetWebPanelReferenceProperty(",
                "private string SetDataProviderOutputSdt(",
            })
            {
                string body = SourceAssert.MethodBody(src, method);
                Assert.Contains("transactionCommitted = true;", body);
                // The failure after the commit passes through the receipt helper; the verification reply is checked by behavior, below.
                Assert.Contains("return transactionCommitted ? CommittedReceipt(failure) : failure;", body);
            }
        }
    }
}
