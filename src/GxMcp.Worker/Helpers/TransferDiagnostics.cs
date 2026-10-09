using System;
using Artech.Architecture.Common.Services;
using Artech.Common.Diagnostics;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    internal sealed class TransferDiagnostics : IOutputTarget, IDisposable
    {
        private readonly IOutputService service;
        internal JArray Messages { get; } = new JArray();
        internal bool Available => service != null;
        internal bool Truncated { get; private set; }

        internal static TransferDiagnostics ForSdk()
        {
            var availability = typeof(CommonServices).GetProperty("IsOutputAvailable");
            bool unavailable = availability?.GetValue(null) is bool value && !value;
            return new TransferDiagnostics(unavailable ? null : CommonServices.Output);
        }

        internal TransferDiagnostics(IOutputService service)
        {
            this.service = service;
            service?.AddListener((IOutputTarget)this);
        }
        internal void Record(string severity, string text, string code = null)
        {
            if (Messages.Count >= 200) { Truncated = true; return; }
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 2000) { text = text.Substring(0, 2000); Truncated = true; }
            Messages.Add(new JObject { ["severity"] = severity, ["code"] = code, ["message"] = text });
        }
        public void Add(OutputError message) => Record(message.Level.ToString(), message.Text, message.ErrorCode);
        public void AddText(string text) => Record("Information", text);
        public void AddLine(string text) => Record("Information", text);
        public void AddWarningLine(string text) => Record("Warning", text);
        public void AddErrorLine(string text) => Record("Error", text);
        public void AddAll(IOutputMessages messages, bool show)
        {
            foreach (var message in messages)
                if (message is OutputError error) Add(error);
                else Record("Information", message.ToString());
        }
        public void StartSection(string name) { }
        public void StartSection(string name, string description) { }
        public void StartSection(string name, bool show) { }
        public void StartSection(string name, string description, bool show) { }
        public void EndSection(string name, bool success) { if (!success) Record("Error", "SDK section failed: " + name); }
        public void EndSection(string name, string description, bool success) => EndSection(name, success);
        public void Clear() { } // keep diagnostics even when SDK clears the output pane
        public void Show() { }
        public void Dispose() => service?.RemoveListener((IOutputTarget)this);
    }
}
