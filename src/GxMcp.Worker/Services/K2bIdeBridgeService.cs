using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>Relays Designer operations to the GeneXus IDE that owns the open document.</summary>
    public sealed class K2bIdeBridgeService
    {
        private readonly KbService _kb;

        public K2bIdeBridgeService(KbService kb) { _kb = kb; }

        public string Run(string target, JObject args)
        {
            args = args ?? new JObject();
            string kbPath = _kb.GetKbPath();
            if (string.IsNullOrWhiteSpace(kbPath))
                return McpResponse.Err("KbNotOpen", "Open the target KB in this MCP session first.", target: target);
            if (string.IsNullOrWhiteSpace(target))
                return McpResponse.Err("K2bTargetRequired", "A WebPanel name is required.");

            string pipeName = Environment.GetEnvironmentVariable("GXMCP_K2B_IDE_PIPE");
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 100
                || !System.Text.RegularExpressions.Regex.IsMatch(pipeName, "^[A-Za-z0-9_-]+$"))
                return McpResponse.Err("K2bIdeUnavailable", "Configure GXMCP_K2B_IDE_PIPE in the MCP and GeneXus IDE processes.", target: target);

            try
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                {
                    var waiting = pipe.BeginWaitForConnection(null, null);
                    if (!waiting.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                        return McpResponse.Err("K2bIdeUnavailable", "No GeneXus IDE bridge connected within 10 seconds. Open the same KB and Designer in an IDE started with the bridge enabled.", target: target);
                    pipe.EndWaitForConnection(waiting);
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                    {
                        var request = (JObject)args.DeepClone();
                        request["action"] = (string)args["action"] ?? "inspect";
                        request["name"] = target;
                        request["kb"] = kbPath;
                        string serialized = request.ToString(Formatting.None);
                        if (serialized.Length > 65536)
                            return McpResponse.Err("K2bRequestTooLarge", "The Designer request exceeds 64 KiB.", target: target);
                        writer.WriteLine(serialized);
                        var read = reader.ReadLineAsync();
                        if (!read.Wait(TimeSpan.FromSeconds(65)))
                            return McpResponse.Err("K2bIdeTimeout", "The IDE did not respond within 65 seconds.",
                                target: target, reconciliationRequired: true);
                        string line = read.Result;
                        if (line == null || line.Length > 1024 * 1024)
                            return McpResponse.Err("K2bIdeInvalidResponse", "The IDE bridge returned an empty or oversized response.", target: target);
                        var response = JObject.Parse(line);
                        if ((string)response["status"] == "ok")
                            return McpResponse.Ok(target: target, code: (string)response["code"], result: response);
                        return McpResponse.Err((string)response["code"] ?? "K2bIdeError",
                            (string)response["message"] ?? "The IDE rejected the Designer operation.", target: target,
                            reconciliationRequired: response["reconciliationRequired"]?.Value<bool?>() == true,
                            errorExtra: new JObject { ["path"] = args["path"], ["property"] = args["property"] });
                    }
                }
            }
            catch (Exception error)
            {
                return McpResponse.Err("K2bIdeBridgeError", error.GetBaseException().Message,
                    target: target, reconciliationRequired: true);
            }
        }
    }
}
