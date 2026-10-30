using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpLayoutTests
    {
        [Fact]
        public void InvalidationFailure_PreservesPersistenceReceiptAndSkipsUnmutatedCalls()
        {
            var receipt = JObject.Parse("{snapshot:{path:'saved'},rollbackVerified:true}");
            int calls = 0;
            System.Action fail = () => { calls++; throw new System.InvalidOperationException("index unavailable"); };
            WwpActionService.FinalizeLayoutReadCaches(false, receipt, fail);
            Assert.Equal(0, calls);
            WwpActionService.FinalizeLayoutReadCaches(true, receipt, fail);
            Assert.Equal(1, calls);
            Assert.Equal("index unavailable", receipt["readCacheInvalidationError"].Value<string>());
            Assert.Equal("saved", receipt["snapshot"]["path"].Value<string>());
            Assert.True(receipt["rollbackVerified"].Value<bool>());
        }

        [Fact]
        public void ParentGate_UsesNativeWebPanelClassSharedByWebComponent()
        {
            var native = Assembly.Load("Artech.Genexus.Common");
            Assert.True(WwpActionService.IsLayoutParentType(native.GetType("Artech.Genexus.Common.Objects.WebPanel", true)));
            Assert.False(WwpActionService.IsLayoutParentType(native.GetType("Artech.Genexus.Common.Objects.Transaction", true)));
            Assert.False(WwpActionService.IsLayoutParentType(native.GetType("Artech.Genexus.Common.Objects.Procedure", true)));
            Assert.False(WwpActionService.IsLayoutParentType(null));
        }

        [Fact]
        public void MutationFinalization_ClearsPublicPatternAndFormPayloadsButPreviewDoesNot()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var set = typeof(ObjectService).GetMethod("SetReadCache", flags);
            var get = typeof(ObjectService).GetMethod("TryGetReadCache", flags);
            string prefix = System.Guid.NewGuid().ToString("N");
            foreach (string part in new[] { "patterninstance", "webform", "events" })
            {
                string key = prefix + "|" + part + "|raw";
                set.Invoke(null, new object[] { key, "old layout source" });
                WwpActionService.InvalidateLayoutReadCaches(false);
                Assert.True((bool)get.Invoke(null, new object[] { key, null }));
            }
            WwpActionService.InvalidateLayoutReadCaches(true);
            foreach (string part in new[] { "patterninstance", "webform", "events" })
                Assert.False((bool)get.Invoke(null, new object[] { prefix + "|" + part + "|raw", null }));
        }

        [Fact]
        public void ProjectionReceipt_RequiresEveryNativeCallbackAndParentSave()
        {
            var result = new WwpProjectionHelper.ProjectionResult
            {
                LifecycleAttempted = true, ShouldBuild = true, BeforeStartBuild = true,
                AfterImportResources = true, UpdateParentObject = true, AfterEndBuild = true
            };
            Assert.False(WwpActionService.LayoutProjectionReceipt(result)["confirmed"].Value<bool>());
            result.ParentSaved = true;
            Assert.True(WwpActionService.LayoutProjectionReceipt(result)["confirmed"].Value<bool>());
            result.AfterEndBuild = false;
            result.Failure = "Callback failed";
            var receipt = WwpActionService.LayoutProjectionReceipt(result);
            Assert.False(receipt["confirmed"].Value<bool>());
            Assert.Equal("Callback failed", receipt["failure"].Value<string>());
        }

        // Mirrors SourcePart's lazy getter and serialization of its backing field.
        private sealed class LazySource : ISource
        {
            private readonly string persisted;
            private string materialized;
            internal int SetterCalls;
            internal LazySource(string persisted) { this.persisted = persisted; }
            public string Source
            {
                get => materialized ?? (materialized = persisted);
                set { SetterCalls++; materialized = value; }
            }
            internal string Serialize() => materialized ?? string.Empty;
        }

        [Fact]
        public void SnapshotPreparation_MaterializesEveryLazySourceWithoutChangingIt()
        {
            var events = new LazySource("Event Start\n  &Value = 1\nEndEvent");
            var rules = new LazySource("parm(&Value);");
            Assert.Equal(string.Empty, events.Serialize());
            WwpActionService.PrepareLayoutSnapshotSources(new ISource[] { events, rules });
            Assert.Equal("Event Start\n  &Value = 1\nEndEvent", events.Serialize());
            Assert.Equal("parm(&Value);", rules.Serialize());
            Assert.Equal(0, events.SetterCalls);
            Assert.Equal(0, rules.SetterCalls);
        }

        private sealed class NativeElement
        {
            public string Name { get; set; }
            public string Type { get; set; }
        }

        [Fact]
        public void NativeVariableIdentity_UsesChildNameRatherThanSpecificationType()
        {
            Assert.True(WwpActionService.IsLayoutVariableElement(new NativeElement { Name = "variable", Type = "TableVariable" }));
            Assert.False(WwpActionService.IsLayoutVariableElement(new NativeElement { Name = "attribute", Type = "TableAttribute" }));
        }

        private const string Empty = "<instance><WPRoot Template='Empty'><table name='TableContent' defaultType='Responsive' childrenOrderedList='old'/></WPRoot></instance>";
        private static JObject Args(string children) => new JObject
        {
            ["tablePath"] = "TableContent", ["children"] = JArray.Parse(children)
        };

        [Fact]
        public void Plan_AppendsTypedNestedChildrenWithoutRewritingBaseline()
        {
            var doc = XDocument.Parse(Empty);
            doc.Root.Element("WPRoot").Element("table").Attribute("childrenOrderedList").Remove();
            var before = new XDocument(doc);
            var args = Args("[{type:'table',name:'Fields',children:[{type:'variable',name:'Application'},{type:'userAction',name:'Save',caption:'Save'}]}]");
            Assert.Null(WwpActionService.PlanLayout(doc, args)["error"]);
            Assert.Equal("Responsive", (string)doc.Root.Element("WPRoot").Element("table").Attribute("defaultType"));
            Assert.Null(doc.Root.Element("WPRoot").Element("table").Attribute("childrenOrderedList"));
            Assert.True(WwpActionService.VerifyLayoutPattern(before, doc, args));
        }

        [Theory]
        [InlineData("table")]
        [InlineData("userAction")]
        public void Plan_RejectsOrderedAdditionsToExistingOrderListWithoutMutation(string type)
        {
            var doc = XDocument.Parse(Empty);
            var result = WwpActionService.PlanLayout(doc, Args("[{type:'" + type + "',name:'NewChild'}]"));
            Assert.Equal("WwpOrderedContainerUnsupported", result["code"].Value<string>());
            Assert.True(XNode.DeepEquals(XDocument.Parse(Empty), doc));
        }

        [Fact]
        public void Plan_VariableAdditionPreservesExistingOrderList()
        {
            var doc = XDocument.Parse(Empty);
            var args = Args("[{type:'variable',name:'Application'}]");
            Assert.Null(WwpActionService.PlanLayout(doc, args)["error"]);
            Assert.Equal("old", (string)doc.Root.Element("WPRoot").Element("table").Attribute("childrenOrderedList"));
            Assert.True(WwpActionService.VerifyLayoutPattern(XDocument.Parse(Empty), doc, args));
        }

        [Theory]
        [InlineData("[{type:'variable',name:'Application',basicType:'VarChar'}]")]
        [InlineData("[{type:'variable',name:'Application',domain:'Other'}]")]
        [InlineData("[{type:'attribute',name:'Application'}]")]
        [InlineData("[{type:'table',name:'Fields',defaultType:'Regular'}]")]
        [InlineData("[{type:'table',name:'Fields',childrenOrderedList:'x'}]")]
        [InlineData("[{type:'variable',name:'Application'},{type:'variable',name:'Application'}]")]
        [InlineData("[{type:'variable',name:'TableContent'}]")]
        [InlineData("[null]")]
        public void Plan_RejectsRetypingRawMetadataUnsupportedAndDuplicateChildren(string children)
        {
            var doc = XDocument.Parse(Empty);
            Assert.NotNull(WwpActionService.PlanLayout(doc, Args(children))["error"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(Empty), doc));
        }

        [Fact]
        public void PersistedPattern_RejectsMissingChildOrUnrelatedMutation()
        {
            var args = Args("[{type:'variable',name:'Application'}]");
            var after = XDocument.Parse(Empty);
            Assert.Null(WwpActionService.PlanLayout(after, args)["error"]);
            after.Root.Element("WPRoot").SetAttributeValue("Template", "Other");
            Assert.False(WwpActionService.VerifyLayoutPattern(XDocument.Parse(Empty), after, args));
            Assert.False(WwpActionService.VerifyLayoutPattern(XDocument.Parse(Empty), XDocument.Parse(Empty), args));
        }

        [Theory]
        [InlineData("var:7", true)]
        [InlineData("att:7", false)]
        [InlineData("var:8", false)]
        public void Projection_RequiresActualVariableIdentity(string binding, bool expected)
        {
            string before = "<Form><gxTextBlock ControlName='Original' Caption='Keep'/></Form>";
            string after = "<Form><gxTextBlock ControlName='Original' Caption='Keep'/><gxEdit ControlName='Application' AttId='" + binding + "'/></Form>";
            var check = WwpActionService.VerifyLayoutProjection(before, after,
                JArray.Parse("[{type:'variable',name:'Application'}]"), new Dictionary<string, string> { ["Application"] = "var:7" });
            Assert.Equal(expected, check["confirmed"].Value<bool>());
            if (!expected) Assert.Equal("variableControlMissingOrAmbiguous", check["failureStage"].Value<string>());
        }

        [Fact]
        public void Projection_RejectsChangedPreexistingControl()
        {
            var check = WwpActionService.VerifyLayoutProjection("<Form><gxEdit ControlName='Old' AttId='var:1'/></Form>",
                "<Form><gxEdit ControlName='Old' AttId='att:1'/><gxEdit ControlName='New' AttId='var:2'/></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.False(check["confirmed"].Value<bool>());
        }

        [Theory]
        [InlineData("<gxEdit ControlName='Two'/><gxEdit ControlName='One'/>")]
        [InlineData("<gxEdit ControlName='One'><Property>changed</Property></gxEdit><gxEdit ControlName='Two'/>")]
        public void Projection_RejectsPreexistingOrderOrPropertyChanges(string controls)
        {
            var check = WwpActionService.VerifyLayoutProjection(
                "<Form><gxEdit ControlName='One'/><gxEdit ControlName='Two'/></Form>",
                "<Form>" + controls + "<gxEdit ControlName='New' AttId='var:2'/></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.False(check["confirmed"].Value<bool>());
        }

        [Theory]
        [InlineData("<td>Original</td>", "<td>Changed</td>")]
        [InlineData("<td>Original</td>", "")]
        [InlineData("<gxLabel Caption='Original'/>", "<gxLabel Caption='Changed'/>")]
        [InlineData("<div><b>Original</b></div>", "<div><i>Original</i></div>")]
        [InlineData("<!--keep--><td/>", "<td/>")]
        public void Projection_RejectsUnnamedContentLossOrMutation(string before, string after)
        {
            var check = WwpActionService.VerifyLayoutProjection("<Form>" + before + "</Form>",
                "<Form>" + after + "<gxEdit ControlName='New' AttId='var:2'/></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.False(check["confirmed"].Value<bool>());
        }

        [Fact]
        public void Projection_PreservesUnnamedContentAndAllowsNewAnonymousLayoutWrappers()
        {
            const string preserved = "<td>Original</td><!--keep--><gxLabel Caption='Original'/>";
            var check = WwpActionService.VerifyLayoutProjection("<Form>" + preserved + "</Form>",
                "<Form>" + preserved + "<tr><td><gxEdit ControlName='New' AttId='var:2'/></td></tr></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.True(check["confirmed"].Value<bool>());
            Assert.True(check["unnamedContentPreserved"].Value<bool>());
        }

        [Fact]
        public void NativeLayoutProjection_AcceptsRegeneratedStructuralIdsAndNativeBindings()
        {
            const string before = "<layout id='11111111-1111-1111-1111-111111111111'><table controlName='Content' tableType='Responsive' id='1'><row><cell><data attribute='var:1' id='33333333-3333-3333-3333-333333333333'/></cell></row></table></layout>";
            const string additions = "<row><cell><data attribute='var:7'/></cell></row><row><cell><action controlName='BtnSave' onClickEvent='&apos;DoSave&apos;' caption='Save'/></cell></row>";
            string after = before.Replace("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")
                .Replace("</table>", additions + "</table>");
            var children = JArray.Parse("[{type:'variable',name:'Application'},{type:'userAction',name:'Save'}]");
            var bindings = new Dictionary<string, string> { ["Application"] = "var:7" };
            Assert.True(WwpActionService.VerifyLayoutProjection(before, after, children, bindings)["confirmed"].Value<bool>());
            foreach (string changed in new[] { after.Replace("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"), after.Replace("id='1'", "id='2'"), after.Replace("Responsive", "Fixed"),
                after.Replace("var:1", "var:2"), after.Replace("DoSave", "DoOther"), after.Replace("BtnSave", "Save") })
                Assert.False(WwpActionService.VerifyLayoutProjection(before, changed, children, bindings)["confirmed"].Value<bool>());
        }

        [Fact]
        public void NativeLayoutProjection_KeepsExistingStructuralIdToleranceWithReferences()
        {
            const string before = "<layout id='11111111-1111-1111-1111-111111111111' target='11111111-1111-1111-1111-111111111111'><table controlName='Main'/></layout>";
            string after = before.Replace("id='11111111-1111-1111-1111-111111111111'", "id='22222222-2222-2222-2222-222222222222'");
            Assert.True(WwpActionService.VerifyLayoutProjection(before, after,
                new JArray(), new Dictionary<string, string>())["confirmed"].Value<bool>());
        }

        [Theory]
        [InlineData("11111111-1111-1111-1111-111111111111")]
        [InlineData("22222222-2222-2222-2222-222222222222")]
        public void NativeLayoutProjection_KeepsTabAndPageIdsSignificant(string changedId)
        {
            const string before = "<layout><tab id='11111111-1111-1111-1111-111111111111' controlName='Tabs'><item id='22222222-2222-2222-2222-222222222222' itemControlName='Page' caption='Page'><table controlName='Main'/></item></tab></layout>";
            string after = before.Replace(changedId, "33333333-3333-3333-3333-333333333333");
            Assert.False(WwpActionService.VerifyLayoutProjection(before, after,
                new JArray(), new Dictionary<string, string>())["confirmed"].Value<bool>());
        }

        [Fact]
        public void Events_AllowsOnlyExactEmptyGeneratedMarkersInNewAction()
        {
            const string start = "/* Generated by DVelop Work With Plus Pattern [Start] - Do not change */";
            const string end = "/* Generated by DVelop Work With Plus Pattern [End] - Do not change */";
            string before = "Event Start\n\t" + start + "\n\t" + end + "\nEndEvent\n\nEvent 'Existing'\n &Value = 1\nEndEvent\n";
            string stub = "Event 'DoSave'\n\n\t" + start + "\n\n\t" + end + "\n\nEndEvent\n\n";
            var children = JArray.Parse("[{type:'userAction',name:'Save'}]");
            string after = before.Replace("Event 'Existing'", stub + "Event 'Existing'");
            Assert.True(WwpActionService.VerifyLayoutEvents(before, after, children));
            Assert.False(WwpActionService.VerifyLayoutEvents(before, after.Replace(stub, stub.Replace(start, start + "\n &Value = 2")), children));
            Assert.False(WwpActionService.VerifyLayoutEvents(before, after.Replace(stub, stub.Replace(end, "/* Other comment */")), children));
            Assert.False(WwpActionService.VerifyLayoutEvents(before, after.Replace("&Value = 1", "&Value = 2"), children));
        }

        [Fact]
        public void Events_AllowsOnlyAnEmptyNewActionStub()
        {
            string original = "Event Start\n  &Value = 1\nEndEvent\n";
            var children = JArray.Parse("[{type:'userAction',name:'Save'}]");
            Assert.True(WwpActionService.VerifyLayoutEvents(original, original + "Event 'DoSave'\nEndEvent\n", children));
            Assert.False(WwpActionService.VerifyLayoutEvents(original, original + "Event 'DoSave'\n  &Value = 2\nEndEvent\n", children));
            Assert.False(WwpActionService.VerifyLayoutEvents(original, original.Replace("= 1", "= 2"), children));
        }

        [Fact]
        public void AddLayoutSaveDiagnostics_ReportsTheSdkValidationMessagesAndInnerExceptions()
        {
            // Shaped like the SDK's ValidationException: the bare "Erro" is its Message, the messages are on Output.
            var validation = new FakeSdkValidationException("Erro", new object[]
            {
                new FakeSdkMessage { ErrorCode = "src0294", Text = "Unknown function 'SetEmpty'" }
            });
            var receipt = new JObject();
            string detail = WwpActionService.AddLayoutSaveDiagnostics(receipt, validation, null, "SamplePanel");
            Assert.True(receipt["sdkDiagnosticsError"] == null && (receipt["sdkMessages"] as JArray)?.Count == 1, receipt.ToString());
            Assert.Contains("src0294", detail);
            Assert.Equal("object-save", receipt["savePhase"].Value<string>());
            Assert.Contains("Unknown function", receipt["sdkMessages"].ToString());

            var inner = new JObject();
            WwpActionService.AddLayoutSaveDiagnostics(inner,
                new System.InvalidOperationException("outer", new System.ArgumentException("inner detail")), null, "SamplePanel");
            Assert.Contains("inner detail", inner["innerMessages"].ToString());
            Assert.Contains("System.ArgumentException", inner["innerExceptionTypes"].ToString());
        }

        // ---- where a failure stopped: stage -> savePhase and code ------------------------------------------------

        [Theory]
        [InlineData(null, "transaction-fresh-read", "WwpLayoutPreSaveFailed")]
        [InlineData("boundary", "transaction-fresh-read", "WwpLayoutPreSaveFailed")]
        [InlineData("create", "native-mutation", "WwpLayoutPreSaveFailed")]
        [InlineData("set:basicLength", "native-mutation", "WwpLayoutPreSaveFailed")]
        [InlineData("attach", "native-mutation", "WwpLayoutPreSaveFailed")]
        [InlineData("save", "object-save", "WwpLayoutSaveFailed")]
        [InlineData("project", "projection", "WwpProjectionFailed")]
        [InlineData("verify", "pre-commit-verification", "WwpLayoutNotVerified")]
        [InlineData("commit", "commit", "WwpLayoutCommitFailed")]
        public void FailureStage_DecidesTheSavePhaseAndTheCodeOfAnUntypedException(string stage, string phase, string code)
        {
            // Only the save stage may say the save failed: saveAttempted is false for every earlier one.
            Assert.Equal(phase, WwpActionService.LayoutStage.Phase(stage));
            Assert.Equal(code, WwpActionService.LayoutStage.FailureCode(stage));
        }

        [Fact]
        public void FailureCode_KeepsATypedCodeAndOtherwiseFollowsTheStageTheReceiptCarries()
        {
            var typed = new JObject { ["failureCode"] = "StaleObject" };
            WwpActionService.LayoutStage.Enter(typed, WwpActionService.LayoutStage.Save);
            Assert.Equal("StaleObject", WwpActionService.LayoutFailureCodeFor(typed, false));
            Assert.Equal("StaleObject", WwpActionService.LayoutFailureCodeFor(typed, true));

            var untyped = new JObject();
            WwpActionService.LayoutStage.Enter(untyped, WwpActionService.LayoutStage.Commit);
            Assert.Equal("commit", WwpActionService.LayoutStage.Of(untyped));
            Assert.Equal("WwpLayoutCommitFailed", WwpActionService.LayoutFailureCodeFor(untyped, false));

            // No stage yet (the transaction never started) is a failure before the save, not a failed verification.
            Assert.Equal("WwpLayoutPreSaveFailed", WwpActionService.LayoutFailureCodeFor(new JObject(), false));
            // A commit that went through and was not confirmed is the post-save verification's refusal.
            Assert.Equal("WwpLayoutNotVerified", WwpActionService.LayoutFailureCodeFor(new JObject(), true));
        }

        [Fact]
        public void AddLayoutSaveDiagnostics_ReportsTheStageTheFailureIsAttributedTo()
        {
            var receipt = new JObject();
            WwpActionService.AddLayoutSaveDiagnostics(receipt, new System.InvalidOperationException("callback failed"), null, "SamplePanel",
                WwpActionService.LayoutStage.Phase("project"));
            Assert.Equal("projection", receipt["savePhase"].Value<string>());

            var unspecified = new JObject();
            WwpActionService.AddLayoutSaveDiagnostics(unspecified, new System.InvalidOperationException("callback failed"), null, "SamplePanel");
            Assert.Equal("object-save", unspecified["savePhase"].Value<string>());
        }

        // ---- what the write amounted to ---------------------------------------------------------------------------

        [Theory]
        //   mutated committed restored confirmed | success persisted known partial dirty
        // verified commit: the baseline differs by design
        [InlineData(true, true, false, true, true, true, true, false, true)]
        // refused before the commit, the rollback restored the baseline: clean, nothing persisted
        [InlineData(true, false, true, null, false, false, true, false, false)]
        // the rollback (or a commit that threw) left a state that is not the baseline: partial persistence
        [InlineData(true, false, false, null, false, null, true, true, true)]
        // the state could not be read back after an attempt: unknown, and dirty until proven otherwise
        [InlineData(true, false, null, null, false, null, false, null, true)]
        // committed, and the post-commit verification refused it
        [InlineData(true, true, false, false, false, null, true, true, true)]
        // committed, and the post-commit verification did not run to the end
        [InlineData(true, true, false, null, false, null, false, true, true)]
        // committed, and neither the comparison nor the verification could run
        [InlineData(true, true, null, null, false, null, false, null, true)]
        // refused at the boundary because somebody else changed the host: this call wrote nothing
        [InlineData(false, false, false, null, false, false, true, false, false)]
        // nothing attempted and nothing readable afterwards: still nothing of this call is in the instance
        [InlineData(false, false, null, null, false, false, true, false, false)]
        public void Outcome_ClassifiesTheWriteFromFactsOnly(bool mutationAttempted, bool committed, bool? restored, bool? confirmed,
            bool success, bool? persisted, bool known, bool? partial, bool dirty)
        {
            var outcome = WwpActionService.ClassifyLayoutOutcome(mutationAttempted, committed, restored, confirmed);
            Assert.Equal(success, outcome.Success);
            Assert.Equal(persisted, outcome.Persisted);
            Assert.Equal(known, outcome.PersistedStateKnown);
            Assert.Equal(partial, outcome.PartialPersistenceDetected);
            Assert.Equal(dirty, outcome.MarkTargetDirty);
        }

        [Fact]
        public void Restoration_IsReportedOnlyAfterARollback()
        {
            var equal = ObjectMoveSnapshot.Comparison.Verified("h1");
            var changed = ObjectMoveSnapshot.Comparison.Failed(new[] { "WebForm" }, "h2");

            // A commit: the state differs from the baseline by design, which is no restoration to report.
            var committed = new JObject();
            Assert.False(WwpActionService.NoteLayoutRestoration(committed, false, false, changed, changed));
            Assert.Null(committed["restorationVerification"]);
            Assert.Null(committed["stateRestoredExactly"]);
            Assert.Null(committed["rollbackVerified"]);

            var rolledBack = new JObject();
            Assert.True(WwpActionService.NoteLayoutRestoration(rolledBack, true, true, equal, equal));
            Assert.True(rolledBack["stateRestoredExactly"].Value<bool>());
            Assert.True(rolledBack["rollbackVerified"].Value<bool>());
            Assert.True(rolledBack["restorationVerification"]["host"]["equal"].Value<bool>());

            // A rollback that threw is not verified, even when the state happens to match.
            var threw = new JObject();
            Assert.True(WwpActionService.NoteLayoutRestoration(threw, true, false, equal, equal));
            Assert.True(threw["stateRestoredExactly"].Value<bool>());
            Assert.False(threw["rollbackVerified"].Value<bool>());

            var moved = new JObject();
            Assert.False(WwpActionService.NoteLayoutRestoration(moved, true, true, equal, changed));
            Assert.False(moved["stateRestoredExactly"].Value<bool>());
            Assert.False(moved["rollbackVerified"].Value<bool>());
            Assert.Equal("WebForm", moved["restorationVerification"]["parent"]["changedParts"][0].Value<string>());
        }

        [Fact]
        public void FirstFailure_IsNotReplacedByTheTransactionFinalizationError()
        {
            Assert.Equal("the cause", WwpActionService.KeepFirstFailure("the cause", "disposal failed"));
            Assert.Equal("disposal failed", WwpActionService.KeepFirstFailure(null, "disposal failed"));
            Assert.Equal("disposal failed", WwpActionService.KeepFirstFailure("", "disposal failed"));
        }

        // ---- the cause of a refused verification ------------------------------------------------------------------

        private static JObject Verification(params string[] failed)
        {
            var result = new JObject
            {
                ["confirmed"] = failed.Length == 0,
                ["patternReReadConfirmed"] = true, ["projection"] = new JObject { ["confirmed"] = true },
                ["unrelatedStatePreserved"] = true, ["eventsPreserved"] = true,
                ["nativeVariableBindingsConfirmed"] = true, ["identityConfirmed"] = true
            };
            foreach (string check in failed)
            {
                if (check == "projection")
                    result["projection"] = new JObject { ["confirmed"] = false, ["failureStage"] = "existingControlOrder" };
                else result[check] = false;
            }
            return result;
        }

        [Fact]
        public void VerificationFailure_NamesTheFailedChecksAndTheProjectionStage()
        {
            var verification = Verification("projection", "eventsPreserved");
            Assert.Equal(new[] { "projection", "eventsPreserved" }, WwpActionService.FailedLayoutChecks(verification));
            Assert.Equal("projection (existingControlOrder), eventsPreserved", WwpActionService.DescribeLayoutVerificationFailure(verification));
            Assert.Equal(" (failed checks: projection (existingControlOrder), eventsPreserved)", WwpActionService.LayoutFailedChecksSuffix(verification));

            Assert.Empty(WwpActionService.FailedLayoutChecks(Verification()));
            Assert.Equal(string.Empty, WwpActionService.LayoutFailedChecksSuffix(Verification()));
            Assert.Empty(WwpActionService.FailedLayoutChecks(null));

            // A projection that could not even be parsed says so by its code.
            var schema = Verification();
            schema["projection"] = new JObject { ["confirmed"] = false, ["code"] = "WwpProjectionSchemaUnsupported" };
            Assert.Equal("projection (WwpProjectionSchemaUnsupported)", WwpActionService.DescribeLayoutVerificationFailure(schema));
        }

        [Fact]
        public void VerificationFailure_ReceiptKeepsOnlyTwoShortFieldsAndTheMessageNamesTheCauseOnItsFirstLine()
        {
            var receipt = new JObject { ["unrelated"] = 1 };
            var verification = Verification("patternReReadConfirmed", "projection");
            verification["projection"]["hash"] = "0123456789abcdef";
            WwpActionService.NoteLayoutVerificationFailure(receipt, verification);
            Assert.Equal(new[] { "patternReReadConfirmed", "projection" }, receipt["failedChecks"].Values<string>());
            Assert.Equal("existingControlOrder", receipt["failureStage"].Value<string>());
            Assert.Equal(3, receipt.Properties().Count());

            string post = WwpActionService.LayoutPostSaveFailureMessage(Verification("unrelatedStatePreserved"));
            Assert.StartsWith("Post-save verification failed (failed checks: unrelatedStatePreserved).", post);
            Assert.DoesNotContain("\n", post);
            Assert.DoesNotContain("0123456789abcdef", WwpActionService.LayoutFailedChecksSuffix(verification));
            Assert.Equal("Post-save verification failed. Inspect saved state before retrying; no compensating write was attempted.",
                WwpActionService.LayoutPostSaveFailureMessage(null));

            // VerifyLayoutState reports the identity check by name, so a mismatch is attributable.
            Assert.Contains("identityConfirmed", WwpActionService.DescribeLayoutVerificationFailure(Verification("identityConfirmed")));
        }

        // ---- arguments the action does not honour ------------------------------------------------------------------

        [Theory]
        [InlineData("{position:0}", "position")]
        [InlineData("{before:'Fields'}", "before")]
        [InlineData("{after:'Fields'}", "after")]
        [InlineData("{position:1,before:'A',after:'B'}", "position, before, after")]
        [InlineData("{position:null,tablePath:'TableContent'}", null)]
        [InlineData("{tablePath:'TableContent'}", null)]
        public void Placement_IsRefusedNotIgnored(string json, string expected)
        {
            Assert.Equal(expected, WwpActionService.UnsupportedLayoutPlacement(JObject.Parse(json)));
        }

        // ---- no raw exception text in the receipt ------------------------------------------------------------------

        private const string LocalPath = "C:\\Users\\Someone\\kb\\index.db";

        [Fact]
        public void ReceiptErrorText_IsSanitized()
        {
            string text = WwpActionService.LayoutErrorText(new System.InvalidOperationException("cannot open " + LocalPath));
            Assert.DoesNotContain("Someone", text);
            Assert.Contains("<path>", text);
            Assert.Equal(string.Empty, WwpActionService.LayoutErrorText(null));
        }

        [Fact]
        public void CacheInvalidationFailures_AreSanitizedInTheReceipt()
        {
            var receipt = new JObject();
            WwpActionService.FinalizeLayoutReadCaches(true, receipt,
                () => throw new System.InvalidOperationException("index at " + LocalPath),
                () => throw new System.InvalidOperationException("cache at " + LocalPath));
            Assert.DoesNotContain("Someone", receipt.ToString());
            Assert.Contains("<path>", receipt["readCacheInvalidationError"].Value<string>());
            Assert.Contains("<path>", receipt["managedReadCacheInvalidationError"].Value<string>());
        }

        [Fact]
        public void ProjectionAndSnapshotFailureTexts_AreSanitizedInTheReceipt()
        {
            var projection = new WwpProjectionHelper.ProjectionResult { Failure = "GetBuildProcess() threw: IOException: missing " + LocalPath };
            Assert.DoesNotContain("Someone", WwpActionService.LayoutProjectionReceipt(projection).ToString());

            var comparison = ObjectMoveSnapshot.Comparison.Failed(new[] { "SnapshotReadFailed: cannot read " + LocalPath, "WebForm" }, null);
            JObject receipt = WwpActionService.LayoutComparisonReceipt(comparison);
            Assert.DoesNotContain("Someone", receipt.ToString());
            Assert.Equal("WebForm", receipt["changedParts"][1].Value<string>());
        }

        // The save, the SDK and the transaction cannot run in a unit test, so the wiring is pinned in the source:
        // every stage is entered, every failure reads its phase and code from the stage, the outcome decides what
        // the receipt claims and whether the target is marked dirty, and no exception message goes in raw.
        [Fact]
        public void RunLayoutOperation_IsWiredToTheStageAndOutcomeFunctions()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WwpActionService.Layout.cs");
            string body = SourceAssert.MethodBody(source, "private string RunLayoutOperation(string target, KBObject instance, JObject args)");
            foreach (string required in new[]
            {
                "UnsupportedLayoutPlacement(args)",
                "LayoutStage.Enter(receipt, LayoutStage.Boundary);", "LayoutStage.Enter(receipt, LayoutStage.Save);",
                "LayoutStage.Enter(receipt, LayoutStage.Project);", "LayoutStage.Enter(receipt, LayoutStage.Verify);",
                "LayoutStage.Enter(receipt, LayoutStage.Commit);",
                "LayoutStage.Phase(failedStage)", "LayoutStage.FailureCode(failedStage)",
                "failure = KeepFirstFailure(failure, finalization);",
                "NoteLayoutRestoration(receipt, rollbackAttempted, rolledBack,",
                "ClassifyLayoutOutcome(mutationAttempted, committed, restored, confirmed)",
                "if (outcome.MarkTargetDirty) WriteService.NotePerTargetWrite(target);",
                "LayoutFailureCodeFor(receipt, committed)",
                "NoteLayoutVerificationFailure(receipt, verification);",
                "NoteLayoutVerificationFailure(receipt, postSaveVerification);",
                "LayoutFailedChecksSuffix(verification)",
                "message: committed ? LayoutPostSaveFailureMessage(postSaveVerification)",
                "receipt[\"savePhase\"] = IsolatedEventsSavePhase.PostCommitVerification;",
                "rollbackAttempted = true;",
                "receipt[\"persisted\"] = outcome.Persisted.HasValue", "receipt[\"persistedStateKnown\"] = outcome.PersistedStateKnown;",
                "outcome.PartialPersistenceDetected.HasValue", "if (outcome.Success)",
                "\"WwpLayoutPlacementUnsupported\""
            })
                Assert.True(body.Contains(required), "RunLayoutOperation no longer contains: " + required);

            // The restoration claims live only in the helper that checks for a rollback.
            Assert.DoesNotContain("\"stateRestoredExactly\"", body);
            Assert.DoesNotContain("\"restorationVerification\"", body);
            // The dirty mark is the outcome's decision, never the success branch's alone.
            Assert.Equal(1, SourceAssert.Count(body, "NotePerTargetWrite("));
            // A receipt field never takes an exception message unsanitized.
            Assert.False(Regex.IsMatch(source, @"receipt\[""[A-Za-z]+""\]\s*=\s*\w+\.Message"),
                "a receipt field takes an exception message without LayoutErrorText");
        }

        // A failed verification names the checks it did not confirm, so every check named must be one the
        // verification reports: a name it never emits would read as failed on every refusal.
        [Fact]
        public void VerifyLayoutState_ReportsEveryCheckTheFailureVocabularyNames()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WwpActionService.Layout.cs");
            string body = SourceAssert.MethodBody(source, "private JObject VerifyLayoutState(");
            foreach (string check in WwpActionService.LayoutVerificationChecks)
                Assert.True(body.Contains("[\"" + check + "\"]"), "VerifyLayoutState does not report the check '" + check + "'");
        }

        internal sealed class FakeSdkValidationException : System.Exception
        {
            public FakeSdkValidationException(string message, object[] output) : base(message) { Output = output; }
            public object[] Output { get; }
        }

        internal sealed class FakeSdkMessage
        {
            public string ErrorCode { get; set; }
            public string Text { get; set; }
        }
    }
}
