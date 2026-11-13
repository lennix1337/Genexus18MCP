using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>
        /// apply_pattern { validate: true } — post-apply build of the generated host so
        /// the LLM sees compile failures in a single tool call. No-op for any other
        /// result. Extracted from DispatchToolCallCoreAsync (phase: post-dispatch validation).
        /// </summary>
        private static async Task ValidateAppliedPatternAsync(JObject? innerResult, string tName, JObject? tArgs)
        {
            // apply_pattern { validate: true } — post-apply build of the
            // generated host so the LLM sees compile failures in a single
            // tool call. Without this the agent declared "Success" and only
            // discovered the broken binding by opening the IDE. Runs OUTSIDE
            // the SendWorkerCommandAsync onSuccess lambda (which is sync) so
            // we can await the validation build here.
            if (innerResult != null
                && !(innerResult["isError"]?.ToObject<bool?>() ?? false)
                && string.Equals(tName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase)
                && (tArgs?["validate"]?.ToObject<bool?>() ?? false))
            {
                string applyText = (innerResult["content"] as JArray)?[0]?["text"]?.ToString() ?? "";
                // Round-trip is structural: innerResult.content[0].text was serialized
                // by BuildToolResultContent inside SendWorkerCommandAsync's sync onSuccess
                // lambda (after truncation/normalization) and no parsed payload survives;
                // we must parse to read patternHost AND to fold the validation block back in.
                JObject? applyPayload = null;
                try { applyPayload = JObject.Parse(applyText); } catch { }
                string? hostName =applyPayload?["patternHost"]?.ToString();

                var vBlock = new JObject { ["target"] = hostName };
                if (string.IsNullOrWhiteSpace(hostName))
                {
                    vBlock["status"] = "skipped";
                    vBlock["reason"] = "No patternHost in apply response — nothing to validate.";
                }
                else
                {
                    // Worker's BuildService.Build is async internally — kicks off
                    // Task.Run and returns a Running envelope with a taskId in ms.
                    // We must (1) fire Build/Build, (2) poll Build/Status with that
                    // taskId until terminal, (3) fetch Build/Result for errors.
                    var vSw = System.Diagnostics.Stopwatch.StartNew();
                    JObject startCmd = new JObject
                    {
                        ["module"] = "Build",
                        ["action"] = "Build",
                        ["target"] = hostName,
                        ["includeCallees"] = "direct"
                    };
                    JObject? startEnv = await SendWorkerCommandAsync(
                        startCmd, 10000,
                        "validate-start timeout",
                        env => env,
                        (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                        toolName: "genexus_apply_pattern.validate.start",
                        toolArgs: null, trackOperation: false);

                    string? taskId = null;
                    JObject? startInner = startEnv?["result"] as JObject;
                    if (startInner == null && startEnv?["result"] is JValue sjv && sjv.Type == JTokenType.String)
                    {
                        try { startInner = JObject.Parse(sjv.ToString()); } catch { }
                    }
                    taskId = startInner?["TaskId"]?.ToString() ?? startInner?["taskId"]?.ToString();

                    JObject? terminal = null;
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        // Poll up to 180s. Adaptive interval: most validation
                        // builds of a single object finish in 1-3s, so poll fast
                        // (250ms) for the first 2s and fall back to 1s — cuts the
                        // common-case wait from ~1-4s to ~0.25-1s.
                        for (int i = 0; i < 180 && vSw.ElapsedMilliseconds < 180000; i++)
                        {
                            await Task.Delay(i < 8 ? 250 : 1000);
                            var statusCmd = new JObject
                            {
                                ["module"] = "Build",
                                ["action"] = "Status",
                                ["target"] = taskId
                            };
                            JObject? statusEnv = await SendWorkerCommandAsync(
                                statusCmd, 8000, "validate-poll timeout",
                                env => env,
                                (_, cid) => new JObject { ["__timeout"] = true, ["correlationId"] = cid },
                                toolName: "genexus_apply_pattern.validate.poll",
                                toolArgs: null, trackOperation: false);
                            JObject? sObj = statusEnv?["result"] as JObject;
                            if (sObj == null && statusEnv?["result"] is JValue pjv && pjv.Type == JTokenType.String)
                            {
                                try { sObj = JObject.Parse(pjv.ToString()); } catch { }
                            }
                            string? sStatus = sObj?["Status"]?.ToString() ?? sObj?["status"]?.ToString();
                            if (ApplyPatchValidation.IsEndState(sStatus))
                            {
                                terminal = sObj;
                                break;
                            }
                        }
                    }
                    vSw.Stop();
                    vBlock["durationMs"] = vSw.ElapsedMilliseconds;

                    if (string.IsNullOrEmpty(taskId))
                    {
                        vBlock["status"] = "error";
                        vBlock["error"] = "Worker did not return a taskId for the validation build.";
                        if (startInner != null) vBlock["startResponse"] = startInner;
                    }
                    else if (terminal == null)
                    {
                        vBlock["status"] = "timeout";
                        vBlock["error"] = "Validation build did not finish in 180s.";
                        vBlock["taskId"] = taskId;
                    }
                    else
                    {
                        ApplyPatchValidation.WriteBlock(vBlock, terminal, taskId);
                    }
                }

                // Fold the validation block back into the toolResult text
                // payload, and promote isError when validation didn't pass.
                if (applyPayload != null)
                {
                    applyPayload["validation"] = vBlock;
                    var newText = applyPayload.ToString(Formatting.None);
                    var contentArr = innerResult["content"] as JArray;
                    if (contentArr != null && contentArr.Count > 0 && contentArr[0] is JObject c0)
                    {
                        c0["text"] = newText;
                    }
                    if (ApplyPatchValidation.PromotesToError(vBlock))
                    {
                        innerResult["isError"] = true;
                    }
                }
            }
        }
    }
}
