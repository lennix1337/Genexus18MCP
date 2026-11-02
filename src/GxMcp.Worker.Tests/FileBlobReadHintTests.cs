using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #824: genexus_read on a File shows only the property XML under WikiBlob;
    // the stored bytes come from genexus_io read_blob. The read must say so.
    public class FileBlobReadHintTests
    {
        [Fact]
        public void File_PointsAtReadBlobWithObjectName()
        {
            var meta = ObjectService.BuildFileBlobHint("File", "GxOpenApiBuilder_Settings");

            Assert.NotNull(meta);
            var next = meta["suggested_next"];
            Assert.Equal("genexus_io", (string)next["tool"]);
            Assert.Equal("read_blob", (string)next["args"]["action"]);
            Assert.Equal("GxOpenApiBuilder_Settings", (string)next["args"]["name"]);
            Assert.Equal("File", (string)next["args"]["type"]);
            Assert.Contains("outputPath", (string)meta["blobContentHint"]);
        }

        [Theory]
        [InlineData("Procedure")]
        [InlineData("Image")]
        [InlineData(null)]
        public void OtherTypes_GetNoHint(string type)
        {
            Assert.Null(ObjectService.BuildFileBlobHint(type, "X"));
        }
    }
}
