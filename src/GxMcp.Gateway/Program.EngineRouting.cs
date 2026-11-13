using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static readonly EngineRoutedOperations EngineOperations = new EngineRoutedOperations(
            () => ActiveConfig,
            Log,
            (payload, isError, toolName, toolArgs) => BuildToolResultContent(payload, isError, toolName, toolArgs),
            ReadEngineActiveModelAsync);

        /// <summary>Budget for the Worker's active-model snapshot, which it answers without the SDK.</summary>
        private const int EngineActiveModelTimeoutMs = 2000;

        /// <summary>Test seam: runs between the candidate Worker check and its pool reservation.</summary>
        internal static Action? EngineActiveModelAfterCandidateCheckForTest;

        private static Task<EngineActiveModel?> ReadEngineActiveModelAsync(string kbPath, CancellationToken cancellationToken) =>
            ReadEngineActiveModelAsync(_workerPool, _currentKb.Value?.Alias, EngineActiveModelTimeoutMs, cancellationToken);

        /// <summary>
        /// The active KB version of the session KB's Worker. Only the existing, SDK-ready Worker found in
        /// the pool is asked, and only while the pool still owns it (<see cref="WorkerPool.BeginCommand(string?, WorkerProcess?)"/>):
        /// an engine call never acquires, starts, respawns or waits for a Worker, so it cannot exceed
        /// <paramref name="timeoutMs"/>. The Worker answers from a snapshot its SDK thread publishes, off
        /// that thread. Null when the answer is unknown (no usable Worker, failure or timeout); a
        /// cancellation of the caller propagates.
        /// </summary>
        internal static async Task<EngineActiveModel?> ReadEngineActiveModelAsync(
            WorkerPool? pool, string? alias, int timeoutMs, CancellationToken cancellationToken)
        {
            if (pool == null || string.IsNullOrWhiteSpace(alias)) return null;
            WorkerProcess? worker = pool.TryGet(alias!);
            if (worker == null || !worker.IsSdkReady) return null;
            EngineActiveModelAfterCandidateCheckForTest?.Invoke();
            Action? release = pool.BeginCommand(alias, worker);
            if (release == null) return null;

            string requestId = Guid.NewGuid().ToString();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(timeoutMs);
                string correlationId = Guid.NewGuid().ToString("N");
                var command = new JObject
                {
                    ["module"] = "KbVersion",
                    ["action"] = "ActiveModel",
                    ["_gxmcpOwnerId"] = "gateway:" + StateScope.ProcessScopeId.ToString(),
                    ["correlationId"] = correlationId,
                };
                var pending = new PendingWorkerRequest
                {
                    CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                    ToolName = "genexus_kb_version",
                    CorrelationId = correlationId,
                    WorkerAlias = worker.Kb?.NormalizedAlias,
                    KbAlias = worker.Kb?.NormalizedAlias,
                };
                _pendingRequests[requestId] = pending;
                Task sendTask = worker.SendCommandAsync(BuildWorkerRpcRequest(
                    command, requestId, null, _currentSessionContext.Value?.OwnerScopeId));
                try
                {
                    await sendTask.WaitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The enqueue is not cancellable: it may still complete, or fail, after the budget.
                    ObserveAbandonedSend(sendTask, alias!, Log);
                    throw;
                }
                string raw = await pending.CompletionSource.Task.WaitAsync(budget.Token).ConfigureAwait(false);
                return EngineActiveModel.FromWorkerReply(pending.ParsedResponse ?? JObject.Parse(raw));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"[Engine] active KB version for '{alias}' unknown: {ex.Message}");
                return null;
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
                release();
            }
        }

        /// <summary>
        /// Observes the failure of an enqueue the read stopped waiting for (for example a command queue
        /// that stays full), so it is logged instead of surfacing as an unobserved task exception.
        /// </summary>
        internal static void ObserveAbandonedSend(Task sendTask, string alias, Action<string> log) =>
            sendTask.ContinueWith(
                faulted => log($"[Engine] abandoned active KB version request for '{alias}' failed to enqueue: {faulted.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        /// <summary>
        /// Called from the tool dispatch pipeline before the SDK index gate;
        /// see <see cref="EngineRoutedOperations.TryDispatchAsync"/>.
        /// </summary>
        internal static Task<JObject?> TryDispatchEngineRoutedOperationAsync(
            string toolName,
            string? lifecycleAction,
            JObject? toolArgs,
            string? kbPath,
            string? kbAlias,
            CancellationToken cancellationToken) =>
            EngineOperations.TryDispatchAsync(toolName, lifecycleAction, toolArgs, kbPath, kbAlias, cancellationToken);

        /// <summary>
        /// genexus_edit validationMode=native: after the SDK save, attaches the engine's
        /// <c>nativeDiagnostics</c> block (see <see cref="EngineRoutedOperations.DiagnoseSavedObjectAsync"/>).
        /// A call that did not ask for it, or whose edit failed, is left untouched and costs one string compare.
        /// Never throws and never changes the edit's outcome.
        /// </summary>
        internal static async Task AttachNativeDiagnosticsAsync(
            JObject? innerResult, string toolName, JObject? toolArgs, CancellationToken cancellationToken)
        {
            if (!EngineRoutedOperations.WantsNativeDiagnostics(toolName, toolArgs)) return;
            if (innerResult == null || (innerResult["isError"]?.ToObject<bool?>() ?? false)) return;
            try
            {
                JObject? payload = TryReadResultPayload(innerResult, out JObject? parsed) ? parsed : null;
                JObject block = payload != null && IsSuccessfulMutationPayload(payload)
                    ? await EngineOperations.DiagnoseSavedObjectAsync(
                        toolArgs, _currentKb.Value?.Path, _currentKb.Value?.Alias,
                        EngineRoutedOperations.NativeDiagnosticsBudgetMs, cancellationToken).ConfigureAwait(false)
                    : new JObject { ["status"] = "unavailable", ["reason"] = "the edit result was not a plain success" };
                if (payload != null) FoldNativeDiagnostics(innerResult, payload, block);
            }
            catch (Exception ex)
            {
                Log($"[Engine] native diagnostics not attached: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryReadResultPayload(JObject innerResult, out JObject? payload)
        {
            payload = null;
            string? text = (innerResult["content"] as JArray)?.FirstOrDefault() is JObject first ? first["text"]?.ToString() : null;
            if (string.IsNullOrEmpty(text)) return false;
            try { payload = JObject.Parse(text); return true; }
            catch (Newtonsoft.Json.JsonException) { return false; }
        }

        internal static void FoldNativeDiagnostics(JObject innerResult, JObject payload, JObject block)
        {
            payload["nativeDiagnostics"] = block;
            if ((innerResult["content"] as JArray)?.FirstOrDefault() is JObject first)
                first["text"] = payload.ToString(Newtonsoft.Json.Formatting.None);
            if (innerResult["structuredContent"] is JObject structured)
                structured["nativeDiagnostics"] = block.DeepClone();
        }
    }
}
