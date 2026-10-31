using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>How long genexus_doctor waits for a running Worker to report its WorkWithPlus templates.</summary>
        internal static readonly TimeSpan DoctorWwpTemplatesBudget = TimeSpan.FromSeconds(4);

        /// <summary>
        /// The WorkWithPlus template block for genexus_doctor. It asks only a Worker that is already running and serves
        /// the session's KB: it never starts or wakes a Worker, never waits for SDK readiness, and registers no operation
        /// or recovery fence. Every failure is an unavailable block that names its reason.
        /// </summary>
        internal static async Task<JObject> ProbeDoctorWwpTemplatesAsync(string? sessionId)
        {
            try
            {
                string? alias = _currentKb.Value?.NormalizedAlias;
                if (string.IsNullOrWhiteSpace(alias)) return UnavailableWwpTemplatesBlock("KbNotOpen");

                WorkerPool? pool = _workerPool;
                WorkerProcess? worker = pool?.TryGetWorker(alias);
                if (pool == null || worker == null) return UnavailableWwpTemplatesBlock("WorkerNotRunning");
                if (!worker.IsSdkReady) return UnavailableWwpTemplatesBlock("WorkerNotReady");

                // The reservation keeps capacity eviction away from the Worker while the probe waits.
                Action? release = pool.BeginCommand(alias, worker);
                if (release == null) return UnavailableWwpTemplatesBlock("WorkerNotRunning");
                try
                {
                    return await ExchangeDoctorWwpTemplatesAsync(
                        rpc => worker.SendCommandAsync(rpc),
                        DoctorWwpTemplatesBudget,
                        sessionId,
                        alias,
                        worker).ConfigureAwait(false);
                }
                finally
                {
                    release();
                }
            }
            catch (Exception ex)
            {
                Log("[Gateway] genexus_doctor WorkWithPlus template probe failed: " + ex.Message);
                return UnavailableWwpTemplatesBlock("WorkerError");
            }
        }

        /// <summary>
        /// Registers the probe as a pending Worker request, sends it through <paramref name="send"/>, and waits for the
        /// answer for at most <paramref name="budget"/>. Whatever the outcome, the pending entry is removed, so a late
        /// answer is dropped and nothing (operation, retry, fence) follows from it.
        /// </summary>
        internal static async Task<JObject> ExchangeDoctorWwpTemplatesAsync(
            Func<JObject, Task> send,
            TimeSpan budget,
            string? sessionId,
            string? alias = null,
            WorkerProcess? worker = null)
        {
            string requestId = Guid.NewGuid().ToString();
            var pending = new PendingWorkerRequest
            {
                CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                ToolName = "genexus_doctor",
                CorrelationId = requestId,
                CreatedAtUtc = DateTime.UtcNow,
                RequestStartedAtUtc = DateTime.UtcNow,
                WorkerAlias = alias,
                KbAlias = alias,
                Worker = worker,
                McpSessionId = sessionId ?? "stdio"
            };
            _pendingRequests[requestId] = pending;
            try
            {
                var command = new JObject
                {
                    ["module"] = "Doctor",
                    ["action"] = "WwpTemplates",
                    ["target"] = "_self"
                };
                await send(BuildWorkerRpcRequest(command, requestId, null, sessionId)).ConfigureAwait(false);

                Task<string> answer = pending.CompletionSource.Task;
                Task finished = await Task.WhenAny(answer, Task.Delay(budget)).ConfigureAwait(false);
                if (!ReferenceEquals(finished, answer))
                {
                    var timedOut = UnavailableWwpTemplatesBlock("Timeout");
                    timedOut["timeoutMs"] = (long)budget.TotalMilliseconds;
                    return timedOut;
                }
                return ParseWwpTemplatesAnswer(await answer.ConfigureAwait(false));
            }
            catch (WorkerCommandNotDeliveredException)
            {
                return UnavailableWwpTemplatesBlock("WorkerNotRunning");
            }
            catch (Exception ex)
            {
                Log("[Gateway] genexus_doctor WorkWithPlus template probe failed: " + ex.Message);
                return UnavailableWwpTemplatesBlock("WorkerError");
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        /// <summary>
        /// The block from the Worker's answer. An error envelope, or an answer that is not JSON, is an unavailable block
        /// that carries the Worker's error code when it has one.
        /// </summary>
        internal static JObject ParseWwpTemplatesAnswer(string answerJson)
        {
            JObject answer;
            try
            {
                answer = JObject.Parse(answerJson);
            }
            catch (Exception)
            {
                return UnavailableWwpTemplatesBlock("WorkerError");
            }

            if (string.Equals(answer["status"]?.ToString(), "ok", StringComparison.Ordinal)
                && answer["result"] is JObject block)
                return (JObject)block.DeepClone();

            var unavailable = UnavailableWwpTemplatesBlock("WorkerError");
            string? errorCode = answer["error"]?["code"]?.ToString();
            if (!string.IsNullOrWhiteSpace(errorCode)) unavailable["errorCode"] = errorCode;
            return unavailable;
        }

        internal static JObject UnavailableWwpTemplatesBlock(string reason)
            => new JObject { ["available"] = false, ["reason"] = reason };

        /// <summary>
        /// Test seam for the reply path: completes a pending template request the way the Worker's reply router does,
        /// and reports whether that request was still pending.
        /// </summary>
        internal static bool CompleteDoctorWwpTemplatesForTest(string requestId, string answerJson)
        {
            if (!_pendingRequests.TryRemove(requestId, out var pending)) return false;
            pending.CompletionSource.TrySetResult(answerJson);
            return true;
        }
    }
}
