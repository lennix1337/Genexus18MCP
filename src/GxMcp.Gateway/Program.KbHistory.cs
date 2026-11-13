using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        /// <summary>Null unless GXMCP_KB_HISTORY enables it; every call site checks for null first.</summary>
        internal static KbHistoryRecorder? _kbHistory;

        /// <summary>
        /// Request-path hook of the opt-in KB history: classify the Worker envelope and enqueue a
        /// job. Never waits for git, never throws, and does nothing when the feature is off.
        /// </summary>
        internal static void TryRecordKbHistory(KbHistoryRecorder? recorder, ToolCallResponseContext ctx, JToken? workerPayload)
        {
            if (recorder == null || !KbHistoryCapture.IsHistoryTool(ctx.ToolName)) return;
            try
            {
                KbHistoryJob? job = KbHistoryCapture.TryCreate(
                    ctx.ToolName, ctx.ToolArgs, workerPayload, ctx.KbPath, ctx.KbAlias, ctx.SessionId);
                if (job == null) recorder.NoteSkipped();
                else recorder.TryEnqueue(job);
            }
            catch (Exception ex)
            {
                recorder.NoteSkipped();
                Log($"[KbHistory] capture failed for {ctx.ToolName}: {ex.Message}");
            }
        }
    }
}
