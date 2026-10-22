using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// The SDK side of regenerating the objects a WorkWithPlus instance derives: which objects
    /// those are, and the one engine call that rebuilds them.
    ///
    /// <para>
    /// The derived objects are not found by name. The pattern engine itself treats every object
    /// whose parent is the PatternInstance as owned by it (<c>Model.Objects.GetChildren</c>),
    /// which is also what it deletes when the instance stops generating one. Using the same
    /// relation keeps this correct when the KB renames the family through the Settings naming
    /// attributes.
    /// </para>
    ///
    /// <para>
    /// The rebuild is <c>PatternEngine.ApplyPattern(PatternInstance, ApplySettings)</c>, the
    /// engine's public entry point and the one the IDE reaches from Apply Pattern. It
    /// regenerates the derived objects from the persisted instance, saves them, updates and
    /// saves the parent, and runs the package's own end-of-build step, in that order, so the
    /// parent is never saved against a helper that still has the old contract. The apply path of
    /// <c>genexus_apply_pattern</c> does not call it for an existing WorkWithPlus instance; it
    /// re-projects the parent only.
    /// </para>
    /// </summary>
    internal static class WwpDerivedEngine
    {
        private const string PatternEngineType = "Artech.Packages.Patterns.PatternEngine";
        private const string ApplySettingsType = "Artech.Packages.Patterns.Engine.ApplySettings";

        internal sealed class RunResult
        {
            internal bool Invoked;
            internal bool EngineReportedSuccess;
            internal string Failure;
            internal JArray Messages = new JArray();
            internal bool MessagesTruncated;
        }

        /// <summary>
        /// The objects <paramref name="instance"/> owns, in a stable order (type, then name). The
        /// instance itself and its parent are not derived objects and are never listed.
        /// </summary>
        internal static List<KBObject> EnumerateDerived(KBObject instance)
        {
            var derived = new List<KBObject>();
            if (instance?.Model == null) return derived;
            foreach (KBObject child in instance.Model.Objects.GetChildren(instance))
            {
                if (child == null || child.Guid == instance.Guid) continue;
                derived.Add(child);
            }
            return derived
                .OrderBy(o => o.TypeDescriptor?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Rebuilds the derived objects of <paramref name="instance"/> through the engine. The engine
        /// reports a generated object that could not be saved as a <c>false</c> result and writes the
        /// reason to the SDK output, which is captured here and returned either way.
        /// </summary>
        internal static bool IsAvailable(KBObject instance) => FindApply(instance, out _) != null;

        private static MethodInfo FindApply(KBObject instance, out Type settingsType)
        {
            Type engine = SdkReflection.FindType(PatternEngineType);
            settingsType = SdkReflection.FindType(ApplySettingsType);
            if (engine == null || settingsType == null || instance == null) return null;
            Type settings = settingsType;
            return engine.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ApplyPattern" && m.ReturnType == typeof(bool)
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == settings
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(instance));
        }

        internal static RunResult Apply(KBObject instance)
        {
            var result = new RunResult();
            MethodInfo apply = FindApply(instance, out Type settingsType);
            if (apply == null)
            {
                result.Failure = "PatternEngine.ApplyPattern(PatternInstance, ApplySettings) is not available in this SDK build.";
                return result;
            }

            object settings = Activator.CreateInstance(settingsType);
            // The engine reports through the IDE output pane; the section header would only
            // open a pane that does not exist here. Per-line messages are still captured.
            SetProperty(settings, "OutputSection", false);
            SetProperty(settings, "OutputLevel", TraceLevel.Info);
            // Refresh mode is how the engine itself batches instance regeneration before a
            // build: it rebuilds the instance's own objects and skips the KB-wide global
            // objects (Design System helpers, shared procedures). Without it a run for one
            // combo would also rewrite objects that belong to no instance. The setter is
            // not public, so a build that does not have it cannot be confined: refuse.
            if (!SetProperty(settings, "IsRefresh", true))
            {
                result.Failure = "This SDK build cannot confine the regeneration to the instance's own objects (ApplySettings.IsRefresh is missing); nothing was run.";
                return result;
            }

            using (TransferDiagnostics diagnostics = TransferDiagnostics.ForSdk())
            {
                try
                {
                    result.Invoked = true;
                    result.EngineReportedSuccess = (bool)apply.Invoke(null, new[] { instance, settings });
                }
                catch (TargetInvocationException tie)
                {
                    Exception inner = tie.InnerException ?? tie;
                    result.Failure = inner.GetType().Name + ": " + inner.Message;
                }
                result.Messages = diagnostics.Messages;
                result.MessagesTruncated = diagnostics.Truncated;
            }
            if (result.Failure == null && !result.EngineReportedSuccess)
                result.Failure = "The pattern engine reported that at least one derived object, or the parent, was not saved.";
            return result;
        }

        private static bool SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo setter = property?.GetSetMethod(nonPublic: true);
            if (setter == null) return false;
            setter.Invoke(target, new[] { value });
            return true;
        }
    }
}
