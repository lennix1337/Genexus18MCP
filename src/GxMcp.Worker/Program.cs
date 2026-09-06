using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace GxMcp.Worker
{
    class Program
    {
        public static readonly BlockingCollection<string> CommandQueue = new BlockingCollection<string>(ResolveQueueCapacity("GXMCP_COMMAND_QUEUE_CAPACITY", 256));
        public static readonly BlockingCollection<string> SdkCommandQueue = new BlockingCollection<string>(ResolveQueueCapacity("GXMCP_SDK_COMMAND_QUEUE_CAPACITY", 64));
        public static readonly ConcurrentQueue<Action> BackgroundQueue = new ConcurrentQueue<Action>();
        // Plan 037: low-priority jobs that must run on the SAME STA thread as ordinary
        // dispatched commands (the sdkWorker/WinForms-bridge thread below), drained by
        // its poll timer only when no real command is pending. Used by KbWatcherService
        // so its SDK polling no longer touches the SDK from a second STA apartment.
        public static readonly BlockingCollection<Action> SdkActionQueue = new BlockingCollection<Action>(ResolveSdkActionCapacity());
        private static int _sdkWorkerThreadId;
        internal static readonly SdkExecutor SdkExecutor = new SdkExecutor(
            () => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _sdkWorkerThreadId),
            action =>
            {
                if (!SdkActionQueue.TryAdd(action)) return false;
                try
                {
                    if (_bridgeForm != null && _bridgeForm.IsHandleCreated)
                        _bridgeForm.BeginInvoke((Action)DrainSdkCommands);
                }
                catch { }
                return true;
            },
            ResolveSdkActionCapacity());

        private static int ResolveSdkActionCapacity()
        {
            var raw = Environment.GetEnvironmentVariable("GXMCP_SDK_QUEUE_CAPACITY");
            return int.TryParse(raw, out var value) && value > 0 && value <= 4096 ? value : 64;
        }

        internal static int ResolveQueueCapacity(string variable, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            return int.TryParse(raw, out var value) && value > 0 && value <= 4096 ? value : fallback;
        }

        internal static bool EnqueueSdkAction(Action action) => SdkExecutor.TryEnqueue(action);

        // FR#20 (v2.6.6 Stream B): soft-reload coordination. When a genexus_worker_reload
        // with mode=soft arrives, we flip this flag, drain the queues, persist
        // BackgroundJobRegistry state, and exit cleanly with code 0 so the gateway can
        // respawn. Read by every dispatch loop that needs to honor a quiesce request.
        public static volatile bool ShuttingDown = false;
        // Exit code 17 = "busy" (FR#19 single-instance reject). Distinct from 0
        // (clean shutdown / soft reload), 1 (init failure), and the process-crash codes.
        public const int ExitCodeBusy = 17;
        public const int ExitCodeSoftReload = 0;
        // Distinct exit code for a recovered native/corrupted-state SDK crash: the in-flight call
        // was answered with an error and the worker is exiting so the gateway respawns a fresh one
        // (the AppDomain is unsafe to reuse after a corrupted-state exception). Issue #35.
        public const int ExitCodePoisoned = 70;

        public static SingleInstanceLock InstanceLock { get; private set; }
        // PERFORMANCE (W-B3): signal that wakes the background worker immediately on Enqueue,
        // instead of busy-polling with Thread.Sleep(100). WaitOne(100) keeps a safety timeout
        // so the loop still re-checks shutdown state if a signal is missed.
        public static readonly AutoResetEvent BackgroundSignal = new AutoResetEvent(false);

        public static void EnqueueBackground(Action work)
        {
            if (work == null) return;
            BackgroundQueue.Enqueue(work);
            try { BackgroundSignal.Set(); } catch { }
        }
        // Last time a real command was dispatched — drives idle LOH compaction below.
        private static long _lastActivityTicks = DateTime.UtcNow.Ticks;
        internal static void MarkWorkerActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        // Bug #4: single-flight visibility. Every SDK-touching command runs serially on the
        // SdkWorker STA thread; a long op (build / reorg_impact deep / drift deep / big edit)
        // holds it for minutes while a second SDK command queues behind it. The gateway then
        // reports a misleading "Gateway timeout starting build" 60s later. We track what the
        // SDK thread is doing so a new heavy command that arrives while the thread has been
        // busy longer than the reject threshold gets an immediate, clear WorkerBusy envelope
        // instead of silently queuing behind the long op.
        private static volatile int _sdkBusy;        // 1 while a command runs on the SdkWorker STA thread
        private static long _sdkBusySinceTicks;       // DateTime.UtcNow.Ticks when the current SDK command started (x86: use Interlocked)
        private static volatile string _sdkBusyOp;    // "method/action" of the in-flight SDK command (diagnostics)
        private static volatile string _sdkBusyOperationId; // gateway operationId from _meta.progressToken
        private static readonly int _busyRejectThresholdMs = ResolveBusyRejectThresholdMs();

        internal static JObject GetSdkBusyStatus(DateTime? nowUtc = null)
        {
            bool active = _sdkBusy == 1;
            long sinceTicks = Interlocked.Read(ref _sdkBusySinceTicks);
            var status = new JObject
            {
                ["active"] = active,
                ["operation"] = active ? _sdkBusyOp : null,
                ["operationId"] = active ? _sdkBusyOperationId : null,
                ["elapsedMs"] = 0
            };
            if (active && sinceTicks > 0)
            {
                var since = new DateTime(sinceTicks, DateTimeKind.Utc);
                status["elapsedMs"] = Math.Max(0L, (long)((nowUtc ?? DateTime.UtcNow) - since).TotalMilliseconds);
            }
            return status;
        }

        internal static void MergeSdkBusyStatus(JObject status, JObject sdkBusy)
        {
            if (status == null) return;
            sdkBusy = sdkBusy ?? new JObject { ["active"] = false, ["elapsedMs"] = 0 };
            bool active = sdkBusy["active"]?.ToObject<bool?>() == true;
            status["sdkBusy"] = sdkBusy;
            if (active)
            {
                status["isBusy"] = true;
                status["activeOperation"] = sdkBusy["operation"]?.DeepClone();
            }
        }

        private static int ResolveBusyRejectThresholdMs()
        {
            var s = Environment.GetEnvironmentVariable("GXMCP_BUSY_REJECT_MS");
            if (s != null && int.TryParse(s, out var v)) return v <= 0 ? int.MaxValue : v; // 0/negative disables rejection
            return 3000;
        }

        // Output is bounded as well as input. A blocked/slow MCP client must not
        // turn verbose SDK logging or a large response burst into an unbounded
        // process-wide queue; QueueWriter applies normal producer backpressure
        // while the dedicated writer drains it.
        private static readonly BlockingCollection<string> _outputQueue = new BlockingCollection<string>(ResolveQueueCapacity("GXMCP_OUTPUT_QUEUE_CAPACITY", 256));
        private static readonly BlockingCollection<string> _errorQueue = new BlockingCollection<string>(ResolveQueueCapacity("GXMCP_ERROR_QUEUE_CAPACITY", 256));
        private static CommandDispatcher _dispatcher;
        private static TextWriter _originalOut;
        private static TextWriter _originalError;
        private static StreamWriter _pipeWriter;
        private static Form _bridgeForm;
        private static readonly ManualResetEvent _uiReady = new ManualResetEvent(false);

        [STAThread]
        static void Main(string[] args)
        {
            try {
                // Force UTF-8 on worker stdio before capturing the original writers.
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);

                // ELITE: Start output threads first to capture all logs immediately
                StartOutputThreads();
                Console.SetOut(new QueueWriter(_outputQueue));
                Console.SetError(new QueueWriter(_errorQueue));

                // Ensure culture is Portuguese-Brazil for SDK character mapping
                try {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
                    System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("pt-BR");
                } catch { }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                    try
                    {
                        var ex = e.ExceptionObject as Exception;
                        var proc = System.Diagnostics.Process.GetCurrentProcess();
                        long memMb = proc.WorkingSet64 / (1024 * 1024);
                        long privMb = proc.PrivateMemorySize64 / (1024 * 1024);
                        var uptime = DateTime.UtcNow - proc.StartTime.ToUniversalTime();
                        Logger.Error(
                            "[WORKER-CRASH] terminating=" + e.IsTerminating
                            + " memMB=" + memMb + " privMB=" + privMb
                            + " uptimeSec=" + (int)uptime.TotalSeconds
                            + " gcMemMB=" + (GC.GetTotalMemory(false) / (1024 * 1024))
                            + " threadCount=" + proc.Threads.Count
                            + " exType=" + (ex?.GetType().FullName ?? "<null>")
                            + " exMsg=" + (ex?.Message ?? "<null>")
                            + Environment.NewLine + "Stack: " + (ex?.ToString() ?? "<null>"));
                    }
                    catch { Logger.Error("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject as Exception)?.ToString()); }
                };

                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => {
                    Logger.Error("[WORKER-CRASH] UNOBSERVED TASK EXCEPTION: " + e.Exception?.ToString());
                    e.SetObserved();
                };

                WriteLine("WORKER_HANDSHAKE_START");
                Logger.Info("Worker process started (STA Mode).");

                // Friction 2026-05-22: reap orphaned MSBuild.exe processes whose
                // parent worker died mid-build (worker_reload force=true, crash, etc.).
                // Safe heuristic: only kill MSBuild.exe whose ProcessParentId points
                // to a PID that no longer exists. Doesn't touch the user's IDE / VS.
                try { ReapOrphanMsbuilds(); }
                catch (Exception ex) { Logger.Warn("[ORPHAN-REAP] failed: " + ex.Message); }

                // ELITE: Configuration Resolve Logic (Env > Local Config > Error)
                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR");
                string kbPath = Environment.GetEnvironmentVariable("GX_KB_PATH");

                if (string.IsNullOrEmpty(gxPath) || string.IsNullOrEmpty(kbPath))
                {
                    var config = LoadLocalConfig();
                    if (config != null)
                    {
                        if (string.IsNullOrEmpty(gxPath)) gxPath = config["InstallationPath"]?.ToString();
                        if (string.IsNullOrEmpty(kbPath)) kbPath = config["KBPath"]?.ToString();
                    }
                }

                if (string.IsNullOrEmpty(gxPath))
                    throw new Exception("GX_PROGRAM_DIR not specified in environment or local config.json.");

                // FR#19 (v2.6.6 Stream B): refuse to start when another worker already
                // serves this (kbPath, workerExe) pair. We resolve the cli-arg kbPath
                // first so the lock key matches whatever the gateway intended.
                string lockKb = kbPath;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--kb" && i + 1 < args.Length) { lockKb = args[i + 1]; break; }
                }
                string workerExePath = "";
                try { workerExePath = Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location ?? ""; }
                catch (Exception lockEx) { Logger.Error("[SingleInstanceLock] could not resolve own exe path: " + lockEx.Message); }
                string lockDir = "";
                try { lockDir = Path.GetDirectoryName(workerExePath) ?? Path.GetTempPath(); }
                catch { lockDir = Path.GetTempPath(); }
                InstanceLock = SingleInstanceLock.TryAcquire(lockKb ?? "", workerExePath, lockDir);
                if (!InstanceLock.Acquired)
                {
                    int existing = InstanceLock.ExistingPid ?? -1;
                    // Bypass the QueueWriter to guarantee the gateway sees this message
                    // even before the output thread fully spins up.
                    try { _originalOut?.WriteLine("WORKER_HANDSHAKE_REJECT_BUSY pid=" + existing); _originalOut?.Flush(); } catch { }
                    Logger.Info("[SingleInstanceLock] reject_busy existingPid=" + existing + " key=" + InstanceLock.Key);
                    Environment.Exit(ExitCodeBusy);
                    return;
                }
                Logger.Info("[SingleInstanceLock] acquired pid=" + Process.GetCurrentProcess().Id + " lockPath=" + InstanceLock.LockPath);
                // Advertise soft-reload support in the handshake so the gateway can
                // default to mode=soft for callers that don't specify a mode.
                WriteLine("WORKER_HANDSHAKE_FEATURES soft_reload=true single_instance=true");

                AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) => {
                    try {
                        string assemblyName = new AssemblyName(resolveArgs.Name).Name + ".dll";
                        string assemblyPath = Path.Combine(gxPath, assemblyName);
                        if (File.Exists(assemblyPath)) return Assembly.LoadFrom(assemblyPath);
                    } catch { }
                    return null;
                };

                string pipeName = Environment.GetEnvironmentVariable("GX_MCP_PIPE");
                if (!string.IsNullOrEmpty(pipeName))
                {
                    try {
                        var pipeClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                        pipeClient.Connect(30000);
                        var writer = new StreamWriter(pipeClient, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                        var reader = new StreamReader(pipeClient, new System.Text.UTF8Encoding(false));
                        
                        _pipeWriter = writer;
                        Console.SetIn(reader);
                        Logger.Info($"[Worker] Connected to IPC Pipe {pipeName} successfully.");
                    } catch (Exception ex) {
                        Logger.Error($"[Worker] IPC Pipe Connection Error: {ex.Message}. Falling back to STDIO.");
                    }
                }

                // v2.6.6 live-test finding: ArtechTask.cctor activates the
                // GxServiceManager process-singleton and throws GxException
                // "Service Manager já foi ativado" if SM is already active
                // when the cctor first runs. Our worker previously activated SM
                // via KbService.OpenKB → ArtechTask cctor would throw on first
                // in-process build, then the type was permanently dead for the
                // worker's lifetime (cctor failures cache forever in .NET).
                //
                // The IDE avoids this by triggering ArtechTask FIRST. We mirror
                // that: warm the cctor BEFORE InitializeSdk so ArtechTask gets
                // to set up SM on its own terms; later SDK Initialize calls and
                // KbService.OpenKB then reuse the same activated SM cleanly.
                //
                // Best-effort: any failure here is logged and ignored — the
                // in-process build path will fall back to MSBuild.exe spawn.
                //
                // Cold-start breakdown is logged per-phase ([ArtechTask-warmup] …,
                // Full SDK Initialization SUCCESS in …, [KB-OPEN] elapsedMs=…). This
                // stopwatch adds a single end-to-end [COLD-START] line at sdk_ready so
                // support can read time-to-ready from one line. On most KBs the SM
                // warmup dominates; a sudden jump in kbOpen points at the data-store
                // connect the SDK attempts during open.
                var coldStartSw = System.Diagnostics.Stopwatch.StartNew();
                TryWarmupArtechTaskCctor(gxPath);

                InitializeSdk(gxPath);
                _dispatcher = CommandDispatcher.Instance;
                
                // Check command line arguments for --kb
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--kb" && i + 1 < args.Length)
                    {
                        kbPath = args[i + 1];
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(kbPath))
                {
                    try {
                        Logger.Info($"Worker auto-opening KB: {kbPath}");
                        _dispatcher.GetKbService().OpenKB(kbPath);
                    } catch (Exception ex) {
                        Logger.Error($"Worker failed to auto-open KB: {ex.Message}");
                    }
                }

                coldStartSw.Stop();
                _sdkReadyAtMs = _processSw.ElapsedMilliseconds;
                Logger.Info($"[COLD-START] totalMs={coldStartSw.ElapsedMilliseconds} (SM-warmup + SDK-init + KB-open until ready) kb={(string.IsNullOrEmpty(kbPath) ? "<none>" : kbPath)}");
                // Fase 0: one consolidated line attributing the cold start so "where did the
                // N seconds go" is answerable from a single grep. Percentages computed inline.
                {
                    long total = coldStartSw.ElapsedMilliseconds;
                    long kbOpenMs = KbService.LastOpenElapsedMs;
                    long dsMs = KbService.LastDatastoreProbeMs;
                    long accounted = _warmupMs + _sdkInitMs + kbOpenMs;
                    double pct(long part) => total > 0 ? Math.Round(part * 100.0 / total, 1) : 0;
                    Logger.Info($"[COLD-START-BREAKDOWN] totalMs={total} smWarmupMs={_warmupMs} sdkInitMs={_sdkInitMs} kbOpenMs={kbOpenMs} kbOpenDatastoreMs={dsMs} unaccountedMs={Math.Max(0, total - accounted)} | smWarmupPct={pct(_warmupMs)} sdkInitPct={pct(_sdkInitMs)} kbOpenPct={pct(kbOpenMs)}");
                }
                Logger.Info("Worker SDK ready.");
                // Tell the gateway the SDK is up so it can start the per-tool timeout
                // clock only AFTER cold-start finishes (KB open + SDK init can take ~50s).
                // Without this, the first tool call's timeout budget is consumed by boot.
                try { SendNotification("notifications/worker/sdk_ready", new { kb = kbPath }); } catch { }

                // FR#20: best-effort restore of BackgroundJobRegistry rehydration state
                // written by the previous (soft-reloaded) worker. JobEntry lives in the
                // gateway, but we surface the file path here so a future worker-side
                // job system can pick it up — and so soft reload tests can observe the
                // file lifecycle without touching the gateway.
                try
                {
                    string stateDir = Path.Combine(Path.GetDirectoryName(workerExePath) ?? Path.GetTempPath(), "state");
                    string jobsFile = Path.Combine(stateDir, "jobs.json");
                    if (File.Exists(jobsFile))
                    {
                        Logger.Info("[SoftReload] found pending jobs snapshot at " + jobsFile + " (size=" + new FileInfo(jobsFile).Length + ")");
                        // Snapshot is consumed by the gateway through worker_reload_jobs_path
                        // notification; we just leave it for the next pickup. Removed only
                        // by the consumer to avoid races on dual-restart scenarios.
                        SendNotification("notifications/worker/jobs_restored", new {
                            path = jobsFile,
                            sizeBytes = new FileInfo(jobsFile).Length
                        });
                    }
                }
                catch (Exception jrEx) { Logger.Error("[SoftReload] jobs snapshot probe failed: " + jrEx.Message); }


                // Start External KB Watcher
                // Fase 2: pass the IndexCacheService so detected changes update the in-memory
                // index live (keeps it warm during a session); the notification callback is
                // unchanged.
                var watcher = new KbWatcherService(_dispatcher.GetKbService(), (name, type, time) => {
                    SendNotification("notifications/resources/updated", new {
                        name = name,
                        type = type,
                        updatedAt = time,
                        external = true
                    });
                }, _dispatcher.GetIndexCacheService());
                watcher.Start();

                var readerThread = new Thread(() => {
                    while (true) {
                        string line = Console.ReadLine();
                        if (line == null) break;
                        if (line.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase) || line.Contains("\"method\":\"ping\"") || line.Contains("\"action\":\"Ping\""))
                        {
                            WriteLine("{\"jsonrpc\":\"2.0\",\"result\":{\"status\":\"Ready\"},\"id\":\"heartbeat\"}");
                            if (!line.Contains("\"method\"")) continue;
                        }
                        if (!string.IsNullOrWhiteSpace(line) && !CommandQueue.TryAdd(line))
                        {
                            SendQueueBusy(line, "main command queue");
                        }
                    }
                    CommandQueue.CompleteAdding();
                }) { IsBackground = true, Name = "HeartbeatReader" };
                readerThread.Start();

                                // DEDICATED SDK WORKER THREAD (STA with WinForms Bridge)
                var sdkWorker = new Thread(() => {
                    Volatile.Write(ref _sdkWorkerThreadId, Thread.CurrentThread.ManagedThreadId);
                    Logger.Info("SDK Worker Thread started (WinForms Bridge enabled).");
                    _bridgeForm = new Form { 
                        ShowInTaskbar = false, 
                        WindowState = FormWindowState.Minimized,
                        Visible = false 
                    };
                    
                    // The 'Quiet Mode' loop: process commands on the UI thread
                    var pollTimer = new System.Windows.Forms.Timer { Interval = 15 };
                    pollTimer.Tick += (s, e) => DrainSdkCommands();
                    
                    _bridgeForm.Load += (s, e) => {
                        pollTimer.Start();
                        _uiReady.Set();
                        Logger.Debug("WinForms Bridge Ready.");
                    };
                    
                    Application.Run(_bridgeForm);
                }) { IsBackground = true, Name = "SdkWorker", Priority = ThreadPriority.AboveNormal };
                sdkWorker.SetApartmentState(ApartmentState.STA);
                sdkWorker.Start();
                
                _uiReady.WaitOne(10000); // Wait for the bridge to come online

                // DEDICATED STA BACKGROUND TASK THREAD
                var backgroundWorker = new Thread(() => {
                    Logger.Info("Background STA Worker Thread started.");
                    while (!CommandQueue.IsCompleted) {
                        if (BackgroundQueue.TryDequeue(out var action)) {
                            try { action(); }
                            catch (Exception ex) { Logger.Error("Background Task Error: " + ex.Message); }
                        } else {
                            // PERFORMANCE (W-B3): wait for signal from EnqueueBackground; 100ms
                            // fallback re-checks CommandQueue.IsCompleted on shutdown.
                            BackgroundSignal.WaitOne(100);
                        }
                    }
                }) { IsBackground = true, Name = "BackgroundWorker" };
                backgroundWorker.SetApartmentState(ApartmentState.STA);
                backgroundWorker.Start();

                // Idle LOH compaction: the x86 heap fragments over a long session (big JSON
                // serializations, SDK part buffers). When the worker has been idle a while,
                // compact the LOH once and collect — zero user-visible latency (nobody's
                // waiting) and it reclaims address space that would otherwise push the worker
                // toward the ~4GB ceiling / a heap-recycle. One compaction per idle period.
                var idleGcWorker = new Thread(IdleMemoryMaintenance) { IsBackground = true, Name = "IdleMemoryMaintenance" };
                idleGcWorker.Start();

                // MAIN DISPATCHER LOOP
                while (!CommandQueue.IsCompleted || CommandQueue.Count > 0)
                {
                    if (CommandQueue.TryTake(out string line, 100))
                    {
                        // P3 perf: parseia o comando UMA vez aqui e propaga o JObject pelo
                        // caminho MTA (Task.Run) — elimina os 3 re-parses em IsThreadSafe/
                        // Dispatch/DispatchInternal. A fila STA segue recebendo a string crua.
                        var cmdObj = TryParseCommand(line);
                        if (_dispatcher.IsThreadSafe(cmdObj))
                            System.Threading.Tasks.Task.Run(() => ProcessCommand(cmdObj, line));
                        else if (TryRejectBusy(line))
                        { /* answered with a WorkerBusy envelope — do not queue behind the long op */ }
                        else
                            EnqueueSdkCommand(line);
                    }
                }

                Logger.Info("Input EOF reached. Shutting down...");
                SdkCommandQueue.CompleteAdding();
                while (!SdkCommandQueue.IsCompleted || SdkCommandQueue.Count > 0)
                {
                    Thread.Sleep(50);
                }
                Logger.Info("Worker shutting down safely.");
                SdkActionQueue.CompleteAdding();
                SdkExecutor.Dispose();
            } catch (Exception ex) {
                Logger.Error($"Main FATAL: {ex.Message}");
            }
        }

        private static bool EnqueueSdkCommand(string line)
        {
            if (!SdkCommandQueue.TryAdd(line))
            {
                SendQueueBusy(line, "SDK command queue");
                return false;
            }
            try
            {
                if (_bridgeForm != null && _bridgeForm.IsHandleCreated)
                {
                    _bridgeForm.BeginInvoke((Action)DrainSdkCommands);
                }
            }
            catch { }
            return true;
        }

        private static void SendQueueBusy(string line, string queueName)
        {
            string idJson = "null";
            try { idJson = JObject.Parse(line)["id"]?.ToString() ?? "null"; }
            catch { }

            string busy = GxMcp.Worker.Models.McpResponse.Err(
                code: "WorkerBusy",
                message: "The " + queueName + " is full; the request was not started.",
                hint: "Retry after the active SDK work yields or cancel the running operation.",
                retryAfterMs: 250,
                errorExtra: new JObject
                {
                    ["queue"] = queueName,
                    ["capacity"] = queueName == "SDK command queue" ? SdkCommandQueue.BoundedCapacity : CommandQueue.BoundedCapacity
                });
            SendResponse(busy, idJson);
            Logger.Warn("[QUEUE-BUSY] " + queueName + " capacity="
                + (queueName == "SDK command queue" ? SdkCommandQueue.BoundedCapacity : CommandQueue.BoundedCapacity)
                + " id=" + idJson);
        }

        private static void DrainSdkCommands()
        {
            while (SdkCommandQueue.TryTake(out string line))
            {
                Interlocked.Exchange(ref _sdkBusySinceTicks, DateTime.UtcNow.Ticks);
                _sdkBusyOp = DescribeCommand(line);
                _sdkBusyOperationId = ExtractOperationId(line);
                _sdkBusy = 1;
                try { ProcessCommand(line); }
                catch (Exception ex) { Logger.Error("SDK Command Error: " + ex.Message); }
                finally { _sdkBusy = 0; _sdkBusyOp = null; _sdkBusyOperationId = null; }
            }
            while (SdkActionQueue.TryTake(out var job))
            {
                try { job(); }
                catch (Exception ex) { Logger.Error("SDK Action Error: " + ex.Message); }
            }
        }

        /// <summary>
        /// FR#20: drain in-flight commands, persist gateway job state via stdout notification,
        /// close the KB, and exit cleanly so the gateway can respawn against a fresh binary.
        /// Returns the worker-side ack JSON; the gateway sees the clean exit and re-launches.
        /// </summary>
        public static string PerformSoftReload(int drainTimeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Logger.Info("[SoftReload] beginning soft reload drainTimeoutMs=" + drainTimeoutMs);
                ShuttingDown = true;

                // Signal the gateway: persist your JobRegistry NOW before we exit.
                // The gateway listens for this and dumps to publish\worker\state\jobs.json.
                try
                {
                    string stateDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state");
                    try { Directory.CreateDirectory(stateDir); }
                    catch (Exception dirEx) { Logger.Error("[SoftReload] state dir create failed: " + dirEx.Message); }
                    SendNotification("notifications/worker/persist_jobs_request", new {
                        path = Path.Combine(stateDir, "jobs.json"),
                        reason = "soft_reload"
                    });
                }
                catch (Exception nEx) { Logger.Error("[SoftReload] persist_jobs notification failed: " + nEx.Message); }

                // Drain main queue (already STA-routed) — let in-flight commands flush.
                var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, drainTimeoutMs));
                while (DateTime.UtcNow < deadline)
                {
                    if (CommandQueue.Count == 0 && SdkCommandQueue.Count == 0 && BackgroundQueue.IsEmpty) break;
                    Thread.Sleep(100);
                }
                Logger.Info("[SoftReload] drain finished pendingMain=" + CommandQueue.Count +
                            " pendingSdk=" + SdkCommandQueue.Count + " pendingBg=" + BackgroundQueue.Count +
                            " elapsedMs=" + sw.ElapsedMilliseconds);

                // Close KB best-effort. KbService has no explicit Close — releasing the dynamic
                // reference is the closest we have; the SDK disposes on AppDomain unload.
                try
                {
                    var kb = _dispatcher?.GetKbService()?.GetKB();
                    if (kb != null)
                    {
                        try { kb.Close(); } catch (Exception closeEx) { Logger.Error("[SoftReload] kb.Close() threw: " + closeEx.Message); }
                    }
                }
                catch (Exception kbEx) { Logger.Error("[SoftReload] KB close best-effort failed: " + kbEx.Message); }

                // Schedule exit on a background thread so the dispatcher can return the
                // ack to the gateway BEFORE the process dies.
                var exitDelay = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(750);
                    try { InstanceLock?.Dispose(); } catch (Exception lockEx) { Logger.Error("[SoftReload] lock dispose: " + lockEx.Message); }
                    Logger.Info("[SoftReload] exiting code=" + ExitCodeSoftReload + " totalMs=" + sw.ElapsedMilliseconds);
                    Environment.Exit(ExitCodeSoftReload);
                });

                return "{\"status\":\"Accepted\",\"mode\":\"soft\",\"drainMs\":" + sw.ElapsedMilliseconds +
                       ",\"note\":\"Worker draining and exiting code 0 in ~750ms; gateway will respawn against current binary.\"}";
            }
            catch (Exception ex)
            {
                Logger.Error("[SoftReload] failed: " + ex.Message);
                return "{\"status\":\"Error\",\"mode\":\"soft\",\"error\":\"" + ex.Message.Replace("\"", "\\\"") + "\"}";
            }
        }

        // v2.6.6: one-shot warmup of Artech.MsBuild.Common.ArtechTask so its
        // static ctor activates GxServiceManager BEFORE InitializeSdk / OpenKB
        // get a chance to. Order matters: cctor failures cache permanently per
        // AppDomain, so this MUST succeed on the first try. If it throws here
        // (e.g. SDK install layout mismatch) the in-process build path will be
        // dead for this worker session, but the external MSBuild.exe fallback
        // still works.
        // Set true once the ArtechTask cctor has activated the GxServiceManager, so
        // InitializeSdk can skip the redundant (and ~35s-wasteful) Connector SM activation
        // that otherwise throws "O Service Manager já foi ativado".
        private static bool _serviceManagerActivatedByWarmup;

        // Fase 0 instrumentation: per-phase cold-start elapsed captured into statics so
        // the consolidated [COLD-START-BREAKDOWN] line can attribute totalMs across
        // SM-warmup / SDK-init / KB-open without re-parsing separate log lines. _processSw
        // anchors process start so [TIME-TO-USABLE] can separate the ~90s SM warmup from
        // the object-walk (lite pass runs after sdk_ready, outside coldStartSw).
        private static long _warmupMs = 0;
        private static long _sdkInitMs = 0;
        private static long _sdkReadyAtMs = 0;
        internal static readonly System.Diagnostics.Stopwatch _processSw = System.Diagnostics.Stopwatch.StartNew();
        internal static long ProcessElapsedMs => _processSw.ElapsedMilliseconds;
        internal static long SdkReadyAtMs => _sdkReadyAtMs;

        private static void TryWarmupArtechTaskCctor(string gxPath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string asmPath = Path.Combine(gxPath, "Genexus.MsBuild.Tasks.dll");
                // No File.Exists pre-check — LoadFrom throws a clean
                // FileNotFoundException that we unwrap below.
                var asm = Assembly.LoadFrom(asmPath);
                // SpecifyOneOnly inherits from ArtechTask; touching the type
                // triggers ArtechTask's static initializer.
                var t = asm.GetType("Genexus.MsBuild.Tasks.SpecifyOneOnly", throwOnError: false);
                if (t == null)
                {
                    Logger.Warn("[ArtechTask-warmup] SpecifyOneOnly type not found — skipping");
                    return;
                }
                var artechBase = t.BaseType;
                while (artechBase != null && artechBase.FullName != "Artech.MsBuild.Common.ArtechTask")
                    artechBase = artechBase.BaseType;
                if (artechBase == null)
                {
                    Logger.Warn("[ArtechTask-warmup] ArtechTask not in inheritance chain of SpecifyOneOnly — skipping");
                    return;
                }
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(artechBase.TypeHandle);
                _serviceManagerActivatedByWarmup = true;
                Logger.Info($"[ArtechTask-warmup] OK — Service Manager activated by ArtechTask cctor in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                int depth = 0;
                for (Exception e = ex; e != null && depth < 6; e = e.InnerException, depth++)
                {
                    Logger.Error("[ArtechTask-warmup] ex[" + depth + "] "
                                 + e.GetType().FullName + ": " + e.Message);
                }
            }
            finally { sw.Stop(); _warmupMs = sw.ElapsedMilliseconds; }
        }

        private static void InitializeSdk(string gxPath)
        {
            // Per-step timing + full exception-chain logging. Cold-start was observed at
            // ~92s (51s ArtechTask warmup + ~35s here + 6s KB open) and this block ended in
            // a swallowed TargetInvocationException whose real cause was hidden behind the
            // outer message. We now log each step's elapsed ms and, on failure, unwrap the
            // inner-exception chain so the actual culprit + the slow step are visible.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            string lastStep = "(none)";
            void Step(string name, Action body)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                lastStep = name;
                body();
                sw.Stop();
                Logger.Info($"[SDK-INIT] {name} OK in {sw.ElapsedMilliseconds}ms");
            }
            try {
                Logger.Debug($"Setting current directory to {gxPath}");
                Directory.SetCurrentDirectory(gxPath);

                Assembly archAsm = null, connAsm = null;
                Step("LoadFrom Artech.Architecture.Common", () => archAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.Common.dll")));
                Step("ContextService.Initialize", () => {
                    var t = archAsm.GetType("Artech.Architecture.Common.Services.ContextService");
                    t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                });

                Step("CommonServices.Initialize", () => {
                    var blAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.BL.Framework.dll"));
                    var t = blAsm.GetType("Artech.Architecture.BL.Framework.Services.CommonServices");
                    t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                });

                Step("UIServices.SetDisableUI", () => {
                    var uiAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.UI.Framework.dll"));
                    var t = uiAsm.GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                    t?.GetMethod("SetDisableUI", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { true });
                });

                Step("UIServices.Initialize", () => {
                    var uiAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Architecture.UI.Framework.dll"));
                    var t = uiAsm.GetType("Artech.Architecture.UI.Framework.Services.UIServices");
                    t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                });

                Step("KBModelObjectsInitializer.Initialize", () => {
                    var commonAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Artech.Genexus.Common.dll"));
                    var t = commonAsm.GetType("Artech.Genexus.Common.KBModelObjectsInitializer");
                    t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                });

                Step("Connector.Initialize+Start", () => {
                    connAsm = Assembly.LoadFrom(Path.Combine(gxPath, "Connector.dll"));
                    if (_serviceManagerActivatedByWarmup)
                    {
                        // The ArtechTask warmup already activated the GxServiceManager.
                        // Calling Connector.Initialize/Start here re-activates it, which
                        // wastes ~35s and then throws "O Service Manager já foi ativado".
                        // Skip it — SM is up, and KBFactory link + OpenKB below cover the rest.
                        Logger.Info("[SDK-INIT] Connector.Initialize+Start skipped — Service Manager already activated by ArtechTask warmup (avoids the ~35s redundant re-activation).");
                        return;
                    }
                    var connType = connAsm.GetType("Artech.Core.Connector");
                    connType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                    connType?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                });

                Step("Link KBFactory", () => {
                    var kbBaseType = archAsm.GetType("Artech.Architecture.Common.Objects.KnowledgeBase");
                    var factoryProp = kbBaseType?.GetProperty("KBFactory", BindingFlags.Public | BindingFlags.Static);
                    if (factoryProp != null) {
                        var factoryType = connAsm.GetType("Connector.KBFactory");
                        if (factoryType != null) {
                            factoryProp.SetValue(null, Activator.CreateInstance(factoryType));
                            Logger.Info("KBFactory Linked successfully.");
                        }
                    }
                });

                Logger.Info($"Full SDK Initialization SUCCESS in {swTotal.ElapsedMilliseconds}ms.");
            } catch (Exception ex) {
                Logger.Error($"CRITICAL Init Error at step '{lastStep}' after {swTotal.ElapsedMilliseconds}ms (worker still serves via OpenKB):");
                // Unwrap TargetInvocationException + TypeInitializationException layers so the
                // real cause is visible instead of the generic "exception by invocation target".
                int depth = 0;
                for (Exception e = ex; e != null && depth < 8; e = e.InnerException, depth++)
                {
                    Logger.Error($"[SDK-INIT] ex[{depth}] {e.GetType().FullName}: {e.Message}");
                }
            }
            finally { swTotal.Stop(); _sdkInitMs = swTotal.ElapsedMilliseconds; }
        }

        // Idle-driven LOH compaction. Runs one CompactOnce+collect per idle period (reset
        // when activity resumes), so a long session can't accumulate fragmentation
        // indefinitely on the x86 heap. Opt out with GXMCP_IDLE_GC=0.
        private static void IdleMemoryMaintenance()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("GXMCP_IDLE_GC"), "0", StringComparison.OrdinalIgnoreCase))
                return;
            bool compactedThisIdle = false;
            while (!CommandQueue.IsCompleted)
            {
                try
                {
                    Thread.Sleep(30000);
                    double idleMs = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks))).TotalMilliseconds;
                    bool busy = CommandQueue.Count > 0 || SdkCommandQueue.Count > 0 || !BackgroundQueue.IsEmpty;
                    if (busy || idleMs < 60000)
                    {
                        compactedThisIdle = false; // activity resumed — re-arm for the next idle window
                        continue;
                    }
                    if (compactedThisIdle) continue;
                    long beforeMb = GC.GetTotalMemory(false) / (1024 * 1024);
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    long afterMb = GC.GetTotalMemory(true) / (1024 * 1024);
                    Logger.Info($"[IDLE-GC] LOH compaction gcMemMB {beforeMb}->{afterMb} (idleMs={(int)idleMs})");
                    compactedThisIdle = true;
                }
                catch (Exception ex) { Logger.Warn("[IDLE-GC] " + ex.Message); }
            }
        }

        // HandleProcessCorruptedStateExceptions (+ App.config legacyCorruptedStateExceptionsPolicy)
        // lets the catch below see an AccessViolation/SEH raised by the GeneXus SDK instead of the
        // CLR killing the process first. Issue #35: a single bad SDK call must not silently take
        // the worker down mid-request.
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions, System.Security.SecurityCritical]
        // Bug #4: compact "method/action" label for the in-flight SDK command.
        private static string DescribeCommand(string line)
        {
            try
            {
                var o = JObject.Parse(line);
                string m = o["method"]?.ToString();
                string a = o["action"]?.ToString() ?? o["params"]?["action"]?.ToString();
                return string.IsNullOrEmpty(a) ? (m ?? "?") : (m + "/" + a);
            }
            catch { return "?"; }
        }

        internal static string ExtractOperationId(string line)
        {
            try { return JObject.Parse(line)["_meta"]?["progressToken"]?.ToString(); }
            catch { return null; }
        }

        // Bug #4: when the SdkWorker STA thread has been busy with another command longer than
        // the reject threshold, answer this non-thread-safe command immediately with a clear
        // WorkerBusy envelope instead of silently queuing it behind the long op (which makes the
        // gateway time out with a misleading "Gateway timeout starting build"). Control/recovery
        // commands are never rejected so a wedged worker stays recoverable. Returns true when it
        // handled (answered) the command.
        private static bool TryRejectBusy(string line)
        {
            if (_sdkBusy != 1) return false;

            long sinceTicks = Interlocked.Read(ref _sdkBusySinceTicks);
            double ageMs = (DateTime.UtcNow - new DateTime(sinceTicks, DateTimeKind.Utc)).TotalMilliseconds;
            if (ageMs < _busyRejectThresholdMs) return false;

            string idJson = "null";
            string method = null, action = null;
            try
            {
                var o = JObject.Parse(line);
                idJson = o["id"]?.ToString() ?? "null";
                method = o["method"]?.ToString()?.ToLowerInvariant();
                action = (o["action"]?.ToString() ?? o["params"]?["action"]?.ToString())?.ToLowerInvariant();
            }
            catch { return false; } // unparseable → let the normal path handle it

            // Never reject control / recovery / health ops.
            if (method == "control" || method == "ping" || method == "health"
                || (method != null && method.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0)
                || action == "cancel" || action == "reload")
                return false;

            string busy = GxMcp.Worker.Models.McpResponse.Err(
                code: "WorkerBusy",
                message: "The worker is executing a long-running SDK operation (" + (_sdkBusyOp ?? "unknown")
                       + ", running " + Math.Round(ageMs / 1000.0, 1) + "s) and runs SDK commands one at a time, so your "
                       + "request was not queued behind it. Retry when it finishes, poll genexus_lifecycle action=status, "
                       + "or cancel the running op with genexus_lifecycle action=cancel.",
                hint: "The GeneXus model is single-threaded — only one SDK operation runs at a time. Tune the reject window with GXMCP_BUSY_REJECT_MS (ms; 0 disables).",
                retryAfterMs: Math.Max(1000, _busyRejectThresholdMs),
                errorExtra: new JObject
                {
                    ["blockingOperationId"] = _sdkBusyOperationId,
                    ["blockingOperation"] = _sdkBusyOp,
                    ["elapsedMs"] = Math.Max(0L, (long)ageMs)
                });
            SendResponse(busy, idJson);
            Logger.Warn("[BUSY-REJECT] " + DescribeCommand(line) + " id=" + idJson
                + " — SDK thread busy with " + (_sdkBusyOp ?? "?") + " for " + Math.Round(ageMs) + "ms");
            return true;
        }

        private static void ProcessCommand(string line)
        {
            JObject obj;
            try { obj = JObject.Parse(line); }
            catch (Exception ex)
            {
                // Compatibilidade: o parse legado acontecia dentro do try do corpo; uma
                // linha malformada respondia WorkerInternalError com id "null". Preservar
                // envelope e mensagem exatamente como antes.
                Logger.Error("ProcessCommand Error: " + ex.Message);
                SendWorkerErrorEnvelope("WorkerInternalError", "Unhandled worker error: " + ex.Message, "null");
                return;
            }
            ProcessCommand(obj, line);
        }

        // P3 perf: parse tolerante usado pelo loop principal. null = JSON malformado —
        // IsThreadSafe(JObject null) devolve false (NRE capturada), mesma resposta do
        // overload por string, e a linha segue para TryRejectBusy/fila STA como antes.
        private static JObject TryParseCommand(string line)
        {
            try { return JObject.Parse(line); }
            catch { return null; }
        }

        private static void SendWorkerErrorEnvelope(string code, string message, string idJson)
        {
            try {
                string errResult = GxMcp.Worker.Models.McpResponse.Err(code: code, message: message);
                SendResponse(errResult, idJson);
            } catch (Exception sendEx) {
                Logger.Error("ProcessCommand failed to send error response: " + sendEx.Message);
            }
        }

        // P3 perf: overload que recebe o comando já parseado pelo loop principal —
        // usado apenas no caminho MTA (Task.Run); a fila STA continua por string.
        private static void ProcessCommand(JObject obj, string line)
        {
            MarkWorkerActivity();
            string idJson = "null";
            try {
                idJson = obj["id"]?.ToString() ?? "null";
                string method = obj["method"]?.ToString();
                string correlationId = obj["params"]?["correlationId"]?.ToString() ?? "n/a";
                long queueWaitMs = ComputeQueueWaitMs(obj["_meta"]?["queuedAtUtc"], DateTime.UtcNow);
                Logger.Info($"[WORKER] Command: {method} ({idJson}) [cid:{correlationId}]");
                var dispatchSw = Stopwatch.StartNew();
                string result = _dispatcher.Dispatch(obj, line);
                dispatchSw.Stop();
                // Keep phase timings in the response metadata so the Gateway can
                // aggregate SDK cost separately from its own queue/transform cost.
                // Dispatch includes the native SDK call and is therefore the only
                // trustworthy SDK boundary available to the Worker without changing
                // every service signature.
                var telemetry = new JObject
                {
                    ["sdkMs"] = Math.Max(0L, dispatchSw.ElapsedMilliseconds),
                    ["queueWaitMs"] = queueWaitMs,
                    ["cacheOutcome"] = "unknown"
                };
                SendResponse(result, idJson, telemetry);
            } catch (Exception ex) when (GxMcp.Worker.Helpers.WorkerCrashGuard.IsCorruptedState(ex)) {
                // Native/corrupted-state SDK crash: the heap may be inconsistent, so answer THIS
                // call with a structured error and then exit — the gateway respawns a fresh worker
                // rather than us continuing to serve from a poisoned AppDomain. Keep the work here
                // minimal (log + one stdout line + schedule exit); don't touch the SDK again.
                string reason = GxMcp.Worker.Helpers.WorkerCrashGuard.CrashReason(ex);
                Logger.Error("[WORKER-CRASH] recovered corrupted-state exception reason=" + reason
                    + " exType=" + ex.GetType().FullName + " exMsg=" + ex.Message
                    + Environment.NewLine + "Stack: " + ex);
                try {
                    string errResult = GxMcp.Worker.Models.McpResponse.Err(
                        code: "WorkerNativeCrashRecovered",
                        message: "The GeneXus SDK raised a native fault (" + reason + ") on this call. "
                               + "The worker is restarting; retry the operation (consider a smaller/simpler edit).");
                    SendResponse(errResult, idJson);
                } catch (Exception sendEx) {
                    Logger.Error("[WORKER-CRASH] failed to send recovery response: " + sendEx.Message);
                }
                SchedulePoisonedExit();
                return;
            } catch (Exception ex) {
                Logger.Error("ProcessCommand Error: " + ex.Message);
                // Never leave a request unanswered: an exception escaping Dispatch (e.g.
                // from using-block disposal or the idempotency cache) previously only got
                // logged, so the gateway had to wait out its full timeout. Send an error
                // result envelope for the parsed id, matching the shape normal errors use.
                try {
                    string errResult = GxMcp.Worker.Models.McpResponse.Err(
                        code: "WorkerInternalError",
                        message: "Unhandled worker error: " + ex.Message);
                    SendResponse(errResult, idJson);
                } catch (Exception sendEx) {
                    Logger.Error("ProcessCommand failed to send error response: " + sendEx.Message);
                }
            }
        }

        private static int _poisonedExitScheduled;
        // Exit shortly after a recovered corrupted-state crash so the last stdout response flushes
        // to the gateway first; the nonzero code marks it unexpected so the gateway respawns fresh.
        private static void SchedulePoisonedExit()
        {
            if (System.Threading.Interlocked.Exchange(ref _poisonedExitScheduled, 1) != 0) return;
            ShuttingDown = true;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(500); } catch { }
                try { InstanceLock?.Dispose(); } catch { }
                Logger.Error("[WORKER-CRASH] exiting code=" + ExitCodePoisoned + " for gateway respawn after corrupted-state recovery");
                Environment.Exit(ExitCodePoisoned);
            });
        }

        private static void SendResponse(string result, string id, JObject telemetry = null)
        {
            try {
                var transformSw = Stopwatch.StartNew();
                object resultObj;
                try { resultObj = JToken.Parse(result); } catch { resultObj = result; }
                transformSw.Stop();

                JObject resultObject = resultObj as JObject;
                if (resultObject != null && telemetry != null)
                {
                    telemetry["transformMs"] = Math.Max(0L, transformSw.ElapsedMilliseconds);
                    telemetry["serializeMs"] = 0L;
                    telemetry["responseBytes"] = 0L;
                    JObject meta = resultObject["_meta"] as JObject;
                    if (meta == null)
                    {
                        meta = new JObject();
                        resultObject["_meta"] = meta;
                    }
                    meta["telemetry"] = telemetry;
                }

                var response = new { jsonrpc = "2.0", result = resultObj, id = id };
                var serializeSw = Stopwatch.StartNew();
                string serialized = JsonConvert.SerializeObject(response, Formatting.None);
                serializeSw.Stop();
                if (resultObject != null && telemetry != null)
                {
                    telemetry["serializeMs"] = Math.Max(0L, serializeSw.ElapsedMilliseconds);
                    telemetry["responseBytes"] = System.Text.Encoding.UTF8.GetByteCount(serialized);
                    // Include the measured values in the final wire payload. The
                    // second serialization is intentionally limited to instrumented
                    // responses and keeps the phase contract self-describing.
                    serialized = JsonConvert.SerializeObject(response, Formatting.None);
                }
                WriteLine(serialized);
            } catch (Exception ex) { Logger.Error("SendResponse Error: " + ex.Message); }
        }

        /// <summary>
        /// Computes the time a command spent in the Gateway/Worker queues from the
        /// transport-only enqueue timestamp. Invalid or future timestamps are treated as
        /// zero so telemetry can never turn into a request failure.
        /// </summary>
        internal static long ComputeQueueWaitMs(JToken queuedAtUtc, DateTime nowUtc)
        {
            if (queuedAtUtc == null || queuedAtUtc.Type == JTokenType.Null) return 0;
            if (!DateTimeOffset.TryParse(
                    queuedAtUtc.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var queued))
                return 0;

            double elapsed = (nowUtc.ToUniversalTime() - queued.UtcDateTime).TotalMilliseconds;
            if (elapsed <= 0) return 0;
            // A stale/replayed envelope must not produce unbounded bogus aggregates.
            return Math.Min((long)Math.Round(elapsed), 24L * 60L * 60L * 1000L);
        }

        public static void SendNotification(string method, object @params)
        {
            try {
                var notification = new { jsonrpc = "2.0", method = method, @params = @params };
                WriteLine(JsonConvert.SerializeObject(notification, Formatting.None));
            } catch (Exception ex) { Logger.Error("SendNotification Error: " + ex.Message); }
        }

        public static void WriteLine(string line) => _outputQueue.Add(line);
        public static void WriteError(string line) => _errorQueue.Add(line);

        // Friction 2026-05-22: on startup, kill MSBuild.exe instances whose parent
        // PID no longer exists. These accumulate when a build is cancelled or the
        // worker crashes mid-build (the /m worker nodes survive the parent kill on
        // net48 since Process.Kill(bool) doesn't exist). Opt-out:
        //   GXMCP_REAP_ORPHAN_MSBUILD=0
        private static void ReapOrphanMsbuilds()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("GXMCP_REAP_ORPHAN_MSBUILD"), "0",
                              StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int reaped = 0;
            try
            {
                // Snapshot every process ID once to test parent liveness in O(1).
                var allPids = new HashSet<int>();
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try { allPids.Add(p.Id); } finally { try { p.Dispose(); } catch { } }
                }

                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'MSBuild.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject mo in results)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            int parent = Convert.ToInt32(mo["ParentProcessId"]);
                            // R5/R8: scope by command-line so we only touch GeneXus KB
                            // builds — VS / IDE MSBuilds and unrelated /m worker nodes
                            // are left alone even if their parent is gone. The
                            // command-line filter is the primary scope; the parent-alive
                            // check below is defense-in-depth against the
                            // GetProcesses()/WMI snapshot race.
                            var cmdLine = mo["CommandLine"] as string;
                            if (string.IsNullOrEmpty(cmdLine)) continue;
                            bool matchesGxBuild =
                                cmdLine.IndexOf("LastBuild.sln", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cmdLine.IndexOf("\\Desenv\\build\\", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!matchesGxBuild) continue;
                            // Parent process either gone, or PID got recycled into a different
                            // process — in either case, a /m worker node has no one waiting on it.
                            if (!allPids.Contains(parent))
                            {
                                try
                                {
                                    using (var child = System.Diagnostics.Process.GetProcessById(pid))
                                    {
                                        if (!child.HasExited) { child.Kill(); reaped++; }
                                    }
                                }
                                catch { /* race: already exited */ }
                            }
                        }
                        catch { }
                        finally { try { mo?.Dispose(); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[ORPHAN-REAP] WMI scan failed: " + ex.Message);
                return;
            }

            if (reaped > 0)
                Logger.Info("[ORPHAN-REAP] killed " + reaped + " orphan MSBuild.exe process(es) at startup");
        }

        private static void StartOutputThreads()
        {
            _originalOut = Console.Out;
            _originalError = Console.Error;

            var outThread = new Thread(() => {
                foreach (var line in _outputQueue.GetConsumingEnumerable()) {
                    try { 
                        if (_pipeWriter != null) {
                            _pipeWriter.WriteLine(line);
                            _pipeWriter.Flush();
                        } else {
                            _originalOut.WriteLine(line); 
                            _originalOut.Flush(); 
                        }
                    } catch { }
                }
            }) { IsBackground = true, Name = "OutputWriter" };
            outThread.Start();

            var errThread = new Thread(() => {
                foreach (var line in _errorQueue.GetConsumingEnumerable()) {
                    try { _originalError.WriteLine(line); _originalError.Flush(); } catch { }
                }
            }) { IsBackground = true, Name = "ErrorWriter" };
            errThread.Start();
        }

        private static JObject LoadLocalConfig()
        {
            try {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string configPath = Path.Combine(exeDir, "config.json");
                if (File.Exists(configPath)) return JObject.Parse(File.ReadAllText(configPath));
            } catch { }
            return null;
        }
    }

    // PERFORMANCE (W-A5): the previous implementation took a lock per-character on every
    // Console.Write call. Every IPC line — JSON-RPC responses that can be tens of KB — paid
    // that cost. Here Write(string) and WriteLine(string) acquire the lock once per call and
    // handle the '\n'/'\r' split internally. Write(char) is preserved for completeness but no
    // longer the hot path.
    public class QueueWriter : TextWriter
    {
        private readonly BlockingCollection<string> _queue;
        private readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();

        public QueueWriter(BlockingCollection<string> queue) { _queue = queue; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\r') return;
            lock (_buffer)
            {
                if (value == '\n')
                {
                    _queue.Add(_buffer.ToString());
                    _buffer.Clear();
                }
                else
                {
                    _buffer.Append(value);
                }
            }
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_buffer)
            {
                AppendInternal(value);
            }
        }

        public override void WriteLine(string value)
        {
            lock (_buffer)
            {
                if (!string.IsNullOrEmpty(value)) AppendInternal(value);
                _queue.Add(_buffer.ToString());
                _buffer.Clear();
            }
        }

        // Caller MUST hold _buffer lock.
        private void AppendInternal(string value)
        {
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r') continue;
                if (c == '\n')
                {
                    if (i > start) _buffer.Append(value, start, i - start);
                    _queue.Add(_buffer.ToString());
                    _buffer.Clear();
                    start = i + 1;
                }
            }
            if (start < value.Length)
            {
                // Append the trailing partial line (no terminator yet) in one shot,
                // skipping any stray '\r' characters between start and end.
                for (int j = start; j < value.Length; j++)
                {
                    char c = value[j];
                    if (c != '\r') _buffer.Append(c);
                }
            }
        }
    }
}
