using System;
using System.IO;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class TestService
    {
        private readonly KbService _kbService;
        private readonly BuildService _buildService;

        public TestService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _buildService = buildService;
        }

        // Security: `target` is LLM-controlled and flows into an MSBuild XML
        // document. Unescaped, an attacker could inject arbitrary MSBuild
        // tasks (e.g. <Exec Command="calc"/>) and achieve RCE under the
        // worker's privileges. Validate first, then XML-escape on the way in.
        // The same goes for the KB path — it's derived from the SDK but we
        // still escape to be defensive in depth.
        internal static bool IsSafeTestTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            if (target.Length > 256) return false;
            // GeneXus object names can include letters, digits, underscore, dot,
            // and we allow comma/semicolon for "Build With These Only" style
            // lists. Anything else (especially `<`, `>`, `"`, `'`, `&`) is
            // disallowed at the entrypoint so it can't reach the XML emitter.
            foreach (var c in target)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '.'
                       || c == ',' || c == ';' || c == ' ' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        public string RunTest(string target)
        {
            try
            {
                if (!IsSafeTestTarget(target))
                {
                    return "{\"status\":\"Error\",\"code\":\"InvalidTarget\"," +
                        "\"error\":\"target contains characters outside the allowed set " +
                        "(letters, digits, '_', '.', ',', ';', ' ', '-').\"}";
                }

                var kb = _kbService.GetKB();
                Logger.Info(string.Format("Preparing to run test: {0}", target));

                string kbPath = _buildService.GetKBPath();
                string gxPath = GeneXusInstallPath.Resolve();
                string gxTestProject = ResolveGxTestProject(gxPath);
                if (gxTestProject == null)
                {
                    return Models.McpResponse.Err(
                        code: "GXtestNotInstalled",
                        message: "GXtest is not installed in " + (gxPath ?? "the GeneXus installation") + " (no GXtest.msbuild), so there is nothing to run the tests with.",
                        hint: "Install GXtest for this GeneXus version. GeneXus X Evolution 3 does not ship it.",
                        target: target);
                }
                string msbuildPath = _buildService.MsBuildPath;
                if (string.IsNullOrEmpty(msbuildPath) || !File.Exists(msbuildPath))
                {
                    return Models.McpResponse.Err(
                        code: "MsBuildNotFound",
                        message: "No MSBuild.exe was found to run GXtest.",
                        hint: "Install the .NET Framework 4.x MSBuild or Visual Studio Build Tools.",
                        target: target);
                }

                // GXtest's own project opens the KB and runs RunTests with its defaults; the
                // target list is validated by IsSafeTestTarget, so it is safe as a property.
                Logger.Info(string.Format("Running GXtest via MSBuild for {0}...", target));

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = msbuildPath,
                    Arguments = BuildGxTestArguments(gxTestProject, kbPath, target),
                    WorkingDirectory = gxPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(startInfo))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    bool success = stdout.Contains("Tests execution succeeded") || stdout.Contains("Passed: 1");
                    
                    return "{\"status\":\"" + (success ? "Success" : "Failed") + "\", \"output\":\"" + 
                        CommandDispatcher.EscapeJsonString(stdout) + "\", \"errors\":\"" + 
                        CommandDispatcher.EscapeJsonString(stderr) + "\"}";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Test execution error: {0}", ex.Message));
                return "{\"status\":\"Error\",\"message\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        /// <summary>The GXtest project a GeneXus install ships, or null when GXtest is not installed.</summary>
        internal static string ResolveGxTestProject(string gxPath)
        {
            if (string.IsNullOrWhiteSpace(gxPath)) return null;
            string project = Path.Combine(gxPath, "GXtest.msbuild");
            return File.Exists(project) ? project : null;
        }

        internal static string BuildGxTestArguments(string gxTestProject, string kbPath, string target)
        {
            return string.Format(
                "/nologo /verbosity:normal /t:RunTests \"{0}\" \"/p:KBPath={1}\" \"/p:TestObjects={2}\"",
                gxTestProject, kbPath, target.Replace(',', ';'));
        }
    }
}
