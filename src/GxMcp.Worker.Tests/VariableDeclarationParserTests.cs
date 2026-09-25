using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class VariableDeclarationParserTests
    {
        [Fact]
        public void TryParse_PreservesModuleQualifiedSdtType()
        {
            Assert.True(VariableDeclarationParser.TryParse(
                "&GridState : WWPGridState, WorkWithPlus_Web",
                out var declaration));

            Assert.Equal("GridState", declaration.Name);
            Assert.Equal("WWPGridState, WorkWithPlus_Web", declaration.TypeName);
        }

        [Fact]
        public void TryParse_PreservesVectorAndMatrixDimensions()
        {
            Assert.True(VariableDeclarationParser.TryParse(
                "&Vector(999) : Attribute:SomeKey",
                out var vector));
            Assert.Equal("Vector", vector.Name);
            Assert.Equal(1, vector.Dimensions);
            Assert.Equal(new[] { 999 }, vector.DimensionSizes);

            Assert.True(VariableDeclarationParser.TryParse(
                "&Matrix(10,20) : Numeric(8.2)",
                out var matrix));
            Assert.Equal("Matrix", matrix.Name);
            Assert.Equal("Numeric", matrix.TypeName);
            Assert.Equal(8, matrix.Length);
            Assert.Equal(2, matrix.Decimals);
            Assert.Equal(2, matrix.Dimensions);
            Assert.Equal(new[] { 10, 20 }, matrix.DimensionSizes);
        }

        [Fact]
        public void TryParse_DoesNotConfuseTypeLengthWithArraySize()
        {
            Assert.True(VariableDeclarationParser.TryParse(
                "&Plain : Character(40)",
                out var declaration));

            Assert.Equal("Plain", declaration.Name);
            Assert.Equal(0, declaration.Dimensions);
            Assert.Empty(declaration.DimensionSizes);
            Assert.Equal(40, declaration.Length);
        }

        [Fact]
        public void TrySplitModuleQualifiedTypeName_ReturnsObjectAndModule()
        {
            Assert.True(VariableDeclarationParser.TrySplitModuleQualifiedTypeName(
                "WWPGridState, WorkWithPlus_Web",
                out var objectName,
                out var moduleName));

            Assert.Equal("WWPGridState", objectName);
            Assert.Equal("WorkWithPlus_Web", moduleName);
        }
    }
}
