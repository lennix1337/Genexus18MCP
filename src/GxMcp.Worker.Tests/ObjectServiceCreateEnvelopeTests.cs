using System;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // TECHDEBT-02: ObjectService.CreateObject's "No KB open" / unsupported-type
    // error paths used to hand-roll {"status":"Error",...} strings. These pin
    // the canonical McpResponse.Err envelope now in use (AlreadyExists /
    // CreateObjectFailed require a real SDK KB and are covered by the live
    // worker integration suite instead).
    //
    // The WorkWithPlus template refusal runs before the Knowledge Base is read,
    // so it is pinned here with no KB open, on the real methods.
    public class ObjectServiceCreateEnvelopeTests
    {
        private static ObjectService BuildIsolated()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            return new ObjectService(kb, build);
        }

        [Fact]
        public void CreateObject_NoKbOpen_ReturnsCanonicalError()
        {
            var svc = BuildIsolated();
            var j = JObject.Parse(svc.CreateObject("Procedure", "Foo"));
            Assert.Equal("error", (string)j["status"]);
            Assert.Equal("NoKb", (string)j["error"]["code"]);
        }

        [Fact]
        public void CreateObject_WorkWithPlusTemplate_IsRefusedWithTheIdeHint_WithNoKbOpen()
        {
            var svc = BuildIsolated();
            var j = JObject.Parse(svc.CreateObject("WorkWithPlus for Web Template", "SynthTemplate"));
            Assert.Equal("error", (string)j["status"]);
            Assert.Equal("UnsupportedObjectType", (string)j["error"]["code"]);
            Assert.Equal(ObjectCreateTypes.WorkWithPlusTemplateHint, (string)j["error"]["hint"]);
        }

        [Fact]
        public void CreateObjectInstance_WorkWithPlusTemplate_ThrowsWithTheIdeHint_WithNoKbOpen()
        {
            var svc = BuildIsolated();
            var ex = Assert.Throws<ArgumentException>(() =>
                svc.CreateObjectInstance("WorkWithPlus for Web Template", "SynthTemplate", null, out _, out _));
            Assert.Contains(ObjectCreateTypes.WorkWithPlusTemplateHint, ex.Message);
        }

        [Fact]
        public void CreateObjectInstance_Procedure_WithNoKbOpen_StillThrowsKbNotOpened()
        {
            var svc = BuildIsolated();
            var ex = Assert.Throws<Exception>(() =>
                svc.CreateObjectInstance("Procedure", "SynthProcedure", null, out _, out _));
            Assert.Equal("KB not opened", ex.Message);
        }
    }
}
