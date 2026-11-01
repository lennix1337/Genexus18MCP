using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A read reply names the object the SDK resolved and lists the parts the object has. Both are added by small
    // functions that take the name, type and listing as values, so they are tested without an SDK or a KB open.
    public sealed class ObjectReplyHelpersTests
    {
        [Fact]
        public void WithObjectIdentity_names_the_object_the_read_resolved()
        {
            JObject reply = JObject.Parse(ObjectService.WithObjectIdentity("{\"source\":\"Synth text\"}", "SynthA", "Procedure"));

            Assert.Equal("SynthA", (string)reply["objectName"]!);
            Assert.Equal("Procedure", (string)reply["objectType"]!);
            Assert.Equal("Synth text", (string)reply["source"]!);
        }

        [Fact]
        public void WithObjectIdentity_replaces_a_name_the_reply_already_holds()
        {
            // The identity is the object the SDK resolved, not a name the reply happened to carry.
            JObject reply = JObject.Parse(ObjectService.WithObjectIdentity("{\"objectName\":\"Other\",\"source\":\"x\"}", "SynthA", "Procedure"));

            Assert.Equal("SynthA", (string)reply["objectName"]!);
        }

        [Fact]
        public void WithObjectIdentity_returns_a_reply_that_is_not_json_unchanged()
        {
            Assert.Equal("not json", ObjectService.WithObjectIdentity("not json", "SynthA", "Procedure"));
        }

        [Fact]
        public void WithAvailableParts_lists_the_parts_the_object_has()
        {
            var reply = new JObject { ["parts"] = new JObject() };

            ObjectService.WithAvailableParts(reply, () => new[] { "Source", "Rules" });

            Assert.Equal(new[] { "Source", "Rules" }, ((JArray)reply["availableParts"]!).Select(token => (string)token!).ToArray());
        }

        [Fact]
        public void WithAvailableParts_lists_an_empty_list_for_an_object_without_parts()
        {
            var reply = new JObject();

            ObjectService.WithAvailableParts(reply, () => new string[0]);

            var list = reply["availableParts"] as JArray;
            Assert.NotNull(list);
            Assert.Empty(list);
        }

        [Fact]
        public void WithAvailableParts_leaves_the_field_out_when_the_listing_throws()
        {
            var reply = new JObject();

            ObjectService.WithAvailableParts(reply, () => throw new InvalidOperationException("the SDK could not list the parts"));

            Assert.Null(reply["availableParts"]);
        }

        [Fact]
        public void WithAvailableParts_leaves_the_field_out_when_the_listing_is_null()
        {
            var reply = new JObject();

            ObjectService.WithAvailableParts(reply, () => null);

            Assert.Null(reply["availableParts"]);
        }

        [Fact]
        public void A_reply_whose_listing_failed_is_not_taken_for_a_type_without_parts()
        {
            var reply = new JObject { ["code"] = "PartNotFound", ["error"] = "Part 'Source' not found in SynthA" };

            ObjectService.WithAvailableParts(reply, () => throw new InvalidOperationException("the SDK could not list the parts"));

            Assert.False(SdkTextTreeService.IsNoTextPartsReply(reply));
        }
    }
}
