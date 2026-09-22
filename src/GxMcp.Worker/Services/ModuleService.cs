using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using Module = Artech.Architecture.Common.Objects.Module;
using System.Xml;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_module over the official IModuleManagerService. The service exposes
    /// installed modules, package/publish/restore, and configured module servers while
    /// keeping the SDK as the only KB/configuration authority.
    /// </summary>
    public class ModuleService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;
        private readonly IndexCacheService _index;

        public ModuleService(KbService kb, ObjectService objects, IndexCacheService index = null)
        {
            _kb = kb;
            _objects = objects;
            _index = index;
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "list";
            action = action.Trim().ToLowerInvariant();

            var validActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "install", "install_builtin", "update", "list", "package", "publish",
                "restore", "list_modules_servers", "add_modules_server", "search_modules_in_servers"
            };
            if (!validActions.Contains(action))
            {
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'.",
                    hint: "Use install, install_builtin, update, list, package, publish, restore, add_modules_server or search_modules_in_servers.");
            }

            if (action == "install" || action == "install_builtin") return InstallVerified(args);
            if (args?["dryRun"]?.Value<bool>() == true && action != "list" && action != "list_modules_servers" && action != "search_modules_in_servers")
                return McpResponse.Err("ModuleDryRunUnsupported", "This action has no safe preview; nothing was dispatched.");

            KnowledgeBase kb;
            try { kb = _kb?.GetKB() as KnowledgeBase; }
            catch { kb = null; }
            if (kb == null)
                return McpResponse.Err(code: "NoKbOpen", message: "No KB is open in this worker session.", hint: "Open a KB first (genexus_kb action=open).");

            if (action == "list") return ListModules(kb);

            IModuleManagerService svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<IModuleManagerService>();
            if (svc == null)
            {
                return McpResponse.Err(
                    code: "ModuleManagerServiceUnavailable",
                    message: "The GeneXus SDK's IModuleManagerService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
            }

            try
            {
                switch (action)
                {
                    case "update": return Update(svc, kb.DesignModel, args);
                    case "package": return Package(svc, kb.DesignModel, args);
                    case "publish": return Publish(svc, kb.DesignModel, args);
                    case "restore": return Restore(svc, kb.DesignModel, args);
                    case "list_modules_servers": return ListServers(svc);
                    case "add_modules_server": return AddServer(svc, args);
                    case "search_modules_in_servers": return SearchServers(svc, args);
                    default: return McpResponse.Err(code: "BadAction", message: "Unsupported Module Manager action.");
                }
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleOperationFailed", message: ex.Message, hint: "Check the worker log for the SDK exception.");
            }
        }

        private string InstallVerified(JObject args)
        {
            bool dispatched = false;
            if (_kb == null) return McpResponse.Err("NoKbOpen", "No KB is open in this worker session.");
            if (!Monitor.TryEnter(_kb.KbLock)) return McpResponse.Err("ModuleOperationBusy", "An incompatible KB operation is in progress.", retryable: false);
            try
            {
                var kb = _kb.GetKB() as KnowledgeBase;
                if (kb?.DesignModel == null) return McpResponse.Err("NoKbOpen", "No design model is open in this worker session.");
                var guard = WriteDestinationGuard.CheckCommand(_kb, "Module", "Run", args);
                if (guard != null) return guard;
                bool dryRun = args?["dryRun"]?.Value<bool>() == true;
                bool builtin = string.Equals(args?["action"]?.ToString(), "install_builtin", StringComparison.OrdinalIgnoreCase);
                var manager = dryRun && !builtin ? null : Helpers.SdkServiceResolver.Resolve<IModuleManagerService>();
                if ((!dryRun || builtin) && manager == null) return McpResponse.Err("ModuleManagerServiceUnavailable", "The native module manager service is unavailable.");
                string requestedVersion = args?["version"]?.ToString();
                if (builtin)
                {
                    string name = args?["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(args?["opcFile"]?.ToString()))
                        throw new ModuleInstallPlanException("BadArgs", "install_builtin requires a built-in name and does not accept opcFile.");
                    if (!manager.IsBuiltInModule(name)) throw new ModuleInstallPlanException("ModuleBuiltinNotRegistered", "The selected SDK does not register this built-in module.");
                    string builtinVersion = manager.GetBuiltinModuleVersion(name);
                    if (string.IsNullOrWhiteSpace(builtinVersion)) throw new ModuleInstallPlanException("ModuleBuiltinVersionUnavailable", "The SDK did not report the built-in version.");
                    if (!string.IsNullOrWhiteSpace(requestedVersion) && requestedVersion != builtinVersion)
                        throw new ModuleInstallPlanException("ModuleBuiltinVersionConflict", "The requested version differs from the SDK registered built-in version.");
                    requestedVersion = builtinVersion;
                }
                // Cache staging is shared by SDK sessions. Never wait behind another
                // installation or silently restart one after a timeout.
                using (var gate = new Mutex(false, @"Local\GxMcp.ModuleInstall"))
                {
                    bool entered;
                    try { entered = gate.WaitOne(0); }
                    catch (AbandonedMutexException) { gate.ReleaseMutex(); return McpResponse.Err("ModuleReconciliationRequired", "An earlier installation ended without a verified receipt. Inspect the module inventory before recovery.", retryable: false, reconciliationRequired: true); }
                    if (!entered) return McpResponse.Err("ModuleOperationBusy", "Another module installation is in progress.", retryable: false);
                    try
                    {
                        var sdkPath = Path.GetDirectoryName(typeof(KnowledgeBase).Assembly.Location);
                        var packages = ModuleInstallPackage.Plan(args?["opcFile"]?.ToString(), args?["name"]?.ToString(), requestedVersion, sdkPath);
                        var backend = new ModuleInstallSdkBackend(_kb, kb, manager, _index, packages);
                        dispatched = !dryRun;
                        var receipt = ModuleInstallFlow.Run(packages, dryRun, backend);
                        receipt["sdkCache"] = backend.CacheDiagnostics;
                        string code = receipt["code"]?.ToString();
                        bool success = receipt["status"]?.ToString() == "ok";
                        receipt.Remove("status");
                        receipt.Remove("code");
                        receipt["verificationScope"] = "module identity, version, dependency metadata and object inventory";
                        return success ? McpResponse.Ok(code: code, result: receipt)
                            : McpResponse.Err(code, receipt["diagnostic"]?["message"]?.ToString() ?? "The installation was not verified; inspect the reported inventory and diagnostic.",
                                extra: receipt, retryable: false,
                                reconciliationRequired: receipt["noMutation"]?.Value<bool>() != true);
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (ModuleInstallPlanException ex)
            {
                return McpResponse.Err(ex.Code, ex.Message, extra: new JObject { ["persisted"] = false, ["implicitLifecycleOperations"] = new JArray() });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("ModulePreflightFailed", "The module installation preflight failed.",
                    errorExtra: new JObject { ["exceptionType"] = ex.GetType().Name, ["parameter"] = (ex as ArgumentException)?.ParamName },
                    extra: new JObject { ["persisted"] = dispatched ? JValue.CreateNull() : new JValue(false),
                        ["persistedStateKnown"] = !dispatched, ["implicitLifecycleOperations"] = new JArray() }, retryable: false,
                    reconciliationRequired: dispatched);
            }
            finally { Monitor.Exit(_kb.KbLock); }
        }

        private string Update(IModuleManagerService svc, KBModel model, JObject args)
        {
            string name = args?["name"]?.ToString();
            string version = args?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "BadArgs", message: "action=update requires name.", hint: "Pass name=<installed module> and version=<target version>.");
            Module module = ResolveModule(name);
            if (module == null)
                return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.", hint: "Run action=list to inspect installed modules.");
            if (IsDryRun(args))
            {
                return McpResponse.Ok(code: "ModuleUpdatePreview", result: new JObject
                {
                    ["action"] = "update",
                    ["name"] = name,
                    ["version"] = version,
                    ["modulePresent"] = true,
                    ["persisted"] = false,
                    ["note"] = "Read-only preview: the installed module was resolved but no SDK update/save was called."
                });
            }
            bool ok;
            try
            {
                ok = svc.Update(model, module, version);
            }
            catch (Exception ex)
            {
                return InstallFailure("Update", name, null, version, ex, null);
            }
            bool presentAfter = ResolveModule(name) != null;
            var details = new JObject
            {
                ["name"] = name,
                ["version"] = version,
                ["modulePresent"] = presentAfter,
                ["verified"] = presentAfter,
                ["rereadConfirmed"] = presentAfter
            };
            if (!ok)
                return OperationResultWithDetails("ModuleUpdateDeclined", false, details,
                    "The SDK declined the update without throwing; inspect modulePresent and retry only after inspecting the worker log.");
            return OperationResult("ModuleUpdated", true, details);
        }

        private static bool IsDryRun(JObject args)
        {
            return args?["dryRun"]?.ToObject<bool?>() == true;
        }

        internal sealed class OpcPackageDependency
        {
            public string Name;
            public string Version;
            public string MinimumVersion;
            public string MaximumVersion;
            public string Guid;
        }

        internal sealed class OpcPackageIdentity
        {
            public string Name;
            public string Version;
            public string Id;
            public string Description;
            public string FileName;
            public long FileBytes;
            public List<OpcPackageDependency> Dependencies = new List<OpcPackageDependency>();
        }

        internal static OpcPackageIdentity ReadOpcPackage(string opcFile)
        {
            if (string.IsNullOrWhiteSpace(opcFile))
                throw new InvalidDataException("Module package path is empty.");
            FileInfo info;
            try
            {
                info = new FileInfo(opcFile);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Module package path is not usable: " + ex.GetType().Name + ".");
            }
            if (!info.Exists)
                throw new FileNotFoundException("Module package file was not found.");
            if (!string.Equals(info.Extension, ".opc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Module package '" + info.Name + "' does not have the .opc extension.");
            try
            {
                ZipArchive archive;
                try
                {
                    archive = ZipFile.OpenRead(info.FullName);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Module package '" + info.Name + "' could not be read: " + ex.GetType().Name + ".");
                }
                using (archive)
                {
                    ZipArchiveEntry manifest = null;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.Equals(entry.FullName, "ModuleManifest.mf", StringComparison.OrdinalIgnoreCase))
                        {
                            manifest = entry;
                            break;
                        }
                    }
                    if (manifest == null)
                        throw new InvalidDataException("Module package '" + info.Name + "' does not contain ModuleManifest.mf.");
                    if (manifest.Length > 16 * 1024 * 1024)
                        throw new InvalidDataException("Module package manifest in '" + info.Name + "' is unexpectedly large.");
                    string xml;
                    using (var stream = manifest.Open())
                    using (var reader = new StreamReader(stream))
                        xml = reader.ReadToEnd();
                    var document = new XmlDocument();
                    try
                    {
                        document.LoadXml(xml);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException("Module package manifest in '" + info.Name + "' is not valid XML: " + ex.GetType().Name + ".");
                    }
                    XmlElement root = document.DocumentElement;
                    if (root == null || !string.Equals(root.LocalName, "ModulePackage", StringComparison.Ordinal))
                        throw new InvalidDataException("Module package manifest in '" + info.Name + "' is not a ModulePackage manifest.");
                    string packageName = ReadManifestText(root, "Name");
                    if (string.IsNullOrWhiteSpace(packageName))
                        throw new InvalidDataException("Module package manifest in '" + info.Name + "' does not declare a module Name.");
                    var identity = new OpcPackageIdentity
                    {
                        Name = packageName.Trim(),
                        Version = ReadManifestText(root, "Version"),
                        Id = ReadManifestText(root, "ID"),
                        Description = ReadManifestText(root, "Description"),
                        FileName = info.Name,
                        FileBytes = info.Length
                    };
                    XmlNodeList dependencies = root.GetElementsByTagName("PackagedModuleDependency");
                    for (int i = 0; i < dependencies.Count && identity.Dependencies.Count < 1024; i++)
                    {
                        var element = dependencies[i] as XmlElement;
                        if (element == null) continue;
                        identity.Dependencies.Add(new OpcPackageDependency
                        {
                            Name = ReadManifestText(element, "Name"),
                            Version = ReadManifestText(element, "Version"),
                            MinimumVersion = ReadManifestText(element, "MinimumVersion"),
                            MaximumVersion = ReadManifestText(element, "MaximumVersion"),
                            Guid = ReadManifestText(element, "Guid")
                        });
                    }
                    return identity;
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                throw new FileNotFoundException("Module package file was not found.");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Module package '" + info.Name + "' could not be read: " + ex.GetType().Name + ".");
            }
        }

        private static string ReadManifestText(XmlElement parent, string tag)
        {
            try
            {
                XmlNodeList nodes = parent.GetElementsByTagName(tag);
                if (nodes == null || nodes.Count == 0) return null;
                foreach (XmlNode node in nodes)
                {
                    if (node != null && node.ParentNode == parent && node is XmlElement)
                        return string.IsNullOrWhiteSpace(node.InnerText) ? null : node.InnerText.Trim();
                }
                string text = nodes[0] != null ? nodes[0].InnerText : null;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string PackageValidationError(string code, string message, string opcFile, string name, string hint)
        {
            var details = new JObject
            {
                ["packageSource"] = opcFile != null ? "opcFile" : "name",
                ["persisted"] = false
            };
            if (opcFile != null) details["fileName"] = Path.GetFileName(opcFile);
            return McpResponse.Err(code, message, hint,
                target: name ?? (opcFile == null ? null : Path.GetFileName(opcFile)),
                errorExtra: details);
        }

        private string PreviewInstall(OpcPackageIdentity package, string requestedVersion)
        {
            var dependencies = new JArray();
            foreach (OpcPackageDependency dependency in package.Dependencies)
            {
                bool installed = false;
                try
                {
                    installed = !string.IsNullOrWhiteSpace(dependency.Name) && ResolveModule(dependency.Name) != null;
                }
                catch
                {
                }
                dependencies.Add(new JObject
                {
                    ["name"] = dependency.Name,
                    ["version"] = dependency.Version,
                    ["minimumVersion"] = dependency.MinimumVersion,
                    ["maximumVersion"] = dependency.MaximumVersion,
                    ["installedInKb"] = installed
                });
            }
            bool present = false;
            try
            {
                present = ResolveModule(package.Name) != null;
            }
            catch
            {
            }
            return McpResponse.Ok(code: "ModuleInstallPreview", result: new JObject
            {
                ["action"] = "install",
                ["packageSource"] = "opcFile",
                ["package"] = new JObject
                {
                    ["name"] = package.Name,
                    ["version"] = package.Version,
                    ["id"] = package.Id,
                    ["description"] = package.Description,
                    ["fileName"] = package.FileName,
                    ["fileBytes"] = package.FileBytes
                },
                ["requestedVersion"] = requestedVersion,
                ["dependencies"] = dependencies,
                ["dependencyCount"] = dependencies.Count,
                ["alreadyInstalled"] = present,
                ["modulePresent"] = present,
                ["persisted"] = false,
                ["note"] = "Read-only preview: the .opc manifest was read without the SDK and no install/save was called."
            });
        }

        private string PreviewInstallByName(string name, string version, bool builtIn = false)
        {
            bool present = false;
            try
            {
                present = ResolveModule(name) != null;
            }
            catch
            {
            }
            return McpResponse.Ok(code: builtIn ? "ModuleInstallBuiltInPreview" : "ModuleInstallPreview", result: new JObject
            {
                ["action"] = builtIn ? "install_builtin" : "install",
                ["packageSource"] = builtIn ? "builtin" : "name",
                ["name"] = name,
                ["version"] = version,
                ["alreadyInstalled"] = present,
                ["modulePresent"] = present,
                ["persisted"] = false,
                ["note"] = builtIn
                    ? "Read-only preview: the built-in module name was validated and the KB was inspected without calling the SDK install."
                    : "Read-only preview: the KB was inspected without contacting module servers and no install/save was called. Package identity and dependencies resolve through the configured module servers at execution time."
            });
        }

        private JObject PackageResultDetails(OpcPackageIdentity package, string requestedVersion, bool presentBefore, bool presentAfter, int countBefore, int countAfter)
        {
            var dependencies = new JArray();
            foreach (OpcPackageDependency dependency in package.Dependencies)
            {
                bool installed = false;
                try
                {
                    installed = !string.IsNullOrWhiteSpace(dependency.Name) && ResolveModule(dependency.Name) != null;
                }
                catch
                {
                }
                dependencies.Add(new JObject
                {
                    ["name"] = dependency.Name,
                    ["version"] = dependency.Version,
                    ["minimumVersion"] = dependency.MinimumVersion,
                    ["maximumVersion"] = dependency.MaximumVersion,
                    ["installedInKb"] = installed
                });
            }
            return new JObject
            {
                ["packageSource"] = "opcFile",
                ["package"] = new JObject
                {
                    ["name"] = package.Name,
                    ["version"] = package.Version,
                    ["id"] = package.Id,
                    ["fileName"] = package.FileName,
                    ["fileBytes"] = package.FileBytes
                },
                ["requestedVersion"] = requestedVersion,
                ["dependencies"] = dependencies,
                ["dependencyCount"] = dependencies.Count,
                ["alreadyInstalled"] = presentBefore,
                ["modulePresent"] = presentAfter,
                ["verified"] = presentAfter,
                ["rereadConfirmed"] = presentAfter,
                ["moduleCountBefore"] = countBefore,
                ["moduleCountAfter"] = countAfter,
                ["note"] = "The SDK reported success and the module was verified by an independent KB readback. A repeated install of the same verified package is a safe no-op at the KB level."
            };
        }

        private string InstallFailure(string stage, string moduleName, string opcFile, string version, Exception ex, OpcPackageIdentity package)
        {
            bool? observed = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(moduleName)) observed = ResolveModule(moduleName) != null;
            }
            catch
            {
            }
            int countAfter = CountModules();
            var details = new JObject
            {
                ["stage"] = "sdk:IModuleManagerService." + stage,
                ["module"] = moduleName,
                ["exception"] = DescribeExceptionChain(ex),
                ["modulePresent"] = observed.HasValue ? (JToken)observed.Value : JValue.CreateNull(),
                ["moduleCount"] = countAfter,
                ["rollbackClaimed"] = false
            };
            if (!string.IsNullOrWhiteSpace(version)) details["version"] = version;
            if (!string.IsNullOrWhiteSpace(opcFile)) details["fileName"] = Path.GetFileName(opcFile);
            if (package != null) details["packageVersion"] = package.Version;
            return McpResponse.Err(
                code: "ModuleOperationFailed",
                message: "Module install failed in " + stage + ": " + RootExceptionLabel(ex) + ".",
                hint: "Inspect modulePresent/moduleCount: when the module is now listed, verify its objects before retrying; a retry after a partial install is not automatically safe. No rollback was performed or claimed. Check the worker log for the full SDK stack.",
                target: moduleName,
                errorExtra: details);
        }

        internal static string DescribeExceptionChain(Exception ex)
        {
            var parts = new List<string>();
            for (Exception current = ex; current != null && parts.Count < 4; current = current.InnerException)
            {
                string message = (current.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                if (message.Length > 300) message = message.Substring(0, 300) + "…";
                parts.Add(current.GetType().Name + ": " + message);
            }
            string joined = string.Join(" <- ", parts);
            if (joined.Length > 1200) joined = joined.Substring(0, 1200) + "…";
            return joined;
        }

        private static string RootExceptionLabel(Exception ex)
        {
            try
            {
                if (ex == null) return "unknown SDK failure";
                return ex.GetType().Name;
            }
            catch
            {
                return "unknown SDK failure";
            }
        }

        private int CountModules()
        {
            try
            {
                KnowledgeBase kb = _kb != null ? _kb.GetKB() as KnowledgeBase : null;
                if (kb == null) return -1;
                int count = 0;
                foreach (KBObject obj in kb.DesignModel.Objects.GetAll())
                {
                    if (obj != null && string.Equals(obj.TypeDescriptor != null ? obj.TypeDescriptor.Name : null, "Module", StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                return count;
            }
            catch
            {
                return -1;
            }
        }

        private static string OperationResultWithDetails(string code, bool success, JObject details, string note)
        {
            details = details ?? new JObject();
            if (!string.IsNullOrWhiteSpace(note)) details["note"] = note;
            return OperationResult(code, success, details);
        }

        private string Package(IModuleManagerService svc, KBModel model, JObject args)
        {
            string name = args?["name"]?.ToString();
            string outputDirectory = args?["outputPath"]?.ToString() ?? args?["outputDirectory"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(outputDirectory))
                return McpResponse.Err(code: "BadArgs", message: "action=package requires name and outputPath.", hint: "outputPath is the destination directory for the generated .opc file.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=package writes a module package and requires confirm=true.", hint: "Review the destination and repeat with confirm=true.");

            Module module = ResolveModule(name);
            if (module == null)
                return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.");

            bool rebuild = args?["rebuild"]?.ToObject<bool?>() ?? false;
            string opcFile;
            bool packageOk;
            bool packageSupported = TryInvokePackage(svc, module, model, rebuild, outputDirectory, out packageOk, out opcFile);
            if (!packageSupported)
                return McpResponse.Err(code: "ModulePackageUnsupported", message: "The installed GeneXus SDK does not expose a supported Module.Package signature.");
            return OperationResult(packageOk ? "ModulePackaged" : "ModulePackageDeclined", packageOk, new JObject
            {
                ["name"] = name,
                ["outputDirectory"] = outputDirectory,
                ["opcFile"] = opcFile,
                ["rebuild"] = rebuild
            });
        }

        private static bool TryInvokePackage(
            IModuleManagerService svc,
            Module module,
            KBModel model,
            bool rebuild,
            string outputDirectory,
            out bool packageOk,
            out string opcFile)
        {
            packageOk = false;
            opcFile = null;
            if (svc == null || module == null) return false;
            var candidates = svc.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, "Package", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.GetParameters().Length)
                .ToList();
            foreach (var method in candidates)
            {
                var parameters = method.GetParameters();
                var values = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p.ParameterType == typeof(Module) || p.ParameterType.IsInstanceOfType(module))
                        values[i] = module;
                    else if (p.ParameterType == typeof(bool))
                        values[i] = rebuild;
                    else if (p.ParameterType == typeof(string))
                    {
                        if (p.IsOut || p.ParameterType.IsByRef) values[i] = null;
                        else if (p.Name.IndexOf("out", StringComparison.OrdinalIgnoreCase) >= 0
                            || p.Name.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
                            values[i] = outputDirectory;
                        else values[i] = outputDirectory;
                    }
                    else if (p.ParameterType.IsAssignableFrom(typeof(List<KBModel>)))
                        values[i] = new List<KBModel> { model };
                    else if (p.ParameterType.IsAssignableFrom(typeof(KBModel)))
                        values[i] = model;
                    else if (p.ParameterType.IsByRef) values[i] = null;
                    else return false; // unsupported shape; try another overload
                }
                try
                {
                    object result = method.Invoke(svc, values);
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (parameters[i].ParameterType.IsByRef)
                        {
                            if (parameters[i].ParameterType.GetElementType() == typeof(string))
                                opcFile = values[i] as string;
                        }
                    }
                    packageOk = result is bool boolResult ? boolResult : true;
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    GxMcp.Worker.Helpers.Logger.Warn("Module.Package invocation failed: " + (ex.InnerException ?? ex).Message);
                    return true;
                }
                catch { /* try another SDK overload */ }
            }
            return false;
        }

        private static bool TryInvokeSdk(
            object target,
            string methodName,
            object[] suppliedArguments,
            out object result,
            out Exception error)
        {
            result = null;
            error = null;
            if (target == null || string.IsNullOrWhiteSpace(methodName)) return false;
            Exception lastError = null;
            foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                if (!TryMapSdkArguments(method.GetParameters(), suppliedArguments, out object[] values)) continue;
                try
                {
                    result = method.Invoke(target, values);
                    error = null;
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    lastError = ex.InnerException ?? ex;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            error = lastError;
            return false;
        }

        private static bool TryMapSdkArguments(ParameterInfo[] parameters, object[] supplied, out object[] values)
        {
            values = new object[parameters?.Length ?? 0];
            var assigned = new bool[values.Length];
            supplied = supplied ?? Array.Empty<object>();
            for (int argumentIndex = 0; argumentIndex < supplied.Length; argumentIndex++)
            {
                object argument = supplied[argumentIndex];
                int best = -1;
                for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (assigned[parameterIndex] || parameters[parameterIndex].ParameterType.IsByRef) continue;
                    Type parameterType = parameters[parameterIndex].ParameterType;
                    if (argument == null)
                    {
                        if (!parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null) { best = parameterIndex; break; }
                        continue;
                    }
                    if (parameterType == argument.GetType() || parameterType.IsInstanceOfType(argument))
                    {
                        best = parameterIndex;
                        break;
                    }
                    if (parameterType.IsAssignableFrom(argument.GetType()))
                    {
                        best = parameterIndex;
                        break;
                    }
                }
                if (best < 0) return false;
                values[best] = argument;
                assigned[best] = true;
            }
            for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                if (assigned[parameterIndex]) continue;
                ParameterInfo parameter = parameters[parameterIndex];
                if (parameter.ParameterType.IsByRef || parameter.IsOptional)
                    values[parameterIndex] = null;
                else if (parameter.HasDefaultValue)
                    values[parameterIndex] = parameter.DefaultValue;
                else
                    return false;
            }
            return true;
        }

        private static IEnumerable<object> EnumerateSdkObjects(object value)
        {
            if (!(value is IEnumerable enumerable)) yield break;
            foreach (object item in enumerable)
            {
                if (item != null) yield return item;
            }
        }

        private string Publish(IModuleManagerService svc, KBModel model, JObject args)
        {
            string serverId = args?["server"]?.ToString();
            string opcFile = args?["opcFile"]?.ToString();
            string name = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(serverId))
                return McpResponse.Err(code: "BadArgs", message: "action=publish requires server.", hint: "Pass the configured module server id.");
            if (string.IsNullOrWhiteSpace(opcFile) && string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "BadArgs", message: "action=publish requires opcFile or name.", hint: "Publish a generated .opc file or an installed Module by name.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=publish requires confirm=true.", hint: "Publishing uploads a module to an external server.");

            object publishResult;
            Exception publishError;
            bool invoked;
            if (!string.IsNullOrWhiteSpace(opcFile))
                invoked = TryInvokeSdk(svc, "Publish", new object[] { opcFile, serverId }, out publishResult, out publishError);
            else
            {
                Module module = ResolveModule(name);
                if (module == null) return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.");
                invoked = TryInvokeSdk(svc, "Publish", new object[] { module, serverId }, out publishResult, out publishError);
            }
            if (!invoked && publishError != null)
                return McpResponse.Err(code: "ModuleOperationFailed", message: "Module publish failed: " + RootExceptionLabel(publishError), hint: "Inspect the worker log for the SDK exception.");
            if (!invoked)
                return McpResponse.Err(code: "ModulePublishUnsupported", message: "The installed GeneXus SDK does not expose a supported Module Publish signature.");
            bool ok = publishResult is bool publishBool ? publishBool : true;
            return OperationResult(ok ? "ModulePublished" : "ModulePublishDeclined", ok, new JObject
            {
                ["name"] = name,
                ["opcFile"] = opcFile,
                ["server"] = serverId
            });
        }

        private string Restore(IModuleManagerService svc, KBModel model, JObject args)
        {
            string name = args?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err(code: "BadArgs", message: "action=restore requires name.", hint: "Pass the installed module name.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=restore requires confirm=true.", hint: "Restore writes module files into the KB.");

            Module module = ResolveModule(name);
            if (module == null)
                return McpResponse.Err(code: "ModuleNotFound", message: "Module '" + name + "' not found in this KB.");

            string serverId = args?["server"]?.ToString();
            IModuleManagerServer server = string.IsNullOrWhiteSpace(serverId) ? null : FindServer(svc, serverId);
            if (!string.IsNullOrWhiteSpace(serverId) && server == null)
                return McpResponse.Err(code: "ModuleServerNotFound", message: "Module server '" + serverId + "' is not configured.");
            object[] restoreArgs = server == null
                ? new object[] { model, module }
                : new object[] { model, server, module };
            object restoreResult;
            Exception restoreError;
            bool restoreInvoked = TryInvokeSdk(svc, "Restore", restoreArgs, out restoreResult, out restoreError);
            if (!restoreInvoked && restoreError != null)
                return McpResponse.Err(code: "ModuleOperationFailed", message: "Module restore failed: " + RootExceptionLabel(restoreError), hint: "Inspect the worker log for the SDK exception.");
            if (!restoreInvoked)
                return McpResponse.Err(code: "ModuleRestoreUnsupported", message: "The installed GeneXus SDK does not expose a supported Module Restore signature.");
            bool ok = restoreResult is bool restoreBool ? restoreBool : true;
            return OperationResult(ok ? "ModuleRestored" : "ModuleRestoreDeclined", ok, new JObject { ["name"] = name, ["server"] = serverId });
        }

        private static string AddServer(IModuleManagerService svc, JObject args)
        {
            string source = args?["source"]?.ToString();
            string serverId = args?["server"]?.ToString() ?? args?["name"]?.ToString();
            string typeValue = args?["serverType"]?.ToString() ?? "ModuleServer";
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(serverId))
                return McpResponse.Err(code: "BadArgs", message: "action=add_modules_server requires server/name and source.", hint: "Pass server=<id>, source=<URL or directory>, and serverType when needed.");
            if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(code: "ConfirmRequired", message: "action=add_modules_server requires confirm=true.", hint: "This changes the SDK Module Manager server configuration.");
            if (!Enum.TryParse(typeValue, true, out ServerType serverType))
                return McpResponse.Err(code: "BadArgs", message: "Unknown serverType '" + typeValue + "'.", hint: "Use Directory, Nexus, NexusNuGet or ModuleServer.");

            bool preserve = args?["preserveConfiguration"]?.ToObject<bool?>() ?? true;
            object addResult;
            Exception addError;
            bool addInvoked = TryInvokeSdk(svc, "AddServer", new object[] { serverType, serverId, source, preserve }, out addResult, out addError);
            if (!addInvoked && addError != null)
                return McpResponse.Err(code: "ModuleOperationFailed", message: "Module server add failed: " + RootExceptionLabel(addError), hint: "Inspect the worker log for the SDK exception.");
            if (!addInvoked)
                return McpResponse.Err(code: "ModuleServerAddUnsupported", message: "The installed GeneXus SDK does not expose a supported AddServer signature.");
            IModuleManagerServer server = addResult as IModuleManagerServer;
            if (server == null) return McpResponse.Err(code: "ModuleServerAddDeclined", message: "The SDK declined the module server.");
            return McpResponse.Ok(code: "ModuleServerAdded", result: ServerToJson(server));
        }

        private static string SearchServers(IModuleManagerService svc, JObject args)
        {
            string requested = args?["server"]?.ToString();
            string filter = args?["filter"]?.ToString();
            if (string.IsNullOrWhiteSpace(requested))
                return McpResponse.Err(code: "ModuleServerRequired", message: "action=search_modules_in_servers requires server.", hint: "Run action=list_modules_servers first, then pass one configured server name to bound the SDK network operation.");
            IEnumerable<IModuleManagerServer> servers = svc.ListServers() ?? Enumerable.Empty<IModuleManagerServer>();
            if (!string.IsNullOrWhiteSpace(requested))
                servers = servers.Where(server => string.Equals(server?.Name, requested, StringComparison.OrdinalIgnoreCase));

            var results = new JArray();
            foreach (IModuleManagerServer server in servers)
            {
                if (server == null) continue;
                object listResult;
                Exception listError;
                object[] listArguments = string.IsNullOrWhiteSpace(filter)
                    ? Array.Empty<object>()
                    : new object[] { filter };
                if (!TryInvokeSdk(server, "List", listArguments, out listResult, out listError))
                    return McpResponse.Err(code: "ModuleServerListUnsupported", message: "The configured server does not expose a supported List signature.", target: server.Name);
                if (listError != null)
                    return McpResponse.Err(code: "ModuleServerListFailed", message: listError.Message, target: server.Name);
                var modules = new JArray();
                foreach (object packageObject in EnumerateSdkObjects(listResult))
                {
                    var package = packageObject as ModulePackage;
                    if (package != null) modules.Add(PackageToJson(package));
                }
                results.Add(new JObject
                {
                    ["server"] = server.Name,
                    ["modules"] = modules,
                    ["count"] = modules.Count
                });
            }
            return McpResponse.Ok(code: "ModuleServerSearchCompleted", result: new JObject
            {
                ["filter"] = filter,
                ["servers"] = results,
                ["serverCount"] = results.Count
            });
        }

        private static string ListServers(IModuleManagerService svc)
        {
            try
            {
                IEnumerable<IModuleManagerServer> servers = svc.ListServers() ?? Enumerable.Empty<IModuleManagerServer>();
                var result = new JArray();
                foreach (IModuleManagerServer server in servers)
                    if (server != null) result.Add(ServerToJson(server));
                return McpResponse.Ok(code: "ModuleServerListRetrieved", result: new JObject
                {
                    ["servers"] = result,
                    ["count"] = result.Count
                });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleServerListFailed", message: ex.Message, hint: "Check the worker log for the SDK exception.");
            }
        }

        private Module ResolveModule(string name)
        {
            try { return _objects?.FindObject(name, "Module") as Module; }
            catch { return null; }
        }

        private static IModuleManagerServer FindServer(IModuleManagerService svc, string name)
        {
            try
            {
                return (svc.ListServers() ?? Enumerable.Empty<IModuleManagerServer>())
                    .FirstOrDefault(server => string.Equals(server?.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private static string ListModules(dynamic kb)
        {
            try
            {
                var modules = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (KBObject obj in kb.DesignModel.Objects.GetAll())
                {
                    if (obj == null || !string.Equals(obj.TypeDescriptor?.Name, "Module", StringComparison.OrdinalIgnoreCase)) continue;
                    string guid = SafeString(() => obj.Guid == Guid.Empty ? string.Empty : obj.Guid.ToString());
                    string entityKey = SafeString(() => obj.Key?.ToString());
                    string path = BuildModulePath(obj);
                    string parent = path;
                    int parentSeparator = path.LastIndexOf('/');
                    if (parentSeparator >= 0) parent = path.Substring(0, parentSeparator);
                    else parent = string.Empty;
                    string identity = BuildStableModuleKey(guid, entityKey, path, obj.Name);
                    if (modules.ContainsKey(identity)) continue;
                    modules[identity] = new JObject
                    {
                        ["name"] = obj.Name,
                        ["version"] = obj.GetPropertyValue<string>("ModuleVersion"),
                        ["description"] = SafeString(() => obj.Description),
                        ["guid"] = guid,
                        ["entityKey"] = entityKey,
                        ["parent"] = parent,
                        ["path"] = path,
                        ["qualifiedName"] = path
                    };
                }
                var ordered = new JArray();
                foreach (JObject module in modules.Values
                    .OrderBy(item => item["path"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item["name"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item["guid"]?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    ordered.Add(module);
                return McpResponse.Ok(code: "ModuleListRetrieved", result: new JObject
                {
                    ["count"] = ordered.Count,
                    ["modules"] = ordered,
                    ["source"] = "sdk:DesignModel.Objects"
                });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ModuleListFailed", message: ex.Message, hint: "Check the worker log for the SDK exception.");
            }
        }

        internal static string BuildStableModuleKey(string guid, string entityKey, string path, string name)
        {
            if (!string.IsNullOrWhiteSpace(guid)) return "guid:" + guid.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(entityKey)) return "entity:" + entityKey.Trim().ToLowerInvariant();
            return "path:" + (path ?? string.Empty).Trim().ToLowerInvariant() + ":" + (name ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string BuildModulePath(KBObject module)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            KBObject current = module;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                string name = SafeString(() => current.Name);
                if (string.IsNullOrWhiteSpace(name)) break;
                string key = SafeString(() => current.Guid == Guid.Empty ? name : current.Guid.ToString());
                if (!seen.Add(key)) break;
                names.Add(name);
                try { current = current.Parent; }
                catch { break; }
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string OperationResult(string code, bool success, JObject details)
        {
            details = details ?? new JObject();
            details["success"] = success;
            details["source"] = "sdk:IModuleManagerService";
            return McpResponse.Ok(code: code, result: details);
        }

        private static object ReadSdkProperty(object target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(target, null);
            }
            catch { return null; }
        }

        private static JObject ServerToJson(IModuleManagerServer server)
        {
            var result = new JObject
            {
                ["name"] = server?.Name,
                ["canDelete"] = server?.CanDelete ?? false,
                ["canUpdate"] = ReadSdkProperty(server, "CanUpdate") is bool canUpdate && canUpdate,
                ["needsProfile"] = server?.NeedsProfile ?? false
            };
            if (server is IModuleManagerConfigurableServer configurable)
            {
                result["serverType"] = configurable.ServerType.ToString();
                result["sourceConfigured"] = !string.IsNullOrWhiteSpace(configurable.Source);
                result["preserveConfiguration"] = ReadSdkProperty(configurable, "PreserveConfiguration") is bool preserve
                    ? (JToken)preserve
                    : JValue.CreateNull();
            }
            return result;
        }

        private static JToken ToJToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken token) return token;
            return JToken.FromObject(value);
        }

        private static JObject PackageToJson(ModulePackage package)
        {
            return new JObject
            {
                ["id"] = package?.ID,
                ["name"] = package?.Name,
                ["version"] = package?.Version,
                ["description"] = package?.Description,
                ["owner"] = package?.Owner,
                ["author"] = package?.Author,
                ["hasDatabase"] = package?.HasDatabase ?? false,
                ["serverUrl"] = ToJToken(ReadSdkProperty(package, "ServerUrl")),
                ["tags"] = ToJToken(ReadSdkProperty(package, "Tags"))
            };
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }
    }
}
