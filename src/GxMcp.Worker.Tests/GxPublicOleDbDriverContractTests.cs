using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using GxMcp.Worker.Drivers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GxPublicOleDbDriverContractTests
    {
        [Fact]
        public void OpenAndQuery_UsesDocumentedGxPublicMetadataSurface()
        {
            var connection = new FakeGxPublicConnection("GXPublic.GXPublic.4");
            var driver = new GxPublicOleDbDriver(
                progIdResolver: provider => provider == "GXPublic.GXPublic.4" ? typeof(object) : null,
                connectionFactory: provider => provider == "GXPublic.GXPublic.4" ? connection : null);

            bool opened = driver.OpenKB(@"C:\KBs\GX8", out string openError);
            Assert.True(opened);
            Assert.Null(openError);
            Assert.Equal("GXPublic.GXPublic.4", driver.ResolvedProgId);
            Assert.Equal(@"C:\KBs\GX8", driver.ActiveKbPath);

            var objects = driver.QueryObjects("procedure", "Cli", out string queryError);
            Assert.Null(queryError);
            Assert.Single(objects);
            Assert.Equal("Client", objects[0]);
            Assert.Equal("SELECT * FROM Object", connection.LastSql);

            var metadata = driver.QueryObjectMetadata("procedure", "Client", out string metadataError);
            Assert.Null(metadataError);
            var client = Assert.Single(metadata);
            Assert.Equal("Client", client.Name);
            Assert.Equal("procedure", client.Type);
            Assert.Equal("Customer lookup", client.Description);
            Assert.Equal("1", client.ModelId);

            string source = driver.ReadObjectPart("Client", "Source", out string sourceError);
            Assert.Null(source);
            Assert.StartsWith("GXPUBLIC_SOURCE_UNSUPPORTED:", sourceError);
        }

        [Fact]
        public void QueryFailure_NamesTheStageProviderAndRowsRead()
        {
            var connection = new FailingReaderConnection("GXPublic.GXPublic.5");
            var driver = new GxPublicOleDbDriver(
                progIdResolver: provider => provider == "GXPublic.GXPublic.5" ? typeof(object) : null,
                connectionFactory: provider => provider == "GXPublic.GXPublic.5" ? connection : null);
            Assert.True(driver.OpenKB(@"C:\KBs\GX9", out string openError), openError);

            var objects = driver.QueryObjects(null, null, out string error);

            Assert.Empty(objects);
            Assert.Contains("protected memory", error);
            Assert.Contains("stage 'ExecuteReader'", error);
            Assert.Contains("provider GXPublic.GXPublic.5", error);
            Assert.Contains("rows read 0", error);
        }

        [Fact]
        public void Open_FallsBackWhenTheFirstRegisteredProviderCannotOpenTheKb()
        {
            var fallback = new FakeGxPublicConnection("GXPublic.GXPublic.5");
            var driver = new GxPublicOleDbDriver(
                progIdResolver: provider => provider == "GXPublic.GXPublic.4" || provider == "GXPublic.GXPublic.5"
                    ? typeof(object)
                    : null,
                connectionFactory: provider => provider == "GXPublic.GXPublic.4"
                    ? new ThrowingGxPublicConnection(provider)
                    : (IGxPublicConnection)fallback);

            bool opened = driver.OpenKB(@"C:\KBs\GX9", out string error);
            Assert.True(opened);
            Assert.Null(error);
            Assert.Equal("GXPublic.GXPublic.5", driver.ResolvedProgId);
            Assert.True(fallback.IsOpen);
        }

        [Fact]
        public void MissingProvider_ReturnsStructuredRegistrationError()
        {
            var driver = new GxPublicOleDbDriver(progIdResolver: _ => null);

            bool opened = driver.OpenKB(@"C:\KBs\GX8", out string errorJson);
            Assert.False(opened);

            var response = JObject.Parse(errorJson);
            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("GXMCP_GXPUBLIC_PROVIDER_NOT_REGISTERED", response["error"]?["code"]?.ToString());
            Assert.Contains("matching the KB generation", response["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void QueryFailure_LogsOpenVsQueryApartmentAndThreadIdsAndInprocCategory()
        {
            var connection = new FailingReaderConnection("GXPublic.GXPublic.5");
            var driver = new GxPublicOleDbDriver(
                progIdResolver: provider => provider == "GXPublic.GXPublic.5" ? typeof(object) : null,
                connectionFactory: provider => provider == "GXPublic.GXPublic.5" ? connection : null);

            int openThreadId = -1;
            var staThread = new Thread(() =>
            {
                openThreadId = Thread.CurrentThread.ManagedThreadId;
                Assert.True(driver.OpenKB(@"C:\KBs\GX9", out string openError), openError);
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            int queryThreadId = -1;
            string error = null;
            var mtaThread = new Thread(() =>
            {
                queryThreadId = Thread.CurrentThread.ManagedThreadId;
                driver.QueryObjects(null, null, out error);
            });
            mtaThread.SetApartmentState(ApartmentState.MTA);
            mtaThread.Start();
            mtaThread.Join();

            Assert.NotNull(error);
            Assert.Contains("provider GXPublic.GXPublic.5", error);
            Assert.Contains($"openThread {openThreadId} (STA)", error);
            Assert.Contains($"queryThread {queryThreadId} (MTA)", error);
            Assert.Contains("inprocServer32 ", error);
        }

        [Theory]
        [InlineData(@"C:\Program Files (x86)\GeneXus\GX9\GxPublic.dll", "program-files-x86")]
        [InlineData(@"C:\Program Files\Common Files\GeneXus\GxPublic.dll", "common-files")]
        [InlineData(@"C:\Windows\SysWOW64\GxPublic.dll", "system-wow64")]
        [InlineData(@"C:\Users\sensitive-user\Downloads\GxPublic.dll", "user-profile")]
        [InlineData(null, "not-found")]
        public void CategorizeInprocServer32Path_OmitsUserPaths(string rawPath, string expectedCategory)
        {
            string category = GxPublicOleDbDriver.CategorizeInprocServer32Path(rawPath);
            Assert.Equal(expectedCategory, category);
            Assert.DoesNotContain("sensitive-user", category, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", category);
        }

        [Fact]
        public void OleDbConnection_BuildsConnectionStringInOnePlace_AndFallsBackWhenModeReadIsRejected()
        {
            string driverSourcePath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "GxMcp.Worker", "Drivers", "GxPublicOleDbDriver.cs"));
            string driverSource = File.ReadAllText(driverSourcePath);
            Assert.Single(Regex.Matches(driverSource, @"new\s+OleDbConnectionStringBuilder\b"));

            var attempted = new List<string>();
            using (var conn = new OleDbGxPublicConnection("GXPublic.GXPublic.5", cs =>
            {
                attempted.Add(cs);
                var parsed = new OleDbConnectionStringBuilder(cs);
                if (parsed.ContainsKey("Mode"))
                    throw new InvalidOperationException("Provider does not support Mode=Read");
            }))
            {
                conn.Open(@"C:\KBs\GX9");
            }

            Assert.Equal(2, attempted.Count);
            var first = new OleDbConnectionStringBuilder(attempted[0]);
            Assert.Equal("GXPublic.GXPublic.5", first["Provider"]?.ToString());
            Assert.Equal(@"C:\KBs\GX9", first["Data Source"]?.ToString());
            Assert.Equal("Read", first["Mode"]?.ToString());

            var second = new OleDbConnectionStringBuilder(attempted[1]);
            Assert.Equal("GXPublic.GXPublic.5", second["Provider"]?.ToString());
            Assert.Equal(@"C:\KBs\GX9", second["Data Source"]?.ToString());
            Assert.False(second.ContainsKey("Mode"));
        }

        private sealed class FakeGxPublicConnection : IGxPublicConnection
        {
            private readonly DataTable _objects = new DataTable("Object");

            public FakeGxPublicConnection(string providerName)
            {
                ProviderName = providerName;
                _objects.Columns.Add("ObjNam", typeof(string));
                _objects.Columns.Add("ObjCls", typeof(int));
                _objects.Columns.Add("ObjDsc", typeof(string));
                _objects.Columns.Add("MdlId", typeof(int));
                _objects.Rows.Add("Client", 1, "Customer lookup", 1);
                _objects.Rows.Add("Invoice", 0, "Invoice transaction", 1);
            }

            public string ProviderName { get; }
            public bool IsOpen { get; private set; }
            public string LastSql { get; private set; }

            public void Open(string kbPath)
            {
                IsOpen = true;
            }

            public void Close()
            {
                IsOpen = false;
            }

            public IDataReader ExecuteReader(string sql)
            {
                LastSql = sql;
                return _objects.CreateDataReader();
            }

            public void Dispose()
            {
                Close();
            }
        }

        private sealed class FailingReaderConnection : IGxPublicConnection
        {
            public FailingReaderConnection(string providerName)
            {
                ProviderName = providerName;
            }

            public string ProviderName { get; }
            public bool IsOpen { get; private set; }

            public void Open(string kbPath)
            {
                IsOpen = true;
            }

            public void Close()
            {
                IsOpen = false;
            }

            public IDataReader ExecuteReader(string sql) => throw new AccessViolationException("Attempted to read or write protected memory.");
            public void Dispose() { }
        }

        private sealed class ThrowingGxPublicConnection : IGxPublicConnection
        {
            public ThrowingGxPublicConnection(string providerName)
            {
                ProviderName = providerName;
            }

            public string ProviderName { get; }
            public bool IsOpen => false;

            public void Open(string kbPath)
            {
                throw new InvalidOperationException("provider cannot open this KB generation");
            }

            public void Close() { }
            public IDataReader ExecuteReader(string sql) => throw new InvalidOperationException("not open");
            public void Dispose() { }
        }
    }
}
