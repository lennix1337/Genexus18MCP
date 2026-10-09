using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class VariablesTextReceiptTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EqualDslNeverCertifiesMetadataOrCompleteRollback(bool saveAttempted)
        {
            var receipt = JObject.Parse("{code:'WriteNoChange',changed:false,verified:true,persistedVerified:true,rolledBack:true,stateRestored:true,persisted:false,commitState:'RolledBack',persistenceState:'Restored',rollback:{verified:true,rolledBack:true,atomic:true}}");
            receipt["saved"] = saveAttempted;
            receipt["metadataBefore"] = "original description and identity";
            receipt["metadataAfter"] = "different description and identity";
            VariablesTextReceipt.Limit(receipt);
            Assert.True(receipt["textVerified"]?.Value<bool>());
            Assert.False(receipt["verified"]?.Value<bool>());
            Assert.False(receipt["metadataVerified"]?.Value<bool>());
            Assert.False(receipt["rolledBack"]?.Value<bool>());
            Assert.False(receipt["stateRestored"]?.Value<bool>());
            Assert.False(receipt["rollback"]?["atomic"]?.Value<bool>());
            Assert.Equal(JTokenType.Null, receipt["changed"]?.Type);
            Assert.Equal("WriteApplied", receipt["code"]?.ToString());
            Assert.Equal(saveAttempted, receipt["saved"]?.Value<bool>());
        }

        [Fact]
        public void ProvenPreSaveNoOpRetainsNoChangeWithoutClaimingMetadataVerification()
        {
            var receipt = JObject.Parse("{code:'WriteNoChange',changed:false,result:{savePathExercised:false}}");
            VariablesTextReceipt.Limit(receipt);
            Assert.Equal("WriteNoChange", receipt["code"]?.ToString());
            Assert.False(receipt["changed"]?.Value<bool>());
            Assert.False(receipt["metadataVerified"]?.Value<bool>());
        }
    }
}
