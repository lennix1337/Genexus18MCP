using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Worker.Helpers;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Services;
using Artech.Architecture.Common.Services;
using Artech.Udm.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security;

namespace GxMcp.Worker.Services
{
    public class BuildService
    {
        private string _msbuildPath;
        private string _gxDir;
        private KbService _kbService;
        private static readonly ConcurrentDictionary<string, BuildTaskStatus> _tasks = new ConcurrentDictionary<string, BuildTaskStatus>();

        public class BuildTaskStatus
        {
            public string TaskId { get; set; }
            public string Status { get; set; } // "Running", "Completed", "Error"
            public string Action { get; set; }
            public string Target { get; set; }
            public string Output { get; set; }
            public string StartTime { get; set; }
            public string EndTime { get; set; }
            public int ExitCode { get; set; }
            public string Error { get; set; }
        }

        public BuildService()
        {
            _gxDir = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
            
            string[] searchPaths = new[] {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
                Path.Combine(_gxDir, "MSBuild.exe"),
                @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
            };

            foreach (var p in searchPaths) { if (File.Exists(p)) { _msbuildPath = p; break; } }
        }

        public void SetKbService(KbService kbService) { _kbService = kbService; }
        public KbService KbService => _kbService;

        public string Build(string action, string target)
        {
            string taskId = Guid.NewGuid().ToString().Substring(0, 8);
            var status = new BuildTaskStatus {
                TaskId = taskId,
                Action = action,
                Target = target,
                Status = "Running",
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            _tasks[taskId] = status;

            // Run in background
            Task.Run(() => {
                try {
                    string result = BuildWithMSBuild(action, target);
                    var json = JObject.Parse(result);
                    status.Status = json["status"]?.ToString() ?? "Unknown";
                    status.Output = json["output"]?.ToString();
                    status.Error = json["error"]?.ToString();
                    status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Logger.Info($"Background Build {taskId} finished with status: {status.Status}");
                }
                catch (Exception ex) {
                    status.Status = "Error";
                    status.Error = ex.Message;
                    status.EndTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Logger.Error($"Background Build {taskId} FAILED: {ex.Message}");
                }
            });

            return JsonConvert.SerializeObject(new { 
                status = "Accepted", 
                message = "Build task started in background", 
                taskId = taskId 
            });
        }

        public string GetStatus(string taskId)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                // Return all recent tasks
                return JsonConvert.SerializeObject(new { tasks = _tasks.Values.OrderByDescending(t => t.StartTime).Take(10) });
            }

            if (_tasks.TryGetValue(taskId, out var status))
            {
                return JsonConvert.SerializeObject(status);
            }
            return "{\"error\": \"Task ID not found\"}";
        }

        private string BuildWithMSBuild(string action, string target)
        {
            try
            {
                if (_kbService != null)
                {
                    int waitAttempts = 0;
                    while (_kbService.IsInitializing && waitAttempts < 15)
                    {
                        System.Threading.Thread.Sleep(1000);
                        waitAttempts++;
                    }
                }

                string kbPath = GetKBPath();
                if (string.IsNullOrEmpty(kbPath)) return "{\"error\": \"KB Path not found in Environment (GX_KB_PATH)\"}";

                string tempFile = Path.Combine(Path.GetTempPath(), "GxBuild_" + Guid.NewGuid().ToString().Substring(0,8) + ".msbuild");
                string importPath = SecurityElement.Escape(Path.Combine(_gxDir, "Genexus.Tasks.targets"));
                string kbPathEsc = SecurityElement.Escape(kbPath);
                string targetEsc = SecurityElement.Escape(target ?? string.Empty);
                var sb = new StringBuilder();
                sb.AppendLine("<Project DefaultTargets=\"Execute\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
                sb.AppendLine("  <Import Project=\"" + importPath + "\" />");
                sb.AppendLine("  <Target Name=\"Execute\">");
                sb.AppendLine("    <OpenKnowledgeBase Directory=\"" + kbPathEsc + "\" />");

                if (action.Equals("Build", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(target))
                    sb.AppendLine("    <BuildOne BuildCalled=\"false\" ObjectName=\"" + targetEsc + "\" ForceRebuild=\"false\" />");
                else if (action.Equals("RebuildAll", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <RebuildAll />");
                else if (action.Equals("Reorg", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <CheckAndInstallDatabase />");
                else if (action.Equals("Validate", StringComparison.OrdinalIgnoreCase) || action.Equals("Check", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <CheckKnowledgeBase />");
                else if (action.Equals("Sync", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("    <BuildAll />");
                else sb.AppendLine("    <BuildAll />");

                sb.AppendLine("    <CloseKnowledgeBase />");
                sb.AppendLine("  </Target></Project>");
                
                File.WriteAllText(tempFile, sb.ToString());

                var startInfo = new ProcessStartInfo {
                    FileName = _msbuildPath,
                    Arguments = "/nologo /m /v:q /nodeReuse:false /target:Execute \"" + tempFile + "\"", 
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, WorkingDirectory = _gxDir
                };

                using (var process = Process.Start(startInfo)) {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    try { File.Delete(tempFile); } catch { }
                    
                    if (!string.IsNullOrEmpty(error)) output += "\nERROR:\n" + error;

                    return JsonConvert.SerializeObject(new { 
                        status = process.ExitCode == 0 ? "Success" : "Error", 
                        output = output,
                        exitCode = process.ExitCode
                    });
                }
            }
            catch (Exception ex) { return "{\"status\": \"Error\", \"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        public string GetKBPath()
        {
            return Environment.GetEnvironmentVariable("GX_KB_PATH") ?? "";
        }
    }
}
