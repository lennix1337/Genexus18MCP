using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Opt-in git history of what agents changed in a Knowledge Base. Confirmed writes are queued
    /// (an O(1) enqueue on the request path) and a single background consumer writes the part text
    /// into a shadow repository outside the KB folder and makes one commit per change, so a
    /// developer can use plain <c>git log</c>, <c>git diff</c> and <c>git blame</c> per object.
    /// History never fails a tool call: any problem is counted and logged here, and a missing git
    /// or an unusable repository switches the feature off for the process with a single log line.
    /// </summary>
    internal sealed class KbHistoryRecorder
    {
        internal const string EnabledVariable = "GXMCP_KB_HISTORY";
        internal const string DirectoryVariable = "GXMCP_KB_HISTORY_DIR";
        internal const int DefaultCapacity = 256;
        private const int GitTimeoutMs = 30_000;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly string[] ScrubbedGitVariables =
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
            "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES"
        };

        private readonly string _root;
        private readonly string _gitExecutable;
        private readonly Action<string> _log;
        private readonly Channel<KbHistoryJob> _queue;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly Task _consumer;
        private readonly HashSet<string> _readyRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _gitChecked;
        private volatile bool _disabled;
        private long _enqueued, _dropped, _committed, _unchanged, _failed, _skipped, _abandoned;

        internal KbHistoryRecorder(string root, string gitExecutable, Action<string> log, int capacity = DefaultCapacity)
        {
            _root = Path.GetFullPath(root);
            _gitExecutable = gitExecutable;
            _log = log;
            // Drop-oldest keeps the newest persisted text (every job carries the whole part), so a
            // burst larger than the queue loses intermediate commits, never the final state.
            _queue = Channel.CreateBounded<KbHistoryJob>(
                new BoundedChannelOptions(Math.Max(1, capacity))
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                },
                _ => NoteDropped());
            _consumer = Task.Factory.StartNew(
                () => ConsumeAsync(_cancel.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        internal bool IsDisabled => _disabled;
        internal long Enqueued => Interlocked.Read(ref _enqueued);
        internal long Dropped => Interlocked.Read(ref _dropped);
        internal long Committed => Interlocked.Read(ref _committed);
        internal long Unchanged => Interlocked.Read(ref _unchanged);
        internal long Failed => Interlocked.Read(ref _failed);
        internal long Skipped => Interlocked.Read(ref _skipped);
        internal long Abandoned => Interlocked.Read(ref _abandoned);

        /// <summary>Null (feature off, zero cost) unless <c>GXMCP_KB_HISTORY</c> is 1/true.</summary>
        internal static KbHistoryRecorder? CreateFromEnvironment(Action<string> log)
        {
            string? flag = Environment.GetEnvironmentVariable(EnabledVariable);
            if (!string.Equals(flag?.Trim(), "1", StringComparison.Ordinal)
                && !string.Equals(flag?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return null;

            string? configured = Environment.GetEnvironmentVariable(DirectoryVariable);
            string root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GxMcp", "history")
                : configured.Trim();
            log($"[KbHistory] enabled; shadow repositories under '{root}'.");
            return new KbHistoryRecorder(root, "git", log);
        }

        /// <summary>Counts a candidate call that was not a confirmed write (cheap, request path).</summary>
        internal void NoteSkipped() => Interlocked.Increment(ref _skipped);

        /// <summary>O(1), non-blocking, never throws. False when the feature has switched itself off.</summary>
        internal bool TryEnqueue(KbHistoryJob job)
        {
            if (_disabled) return false;
            if (!_queue.Writer.TryWrite(job)) return false;
            Interlocked.Increment(ref _enqueued);
            return true;
        }

        /// <summary>
        /// Stops accepting jobs, lets the consumer drain for up to <paramref name="drainTimeout"/>,
        /// then cancels the rest (killing a running git). Safe to call more than once.
        /// </summary>
        internal async Task StopAsync(TimeSpan drainTimeout)
        {
            _queue.Writer.TryComplete();
            if (await Task.WhenAny(_consumer, Task.Delay(drainTimeout)).ConfigureAwait(false) != _consumer)
            {
                _cancel.Cancel();
                await Task.WhenAny(_consumer, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            // Whatever the consumer never reached is abandoned, not lost silently.
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _abandoned);
            _log($"[KbHistory] stopped: enqueued={Enqueued} committed={Committed} unchanged={Unchanged} dropped={Dropped} failed={Failed} skipped={Skipped} abandoned={Abandoned}.");
        }

        private void NoteDropped()
        {
            long dropped = Interlocked.Increment(ref _dropped);
            if (dropped == 1 || dropped % 100 == 0)
                _log($"[KbHistory] queue full; dropped the oldest pending history entry (total dropped: {dropped}).");
        }

        private async Task ConsumeAsync(CancellationToken token)
        {
            try
            {
                await foreach (KbHistoryJob job in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (_disabled) continue;
                    try
                    {
                        RecordJob(job, token);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref _abandoned);
                        return;
                    }
                    catch (Exception ex)
                    {
                        long failed = Interlocked.Increment(ref _failed);
                        if (failed <= 5 || failed % 100 == 0)
                            _log($"[KbHistory] could not record {job.ObjectType}/{job.ObjectName}: {LogRedaction.Redact(ex.Message)} (failures: {failed}).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Disable(string reason)
        {
            if (_disabled) return;
            _disabled = true;
            _log($"[KbHistory] disabled for this process: {LogRedaction.Redact(reason)}");
        }

        private void RecordJob(KbHistoryJob job, CancellationToken token)
        {
            if (!_gitChecked)
            {
                _gitChecked = true;
                var probe = RunGit(null, token, "--version");
                if (probe.ExitCode != 0)
                {
                    Disable($"git is not available ({probe.Error}).");
                    return;
                }
            }

            string? repo = EnsureRepository(job, token);
            if (repo == null) return;

            string relativeDir = job.ObjectType + "/" + job.ObjectName;
            string objectDir = Path.GetFullPath(Path.Combine(repo, job.ObjectType, job.ObjectName));
            if (!objectDir.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("history path escaped the repository");

            string stagedPath;
            if (job.Kind == KbHistoryJobKind.Delete)
            {
                if (!Directory.Exists(objectDir)) { Interlocked.Increment(ref _unchanged); return; }
                Directory.Delete(objectDir, recursive: true);
                stagedPath = relativeDir;
            }
            else
            {
                string content = job.Content ?? string.Empty;
                if (!string.IsNullOrEmpty(job.ExpectedHash)
                    && !string.Equals(Sha256Hex(content), job.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    // The receipt text is not the text it certifies; recording it would be a false history.
                    Interlocked.Increment(ref _skipped);
                    return;
                }
                Directory.CreateDirectory(objectDir);
                File.WriteAllText(Path.Combine(objectDir, job.Part + ".gx"), content, Utf8NoBom);
                stagedPath = relativeDir + "/" + job.Part + ".gx";
            }

            Git(repo, token, "add", "-A", "--", stagedPath);
            string subject = job.Kind == KbHistoryJobKind.Delete
                ? $"{job.ToolName} {job.Action} {job.ObjectType} {job.ObjectName}"
                : $"{job.ToolName} {job.Action} {job.ObjectType} {job.ObjectName} ({job.Part})";
            var commit = RunGit(repo, token, CommitArguments(subject, BuildBody(job)));
            if (commit.ExitCode == 0)
            {
                Interlocked.Increment(ref _committed);
                return;
            }
            // Exit 1 with nothing staged is "the text did not change", not a failure.
            if (RunGit(repo, token, "diff", "--cached", "--quiet").ExitCode == 0)
            {
                Interlocked.Increment(ref _unchanged);
                return;
            }
            throw new InvalidOperationException("git commit failed: " + commit.Error);
        }

        private string? EnsureRepository(KbHistoryJob job, CancellationToken token)
        {
            string repo = ResolveRepositoryPath(job);
            if (_readyRepositories.Contains(repo)) return repo;

            string kbFull = Path.GetFullPath(job.KbPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (repo.StartsWith(kbFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(repo, kbFull, StringComparison.OrdinalIgnoreCase))
            {
                Disable($"the history location '{repo}' is inside the Knowledge Base folder.");
                return null;
            }

            try
            {
                Directory.CreateDirectory(repo);
                if (!Directory.Exists(Path.Combine(repo, ".git")))
                {
                    var init = RunGit(repo, token, "init", "-q");
                    if (init.ExitCode != 0) { Disable($"git init failed ({init.Error})."); return null; }
                }
                string attributes = Path.Combine(repo, ".gitattributes");
                if (!File.Exists(attributes))
                {
                    // Keep the persisted text byte for byte: no EOL conversion on add or checkout.
                    File.WriteAllText(attributes, "* -text\n", Utf8NoBom);
                    Git(repo, token, "add", "--", ".gitattributes");
                    Git(repo, token, CommitArguments("Initialize GeneXus KB history", "Shadow repository written by GeneXus MCP."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Disable($"the history repository could not be initialised ({ex.Message}).");
                return null;
            }

            _readyRepositories.Add(repo);
            _log($"[KbHistory] repository for KB '{job.KbAlias ?? job.KbPath}': {repo}");
            return repo;
        }

        private string ResolveRepositoryPath(KbHistoryJob job)
        {
            string alias = job.KbAlias != null && SafePathSegment.IsSafe(job.KbAlias) && job.KbAlias.Length <= 48
                ? job.KbAlias
                : "kb";
            return Path.GetFullPath(Path.Combine(_root, alias + "-" + GxwireCacheLocation.StableKey(Path.GetFullPath(job.KbPath))[..12]));
        }

        // Commit identity is fixed and passed per call: nothing reads or changes the user's git config.
        private static string[] CommitArguments(string subject, string body)
            => new[]
            {
                "-c", "user.name=GeneXus MCP", "-c", "user.email=genexus-mcp@localhost",
                "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false",
                // --no-verify: a global pre-commit hook must not block or slow the shadow repository.
                "commit", "-q", "--no-verify", "-m", subject, "-m", body
            };

        private static string BuildBody(KbHistoryJob job)
        {
            var body = new StringBuilder();
            void Line(string label, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                string clean = LogRedaction.Redact(value).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (clean.Length > 200) clean = clean.Substring(0, 200);
                body.Append(label).Append(": ").Append(clean).Append('\n');
            }
            Line("KB", job.KbAlias);
            Line("Session", job.SessionId);
            Line("Operation", job.OperationId);
            Line("Client-Request-Id", job.ClientRequestId);
            if (job.Kind == KbHistoryJobKind.Write) Line("Content-SHA256", Sha256Hex(job.Content ?? string.Empty));
            return body.Length == 0 ? "GeneXus MCP" : body.ToString().TrimEnd('\n');
        }

        private static string Sha256Hex(string text)
            => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(text))).ToLowerInvariant();

        private void Git(string repo, CancellationToken token, params string[] arguments)
        {
            var result = RunGit(repo, token, arguments);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"git exited {result.ExitCode}: {result.Error}");
        }

        private (int ExitCode, string Error) RunGit(string? repo, CancellationToken token, params string[] arguments)
        {
            var start = new ProcessStartInfo(_gitExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The availability probe (no repository) runs wherever the process is; it must not create folders.
                WorkingDirectory = repo ?? Path.GetTempPath()
            };
            // Arguments are a list, never an interpolated command line; -C pins the repository so a
            // GIT_DIR inherited from the environment cannot redirect a commit into another repo.
            if (repo != null) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(repo); }
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (string name in ScrubbedGitVariables) start.Environment.Remove(name);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return (-1, ex.Message);
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var registration = token.Register(() => TryKill(process));
                if (!process.WaitForExit(GitTimeoutMs))
                {
                    TryKill(process);
                    return (-1, "git timed out");
                }
                token.ThrowIfCancellationRequested();
                process.WaitForExit();
                stdout.GetAwaiter().GetResult();
                return (process.ExitCode, stderr.GetAwaiter().GetResult().Trim());
            }
        }

        private static void TryKill(Process process)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
