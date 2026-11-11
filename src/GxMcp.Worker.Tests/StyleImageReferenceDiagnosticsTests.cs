using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Common.Diagnostics;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The SDK refuses a style source whose gx-image() points at an Image object that is not in the Knowledge Base, and
    /// it refuses it without a word: the public validation keeps its reason to itself. These tests pin what the helper
    /// does after such a refusal - it names the unresolved references, passes on the SDK's own messages when it can get
    /// them, blames the images only when the SDK confirms it, and keeps the old answer when it can say nothing more.
    /// The SDK texts used below are the ones GeneXus 18 U16 answers (live capture, synthetic names): "&lt;Name&gt; not found"
    /// for a missing image, and ANTLR's "mismatched input ... expecting ..." / "extraneous input ..." for a source with a
    /// syntax error, which it reports before it looks at any reference.
    /// </summary>
    public class StyleImageReferenceDiagnosticsTests
    {
        private const string SdkOnlyWords = "The GeneXus SDK rejected the StyleSheet source during validation.";

        private static string Styles(params string[] bodies) =>
            "styles Brand {\n" + string.Concat(bodies.Select((b, i) => "    .C" + i + " {\n        " + b + "\n    }\n")) + "}\n";

        // ---- extraction -----------------------------------------------------------------------------------------

        [Fact]
        public void ExtractImageNames_ReadsBareAndQuotedNamesOncePerNameInOrder()
        {
            string css = Styles(
                "background-image: gx-image(SynthLogo);",
                "background-image: gx-image(\"SynthWallpaper\");",
                "background-image: gx-image( synthlogo );",
                "background-image: GX-IMAGE('Spinner');");

            Assert.Equal(new[] { "SynthLogo", "SynthWallpaper", "Spinner" }, DesignSystemSourceParser.ExtractImageNames(css));
        }

        [Fact]
        public void ExtractImageNames_IgnoresReferencesInsideComments()
        {
            string css = "styles Brand {\n    /* background-image: gx-image(Commented); */\n    // gx-image(AlsoCommented)\n"
                + "    .C0 {\n        background-image: gx-image(Real);\n    }\n}\n";

            Assert.Equal(new[] { "Real" }, DesignSystemSourceParser.ExtractImageNames(css));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("styles Brand { .C0 { color: red; } }")]
        public void ExtractImageNames_HasNothingToSayAboutASourceWithoutReferences(string css)
        {
            Assert.Empty(DesignSystemSourceParser.ExtractImageNames(css));
        }

        [Fact]
        public void DesignSystemSourceParser_StillListsImagesInTheParseResult()
        {
            var parsed = DesignSystemSourceParser.Parse(null, Styles("background-image: gx-image(Logo);", "background-image: gx-image(Logo);"));

            Assert.Equal(new[] { "Logo" }, parsed.Images);
        }

        // ---- which references are unresolved ----------------------------------------------------------------------

        [Fact]
        public void FindUnresolvedImageReferences_ListsOnlyTheNamesTheProbeSaysAreMissing()
        {
            string css = Styles("background-image: gx-image(Here);", "background-image: gx-image(Gone);", "background-image: gx-image(Unknown);", "background-image: gx-image(AlsoGone);");
            Func<string, bool?> probe = name =>
            {
                if (name == "Here") return true;
                if (name == "Unknown") return null;
                return false;
            };

            Assert.Equal(new[] { "Gone", "AlsoGone" }, ThemeStyleEditHelper.FindUnresolvedImageReferences(css, probe));
        }

        [Fact]
        public void FindUnresolvedImageReferences_TreatsAProbeThatThrowsAsUnknown()
        {
            string css = Styles("background-image: gx-image(Boom);");

            Assert.Empty(ThemeStyleEditHelper.FindUnresolvedImageReferences(css, name => throw new InvalidOperationException("lookup failed")));
        }

        [Fact]
        public void FindUnresolvedImageReferences_WithoutAProbeNamesNothing()
        {
            Assert.Empty(ThemeStyleEditHelper.FindUnresolvedImageReferences(Styles("background-image: gx-image(Gone);"), null));
        }

        // ---- the refusal of a design-system style sheet, the SDK confirming the missing image -----------------------

        [Fact]
        public void TryApply_WhenTheSdkSaysAnImageIsNotThere_NamesEveryMissingImageAsTheCause()
        {
            // The SDK names only the first missing image; the source has one more.
            var part = new TalkativeDesignStylesPart { Source = "previous source", Messages = new[] { "SynthMissing not found" } };
            string css = Styles("background-image: gx-image(Present);", "background-image: gx-image(SynthMissing);", "background-image: gx-image(\"SynthAlsoMissing\");");

            Assert.False(ThemeStyleEditHelper.TryApply(part, css, out JObject details, out string error, name => name == "Present"));

            Assert.Equal(new[] { "SynthMissing", "SynthAlsoMissing" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Contains("2 Image objects that do not exist", error);
            Assert.Contains("SynthMissing, SynthAlsoMissing", error);
            Assert.DoesNotContain("Present", error);
            Assert.Contains(SdkOnlyWords, error);
            Assert.Contains("SDK messages: SynthMissing not found", error);
            Assert.Equal("unresolvedImageReferences", (string)details["rejectionCause"]);
            Assert.Equal("ThemeStyleUnresolvedReferences", ThemeStyleEditHelper.FailureCode(details));
            Assert.Single(details["sdkMessages"]);
        }

        [Fact]
        public void TryApply_WhenTheSdkSaysAnImageIsNotThere_SpeaksInTheSingularForOneImage()
        {
            var part = new TalkativeDesignStylesPart { Messages = new[] { "SynthMissing not found" } };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(SynthMissing);"),
                out JObject details, out string error, name => false));

            Assert.Contains("1 Image object that does not exist", error);
            Assert.Contains("this Image object", error);
            Assert.Equal(new[] { "SynthMissing" }, details["unresolvedImageReferences"].Select(t => (string)t));
        }

        [Fact]
        public void TryApply_OneSdkMessageThatNamesAMissingImageIsEnoughToBlameTheImages_WhateverTheCase()
        {
            var part = new TalkativeDesignStylesPart { Messages = new[] { "unrelated warning", "synthgone NOT FOUND" } };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(SynthGone);"), out JObject details, out string error, name => false));

            Assert.Equal("ThemeStyleUnresolvedReferences", ThemeStyleEditHelper.FailureCode(details));
            Assert.Equal("unresolvedImageReferences", (string)details["rejectionCause"]);
        }

        [Fact]
        public void TryApply_ASourceThatReachedTheSdkIsNeverWrittenToThePart()
        {
            var part = new TalkativeDesignStylesPart { Source = "previous source", Messages = new[] { "SynthMissing not found" } };
            string css = Styles("background-image: gx-image(SynthMissing);");
            int writesBefore = part.SourceWrites;

            Assert.False(ThemeStyleEditHelper.TryApply(part, css, out _, out _, name => false));

            // The refused text is validated where it is: the part's own Source is neither replaced nor touched.
            Assert.Equal(css, part.ValidatedSource);
            Assert.Equal("previous source", part.Source);
            Assert.Equal(writesBefore, part.SourceWrites);
        }

        // ---- the missing images are listed, not blamed ---------------------------------------------------------------

        [Fact]
        public void TryApply_WhenTheSdkSaysSomethingElse_TheMissingImagesAreListedButNotBlamed()
        {
            // The SDK reports a syntax error before it looks at any reference: the missing image is a second problem, not
            // the reason for this refusal, so the generic code stays and the caller is not sent to create an image that
            // would not make the write pass.
            var part = new TalkativeDesignStylesPart { Messages = new[] { "mismatched input '\"Quoted\"' expecting {Identifier}", "extraneous input ')' expecting ';'" } };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(\"Quoted\");", "background-image: gx-image(Gone);"),
                out JObject details, out string error, name => false));

            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
            Assert.Null(details["rejectionCause"]);
            Assert.Equal(new[] { "Quoted", "Gone" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Equal(2, details["sdkMessages"].Count());
            Assert.Contains("SDK messages: mismatched input", error);
            Assert.Contains("The source also refers to 2 Image objects that do not exist in this Knowledge Base: Quoted, Gone.", error);
            Assert.DoesNotContain("Create or import", error);
        }

        [Fact]
        public void TryApply_AMessageThatDoesNotMentionTheMissingImageDoesNotConfirmIt()
        {
            var part = new TalkativeDesignStylesPart { Messages = new[] { "The class 'Ghost' does not exists" } };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(SynthGone);"), out JObject details, out string error, name => false));

            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
            Assert.Null(details["rejectionCause"]);
            Assert.Equal(new[] { "SynthGone" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Contains("SDK messages: The class 'Ghost' does not exists", error);
            Assert.Contains("The source also refers to 1 Image object that does not exist in this Knowledge Base: SynthGone.", error);
        }

        [Fact]
        public void TryApply_ANameThatOnlyOccursInsideALongerOneDoesNotConfirmTheMissingImage()
        {
            // "SynthLogo" is missing; the SDK complains about "SynthLogoOld", which is another name.
            var part = new TalkativeDesignStylesPart { Messages = new[] { "SynthLogoOld not found", "The class 'SynthLogo2' does not exists" } };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(SynthLogo);"), out JObject details, out _, name => false));

            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
            Assert.Equal(new[] { "SynthLogo" }, details["unresolvedImageReferences"].Select(t => (string)t));
        }

        [Fact]
        public void TryApply_APartThatHasNothingToAskAddsNoMessagesAndListsTheImages()
        {
            AssertListedNotBlamed(new SilentDesignStylesPart { Source = "previous source" });
        }

        [Fact]
        public void TryApply_AValidationThatSaysNothingListsTheImagesAndBlamesNobody()
        {
            AssertListedNotBlamed(new MuteDesignStylesPart { Source = "previous source" });
        }

        [Fact]
        public void TryApply_AValidationThatThrowsIsSwallowedAndListsTheImagesAndBlamesNobody()
        {
            AssertListedNotBlamed(new ThrowingDesignStylesPart { Source = "previous source" });
        }

        [Fact]
        public void TryApply_APartThatCannotSupplyItsInheritedClassesListsTheImagesAndBlamesNobody()
        {
            AssertListedNotBlamed(new UnreadableDesignStylesPart { Source = "previous source" });
        }

        private static void AssertListedNotBlamed(object part)
        {
            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(SynthGone);", "background-image: gx-image(SynthGoneToo);"),
                out JObject details, out string error, name => false));

            Assert.Null(details["sdkMessages"]);
            Assert.Null(details["rejectionCause"]);
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
            Assert.Equal(new[] { "SynthGone", "SynthGoneToo" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Equal(SdkOnlyWords + " The source also refers to 2 Image objects that do not exist in this Knowledge Base: SynthGone, SynthGoneToo.", error);
            Assert.Equal("previous source", ((IHasSource)part).Source);
        }

        // ---- the refusal says what it can, and nothing more ---------------------------------------------------------

        [Fact]
        public void TryApply_WhenEveryReferencedImageExists_KeepsTheSdkWordsAndTheGenericCode()
        {
            Assert.False(ThemeStyleEditHelper.TryApply(new SilentDesignStylesPart(), Styles("background-image: gx-image(Present);"),
                out JObject details, out string error, name => true));

            Assert.Equal(SdkOnlyWords, error);
            Assert.Null(details["unresolvedImageReferences"]);
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_WithoutAnImageProbe_KeepsTheSdkWordsAndTheGenericCode()
        {
            Assert.False(ThemeStyleEditHelper.TryApply(new SilentDesignStylesPart(), Styles("background-image: gx-image(Missing);"),
                out JObject details, out string error));

            Assert.Equal(SdkOnlyWords, error);
            Assert.Null(details["unresolvedImageReferences"]);
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_AReferenceOnlyInsideACommentIsNotReportedAsUnresolved()
        {
            string css = "styles Brand {\n    /* gx-image(Commented) */\n    .C0 {\n        color: red;\n    }\n}\n";

            Assert.False(ThemeStyleEditHelper.TryApply(new SilentDesignStylesPart(), css, out JObject details, out string error, name => false));

            Assert.Equal(SdkOnlyWords, error);
            Assert.Null(details["unresolvedImageReferences"]);
        }

        [Fact]
        public void TryApply_AnAcceptedSourceNeverAsksWhetherTheImagesExist()
        {
            int probes = 0;
            var part = new AcceptingDesignStylesPart();

            Assert.True(ThemeStyleEditHelper.TryApply(part, Styles("background-image: gx-image(Missing);"), out JObject details, out string error,
                name => { probes++; return false; }));

            Assert.Equal(0, probes);
            Assert.Null(details["unresolvedImageReferences"]);
            Assert.Equal(1, part.SourceWrites);
        }

        // ---- the SDK's own words ---------------------------------------------------------------------------------

        [Fact]
        public void TryApply_PassesOnWhatTheSdkSaysAboutTheRefusedSource()
        {
            var part = new TalkativeDesignStylesPart { Source = "previous source" };
            string css = Styles("color: red;");

            Assert.False(ThemeStyleEditHelper.TryApply(part, css, out JObject details, out string error));

            Assert.Contains(SdkOnlyWords, error);
            Assert.Contains("SDK messages: The class 'Ghost' does not exists", error);
            var message = Assert.Single(details["sdkMessages"]);
            Assert.Equal("The class 'Ghost' does not exists", (string)message["text"]);
            Assert.Equal("Error", (string)message["level"]);
            Assert.Equal(css, part.ValidatedSource);
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_SummarizesManySdkMessagesAndKeepsThemAll()
        {
            var part = new TalkativeDesignStylesPart { Messages = Enumerable.Range(1, 8).Select(i => "problem " + i).ToArray() };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("color: red;"), out JObject details, out string error));

            Assert.Equal(8, details["sdkMessages"].Count());
            Assert.Contains("problem 1; problem 2; problem 3; problem 4; problem 5", error);
            Assert.DoesNotContain("problem 6;", error);
            Assert.Contains("(+3 more in sdkMessages)", error);
        }

        [Fact]
        public void TryApply_KeepsAtMostTwentySdkMessages()
        {
            var part = new TalkativeDesignStylesPart { Messages = Enumerable.Range(1, 25).Select(i => "problem " + i).ToArray() };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("color: red;"), out JObject details, out string error));

            Assert.Equal(20, details["sdkMessages"].Count());
            Assert.Contains("(+15 more in sdkMessages)", error);
        }

        [Fact]
        public void TryApply_ShortensAVeryLongSdkMessageInTheErrorButKeepsItWholeInTheList()
        {
            string longMessage = new string('x', 300);
            var part = new TalkativeDesignStylesPart { Messages = new[] { longMessage } };

            Assert.False(ThemeStyleEditHelper.TryApply(part, Styles("color: red;"), out JObject details, out string error));

            Assert.Equal(longMessage, (string)details["sdkMessages"][0]["text"]);
            Assert.Contains(new string('x', 240) + "...", error);
            Assert.DoesNotContain(new string('x', 241), error);
        }

        // ---- the same refusal on the structured and theme routes ----------------------------------------------------

        [Fact]
        public void TryApply_ThemeClassImportThatTheSdkRefusesForTheImage_BlamesTheImage()
        {
            // ThemeStylesPart.ImportCss returns nothing: its refusal is the exception it throws, and that is the SDK's reason.
            var part = new RefusingThemeStylesPart { Reason = "SynthGone not found" };
            string request = new JObject { ["className"] = "Probe", ["css"] = ".Probe { background-image: gx-image(SynthGone); }" }.ToString();

            Assert.False(ThemeStyleEditHelper.TryApply(part, request, out JObject details, out string error, name => false));

            Assert.Equal(new[] { "SynthGone" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Equal("SynthGone not found", (string)Assert.Single(details["sdkMessages"])["text"]);
            Assert.Contains("SDK result: The GeneXus SDK rejected the CSS source: SynthGone not found", error);
            Assert.Equal("ThemeStyleUnresolvedReferences", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_ThemeClassImportThatTheSdkRefusesForAnotherReason_PassesItOnAndListsTheImages()
        {
            var part = new RefusingThemeStylesPart { Reason = "Unexpected token at line 3" };
            string request = new JObject { ["className"] = "Probe", ["css"] = ".Probe { background-image: gx-image(SynthGone); }" }.ToString();

            Assert.False(ThemeStyleEditHelper.TryApply(part, request, out JObject details, out string error, name => false));

            Assert.Equal("Unexpected token at line 3", (string)Assert.Single(details["sdkMessages"])["text"]);
            Assert.Equal("The GeneXus SDK rejected the CSS source: Unexpected token at line 3 The source also refers to 1 Image object that does not exist in this Knowledge Base: SynthGone.", error);
            Assert.Equal(new[] { "SynthGone" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Null(details["rejectionCause"]);
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_WholeThemeImportThatTheSdkRefusesForTheImage_BlamesTheImage()
        {
            var part = new RefusingThemeStylesPart { Reason = "Image 'SynthGone' not found" };

            Assert.False(ThemeStyleEditHelper.TryApply(part, ".Probe { background-image: gx-image(SynthGone); }", out JObject details, out string error, name => false));

            Assert.Equal(new[] { "SynthGone" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Contains("Image object that does not exist", error);
            Assert.Equal("ThemeStyleUnresolvedReferences", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_WholeThemeImportThatTheSdkRefusesForAnotherReason_ListsTheImagesAndBlamesNobody()
        {
            var part = new RefusingThemeStylesPart { Reason = "Invalid CSS" };

            Assert.False(ThemeStyleEditHelper.TryApply(part, ".Probe { background-image: gx-image(SynthGone); }", out JObject details, out string error, name => false));

            Assert.Equal("Invalid CSS", (string)Assert.Single(details["sdkMessages"])["text"]);
            Assert.Equal(new[] { "SynthGone" }, details["unresolvedImageReferences"].Select(t => (string)t));
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void TryApply_ThemeImportThatAnOlderSdkAnswersWithFalse_ListsTheImagesAndBlamesNobody()
        {
            Assert.False(ThemeStyleEditHelper.TryApply(new FalseReturningThemeStylesPart(), ".Probe { background-image: gx-image(SynthGone); }",
                out JObject details, out string error, name => false));

            Assert.Null(details["sdkMessages"]);
            Assert.Equal("The GeneXus SDK rejected the CSS source. The source also refers to 1 Image object that does not exist in this Knowledge Base: SynthGone.", error);
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(details));
        }

        [Fact]
        public void FailureCode_IsTheGenericCodeWhenNothingWasExplained()
        {
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(null));
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(new JObject()));
            // Listing the missing images is not blaming them: only the cause makes the code.
            Assert.Equal("ThemeStyleApplyFailed", ThemeStyleEditHelper.FailureCode(new JObject { ["unresolvedImageReferences"] = new JArray("X") }));
            Assert.Equal("ThemeStyleUnresolvedReferences", ThemeStyleEditHelper.FailureCode(new JObject { ["rejectionCause"] = "unresolvedImageReferences" }));
        }

        // ---- the message and the hint of the refusal ----------------------------------------------------------------

        private const string HistoricalHint = "Read the style part first; use raw CSS/source for StyleSheet or {className, properties} for a Theme class.";

        [Fact]
        public void DescribeFailure_TheCauseCodeSendsTheCallerToCreateTheImages()
        {
            ThemeStyleEditHelper.DescribeFailure("ThemeStyleUnresolvedReferences", new JObject(), out string message, out string hint);

            Assert.Equal("Theme/StyleSheet write refused: the style source refers to Image objects that do not exist in the Knowledge Base.", message);
            Assert.Contains("Create or import the Image objects listed in unresolvedImageReferences", hint);
            Assert.EndsWith("Nothing was saved.", hint);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("{}")]
        public void DescribeFailure_WithNothingNewToSayIsTheHistoricalAnswer(string detailsJson)
        {
            ThemeStyleEditHelper.DescribeFailure("ThemeStyleApplyFailed", detailsJson == null ? null : JObject.Parse(detailsJson), out string message, out string hint);

            Assert.Equal("Theme/StyleSheet write failed.", message);
            Assert.Equal(HistoricalHint, hint);
        }

        [Fact]
        public void DescribeFailure_SdkMessagesComeFirstAndTheHistoricalGuidanceStays()
        {
            ThemeStyleEditHelper.DescribeFailure("ThemeStyleApplyFailed", new JObject { ["sdkMessages"] = new JArray() }, out string message, out string hint);

            Assert.Equal("Theme/StyleSheet write failed.", message);
            Assert.StartsWith("The SDK's own validation messages are in sdkMessages; correct what they report in the style source and write again.", hint);
            Assert.EndsWith("If they do not explain it: " + char.ToLowerInvariant(HistoricalHint[0]) + HistoricalHint.Substring(1), hint);
            Assert.DoesNotContain("unresolvedImageReferences", hint);
        }

        [Fact]
        public void DescribeFailure_ImagesListedButNotBlamedAreMentionedBesideTheGenericGuidance()
        {
            ThemeStyleEditHelper.DescribeFailure("ThemeStyleApplyFailed", new JObject { ["unresolvedImageReferences"] = new JArray("SynthGone") }, out _, out string hint);

            Assert.StartsWith(HistoricalHint, hint);
            Assert.EndsWith("The Image objects listed in unresolvedImageReferences do not exist in the Knowledge Base either: create or import them too.", hint);
        }

        [Fact]
        public void DescribeFailure_SdkMessagesAndListedImagesTravelTogether()
        {
            ThemeStyleEditHelper.DescribeFailure("ThemeStyleApplyFailed",
                new JObject { ["sdkMessages"] = new JArray(), ["unresolvedImageReferences"] = new JArray("SynthGone") }, out _, out string hint);

            Assert.StartsWith("The SDK's own validation messages are in sdkMessages;", hint);
            Assert.Contains("do not exist in the Knowledge Base either", hint);
        }

        [Fact]
        public void WriteThemeStylePart_AsksTheKbAboutImagesAndAnswersWithTheCauseCode()
        {
            // The write needs a live SDK object, so pin the wiring at source level.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.ThemeWrite.cs");

            Assert.Contains("ThemeStyleEditHelper.TryApply(stylePart, content, out JObject details, out string applyError, ImageObjectExists)", source, StringComparison.Ordinal);
            Assert.Contains("ThemeStyleEditHelper.FailureCode(details)", source, StringComparison.Ordinal);
            Assert.Contains("_objectService.FindObject(name, \"Image\") != null", source, StringComparison.Ordinal);
            Assert.Contains("ThemeStyleEditHelper.DescribeFailure(code, styleDetails, out string message, out string hint)", source, StringComparison.Ordinal);
        }

        // ---- doubles: the part types are recognised by name, as for the SDK's own ---------------------------------
        // The real DesignStylesPart answers ValidateNewSource(source) with its private
        // Validate(source, InheritedClasses, ChildrenDesignSystemNames, new OutputMessages()); the doubles that can talk have that method.

        public interface IHasSource
        {
            string Source { get; }
        }

        public class SilentDesignStylesPart : IHasSource
        {
            private string _source = string.Empty;
            public int SourceWrites { get; private set; }
            public string Source { get => _source; set { _source = value; SourceWrites++; } }
            public bool ValidateNewSource(string source) => false;
        }

        public class AcceptingDesignStylesPart : IHasSource
        {
            private string _source = string.Empty;
            public int SourceWrites { get; private set; }
            public string Source { get => _source; set { _source = value; SourceWrites++; } }
            public bool ValidateNewSource(string source) => true;
        }

        public class TalkativeDesignStylesPart : IHasSource
        {
            private string _source = string.Empty;
            public int SourceWrites { get; private set; }
            public string Source { get => _source; set { _source = value; SourceWrites++; } }
            public string ValidatedSource { get; private set; }
            public string[] Messages { get; set; } = { "The class 'Ghost' does not exists" };
            public IEnumerable<object> InheritedClasses => new object[0];
            public IEnumerable<string> ChildrenDesignSystemNames => new string[0];
            public bool ValidateNewSource(string source) => false;

            private bool Validate(string source, IEnumerable<object> inheritedClasses, IEnumerable<string> childrenDSOs, OutputMessages output)
            {
                ValidatedSource = source;
                foreach (string message in Messages) output.Add(new OutputError(message));
                return false;
            }
        }

        public class MuteDesignStylesPart : IHasSource
        {
            public string Source { get; set; } = string.Empty;
            public IEnumerable<object> InheritedClasses => new object[0];
            public IEnumerable<string> ChildrenDesignSystemNames => new string[0];
            public bool ValidateNewSource(string source) => false;
            private bool Validate(string source, IEnumerable<object> inheritedClasses, IEnumerable<string> childrenDSOs, OutputMessages output) => false;
        }

        public class ThrowingDesignStylesPart : IHasSource
        {
            public string Source { get; set; } = string.Empty;
            public IEnumerable<object> InheritedClasses => new object[0];
            public IEnumerable<string> ChildrenDesignSystemNames => new string[0];
            public bool ValidateNewSource(string source) => false;
            private bool Validate(string source, IEnumerable<object> inheritedClasses, IEnumerable<string> childrenDSOs, OutputMessages output)
                => throw new InvalidOperationException("validation unavailable");
        }

        public class UnreadableDesignStylesPart : IHasSource
        {
            public string Source { get; set; } = string.Empty;
            public IEnumerable<object> InheritedClasses => throw new InvalidOperationException("model unavailable");
            public IEnumerable<string> ChildrenDesignSystemNames => new string[0];
            public bool ValidateNewSource(string source) => false;
            private bool Validate(string source, IEnumerable<object> inheritedClasses, IEnumerable<string> childrenDSOs, OutputMessages output)
            {
                output.Add(new OutputError("must not be reached"));
                return false;
            }
        }

        // ThemeStylesPart.ImportCss(string) is void in the SDK: a refusal is the exception it throws.
        public class RefusingThemeStylesPart
        {
            public string Reason { get; set; } = "Invalid CSS";
            public object GetStyle(string name) => new RefusingStyle { Reason = Reason };
            public void ImportCss(string css) => throw new InvalidOperationException(Reason);
        }

        public class RefusingStyle
        {
            public string Reason { get; set; } = "Invalid CSS";
            public void ImportCss(string css) => throw new InvalidOperationException(Reason);
        }

        public class FalseReturningThemeStylesPart
        {
            public bool ImportCss(string css) => false;
        }
    }
}
