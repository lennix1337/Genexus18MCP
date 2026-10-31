using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>How long genexus_doctor waits for a running Worker to report its WorkWithPlus templates.</summary>
        internal static readonly TimeSpan DoctorWwpTemplatesBudget = TimeSpan.FromSeconds(4);

        /// <summary>
        /// The Worker is asked to give up on a command it cannot start within this slack below the budget, so the
        /// Gateway's own timeout is not the first to answer.
        /// </summary>
        private const int DoctorWwpTemplatesBusySlackMs = 500;

        /// <summary>
        /// The WorkWithPlus template block for genexus_doctor. It asks only a Worker that is already running and serves
        /// the session's KB: it never starts or wakes a Worker and registers no operation or recovery fence. Every
        /// failure is an unavailable block that names its reason.
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
        /// Registers the probe as a pending Worker request and sends it inside the budget, as the engine read does, then
        /// waits for the reply with what is left of the budget. The command carries a busy wait below the budget, so a
        /// command still queued behind a busy SDK thread after that wait is refused with WorkerBusy instead of running
        /// late. Whatever the outcome, the pending entry is removed, so a late reply is dropped.
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
            using var deadline = new CancellationTokenSource(budget);
            Task? sendTask = null;
            try
            {
                var command = new JObject
                {
                    ["module"] = "Doctor",
                    ["action"] = "WwpTemplates",
                    ["target"] = "_self",
                    ["busyWaitMs"] = Math.Max(0, (int)budget.TotalMilliseconds - DoctorWwpTemplatesBusySlackMs)
                };
                sendTask = send(BuildWorkerRpcRequest(command, requestId, null, sessionId));
                await sendTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                string reply = await pending.CompletionSource.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                return ParseWwpTemplatesAnswer(reply);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                if (sendTask != null) ObserveAbandonedSend(sendTask, alias ?? string.Empty, Log);
                var timedOut = UnavailableWwpTemplatesBlock("Timeout");
                timedOut["timeoutMs"] = (long)budget.TotalMilliseconds;
                return timedOut;
            }
            catch (WorkerCommandNotDeliveredException)
            {
                return UnavailableWwpTemplatesBlock("WorkerNotRunning");
            }
            catch (WorkerCommandQueueFullException)
            {
                return UnavailableWwpTemplatesBlock("WorkerBusy");
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
        /// The block from the Worker's reply. The reply is the JSON-RPC frame the reply router delivers, whose `result`
        /// is the command envelope, whose `result` is the block. A JSON-RPC error, a command error, and a result that is
        /// not the block (an older Worker that ignores the action answers the whole doctor envelope) are unavailable
        /// blocks that name why. A refusal that ran nothing answers WorkerBusy, whether it came from the SDK thread or
        /// from the admission budget of the Worker's queue.
        /// </summary>
        internal static JObject ParseWwpTemplatesAnswer(string replyJson)
        {
            JObject frame;
            try
            {
                frame = JObject.Parse(replyJson);
            }
            catch (Exception)
            {
                return UnavailableWwpTemplatesBlock("WorkerError", "UnexpectedReply");
            }

            if (frame["error"] is JObject rpcError)
                return UnavailableWwpTemplatesBlock("WorkerError", rpcError["code"]?.ToString());

            JObject response = frame["result"] as JObject ?? frame;
            if (!string.Equals(response["status"]?.ToString(), "ok", StringComparison.Ordinal))
            {
                string? code = response["error"]?["code"]?.ToString();
                return IsRefusedBeforeRunning(code)
                    ? UnavailableWwpTemplatesBlock("WorkerBusy")
                    : UnavailableWwpTemplatesBlock("WorkerError", code);
            }

            if (response["result"] is JObject block && block["available"]?.Type == JTokenType.Boolean)
                return (JObject)block.DeepClone();
            return UnavailableWwpTemplatesBlock("WorkerError", "UnexpectedReply");
        }

        /// <summary>
        /// The Worker refuses a command without running it when its SDK thread stays busy past the busy wait
        /// (WorkerBusy), and when the admission budget of its queue is full (WorkerQueueSaturated).
        /// </summary>
        private static bool IsRefusedBeforeRunning(string? code) =>
            string.Equals(code, "WorkerBusy", StringComparison.Ordinal)
            || string.Equals(code, "WorkerQueueSaturated", StringComparison.Ordinal);

        internal static JObject UnavailableWwpTemplatesBlock(string reason, string? errorCode = null)
        {
            var block = new JObject { ["available"] = false, ["reason"] = reason };
            if (!string.IsNullOrWhiteSpace(errorCode)) block["errorCode"] = errorCode;
            return block;
        }

        /// <summary>
        /// Test seam: delivers a reply frame to the Worker reply router, the path every line from a Worker takes, so a
        /// test completes a template request with the frame the Worker writes and not with a copy of it.
        /// </summary>
        internal static void RouteWorkerReplyForTest(string replyJson)
        {
            HandleWorkerResponse(replyJson, JObject.Parse(replyJson));
        }
    }
}
