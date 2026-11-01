using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The three paths of genexus_properties that can touch a WorkWithPlus template (get, single set and batch set), run
    // through PropertyService itself. The fake target answers the SDK steps from fixed values, so no knowledge base is
    // needed. The cases below never reach the write: the template decision refuses first, or the object is not a template.
    public sealed class PropertyServiceWwpTemplateWiringTests
    {
        private static readonly Guid OtherObjectType = new Guid("3f0c2a5e-9b1d-4c7a-8e61-0d2b4f9a7c15");

        [Fact]
        public void GetAnswersAnAliasWithItsSdkNameAsNativeName()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");
            PropertyService.InvalidatePropertyCache(service.Target);

            string raw = service.GetProperties("SynthTemplate", propertyName: "TemplateType");
            JToken property = JObject.Parse(raw)["result"]?["property"];

            Assert.True(property != null, raw);
            Assert.Equal("TemplateType", property?["name"]?.ToString());
            Assert.Equal("WWPTemplate_Type", property?["nativeName"]?.ToString());
            Assert.Equal("Trn.Selection", property?["value"]?.ToString());
        }

        [Fact]
        public void TheReconciliationReadTranslatesTheAliasesOfTheFenceToTheSdkNames()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            string raw = service.GetProperties("SynthTemplate", propertyNames: new[] { "TemplateXml" }, reconcileTimedOutWrite: true);
            JToken property = JObject.Parse(raw)["result"]?["property"];

            Assert.True(property != null, raw);
            Assert.Equal("TemplateXml", property?["name"]?.ToString());
            Assert.Equal("WWPTemplate_TemplateXml", property?["nativeName"]?.ToString());
        }

        [Fact]
        public void GetDoesNotTranslateAnAliasOnAnotherObjectType()
        {
            var service = new FakeTemplateTarget(OtherObjectType, "Trn.Selection", "<selection />");
            PropertyService.InvalidatePropertyCache(service.Target);

            string raw = service.GetProperties("SynthObject", propertyName: "TemplateType");

            Assert.Equal("PropertyNotFound", JObject.Parse(raw)["error"]?["code"]?.ToString() ?? raw);
        }

        [Fact]
        public void ASingleSetOfAnEmptyTypeIsRefusedBeforeAnyWrite()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperty("SynthTemplate", "TemplateType", ""));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("TypeEmpty", json["error"]?["violations"]?[0]?["check"]?.ToString());
        }

        [Fact]
        public void ASingleSetOfMalformedXmlIsRefusedBeforeAnyWrite()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperty("SynthTemplate", "TemplateXml", "<selection>"));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("XmlMalformed", json["error"]?["violations"]?[0]?["check"]?.ToString());
        }

        [Fact]
        public void ASingleSetOfTheSameNameOnAnotherObjectTypeIsNotRefusedAsATemplate()
        {
            var service = new FakeTemplateTarget(OtherObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperty("SynthObject", "TemplateType", ""));

            Assert.NotEqual("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal(new[] { ("TemplateType", "") }, service.Validations);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ABatchOfAnUnknownTypeIsRefusedWhetherOrNotItIsADryRun(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate",
                new JObject { ["TemplateType"] = "Trn.Panel" }, dryRun: dryRun));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("TypeUnknown", json["error"]?["violations"]?[0]?["check"]?.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ABatchThatChangesOnlyTheXmlIsCheckedAgainstTheStoredType(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Transaction", "<transaction />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate",
                new JObject { ["TemplateXml"] = "<selection />" }, dryRun: dryRun));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("RootMismatch", json["error"]?["violations"]?[0]?["check"]?.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ABatchNamingOnePropertyTwiceIsRefusedAsAConflictWhetherOrNotItIsADryRun(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate",
                new JObject { ["TemplateType"] = "Trn.View", ["WWPTemplate_Type"] = "Trn.View" }, dryRun: dryRun));

            Assert.Equal("PropertyAliasConflict", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AValidBatchIsNotRefusedByTheTemplateChecks()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate",
                new JObject { ["TemplateType"] = "Trn.View", ["TemplateXml"] = "<view />" }));

            Assert.NotEqual("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.NotEqual("PropertyAliasConflict", json["error"]?["code"]?.ToString());
            Assert.Equal(new[] { ("WWPTemplate_Type", "Trn.View"), ("WWPTemplate_TemplateXml", "<view />") }, service.Validations);
            Assert.Empty(service.Writes);
        }

        [Fact]
        public void ABatchOnAnotherObjectTypeIsNotCheckedAsATemplate()
        {
            var service = new FakeTemplateTarget(OtherObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperties("SynthObject", new JObject { ["TemplateType"] = "" }));

            Assert.NotEqual("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal(new[] { ("TemplateType", "") }, service.Validations);
        }

        [Fact]
        public void ADryRunOfAValidAliasIsPreviewedWithTheSdkNameAndTheValueChecked()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<view />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", new JObject { ["TemplateType"] = "Trn.View" }, dryRun: true));
            JObject preview = json["result"] as JObject;

            Assert.Equal("DryRun", json["code"]?.ToString());
            Assert.Equal("WWPTemplate_Type", preview?["nativeNames"]?["TemplateType"]?.ToString());
            Assert.Equal("Trn.View", preview?["requested"]?["TemplateType"]?.ToString());
            Assert.Equal("Trn.Selection", preview?["before"]?["TemplateType"]?.ToString());
            Assert.Equal("v-current", preview?["versionToken"]?.ToString());
            Assert.False(preview?["persisted"]?.ToObject<bool>() ?? true);
            Assert.Equal(new[] { ("WWPTemplate_Type", "Trn.View") }, service.Validations);
            Assert.Empty(service.Writes);
        }

        [Fact]
        public void ADryRunOfAnUnknownTypeIsRefusedBeforeAnyStoredValueIsChecked()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", new JObject { ["TemplateType"] = "Trn.Panel" }, dryRun: true));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Empty(service.Validations);
        }

        [Fact]
        public void ASingleSetOfAValidAliasReachesTheCheckWithTheSdkNameAndTheValue()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<view />");

            JObject json = JObject.Parse(service.SetProperty("SynthTemplate", "TemplateType", "Trn.View"));

            Assert.NotEqual("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal(new[] { ("WWPTemplate_Type", "Trn.View") }, service.Validations);
        }

        [Fact]
        public void AStaleVersionIsReportedAsAConflictBeforeTheTemplateChecksRun()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Transaction", "<transaction />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", new JObject { ["TemplateXml"] = "<selection />" },
                expectedVersion: "v-old", dryRun: true));

            Assert.Equal("VersionConflict", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void TheSameChangeWithTheCurrentVersionIsCheckedAndRefused()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Transaction", "<transaction />");

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", new JObject { ["TemplateXml"] = "<selection />" },
                expectedVersion: "v-current", dryRun: true));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("RootMismatch", json["error"]?["violations"]?[0]?["check"]?.ToString());
        }

        [Fact]
        public void ADirectWriteOfAnEmptyTypeIsRefusedBeforeAnyValueIsWritten()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            WwpTemplateRefusedException refusal = Assert.Throws<WwpTemplateRefusedException>(() =>
                service.ApplyPropertiesDirect(service.Target, new JObject { ["WWPTemplate_Type"] = "" }));

            Assert.Equal("WwpTemplateInvalid", refusal.Wiring.RefusalCode);
            Assert.Equal("TypeEmpty", refusal.Wiring.Violations[0]["check"]?.ToString());
            Assert.Empty(service.Writes);
        }

        [Fact]
        public void ADirectWriteWritesTheSdkNameOfAnAliasWithTheValueSent()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", "<selection />");

            service.ApplyPropertiesDirect(service.Target, new JObject { ["TemplateType"] = "Trn.View", ["TemplateXml"] = "<view />" });

            Assert.Equal(new[] { ("WWPTemplate_Type", "Trn.View"), ("WWPTemplate_TemplateXml", "<view />") }, service.Writes);
        }

        [Fact]
        public void ADirectWriteOnAnotherObjectTypeWritesTheNameAsItIs()
        {
            var service = new FakeTemplateTarget(OtherObjectType, "Trn.Selection", "<selection />");

            service.ApplyPropertiesDirect(service.Target, new JObject { ["TemplateType"] = "" });

            Assert.Equal(new[] { ("TemplateType", "") }, service.Writes);
        }

        private const string StoredXml = "<selection Name=\"SynthTemplate\" instanceType=\"WebPanel\"><table name=\"TableMain\" class=\"SynthOld\" /></selection>";
        private const string SelfClosingXml = "<selection Name=\"SynthTemplate\" />";

        private static JObject PatchOf(string find, string replace)
            => new JObject { ["TemplateXml"] = new JObject { ["find"] = find, ["replace"] = replace } };

        [Theory]
        [InlineData("wwptemplate_templatexml")]
        [InlineData(" WWPTemplate_TemplateXml ")]
        public void APatchUnderAnotherSpellingOfTheSdkNameIsCheckedAsTheXmlAndNeverWritten(string name)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);
            var batch = new JObject { [name] = new JObject { ["find"] = "<table name=\"TableMain\" class=\"SynthOld\" />", ["replace"] = "<table name=\"TableMain\"" } };

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", batch, dryRun: false));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("XmlMalformed", json["error"]?["violations"]?[0]?["check"]?.ToString());
            Assert.Empty(service.Writes);
        }

        [Theory]
        [InlineData("wwptemplate_templatexml")]
        [InlineData(" WWPTemplate_TemplateXml ")]
        public void APatchUnderAnotherSpellingOfTheSdkNameIsPreviewedUnderTheSdkName(string name)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);
            var batch = new JObject { [name] = new JObject { ["find"] = "class=\"SynthOld\"", ["replace"] = "class=\"SynthNew\"" } };

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", batch, dryRun: true));

            Assert.Equal("DryRun", json["code"]?.ToString());
            var validated = Assert.Single(service.Validations);
            Assert.Equal("WWPTemplate_TemplateXml", validated.Name);
            Assert.Contains("class=\"SynthNew\"", validated.Value);
        }

        [Theory]
        [InlineData("TemplateXml")]
        [InlineData("WWPTemplate_TemplateXml")]
        public void ADryRunOfATemplateXmlPatchPreviewsTheSummaryAndNeitherTheXmlNorItsOldValue(string name)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);
            var batch = new JObject { [name] = new JObject { ["find"] = "class=\"SynthOld\"", ["replace"] = "class=\"SynthNew\"" } };

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", batch, dryRun: true));
            JObject preview = json["result"] as JObject;

            Assert.Equal("DryRun", json["code"]?.ToString());
            Assert.Equal(1, preview?["patch"]?[name]?["occurrences"]?.ToObject<int>());
            Assert.Equal(1, preview?["patch"]?[name]?["replaced"]?.ToObject<int>());
            Assert.Null(preview?["before"]?[name]);
            Assert.Null(preview?["requested"]?[name]);
            Assert.Equal("v-current", preview?["versionToken"]?.ToString());
            Assert.False(preview?["persisted"]?.ToObject<bool>() ?? true);
            var validated = Assert.Single(service.Validations);
            Assert.Equal("WWPTemplate_TemplateXml", validated.Name);
            Assert.Contains("class=\"SynthNew\"", validated.Value);
            Assert.Empty(service.Writes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APatchThatBreaksTheXmlIsRefusedWithTheSameVerdictInBothModes(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate",
                PatchOf("<table name=\"TableMain\" class=\"SynthOld\" />", "<table name=\"TableMain\""), dryRun: dryRun));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("XmlMalformed", json["error"]?["violations"]?[0]?["check"]?.ToString());
            Assert.Empty(service.Writes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APatchThatChangesTheRootIsRefusedAsARootMismatchInBothModes(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", SelfClosingXml);

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", PatchOf("<selection", "<view"), dryRun: dryRun));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("RootMismatch", json["error"]?["violations"]?[0]?["check"]?.ToString());
            Assert.Empty(service.Writes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APatchThatFindsNothingIsRefusedWithTheReadNextStepInBothModes(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", PatchOf("<table name=\"Absent\"", "<table />"), dryRun: dryRun));

            Assert.Equal("NoMatch", json["error"]?["code"]?.ToString());
            Assert.Equal("read_template_xml", json["suggested_next_step"]?["action"]?.ToString());
            Assert.Empty(service.Writes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APatchGivesTheSameRefusalAsTheWholeXmlItProduces(bool dryRun)
        {
            // Each refused result is checked twice: as a patch of the stored XML, and as the XML the patch produces, sent whole.
            var cases = new[]
            {
                (Stored: StoredXml, Find: "<table name=\"TableMain\" class=\"SynthOld\" />", Replace: "<table name=\"TableMain\""),
                (Stored: SelfClosingXml, Find: "<selection", Replace: "<view")
            };
            foreach (var (stored, find, replace) in cases)
            {
                var byPatch = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", stored);
                var byWhole = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", stored);
                string wholeXml = stored.Replace(find, replace);

                JObject patched = JObject.Parse(byPatch.SetProperties("SynthTemplate", PatchOf(find, replace), dryRun: dryRun));
                JObject whole = JObject.Parse(byWhole.SetProperties("SynthTemplate", new JObject { ["TemplateXml"] = wholeXml }, dryRun: dryRun));

                Assert.Equal("WwpTemplateInvalid", patched["error"]?["code"]?.ToString());
                Assert.Equal(whole["error"]?["code"]?.ToString(), patched["error"]?["code"]?.ToString());
                Assert.Equal(whole["error"]?["violations"]?[0]?["check"]?.ToString(), patched["error"]?["violations"]?[0]?["check"]?.ToString());
                Assert.Empty(byPatch.Writes);
            }
        }

        [Fact]
        public void APatchAndATypeChangeInOneBatchAreCheckedTogetherAgainstThePatchedXml()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", SelfClosingXml);
            var batch = PatchOf("<selection", "<view");
            batch["TemplateType"] = "Trn.View";

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", batch, dryRun: true));

            Assert.Equal("DryRun", json["code"]?.ToString());
            Assert.Equal(new[] { ("WWPTemplate_TemplateXml", "<view Name=\"SynthTemplate\" />"), ("WWPTemplate_Type", "Trn.View") },
                service.Validations);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APatchThatChangesNothingWritesNothingInEitherMode(bool dryRun)
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);

            JObject json = JObject.Parse(service.SetProperties("SynthTemplate", PatchOf("class=\"SynthOld\"", "class=\"SynthOld\""), dryRun: dryRun));

            Assert.Equal("WriteNoChange", json["code"]?.ToString());
            Assert.Empty(service.Validations);
            Assert.Empty(service.Writes);
        }

        [Fact]
        public void APatchOnAnotherObjectTypeIsNotAppliedAndItsValueIsCheckedAsText()
        {
            var service = new FakeTemplateTarget(OtherObjectType, "Trn.Selection", StoredXml);

            JObject json = JObject.Parse(service.SetProperties("SynthObject", PatchOf("class", "klass"), dryRun: true));

            Assert.Equal("DryRun", json["code"]?.ToString());
            Assert.Null(json["result"]?["patch"]);
            var validated = Assert.Single(service.Validations);
            Assert.Equal("TemplateXml", validated.Name);
            Assert.StartsWith("{", validated.Value);
        }

        [Fact]
        public void ASingleSetOfAPatchShapedValueIsTextAndNotPatched()
        {
            var service = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);

            JObject json = JObject.Parse(service.SetProperty("SynthTemplate", "TemplateXml", "{\"find\":\"class\",\"replace\":\"klass\"}"));

            Assert.Equal("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
            Assert.Equal("XmlMalformed", json["error"]?["violations"]?[0]?["check"]?.ToString());
        }

        [Fact]
        public void APatchTakesTheFreshReadOfTheObjectAndAPlainWriteDoesNot()
        {
            var patched = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);
            JObject.Parse(patched.SetProperties("SynthTemplate", PatchOf("<table name=\"Absent\"", "<table />")));
            Assert.Equal(1, patched.FreshReads);
            Assert.Empty(patched.Writes);

            var plain = new FakeTemplateTarget(WwpTemplateXml.ObjectType, "Trn.Selection", StoredXml);
            JObject.Parse(plain.SetProperties("SynthTemplate", new JObject { ["TemplateType"] = "Trn.Panel" }));
            Assert.Equal(0, plain.FreshReads);
        }

        // A PropertyService whose SDK steps answer from fixed values: the target is an uninitialized SDK object of a
        // type that is not a template, unless the test says otherwise, and the stored template properties are the test's.
        private sealed class FakeTemplateTarget : PropertyService
        {
            private readonly Guid _objectType;
            private readonly string _cacheKey = Guid.NewGuid().ToString();

            // What the production path asked of the SDK, in order: each value checked, and each value written.
            internal List<(string Name, string Value)> Validations { get; } = new List<(string Name, string Value)>();

            internal List<(string Name, string Value)> Writes { get; } = new List<(string Name, string Value)>();
            private readonly Dictionary<string, string> _stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal FakeTemplateTarget(Guid objectType, string storedType, string storedXml)
                : base(null)
            {
                _objectType = objectType;
                _stored["WWPTemplate_Type"] = storedType;
                _stored["WWPTemplate_TemplateXml"] = storedXml;
                Target = (Artech.Architecture.Common.Objects.KBObject)FormatterServices.GetUninitializedObject(
                    typeof(Artech.Genexus.Common.Objects.Report));
            }

            internal Artech.Architecture.Common.Objects.KBObject Target { get; }

            protected internal override Artech.Architecture.Common.Objects.KBObject FindTarget(string target, string typeFilter) => Target;

            // How many fresh reads the call took, one for each time the production path asked for an independent read.
            internal int FreshReads { get; private set; }

            protected internal override Artech.Architecture.Common.Objects.KBObject FindFreshTarget(Artech.Architecture.Common.Objects.KBObject obj)
            {
                FreshReads++;
                return obj;
            }

            protected internal override Guid TargetTypeOf(Artech.Architecture.Common.Objects.KBObject obj) => _objectType;

            protected internal override bool IsWebPanelTarget(Artech.Architecture.Common.Objects.KBObject obj) => false;

            // A key of its own, so that cached properties never cross between two fake targets.
            protected internal override string TargetKey(Artech.Architecture.Common.Objects.KBObject obj, string controlName)
                => _cacheKey + "|" + (controlName ?? string.Empty);

            protected internal override string ReadStoredProperty(Artech.Architecture.Common.Objects.KBObject obj, string nativeName, string controlName)
                => _stored.TryGetValue(nativeName, out string value) ? value : null;

            // Like the SDK serializer: every stored property, or only the ones asked for when names are given.
            protected internal override JObject SerializeTarget(Artech.Architecture.Common.Objects.KBObject obj, object container,
                string[] selectedNames, string controlName)
            {
                var properties = new JArray();
                foreach (var item in _stored.Where(s => selectedNames.Length == 0 || selectedNames.Contains(s.Key, StringComparer.OrdinalIgnoreCase)))
                    properties.Add(new JObject { ["name"] = item.Key, ["value"] = item.Value ?? string.Empty, ["type"] = "System.String", ["readOnly"] = false });
                return new JObject { ["properties"] = properties };
            }

            protected internal override object ReadTextMode(Artech.Architecture.Common.Objects.KBObject obj) => null;

            protected internal override string VersionOf(Artech.Architecture.Common.Objects.KBObject obj) => "v-current";

            protected internal override bool IsReferenceProperty(object container, string nativeName) => false;

            // An empty snapshot: the preview reports the parts it keeps, and this target keeps none.
            internal override ObjectMoveSnapshot CaptureSnapshot(Artech.Architecture.Common.Objects.KBObject obj)
            {
                ConstructorInfo ctor = typeof(ObjectMoveSnapshot).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                return (ObjectMoveSnapshot)ctor.Invoke(new[] { Activator.CreateInstance(ctor.GetParameters()[0].ParameterType), new byte[0], new byte[0] });
            }

            protected internal override object ResolvePropertyContainer(Artech.Architecture.Common.Objects.KBObject obj, string nativeName, string controlName)
                => null;

            protected internal override string ValidatePropertyValue(object container, string nativeName, string value, bool textMode)
            {
                Validations.Add((nativeName, value));
                return null;
            }

            protected internal override void WritePropertyValue(object container, string nativeName, string value, string controlName,
                Artech.Architecture.Common.Objects.KBObject obj)
            {
                Writes.Add((nativeName, value));
            }
        }
    }
}
