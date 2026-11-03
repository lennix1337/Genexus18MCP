using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #830: what a refusal says about a control must send the caller to something that works. A hint that
    /// recommended <c>projection=minimal</c> for a control produced an empty list; a control id was refused without
    /// saying which spellings are accepted or why an id read a moment ago no longer resolved.
    /// </summary>
    public sealed class ControlPropertyHintsTests
    {
        private const string Guid = "9c1f2a3b-4d5e-4f60-8a7b-0c1d2e3f4a5b";

        private static JObject GridProps(params string[] names) => new JObject
        {
            ["name"] = "PainelLista",
            ["control"] = "GridProva",
            ["properties"] = new JArray(names.Select(n => new JObject { ["name"] = n, ["value"] = "", ["type"] = "System.String", ["readOnly"] = false }))
        };

        private static string Source(params string[] relative)
            => System.IO.File.ReadAllText(System.IO.Path.Combine(
                new[] { TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker" }.Concat(relative).ToArray()));

        // ------------------------------------------------------------------------ control references

        [Theory]
        [InlineData("GridProva")]
        [InlineData("att:12")]
        [InlineData("&Filtro")]
        public void ControlNotFoundHint_ListsTheAcceptedSpellings(string control)
        {
            string hint = ControlPropertyHints.ControlNotFoundHint(control);

            Assert.Contains("genexus_inspect include=controls", hint);
            Assert.Contains("control name", hint);
            Assert.Contains("att:<id>", hint);
            Assert.Contains("GUID id", hint);
            Assert.Contains("<element>:<guid> with the control's own element", hint);
            Assert.Contains("&Variable", hint);
            Assert.DoesNotContain("WorkWithPlus", hint);
        }

        [Theory]
        [InlineData(Guid)]
        [InlineData("grid:" + Guid)]
        public void ControlNotFoundHint_ForAnIdAlsoSaysThatPatternGeneratedIdsChangeOnEveryRead(string control)
        {
            string hint = ControlPropertyHints.ControlNotFoundHint(control);

            Assert.Contains("WorkWithPlus", hint);
            Assert.Contains("change on every read", hint);
            Assert.Contains("by name", hint);
        }

        [Fact]
        public void ControlNotFoundHint_OfNothing_IsTheGeneralOne()
        {
            Assert.DoesNotContain("WorkWithPlus", ControlPropertyHints.ControlNotFoundHint(null));
        }

        [Fact]
        public void Wiring_EveryControlLookupInThePropertyServiceAnswersWithTheSharedHint()
        {
            string source = Source("Services", "PropertyService.cs");

            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, @"return ControlNotFoundError\(obj, target, controlName\);").Count);
            Assert.Contains("hint: ControlPropertyHints.ControlNotFoundHint(controlName)", source);
            Assert.DoesNotContain("Use genexus_inspect to list controls available", source);
        }

        [Fact]
        public void Wiring_TheTypedRoutesAnswerAMissingControlWithTheSharedHint()
        {
            string source = Source("Services", "ControlParametersService.cs");

            Assert.Contains("hint: ControlPropertyHints.ControlNotFoundHint(control),", source);
            Assert.DoesNotContain("pass the attribute name, att:<id>, the control name or the control GUID.", source);
        }

        // ------------------------------------------------------------------------------ the Conditions names

        [Theory]
        [InlineData("Condition")]
        [InlineData("conditions")]
        [InlineData("WHERE")]
        [InlineData("ControlWhere")]
        [InlineData("grid_condition")]
        [InlineData("Grid Where")]
        public void TheNamesACallerTriesForAGridsConditionsAreRecognised(string name)
        {
            Assert.True(ControlPropertyHints.IsConditionsName(name));
        }

        [Theory]
        [InlineData("MaxRows")]
        [InlineData("collection")]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public void AnythingElseIsNotAConditionsName(string name)
        {
            Assert.False(ControlPropertyHints.IsConditionsName(name));
        }

        [Fact]
        public void TheControlWhereHint_SaysWhereTheConditionsAreAndWhichSpellingsReachThem()
        {
            string hint = ControlPropertyHints.ControlWhereHint("GridProva");

            Assert.Contains("its ControlWhere property", hint);
            Assert.Contains("Conditions and Condition reach it too", hint);
            Assert.Contains("Where, propertyNames[] and query do not", hint);
            Assert.Contains("genexus_properties action=get control=GridProva propertyName=ControlWhere", hint);
            Assert.Contains("expectedVersion=<versionToken>", hint);
            Assert.Contains("rollbackOnFailure=true", hint);
            Assert.Contains("A <condition> child or attribute in the WebForm XML is not part of the layout format", hint);
        }

        [Fact]
        public void TheControlWhereHint_PromisesNothingAboutTheConditionsPart()
        {
            string hint = ControlPropertyHints.ControlWhereHint("GridProva");

            Assert.DoesNotContain("part=Conditions", hint);
            Assert.DoesNotContain("classic", hint);
        }

        // --------------------------------------------------------- what the refusal tells a caller to do next

        [Fact]
        public void PropertyNotFoundOnAControl_PointsToTheControlsFullListing_NotToAProjection()
        {
            var error = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridProps("collection", "controlName"), target: "PainelLista", controlName: "GridProva", propertyName: "Bogus"))["error"];

            string hint = (string)error["hint"];
            Assert.Contains("every property of the control", hint);
            Assert.Contains("no projection", hint);
            var steps = ((JArray)error["nextSteps"]).Cast<JObject>().ToList();
            var listing = steps.Last();
            Assert.Equal("genexus_properties", (string)listing["tool"]);
            Assert.Equal("GridProva", (string)listing["args"]["control"]);
            Assert.Null(listing["args"]["projection"]);
        }

        [Fact]
        public void PropertyNotFoundOnAnObject_KeepsTheMinimalProjectionAdvice()
        {
            var error = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridProps("Description", "Name"), target: "Customer", propertyName: "Bogus"))["error"];

            Assert.Contains("projection=minimal", (string)error["hint"]);
            var listing = ((JArray)error["nextSteps"]).Cast<JObject>().Last();
            Assert.Equal("minimal", (string)listing["args"]["projection"]);
            Assert.Null(listing["args"]["control"]);
        }

        [Fact]
        public void QueryAndMultiNotFoundOnAControl_AlsoPointToTheFullListing()
        {
            var query = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridProps("collection"), target: "PainelLista", controlName: "GridProva", query: "Bogus"))["error"];
            var multi = JObject.Parse(PropertyService.ShapeGetPropertiesResult(
                GridProps("collection"), target: "PainelLista", controlName: "GridProva", propertyNames: new[] { "Bogus", "Other" }))["error"];

            foreach (var error in new[] { query, multi })
            {
                Assert.Contains("every property of the control", (string)error["hint"]);
                Assert.Null(((JArray)error["nextSteps"]).Cast<JObject>().Last()["args"]["projection"]);
            }
        }

        // ------------------------------------------------------------------ the commit meaning of a failed visual write

        [Fact]
        public void VisualHint_IsAppendedOnceAndKeepsTheHintThatWasThere()
        {
            var error = new JObject { ["hint"] = "See 'verifyDiff'." };
            string meaning = WriteService.VisualCommitMeaningHint;

            WriteService.AppendVisualHint(error, meaning);
            WriteService.AppendVisualHint(error, meaning);

            string hint = (string)error["hint"];
            Assert.StartsWith("See 'verifyDiff'. saved and persisted report", hint);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(hint, "persistedVerified=false"));
        }

        [Fact]
        public void VisualHint_OnAnEnvelopeWithoutOne_BecomesTheHint()
        {
            var error = new JObject();

            WriteService.AppendVisualHint(error, "text");
            WriteService.AppendVisualHint(null, "ignored");
            WriteService.AppendVisualHint(error, null);

            Assert.Equal("text", (string)error["hint"]);
        }

        [Fact]
        public void CommitMeaningHint_SaysWhatSavedAndPersistedMean_AndPromisesNothingAboutVerifyDiff()
        {
            string hint = WriteService.VisualCommitMeaningHint;

            Assert.Contains("saved and persisted", hint);
            Assert.Contains("committed the layout it kept", hint);
            Assert.Contains("persistedVerified=false", hint);
            // A difference of an attribute value or of text leaves verifyDiff without a rejected list: the hint before this one
            // (BuildSuggestion) is the one that points there.
            Assert.DoesNotContain("verifyDiff", hint);
        }

        [Fact]
        public void VerificationFailureSuggestion_SpeaksOfChildrenWhenOnlyChildrenDiffer()
        {
            string children = WriteService.BuildVerificationFailureSuggestion(new XmlEquivalenceDiff { RejectedChildren = new[] { "condition" } });

            Assert.Contains("rejectedChildren", children);
            Assert.Contains("child elements that its schema defines", children);
            Assert.DoesNotContain("Theme class", children);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void VerificationFailureSuggestion_KeepsTheStandingAttributeAdviceOtherwise(bool withChildren, bool withAttributes)
        {
            var diff = new XmlEquivalenceDiff
            {
                RejectedChildren = withChildren ? new[] { "condition" } : null,
                RejectedAttributes = withAttributes ? new[] { "style" } : null
            };

            string suggestion = WriteService.BuildVerificationFailureSuggestion(diff);

            Assert.Contains("rejected/added attrs", suggestion);
            Assert.Contains("Theme class", suggestion);
        }

        [Fact]
        public void VerificationFailureSuggestion_WithoutADiffIsTheStandingAttributeAdvice()
        {
            Assert.Contains("Theme class", WriteService.BuildVerificationFailureSuggestion(null));
        }

        [Fact]
        public void GotchaGuidance_IsTheMessageAndTheWorkaroundOfTheFirstGotchaWithThatCode()
        {
            var gotchas = new JArray
            {
                new JObject { ["code"] = "GotchaDuplicateControlName", ["message"] = "other", ["workaround"] = "other fix" },
                new JObject { ["code"] = GotchaCodes.GotchaGridConditionIgnored, ["message"] = "A <condition> child is dropped.", ["workaround"] = "Use ControlWhere." },
                new JObject { ["code"] = GotchaCodes.GotchaGridConditionIgnored, ["message"] = "second", ["workaround"] = "second fix" }
            };

            Assert.Equal("A <condition> child is dropped. Use ControlWhere.",
                WriteService.GotchaGuidance(gotchas, GotchaCodes.GotchaGridConditionIgnored));
        }

        [Fact]
        public void GotchaGuidance_IsNullWithoutAGotchaOfThatCode()
        {
            var gotchas = new JArray { new JObject { ["code"] = "GotchaDuplicateControlName", ["message"] = "m", ["workaround"] = "w" } };

            Assert.Null(WriteService.GotchaGuidance(gotchas, GotchaCodes.GotchaGridConditionIgnored));
            Assert.Null(WriteService.GotchaGuidance(new JArray(), GotchaCodes.GotchaGridConditionIgnored));
            Assert.Null(WriteService.GotchaGuidance(null, GotchaCodes.GotchaGridConditionIgnored));
            Assert.Null(WriteService.GotchaGuidance(
                new JArray { new JObject { ["code"] = GotchaCodes.GotchaGridConditionIgnored } }, GotchaCodes.GotchaGridConditionIgnored));
        }

        // -------------------------------------------- what the verification failure adds to its hint, as a whole

        private static JArray GridGotcha() => new JArray
        {
            new JObject { ["code"] = GotchaCodes.GotchaGridConditionIgnored, ["message"] = "A <condition> child is dropped.", ["workaround"] = "Use ControlWhere." }
        };

        [Fact]
        public void TheGuidanceAfterACommit_ExplainsSavedAndPersistedThenTheGotcha()
        {
            var error = new JObject { ["hint"] = "See 'verifyDiff'." };

            WriteService.AppendVisualVerificationGuidance(error, true, GridGotcha());

            string hint = (string)error["hint"];
            int meaning = hint.IndexOf("saved and persisted report", System.StringComparison.Ordinal);
            int gotcha = hint.IndexOf("A <condition> child is dropped. Use ControlWhere.", System.StringComparison.Ordinal);
            Assert.True(meaning > 0, hint);
            Assert.True(gotcha > meaning, hint);
        }

        [Fact]
        public void TheGuidanceWithoutACommit_HasNoSavedAndPersistedExplanation_ButKeepsTheGotcha()
        {
            var error = new JObject();

            WriteService.AppendVisualVerificationGuidance(error, false, GridGotcha());

            string hint = (string)error["hint"];
            Assert.DoesNotContain("saved and persisted", hint);
            Assert.Equal("A <condition> child is dropped. Use ControlWhere.", hint);
        }

        [Fact]
        public void TheGuidanceWithoutAGotcha_IsTheMeaningAlone_AndIsAppendedOnlyOnce()
        {
            var error = new JObject();

            WriteService.AppendVisualVerificationGuidance(error, true, null);
            WriteService.AppendVisualVerificationGuidance(error, true, new JArray());

            Assert.Equal(WriteService.VisualCommitMeaningHint, (string)error["hint"]);
        }

        // ----------------------------------------------- the failure of a visual write, as the caller receives it

        private static string VerificationFailure() => new JObject
        {
            ["status"] = "error",
            ["error"] = new JObject { ["code"] = "WriteFailed", ["message"] = "Visual write verification failed", ["hint"] = "See 'verifyDiff'." },
            ["verifyDiff"] = new JObject()
        }.ToString();

        [Fact]
        public void TheDecoratedFailureAfterACommit_CarriesTheCommitEvidence_TheHint_TheRestoreStepAndTheRollbackRequest()
        {
            var json = JObject.Parse(WriteService.DecorateVisualVerificationFailure(
                VerificationFailure(), null, null, true, false, GridGotcha(), "PainelLista", "WebForm"));
            var error = (JObject)json["error"];

            foreach (var level in new[] { json, error })
            {
                Assert.True((bool)level["saved"]);
                Assert.True((bool)level["persisted"]);
                Assert.False((bool)level["persistedVerified"]);
            }
            string hint = (string)error["hint"];
            Assert.StartsWith("See 'verifyDiff'.", hint);
            Assert.Contains("saved and persisted report", hint);
            Assert.Contains("A <condition> child is dropped. Use ControlWhere.", hint);
            Assert.False((bool)json["rollbackRequested"]);
            var restore = ((JArray)error["nextSteps"]).Cast<JObject>().Single();
            Assert.Equal("genexus_versioning", (string)restore["tool"]);
            Assert.Equal("PainelLista", (string)restore["args"]["name"]);
        }

        [Fact]
        public void TheDecoratedFailureWithoutACommit_HasNoCommitEvidenceAndNoSavedAndPersistedExplanation_ButKeepsTheGotcha()
        {
            var json = JObject.Parse(WriteService.DecorateVisualVerificationFailure(
                VerificationFailure(), null, null, false, true, GridGotcha(), "PainelLista", "WebForm"));
            var error = (JObject)json["error"];

            Assert.Null(json["saved"]);
            Assert.Null(error["persisted"]);
            string hint = (string)error["hint"];
            Assert.DoesNotContain("saved and persisted", hint);
            Assert.Contains("A <condition> child is dropped. Use ControlWhere.", hint);
            Assert.True((bool)json["rollbackRequested"]);
        }

        [Fact]
        public void TheDecoratedFailure_CarriesTheSdkSaveErrorWhenThereWasOne()
        {
            var saveError = new JObject { ["type"] = "Exception", ["message"] = "The SDK refused" };

            var json = JObject.Parse(WriteService.DecorateVisualVerificationFailure(
                VerificationFailure(), saveError, null, true, false, null, "PainelLista", "WebForm"));
            var error = (JObject)json["error"];

            Assert.True((bool)error["sdkSaveRejected"]);
            Assert.Equal("The SDK refused", (string)error["sdkSaveError"]["message"]);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("")]
        public void AFailureThatCannotBeReadIsReturnedUntouched(string failure)
        {
            Assert.Equal(failure, WriteService.DecorateVisualVerificationFailure(failure, null, null, true, false, GridGotcha(), "T", "WebForm"));
        }

        [Fact]
        public void Wiring_TheVerificationFailureOfAVisualWriteIsDecoratedByTheSharedFunction()
        {
            string source = Source("Services", "WriteService.VisualWrite.cs");

            Assert.Contains("return DecorateVisualVerificationFailure(visualVerifyErr, webFormPartSaveError, forceSaveError,", source);
            Assert.Contains("sdkCommitCompleted, rollbackOnFailure, prospectiveGotchas, target, partName);", source);
        }
    }
}
