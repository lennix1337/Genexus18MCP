using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WriteServiceFacadeArgsTests
    {
        [Fact]
        public void NormalizeFacadeArgs_FullMode_UsesRealPartAndContent()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "Source",
                ["content"] = "parm(out:&Ok);",
                ["dryRun"] = true
            });

            Assert.Null(normalized.Mode);
            Assert.Equal("Source", normalized.PartName);
            Assert.Equal("parm(out:&Ok);", normalized.Content);
            Assert.True(normalized.DryRun);
        }

        [Theory]
        [InlineData("baseVersion")]
        [InlineData("expectedVersion")]
        [InlineData("versionToken")]
        public void NormalizeFacadeArgs_ParsesBaseVersion_FromAliases(string key)
        {
            // stale-edit optimistic-concurrency guard: the token can arrive under any
            // of these aliases and must land in BaseVersion.
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "Source",
                ["content"] = "x",
                [key] = "637000000000000000"
            });
            Assert.Equal("637000000000000000", normalized.BaseVersion);
        }

        [Fact]
        public void NormalizeFacadeArgs_NoVersion_LeavesBaseVersionNull()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject { ["part"] = "Source", ["content"] = "x" });
            Assert.Null(normalized.BaseVersion);
        }

        [Fact]
        public void NormalizeFacadeArgs_NullBaseVersion_DoesNotHideExpectedVersion()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "Variables",
                ["content"] = "&Value : Numeric(4)",
                ["dryRun"] = true,
                ["baseVersion"] = JValue.CreateNull(),
                ["expectedVersion"] = "current-part-token"
            });

            Assert.Equal("current-part-token", normalized.BaseVersion);
        }

        [Fact]
        public void ComputeVersionToken_Null_ReturnsNull()
        {
            Assert.Null(WriteService.ComputeVersionToken(null));
        }

        [Fact]
        public void ContentFingerprint_ChangesForEverySourceState()
        {
            string first = WriteService.ComputeContentFingerprint("// state 1");
            string second = WriteService.ComputeContentFingerprint("// state 2");

            Assert.NotEqual(first, second);
            Assert.Equal(first, WriteService.ComputeContentFingerprint("// state 1"));
            Assert.Equal(64, first.Length);
        }

        [Fact]
        public void NormalizeFacadeArgs_PatchMode_UnwrapsFindReplaceShape()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "patch",
                ["part"] = "Source",
                ["content"] = new JObject
                {
                    ["find"] = "old line",
                    ["replace"] = "new line"
                },
                ["expectedCount"] = 2,
                ["replaceAll"] = true
            });

            Assert.Equal("patch", normalized.Mode);
            Assert.Equal("Source", normalized.PartName);
            Assert.Equal("Replace", normalized.Operation);
            Assert.Equal("old line", normalized.Context);
            Assert.Equal("new line", normalized.Content);
            Assert.Equal(2, normalized.ExpectedCount);
            Assert.True(normalized.ReplaceAll);
        }

        [Theory]
        [InlineData(null, true)]            // default → strict verify on
        [InlineData("strict", true)]        // explicit strict → verify on
        [InlineData("best-effort", false)]  // best-effort → skip post-write verify
        public void NormalizeFacadeArgs_Validate_DrivesStrictVerify(string validate, bool expectStrict)
        {
            var args = new JObject { ["part"] = "WebForm", ["content"] = "<GxMultiForm/>" };
            if (validate != null) args["validate"] = validate;

            var normalized = WriteService.NormalizeFacadeArgs(args);

            // strictVerify is derived at the WriteObject facade as
            // !Validate.Equals("best-effort"); mirror that derivation here so the
            // mapping stays locked even though the flag itself is a local.
            bool strictVerify = !string.Equals(normalized.Validate, "best-effort",
                System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expectStrict, strictVerify);
        }

        [Fact]
        public void NormalizeFacadeArgs_ValidateOnly_ForcesDryRun()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "WebForm",
                ["content"] = "<GxMultiForm/>",
                ["validate"] = "only"
            });

            Assert.True(normalized.DryRun);
            Assert.Equal("only", normalized.Validate);
        }

        [Fact]
        public void NormalizeFacadeArgs_ParsesVerifyModeAndRollbackOnFailure()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "patch",
                ["part"] = "Source",
                ["context"] = "old",
                ["content"] = "new",
                ["verifyMode"] = "exact",
                ["rollbackOnFailure"] = true
            });

            Assert.Equal("exact", normalized.VerifyMode);
            Assert.True(normalized.RollbackOnFailure);
        }

        [Fact]
        public void NormalizeFacadeArgs_ConcurrencyPolicy_DefaultsToWarn()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "Source",
                ["content"] = "parm();"
            });

            Assert.Equal("warn", normalized.ConcurrencyPolicy);
        }

        [Fact]
        public void NormalizeFacadeArgs_ConcurrencyPolicy_ParsesCustomValue()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "Source",
                ["content"] = "parm();",
                ["concurrencyPolicy"] = "fail_if_open"
            });

            Assert.Equal("fail_if_open", normalized.ConcurrencyPolicy);
        }

        [Fact]
        public void NormalizeFacadeArgs_ParsesRequireObjectSave()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "patch",
                ["part"] = "Events",
                ["requireObjectSave"] = true
            });

            Assert.True(normalized.RequireObjectSave);
        }

        [Fact]
        public void ValidateRequireObjectSave_FullMode_IsRejected()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "full",
                ["part"] = "Events",
                ["content"] = "Event Enter\nEndevent",
                ["requireObjectSave"] = true
            });

            var response = JObject.Parse(WriteService.ValidateRequireObjectSaveArgs("SamplePanel", normalized));

            Assert.Equal("RequireObjectSaveUnsupportedMode", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ValidateRequireObjectSave_NonEventsPatch_IsRejected()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "patch",
                ["part"] = "Source",
                ["context"] = "old",
                ["content"] = "new",
                ["requireObjectSave"] = true
            });

            var response = JObject.Parse(WriteService.ValidateRequireObjectSaveArgs("SamplePanel", normalized));

            Assert.Equal("RequireObjectSaveUnsupportedPart", response["error"]?["code"]?.ToString());
        }
    }
}
