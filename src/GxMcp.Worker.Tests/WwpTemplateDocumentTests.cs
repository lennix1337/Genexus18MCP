using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The layout of the template text. The round trip reads the text back with a reader written for these tests, so the escapes
    // are checked against a second reading of the text and not only against the writer.
    public sealed class WwpTemplateDocumentTests
    {
        // The official text of the template, as the brief's sample shows it, with LF line endings.
        private static readonly string OfficialSample = string.Join("\n", new[]
        {
            "WorkWithPlusforWebTemplate SynthTemplate",
            "{",
            "\t#Properties",
            "\t\tName = \"SynthTemplate\"",
            "",
            "\t\tDescription = \"Synth description\"",
            "",
            "\t\tTemplateCategory = \"SynthCategory\"",
            "",
            "\t\tTemplateCategoryOrder = \"0\"",
            "",
            "\t\tTemplateType = \"Trn.Selection\"",
            "",
            "\t\tTemplateXml = \"<selection Name=\\\"SynthTemplate\\\" instanceType=\\\"WebPanel\\\"><table name=\\\"TableMain\\\" /></selection>\"",
            "",
            "\t\tMainTemplate = \"Main\"",
            "",
            "\t\tInstanceType = \"WebPanel\"",
            "",
            "\t\tPreviewImage = \"\"",
            "",
            "\t\tIsDefault = \"False\"",
            "",
            "\t\t",
            "\t#End",
            "",
            "}",
            ""
        });

        [Fact]
        public void The_header_keyword_is_the_type_without_spaces_and_the_document_reads_back_as_a_properties_part()
        {
            Assert.Equal("WorkWithPlusforWebTemplate", WwpTemplateDocument.Keyword);
            string document = WwpTemplateDocument.Build("SynthTemplate", SampleValues());

            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(document, out string type, out string name, out Dictionary<string, string> parts, out _));
            Assert.Equal("WorkWithPlusforWebTemplate", type);
            Assert.Equal("SynthTemplate", name);
            Assert.Contains("Properties", parts.Keys);
        }

        [Fact]
        public void Each_official_name_reads_the_sdk_property_it_stands_for()
        {
            Assert.Null(WwpTemplateDocument.SdkNameOf("Name"));
            Assert.Equal("Description", WwpTemplateDocument.SdkNameOf("Description"));
            Assert.Equal("WWPTemplate_Category", WwpTemplateDocument.SdkNameOf("TemplateCategory"));
            Assert.Equal("WWPTemplate_CategoryOrder", WwpTemplateDocument.SdkNameOf("TemplateCategoryOrder"));
            Assert.Equal("WWPTemplate_Type", WwpTemplateDocument.SdkNameOf("TemplateType"));
            Assert.Equal("WWPTemplate_TemplateXml", WwpTemplateDocument.SdkNameOf("TemplateXml"));
            Assert.Equal("WWPTemplate_MainTemplate", WwpTemplateDocument.SdkNameOf("MainTemplate"));
            Assert.Equal("WWPTemplate_InstanceType", WwpTemplateDocument.SdkNameOf("InstanceType"));
            Assert.Equal("WWPTemplate_PreviewImage", WwpTemplateDocument.SdkNameOf("PreviewImage"));
        }

        [Fact]
        public void The_document_has_the_layout_of_the_official_text_byte_for_byte()
        {
            Assert.Equal(OfficialSample, WwpTemplateDocument.Build("SynthTemplate", SampleValues()));
        }

        [Fact]
        public void The_properties_are_written_in_the_official_order_whatever_the_order_of_the_values()
        {
            var reversed = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> pair in SampleValues().Reverse()) reversed[pair.Key] = pair.Value;

            Assert.Equal(OfficialSample, WwpTemplateDocument.Build("SynthTemplate", reversed));
        }

        [Fact]
        public void A_property_the_values_do_not_hold_has_no_line()
        {
            var values = SampleValues();
            values.Remove("IsDefault");

            string document = WwpTemplateDocument.Build("SynthTemplate", values);

            Assert.DoesNotContain("IsDefault", document);
            Assert.Contains("\t\tPreviewImage = \"\"\n\n\t\t\n\t#End\n", document);
        }

        [Fact]
        public void An_empty_value_is_written_between_two_quotes()
        {
            string document = WwpTemplateDocument.Build("SynthTemplate", SampleValues());

            Assert.Contains("\t\tPreviewImage = \"\"\n", document);
        }

        [Fact]
        public void The_escapes_are_the_convention_of_this_export_and_the_double_quote_is_the_official_one()
        {
            Assert.Equal(@"a\\b", WwpTemplateDocument.EscapeValue(@"a\b"));
            Assert.Equal("say \\\"hi\\\"", WwpTemplateDocument.EscapeValue("say \"hi\""));
            Assert.Equal(@"line1\r\nline2", WwpTemplateDocument.EscapeValue("line1\r\nline2"));
            Assert.Equal(@"tab\there", WwpTemplateDocument.EscapeValue("tab\there"));
            Assert.Equal("<x a=\\\"1\\\"/>", WwpTemplateDocument.EscapeValue("<x a=\"1\"/>"));
            Assert.Equal(string.Empty, WwpTemplateDocument.EscapeValue(string.Empty));
            Assert.Equal(string.Empty, WwpTemplateDocument.EscapeValue(null));
        }

        [Fact]
        public void Every_value_reads_back_to_the_value_it_was_built_from()
        {
            var values = new Dictionary<string, string>
            {
                ["Name"] = "SynthTemplate",
                ["Description"] = "back\\slash and \"quotes\"",
                ["TemplateXml"] = "<selection Name=\"SynthTemplate\">\r\n\t<table name=\"TableMain\" />\n</selection>",
                ["MainTemplate"] = "\\n is not a line break",
                ["PreviewImage"] = string.Empty
            };

            string document = WwpTemplateDocument.Build("SynthTemplate", values);
            Dictionary<string, string> read = ReadProperties(document);

            Assert.Equal(values.Keys.OrderBy(key => Array.IndexOf(WwpTemplateDocument.PropertyOrder, key)), read.Keys);
            foreach (KeyValuePair<string, string> pair in values) Assert.Equal(pair.Value, read[pair.Key]);
        }

        private static Dictionary<string, string> SampleValues()
        {
            return new Dictionary<string, string>
            {
                ["Name"] = "SynthTemplate",
                ["Description"] = "Synth description",
                ["TemplateCategory"] = "SynthCategory",
                ["TemplateCategoryOrder"] = "0",
                ["TemplateType"] = "Trn.Selection",
                ["TemplateXml"] = "<selection Name=\"SynthTemplate\" instanceType=\"WebPanel\"><table name=\"TableMain\" /></selection>",
                ["MainTemplate"] = "Main",
                ["InstanceType"] = "WebPanel",
                ["PreviewImage"] = string.Empty,
                ["IsDefault"] = "False"
            };
        }

        // Reads the #Properties section of a template document: one "Name = \"value\"" line per property, the escapes undone.
        private static Dictionary<string, string> ReadProperties(string document)
        {
            var values = new Dictionary<string, string>();
            bool inSection = false;
            foreach (string line in document.Split('\n'))
            {
                if (line == "\t#Properties") inSection = true;
                else if (line == "\t#End") break;
                else if (inSection && line.StartsWith("\t\t", StringComparison.Ordinal) && line.Trim().Length > 0)
                {
                    int equals = line.IndexOf(" = \"", StringComparison.Ordinal);
                    values[line.Substring(2, equals - 2)] = Unescape(line.Substring(equals + 4, line.Length - equals - 5));
                }
            }
            return values;
        }

        private static string Unescape(string escaped)
        {
            var text = new StringBuilder();
            for (int index = 0; index < escaped.Length; index++)
            {
                char ch = escaped[index];
                if (ch != '\\')
                {
                    text.Append(ch);
                    continue;
                }
                char next = escaped[++index];
                switch (next)
                {
                    case 'r': text.Append('\r'); break;
                    case 'n': text.Append('\n'); break;
                    case 't': text.Append('\t'); break;
                    default: text.Append(next); break;
                }
            }
            return text.ToString();
        }
    }
}
