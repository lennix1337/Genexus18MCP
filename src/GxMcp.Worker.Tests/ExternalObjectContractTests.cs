using System;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Parts.ExternalObject;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ExternalObjectContractTests
    {
        [Theory]
        [InlineData("Source", true)]
        [InlineData("source", true)]
        [InlineData("Structure", true)]
        [InlineData("EXOStructure", true)]
        [InlineData("Documentation", false)]
        [InlineData("Help", false)]
        public void StructureAliasesDoNotSelectDocumentation(string part, bool expected) =>
            Assert.Equal(expected, ExternalObjectContract.IsStructureAlias(part));

        [Theory]
        [InlineData("in", ParameterInOut.In)]
        [InlineData("out", ParameterInOut.Out)]
        [InlineData("InOut", ParameterInOut.InOut)]
        [InlineData(null, ParameterInOut.In)]
        public void DirectionUsesSdkEnum(string input, ParameterInOut expected) =>
            Assert.Equal(expected, ExternalObjectContract.ParseDirection(input));

        [Theory]
        [InlineData("out", ParameterInOut.In, "out")]
        [InlineData("inout", ParameterInOut.In, "inout")]
        [InlineData(null, ParameterInOut.Out, "out")]
        public void StoredProcedureNativeDirectionOverridesGenericSdkGetter(string nativeAccess, ParameterInOut fallback, string expected) =>
            Assert.Equal(expected, ExternalObjectContract.ReadDirection(nativeAccess, fallback));

        [Theory]
        [InlineData("GUID", eDBType.GUID, null, null)]
        [InlineData("Boolean", eDBType.Boolean, null, null)]
        [InlineData("VarChar(128)", eDBType.VARCHAR, 128, 0)]
        [InlineData("VarChar(512)", eDBType.VARCHAR, 512, 0)]
        [InlineData("Numeric(10,2)", eDBType.NUMERIC, 10, 2)]
        public void PrimitiveContractsUseNativeTypesAndDimensions(string input, eDBType expected, int? length, int? decimals)
        {
            Assert.True(ExternalObjectContract.ParseType(input, new JObject(), out var type, out var actualLength, out var actualDecimals));
            Assert.Equal(expected, type); Assert.Equal(length, actualLength); Assert.Equal(decimals, actualDecimals);
        }

        [Theory]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[{\"name\":\"Id\",\"inout\":\"input\"}]}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[{\"type\":\"GUID\"}]}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[{\"name\":\"Id\"},{\"name\":\"id\"}]}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[{\"name\":\"Id\",\"inout\":\"in\",\"direction\":\"out\"}]}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[{\"name\":\"Text\",\"type\":\"VarChar(128)\",\"length\":512}]}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[{\"name\":\"Text\",\"type\":\"VarChar\",\"length\":0}]}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":{}}")]
        [InlineData("{\"name\":\"DoWork\",\"parameters\":[null]}")]
        public void InvalidContractIsRejectedBeforeSdkMutation(string input) =>
            Assert.Throws<ArgumentException>(() => ExternalObjectContract.ValidatePayload(JObject.Parse(input)));

        public sealed class OldStructure { }
        public sealed class GenericItem { public string SerializeToXml() => "<GenericType name='T'/>"; }
        public sealed class NewStructure { public GenericItem[] ExternalGenericTypes { get; } = new[] { new GenericItem() }; }

        [Fact]
        public void GenericTypesExposeAvailabilityAcrossSdkMajors()
        {
            Assert.Empty(ExternalObjectContract.ReadGenericTypes(new OldStructure(), out bool oldAvailable));
            Assert.False(oldAvailable);
            var types = ExternalObjectContract.ReadGenericTypes(new NewStructure(), out bool newAvailable);
            Assert.True(newAvailable);
            Assert.Single(types);
            Assert.Equal("<GenericType name='T'/>", types[0].ToString());
        }

        [Fact]
        public void LegacyPayloadAndExternalTypeMappingRemainAccepted()
        {
            ExternalObjectContract.ValidatePayload(JObject.Parse("{\"name\":\"DoWork\",\"returnType\":\"Vendor.Result\",\"parameters\":[{\"name\":\"Value\",\"type\":\"Vendor.Value\"}]}"));
            Assert.False(ExternalObjectContract.ParseType("Vendor.Value", new JObject(), out _, out _, out _));
        }

        [Fact]
        public void ComparisonIgnoresOnlyPropertyEncodingAndDetectsContractChanges()
        {
            var a = JObject.Parse("{\"name\":\"DoWork\",\"externalName\":\"Sample.DoWork\",\"propertiesXml\":\"before\",\"parameters\":[{\"name\":\"Text\",\"type\":\"VARCHAR\",\"length\":128,\"inout\":\"in\",\"propertiesXml\":\"before\"}]}");
            var b = (JObject)a.DeepClone();
            b["propertiesXml"] = "after"; b["parameters"][0]["propertiesXml"] = "after";
            Assert.True(ExternalObjectContract.SameSignature(a, b));
            b["parameters"][0]["length"] = 512;
            Assert.False(ExternalObjectContract.SameSignature(a, b));
            Assert.NotEqual(ExternalObjectContract.Version(a), ExternalObjectContract.Version(b));
        }

        [Fact]
        public void OutputCaptureKeepsDiagnosticsAcrossClearAndBoundsMemory()
        {
            using (var capture = new TransferDiagnostics(null))
            {
                capture.AddErrorLine("Synthetic import failure"); capture.Clear();
                Assert.Single(capture.Messages);
                for (int i = 0; i < 500; i++) capture.AddWarningLine("warning");
                Assert.Equal(200, capture.Messages.Count); Assert.True(capture.Truncated);
                Assert.False(capture.Available);
            }
        }

        [Fact]
        public void DeclinedImportIsExplicitErrorWithOriginalSdkDiagnostic()
        {
            using (var capture = new TransferDiagnostics(null))
            {
                capture.AddErrorLine("Synthetic conflict");
                var response = JObject.Parse(TransferService.ImportDeclined("sample.xpz", capture));
                Assert.Equal("error", response["status"].ToString());
                Assert.Equal("TransferImportDeclined", response["error"]["code"].ToString());
                Assert.False(response["success"].Value<bool>());
                Assert.False(response["imported"].Value<bool>());
                Assert.Contains("Synthetic conflict", response["sdkDiagnostics"].ToString());
            }
        }
    }
}
