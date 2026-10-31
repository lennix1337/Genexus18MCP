using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // An explicit target that the index does not hold goes to the SDK by its identity: a GUID or an EntityKey is
    // looked up by that identity, and only a name is looked up by name. The SDK call itself needs a KB; these tests
    // pin the choice of identity it is made with.
    public sealed class SdkTextTreeLookupIdentityTests
    {
        private const string Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11";
        private const string EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001";

        [Fact]
        public void A_name_target_is_looked_up_by_name()
        {
            (string name, string guid, string entityKey) = SdkTextTreeService.LookupIdentity(new TextSelector { Name = "SynthTemplate" });

            Assert.Equal("SynthTemplate", name);
            Assert.Null(guid);
            Assert.Null(entityKey);
        }

        [Fact]
        public void A_GUID_target_is_looked_up_by_GUID_and_not_by_name()
        {
            (string name, string guid, string entityKey) = SdkTextTreeService.LookupIdentity(new TextSelector { Name = Guid });

            Assert.Null(name);
            Assert.Equal(Guid, guid);
            Assert.Null(entityKey);
        }

        [Fact]
        public void An_EntityKey_target_is_looked_up_by_EntityKey_and_not_by_name()
        {
            (string name, string guid, string entityKey) = SdkTextTreeService.LookupIdentity(new TextSelector { Name = EntityKey });

            Assert.Null(name);
            Assert.Null(guid);
            Assert.Equal(EntityKey, entityKey);
        }

        [Fact]
        public void A_colon_form_EntityKey_target_is_looked_up_by_EntityKey()
        {
            (string name, string guid, string entityKey) = SdkTextTreeService.LookupIdentity(
                new TextSelector { Name = "083f1b21-5715-45e1-8a8d-ceadef141e02:9001" });

            Assert.Null(name);
            Assert.Null(guid);
            Assert.Equal("083f1b21-5715-45e1-8a8d-ceadef141e02:9001", entityKey);
        }
    }
}
