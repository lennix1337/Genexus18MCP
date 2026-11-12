using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Common.Diagnostics;
using Artech.Genexus.Common.Parts;
using Artech.Genexus.Common.Parts.Form;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Issue #963: the WinForm part (text/5250 and Windows forms of a WorkPanel) as the SDK's own
    /// Part XML, the shape an XPZ export carries: Document/Form/Type/Class/ClassGuid/SizeUnit/Info/
    /// Canvas/Elements. Reading uses the part's KMW serializer (<c>Serialize</c>) and writing uses
    /// the importer the XPZ import itself runs (<c>KMW.WinForm.Deserialize</c>), so this class owns
    /// neither the element model nor the property syntax.
    /// </summary>
    internal static class WinFormPartXml
    {
        internal const string PartTypeGuid = "4c28dfb9-f83b-46f0-9cf3-f7e090b525d5";

        internal static WinFormPart Find(KBObject obj)
            => GxMcp.Worker.Structure.PartAccessor.GetPart(obj, "WinForm") as WinFormPart;

        /// <summary>
        /// The part's serialized Document. An untouched default form serializes without one in an
        /// export, so it is expanded to the default Windows form: the text read returns is then
        /// always something <see cref="Validate"/> accepts and a write can send back.
        /// </summary>
        internal static string Serialize(WinFormPart part)
        {
            string plain = Write(part, SerializationMode.Default);
            return plain.Contains("<Document") ? plain : Write(part, SerializationMode.ExpandDefault);
        }

        private static string Write(WinFormPart part, SerializationMode mode)
        {
            var writer = new StringWriter();
            part.Serialize(writer, mode);
            return writer.ToString();
        }

        /// <summary>
        /// Checks that <paramref name="xml"/> holds a Form the SDK deserializer will actually apply
        /// (it returns silently on anything else) and returns it as a Part element. Accepts a Part
        /// root, as read returns, or a bare Document root.
        /// </summary>
        internal static string Validate(string xml)
        {
            XElement root;
            try { root = XDocument.Parse(xml ?? string.Empty).Root; }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is ArgumentException)
            {
                throw new FormatException("The WinForm content is not well-formed XML: " + ex.Message);
            }

            XElement document;
            if (root.Name.LocalName == "Part")
            {
                string type = (string)root.Attribute("type");
                if (type != null && !string.Equals(type, PartTypeGuid, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("The Part type '" + type + "' is not the WinForm part (" + PartTypeGuid + ").");
                document = root.Element("Document");
            }
            else if (root.Name.LocalName == "Document")
            {
                document = root;
            }
            else
            {
                throw new FormatException("The WinForm content must have a <Part> or <Document> root, found <" + root.Name.LocalName + ">.");
            }

            XElement form = document?.Element("Form");
            if (form == null)
                throw new FormatException("The WinForm content has no <Document><Form>; send the full serialized part as genexus_read returns it for a non-default form.");

            string formType = ((string)form.Element("Type") ?? string.Empty).Trim();
            if (formType.Length == 0)
                throw new FormatException("<Form> has no <Type>; expected one of " + string.Join(", ", Enum.GetNames(typeof(FormType))) + ".");
            if (!Enum.IsDefined(typeof(FormType), formType))
                throw new FormatException("<Type>" + formType + "</Type> is not a form type; expected one of " + string.Join(", ", Enum.GetNames(typeof(FormType))) + ".");

            string classGuid = ((string)form.Element("ClassGuid") ?? string.Empty).Trim();
            if (classGuid.Length > 0 && !Guid.TryParse(classGuid, out _))
                throw new FormatException("<ClassGuid>" + classGuid + "</ClassGuid> is not a GUID.");

            return new XElement("Part", new XAttribute("type", PartTypeGuid), document).ToString(SaveOptions.None);
        }

        /// <summary>Controls under every Elements container of the form's Canvas, at any depth.</summary>
        internal static int CountElements(string xml)
        {
            XElement canvas = XDocument.Parse(xml).Descendants("Canvas").FirstOrDefault();
            return canvas == null ? 0 : canvas.DescendantsAndSelf("Elements").Sum(e => e.Elements().Count());
        }

        internal const string CanonicalContentField = "canonicalContent";

        /// <summary>
        /// Removes the form a WinForm write staged from its result and returns it, or null when the
        /// response carries none. It is a comparison input for the receipt, not part of the response.
        /// </summary>
        internal static string TakeCanonicalContent(JObject response)
        {
            if (!(response?["result"] is JObject result)) return null;
            string content = result[CanonicalContentField]?.Type == JTokenType.String ? (string)result[CanonicalContentField] : null;
            result.Remove(CanonicalContentField);
            return content;
        }

        /// <summary>Runs the XPZ importer over <paramref name="validatedXml"/>; returns its message count.</summary>
        internal static int Apply(WinFormPart part, string validatedXml)
        {
            var navigator = new XPathDocument(new StringReader(validatedXml)).CreateNavigator();
            navigator.MoveToFirstChild();
            var messages = new OutputMessages();
            Artech.Genexus.Common.KMW.WinForm.Deserialize(part, navigator, messages);
            // The importer leaves the part clean, so without this the object save writes nothing for it.
            part.Dirty = true;
            return messages.Count;
        }
    }
}
