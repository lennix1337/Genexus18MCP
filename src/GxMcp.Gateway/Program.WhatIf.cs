using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        internal static bool IsImpactWhatIf(string tName, JObject? tArgs) =>
            string.Equals(tName, "genexus_impact", StringComparison.OrdinalIgnoreCase)
            && tArgs?["whatIf"]?.Type == JTokenType.Boolean
            && tArgs["whatIf"]!.Value<bool>();

        /// <summary>
        /// genexus_impact whatIf=true: composes the caller graph, build history and schema diff in the
        /// Gateway (see <see cref="ImpactWhatIf"/>). Each leg is an ordinary read-only tool call through
        /// the normal dispatch, so index gating, the semantic cache and the Worker timeouts apply per leg.
        /// Returns null for any other call.
        /// </summary>
        private static async Task<JObject?> TryDispatchImpactWhatIfAsync(
            string tName,
            JObject? tArgs,
            JObject request,
            string sessionId,
            bool sessionContextEnabled,
            JToken? idToken,
            CancellationToken transportCancellation)
        {
            if (!IsImpactWhatIf(tName, tArgs)) return null;

            async Task<ImpactWhatIf.LegResult> RunLeg(string tool, JObject legArgs, CancellationToken token)
            {
                var legParams = new JObject { ["name"] = tool, ["arguments"] = legArgs };
                JObject result = await DispatchToolCallCoreAsync(
                    legParams, request, sessionId, sessionContextEnabled, idToken, token);
                return ParseWhatIfLeg(result);
            }

            var outcome = await ImpactWhatIf.ComposeAsync(
                tArgs!,
                RunLeg,
                () => JobRegistry.EstimateBuildSeconds(ImpactWhatIf.BuildHistoryKind),
                TimeSpan.FromMilliseconds(ImpactWhatIf.DefaultBudgetMs),
                transportCancellation);
            return BuildToolResultContent(outcome.Payload, outcome.IsError, tName, tArgs);
        }

        internal static ImpactWhatIf.LegResult ParseWhatIfLeg(JObject toolResult)
        {
            bool isError = toolResult["isError"]?.Type == JTokenType.Boolean && toolResult["isError"]!.Value<bool>();
            JObject? payload = null;
            string? text = (toolResult["content"] as JArray)?.First?["text"]?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { payload = JToken.Parse(text) as JObject; } catch (Newtonsoft.Json.JsonException) { }
            }
            var leg = new ImpactWhatIf.LegResult { IsError = isError, Payload = payload };
            if (isError)
            {
                var error = payload?["error"];
                leg.Code = (error as JObject)?["code"]?.ToString() ?? payload?["code"]?.ToString()
                    ?? toolResult["usageCode"]?.ToString();
                leg.Message = (error as JObject)?["message"]?.ToString()
                    ?? (error?.Type == JTokenType.String ? error.ToString() : null)
                    ?? payload?["message"]?.ToString() ?? toolResult["message"]?.ToString();
            }
            return leg;
        }
    }
}
