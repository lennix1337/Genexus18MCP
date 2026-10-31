using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DoctorWwpTemplatesTests
    {
        [Fact]
        public void Without_an_open_kb_the_action_answers_an_unavailable_block_with_its_reason()
        {
            var json = JObject.Parse(new DoctorService(null, null).WwpTemplatesResponse());

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("DoctorWwpTemplatesOk", json["code"]?.ToString());
            Assert.False(json["result"]?["available"]?.ToObject<bool>());
            Assert.Equal("KbNotOpen", json["result"]?["reason"]?.ToString());
        }
    }
}
