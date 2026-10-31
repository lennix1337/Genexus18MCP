using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A text clone declares the variables in the order it reads them and the target numbers them in that order, so a
    // WebForm control can keep its var:N and reach another variable. The pure remap rebinds it by name. The cases use
    // synthetic names, and the source order of the first case is the one a real WebPanel showed: ids 5-11, then 15-20,
    // then 12-14, then the system variables 1-4.
    public sealed class SaveAsVariableIdRemapTests
    {
        [Fact]
        public void The_permutation_of_a_webpanel_rebinds_each_control_to_the_variable_with_its_name()
        {
            var outcome = SaveAsVariableIdRemap.ForClone(
                Form(Data("var:11"), Item("var:12"), Item("var:13"), Data("var:15"), Data("var:16"), Data("var:17"), Data("var:18"), Data("var:19"), Data("var:20")),
                SourceVariables(),
                TargetVariables());

            Assert.Equal(8, outcome.Remapped);
            Assert.Empty(outcome.Unmapped);
            Assert.Null(outcome.MissingInTarget);
            Assert.Null(outcome.Skipped);
            Assert.Equal(new[] { 11, 18, 19, 12, 13, 14, 15, 16, 17 }, BindingIds(outcome.Xml));
        }

        [Fact]
        public void A_gap_in_the_source_ids_rebinds_to_the_target_id_of_the_same_name()
        {
            // The source deleted the variable that had 7, so its ids run 5, 6, 8; the target numbers 5, 6, 7.
            var source = new[] { V("Alpha", 5), V("Beta", 6), V("Gamma", 8) };
            var target = new[] { V("Alpha", 5), V("Beta", 6), V("Gamma", 7) };

            var outcome = SaveAsVariableIdRemap.Apply(
                Form(Data("var:5"), Data("var:8")),
                SaveAsVariableIdRemap.SourceNamesById(source),
                SaveAsVariableIdRemap.TargetIdsByName(target));

            Assert.Equal(1, outcome.Remapped);
            Assert.Equal(new[] { 5, 7 }, BindingIds(outcome.Xml));
        }

        [Fact]
        public void Identical_ids_change_nothing_and_return_the_same_text()
        {
            string form = Form(Data("var:5"), Data("var:6"));

            var outcome = SaveAsVariableIdRemap.Apply(
                form,
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Alpha", 5), V("Beta", 6) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Alpha", 5), V("Beta", 6) }));

            Assert.Equal(0, outcome.Remapped);
            Assert.Same(form, outcome.Xml);
        }

        [Fact]
        public void Only_the_var_attribute_values_change_an_att_reference_and_other_numbers_stay()
        {
            string form = Form(
                "<data attribute=\"att:11\" width=\"11\" />",
                "<label>var:11</label>",
                Data("var:11"),
                "<data attribute='var:11' />");

            var outcome = SaveAsVariableIdRemap.Apply(
                form,
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Eta", 11) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 14) }));

            Assert.Equal(2, outcome.Remapped);
            Assert.Contains("attribute=\"att:11\" width=\"11\"", outcome.Xml);
            Assert.Contains("<label>var:11</label>", outcome.Xml);
            Assert.Contains("attribute=\"var:14\"", outcome.Xml);
            Assert.Contains("attribute='var:14'", outcome.Xml);
        }

        [Fact]
        public void A_number_no_source_variable_carries_is_kept_and_reported()
        {
            var outcome = SaveAsVariableIdRemap.Apply(
                Form(Data("var:99"), Data("var:11")),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Eta", 11) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 14) }));

            Assert.Equal(1, outcome.Remapped);
            Assert.Equal(new[] { 99 }, outcome.Unmapped);
            Assert.Null(outcome.MissingInTarget);
            Assert.Equal(new[] { 99, 14 }, BindingIds(outcome.Xml));
        }

        [Fact]
        public void A_source_variable_the_target_does_not_declare_refuses_the_remap()
        {
            string form = Form(Data("var:15"), Data("var:11"));

            var outcome = SaveAsVariableIdRemap.Apply(
                form,
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Eta", 11), V("Lambda", 15) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 11) }));

            Assert.Equal("Lambda", outcome.MissingInTarget);
            Assert.Equal(0, outcome.Remapped);
            Assert.Same(form, outcome.Xml);
        }

        [Fact]
        public void A_target_variable_without_a_usable_id_counts_as_not_declared()
        {
            var outcome = SaveAsVariableIdRemap.Apply(
                Form(Data("var:15")),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Lambda", 15) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Lambda", 0) }));

            Assert.Equal("Lambda", outcome.MissingInTarget);
        }

        [Fact]
        public void After_a_remap_a_second_pass_with_the_new_ids_changes_nothing()
        {
            var first = SaveAsVariableIdRemap.Apply(
                Form(Data("var:11"), Item("var:12"), Data("var:15")),
                SaveAsVariableIdRemap.SourceNamesById(SourceVariables()),
                SaveAsVariableIdRemap.TargetIdsByName(TargetVariables()));

            // Once the ids agree by name, the target's own numbering is the source numbering for the next pass.
            var agreed = TargetVariables().ToArray();
            var second = SaveAsVariableIdRemap.Apply(
                first.Xml,
                SaveAsVariableIdRemap.SourceNamesById(agreed),
                SaveAsVariableIdRemap.TargetIdsByName(agreed));

            Assert.Equal(0, second.Remapped);
            Assert.Same(first.Xml, second.Xml);
        }

        [Fact]
        public void A_greater_than_sign_inside_a_quoted_value_does_not_end_the_tag_and_text_is_left_alone()
        {
            var outcome = SaveAsVariableIdRemap.Apply(
                Form("<data caption=\"a > b\" attribute=\"var:11\" />", "<label>a var:11 b</label>"),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Eta", 11) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 14) }));

            Assert.Equal(1, outcome.Remapped);
            Assert.Contains("<data caption=\"a > b\" attribute=\"var:14\" />", outcome.Xml);
            Assert.Contains("<label>a var:11 b</label>", outcome.Xml);
        }

        [Fact]
        public void Names_match_without_regard_to_case()
        {
            var outcome = SaveAsVariableIdRemap.Apply(
                Form(Data("var:11")),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("eta", 11) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 14) }));

            Assert.Equal(new[] { 14 }, BindingIds(outcome.Xml));
        }

        [Fact]
        public void A_source_id_that_two_variables_share_is_left_out_of_the_map()
        {
            var names = SaveAsVariableIdRemap.SourceNamesById(new[] { V("Alpha", 5), V("Beta", 5), V("Gamma", 6) });

            Assert.False(names.ContainsKey(5));
            Assert.Equal("Gamma", names[6]);

            var outcome = SaveAsVariableIdRemap.Apply(
                Form(Data("var:5")),
                names,
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Alpha", 5), V("Beta", 9) }));

            Assert.Equal(new[] { 5 }, outcome.Unmapped);
            Assert.Equal(0, outcome.Remapped);
        }

        [Fact]
        public void Ids_that_are_zero_or_negative_are_ignored_by_both_maps()
        {
            Assert.Empty(SaveAsVariableIdRemap.SourceNamesById(new[] { V("Zero", 0), V("Minus", -1) }));
            Assert.Empty(SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Zero", 0), V("Minus", -1) }));
        }

        [Fact]
        public void An_unreadable_id_in_the_source_skips_the_remap_and_keeps_the_webform()
        {
            string form = Form(Data("var:11"));

            var outcome = SaveAsVariableIdRemap.ForClone(form, new[] { V("Eta", -1) }, new[] { V("Eta", 14) });

            Assert.Equal(SaveAsVariableIdRemap.IdUnavailable, outcome.Skipped);
            Assert.Equal(0, outcome.Remapped);
            Assert.Same(form, outcome.Xml);
        }

        [Fact]
        public void An_unreadable_id_in_the_target_skips_the_remap()
        {
            string form = Form(Data("var:11"));

            var outcome = SaveAsVariableIdRemap.ForClone(form, new[] { V("Eta", 11) }, new[] { V("Eta", 0) });

            Assert.Equal(SaveAsVariableIdRemap.IdUnavailable, outcome.Skipped);
            Assert.Same(form, outcome.Xml);
        }

        [Fact]
        public void Variables_that_cannot_be_listed_skip_the_remap()
        {
            string form = Form(Data("var:11"));

            Assert.Equal(SaveAsVariableIdRemap.IdUnavailable, SaveAsVariableIdRemap.ForClone(form, null, new[] { V("Eta", 14) }).Skipped);
            Assert.Equal(SaveAsVariableIdRemap.IdUnavailable, SaveAsVariableIdRemap.ForClone(form, new[] { V("Eta", 11) }, null).Skipped);
        }

        // The <ucw> of the sidebar in the Unanimo fixture (src/gx-engine/tests/fixtures/unanimo_master/masterunanimosidebar.webform.txt,
        // line 28): its SidebarItems property is bound to var:5, the id of sidebarItems in that object.
        private const string SidebarUserControl = @"<ucw gxControlType=""-1054227045"" PATTERN_ELEMENT_CUSTOM_PROPERTIES=""&lt;Properties&gt;&lt;Property&gt;&lt;Name&gt;ControlName&lt;/Name&gt;&lt;Value&gt;SidebarMenu&lt;/Value&gt;&lt;/Property&gt;&lt;Property&gt;&lt;Name&gt;SidebarItems&lt;/Name&gt;&lt;Value&gt;var:5&lt;/Value&gt;&lt;/Property&gt;&lt;/Properties&gt;"" />";

        [Fact]
        public void A_user_control_property_bound_to_a_variable_is_rebound_to_the_target_id_and_the_rest_of_the_line_stays()
        {
            // sidebarItems has id 5 in the source and 9 in the target.
            var outcome = SaveAsVariableIdRemap.Apply(
                SidebarUserControl,
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("sidebarItems", 5) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("sidebarItems", 9) }));

            Assert.Equal(1, outcome.Remapped);
            Assert.Empty(outcome.Unmapped);
            Assert.Equal(SidebarUserControl.Replace("&lt;Value&gt;var:5&lt;/Value&gt;", "&lt;Value&gt;var:9&lt;/Value&gt;"), outcome.Xml);
        }

        [Fact]
        public void A_chain_of_payload_bindings_is_rebound_in_one_pass()
        {
            // Theta and Lambda swap their ids; a second pass over the new ids would turn var:15 back into var:12.
            var outcome = SaveAsVariableIdRemap.Apply(
                Form("<ucw PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Value&gt;var:12&lt;/Value&gt;&lt;Value&gt;var:15&lt;/Value&gt;\" />"),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Theta", 12), V("Lambda", 15) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Theta", 15), V("Lambda", 12) }));

            Assert.Equal(2, outcome.Remapped);
            Assert.Contains("&lt;Value&gt;var:15&lt;/Value&gt;&lt;Value&gt;var:12&lt;/Value&gt;", outcome.Xml);
        }

        [Fact]
        public void An_attribute_binding_and_a_payload_binding_in_one_document_are_both_rebound()
        {
            var outcome = SaveAsVariableIdRemap.Apply(
                Form(Data("var:11"), "<ucw PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Value&gt;var:15&lt;/Value&gt;\" />"),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Eta", 11), V("Lambda", 15) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 14), V("Lambda", 19) }));

            Assert.Equal(2, outcome.Remapped);
            Assert.Equal(new[] { 14 }, BindingIds(outcome.Xml));
            Assert.Contains("&lt;Value&gt;var:19&lt;/Value&gt;", outcome.Xml);
        }

        [Fact]
        public void Other_values_and_a_payload_outside_a_tag_are_left_alone()
        {
            // A <Value> that is text, a var number inside a text, a number with a letter after it, and a payload in text.
            string form = Form(
                "<ucw PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Value&gt;SidebarMenu&lt;/Value&gt;&lt;Value&gt;a var:11 b&lt;/Value&gt;&lt;Value&gt;var:11x&lt;/Value&gt;\" />",
                "<label>&lt;Value&gt;var:11&lt;/Value&gt;</label>");

            var outcome = SaveAsVariableIdRemap.Apply(
                form,
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Eta", 11) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Eta", 14) }));

            Assert.Equal(0, outcome.Remapped);
            Assert.Same(form, outcome.Xml);
        }

        [Fact]
        public void A_payload_number_is_matched_whole_so_var_1_does_not_match_var_12()
        {
            var outcome = SaveAsVariableIdRemap.Apply(
                Form("<ucw PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Value&gt;var:12&lt;/Value&gt;\" />"),
                SaveAsVariableIdRemap.SourceNamesById(new[] { V("Today", 1) }),
                SaveAsVariableIdRemap.TargetIdsByName(new[] { V("Today", 7) }));

            Assert.Equal(0, outcome.Remapped);
            Assert.Equal(new[] { 12 }, outcome.Unmapped);
        }

        [Fact]
        public void The_primary_id_is_the_id_property_read_as_an_int()
        {
            Assert.Equal(14, SaveAsVariableIdRemap.PrimaryVariableId(new WithIdProperty { Id = 14 }));
        }

        [Fact]
        public void The_primary_id_reads_a_short_id_property_too()
        {
            Assert.Equal(9, SaveAsVariableIdRemap.PrimaryVariableId(new WithShortIdProperty { Id = 9 }));
        }

        [Fact]
        public void An_object_without_an_id_property_is_unavailable_and_is_not_given_a_position()
        {
            // Index, Number and AttId are values an enumeration or a property bag can carry; none of them is the id.
            Assert.Equal(-1, SaveAsVariableIdRemap.PrimaryVariableId(new WithOnlyPositionProperties()));
        }

        [Fact]
        public void A_zero_or_negative_id_is_unavailable()
        {
            Assert.Equal(-1, SaveAsVariableIdRemap.PrimaryVariableId(new WithIdProperty { Id = 0 }));
            Assert.Equal(-1, SaveAsVariableIdRemap.PrimaryVariableId(new WithIdProperty { Id = -3 }));
        }

        [Fact]
        public void An_id_property_that_throws_is_unavailable_and_no_other_property_is_tried()
        {
            Assert.Equal(-1, SaveAsVariableIdRemap.PrimaryVariableId(new WithThrowingId()));
        }

        private sealed class WithIdProperty
        {
            public int Id { get; set; }
        }

        private sealed class WithShortIdProperty
        {
            public short Id { get; set; }
        }

        private sealed class WithOnlyPositionProperties
        {
            public int Index { get { return 3; } }

            public int Number { get { return 2; } }

            public int AttId { get { return 7; } }
        }

        private sealed class WithThrowingId
        {
            public int Id { get { throw new InvalidOperationException("the id is not readable"); } }

            public int Index { get { return 3; } }
        }

        private static string Form(params string[] tags)
        {
            return "<GxMultiForm rootId=\"1\">\n" + string.Join("\n", tags) + "\n</GxMultiForm>";
        }

        private static string Data(string binding) => "<data attribute=\"" + binding + "\" />";

        private static string Item(string binding) => "<item attribute=\"" + binding + "\" />";

        private static SaveAsVariableIdRemap.NamedId V(string name, int id) => new SaveAsVariableIdRemap.NamedId(name, id);

        // The source ids of the permutation case, in the order the source enumerates them.
        private static IEnumerable<SaveAsVariableIdRemap.NamedId> SourceVariables()
        {
            return new[]
            {
                V("Alpha", 5), V("Beta", 6), V("Gamma", 7), V("Delta", 8), V("Epsilon", 9), V("Zeta", 10), V("Eta", 11),
                V("Lambda", 15), V("Mu", 16), V("Nu", 17), V("Xi", 18), V("Omicron", 19), V("Pi", 20),
                V("Theta", 12), V("Iota", 13), V("Kappa", 14),
                V("Today", 1), V("Time", 2), V("Pgmname", 3), V("Pgmdesc", 4),
            };
        }

        // The target numbers the same declarations in the order it reads them, after the system variables it already has.
        private static IEnumerable<SaveAsVariableIdRemap.NamedId> TargetVariables()
        {
            return new[]
            {
                V("Today", 1), V("Time", 2), V("Pgmname", 3), V("Pgmdesc", 4),
                V("Alpha", 5), V("Beta", 6), V("Gamma", 7), V("Delta", 8), V("Epsilon", 9), V("Zeta", 10), V("Eta", 11),
                V("Lambda", 12), V("Mu", 13), V("Nu", 14), V("Xi", 15), V("Omicron", 16), V("Pi", 17),
                V("Theta", 18), V("Iota", 19), V("Kappa", 20),
            };
        }

        private static IEnumerable<int> BindingIds(string xml)
        {
            return Regex.Matches(xml, "attribute=\"var:(\\d+)\"").Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        }
    }
}
