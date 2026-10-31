using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Finds the WorkWithPlus templates that break the WorkWithPlus editor. The editor reads WWPTemplate_Type of every
    /// template in the KB when it builds its template list, so one template with an empty type, or with an empty or
    /// malformed WWPTemplate_TemplateXml, makes the editor fail for every instance ("Unknown TemplateType").
    /// Pure: the caller reads the properties through the SDK and passes them in.
    /// </summary>
    internal static class WwpTemplateHealth
    {
        /// <summary>Entries listed at most; the total is always reported. Same cap as the doctor's top-N lists.</summary>
        internal const int ListLimit = 5;

        internal const string EmptyType = "emptyType";
        internal const string EmptyTemplateXml = "emptyTemplateXml";
        internal const string MalformedTemplateXml = "malformedTemplateXml";

        /// <summary>A template as the SDK read it. <see cref="Unreadable"/> means its properties could not be read.</summary>
        internal sealed class TemplateRecord
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string TemplateXml { get; set; }
            public bool Unreadable { get; set; }
        }

        /// <summary>The problem that breaks the editor, or null when the template is usable.</summary>
        internal static string Diagnose(string type, string templateXml)
        {
            if (string.IsNullOrWhiteSpace(type)) return EmptyType;
            if (string.IsNullOrWhiteSpace(templateXml)) return EmptyTemplateXml;
            return IsWellFormed(templateXml) ? null : MalformedTemplateXml;
        }

        internal static bool IsWellFormed(string templateXml)
        {
            try
            {
                XDocument.Parse(templateXml.Trim());
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// The doctor block: how many templates were read, how many break the editor, how many could not be read, and
        /// the first <see cref="ListLimit"/> broken ones. An unreadable template is counted apart and never classified.
        /// </summary>
        internal static JObject Summarize(IEnumerable<TemplateRecord> templates)
        {
            int read = 0;
            int unreadable = 0;
            int invalid = 0;
            var listed = new JArray();
            foreach (var template in templates)
            {
                if (template.Unreadable)
                {
                    unreadable++;
                    continue;
                }

                read++;
                string problem = Diagnose(template.Type, template.TemplateXml);
                if (problem == null) continue;

                invalid++;
                if (listed.Count < ListLimit)
                    listed.Add(new JObject { ["name"] = template.Name, ["problem"] = problem });
            }

            var block = new JObject
            {
                ["available"] = true,
                ["read"] = read,
                ["invalid"] = invalid,
                ["unreadable"] = unreadable,
                ["listLimit"] = ListLimit,
                ["listed"] = listed
            };
            if (invalid > 0)
            {
                block["severity"] = "high";
                block["hint"] = "Correct each listed template with genexus_properties set (WWPTemplate_Type, WWPTemplate_TemplateXml). "
                    + "Delete a template only with the WorkWithPlus Settings Editor.";
            }
            return block;
        }
    }
}
