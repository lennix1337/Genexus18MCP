using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// #656 and #654: the compact lifecycle shape lists at most ten errors, so it says how
    /// many it left out and where the rest is, and it keeps what a specify validated.
    /// </summary>
    public class LifecycleErrorCoverageTests
    {
        private static JObject Status(int errorCount, int listed, params (string Key, JToken Value)[] extra)
        {
            var errors = new JArray();
            for (int i = 0; i < listed; i++) errors.Add("error " + i);
            var status = new JObject
            {
                ["Status"] = "Failed",
                ["Phase"] = "Done",
                ["TaskId"] = "synthetic",
                ["ErrorCount"] = errorCount,
                ["WarningCount"] = 0,
                ["Errors"] = errors,
                ["Warnings"] = new JArray(),
                ["fullLogPath"] = "synthetic.log",
            };
            foreach (var (key, value) in extra) status[key] = value;
            return status;
        }

        [Fact]
        public void ACutErrorListSaysHowManyErrorsAreMissing()
        {
            var compact = LifecycleResponseShaper.CompactObject(Status(errorCount: 120, listed: 50));

            Assert.Equal(10, ((JArray)compact["errors"]!).Count);
            Assert.Equal(110, (int)compact["omittedErrors"]!);
            Assert.Equal(120, (int)compact["errorCount"]!);
            Assert.Contains("first 10 of 120", (string)compact["errorsHint"]!);
            Assert.Contains("fullLogPath", (string)compact["errorsHint"]!);
            Assert.Equal("synthetic.log", (string)compact["fullLogPath"]!);
        }

        [Fact]
        public void AListThatShowsEveryErrorReportsNoOmission()
        {
            var compact = LifecycleResponseShaper.CompactObject(Status(errorCount: 4, listed: 4));

            Assert.Null(compact["omittedErrors"]);
            Assert.Null(compact["errorsHint"]);
        }

        [Fact]
        public void AnErrorCountBeyondWhatTheTaskKeptIsStillReportedAsOmitted()
        {
            // The task keeps 50 lines; 12 shown of 60 counted leaves 48 unseen, not 10.
            var compact = LifecycleResponseShaper.CompactObject(Status(errorCount: 60, listed: 12));

            Assert.Equal(50, (int)compact["omittedErrors"]!);
        }

        [Fact]
        public void WhatASpecifyValidatedSurvivesTheCompactShape()
        {
            var compact = LifecycleResponseShaper.CompactObject(Status(2, 2,
                ("validatedSource", "environment-copy"),
                ("staleEnvironmentCopy", true)));

            Assert.Equal("environment-copy", (string)compact["validatedSource"]!);
            Assert.True((bool)compact["staleEnvironmentCopy"]!);

            var plain = LifecycleResponseShaper.CompactObject(Status(0, 0));
            Assert.Null(plain["validatedSource"]);
            Assert.Null(plain["staleEnvironmentCopy"]);
        }
    }
}
