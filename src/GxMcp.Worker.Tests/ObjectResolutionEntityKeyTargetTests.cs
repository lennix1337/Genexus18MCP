using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A read, an inspection or an export that names an object by its EntityKey resolves it by identity, as an
    // explicit entityKey does. The lookup itself needs the SDK; these tests cover the target normalization it runs first.
    public sealed class ObjectResolutionEntityKeyTargetTests
    {
        private const string EntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9001";
        private const string OtherEntityKey = "083f1b21-5715-45e1-8a8d-ceadef141e02-9002";
        private const string Guid = "6b1f0c52-0d8a-4a57-9f3e-2f6d1c0a9e11";

        [Fact]
        public void A_name_target_leaves_the_entity_key_unset()
        {
            string entityKey = null;

            Assert.True(ObjectService.NormalizeEntityKeyTarget("SynthTemplate", ref entityKey));
            Assert.Null(entityKey);
        }

        [Fact]
        public void An_EntityKey_target_names_the_object_when_no_entity_key_is_given()
        {
            string entityKey = null;

            Assert.True(ObjectService.NormalizeEntityKeyTarget(EntityKey, ref entityKey));
            Assert.Equal(EntityKey, entityKey);
        }

        [Fact]
        public void An_EntityKey_target_agreeing_with_the_entity_key_is_accepted()
        {
            string entityKey = EntityKey.ToUpperInvariant();

            Assert.True(ObjectService.NormalizeEntityKeyTarget(EntityKey, ref entityKey));
            Assert.Equal(EntityKey.ToUpperInvariant(), entityKey);
        }

        [Fact]
        public void An_EntityKey_target_contradicting_the_entity_key_is_refused()
        {
            string entityKey = OtherEntityKey;

            Assert.False(ObjectService.NormalizeEntityKeyTarget(EntityKey, ref entityKey));
            Assert.Equal(OtherEntityKey, entityKey);
        }

        [Fact]
        public void A_GUID_target_is_left_to_the_GUID_normalization()
        {
            string entityKey = null;

            Assert.True(ObjectService.NormalizeEntityKeyTarget(Guid, ref entityKey));
            Assert.Null(entityKey);
        }

        [Fact]
        public void An_EntityKey_target_in_colon_form_agreeing_with_the_entity_key_is_accepted()
        {
            string entityKey = EntityKey;

            Assert.True(ObjectService.NormalizeEntityKeyTarget(EntityKey.Replace("-9001", ":9001"), ref entityKey));
            Assert.Equal(EntityKey, entityKey);
        }

        [Fact]
        public void An_EntityKey_target_in_wrapped_form_agreeing_with_the_entity_key_is_accepted()
        {
            string entityKey = EntityKey;

            Assert.True(ObjectService.NormalizeEntityKeyTarget("EntityKey(083f1b21-5715-45e1-8a8d-ceadef141e02, 9001)", ref entityKey));
            Assert.Equal(EntityKey, entityKey);
        }

        [Fact]
        public void An_EntityKey_target_in_colon_form_contradicting_the_entity_key_is_refused()
        {
            string entityKey = EntityKey;

            Assert.False(ObjectService.NormalizeEntityKeyTarget(OtherEntityKey.Replace("-9002", ":9002"), ref entityKey));
            Assert.Equal(EntityKey, entityKey);
        }
    }
}
