using System;
using System.IO;
using GxMcp.TestSupport;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Opt-in genexus_read answered from the durable source store (GXMCP_SOURCE_STORE_READS).
    /// The serving decision needs a live SDK object (its LastUpdate, parent and dirty flag),
    /// so this file covers the store half behaviourally and pins the ObjectService half
    /// structurally; the end-to-end check is in the PR's measurement plan.
    /// </summary>
    public class SourceStoreReadFastPathTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly Func<string> _originalSetting;

        public SourceStoreReadFastPathTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-store-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SourceStoreService.Instance.SetStoreDirectoryForTest(_tempDir);
            _originalSetting = SourceStoreService.ReadFastPathSetting;
        }

        public void Dispose()
        {
            SourceStoreService.ReadFastPathSetting = _originalSetting;
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void Flag_Is_Off_Unless_Exactly_One()
        {
            foreach (string value in new[] { null, "", "0", "true", "on" })
            {
                SourceStoreService.ReadFastPathSetting = () => value;
                Assert.False(SourceStoreService.ReadFastPathEnabled());
            }
            SourceStoreService.ReadFastPathSetting = () => "1";
            Assert.True(SourceStoreService.ReadFastPathEnabled());
            SourceStoreService.ReadFastPathSetting = () => throw new InvalidOperationException();
            Assert.False(SourceStoreService.ReadFastPathEnabled());
        }

        [Fact]
        public void Only_A_Record_Stored_With_A_Version_Token_Can_Answer()
        {
            var store = SourceStoreService.Instance;
            string guid = Guid.NewGuid().ToString();
            DateTime stamp = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.False(store.TryGetWithVersionToken(guid, "Source", out _, out _));

            Assert.True(store.Put(guid, "Source", "parm(in:&A);", stamp, null));
            Assert.False(store.TryGetWithVersionToken(guid, "Source", out _, out _));

            Assert.True(store.Put(guid, "Source", "parm(in:&B);", stamp.AddMinutes(1), "token-b"));
            Assert.True(store.TryGetWithVersionToken(guid, "source", out string body, out string token));
            Assert.Equal("parm(in:&B);", body);
            Assert.Equal("token-b", token);

            // A later write without a token (search, plain read promotion) drops the claim.
            Assert.True(store.Put(guid, "Source", "parm(in:&C);", stamp.AddMinutes(2), null));
            Assert.False(store.TryGetWithVersionToken(guid, "Source", out _, out _));
        }

        [Fact]
        public void A_Corrupt_Record_Does_Not_Answer_Even_With_A_Token()
        {
            var store = SourceStoreService.Instance;
            string guid = Guid.NewGuid().ToString();
            Assert.True(store.Put(guid, "Events", "Event Start\nEndEvent", DateTime.UtcNow, "token"));

            string file = Directory.GetFiles(_tempDir, "*events*.bin.gz", SearchOption.AllDirectories)[0];
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

            Assert.False(store.TryGetWithVersionToken(guid, "Events", out string body, out string token));
            Assert.Null(body);
            Assert.Null(token);
        }

        [Fact]
        public void A_Complete_Read_Certifies_The_Record_Only_When_Given_The_Token()
        {
            var guid = Guid.NewGuid();
            var indexCache = new IndexCacheService();
            indexCache.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Guid = guid.ToString(), Name = "TokenedRead", Type = "Procedure",
                    LastUpdate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc) }
            });
            indexCache.MarkIndexComplete(1);
            var service = new ObjectService(new KbService(indexCache), null);
            var payload = new JObject
            {
                ["source"] = "parm(&CustomerId);",
                ["truncated"] = false,
                ["isTruncatedByWorker"] = false,
                ["isBase64"] = false
            };

            service.TryPromoteCompleteSourceRead(guid, "Source", payload, null, "mcp", false);
            Assert.False(SourceStoreService.Instance.TryGetWithVersionToken(guid.ToString(), "Source", out _, out _));

            service.TryPromoteCompleteSourceRead(guid, "Source", payload, null, "mcp", false, versionToken: "v1:abc");
            Assert.True(SourceStoreService.Instance.TryGetWithVersionToken(guid.ToString(), "Source", out string body, out string token));
            Assert.Equal("parm(&CustomerId);", body);
            Assert.Equal("v1:abc", token);
        }

        /// <summary>
        /// The serving decision needs an SDK object, which a unit test cannot build, so the
        /// load-bearing lines are pinned: the flag gates both the read and the certification,
        /// the stored token must equal the token this object yields for the stored text, the
        /// payload goes through the same ProcessSourceContent as the SDK branch, and an object
        /// with a dirty or unreadable flag is refused.
        /// </summary>
        [Fact]
        public void The_Read_Path_Serves_Only_A_Version_Matched_Clean_Record()
        {
            string src = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("bool storeEligible = SourceStoreService.ReadFastPathEnabled()", src);
            Assert.Contains("(storeEligible ? TryReadSourceFromStore(obj, resolvedPart, offset, limit, client) : null)", src);
            Assert.Contains("storeEligible ? parsedPayload[\"versionToken\"]?.ToString() : null", src);

            int start = src.IndexOf("private string TryReadSourceFromStore(", StringComparison.Ordinal);
            Assert.True(start >= 0, "TryReadSourceFromStore not found");
            int end = src.IndexOf("private void ProcessSourceContent(", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not delimit TryReadSourceFromStore");
            string body = src.Substring(start, end - start);
            Assert.Contains("string expected = WriteService.ComputeContentVersionToken(obj, stored);", body);
            Assert.Contains("string.Equals(expected, storedToken, StringComparison.Ordinal)", body);
            Assert.Contains("ProcessSourceContent(obj, stored, offset, limit, result, client);", body);
            Assert.Contains("DescribeReadFreshness(result.ToString(), false, ReadOriginSourceStore)", body);

            Assert.Contains("else if (!IsKnownClean(obj) || !IsKnownClean(part))", src);
            Assert.Contains("return dirty is bool flag && !flag;", src);
        }
    }
}
