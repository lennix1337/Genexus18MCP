using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class ChangedObjectsCandidateTests
    {
        [Fact]
        public void LastUpdateSelectsContentCandidatesAndUnknownTimesStayConservative()
        {
            DateTime baseline = DateTime.Parse("2026-09-21T10:00:00");
            Assert.False(KbVersionService.IsContentCandidate(baseline, baseline));
            Assert.True(KbVersionService.IsContentCandidate(baseline, baseline.AddSeconds(1)));
            Assert.True(KbVersionService.IsContentCandidate(DateTime.MinValue, DateTime.MinValue));
        }

        private sealed class Gx18Version { public object Model { get; } = "gx18-model"; }
        private sealed class Evolution3Version
        {
            public object Model { get; } = "udm-model-none";
            public object ModelVersion { get; } = "ev3-model";
        }

        [Fact]
        public void TheFrozenModelIsFoundUnderEitherSdkPropertyName()
        {
            Assert.Equal("gx18-model", KbVersionService.VersionModelOf(new Gx18Version()));
            Assert.Equal("ev3-model", KbVersionService.VersionModelOf(new Evolution3Version()));
            Assert.Null(KbVersionService.VersionModelOf(null));
        }
    }
}
