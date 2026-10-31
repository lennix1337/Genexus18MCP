using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
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
        }

        [Fact]
        public void ABatchOnAnotherObjectTypeIsNotCheckedAsATemplate()
        {
            var service = new FakeTemplateTarget(OtherObjectType, "Trn.Selection", "<selection />");

            JObject json = JObject.Parse(service.SetProperties("SynthObject", new JObject { ["TemplateType"] = "" }));

            Assert.NotEqual("WwpTemplateInvalid", json["error"]?["code"]?.ToString());
        }

        // A PropertyService whose SDK steps answer from fixed values: the target is an uninitialized SDK object of a
        // type that is not a template, unless the test says otherwise, and the stored template properties are the test's.
        private sealed class FakeTemplateTarget : PropertyService
        {
            private readonly Guid _objectType;
            private readonly string _cacheKey = Guid.NewGuid().ToString();
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

            protected internal override Artech.Architecture.Common.Objects.KBObject FindFreshTarget(Artech.Architecture.Common.Objects.KBObject obj) => obj;

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
        }
    }
}
