using System;
using Artech.Common.Diagnostics;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A Trial licence caps the number of objects in a KB. The SDK raises MaxObjCountException
    // from Save(); callers used to see a generic CreateObjectFailed and kept probing.
    public sealed class TrialObjectLimitTests
    {
        private const string Sdk = "Web Panel 'W' cannot be saved because a maximum limit was reached for your GeneXus product license.";

        [Fact]
        public void FindsTheLimitThroughWrappers()
        {
            var limit = new MaxObjCountException(818, Sdk);
            var wrapped = new InvalidOperationException("outer", new System.Reflection.TargetInvocationException(limit));
            Assert.Same(limit, TrialObjectLimit.Find(wrapped));
            Assert.Null(TrialObjectLimit.Find(new InvalidOperationException("other")));
        }

        [Fact]
        public void ResponseNamesTheTrialCapAndSaysNothingWasSaved()
        {
            var json = JObject.Parse(TrialObjectLimit.Response("W", new MaxObjCountException(818, Sdk)));
            var error = json["error"];
            Assert.Equal("TrialObjectLimitReached", (string)error["code"]);
            Assert.Contains("Trial", (string)error["hint"]);
            Assert.Equal(818, (int)error["maxCount"]);
            Assert.False((bool)json["persisted"]);
            Assert.False((bool)error["retryable"]);
        }
    }
}
