using System;
using System.IO;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AssetSearchDescriptorTests
    {
        // A KB folder holds the KB's own SQL Server database (GX_KB_*.mdf/.ldf), which the
        // database engine keeps open exclusively. A search must still list it instead of
        // failing the whole search on the first locked file.
        [Fact]
        public void A_file_another_process_holds_exclusively_is_listed_without_its_hash()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-asset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string locked = Path.Combine(root, "GX_KB_Test.mdf");
                File.WriteAllText(locked, "database");
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var descriptor = AssetService.DescribeForSearch(root, locked);

                    Assert.Equal("GX_KB_Test.mdf", (string)descriptor["relativePath"]);
                    Assert.Equal(8L, (long)descriptor["size"]);
                    Assert.False((bool)descriptor["readable"]);
                    Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, descriptor["sha256"].Type);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void A_readable_file_keeps_its_hash()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-asset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string file = Path.Combine(root, "logo.txt");
                File.WriteAllText(file, "abc");

                var descriptor = AssetService.DescribeForSearch(root, file);

                Assert.True((bool)descriptor["readable"]);
                Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", (string)descriptor["sha256"]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
