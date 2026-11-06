using System;
using System.Collections;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GenexusServices = Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_layout action=list_controls (P2 #10). Read-only.
    ///
    /// Enumerates the KB's control definitions (user controls + built-ins) via
    /// <c>IUserControlsManagerService.GetControlDefinitionCollection</c> so a layout-authoring
    /// agent can see which control types / theme-class names are valid before writing a form.
    ///
    /// Not in the headless service registry → construct the public concrete
    /// <c>UserControlsManagerService</c> directly (ConstructOrResolve). Best-effort Initialize
    /// (the collection getter may need it), then reflect Name/Description off each definition.
    /// </summary>
    public class UserControlsListService
    {
        private readonly KbService _kb;

        public UserControlsListService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var svc = SdkServiceLocator.ConstructOrResolve<GenexusServices.IUserControlsManagerService>(
                () => new Artech.Packages.Genexus.BL.Services.UserControlsManagerService());
            if (svc == null)
                return McpResponse.Err("UserControlsManagerServiceUnavailable", "Could not construct the SDK's UserControlsManagerService.", "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            int limit = args?["limit"]?.ToObject<int?>() ?? 200;
            try
            {
                var controls = new JArray();
                if (limit > 0)
                {
                    try
                    {
                        foreach (var def in GxMcp.Worker.Compatibility.SdkCompat.UserControlDefinitions(svc, model))
                        {
                            if (def == null) continue;
                            controls.Add(new JObject
                            {
                                ["name"] = Reflect(def, "Name"),
                                ["description"] = Reflect(def, "Description")
                            });
                            if (controls.Count >= limit) break;
                        }
                    }
                    catch (Exception ex) { return McpResponse.Err("ListControlsFailed", ex.Message, "Check the worker log."); }
                }

                return McpResponse.Ok(
                    code: "ControlsRetrieved",
                    result: new JObject
                    {
                        ["count"] = controls.Count,
                        ["controls"] = controls,
                        ["source"] = "sdk:IUserControlsManagerService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("ListControlsFailed", ex.Message, "Check the worker log for the full stack trace.");
            }
        }

        private static JToken Reflect(object o, string prop)
        {
            try
            {
                var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                var v = p?.GetValue(o);
                return v?.ToString();
            }
            catch { return null; }
        }
    }
}
