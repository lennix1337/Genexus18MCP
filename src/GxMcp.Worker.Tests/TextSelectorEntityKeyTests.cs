using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // An EntityKey is written as guid:id, guid-id or EntityKey(guid, id). A selector list must keep each one whole:
    // the colon is not a Type:Name separator for it, and the comma of EntityKey(guid, id) does not split the list.
    public sealed class TextSelectorEntityKeyTests
    {
        private const string TypeGuid = "083f1b21-5715-45e1-8a8d-ceadef141e02";

        [Fact]
        public void A_colon_separated_EntityKey_is_one_selector_and_not_a_type_and_name()
        {
            TextSelector selector = Assert.Single(TextSelectorParser.ParseMany(new JValue(TypeGuid + ":9001")));

            Assert.Null(selector.Type);
            Assert.Equal(TypeGuid + ":9001", selector.Name);
        }

        [Fact]
        public void A_wrapped_EntityKey_keeps_its_comma_inside_a_selector_list()
        {
            List<TextSelector> selectors = TextSelectorParser.ParseMany(
                new JValue("SynthA, EntityKey(" + TypeGuid + ", 9001); SynthB"));

            Assert.Equal(
                new[] { "SynthA", "EntityKey(" + TypeGuid + ", 9001)", "SynthB" },
                selectors.Select(selector => selector.Name).ToArray());
        }

        [Fact]
        public void A_type_and_name_still_split_at_the_colon()
        {
            TextSelector selector = Assert.Single(TextSelectorParser.ParseMany(new JValue("Procedure:SynthA")));

            Assert.Equal("Procedure", selector.Type);
            Assert.Equal("SynthA", selector.Name);
        }

        [Fact]
        public void An_EntityKey_selects_its_entry_in_every_written_form()
        {
            var entry = new SearchIndex.IndexEntry
            {
                Name = "SynthA",
                Type = "Procedure",
                EntityKey = TypeGuid + "-9001"
            };

            Assert.True(TextSelectorParser.Matches(new TextSelector { Name = TypeGuid + ":9001" }, entry));
            Assert.True(TextSelectorParser.Matches(new TextSelector { Name = "EntityKey(" + TypeGuid + ", 9001)" }, entry));
            Assert.False(TextSelectorParser.Matches(new TextSelector { Name = "EntityKey(" + TypeGuid + ", 9002)" }, entry));
        }
    }
}
