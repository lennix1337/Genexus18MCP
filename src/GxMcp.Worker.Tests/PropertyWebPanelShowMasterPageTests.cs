using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // ShowMasterPageWhenPopUp of a web panel. The SDK lists the flag as SHOWMASTERPAGE_POPUP. genexus_properties also answers
    // and writes it under its official name, on a web panel at object level only. The fake target answers the SDK steps from a
    // fixed property list, so the production paths run without a knowledge base.
    public sealed class PropertyWebPanelShowMasterPageTests
    {
        private const string Official = "ShowMasterPageWhenPopUp";
        private const string SdkKey = "SHOWMASTERPAGE_POPUP";

        [Theory]
        [InlineData("ShowMasterPageWhenPopUp")]
        [InlineData("  showmasterpagewhenpopup ")]
        public void WebPanelSdkNameOf_MapsTheOfficialNameToItsSdkKeyWhateverItsCase(string name)
            => Assert.Equal(SdkKey, PropertyService.WebPanelSdkNameOf(name));

        [Theory]
        [InlineData("SHOWMASTERPAGE_POPUP")]
        [InlineData("MainProgram")]
        [InlineData("Description")]
        [InlineData("")]
        [InlineData(null)]
        public void WebPanelSdkNameOf_LeavesEveryOtherNameUnmapped(string name)
            => Assert.Null(PropertyService.WebPanelSdkNameOf(name));

        [Theory]
        [InlineData("PropertyNotFound: 'X' is not exposed by this object or control.", "PropertyNotFound")]
        [InlineData("PropertyReadOnly: 'X' is read-only for this object or control.", "PropertyReadOnly")]
        [InlineData("InvalidPropertyValue: 'maybe' cannot be converted to Boolean for 'X'.", "InvalidPropertyValue")]
        [InlineData(null, "InvalidPropertyValue")]
        public void ValidationErrorCode_AnswersTheCodeOfEachRefusal(string validation, string code)
            => Assert.Equal(code, PropertyService.ValidationErrorCode(validation));

        [Fact]
        public void WriteName_SendsTheSdkKeyWhenTheSdkListsOnlyTheKey()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            Assert.Equal(SdkKey, panel.NativeWebPanelAliasName(panel.Target, null, Official));
        }

        [Fact]
        public void WriteName_KeepsTheNameWhenTheSdkListsIt()
        {
            var panel = new FakeWebPanel().Lists(Official, false).Lists(SdkKey, false);

            Assert.Equal(Official, panel.NativeWebPanelAliasName(panel.Target, null, Official));
        }

        [Fact]
        public void WriteName_KeepsTheNameOnAControlAndOnAnotherObjectType()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);
            var transaction = new FakeWebPanel().Lists(SdkKey, false);
            transaction.IsAWebPanel = false;

            Assert.Equal(Official, panel.NativeWebPanelAliasName(panel.Target, "Grid1", Official));
            Assert.Equal(Official, transaction.NativeWebPanelAliasName(transaction.Target, null, Official));
        }

        [Fact]
        public void WriteName_KeepsEveryOtherName()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            Assert.Equal(SdkKey, panel.NativeWebPanelAliasName(panel.Target, null, SdkKey));
            Assert.Equal("Description", panel.NativeWebPanelAliasName(panel.Target, null, "Description"));
        }

        [Fact]
        public void ASingleSetOfTheOfficialNameValidatesTheSdkKey()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            JObject json = JObject.Parse(panel.SetProperty("SynthPanel", Official, "maybe"));

            Assert.Equal("InvalidPropertyValue", json["error"]?["code"]?.ToString());
            Assert.Equal(new[] { SdkKey }, panel.ValidatedNames);
        }

        [Fact]
        public void ASingleSetOfAnUnlistedNameIsNotFound()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            JObject json = JObject.Parse(panel.SetProperty("SynthPanel", "NoSuchProperty", "True"));

            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
        }

        [Theory]
        [InlineData("NoSuchProperty", "True", "PropertyNotFound")]
        [InlineData("SomeReadOnly", "x", "PropertyReadOnly")]
        [InlineData(Official, "maybe", "InvalidPropertyValue")]
        public void ABatchRefusesEachValueWithTheCodeOfTheSingleSet(string name, string value, string code)
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false).Lists("SomeReadOnly", "x", readOnly: true);

            JObject json = JObject.Parse(panel.SetProperties("SynthPanel", new JObject { [name] = value }, dryRun: true));

            Assert.Equal(code, json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ADryRunOfTheOfficialNameReportsTheSdkKeyInNativeNames()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            JObject json = JObject.Parse(panel.SetProperties("SynthPanel", new JObject { [Official] = "True" }, dryRun: true));

            Assert.Equal("DryRun", json["code"]?.ToString());
            Assert.Equal(SdkKey, json["result"]?["nativeNames"]?[Official]?.ToString());
            Assert.Equal("True", json["result"]?["requested"]?[Official]?.ToString());
            Assert.Equal(new[] { SdkKey }, panel.ValidatedNames);
        }

        [Fact]
        public void ADryRunOnAnotherObjectTypeKeepsTheOfficialNameAndIsNotFound()
        {
            var transaction = new FakeWebPanel().Lists(SdkKey, false);
            transaction.IsAWebPanel = false;

            JObject json = JObject.Parse(transaction.SetProperties("SynthTrn", new JObject { [Official] = "True" }, dryRun: true));

            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal(new[] { Official }, transaction.ValidatedNames);
        }

        [Fact]
        public void GetAnswersTheOfficialNameFromTheSdkKeysItem()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            JToken property = JObject.Parse(panel.GetProperties("SynthPanel", propertyName: Official))["result"]?["property"];

            Assert.NotNull(property);
            Assert.Equal(Official, property["name"]?.ToString());
            Assert.Equal(SdkKey, property["nativeName"]?.ToString());
            Assert.Equal("False", property["value"]?.ToString());
        }

        [Fact]
        public void GetBySdkKeyAnswersTheItemUnderItsOwnNameWithoutANativeName()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            JToken property = JObject.Parse(panel.GetProperties("SynthPanel", propertyName: SdkKey))["result"]?["property"];

            Assert.NotNull(property);
            Assert.Equal(SdkKey, property["name"]?.ToString());
            Assert.Null(property["nativeName"]);
        }

        [Fact]
        public void GetOfAnotherObjectTypeDoesNotAnswerTheOfficialName()
        {
            var transaction = new FakeWebPanel().Lists(SdkKey, false);
            transaction.IsAWebPanel = false;

            JObject json = JObject.Parse(transaction.GetProperties("SynthTrn", propertyName: Official));

            Assert.Equal("PropertyNotFound", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void GetOfTheWholeWebPanelListsTheSdkKeyUnderItsOwnName()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false).Lists("Description", "x");

            JArray properties = (JArray)JObject.Parse(panel.GetProperties("SynthPanel"))["result"]?["properties"];

            Assert.Contains(properties, p => string.Equals(p["name"]?.ToString(), SdkKey, StringComparison.Ordinal) && p["nativeName"] == null);
            Assert.DoesNotContain(properties, p => string.Equals(p["name"]?.ToString(), Official, StringComparison.Ordinal));
        }

        [Fact]
        public void GetWithSeveralNamesAnswersTheOfficialNameInItsOwnItem()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false).Lists("Description", "x");

            JObject result = (JObject)JObject.Parse(panel.GetProperties("SynthPanel", propertyNames: new[] { Official, "Description" }))["result"];

            Assert.Equal("False", result["values"]?[Official]?.ToString());
            Assert.Equal(2, ((JArray)result["properties"]).Count);
            Assert.Null(result["missingProperties"]);
        }

        [Fact]
        public void TheReconciliationReadAnswersTheOfficialNameAsAnAuthoritativeRead()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);

            JObject result = (JObject)JObject.Parse(panel.GetProperties("SynthPanel", propertyName: Official, reconcileTimedOutWrite: true))["result"];

            Assert.Equal(true, (bool?)result["authoritativeRead"]);
            Assert.Equal(Official, result["property"]?["name"]?.ToString());
            Assert.Equal(SdkKey, result["property"]?["nativeName"]?.ToString());
        }

        [Fact]
        public void SelectedReadNamesTheOfficialNameAndCarriesTheSdkKey()
        {
            var sdk = new SdkContainer();
            sdk.Properties.Add(new SdkProperty { Name = SdkKey, Definition = new SdkDefinition { Type = typeof(bool) }, Value = true });

            JArray properties = (JArray)PropertyService.SerializeSelectedProperties(sdk, null, new[] { Official }, webPanel: true)["properties"];

            Assert.Single(properties);
            Assert.Equal(Official, properties[0]["name"]?.ToString());
            Assert.Equal(SdkKey, properties[0]["nativeName"]?.ToString());
            Assert.Equal("True", properties[0]["value"]?.ToString());
        }

        [Fact]
        public void TheReconciliationSerializerAnswersTheOfficialNameOnlyOnAWebPanel()
        {
            var panel = new FakeWebPanel().Lists(SdkKey, false);
            var transaction = new FakeWebPanel().Lists(SdkKey, false);
            transaction.IsAWebPanel = false;

            JArray answered = (JArray)panel.SerializeWithProduction(new[] { Official })["properties"];
            JArray unanswered = (JArray)transaction.SerializeWithProduction(new[] { Official })["properties"];

            Assert.Single(answered);
            Assert.Equal(Official, answered[0]["name"]?.ToString());
            Assert.Equal(SdkKey, answered[0]["nativeName"]?.ToString());
            Assert.Empty(unanswered);
        }

        [Fact]
        public void SelectedReadOfAnotherObjectTypeLeavesTheOfficialNameUnanswered()
        {
            var sdk = new SdkContainer();
            sdk.Properties.Add(new SdkProperty { Name = SdkKey, Definition = new SdkDefinition { Type = typeof(bool) }, Value = true });

            JArray properties = (JArray)PropertyService.SerializeSelectedProperties(sdk, null, new[] { Official }, webPanel: false)["properties"];

            Assert.Empty(properties);
        }

        public sealed class SdkContainer
        {
            public List<SdkProperty> Properties { get; } = new List<SdkProperty>();
        }

        public sealed class SdkProperty
        {
            public string Name { get; set; }
            public SdkDefinition Definition { get; set; }
            public object Value { get; set; }
        }

        public sealed class SdkDefinition
        {
            public Type Type { get; set; }
            public bool ReadOnly { get; set; }
        }

        // A web panel (or another object type) whose SDK steps answer from its own property list. ValidatePropertyValue runs the
        // production check on that list; ValidatedNames keeps the SDK names it was asked about, in order.
        private sealed class FakeWebPanel : PropertyService
        {
            private readonly SdkContainer _sdk = new SdkContainer();
            private readonly string _cacheKey = Guid.NewGuid().ToString();

            internal FakeWebPanel() : base(null)
            {
                Target = (KBObject)FormatterServices.GetUninitializedObject(typeof(Artech.Genexus.Common.Objects.Report));
            }

            internal KBObject Target { get; }

            internal bool IsAWebPanel { get; set; } = true;

            internal List<string> ValidatedNames { get; } = new List<string>();

            internal FakeWebPanel Lists(string name, object value, bool readOnly = false)
            {
                _sdk.Properties.Add(new SdkProperty
                {
                    Name = name,
                    Definition = new SdkDefinition { Type = value?.GetType() ?? typeof(string), ReadOnly = readOnly },
                    Value = value
                });
                return this;
            }

            protected internal override KBObject FindTarget(string target, string typeFilter) => Target;

            protected internal override KBObject FindFreshTarget(KBObject obj) => obj;

            protected internal override Guid TargetTypeOf(KBObject obj) => Guid.Empty;

            protected internal override bool IsWebPanelTarget(KBObject obj) => IsAWebPanel;

            // A key of its own, so that cached properties never cross between two fake targets.
            protected internal override string TargetKey(KBObject obj, string controlName) => _cacheKey + "|" + (controlName ?? string.Empty);

            protected internal override bool ObjectListsProperty(KBObject obj, string name) => Find(name) != null;

            protected internal override string ReadStoredProperty(KBObject obj, string nativeName, string controlName)
                => Find(nativeName)?.Value?.ToString();

            // Like the SDK serializer: every listed property, or only the ones asked for when names are given.
            protected internal override JObject SerializeTarget(KBObject obj, object container, string[] selectedNames, string controlName)
            {
                if (selectedNames.Length > 0)
                    return SerializeSelectedProperties(_sdk, null, selectedNames, IsWebPanelTarget(obj));

                var properties = new JArray();
                foreach (SdkProperty p in _sdk.Properties)
                    properties.Add(new JObject { ["name"] = p.Name, ["value"] = p.Value?.ToString() ?? string.Empty, ["readOnly"] = p.Definition.ReadOnly });
                return new JObject { ["properties"] = properties };
            }

            // The production serializer of a reconciliation read, reached through the base class rather than this fake's listing.
            internal JObject SerializeWithProduction(string[] selectedNames) => base.SerializeTarget(Target, _sdk, selectedNames, null);

            protected internal override object ReadTextMode(KBObject obj) => null;

            protected internal override string VersionOf(KBObject obj) => "v-current";

            protected internal override bool IsReferenceProperty(object container, string nativeName) => false;

            protected internal override object ResolvePropertyContainer(KBObject obj, string nativeName, string controlName) => null;

            protected internal override string ValidatePropertyValue(object container, string nativeName, string value, bool textMode)
            {
                ValidatedNames.Add(nativeName);
                return ValidatePropertyWrite(_sdk, nativeName, value);
            }

            // An empty snapshot: the preview reports the parts it keeps, and this target keeps none.
            internal override ObjectMoveSnapshot CaptureSnapshot(KBObject obj)
            {
                ConstructorInfo ctor = typeof(ObjectMoveSnapshot).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                return (ObjectMoveSnapshot)ctor.Invoke(new[] { Activator.CreateInstance(ctor.GetParameters()[0].ParameterType), new byte[0], new byte[0] });
            }

            private SdkProperty Find(string name)
                => _sdk.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
