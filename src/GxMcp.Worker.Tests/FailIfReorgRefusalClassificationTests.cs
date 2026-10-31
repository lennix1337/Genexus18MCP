using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // #749: the FailIfReorg refusal is a reorganization refusal, so the build ends as ReorgRequired
    // instead of a generic failure. The SDK text is localized, so the match uses the sentence the
    // running SDK resolves for Messages.FailIfReorg, under the culture of the worker.
    public class FailIfReorgRefusalClassificationTests
    {
        private const string EnglishRefusal = "Reorganization not executed because FailIfReorg was set.";
        private const string PortugueseRefusal = "Reorganização não executada porque FailIfReorg foi definido.";

        [Theory]
        [InlineData("en-US")]
        [InlineData("pt-BR")]
        public void SDK_refusal_text_resolves_at_runtime_and_is_classified(string culture)
        {
            using (new UiCulture(culture))
            {
                string sdkText = BuildService.SdkFailIfReorgMessage();

                Assert.False(string.IsNullOrWhiteSpace(sdkText));
                Assert.True(BuildService.IsFailIfReorgRefusal(sdkText));
                Assert.True(BuildService.DetectBuildAllReorgRequired(sdkText));
            }
        }

        [Theory]
        [InlineData(EnglishRefusal)]
        [InlineData(PortugueseRefusal)]
        public void Reserve_pattern_recognises_the_refusal_sentence_in_english_and_portuguese(string sentence)
        {
            Assert.True(BuildService.IsFailIfReorgReserveSentence(sentence));
            Assert.True(BuildService.IsFailIfReorgRefusal(sentence));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("pt-BR")]
        public void Refusal_line_drives_the_build_status_to_ReorgRequired(string culture)
        {
            using (new UiCulture(culture))
            {
                string refusal = BuildService.SdkFailIfReorgMessage();
                foreach (string action in new[] { "Build", "BuildAll", "Rebuild", "RebuildAll" })
                {
                    var status = new BuildService.BuildTaskStatus { Action = action, Status = "Failed", PartialSuccess = true };
                    typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(new BuildService(), new object[] { status, "error : " + refusal, true });

                    BuildService.FinalizeBuildAllStatus(status, "error : " + refusal + Environment.NewLine + "Error in reorganization");

                    Assert.Equal("ReorgRequired", status.Status);
                    Assert.Equal("reorg_required", status.FailureOrigin);
                    Assert.True(status.ReorgRequired);
                    Assert.False(status.PartialSuccess);
                }
            }
        }

        [Theory]
        [InlineData("Error in reorganization")]
        [InlineData("Erro na reorganização")]
        [InlineData("Build safety flag unavailable on the SDK task: FailIfReorg; no build was started.")]
        [InlineData("FailIfReorg=true; the KB does not require reorg")]
        public void Other_reorganization_messages_are_not_refusals(string line)
        {
            Assert.False(BuildService.IsFailIfReorgRefusal(line));
        }

        [Fact]
        public void Null_or_empty_text_is_not_a_refusal()
        {
            Assert.False(BuildService.IsFailIfReorgRefusal(null));
            Assert.False(BuildService.IsFailIfReorgRefusal(string.Empty));
        }

        private sealed class UiCulture : IDisposable
        {
            private readonly CultureInfo _previous = Thread.CurrentThread.CurrentUICulture;

            public UiCulture(string name)
            {
                Thread.CurrentThread.CurrentUICulture = new CultureInfo(name);
            }

            public void Dispose()
            {
                Thread.CurrentThread.CurrentUICulture = _previous;
            }
        }
    }
}
