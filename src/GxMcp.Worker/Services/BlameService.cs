using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 88 — code archeology. Shells out to <c>git blame</c> against a file
    /// inside the KB working tree and returns per-line attribution. The KB itself
    /// must be a git repo (or be a subdir of one); otherwise returns a
    /// structured <c>KbNotInGit</c> error.
    ///
    /// Because GeneXus persists most object parts inside proprietary stores (not
    /// loose XML files), the caller MAY pass an explicit <c>filePath</c> relative
    /// to the KB root pointing at the file they want blamed. When omitted, the
    /// service searches the KB for files matching <c>&lt;objectName&gt;*&lt;part&gt;*</c>
    /// and blames the first hit. If multiple files match, the candidate list is
    /// returned so the caller can disambiguate.
    /// </summary>
    public class BlameService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly Func<string, string> _findGitRoot;

        public BlameService(KbService kbService, ObjectService objectService)
            : this(kbService, objectService, FindGitRoot)
        {
        }

        internal BlameService(KbService kbService, ObjectService objectService, Func<string, string> findGitRoot)
        {
            _kbService = kbService;
            _objectService = objectService;
            _findGitRoot = findGitRoot ?? throw new ArgumentNullException(nameof(findGitRoot));
        }

        public sealed class BlameRequest
        {
            public string Name { get; set; }
            public string Part { get; set; }
            public string FilePath { get; set; } // KB-relative or absolute
            public int Line { get; set; }        // 1-based; 0 = whole file
            public int Context { get; set; } = 2; // snippet ±N lines around the blamed line
        }

        public string Blame(BlameRequest req)
        {
            if (req == null) return Error("Empty request.", "missingArgs");
            if (string.IsNullOrWhiteSpace(req.Name) && string.IsNullOrWhiteSpace(req.FilePath))
                return Error("Either name or filePath is required.", "missingArgs");

            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            string kbDir = ResolveKbDirectory(kbPath);
            if (string.IsNullOrEmpty(kbDir) || !Directory.Exists(kbDir))
                return Error("KB is not open or its directory could not be resolved.", "NoKb");

            string gitRoot = _findGitRoot(kbDir);
            if (string.IsNullOrEmpty(gitRoot))
                return Models.McpResponse.Err(
                    code: "KbNotInGit",
                    message: "The KB directory is not inside a git repository.",
                    hint: "Initialise git in the KB directory (git init) or supply filePath pointing at a tracked file.",
                    extra: new JObject { ["kbDir"] = kbDir });

            string targetFile = ResolveTargetFile(req, kbDir, gitRoot, out var candidates, out var resolveErr);
            if (!string.IsNullOrEmpty(resolveErr))
                return resolveErr;
            if (string.IsNullOrEmpty(targetFile))
            {
                return Models.McpResponse.Err(
                    code: "PartNotTracked",
                    message: "Could not locate a tracked file for the requested name/part.",
                    hint: "Pass filePath=<KB-relative file> explicitly. GeneXus persists most parts in proprietary stores; only files tracked by git can be blamed.",
                    extra: new JObject { ["candidates"] = new JArray(candidates ?? new List<string>()) });
            }

            string[] fileLines;
            try { fileLines = File.ReadAllLines(targetFile); }
            catch (Exception ex) { return Models.McpResponse.Err(code: "ReadFailed", message: "Failed to read target file: " + ex.Message); }

            int totalLines = fileLines.Length;
            int requested = req.Line;
            if (requested < 0) requested = 0;
            if (requested > totalLines)
                return Models.McpResponse.Err(code: "LineOutOfRange", message: $"Requested line {requested} exceeds file length {totalLines}.");

            int startLine = requested == 0 ? 1 : requested;
            int endLine = requested == 0 ? totalLines : requested;

            string blameRaw;
            string blameErr;
            int rc = RunGit(gitRoot, new[]
            {
                "blame", "--porcelain",
                "-L", startLine + "," + endLine,
                "--", MakeRelative(gitRoot, targetFile)
            }, out blameRaw, out blameErr);
            if (rc != 0)
            {
                return Models.McpResponse.Err(
                    code: "GitFailed",
                    message: "git blame failed: " + (string.IsNullOrEmpty(blameErr) ? blameRaw : blameErr),
                    target: targetFile);
            }

            var entries = ParsePorcelain(blameRaw);
            var entriesArr = new JArray();
            foreach (var e in entries)
            {
                int ctx = Math.Max(0, req.Context);
                int sliceStart = Math.Max(1, e.LineNumber - ctx);
                int sliceEnd = Math.Min(totalLines, e.LineNumber + ctx);
                var sb = new StringBuilder();
                for (int i = sliceStart; i <= sliceEnd; i++)
                {
                    sb.Append(i).Append(i == e.LineNumber ? "→ " : ": ").AppendLine(fileLines[i - 1]);
                }

                entriesArr.Add(new JObject
                {
                    ["line"] = e.LineNumber,
                    ["commitHash"] = e.CommitHash,
                    ["shortHash"] = e.CommitHash?.Length >= 7 ? e.CommitHash.Substring(0, 7) : e.CommitHash,
                    ["author"] = e.Author,
                    ["authorEmail"] = e.AuthorEmail,
                    ["date"] = e.AuthorDate,
                    ["summary"] = e.Summary,
                    ["content"] = e.LineContent,
                    ["snippetContext"] = sb.ToString().TrimEnd('\r', '\n')
                });
            }

            return new JObject
            {
                ["filePath"] = MakeRelative(gitRoot, targetFile),
                ["gitRoot"] = gitRoot,
                ["totalLines"] = totalLines,
                ["range"] = new JObject { ["start"] = startLine, ["end"] = endLine },
                ["entries"] = entriesArr
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string ResolveKbDirectory(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath)) return null;
            if (Directory.Exists(kbPath)) return kbPath;
            if (File.Exists(kbPath)) return Path.GetDirectoryName(kbPath);
            return null;
        }

        internal static string FindGitRoot(string startDir)
        {
            try
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                        File.Exists(Path.Combine(dir.FullName, ".git")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private string ResolveTargetFile(BlameRequest req, string kbDir, string gitRoot,
                                         out List<string> candidates, out string error)
        {
            candidates = new List<string>();
            error = null;

            if (!string.IsNullOrEmpty(req.FilePath))
            {
                string path = req.FilePath;
                if (!Path.IsPathRooted(path))
                {
                    // Try KB-relative first, then git-root-relative.
                    string p1 = Path.GetFullPath(Path.Combine(kbDir, path));
                    string p2 = Path.GetFullPath(Path.Combine(gitRoot, path));
                    path = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : p1);
                }
                else
                {
                    // Normalise so the StartsWith check below is reliable.
                    try { path = Path.GetFullPath(path); } catch { /* leave as-is */ }
                }
                // SECURITY: `filePath` is LLM-controlled. Without this check a
                // traversal like `filePath = "..\\..\\..\\Users\\me\\.ssh\\id_rsa"`
                // would let File.ReadAllLines surface arbitrary file contents
                // back to the caller via the blame envelope's snippetContext.
                // Anchor every resolved path under the git root (which already
                // contains kbDir) and refuse anything that escapes it.
                bool withinRoot;
                try { withinRoot = PathSafety.TryResolveWithinRoot(gitRoot, path, out path); }
                catch { withinRoot = false; }
                if (!withinRoot)
                {
                    error = Models.McpResponse.Err(
                        code: "PathOutsideRepo",
                        message: "filePath resolves outside the git repository root.");
                    return null;
                }
                if (!File.Exists(path))
                {
                    error = Models.McpResponse.Err(
                        code: "FileNotFound",
                        message: "filePath does not exist: " + req.FilePath);
                    return null;
                }
                return path;
            }

            // Best-effort search: name + part substring across the git-tracked file set.
            string ls, lsErr;
            int rc = RunGit(gitRoot, new[] { "ls-files" }, out ls, out lsErr);
            if (rc != 0)
            {
                error = Models.McpResponse.Err(code: "GitFailed", message: "git ls-files failed: " + lsErr);
                return null;
            }

            string name = req.Name ?? string.Empty;
            string part = req.Part ?? string.Empty;
            foreach (var line in ls.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string rel = line.Trim();
                if (rel.Length == 0) continue;
                if (!string.IsNullOrEmpty(name) &&
                    rel.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(part) &&
                    rel.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                candidates.Add(rel);
            }

            if (candidates.Count == 0) return null;
            // Ambiguous if many matches: caller has to disambiguate.
            if (candidates.Count > 1) return null;
            return Path.Combine(gitRoot, candidates[0]);
        }

        private static string MakeRelative(string root, string full)
        {
            return PathSafety.MakeRelative(root, full);
        }

        private static int RunGit(string workingDir, string[] args, out string stdout, out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            // Prepend safety flags so a configured pager or interactive prompt
            // can't wedge the worker (mirrors PrDescriptionService.RunGit).
            var prefixed = new System.Collections.Generic.List<string> { "--no-pager", "-c", "color.ui=false" };
            prefixed.AddRange(args);

            // net48 has no ArgumentList, so the arguments are joined into one
            // CommandLineToArgv-compatible string. Argv.Quote handles trailing
            // backslashes correctly so a malicious filename ending in '\' can't
            // break out of its quoted token.
            string joined = Argv.Join(prefixed);

            try
            {
                // No encoding passed: this caller has always read git's output in
                // the console codepage, and forcing UTF-8 would change what it sees.
                var outcome = ProcessLauncher.Run(
                    "git",
                    joined,
                    workingDir,
                    GitTimeoutMs,
                    null,
                    new Dictionary<string, string>
                    {
                        { "GIT_TERMINAL_PROMPT", "0" },
                        { "GIT_PAGER", "cat" },
                    });

                if (outcome.StartFailed) { stderr = "Failed to start git process."; return -1; }

                if (outcome.TimedOut)
                {
                    // stdout is deliberately left alone here, as it was before.
                    stderr = "git " + joined + " exceeded 30s in " + workingDir;
                    return -1;
                }

                stdout = outcome.StdOut;
                stderr = outcome.StdErr;
                return outcome.ExitCode;
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return -1;
            }
        }

        private const int GitTimeoutMs = 30000;

        private sealed class BlameEntry
        {
            public string CommitHash;
            public int LineNumber;
            public string Author;
            public string AuthorEmail;
            public string AuthorDate;
            public string Summary;
            public string LineContent;
        }

        private static IEnumerable<BlameEntry> ParsePorcelain(string raw)
        {
            // Porcelain format groups: header line "<hash> <origLine> <finalLine> [<groupSize>]"
            // followed by k:v lines (author, author-mail, summary, author-time, ...) and a
            // single content line prefixed with '\t'.
            var commitInfo = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var entries = new List<BlameEntry>();
            BlameEntry current = null;
            string currentHash = null;
            using (var rdr = new StringReader(raw))
            {
                string line;
                while ((line = rdr.ReadLine()) != null)
                {
                    if (line.StartsWith("\t"))
                    {
                        if (current != null)
                        {
                            current.LineContent = line.Substring(1);
                            if (commitInfo.TryGetValue(currentHash ?? string.Empty, out var info))
                            {
                                info.TryGetValue("author", out current.Author);
                                info.TryGetValue("author-mail", out current.AuthorEmail);
                                info.TryGetValue("summary", out current.Summary);
                                if (info.TryGetValue("author-time", out var t) &&
                                    long.TryParse(t, out var epoch))
                                {
                                    current.AuthorDate = DateTimeOffset.FromUnixTimeSeconds(epoch)
                                        .UtcDateTime.ToString("o");
                                }
                            }
                            entries.Add(current);
                            current = null;
                            currentHash = null;
                        }
                        continue;
                    }

                    var parts = line.Split(' ');
                    // Header: 40-char hash, origLine, finalLine, [groupSize]
                    if (parts.Length >= 3 && parts[0].Length == 40 && IsHex(parts[0]))
                    {
                        currentHash = parts[0];
                        int finalLine;
                        int.TryParse(parts[2], out finalLine);
                        current = new BlameEntry { CommitHash = currentHash, LineNumber = finalLine };
                        if (!commitInfo.ContainsKey(currentHash))
                            commitInfo[currentHash] = new Dictionary<string, string>(StringComparer.Ordinal);
                        continue;
                    }

                    // k:v meta lines, scoped to currentHash.
                    if (currentHash != null && parts.Length >= 1)
                    {
                        int sp = line.IndexOf(' ');
                        if (sp > 0)
                        {
                            string key = line.Substring(0, sp);
                            string val = line.Substring(sp + 1);
                            commitInfo[currentHash][key] = val;
                        }
                    }
                }
            }
            return entries;
        }

        private static bool IsHex(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        private static string Error(string msg, string code)
        {
            return new JObject { ["error"] = msg, ["code"] = code }
                .ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
