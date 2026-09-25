using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class VariableDimensionSupportTests
    {
        private sealed class FakePropertyBag
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public object GetPropertyValue(string name)
            {
                object value;
                return _values.TryGetValue(name ?? string.Empty, out value) ? value : null;
            }

            public void SetPropertyValue(string name, object value)
            {
                _values[name ?? string.Empty] = value;
            }
        }

        private static WriteService BuildIsolatedWriteService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            return new WriteService(new ObjectService(kb, build));
        }

        [Fact]
        public void WorkerAddModifyBatch_RejectDimensionRequestsBeforeMutation()
        {
            WriteService service;
            try { service = BuildIsolatedWriteService(); }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var add = JObject.Parse(service.AddVariable(
                "Missing", "Values", "Character(10)", dimensions: 1,
                dimensionSizes: new JArray(10), collection: true));
            Assert.Equal("CollectionDimensionConflict", add["error"]?["code"]?.ToString());

            var modify = JObject.Parse(service.ModifyVariable(
                "Missing", "Values", "Character(10)", dimensions: 2,
                dimensionSizes: new JArray(10)));
            Assert.Equal("InvalidVariableDimensions", modify["error"]?["code"]?.ToString());

            var batch = JObject.Parse(service.AddVariables("Missing", new JArray(new JObject
            {
                ["varName"] = "Values",
                ["dimensions"] = 1,
                ["dimensionSizes"] = new JArray(0)
            })));
            Assert.Equal("InvalidVariableDimensions", batch["error"]?["code"]?.ToString());
        }

        [Fact]
        public void TryValidate_AcceptsVectorAndMatrixShapes()
        {
            string code, message, hint;
            JObject extra;

            Assert.True(VariableDimensionSupport.TryValidate(1, new JArray(999), false,
                out code, out message, out hint, out extra));
            Assert.True(VariableDimensionSupport.TryValidate(2, new JArray(10, 20), null,
                out code, out message, out hint, out extra));
        }

        [Fact]
        public void TryValidate_RejectsCollectionAndMalformedSizes()
        {
            string code, message, hint;
            JObject extra;

            Assert.False(VariableDimensionSupport.TryValidate(1, new JArray(10), true,
                out code, out message, out hint, out extra));
            Assert.Equal("CollectionDimensionConflict", code);

            Assert.False(VariableDimensionSupport.TryValidate(1, new JArray(0), null,
                out code, out message, out hint, out extra));
            Assert.Equal("InvalidVariableDimensions", code);

            Assert.False(VariableDimensionSupport.TryValidate(2, new JArray(10), null,
                out code, out message, out hint, out extra));
            Assert.Equal("InvalidVariableDimensions", code);
        }

        [Fact]
        public void TryApply_UsesSdkPropertyBagAndReadsBackBothDimensions()
        {
            var variable = new FakePropertyBag();

            Assert.True(VariableDimensionSupport.TryApply((object)variable, 1,
                new JArray(999), out string error), error);
            VariableDimensionInfo info;
            Assert.True(VariableDimensionSupport.TryRead((object)variable, out info));
            Assert.True(info.IsValid);
            Assert.Equal(1, info.Dimensions);
            Assert.Equal(new[] { 999 }, info.DimensionSizes);

            Assert.True(VariableDimensionSupport.TryApply((object)variable, 2,
                new JArray(4, 5), out error), error);
            Assert.True(VariableDimensionSupport.TryRead((object)variable, out info));
            Assert.Equal(2, info.Dimensions);
            Assert.Equal(new[] { 4, 5 }, info.DimensionSizes);
        }

        private enum DimensionKind
        {
            Scalar = 0,
            Vector = 1,
            Matrix = 2
        }

        private sealed class EnumClrVariable
        {
            public DimensionKind AttNumDim { get; set; }
            public int AttRows { get; set; }
            public int AttCols { get; set; }
        }

        [Fact]
        public void TryApply_UsesEnumClrPropertyWhenPropertyBagIsUnavailable()
        {
            var variable = new EnumClrVariable();

            Assert.True(VariableDimensionSupport.TryApply((object)variable, 2,
                new JArray(6, 8), out string error), error);
            VariableDimensionInfo info;
            Assert.True(VariableDimensionSupport.TryRead((object)variable, out info));
            Assert.Equal(2, info.Dimensions);
            Assert.Equal(new[] { 6, 8 }, info.DimensionSizes);
            Assert.Equal(DimensionKind.Matrix, variable.AttNumDim);
        }

        [Fact]
        public void TryApply_RestoresPreviousDimensionsWhenLaterSizeCannotPersist()
        {
            var variable = new PartialPropertyBag();
            variable.SetPropertyValue("AttNumDim", 1);
            variable.SetPropertyValue("AttRows", 7);

            Assert.False(VariableDimensionSupport.TryApply((object)variable, 2,
                new JArray(4, 5), out string error));
            Assert.Contains("second dimension", error, StringComparison.OrdinalIgnoreCase);

            VariableDimensionInfo info;
            Assert.True(VariableDimensionSupport.TryRead((object)variable, out info));
            Assert.Equal(1, info.Dimensions);
            Assert.Equal(new[] { 7 }, info.DimensionSizes);
        }

        private sealed class PartialPropertyBag
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public object GetPropertyValue(string name)
                => _values.TryGetValue(name ?? string.Empty, out object value) ? value : null;

            public void SetPropertyValue(string name, object value)
            {
                if (name != null && (name.Equals("AttCols", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Columns", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DimensionSize2", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Size2", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The second dimension size is read-only in this test double.");
                _values[name ?? string.Empty] = value;
            }
        }

        [Fact]
        public void TryApply_CanBeClearedBackToScalar()
        {
            var variable = new FakePropertyBag();
            Assert.True(VariableDimensionSupport.TryApply((object)variable, 1,
                new JArray(7), out string error), error);

            Assert.True(VariableDimensionSupport.TryClear((object)variable, out error), error);
            VariableDimensionInfo info;
            Assert.True(VariableDimensionSupport.TryRead((object)variable, out info));
            Assert.Equal(0, info.Dimensions);
        }

        [Fact]
        public void AddMetadata_DisclosesMalformedSdkMetadata()
        {
            var variable = new FakePropertyBag();
            variable.SetPropertyValue("AttNumDim", 1);

            var metadata = new JObject();
            VariableDimensionSupport.AddMetadata(metadata, (object)variable);

            Assert.True(metadata["dimensionsMalformed"]?.ToObject<bool>());
            Assert.NotNull(metadata["dimensionsError"]);
        }

        [Fact]
        public void MissingVariableNames_AcceptsFixedSizeDeclaration()
        {
            var missing = WriteService.MissingVariableNames(
                "&VetItems(999) : Attribute:SomeKey\n&Other : Character(10)",
                new[] { "VetItems", "Other" });

            Assert.Empty(missing);
        }

        [Fact]
        public void VariableDeclarationParser_ParsesFixedSizeVectorAndMatrix()
        {
            Assert.True(VariableDeclarationParser.TryParse(
                "&VetItems(999) : Attribute:SomeKey", out var vector));
            Assert.Equal(1, vector.Dimensions);
            Assert.Equal(new[] { 999 }, vector.DimensionSizes);
            Assert.Equal("Attribute:SomeKey", vector.TypeName);

            Assert.True(VariableDeclarationParser.TryParse(
                "&Grid(10,20) : Character(30) Collection", out var matrix));
            Assert.True(matrix.IsCollection);
            Assert.Equal(2, matrix.Dimensions);
            Assert.Equal(new[] { 10, 20 }, matrix.DimensionSizes);
            Assert.Equal(30, matrix.Length);
        }

        [Fact]
        public void VariableDeclarationParser_RejectsMalformedDimensionText()
        {
            Assert.False(VariableDeclarationParser.TryParse(
                "&Bad(0) : Character(10)", out _));
            Assert.False(VariableDeclarationParser.TryParse(
                "&Bad(1,2,3) : Character(10)", out _));
        }
    }
}
