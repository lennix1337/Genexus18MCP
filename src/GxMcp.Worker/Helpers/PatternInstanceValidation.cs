using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Asks the pattern itself why it refused to apply. The engine's first-apply call
    /// returns nothing when the pattern's own validation rejects the instance, so a
    /// refusal such as WorkWithPlus's "a Theme must be imported (Startup Wizard)" used to
    /// surface only as a generic no-op. The instance validated here is the one the
    /// engine builds in memory for the apply; nothing is saved.
    /// </summary>
    internal static class PatternInstanceValidation
    {
        internal static List<string> Collect(KBObject parent, object patternDefinition)
        {
            if (parent == null || patternDefinition == null) return new List<string>();
            try
            {
                Assembly patterns = patternDefinition.GetType().Assembly;
                Type virtualPart = patterns.GetType("Artech.Packages.Patterns.Objects.PatternVirtualPart", false);
                MethodInfo getItem = virtualPart?.GetMethod("GetPatternItem", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(KBObject), patternDefinition.GetType() }, null);
                object item = getItem?.Invoke(null, new[] { parent, patternDefinition });
                object instance = item?.GetType().GetProperty("PatternInstance")?.GetValue(item);
                if (instance == null) return new List<string>();

                MethodInfo validate = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Validate" && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "OutputMessages");
                if (validate == null) return new List<string>();
                object messages = Activator.CreateInstance(validate.GetParameters()[0].ParameterType);
                bool valid = (bool)validate.Invoke(instance, new[] { messages });
                if (valid) return new List<string>();
                return SplitErrors(messages.GetType().GetProperty("ErrorText")?.GetValue(messages) as string);
            }
            catch (Exception ex)
            {
                Logger.Debug("[PATTERN-VALIDATE] " + (ex.InnerException?.Message ?? ex.Message));
                return new List<string>();
            }
        }

        internal static List<string> SplitErrors(string errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText)) return new List<string>();
            return errorText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }
}
