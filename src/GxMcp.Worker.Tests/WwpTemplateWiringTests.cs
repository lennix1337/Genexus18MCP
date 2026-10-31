using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class WwpTemplateWiringTests
    {
        private static readonly Guid OtherObjectType = new Guid("3f0c2a5e-9b1d-4c7a-8e61-0d2b4f9a7c15");

        [Fact]
        public void AWriteOnAnotherObjectTypeKeepsEveryNameAndRefusesNothing()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(OtherObjectType,
                Pairs(("TemplateType", ""), ("WWPTemplate_Type", "Trn.Panel")),
                _ => throw new InvalidOperationException("no stored value is read for another object type"));

            Assert.Null(wiring.RefusalCode);
            Assert.Equal("TemplateType", wiring.NativeNames["TemplateType"]);
            Assert.Equal("WWPTemplate_Type", wiring.NativeNames["WWPTemplate_Type"]);
        }

        [Fact]
        public void AnAliasIsTranslatedToItsSdkNameAndItsValueIsKeptUnderIt()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "Trn.View")), _ => "<view />");

            Assert.Null(wiring.RefusalCode);
            Assert.Equal("WWPTemplate_Type", wiring.NativeNames["TemplateType"]);
            Assert.Equal("Trn.View", wiring.NativeValues["WWPTemplate_Type"]);
        }

        [Fact]
        public void AnAliasAndTheSdkNameOfTheSamePropertyAreRefusedAsAConflict()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "Trn.View"), ("WWPTemplate_Type", "Trn.View")), _ => "<view />");

            Assert.Equal("PropertyAliasConflict", wiring.RefusalCode);
            Assert.Contains("WWPTemplate_Type", wiring.RefusalMessage);
            Assert.Null(wiring.Violations);
        }

        [Fact]
        public void ANameThatIsNotAnAliasDoesNotTriggerTheTemplateChecks()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("WWPTemplate_Category", "Lists")),
                _ => throw new InvalidOperationException("no stored value is read for a property that is not checked"));

            Assert.Null(wiring.RefusalCode);
        }

        [Fact]
        public void AnEmptyTypeSentAloneIsRefusedWithTheTypeCheckAndTheWorkWithPlus162Hint()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "")), _ => "<selection />");

            Assert.Equal("WwpTemplateInvalid", wiring.RefusalCode);
            Assert.Equal("TypeEmpty", FirstCheck(wiring));
            Assert.Contains("WorkWithPlus 16.2", wiring.RefusalHint);
        }

        [Fact]
        public void AnUnknownTypeNamesTheKnownWorkWithPlus162Types()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "Trn.Panel")), _ => "<selection />");

            Assert.Equal("TypeUnknown", FirstCheck(wiring));
            Assert.Contains("known for WorkWithPlus 16.2", wiring.RefusalMessage);
        }

        [Fact]
        public void AChangeOfTheXmlIsCheckedAgainstTheStoredType()
        {
            var stored = new Dictionary<string, string> { ["WWPTemplate_Type"] = "Trn.Transaction" };

            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateXml", "<selection />")), name => stored[name]);

            Assert.Equal("WwpTemplateInvalid", wiring.RefusalCode);
            Assert.Equal("RootMismatch", FirstCheck(wiring));
        }

        [Fact]
        public void AChangeOfTheTypeIsCheckedAgainstTheStoredXml()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "Trn.View")), name => name == "WWPTemplate_TemplateXml" ? "<selection />" : null);

            Assert.Equal("RootMismatch", FirstCheck(wiring));
        }

        [Fact]
        public void AChangeOfTheTypeLeavesATemplateWithNoStoredXmlRefused()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "Trn.Selection")), _ => null);

            Assert.Equal("XmlEmpty", FirstCheck(wiring));
        }

        [Fact]
        public void AValidChangeIsAllowedWithItsSdkNames()
        {
            WriteWiring wiring = WwpTemplateWiring.Write(WwpTemplateXml.ObjectType,
                Pairs(("TemplateType", "Trn.View"), ("TemplateXml", "<view />")),
                _ => throw new InvalidOperationException("both values are sent, so none is read"));

            Assert.Null(wiring.RefusalCode);
            Assert.Equal("Trn.View", wiring.NativeValues["WWPTemplate_Type"]);
            Assert.Equal("<view />", wiring.NativeValues["WWPTemplate_TemplateXml"]);
        }

        private static IEnumerable<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs)
            => pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value));

        private static string FirstCheck(WriteWiring wiring) => wiring.Violations?[0]?["check"]?.ToString();
    }
}
