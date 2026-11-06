using System;
using System.Collections;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GenexusServices = Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_deploy — deploy application over the SDK (P1 #3).
    ///
    /// Actions:
    ///   • list_targets — enumerate configured deployment target types
    ///     (<c>IDeploymentTargetService.GetTargetTypes</c>). Read-only, default.
    ///   • deploy       — <c>IDeploymentService.Deploy(model)</c>. DESTRUCTIVE (builds +
    ///     ships the app); requires confirm=true. Never a default.
    ///
    /// <c>IDeploymentService</c> implements <c>IGxService</c> → <see cref="SdkServiceResolver"/>.
    /// <c>IDeploymentTargetService</c> does not → <see cref="SdkServiceLocator"/> (GUID).
    /// </summary>
    public class DeployService
    {
        private readonly KbService _kb;

        public DeployService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            string action = (args?["action"]?.ToString() ?? "list_targets").Trim().ToLowerInvariant();

            if (action != "list_targets" && action != "deploy")
                return McpResponse.Err(
                    code: "BadAction",
                    message: "Unknown action '" + action + "'. Expected list_targets or deploy.",
                    hint: "genexus_deploy action=list_targets|deploy.");

            // Fail-fast: a static precondition must not depend on KB state.
            if (action == "deploy" && !(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err(
                    code: "ConfirmRequired",
                    message: "action=deploy builds and ships the application; pass confirm=true.",
                    hint: "Review the target with action=list_targets, then set confirm=true.");

            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            if (action == "list_targets")
            {
#if GXMCP_SDK_EV3
                // Evolution 3 deploys to one fixed target (an OPC package under the
                // environment's Deploy folder) and has no IDeploymentTargetService.
                return Evolution3Unsupported.Refuse("genexus_deploy", "list_targets", "the deployment-target catalog");
#else
                var tgtSvc = SdkServiceLocator.ConstructOrResolve<GenexusServices.IDeploymentTargetService>(
                    () => new Artech.Packages.Genexus.BL.Services.DeploymentTargetService());
                if (tgtSvc == null)
                    return McpResponse.Err(
                        code: "DeploymentTargetServiceUnavailable",
                        message: "The GeneXus SDK's IDeploymentTargetService is not registered in this worker session.",
                        hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
                try
                {
                    var targets = new JArray();
                    var list = tgtSvc.GetTargetTypes();
                    if (list is IEnumerable e)
                        foreach (var t in e)
                        {
                            if (t == null) continue;
                            try { targets.Add(JToken.FromObject(t)); }
                            catch { targets.Add(t.ToString()); }
                        }
                    return McpResponse.Ok(code: "DeploymentTargetsRetrieved", result: new JObject
                    {
                        ["count"] = targets.Count,
                        ["targets"] = targets,
                        ["source"] = "sdk:IDeploymentTargetService.GetTargetTypes"
                    });
                }
                catch (Exception ex)
                {
                    return McpResponse.Err("DeployListFailed", ex.Message, "Check the worker log for details.");
                }
#endif
            }

            if (action == "deploy")
            {
                if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                    return McpResponse.Err(
                        code: "ConfirmRequired",
                        message: "action=deploy builds and ships the application; pass confirm=true.",
                        hint: "Review the target with action=list_targets, then set confirm=true.");

                var svc = SdkServiceLocator.ConstructOrResolve<IDeploymentService>(
                    () => new Artech.Packages.Genexus.BL.Services.DeployService());
                if (svc == null)
                    return McpResponse.Err(
                        code: "DeploymentServiceUnavailable",
                        message: "The GeneXus SDK's IDeploymentService is not registered in this worker session.",
                        hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");
                try
                {
                    bool ok = svc.Deploy(model);
                    return McpResponse.Ok(code: ok ? "Deployed" : "DeployDeclined", result: new JObject
                    {
                        ["success"] = ok,
                        ["source"] = "sdk:IDeploymentService.Deploy"
                    });
                }
                catch (Exception ex)
                {
                    return McpResponse.Err("DeployFailed", ex.Message, "Check the worker log for the full stack trace.");
                }
            }

            return McpResponse.Err(
                code: "BadAction",
                message: "Unknown action '" + action + "'. Expected list_targets or deploy.",
                hint: "genexus_deploy action=list_targets|deploy.");
        }
    }
}
