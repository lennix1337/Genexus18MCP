using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Microsoft.Build.Framework;

namespace GxMcp.Worker.Services
{
    // SDK-bound half of lifecycle action=specify_only (the decisions live in SpecifyOnly.cs).
    internal static partial class InProcessBuildRunner
    {
        private const string BuildDaemonClientTypeName = "Artech.Genexus.Common.Build.BuildDaemonClient";
        private const string UnreachableObjectCode = "spc0217";

        // Test seam: when set, the strict runner takes its environment from here instead of the SDK.
        internal static Func<IBuildEngine, Func<string>, ISpecifyOnlyEnvironment> SpecifyOnlyEnvironmentForTests;

        // The environment specify_only reads and acts through. engine is null when only the facts are
        // needed (dryRun, the request-time refusal); the specifier is only invoked with an engine.
        internal static ISpecifyOnlyEnvironment CreateSpecifyOnlyEnvironment(
            object kbHandle, IBuildEngine engine, Func<string> generatorOutputPath)
        {
            if (kbHandle == null) return null;
            try
            {
                if (!EnsureTypesLoaded()) return null;
            }
            catch (Exception ex)
            {
                LogExceptionChain("SpecifyOnly-EnsureTypesLoaded", ex);
                return null;
            }
            return new SdkSpecifyOnlyEnvironment(kbHandle, engine, generatorOutputPath);
        }

        // Runs the strict specification on the build task's thread, under the KB lock the caller holds.
        // Whatever the SDK did not report as an itemized line (a refusal, a failure to run, generation seen,
        // an unreachable object, a concurrent change) is emitted here as an error line carrying
        // SpecifyOnlyCodes.LineMarker, so the task's status ends Failed with the reason; the receipt reports
        // those with their code instead of re-parsing them. The verdict is the receipt's own: both ask
        // SpecifyOnlyJudge, so the status and the receipt cannot disagree.
        internal static InProcessBuildOutcome RunSpecifyOnlyStrict(
            BuildService.BuildTaskStatus status,
            object kbHandle,
            SpecifyOnlyRequest request,
            Action<string, bool> lineSink,
            Func<string> generatorOutputPath)
        {
            var engine = new InProcessBuildEngine(lineSink);
            ISpecifyOnlyEnvironment environment = SpecifyOnlyEnvironmentForTests != null
                ? SpecifyOnlyEnvironmentForTests(engine, generatorOutputPath)
                : new SdkSpecifyOnlyEnvironment(kbHandle, engine, generatorOutputPath);
            lineSink("[BUILD-INPROCESS] specify_only — running the specifier for one object by GUID; Generate, Compile, reorganization and deploy are NOT run.", false);

            SpecifyOnlyRun run = new SpecifyOnlyExecutor(environment).Execute(request);
            int errors;
            lock (status._lock)
            {
                status.SpecifyOnlyRun = run;
                errors = status.ErrorCount;
            }

            // The task is still Running while the strict runner decides: the status is not an input here, the
            // terminal status the caller writes afterwards is (BuildTaskStatus passes it when it renders).
            SpecifyOnlyVerdict verdict = SpecifyOnlyJudge.Judge(run, specifierErrors: errors > 0);
            if (verdict.Succeeded) return InProcessBuildOutcome.Succeeded;
            if (verdict.OwnDiagnostic) lineSink(SpecifyOnlyCodes.Line(verdict.Code, verdict.Message), true);
            return InProcessBuildOutcome.FailedWithDiagnostics;
        }

        private sealed class SdkSpecifyOnlyEnvironment : ISpecifyOnlyEnvironment
        {
            private readonly object _kb;
            private readonly IBuildEngine _engine;
            private readonly Func<string> _generatorOutputPath;

            public SdkSpecifyOnlyEnvironment(object kbHandle, IBuildEngine engine, Func<string> generatorOutputPath)
            {
                _kb = kbHandle;
                _engine = engine;
                _generatorOutputPath = generatorOutputPath;
            }

            private object DesignModel => _kb.GetType().GetProperty("DesignModel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_kb);

            private object TargetModel
            {
                get
                {
                    object environment = DesignModel?.GetType().GetProperty("Environment", BindingFlags.Public | BindingFlags.Instance)?.GetValue(DesignModel);
                    return environment?.GetType().GetProperty("TargetModel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(environment);
                }
            }

            private static global::Artech.Architecture.Common.Objects.KBObject FindByGuid(object model, Guid guid)
            {
                if (model == null) return null;
                var found = ResolveTargetKBObject(model, guid.ToString("D")) as global::Artech.Architecture.Common.Objects.KBObject;
                // ResolveTargetKBObject falls back to name resolution for text that is not a known GUID;
                // only an object that really carries the requested GUID counts.
                return found != null && found.Guid == guid ? found : null;
            }

            private MethodInfo FindCopyMethod()
                => _kb.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "CopyEntity" && m.GetParameters().Length == 3);

            private static PropertyInfo FindConcurrentGenerationProperty()
            {
                Type daemonClient = _typeGenexusBLServices?.Assembly.GetType(BuildDaemonClientTypeName, throwOnError: false);
                PropertyInfo property = daemonClient?.GetProperty("ConcurrentGeneration", BindingFlags.Public | BindingFlags.Static);
                return property != null && property.CanRead && property.CanWrite && property.PropertyType == typeof(bool)
                    ? property
                    : null;
            }

            private static object GetSpecifier()
                => _typeGenexusBLServices?.GetProperty("Specifier", Compatibility.SdkMemberProbe.Static)?.GetValue(null);

            private static MethodInfo FindSpecifyObjects(object specifier, object model)
            {
                if (specifier == null || _typeBuildOptions == null) return null;
                return specifier.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "SpecifyObjects"
                        && m.ReturnType == typeof(bool)
                        && m.GetParameters().Length == 3
                        && (model == null || m.GetParameters()[0].ParameterType.IsInstanceOfType(model))
                        && m.GetParameters()[1].ParameterType.IsGenericType
                        && m.GetParameters()[2].ParameterType == _typeBuildOptions);
            }

            public SpecifyOnlyFacts ReadFacts(Guid guid)
            {
                object designModel = DesignModel;
                if (designModel == null)
                    throw new InvalidOperationException("The Knowledge Base has no design model; it is not open.");
                object targetModel = TargetModel;
                bool separate = targetModel != null && !ReferenceEquals(targetModel, designModel);

                var facts = new SpecifyOnlyFacts
                {
                    SeparateEnvironment = separate,
                    NoTargetModel = targetModel == null,
                    InProcessDisabled = string.Equals(Environment.GetEnvironmentVariable("GXMCP_INPROCESS_BUILD"), "0", StringComparison.OrdinalIgnoreCase),
                    SpecifierAvailable = FindSpecifyObjects(GetSpecifier(), targetModel ?? designModel) != null,
                    GenerationSuppressible = FindConcurrentGenerationProperty() != null,
                    CopyApiAvailable = FindCopyMethod() != null
                };

                var design = FindByGuid(designModel, guid);
                if (design == null) return facts;
                facts.ObjectFound = true;
                facts.Name = design.Name;
                facts.TypeName = design.TypeDescriptor?.Name;
                facts.Version = WriteService.ComputeVersionToken(design);
                facts.DesignLastUpdate = ReadLastUpdate(design);
                if (separate)
                {
                    var copy = FindByGuid(targetModel, guid);
                    facts.EnvironmentCopyExists = copy != null;
                    facts.EnvironmentLastUpdate = copy == null ? (DateTime?)null : ReadLastUpdate(copy);
                }
                return facts;
            }

            public void CopyToEnvironment(Guid guid)
            {
                object designModel = DesignModel;
                object targetModel = TargetModel;
                var design = FindByGuid(designModel, guid);
                if (design == null || targetModel == null)
                    throw new InvalidOperationException("The object or the environment model is not available.");
                MethodInfo copy = FindCopyMethod()
                    ?? throw new MissingMethodException("KnowledgeBase.CopyEntity is not available in this SDK.");
                try
                {
                    copy.Invoke(_kb, new object[] { designModel, targetModel, design.Key });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    LogExceptionChain("SpecifyOnly-CopyEntity", ex);
                    throw ex.InnerException;
                }
            }

            public SpecifyOnlyRunResult Specify(Guid guid)
            {
                var result = new SpecifyOnlyRunResult();
                DateTime startedUtc = DateTime.UtcNow;
                object model = null;
                try
                {
                    object designModel = DesignModel;
                    model = TargetModel ?? designModel;
                    var design = FindByGuid(designModel, guid);
                    object specifier = GetSpecifier();
                    MethodInfo specifyObjects = FindSpecifyObjects(specifier, model);
                    PropertyInfo concurrentGeneration = FindConcurrentGenerationProperty();
                    if (design == null) { result.Failure = "The object was not found in the design model."; return result; }
                    if (specifyObjects == null) { result.Failure = "Specifier.SpecifyObjects(model, keys, options) is not available in this SDK."; return result; }
                    if (concurrentGeneration == null) { result.Failure = "BuildDaemonClient.ConcurrentGeneration is not available in this SDK."; return result; }

                    Type entityKeyType = specifyObjects.GetParameters()[1].ParameterType.GetGenericArguments()[0];
                    var keys = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entityKeyType));
                    keys.Add(design.Key);
                    object options = SpecifyOptions(result);

                    // The specifier client starts the generator for every object it finishes while
                    // ConcurrentGeneration is on (and writes the resource files once). The switch is process-wide:
                    // it is off only for this call, the call holds the KB lock and went through the lifecycle FIFO,
                    // and the previous value is given back whatever happens.
                    string failure;
                    bool ran = SpecifyOnlyGenerationSwitch.RunWithSwitchOff(
                        () => (bool)concurrentGeneration.GetValue(null),
                        value => concurrentGeneration.SetValue(null, value),
                        () =>
                        {
                            result.GenerationSuppressed = true;
                            result.SpecifierInvoked = true;
                            result.SpecifierReturned = WithTaskOutput(_kb, _engine,
                                () => (bool)specifyObjects.Invoke(specifier, new[] { model, keys, options }));
                        },
                        out failure);
                    if (!ran) result.Failure = failure;
                }
                catch (Exception ex)
                {
                    LogExceptionChain("SpecifyOnly", ex);
                    Exception root = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    result.Failure = root.Message;
                }
                finally
                {
                    var engine = _engine as InProcessBuildEngine;
                    result.SectionsObserved = engine?.StartedSections.ToList() ?? new List<string>();
                    result.ObjectUnreachable = engine != null && engine.SawCode(UnreachableObjectCode);
                    result.SpecificationFiles = ListSpecificationFilesSince(model, startedUtc);
                }
                return result;
            }

            // BuildOptions.Rebuild is what SpecifyOneOnly.ForceRebuild sets. Without it the specifier may skip an object
            // it considers up to date and return success having analyzed nothing; this call names its object, so the
            // object is always (re)specified. It does not widen to callees. An SDK without the member gets the plain
            // options, and the receipt says so (evidence.rebuildApplied).
            private static object SpecifyOptions(SpecifyOnlyRunResult result)
            {
                long bits = 0;
                try
                {
                    bits = Convert.ToInt64(Enum.Parse(_typeBuildOptions, "Rebuild"));
                    result.RebuildApplied = true;
                }
                catch (Exception ex)
                {
                    Logger.Warn("[BUILD-INPROCESS] SpecifyOnly: BuildOptions.Rebuild is not available (" + ex.Message + "); using the plain options.");
                }
                return Enum.ToObject(_typeBuildOptions, bits);
            }

            // The specifier keeps its per-object results in <KB>\GXSPC<model id>; name what this call wrote there.
            private List<string> ListSpecificationFilesSince(object model, DateTime startedUtc)
            {
                try
                {
                    string kbDirectory = _kb.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_kb) as string;
                    object id = model?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(model);
                    if (string.IsNullOrWhiteSpace(kbDirectory) || id == null) return new List<string>();
                    string folder = Path.Combine(kbDirectory, "GXSPC" + Convert.ToInt32(id).ToString("D3"));
                    return SpecifyOnlyGeneratorProbe.ListFilesSince(folder, startedUtc, 20);
                }
                catch (Exception ex)
                {
                    Logger.Warn("[BUILD-INPROCESS] SpecifyOnly could not list the specification output: " + ex.Message);
                    return new List<string>();
                }
            }

            public SpecifyOnlyProbe ProbeGeneratorOutput(DateTime startedUtc)
            {
                try { return SpecifyOnlyGeneratorProbe.Probe(ResolveOutputRoot(), startedUtc); }
                catch { return new SpecifyOnlyProbe(); }
            }

            // The environment's output folder. A never-built KB has none yet, so the folder the environment
            // declares (TargetPath) is used even when it does not exist, and the Worker's own web-folder
            // resolution (which needs the folder to exist) is the fallback.
            private string ResolveOutputRoot()
            {
                try
                {
                    string kbDirectory = _kb.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_kb) as string;
                    object environment = DesignModel?.GetType().GetProperty("Environment", BindingFlags.Public | BindingFlags.Instance)?.GetValue(DesignModel);
                    foreach (object candidate in new[] { environment, TargetModel })
                    {
                        string declared = candidate?.GetType().GetProperty("TargetPath", BindingFlags.Public | BindingFlags.Instance)?.GetValue(candidate) as string;
                        if (string.IsNullOrWhiteSpace(declared)) continue;
                        declared = declared.Trim();
                        if (Path.IsPathRooted(declared)) return declared;
                        if (!string.IsNullOrWhiteSpace(kbDirectory)) return Path.Combine(kbDirectory, declared);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("[BUILD-INPROCESS] SpecifyOnly output-folder probe could not read TargetPath: " + ex.Message);
                }

                string web = null;
                try { web = _generatorOutputPath?.Invoke(); } catch { }
                return string.IsNullOrWhiteSpace(web) ? null : Path.GetDirectoryName(web.TrimEnd('\\', '/'));
            }
        }
    }
}
