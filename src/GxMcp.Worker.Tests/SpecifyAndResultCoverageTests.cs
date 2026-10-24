using System.Linq;
using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// #654: <c>specify</c> validates the environment's copy of an object, which only a build
    /// refreshes, and its response never said so. #656: a task keeps 50 error lines but counts
    /// every error, and <c>result</c> gave no sign that its list was a cut.
    /// </summary>
    public class SpecifyAndResultCoverageTests
    {
        private const string StaleCopyLine =
            "warning : changed since the last build: SyntheticProc. GeneXus specifies the environment's copy of an object, which only a build refreshes, so this result reflects the source as of the last build.";

        private static void HandleLine(BuildService.BuildTaskStatus status, string line)
        {
            var method = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(new BuildService(), new object[] { status, line, false });
        }

        [Fact]
        public void Specify_StaleCopyWarning_IsFlaggedOnTheStatus()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "specify-stale", SpecifyOnly = true, ValidatedSource = "environment-copy" };

            HandleLine(status, StaleCopyLine);

            Assert.True(status.StaleEnvironmentCopy);
            Assert.Contains("action=build", status.Hint);
            Assert.Single(status.Warnings);

            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Equal("environment-copy", (string)json["validatedSource"]);
            Assert.True((bool)json["staleEnvironmentCopy"]);
        }

        [Fact]
        public void Specify_OtherWarning_DoesNotFlagTheStatus()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "specify-clean", SpecifyOnly = true, ValidatedSource = "environment-copy" };

            HandleLine(status, "warning : spc0001 something unrelated");

            Assert.Null(status.StaleEnvironmentCopy);
            Assert.Null(status.Hint);
            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Null(json["staleEnvironmentCopy"]);
        }

        [Fact]
        public void Build_WithTheSameLine_IsNotAffected()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "build" };

            HandleLine(status, StaleCopyLine);

            Assert.Null(status.StaleEnvironmentCopy);
            Assert.Null(status.ValidatedSource);
            var json = JObject.FromObject(status, new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
            Assert.Null(json["validatedSource"]);
        }

        [Fact]
        public void OnlyASpecifyTaskIsMarkedAsValidatingTheEnvironmentCopy()
        {
            string build = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "BuildService.cs"));

            Assert.Equal(1, SourceAssert.Count(build, "if (status.SpecifyOnly) status.ValidatedSource = \"environment-copy\";"));
        }

        [Fact]
        public void Result_TellsHowManyErrorLinesWereKept()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "many", ErrorCount = 120, FullLogPath = "synthetic.log" };
            for (int i = 0; i < 50; i++) status.Errors.Add("error " + i);

            var payload = new JObject();
            BuildService.AttachErrorCoverage(payload, status);

            Assert.Equal(120, (int)payload["errorsTotal"]);
            Assert.Equal(50, (int)payload["errorsRetained"]);
            Assert.Equal(70, (int)payload["omittedErrors"]);
            Assert.Contains("first 50 of 120", (string)payload["errorsHint"]);
            Assert.Contains("fullLogPath", (string)payload["errorsHint"]);
        }

        [Fact]
        public void Result_WithEveryErrorKept_ReportsNoOmission()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "few", ErrorCount = 3 };
            foreach (int i in Enumerable.Range(0, 3)) status.Errors.Add("error " + i);

            var payload = new JObject();
            BuildService.AttachErrorCoverage(payload, status);

            Assert.Equal(3, (int)payload["errorsTotal"]);
            Assert.Equal(0, (int)payload["omittedErrors"]);
            Assert.Null(payload["errorsHint"]);
        }

        [Fact]
        public void GetResult_CarriesTheCoverageFields()
        {
            string build = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "BuildService.cs"));
            string body = SourceAssert.MethodBody(build, "public string GetResult(string taskId, int page = 1, int pageSize = 50)");

            Assert.Equal(1, SourceAssert.Count(body, "AttachErrorCoverage(jo, status);"));
        }
    }
}
