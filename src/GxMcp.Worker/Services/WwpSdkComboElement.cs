using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Common.Properties;
using Artech.Packages.Patterns.Objects;
using Artech.Packages.Patterns.Specification;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// <see cref="IWwpComboElement"/> over a native WorkWithPlus <c>attribute</c> element.
    /// Reads and conversions go through the element's specification and property bag, the
    /// same model <c>PatternInstanceElementAttributes.FromXml/ToXml</c> use; writes go through
    /// the Pattern SDK's change command.
    /// </summary>
    internal sealed class WwpSdkComboElement : IWwpComboElement
    {
        private readonly PatternInstanceElement _element;

        internal WwpSdkComboElement(PatternInstanceElement element)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
        }

        internal PatternInstanceElement Element => _element;

        public string Path => _element.Path;

        public string Reference => Spec("attribute")?.GetValueString(_element);

        private SpecificationAttribute Spec(string sdkName) =>
            _element.Specification.Attributes.FirstOrDefault(a => string.Equals(a.Name, sdkName, StringComparison.Ordinal));

        private string Bag(SpecificationAttribute spec) => spec.PropertyDescriptorName;

        public bool HasProperty(string sdkName) => Spec(sdkName) != null;

        public string VisibleIf(string sdkName) => Spec(sdkName)?.VisibleIf;

        public bool IsReadOnly(string sdkName)
        {
            SpecificationAttribute spec = Spec(sdkName);
            if (spec == null) return false;
            return spec.ReadOnly || _element.Attributes.GetPropertyDescriptor(Bag(spec))?.IsReadOnly == true;
        }

        public bool IsApplicable(string sdkName)
        {
            SpecificationAttribute spec = Spec(sdkName);
            if (spec == null) return false;
            var descriptor = _element.Attributes.GetPropertyDescriptor(Bag(spec));
            return descriptor == null || descriptor.IsApplicable;
        }

        public string Effective(string sdkName)
        {
            SpecificationAttribute spec = Spec(sdkName);
            return spec == null ? null : StorageValue(spec);
        }

        // The XML stores StringStorage text, and the specification's VisibleIf expressions were
        // written against it, so effective values are rendered the same way.
        private string StorageValue(SpecificationAttribute spec)
        {
            string bag = Bag(spec);
            try
            {
                object value = _element.Attributes.GetPropertyValue(bag);
                if (value == null) return string.Empty;
                return _element.Attributes.ConvertPropertyValueToString<StringStorage>(bag, value) ?? string.Empty;
            }
            catch (Exception)
            {
                return spec.GetValueString(_element) ?? string.Empty;
            }
        }

        public IReadOnlyDictionary<string, string> EffectiveValues()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SpecificationAttribute spec in _element.Specification.Attributes)
                values[spec.Name] = StorageValue(spec);
            return values;
        }

        public bool TryConvert(string sdkName, string text, out object typed, out string storage, out string error)
        {
            typed = null;
            storage = null;
            error = null;
            SpecificationAttribute spec = Spec(sdkName);
            if (spec == null) { error = "not declared by the element specification"; return false; }
            string bag = Bag(spec);
            try
            {
                typed = _element.Attributes.ConvertPropertyValueFromString<StringStorage>(bag, text);
                if (typed == null && text.Length > 0)
                {
                    error = "the converter produced no value for '" + text + "'";
                    return false;
                }
                storage = typed == null ? string.Empty : _element.Attributes.ConvertPropertyValueToString<StringStorage>(bag, typed) ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex.InnerException ?? ex;
                error = root.GetType().Name + ": " + root.Message;
                return false;
            }
        }

        public bool Apply(string sdkName, object typed, string expectedStorage)
        {
            SpecificationAttribute spec = Spec(sdkName);
            if (spec == null) return false;
            return typed == null
                ? PatternSemanticAttributeWriter.ResetSemanticAttribute(_element, Bag(spec), expectedStorage)
                : PatternSemanticAttributeWriter.ApplySemanticAttributeObject(_element, Bag(spec), typed, expectedStorage);
        }
    }
}
