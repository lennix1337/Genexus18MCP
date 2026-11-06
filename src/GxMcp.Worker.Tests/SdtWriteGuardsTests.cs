using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The decisions the SDK write takes inside its transaction and during a rollback. The SDK calls around them are proven on
    /// a live KB; the decisions themselves are fixed here, because a wrong answer overwrites another writer's work or accepts a
    /// stale token.
    /// </summary>
    public class SdtWriteGuardsTests
    {
        private static JObject Members(string value) => new JObject
        {
            ["debug"] = new JObject { ["kind"] = "item", ["properties"] = new JObject { ["idJsonInclude"] = value } }
        };

        [Fact]
        public void ATokenThatDiffersInAnyWayIsNotCurrent()
        {
            Assert.True(SdtWriteGuards.VersionStillCurrent("639:abc", "639:abc"));
            Assert.False(SdtWriteGuards.VersionStillCurrent("639:abc", "640:abc"));
            Assert.False(SdtWriteGuards.VersionStillCurrent("639:abc", "639:ABC"));
            Assert.False(SdtWriteGuards.VersionStillCurrent(null, "639:abc"));
            Assert.False(SdtWriteGuards.VersionStillCurrent("639:abc", null));
        }

        [Fact]
        public void ARestoreIsAllowedOnlyWhenVersionAndStoredPropertiesAreThoseOfThisWrite()
        {
            Assert.Null(SdtWriteGuards.RestoreRefusal("639:abc", Members("idJsonEmpty"), "639:abc", Members("idJsonEmpty")));
        }

        [Fact]
        public void ARestoreIsRefusedWhenTheWriteNeverReachedASave()
        {
            Assert.Contains("never reached a save", SdtWriteGuards.RestoreRefusal(null, null, "639:abc", Members("x")));
            Assert.Contains("never reached a save", SdtWriteGuards.RestoreRefusal("  ", null, "639:abc", Members("x")));
        }

        [Fact]
        public void ARestoreIsRefusedWhenTheVersionMovedAfterTheSave()
        {
            Assert.Contains("version cannot be attributed",
                SdtWriteGuards.RestoreRefusal("639:abc", Members("idJsonEmpty"), "640:abc", Members("idJsonEmpty")));
        }

        [Fact]
        public void ARestoreIsRefusedWhenTheStoredPropertiesAreNotTheOnesTheWriteSavedEvenInTheSameSecond()
        {
            // The object stamp has one-second resolution: another writer in the same second leaves the same version.
            Assert.Contains("not the ones this write saved",
                SdtWriteGuards.RestoreRefusal("639:abc", Members("idJsonEmpty"), "639:abc", Members("idJsonJsonNull")));
        }

        [Fact]
        public void WithoutTheSavedPropertiesTheVersionAloneDecides()
        {
            Assert.Null(SdtWriteGuards.RestoreRefusal("639:abc", null, "639:abc", Members("anything")));
        }
    }
}
