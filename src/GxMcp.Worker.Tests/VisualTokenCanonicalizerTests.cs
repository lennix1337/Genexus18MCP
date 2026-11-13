using System;
using System.Text;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The version token of a visual part (WebForm, Layout) must not change when only the GUID ids the SDK regenerates on
    /// every load of a form that was never saved change: <c>genexus_read</c> and the fresh read behind <c>baseVersion</c>
    /// then agree on an unchanged object. Everything a real edit changes must still change it. All of it is synthetic: a
    /// form is a string and the object token is a literal, so no Knowledge Base and no SDK object is involved; the
    /// wiring that needs one (the read, the verification read, export_part) is pinned at source level.
    /// </summary>
    public class VisualTokenCanonicalizerTests
    {
        private const string ObjectVersion = "639280960040000000:root";
        // The revision half of a version token ("ticks:parent"), in the synthetic observations below.
        private const string ObservedRevision = "r1:root";
        private const string ThemeClass = "b989b6d7-5d14-4c05-b504-3dc12f28c63c";
        private const string Guid1 = "8df76cc1-1724-4592-9d58-2f25b904c00f";
        private const string Guid2 = "ca56da13-7e19-49b9-9947-67697ae77214";
        private const string Guid3 = "982ae776-ad52-4997-9831-42bb34e94d9b";
        private const string Guid4 = "7aad317f-2ee5-4206-a9be-745392cd8dc3";

        // The id of one element in the n-th load of the same form: a GUID that no other load and no other element shares.
        private static string Id(int load, int element) => string.Format("{0:x8}-0000-4000-8000-{0:x12}", load * 100 + element);

        // A form as one load of the SDK returns it: layout, table, row and cell carry a new GUID id each time; the form's own
        // id is not a GUID, and the class of the text block is a theme GUID plus a suffix, which is not an id either.
        private static string Form(int load, string caption = "Hello")
            => "<GxMultiForm><Form id=\"1\" type=\"layout\"><layout id=\"" + Id(load, 1) + "\">"
                + "<table id=\"" + Id(load, 2) + "\" controlName=\"MainTable\"><row id=\"" + Id(load, 3) + "\"><cell id=\"" + Id(load, 4) + "\">"
                + "<textblock controlName=\"Title\" caption=\"" + caption + "\" class=\"" + ThemeClass + "-1\" />"
                + "</cell></row></table></layout></Form></GxMultiForm>";

        private static string Token(string xml, string part = "WebForm") => WriteService.ComposeContentVersionToken(ObjectVersion, part, xml);

        private static string Pair(string a, string b) => "<x><b id=\"" + a + "\"/><c id=\"" + b + "\"/></x>";

        // ---- the premise and the fix ---------------------------------------------------

        [Fact]
        public void TwoReadsOfAnUnchangedForm_ThatDifferOnlyInTheirGuidIds_GiveOneToken()
        {
            string first = Form(1), second = Form(2);

            // The premise: the texts differ and so do their plain fingerprints, which is what made the token stale against itself.
            Assert.NotEqual(first, second);
            Assert.NotEqual(WriteService.ComputeContentFingerprint(first), WriteService.ComputeContentFingerprint(second));

            Assert.Equal(Token(first), Token(second));
            Assert.Equal(Token(first, "Layout"), Token(second, "Layout"));
        }

        [Fact]
        public void TheCanonicalTextNumbersTheGuidIdsByTheirFirstAppearance_AndKeepsEverythingElse()
        {
            string expected = Form(1)
                .Replace(Id(1, 1), "gx-guid-1")
                .Replace(Id(1, 2), "gx-guid-2")
                .Replace(Id(1, 3), "gx-guid-3")
                .Replace(Id(1, 4), "gx-guid-4");

            Assert.Equal(expected, VisualTokenCanonicalizer.Canonicalize(Form(1)));
            Assert.Contains("<Form id=\"1\"", expected);
            Assert.Contains("class=\"" + ThemeClass + "-1\"", expected);
        }

        [Fact]
        public void TheFingerprintOfAVisualPart_IsThePlainFingerprintOfTheCanonicalText()
        {
            string text = Form(1);

            Assert.Equal(
                WriteService.ComputeContentFingerprint(VisualTokenCanonicalizer.Canonicalize(text)),
                WriteService.ComputePartContentFingerprint("WebForm", text));
        }

        // ---- what still changes the token ----------------------------------------------

        [Fact]
        public void ARealChangeStillChangesTheToken()
        {
            string baseline = Token(Form(1));

            Assert.NotEqual(baseline, Token(Form(2, caption: "Hello!")));                                       // a caption
            Assert.NotEqual(baseline, Token(Form(2).Replace("controlName=\"Title\"", "controlName=\"Heading\""))); // a property
            Assert.NotEqual(baseline, Token(Form(2).Replace("</cell>", "\n</cell>")));                           // one line more
            Assert.NotEqual(baseline, Token(Form(2).Replace("<row ", "<row hidden=\"true\" ")));                  // an attribute added
            Assert.NotEqual(baseline, Token(Form(2).Replace("</row>", "</row><row id=\"" + Id(2, 5) + "\"/>")));  // an element added
        }

        [Fact]
        public void TheGuidOfAThemeClassIsNotAnId()
        {
            string baseline = Token(Form(1));

            Assert.NotEqual(baseline, Token(Form(2).Replace(ThemeClass + "-1", Id(9, 9) + "-1")));
            Assert.NotEqual(baseline, Token(Form(2).Replace(ThemeClass + "-1", ThemeClass + "-2")));
        }

        [Fact]
        public void AnIdThatIsNotAGuidIsKept()
        {
            string baseline = Token(Form(1));

            Assert.NotEqual(baseline, Token(Form(2).Replace("<Form id=\"1\"", "<Form id=\"2\"")));
            Assert.NotEqual(baseline, Token(Form(2).Replace("<Form id=\"1\"", "<Form id=\"MainForm\"")));
            Assert.NotEqual(Token("<a id=\"1\"/>"), Token("<a id=\"2\"/>"));
        }

        [Theory]
        [InlineData("parentId")]
        [InlineData("rootId")]
        [InlineData("controlName")]
        [InlineData("data-id")]
        [InlineData("idx")]
        public void AGuidInAnotherAttributeIsKept(string attribute)
        {
            string With(string guid) => "<x><a id=\"" + Guid1 + "\" " + attribute + "=\"" + guid + "\"/></x>";

            // A GUID that is the value of another attribute and that no id holds stays: it is a different text from another GUID.
            Assert.Equal(With(Guid2).Replace(Guid1, "gx-guid-1"), VisualTokenCanonicalizer.Canonicalize(With(Guid2)));
            Assert.NotEqual(Token(With(Guid2)), Token(With(Guid3)));
        }

        // ---- a reference to an id is numbered with it ----------------------------------

        private static string Refs(string a, string b, string target)
            => "<x><a id=\"" + a + "\"/><b id=\"" + b + "\"/><ref target=\"" + target + "\"/></x>";

        [Fact]
        public void AnAttributeThatHoldsAnIdAsItsWholeValueIsNumberedWithIt()
        {
            Assert.Equal(
                "<x><a id=\"gx-guid-1\"/><b id=\"gx-guid-2\"/><ref target=\"gx-guid-1\"/></x>",
                VisualTokenCanonicalizer.Canonicalize(Refs(Guid1, Guid2, Guid1)));

            // A reference may come before the id it names and may spell it another way.
            Assert.Equal(
                "<x><ref target=\"gx-guid-1\"/><a id=\"gx-guid-1\"/></x>",
                VisualTokenCanonicalizer.Canonicalize("<x><ref target=\"{" + Guid1.ToUpperInvariant() + "}\"/><a id=\"" + Guid1 + "\"/></x>"));

            // The name of the attribute does not matter, and the GUID has to be the whole value.
            Assert.Equal(
                "<x><a id=\"gx-guid-1\"/><b parentId=\"gx-guid-1\" class=\"" + Guid1 + "-1\"/></x>",
                VisualTokenCanonicalizer.Canonicalize("<x><a id=\"" + Guid1 + "\"/><b parentId=\"" + Guid1 + "\" class=\"" + Guid1 + "-1\"/></x>"));
        }

        [Fact]
        public void AGuidThatNoIdHoldsIsNotNumberedWhereverItAppears()
        {
            string text = "<x><a id=\"" + Guid1 + "\"/><ref target=\"" + Guid2 + "\"/><ref target=\"" + Guid2 + "\"/></x>";

            Assert.Equal(
                "<x><a id=\"gx-guid-1\"/><ref target=\"" + Guid2 + "\"/><ref target=\"" + Guid2 + "\"/></x>",
                VisualTokenCanonicalizer.Canonicalize(text));
        }

        [Fact]
        public void WhatARefersToIsPartOfTheState()
        {
            // Two ids swapped while a reference keeps naming the first: the reference now points at the other element.
            Assert.NotEqual(Token(Refs(Guid1, Guid2, Guid1)), Token(Refs(Guid2, Guid1, Guid1)));

            // The reference left dangling by an id that moved is another state as well.
            Assert.NotEqual(Token(Refs(Guid1, Guid2, Guid1)), Token(Refs(Guid3, Guid2, Guid1)));

            // A reference moved to the other element.
            Assert.NotEqual(Token(Refs(Guid1, Guid2, Guid1)), Token(Refs(Guid1, Guid2, Guid2)));

            // With nothing that names the ids, swapping them is not something the text can tell.
            Assert.Equal(Token(Pair(Guid1, Guid2)), Token(Pair(Guid2, Guid1)));
        }

        [Fact]
        public void TwoLoadsOfAFormWhoseElementsNameEachOther_AreOneState()
        {
            // The regenerated ids are the values of other attributes too: they are renumbered together, so the loads agree.
            string Load(int n) => "<GxMultiForm><layout id=\"" + Id(n, 1) + "\"><table id=\"" + Id(n, 2) + "\" layoutId=\"" + Id(n, 1) + "\">"
                + "<cell id=\"" + Id(n, 3) + "\" tableId=\"" + Id(n, 2) + "\" focus=\"" + Id(n, 3) + "\"/></table></layout></GxMultiForm>";

            Assert.NotEqual(Load(1), Load(2));
            Assert.Equal(Token(Load(1)), Token(Load(2)));

            // A reference that names another element is a change.
            Assert.NotEqual(Token(Load(1)), Token(Load(2).Replace("focus=\"" + Id(2, 3) + "\"", "focus=\"" + Id(2, 2) + "\"")));
        }

        // ---- ids that are different stay different -------------------------------------

        [Fact]
        public void DistinctIdsStayDistinct_AndARepeatedIdStaysRepeated()
        {
            string distinct = Pair(Guid1, Guid2);
            string repeated = Pair(Guid1, Guid1);

            Assert.NotEqual(Token(distinct), Token(repeated));

            // Another pair of ids with the same arrangement is the same text.
            Assert.Equal(Token(distinct), Token(Pair(Guid3, Guid4)));
            Assert.Equal(Token(repeated), Token(Pair(Guid3, Guid3)));
            Assert.Equal(Token(Pair(Guid1, Guid2) + Pair(Guid1, Guid3)), Token(Pair(Guid4, Guid3) + Pair(Guid4, Guid2)));
            Assert.NotEqual(Token(Pair(Guid1, Guid2) + Pair(Guid1, Guid3)), Token(Pair(Guid4, Guid3) + Pair(Guid3, Guid2)));
        }

        [Fact]
        public void TheSameGuidInTwoSpellingsIsOneId()
        {
            string upper = Guid1.ToUpperInvariant();
            string braced = "{" + Guid1 + "}";
            string parenthesised = "(" + Guid1 + ")";
            string bare = Guid1.Replace("-", string.Empty);

            Assert.Equal(Token(Pair(Guid3, Guid3)), Token(Pair(Guid1, upper)));
            Assert.Equal(Token(Pair(Guid3, Guid3)), Token(Pair(braced, bare)));
            Assert.Equal(Token(Pair(Guid3, Guid3)), Token(Pair(parenthesised, braced)));
            Assert.Equal(Token(Pair(Guid3, Guid3)), Token(Pair(parenthesised, bare)));
            Assert.NotEqual(Token(Pair(Guid3, Guid3)), Token(Pair(Guid1, Guid2.ToUpperInvariant())));
        }

        [Fact]
        public void AFormWithManyIdsIsNumberedInOrder()
        {
            var form = new StringBuilder("<GxMultiForm>");
            for (int i = 0; i < 5000; i++) form.Append("<cell id=\"").Append(Id(1, i)).Append("\"/>");
            form.Append("</GxMultiForm>");

            string canonical = VisualTokenCanonicalizer.Canonicalize(form.ToString());

            Assert.Contains("<cell id=\"gx-guid-1\"/><cell id=\"gx-guid-2\"/>", canonical);
            Assert.EndsWith("<cell id=\"gx-guid-5000\"/></GxMultiForm>", canonical);
            Assert.DoesNotContain("-0000-4000-8000-", canonical);
        }

        // ---- which values and attributes are an id -------------------------------------

        [Theory]
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>", "<a id=\"gx-guid-1\"/>")]                    // hyphenated
        [InlineData("<a id=\"{8df76cc1-1724-4592-9d58-2f25b904c00f}\"/>", "<a id=\"gx-guid-1\"/>")]                  // braces
        [InlineData("<a id=\"(8df76cc1-1724-4592-9d58-2f25b904c00f)\"/>", "<a id=\"gx-guid-1\"/>")]                  // parentheses
        [InlineData("<a id=\"8df76cc117244592" + "9d582f25b904c00f\"/>", "<a id=\"gx-guid-1\"/>")]                    // 32 digits
        [InlineData("<a id=\"8DF76CC1-1724-4592-9D58-2F25B904C00F\"/>", "<a id=\"gx-guid-1\"/>")]                    // upper case
        [InlineData("<a ID=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>", "<a ID=\"gx-guid-1\"/>")]                    // the name in another case
        [InlineData("<a id='8df76cc1-1724-4592-9d58-2f25b904c00f'/>", "<a id='gx-guid-1'/>")]                        // single quotes
        [InlineData("<a id = \"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>", "<a id = \"gx-guid-1\"/>")]                // white space around =
        [InlineData("<a\r\n  id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"\r\n/>", "<a\r\n  id=\"gx-guid-1\"\r\n/>")]   // attribute on its own line
        [InlineData("<a name=\"n\" id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\" other=\"o\"/>", "<a name=\"n\" id=\"gx-guid-1\" other=\"o\"/>")]
        public void AGuidThatIsTheWholeValueOfAnIdAttributeIsReplaced(string text, string expected)
            => Assert.Equal(expected, VisualTokenCanonicalizer.Canonicalize(text));

        [Theory]
        [InlineData("<a id=\"1\"/>")]
        [InlineData("<a id=\"\"/>")]
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00f-1\"/>")]                            // a theme class is a GUID plus a suffix
        [InlineData("<a id=\"x8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00\"/>")]                               // one digit short
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00f0\"/>")]                             // one digit over
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00g\"/>")]                              // not hexadecimal
        [InlineData("<a id=\"{8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]                             // unbalanced brace
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00f 8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]
        [InlineData("<a id=\"8df76cc1-1724-4592-9d58-2f25b904c00f'/>")]                               // the closing quote is not the opening one
        [InlineData("<a class=\"8df76cc1-1724-4592-9d58-2f25b904c00f-1\"/>")]
        [InlineData("<a parentId=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]                         // the name is exactly id
        [InlineData("<a data-id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]
        [InlineData("<a idx=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]
        [InlineData("<a name=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]
        [InlineData("<a b=\"c\"id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]                         // no white space before the name
        [InlineData("<a/id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\"/>")]
        public void AnythingElseIsLeftAsItIs(string text)
            => Assert.Equal(text, VisualTokenCanonicalizer.Canonicalize(text));

        // ---- no GUID id, no change -----------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<GxMultiForm><Form id=\"1\" type=\"layout\"/></GxMultiForm>")]
        [InlineData("<GxMultiForm><Form id=\"1\"")]
        [InlineData("not xml at all")]
        [InlineData("<!DOCTYPE x [<!ENTITY e 'value'>]><x id=\"1\">&e;</x>")]
        public void WithoutAGuidId_TheTextAndItsTokenAreTheOnesTheyAlwaysWere(string text)
        {
            Assert.Equal(text, VisualTokenCanonicalizer.Canonicalize(text));
            Assert.Equal(WriteService.ComputeContentFingerprint(text), WriteService.ComputePartContentFingerprint("WebForm", text));
            Assert.Equal(ObjectVersion + ":" + WriteService.ComputeContentFingerprint(text), Token(text));
        }

        [Fact]
        public void TheTextIsNotParsed_SoMalformedXmlAndADoctypeAreCanonicalisedLikeAnyOtherText()
        {
            Assert.Equal("<a id=\"gx-guid-1\"><b", VisualTokenCanonicalizer.Canonicalize("<a id=\"" + Guid1 + "\"><b"));
            Assert.Equal(
                "<!DOCTYPE x [<!ENTITY e 'value'>]><x id=\"gx-guid-1\">&e;</x>",
                VisualTokenCanonicalizer.Canonicalize("<!DOCTYPE x [<!ENTITY e 'value'>]><x id=\"" + Guid1 + "\">&e;</x>"));

            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Helpers", "VisualTokenCanonicalizer.cs");
            foreach (string parser in new[] { "XmlDocument", "XDocument", "XElement", "XmlReader", "XmlTextReader", "SafeXml" })
                Assert.DoesNotContain(parser, source);
        }

        [Fact]
        public void AnIdInACommentOrACdataSection_IsReadAsTheAttributeItLooksLike()
        {
            // The documented difference from a parse: the text is scanned, not parsed.
            Assert.Equal(
                "<x><!-- <a id=\"gx-guid-1\"/> --></x>",
                VisualTokenCanonicalizer.Canonicalize("<x><!-- <a id=\"" + Guid1 + "\"/> --></x>"));
            Assert.Equal(
                "<x><![CDATA[ <a id=\"gx-guid-1\"/> ]]></x>",
                VisualTokenCanonicalizer.Canonicalize("<x><![CDATA[ <a id=\"" + Guid1 + "\"/> ]]></x>"));
        }

        // ---- only a visual part ---------------------------------------------------------

        [Theory]
        [InlineData("Source")]
        [InlineData("Events")]
        [InlineData("Rules")]
        [InlineData("Variables")]
        [InlineData("PatternInstance")]
        [InlineData("")]
        [InlineData(null)]
        public void OnlyAVisualPartIsCanonicalised(string part)
        {
            string text = Form(1);

            Assert.Equal(WriteService.ComputeContentFingerprint(text), WriteService.ComputePartContentFingerprint(part, text));
            Assert.NotEqual(Token(Form(1), part), Token(Form(2), part));
        }

        [Theory]
        [InlineData("WebForm")]
        [InlineData("Layout")]
        [InlineData("webform")]
        [InlineData("LAYOUT")]
        public void AVisualPartIsCanonicalised(string part)
        {
            string text = Form(1);

            Assert.Equal(WriteService.ComputePartContentFingerprint(part, text), WriteService.ComputePartContentFingerprint(part, Form(2)));
            Assert.NotEqual(WriteService.ComputeContentFingerprint(text), WriteService.ComputePartContentFingerprint(part, text));
            Assert.Equal(WriteService.ComputePartContentFingerprint("WebForm", text), WriteService.ComputePartContentFingerprint(part, text));
        }

        [Fact]
        public void WithoutAnObjectToken_ThereIsNoToken()
        {
            Assert.Null(WriteService.ComposeContentVersionToken(null, "WebForm", Form(1)));
            Assert.Null(WriteService.ComputePartContentVersionToken(null, "WebForm", Form(1)));
        }

        // ---- the token keeps its shape --------------------------------------------------

        [Fact]
        public void TheTokenKeepsItsShape_AndIsAcceptedWhereTokensAreCompared()
        {
            string token = Token(Form(1));
            string[] segments = token.Split(':');

            Assert.Equal(3, segments.Length);
            Assert.Equal("639280960040000000", segments[0]);
            Assert.Equal("root", segments[1]);
            Assert.Matches("^[0-9a-f]{64}$", segments[2]);

            // A part token extends the object's token with exactly one segment.
            Assert.True(SpecifyOnlyPlanner.VersionMatches(token, ObjectVersion));
            Assert.False(SpecifyOnlyPlanner.VersionMatches(token, "639280960040000001:root"));
            Assert.True(ControlParametersService.VersionMatches(token, Token(Form(2)), () => ObjectVersion));
            Assert.False(ControlParametersService.VersionMatches(token, Token(Form(2, caption: "Other")), () => ObjectVersion));
        }

        // ---- the recovery certificate ---------------------------------------------------

        private static RecoveryReadObservation Observe(string text, string part)
            => new RecoveryReadObservation
            {
                Name = "SyntheticPanel",
                Type = "WebPanel",
                Guid = "0f8fad5b-d9cb-469f-a165-70867728950e",
                EntityKey = "d2b4a0c0-0000-0000-0000-000000000000:7",
                Path = "SyntheticPanel",
                RequestedPart = part,
                ResolvedPart = part,
                Text = text,
                VersionToken = WriteService.ComposeContentVersionToken(ObservedRevision, part, text),
                RequestOffset = null,
                RequestLimit = 0,
                WriteStampBefore = "w100",
                WriteStampAfter = "w100",
                RevisionBefore = ObservedRevision,
                RevisionAfter = ObservedRevision
            };

        [Fact]
        public void TheRecoveryCertificateOfAFormWithRegeneratedIds_IsCompleteAndTheSameOnEveryRead()
        {
            JObject first = RecoveryReadCertificate.Build(Observe(Form(1), "WebForm"));
            JObject second = RecoveryReadCertificate.Build(Observe(Form(2), "WebForm"));

            foreach (JObject certificate in new[] { first, second })
            {
                Assert.True(certificate["complete"]!.Value<bool>(), certificate.ToString());
                Assert.Null(certificate["reason"]);
                // What the Gateway checks: the token ends with the certificate's own hash.
                Assert.EndsWith(":" + certificate["sha256"], certificate["versionToken"]!.ToString());
                Assert.Equal("canonical-visual-xml", certificate["sha256Basis"]!.ToString());
            }

            // The hash is not the SHA-256 of the text, and the certificate says so; and the two reads are one state.
            Assert.NotEqual(WriteService.ComputeContentFingerprint(Form(1)), first["sha256"]!.ToString());
            Assert.Equal(first["sha256"]!.ToString(), second["sha256"]!.ToString());
            Assert.Equal(first["versionToken"]!.ToString(), second["versionToken"]!.ToString());
        }

        [Fact]
        public void TheRecoveryCertificateOfARealChange_IsAnotherState()
        {
            JObject before = RecoveryReadCertificate.Build(Observe(Form(1), "WebForm"));
            JObject after = RecoveryReadCertificate.Build(Observe(Form(2, caption: "Hello!"), "WebForm"));

            Assert.True(after["complete"]!.Value<bool>());
            Assert.NotEqual(before["sha256"]!.ToString(), after["sha256"]!.ToString());
            Assert.NotEqual(before["versionToken"]!.ToString(), after["versionToken"]!.ToString());
        }

        [Theory]
        [InlineData("Events")]
        [InlineData("Source")]
        public void TheRecoveryCertificateOfAnotherPart_KeepsTheHashOfTheText(string part)
        {
            string text = Form(1);
            JObject certificate = RecoveryReadCertificate.Build(Observe(text, part));

            Assert.True(certificate["complete"]!.Value<bool>());
            Assert.Equal(WriteService.ComputeContentFingerprint(text), certificate["sha256"]!.ToString());
            Assert.Null(certificate["sha256Basis"]);
        }

        [Fact]
        public void TheRecoveryCertificateOfAFormWithoutGuidIds_KeepsTheHashOfTheText()
        {
            string text = "<GxMultiForm><Form id=\"1\" type=\"layout\"/></GxMultiForm>";
            JObject certificate = RecoveryReadCertificate.Build(Observe(text, "Layout"));

            Assert.True(certificate["complete"]!.Value<bool>());
            Assert.Equal(WriteService.ComputeContentFingerprint(text), certificate["sha256"]!.ToString());
            Assert.Null(certificate["sha256Basis"]);
        }

        [Fact]
        public void ATokenThatIsNotDerivedFromTheCanonicalText_IsStillRefusedByTheCertificate()
        {
            // The token a read made before the ids were neutralised: the plain fingerprint of the text.
            var observation = Observe(Form(1), "WebForm");
            observation.VersionToken = ObservedRevision + ":" + WriteService.ComputeContentFingerprint(Form(1));

            JObject certificate = RecoveryReadCertificate.Build(observation);

            Assert.False(certificate["complete"]!.Value<bool>());
            Assert.Equal("version-token-not-derived-from-text", certificate["reason"]!.ToString());
        }

        // ---- wiring (the read, the verification read and export_part need a live object) --

        private const string ProcessSourceContentDeclaration =
            "private void ProcessSourceContent(KBObject obj, string content, int? offset, int? limit, JObject result, string client = \"ide\")";
        private const string VerificationReadDeclaration =
            "internal string ReadObjectSourceForVerification(string target, string partName, string typeFilter = null)";
        private const string ExportDeclaration =
            "public string ExportObjectToText(string target, string outputPath, string partName = null, string typeFilter = null, bool overwrite = false)";
        private const string ReadInternalDeclaration = "private string ReadObjectSourceInternal(KBObject obj, string partName,";

        private static string ObjectServiceSource() => RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

        [Fact]
        public void TheReadBuildsItsTokenFromThePartItReads()
        {
            string source = ObjectServiceSource();
            string body = SourceAssert.MethodBody(source, ProcessSourceContentDeclaration);

            Assert.Contains("WriteService.ComputePartContentVersionToken(obj, result[\"part\"]?.ToString(), content)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ComputeContentVersionToken(", body, StringComparison.Ordinal);
            // The plain call is gone from the whole file for the text of a read.
            Assert.DoesNotContain("ComputeContentVersionToken(obj, content)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryPathToProcessSourceContentNamesThePartBeforeTheToken()
        {
            // The part decides which fingerprint the token carries; a path that did not name it would fall back to the plain
            // fingerprint and make the token unstable again without any other symptom.
            string body = SourceAssert.MethodBody(ObjectServiceSource(), ReadInternalDeclaration);

            int visualResult = body.IndexOf("var visualResult = new JObject", StringComparison.Ordinal);
            int visualCall = body.IndexOf("ProcessSourceContent(obj, xml, offset, limit, visualResult, client)", StringComparison.Ordinal);
            Assert.True(visualResult >= 0 && visualCall > visualResult);
            Assert.Contains("[\"part\"] = partName", body.Substring(visualResult, visualCall - visualResult), StringComparison.Ordinal);

            // The result every other part is built in (Source, Variables, Theme, ...) is named the same way, right at its creation.
            Assert.Equal(1, SourceAssert.Count(body, "JObject result = new JObject();"));
            int result = body.IndexOf("JObject result = new JObject();", StringComparison.Ordinal);
            Assert.Contains("result[\"part\"] = partName;", body.Substring(result, 200), StringComparison.Ordinal);
        }

        [Fact]
        public void TheVerificationReadFallbackBuildsItsTokenFromTheResolvedPart()
        {
            string body = SourceAssert.MethodBody(ObjectServiceSource(), VerificationReadDeclaration);

            Assert.Contains("string resolvedPart = ResolvePartName(obj, partName);", body, StringComparison.Ordinal);
            Assert.Contains("WriteService.ComputePartContentVersionToken(obj, resolvedPart, payload[\"source\"].ToString())", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ComputeContentVersionToken(", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTokenOfAnObjectIsComposedFromItsObjectTokenTheNamedPartAndTheText()
        {
            // The only member of the three that needs a KBObject, so it is the one the behaviour tests above cannot call.
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.cs");
            int at = source.IndexOf("internal static string ComputePartContentVersionToken(", StringComparison.Ordinal);
            Assert.True(at >= 0);

            string member = source.Substring(at, source.IndexOf(';', at) - at + 1);

            Assert.Contains("=> ComposeContentVersionToken(ComputeVersionToken(obj), partName, content);", member, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSourceStoreBackfillRecordsTheTokenOfThePartItStores()
        {
            Assert.Contains(
                "version = WriteService.ComputePartContentVersionToken(obj, partName, source);",
                ObjectServiceSource(), StringComparison.Ordinal);
        }

        [Fact]
        public void ExportPartReportsTheFingerprintTheTokenEndsWithWhenItIsNotTheHashOfTheFile()
        {
            string body = SourceAssert.MethodBody(ObjectServiceSource(), ExportDeclaration);

            // The sha256 of the file stays the exact one.
            Assert.Contains("[\"sha256\"] = WriteService.ComputeContentFingerprint(source)", body, StringComparison.Ordinal);
            Assert.Contains("WriteService.ComputePartContentFingerprint(exportResult[\"part\"]?.ToString() ?? normalizedPart, source)", body, StringComparison.Ordinal);
            Assert.Contains("exported[\"tokenFingerprint\"] = tokenFingerprint;", body, StringComparison.Ordinal);
            // The same name the recovery certificate gives the basis of its hash (one vocabulary for the one fact).
            Assert.Contains("exported[\"tokenFingerprintBasis\"] = RecoveryReadCertificate.Sha256BasisCanonicalVisualXml;", body, StringComparison.Ordinal);
            Assert.Equal("canonical-visual-xml", RecoveryReadCertificate.Sha256BasisCanonicalVisualXml);

            // Both new fields are only reported next to the version token they explain, and only when the two hashes differ.
            int token = body.IndexOf("if (!string.IsNullOrWhiteSpace(exportedVersionToken))", StringComparison.Ordinal);
            int fingerprint = body.IndexOf("exported[\"tokenFingerprint\"] = tokenFingerprint;", StringComparison.Ordinal);
            int differs = body.IndexOf("if (!string.Equals(tokenFingerprint, (string)exported[\"sha256\"], StringComparison.Ordinal))", StringComparison.Ordinal);
            Assert.True(token >= 0 && differs > token && fingerprint > differs);
        }

        [Fact]
        public void TheRecoveryCertificateHashesTheTextTheWayTheTokenDoes()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Helpers", "RecoveryReadCertificate.cs");

            Assert.Contains("string sha256 = text == null ? null : WriteService.ComputePartContentFingerprint(observation.ResolvedPart, text);", source, StringComparison.Ordinal);
            Assert.DoesNotContain("? null : WriteService.ComputeContentFingerprint(text)", source, StringComparison.Ordinal);
            Assert.Contains("certificate[\"sha256Basis\"] = Sha256BasisCanonicalVisualXml;", source, StringComparison.Ordinal);
        }
    }
}
