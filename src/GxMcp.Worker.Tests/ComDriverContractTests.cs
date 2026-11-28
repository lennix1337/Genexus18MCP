using System;
using GxMcp.Worker.Drivers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ComDriverContractTests
    {
        [Fact]
        public void ComGxPublicDriver_ReportsNotRegisteredWhenComTypeNotFound()
        {
            var driver = new ComGxPublicDriver(progIdResolver: _ => null);
            Assert.False(driver.IsRegistered);

            bool ok = driver.OpenKB(@"C:\KBs\ClassicKB", out string errorJson);
            Assert.False(ok);
            Assert.NotNull(errorJson);

            var resp = JObject.Parse(errorJson);
            Assert.Equal("error", resp["status"]?.ToString());
            Assert.Equal("GXMCP_GXPUBLIC_COM_NOT_REGISTERED", resp["error"]?["code"]?.ToString());
            Assert.Contains("GXPublic", resp["error"]?["message"]?.ToString());
            Assert.Contains("regsvr32", resp["error"]?["hint"]?.ToString());
        }

        [Theory]
        [InlineData("GXPublic.GXPublic")]
        [InlineData("GXPublic.Application")]
        [InlineData("GXPublic.GXPublic.5")]
        public void ComGxPublicDriver_ResolvesKnownProgIds(string expectedProgId)
        {
            var driver = new ComGxPublicDriver(progIdResolver: progId =>
                progId == expectedProgId ? typeof(MockComAutomationServer) : null);

            Assert.True(driver.IsRegistered);
            Assert.Equal(expectedProgId, driver.ResolvedProgId);
        }

        [Fact]
        public void ComGxPublicDriver_CanConnectAndDisconnectWithMockServer()
        {
            var mockServer = new MockComAutomationServer();
            var driver = new ComGxPublicDriver(
                progIdResolver: _ => typeof(MockComAutomationServer),
                comInstanceFactory: _ => mockServer);

            Assert.True(driver.IsRegistered);
            Assert.False(driver.IsConnected);

            bool ok = driver.OpenKB(@"C:\KBs\ClassicKB", out string error);
            Assert.True(ok);
            Assert.Null(error);
            Assert.True(driver.IsConnected);
            Assert.Equal(@"C:\KBs\ClassicKB", driver.ActiveKbPath);

            driver.CloseKB();
            Assert.False(driver.IsConnected);
            Assert.Null(driver.ActiveKbPath);
        }

        [Fact]
        public void ComGxPublicDriver_ReadObjectPart_ReturnsContentFromCom()
        {
            var mockServer = new MockComAutomationServer();
            mockServer.StoredObjects["Client"] = "<Object><Name>Client</Name><Description>Customer</Description></Object>";

            var driver = new ComGxPublicDriver(
                progIdResolver: _ => typeof(MockComAutomationServer),
                comInstanceFactory: _ => mockServer);

            driver.OpenKB(@"C:\KBs\ClassicKB", out _);

            string content = driver.ReadObjectPart("Client", "Source", out string error);
            Assert.Null(error);
            Assert.NotNull(content);
            Assert.Contains("Customer", content);
        }

        [Fact]
        public void ComGxPublicDriver_QueryObjects_ReturnsObjectList()
        {
            var mockServer = new MockComAutomationServer();
            mockServer.StoredObjects["Customer"] = "<Object><Name>Customer</Name></Object>";
            mockServer.StoredObjects["Invoice"] = "<Object><Name>Invoice</Name></Object>";

            var driver = new ComGxPublicDriver(
                progIdResolver: _ => typeof(MockComAutomationServer),
                comInstanceFactory: _ => mockServer);

            driver.OpenKB(@"C:\KBs\ClassicKB", out _);

            var list = driver.QueryObjects(null, null, out string error);
            Assert.Null(error);
            Assert.Equal(2, list.Count);
        }

        [Fact]
        public void CommandDispatcher_RefusesNativeBulkIndexUnderComDriver()
        {
            using (GxMcp.Worker.Compatibility.DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                var req = new JObject
                {
                    ["method"] = "kb",
                    ["action"] = "BulkIndex",
                    ["params"] = new JObject()
                };

                string respJson = GxMcp.Worker.Services.CommandDispatcher.Instance.Dispatch(req, req.ToString());
                var resp = JObject.Parse(respJson);
                Assert.Equal("error", resp["status"]?.ToString());
                Assert.Equal("UNSUPPORTED_IN_GENEXUS_VERSION", resp["error"]?["code"]?.ToString());
            }
        }

        [Fact]
        public void CommandDispatcher_DispatchesReadUnderComDriver()
        {
            using (GxMcp.Worker.Compatibility.DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                var req = new JObject
                {
                    ["method"] = "read",
                    ["action"] = "ExtractSource",
                    ["target"] = "Client",
                    ["params"] = new JObject { ["part"] = "Source" }
                };

                // With no COM registered or connected, it gracefully returns error
                string respJson = GxMcp.Worker.Services.CommandDispatcher.Instance.Dispatch(req, req.ToString());
                var resp = JObject.Parse(respJson);
                Assert.Equal("error", resp["status"]?.ToString());
            }
        }

        [Fact]
        public void CommandDispatcher_DispatchesQueryUnderComDriver()
        {
            using (GxMcp.Worker.Compatibility.DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                var req = new JObject
                {
                    ["method"] = "search",
                    ["action"] = "Query",
                    ["target"] = "Client",
                    ["params"] = new JObject()
                };

                string respJson = GxMcp.Worker.Services.CommandDispatcher.Instance.Dispatch(req, req.ToString());
                var resp = JObject.Parse(respJson);
                Assert.Equal("error", resp["status"]?.ToString());
            }
        }

        [Fact]
        public void CommandDispatcher_DispatchesListUnderComDriver()
        {
            using (GxMcp.Worker.Compatibility.DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                var req = new JObject
                {
                    ["method"] = "list",
                    ["action"] = "Objects",
                    ["params"] = new JObject()
                };

                string respJson = GxMcp.Worker.Services.CommandDispatcher.Instance.Dispatch(req, req.ToString());
                var resp = JObject.Parse(respJson);
                Assert.Equal("error", resp["status"]?.ToString());
            }
        }

        [Fact]
        public void ComGxPublicDriver_ExportXPZ_InvokesComExport()
        {
            var mockServer = new MockComAutomationServer();
            var driver = new ComGxPublicDriver(
                progIdResolver: _ => typeof(MockComAutomationServer),
                comInstanceFactory: _ => mockServer);

            driver.OpenKB(@"C:\KBs\ClassicKB", out _);
            bool ok = driver.ExportXPZ(@"C:\Exports\out.xpz", new[] { "Cust", "Inv" }, out string error);
            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(@"C:\Exports\out.xpz", mockServer.LastExportPath);
            Assert.Equal(2, mockServer.LastExportObjects.Length);
        }

        [Fact]
        public void ComGxPublicDriver_ImportXPZ_InvokesComImport()
        {
            var mockServer = new MockComAutomationServer();
            var driver = new ComGxPublicDriver(
                progIdResolver: _ => typeof(MockComAutomationServer),
                comInstanceFactory: _ => mockServer);

            driver.OpenKB(@"C:\KBs\ClassicKB", out _);
            bool ok = driver.ImportXPZ(@"C:\Imports\in.xpz", out string error);
            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(@"C:\Imports\in.xpz", mockServer.LastImportPath);
        }

        [Fact]
        public void CommandDispatcher_DispatchesTransferUnderComDriver()
        {
            using (GxMcp.Worker.Compatibility.DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                var req = new JObject
                {
                    ["method"] = "transfer",
                    ["action"] = "export",
                    ["params"] = new JObject
                    {
                        ["outputFile"] = @"C:\Exports\out.xpz",
                        ["targets"] = new JArray("Customer")
                    }
                };

                string respJson = GxMcp.Worker.Services.CommandDispatcher.Instance.Dispatch(req, req.ToString());
                var resp = JObject.Parse(respJson);
                // With ComGxPublicDriver uninitialized, returns error cleanly
                Assert.Equal("error", resp["status"]?.ToString());
            }
        }

        private sealed class MockComAutomationServer : IDisposable
        {
            public System.Collections.Generic.Dictionary<string, string> StoredObjects { get; }
                = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool IsOpen { get; private set; }
            public string KbPath { get; private set; }
            public string LastExportPath { get; private set; }
            public string[] LastExportObjects { get; private set; }
            public string LastImportPath { get; private set; }

            public bool Open(string path)
            {
                IsOpen = true;
                KbPath = path;
                return true;
            }

            public void Close()
            {
                IsOpen = false;
                KbPath = null;
            }

            public string GetObject(string name, string part)
            {
                return StoredObjects.TryGetValue(name, out var val) ? val : null;
            }

            public System.Collections.Generic.IEnumerable<string> GetObjectNames()
            {
                return StoredObjects.Keys;
            }

            public bool Export(string path, string[] objects)
            {
                LastExportPath = path;
                LastExportObjects = objects;
                return true;
            }

            public bool Import(string path)
            {
                LastImportPath = path;
                return true;
            }

            public void Dispose()
            {
                Close();
            }
        }
    }
}
