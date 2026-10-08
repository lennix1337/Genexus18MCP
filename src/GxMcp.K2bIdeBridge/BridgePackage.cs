using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Artech.Architecture.Common.Packages;
using Artech.Architecture.Common.Services;
using Artech.Architecture.UI.Framework.Packages;
using Artech.Architecture.UI.Framework.Services;
using Artech.K2B.ObjectDesigner;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: Package(typeof(GxMcp.K2bIdeBridge.BridgePackage), IsUIPackage = true)]
[assembly: PackageCompatibility(Version = 143920)]

namespace GxMcp.K2bIdeBridge
{
    [System.Runtime.InteropServices.Guid("db89c21d-573f-41ab-a0c9-acdc7f4606bb")]
    public sealed class BridgePackage : AbstractPackageUI
    {
        private readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();
        private System.Windows.Forms.Timer _timer;
        private Thread _listener;
        private string _expectedKb;
        private string _pipeName;
        private string _target;

        public override string Name => "GxMcp.K2bIdeBridge";

        public override void Initialize(IGxServiceProvider services)
        {
            base.Initialize(services);
            Log("Package initialized in process " + System.Diagnostics.Process.GetCurrentProcess().Id);
            _expectedKb = Environment.GetEnvironmentVariable("GXMCP_K2B_IDE_KB");
            _pipeName = Environment.GetEnvironmentVariable("GXMCP_K2B_IDE_PIPE");
            _target = Environment.GetEnvironmentVariable("GXMCP_K2B_IDE_TARGET");
            if (string.IsNullOrWhiteSpace(_expectedKb) || string.IsNullOrWhiteSpace(_pipeName))
            {
                Log("Bridge inactive: configuration is incomplete.");
                return;
            }
            if (_pipeName.Length > 100 || _pipeName.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
            {
                Log("Bridge inactive: pipe name is invalid.");
                return;
            }

            _timer = new System.Windows.Forms.Timer { Interval = 100 };
            _timer.Tick += (sender, args) => ProcessPending();
            _timer.Start();
            _listener = new Thread(Listen) { IsBackground = true, Name = "GxMcp K2B IDE bridge" };
            _listener.Start();
            Log("Bridge connector started.");
        }

        private void Listen()
        {
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                    {
                        pipe.Connect(1000);
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        {
                            string line = reader.ReadLine();
                            if (line == null || line.Length > 65536) continue;
                            var request = new PendingRequest(line);
                            _pending.Enqueue(request);
                            if (!request.Completed.Wait(TimeSpan.FromSeconds(60)))
                                writer.WriteLine(new JObject { ["status"] = "error", ["code"] = "IdeTimeout",
                                    ["message"] = "The IDE operation exceeded 60 seconds; reread before retrying.",
                                    ["reconciliationRequired"] = true }.ToString(Newtonsoft.Json.Formatting.None));
                            else
                                writer.WriteLine(request.Response);
                        }
                    }
                }
                catch (TimeoutException) { }
                catch (Exception error)
                {
                    Log("Listener error: " + error.GetBaseException().Message);
                    Thread.Sleep(1000);
                }
            }
        }

        private void ProcessPending()
        {
            for (int count = 0; count < 4 && _pending.TryDequeue(out var request); count++)
            {
                try { request.Response = Process(JObject.Parse(request.Json)).ToString(Newtonsoft.Json.Formatting.None); }
                catch (Exception error)
                {
                    Log("Request error: " + error.GetBaseException().Message);
                    request.Response = new JObject { ["status"] = "error", ["code"] = "IdeBridgeError",
                        ["message"] = error.GetBaseException().Message, ["reconciliationRequired"] = true }
                        .ToString(Newtonsoft.Json.Formatting.None);
                }
                finally { request.Completed.Set(); }
            }
        }

        private JObject Process(JObject request)
        {
            var kb = UIServices.KB?.CurrentKB;
            if (kb == null || !SamePath(kb.Location, _expectedKb))
                return Error("WrongKb", "The configured KB is not open in this IDE instance.");
            if (!SamePath(kb.Location, (string)request["kb"]))
                return Error("WrongKb", "The MCP Worker and IDE have different KBs open.");
            string target = (string)request["name"];
            if (string.IsNullOrWhiteSpace(target))
                return Error("TargetRequired", "A WebPanel name is required.");
            if (!string.IsNullOrWhiteSpace(_target)
                && !string.Equals(target, _target, StringComparison.OrdinalIgnoreCase))
                return Error("TargetNotAllowed", "This IDE launch restricts the bridge to another WebPanel.");

            string action = (string)request["action"];
            if (action == "ping")
                return new JObject { ["status"] = "ok", ["code"] = "IdeBridgeReady",
                    ["kb"] = kb.Location, ["target"] = _target };
            if (action != "inspect" && action != "tree" && action != "preview"
                && action != "set_property" && action != "add_node"
                && action != "move_node" && action != "remove_node")
                return Error("ActionUnsupported", "The Designer action is unsupported.");

            var manager = UIServices.DocumentManager;
            var obj = manager.OpenedDocuments().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.TypeDescriptor?.Name, "WebPanel", StringComparison.OrdinalIgnoreCase));
            if (obj == null || !manager.IsOpenDocument(obj, out var document))
                return Error("TargetNotOpen", "Open the configured WebPanel in this IDE instance first.");
            var documentPart = document.GetDocumentParts().FirstOrDefault(candidate =>
                candidate.Part is FormDesignerPart);
            if (documentPart?.Part is not FormDesignerPart part || part.RootElement == null)
                return Error("DesignerInactive", "The WebPanel has no active K2B Designer.");

            string version = Hash(part.Source ?? string.Empty);
            object generator = Read(part, "GeneratorManager");
            if (action == "inspect" || action == "tree")
            {
                string selectedPath = (string)request["path"] ?? "root";
                object selected = Find(part.RootElement, selectedPath);
                if (selected == null)
                    return Error("NodeNotFound", "The Designer node path was not found: " + selectedPath);
                return new JObject { ["status"] = "ok", ["code"] = "IdeDesignerRead",
                    ["version"] = version, ["tree"] = Describe(selected, generator, selectedPath, action == "tree", 0),
                    ["documentDirty"] = document.Dirty, ["partDirty"] = documentPart.Dirty };
            }

            bool preview = action == "preview";
            string operation = preview ? (string)request["operation"] : action;
            string path = (string)request["path"];
            if (document.Dirty || documentPart.Dirty)
                return Error("UnsavedDocument", "Save or discard the open document's current changes before an MCP edit.");
            if (!preview && string.IsNullOrWhiteSpace((string)request["expectedVersion"]))
                return Error("VersionRequired", "Pass expectedVersion returned by inspect or preview.");
            if (!preview && !string.Equals((string)request["expectedVersion"], version, StringComparison.Ordinal))
                return new JObject { ["status"] = "error", ["code"] = "VersionConflict",
                    ["message"] = "The Designer changed after inspection.", ["currentVersion"] = version };
            object node = Find(part.RootElement, path);
            if (node == null)
                return Error("NodeNotFound", "The Designer node path was not found: " + path);
            JObject change;
            try { change = ValidateChange(operation, node, part.RootElement, generator, request, preview); }
            catch (EditException error) { return Error(error.Code, error.Message, path, (string)request["property"]); }
            if (preview)
                return new JObject { ["status"] = "ok", ["code"] = "IdeDesignerPreview", ["version"] = version,
                    ["change"] = change, ["persisted"] = false };
            if (operation == "set_property"
                && string.Equals((string)change["before"], (string)change["after"], StringComparison.Ordinal))
                return new JObject { ["status"] = "ok", ["code"] = "NoChange",
                    ["version"] = version, ["change"] = change };

            string beforeSource = part.Source;
            bool priorDocumentDirty = document.Dirty;
            bool priorPartDirty = documentPart.Dirty;
            try
            {
                ApplyChange(operation, node, generator, request);
                if (generator != null && !(bool)Invoke(generator, "ValidatePart", part))
                    throw new EditException("DesignerInvalid", "K2B rejected the edited Designer tree.");
                part.UpdateObject();
                documentPart.Dirty = true;
                document.Dirty = true;
                if (!document.Save())
                    throw new InvalidOperationException("The IDE rejected the document save.");
                return new JObject { ["status"] = "ok", ["code"] = "IdeSaveReturned",
                    ["version"] = Hash(part.Source ?? string.Empty), ["change"] = change,
                    ["independentVerificationRequired"] = true };
            }
            catch (Exception error)
            {
                try
                {
                    part.Source = beforeSource;
                    documentPart.Dirty = priorPartDirty;
                    document.Dirty = priorDocumentDirty;
                    manager.ReloadOpenObject(obj);
                }
                catch { }
                return new JObject { ["status"] = "error", ["code"] = error is EditException edit ? edit.Code : "IdeSaveFailed",
                    ["message"] = error.GetBaseException().Message, ["path"] = path,
                    ["property"] = (string)request["property"], ["reconciliationRequired"] = true };
            }
        }

        private static JObject ValidateChange(string operation, object node, object root, object generator,
            JObject request, bool preview)
        {
            string path = (string)request["path"];
            if (operation == "set_property")
            {
                string property = (string)request["property"];
                string value = (string)request["value"];
                object attributes = Read(node, "Attributes");
                if (string.IsNullOrWhiteSpace(property) || value == null)
                    throw new EditException("PropertyRequired", "property and value are required for node '" + path + "'.");
                if (!(bool)Invoke(attributes, "ContainsPropertyDefinition", property))
                    throw new EditException("PropertyUnknown", "Property '" + property + "' is not defined on node '" + path + "'.");
                if (Read(attributes, "IsReadOnly") is bool readOnly && readOnly)
                    throw new EditException("PropertyReadOnly", "Node '" + path + "' is read-only.");
                object typed;
                try { typed = Invoke(attributes, "GetPropertyValueFromString", property, value); }
                catch (Exception error)
                {
                    throw new EditException("PropertyInvalid", "Property '" + property + "' rejected value: " + error.GetBaseException().Message);
                }
                return new JObject { ["operation"] = operation, ["path"] = path,
                    ["property"] = property, ["before"] = (string)Invoke(attributes, "GetPropertyValueString", property),
                    ["after"] = value, ["typedValue"] = typed?.ToString() };
            }
            if (operation == "remove_node")
            {
                if (ReferenceEquals(node, root) || !(bool)Invoke(node, "CanBeDeleted"))
                    throw new EditException("NodeProtected", "Node '" + path + "' cannot be removed.");
                if (!preview && request["confirm"]?.Value<bool?>() != true)
                    throw new EditException("ConfirmRequired", "remove_node requires confirm=true.");
                return new JObject { ["operation"] = operation, ["path"] = path,
                    ["name"] = Read(node, "InternalName")?.ToString() };
            }
            if (operation == "add_node")
            {
                string nodeType = (string)request["nodeType"];
                Type type = AvailableTypes(generator).FirstOrDefault(candidate =>
                    candidate.Name == nodeType || candidate.FullName == nodeType);
                if (type == null)
                    throw new EditException("NodeTypeUnknown", "Node type '" + nodeType + "' is unavailable.");
                if (!(bool)Invoke(node, "SupportChildType", type))
                    throw new EditException("ChildUnsupported", "Node '" + path + "' cannot contain '" + nodeType + "'.");
                return new JObject { ["operation"] = operation, ["path"] = path, ["nodeType"] = type.FullName };
            }
            if (operation == "move_node")
            {
                string destination = (string)request["destination"];
                object parent = Find(root, destination);
                if (ReferenceEquals(node, root) || parent == null
                    || !(bool)Invoke(parent, "SupportChildType", node.GetType()))
                    throw new EditException("MoveUnsupported", "Node '" + path + "' cannot move to '" + destination + "'.");
                for (object cursor = parent; cursor != null; cursor = Read(cursor, "Parent"))
                    if (ReferenceEquals(cursor, node))
                        throw new EditException("MoveCycle", "A node cannot be moved below itself.");
                int? index = request["index"]?.Value<int?>();
                if (index < 0 || index > Children(parent).Count())
                    throw new EditException("IndexInvalid", "The destination index is outside the child range.");
                return new JObject { ["operation"] = operation, ["path"] = path,
                    ["destination"] = destination, ["index"] = index };
            }
            throw new EditException("OperationUnsupported", "Unsupported Designer edit operation: " + operation);
        }

        private static void ApplyChange(string operation, object node, object generator, JObject request)
        {
            if (operation == "set_property")
            {
                object attributes = Read(node, "Attributes");
                string property = (string)request["property"];
                object typed = Invoke(attributes, "GetPropertyValueFromString", property, (string)request["value"]);
                Invoke(attributes, "SetPropertyValue", property, typed);
            }
            else if (operation == "remove_node")
                Invoke(Read(node, "Parent"), "RemoveChild", node);
            else if (operation == "move_node")
            {
                object root = node;
                while (Read(root, "Parent") != null) root = Read(root, "Parent");
                Invoke(node, "MoveToParent", Find(root, (string)request["destination"]));
                if (request["index"] != null)
                    Invoke(node, "MoveToIndex", request["index"].Value<int>());
            }
            else if (operation == "add_node")
            {
                string nodeType = (string)request["nodeType"];
                Type type = AvailableTypes(generator).First(candidate =>
                    candidate.Name == nodeType || candidate.FullName == nodeType);
                object child = Invoke(generator, "CreateOrphanNode", type, node);
                Invoke(node, "AddNewChild", child);
            }
        }

        private static object Find(object root, string path)
        {
            if (path == "root") return root;
            if (string.IsNullOrWhiteSpace(path)) return null;
            object current = root;
            foreach (string segment in path.Split('/'))
            {
                if (!int.TryParse(segment, out int index) || index < 0) return null;
                object[] children = Children(current).ToArray();
                if (index >= children.Length) return null;
                current = children[index];
            }
            return current;
        }

        private static JObject Describe(object node, object generator, string path, bool recursive, int depth)
        {
            object attributes = Read(node, "Attributes");
            var properties = new JObject();
            foreach (object property in (IEnumerable)Read(attributes, "Properties"))
            {
                string name = Read(property, "Name")?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    properties[name] = (string)Invoke(attributes, "GetPropertyValueString", name);
            }
            var result = new JObject
            {
                ["path"] = path,
                ["type"] = node.GetType().FullName,
                ["name"] = Read(node, "InternalName")?.ToString(),
                ["properties"] = properties,
                ["childCount"] = Children(node).Count()
            };
            if (depth == 0)
                result["availableChildTypes"] = new JArray(AvailableTypes(generator)
                    .Where(type => (bool)Invoke(node, "SupportChildType", type)).Select(type => type.FullName));
            if (recursive && depth < 16)
                result["children"] = new JArray(Children(node).Select((child, index) =>
                    Describe(child, generator, path == "root" ? index.ToString() : path + "/" + index,
                        true, depth + 1)));
            return result;
        }

        private static IEnumerable<Type> AvailableTypes(object generator) =>
            ((IEnumerable)Invoke(generator, "GetNodesTypes")).Cast<Type>();

        private static IEnumerable<object> Children(object node) =>
            ((IEnumerable)Read(node, "Children")).Cast<object>();

        private static object Read(object target, string name) =>
            target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target, null);

        private sealed class EditException : Exception
        {
            public string Code { get; }
            public EditException(string code, string message) : base(message) { Code = code; }
        }

        private static bool SamePath(string left, string right)
        {
            try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string Hash(string content)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "").ToLowerInvariant();
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length
                    && candidate.GetParameters().Select((parameter, index) =>
                        args[index] == null || parameter.ParameterType.IsInstanceOfType(args[index])).All(valid => valid));
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, args);
        }

        private static JObject Error(string code, string message, string path = null, string property = null) =>
            new JObject { ["status"] = "error", ["code"] = code, ["message"] = message,
                ["path"] = path, ["property"] = property };

        private static void Log(string message)
        {
            string path = Environment.GetEnvironmentVariable("GXMCP_K2B_IDE_LOG");
            if (string.IsNullOrWhiteSpace(path)) return;
            try { File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); }
            catch { }
        }

        private sealed class PendingRequest
        {
            public PendingRequest(string json) { Json = json; }
            public string Json { get; }
            public string Response { get; set; }
            public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim();
        }
    }
}
