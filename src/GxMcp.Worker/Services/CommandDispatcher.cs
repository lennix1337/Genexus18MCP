using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class CommandDispatcher
    {
        private static CommandDispatcher _instance;
        private static readonly object _lock = new object();

        private readonly CommandHandlerRegistry _registry;
        private readonly MutationEngine _mutationEngine;
        private readonly ChangeSetService _changeSetService;
        private readonly CompilationPipeline _compilationPipeline;
        private readonly ObjectInspectionModule _objectInspectionModule;
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;
        private readonly BuildService _buildService;
        private readonly WriteService _writeService;
        private readonly UIService _uiService;
        private readonly AnalyzeService _analyzeService;
        private readonly RefactorService _refactorService;
        private readonly BatchService _batchService;
        private readonly ForgeService _forgeService;
        private readonly VariableService _variableService;
        private readonly ValidationService _validationService;
        private readonly TestService _testService;
        private readonly SearchService _searchService;
        private readonly SourceSearchService _sourceSearchService;
        private readonly WikiService _wikiService;
        private readonly HistoryService _historyService;
        private readonly VisualizerService _visualizerService;
        private readonly HealthService _healthService;
        private readonly NavigationService _navigationService;
        private readonly NavigationSqlService _navigationSqlService;
        // Wave-3 items 42 + 92: sample-data generator + translations CSV importer.
        private readonly SampleDataService _sampleDataService;
        // Typed application-row access derived from GeneXus Transaction metadata.
        private readonly TransactionRecordsService _transactionRecordsService;
        private readonly TranslationsService _translationsService;
        private readonly LinterService _linterService;
        private readonly PatternService _patternService;
        private readonly PatternApplyService _patternApplyService;
        private readonly PatchService _patchService;
        private readonly SDTService _sdtService;
        private readonly StructureService _structureService;
        private readonly Structure.AuthoringService _authoringService;
        private readonly FormatService _formatService;
        private readonly PropertyService _propertyService;
        // Issue #62 — atomic create/update (variables + rules + parms + properties + source in one validated op).
        private readonly AtomicCreateService _atomicCreateService;
        private readonly DataViewService _dataViewService;
        private readonly GeneratorReferenceService _generatorReferenceService;
        private readonly AssetService _assetService;
        private readonly VersionControlService _versionControlService;
        private readonly ConversionService _conversionService;
        private readonly SelfTestService _selfTestService;
        private readonly PatternAnalysisService _patternAnalysisService;
        private readonly WwpActionService _wwpActionService;
        private readonly AtomicAuthoringService _atomicAuthoringService;
        private readonly DataInsightService _dataInsightService;
        private readonly DatabaseInfoService _databaseInfoService;
        private readonly SummarizeService _summarizeService;
        private readonly InjectionService _injectionService;
        private readonly ListService _listService;
        private readonly LayoutService _layoutService;
        private readonly KbValidationService _kbValidationService;
        private readonly ValidatePayloadService _validatePayloadService;
        private readonly ExportObjectService _exportObjectService;
        private readonly DiffService _diffService;
        private readonly ApplyTemplateService _applyTemplateService;
        private readonly EditAndBuildOrchestrator _editAndBuildOrchestrator;
        private readonly PreviewService _previewService;
        private readonly PopupTemplateService _popupTemplateService;
        private readonly UndoService _undoService;
        private readonly SecurityAuditService _securityAuditService;
        private readonly OrientService _orientService;
        private readonly DbDriftService _dbDriftService;
        // SOTA tool — static "missing index" advisor for the KB.
        private readonly DbOptimizeService _dbOptimizeService;
        private readonly WebFormEditService _webFormEditService;
        private readonly RunObjectService _runObjectService;
        private readonly ExplainService _explainService;
        private readonly GeneratedDiffService _generatedDiffService;
        private readonly KbReadmeService _kbReadmeService;
        private readonly SaveAsService _saveAsService;
        private readonly OcrScreenshotService _ocrScreenshotService;
        private readonly PrDescriptionService _prDescriptionService;
        private readonly ScreenshotPublishService _screenshotPublishService;
        private readonly FrictionLogService _frictionLogService;
        private readonly MemoryService _memoryService;
        private readonly WcagCheckService _wcagCheckService;
        // Wave-3 (items 76 / 78 / 84 / 86): friction-report aggregation, SDPanel proxy,
        // multi-agent file lock, what-if typed-change simulator.
        private readonly LearningReportService _learningReportService;
        // genexus_gxserver — read-only sync-state surface.
        private readonly GxServerSyncService _gxServerSyncService;
        // genexus_compare — read-only IComparerService surface ("Compare Objects" parity).
        private readonly CompareService _compareService;
        // genexus_module — GeneXus Module Manager (install/update) over IModuleManagerService.
        private readonly ModuleService _moduleService;
        // genexus_gam — GAM / integrated-security provisioning over IIntegratedSecurityService.
        private readonly GamService _gamService;
        // genexus_merge — WRITE IMergeService surface (2-way/3-way object merge).
        private readonly MergeToolService _mergeToolService;
        // genexus_kb_version — KB model-version management over KBVersionHelper.
        private readonly KbVersionService _kbVersionService;
        // genexus_transfer — real XPZ export/import over IKnowledgeManagerService.
        private readonly TransferService _transferService;
        // genexus_deploy — deploy application over IDeploymentService/IDeploymentTargetService.
        private readonly DeployService _deployService;
        // genexus_db action=reorg_impact — reorg/DDL impact over IModelInformationService/ISpecifierService.
        private readonly ReorgImpactService _reorgImpactService;
        // genexus_analyze mode=kb_stats — KB activity/freshness over IModelInformationService/IStatisticsService.
        private readonly KbStatsService _kbStatsService;
        // genexus_security action=scan_native — native Security Scanner over ISecurityScannerService.
        private readonly SecurityScanService _securityScanService;
        // genexus_gxserver action=pipeline_* — CI pipelines over IContinuousIntegrationService.
        private readonly CiPipelineService _ciPipelineService;
        // genexus_analyze mode=table_relations — table↔transaction relations over ITablesService.
        private readonly TableRelationsService _tableRelationsService;
        // genexus_layout action=list_controls — control/theme catalog over IUserControlsManagerService.
        private readonly UserControlsListService _userControlsListService;
        // genexus_create action=curl_procedure — curl→Procedure over ICurlGeneratorService.
        private readonly CurlProcService _curlProcService;
        // genexus_layout action=design_system — DSO tokens/classes/images over DesignSystemHelper.
        private readonly DesignSystemService _designSystemService;
        private readonly SdPanelService _sdPanelService;
        private readonly MultiAgentLockService _multiAgentLockService;
        private readonly WhatIfService _whatIfService;
        private readonly TutorialService _tutorialService;
        private readonly PlaybookService _playbookService;
        private readonly GithubService _githubService;
        private readonly AiCompleteService _aiCompleteService;
        private readonly TimeTravelService _timeTravelService;
        private readonly VoiceIntentService _voiceIntentService;
        private readonly AutoTestService _autoTestService;
        private readonly ReversePatternService _reversePatternService;
        private readonly CrossBrowserService _crossBrowserService;
        private readonly BlameService _blameService;
        private readonly KbExplorerService _kbExplorerService;
        private readonly NavigationViewService _navigationViewService;
        private readonly KbStartupService _kbStartupService;
        // Wave-3 browser-verify pipeline. Shared invoker → 3 services.
        private readonly IBrowserDriverInvoker _browserDriverInvoker;
        private readonly BrowserCaptureService _browserCaptureService;
        private readonly SmokeTestService _smokeTestService;
        private readonly A11yAuditService _a11yAuditService;
        // Wave-3 items 5 + 37: post-edit visual verification (screenshot + pixel-diff).
        // Lazy because it shells out to chrome-devtools-axi / playwright and is only
        // engaged when callers opt in via `visualVerify=true`.
        private readonly VisualVerifyService _visualVerifyService;
        // Wave-3 item 30: per-target build-plan with per-tool p95-derived estimates.
        private readonly BuildPlanService _buildPlanService;
        // genexus_doctor: one-call health/triage envelope.
        private readonly DoctorService _doctorService;
        // genexus_types: Domain + SDT type introspection + value validation.
        private readonly TypeIntrospectService _typeIntrospectService;
        // genexus_api: REST endpoint introspection + breaking-change diff.
        private readonly ApiIntrospectService _apiIntrospectService;
        // genexus_profile: runtime profiler XML bridge (file-only ingest v1).
        private readonly ProfileService _profileService;
        private readonly SdkProbeService _sdkProbeService;
        // issue #60 — save+specify in one call. Runs the inline Specify pass after a
        // successful write when validationMode="specify", surfacing structured diagnostics
        // and (optionally) rolling back on failure.
        private readonly SaveSpecifyOrchestrator _saveSpecifyOrchestrator;

        private readonly Dictionary<string, CommandHandler> _commandTable;

        private CommandDispatcher()
        {
            // Phase 1: Creation
            _indexCacheService = new IndexCacheService();
            _buildService = new BuildService();
            _kbService = new KbService(_indexCacheService);
            _visualizerService = new VisualizerService();
            _healthService = new HealthService();
            _formatService = new FormatService();
            _objectService = new ObjectService(_kbService, _buildService);
            _assetService = new AssetService(_buildService);
            _navigationService = new NavigationService(_kbService);
            _navigationSqlService = new NavigationSqlService(_navigationService, _kbService, _objectService);
            _sampleDataService = new SampleDataService(_objectService);
            _transactionRecordsService = new TransactionRecordsService(_kbService, _objectService);
            _translationsService = new TranslationsService(null); // writeService linked in Phase 2
            _listService = new ListService(_kbService, _indexCacheService);
            _uiService = new UIService(_kbService, _objectService);
            var callerGraphService = new CallerGraphService(_indexCacheService, _objectService);
            _analyzeService = new AnalyzeService(_kbService, _objectService, _indexCacheService, _navigationService, _uiService, callerGraphService);
            _buildService.SetCallerGraphService(callerGraphService);
            _summarizeService = new SummarizeService(_kbService, _objectService);
            _injectionService = new InjectionService(_kbService, _objectService, _analyzeService);
            _patternAnalysisService = new PatternAnalysisService(_objectService);
            _layoutService = new LayoutService(_objectService);
            _validationService = new ValidationService(_kbService);
            _searchService = new SearchService(_indexCacheService, _objectService);
            _sourceSearchService = new SourceSearchService(_indexCacheService, _objectService);
            _versionControlService = new VersionControlService(_kbService);
            _dataInsightService = new DataInsightService(_kbService, _objectService, _navigationService, _patternAnalysisService);
            _databaseInfoService = new DatabaseInfoService(_kbService);
            _writeService = new WriteService(_objectService);
            _variableService = new VariableService(_objectService, _writeService);
            _wwpActionService = new WwpActionService(_objectService, _patternAnalysisService, _writeService);
            _refactorService = new RefactorService(_kbService, _objectService, _indexCacheService, _writeService, _patternAnalysisService);
            _patchService = new PatchService(_objectService, _writeService, _patternAnalysisService);
            _batchService = new BatchService(_kbService, _writeService, _patchService, _objectService);
            _forgeService = new ForgeService(_kbService);
            _testService = new TestService(_kbService, _buildService);
            _wikiService = new WikiService(_objectService, _searchService);
            _historyService = new HistoryService(_objectService, _writeService);
            _saveSpecifyOrchestrator = new SaveSpecifyOrchestrator(_buildService, _historyService);
            _linterService = new LinterService(_objectService, _navigationService);
            _patternService = new PatternService(_indexCacheService, _objectService);
            _patternApplyService = new PatternApplyService(_objectService);
            _sdtService = new SDTService(_objectService);
            _structureService = new StructureService(_objectService);
            _writeService.SetStructureService(_structureService);
            _authoringService = new Structure.AuthoringService(_objectService);
            _propertyService = new PropertyService(_objectService);
            _atomicAuthoringService = new AtomicAuthoringService(_objectService, _writeService, _propertyService, _buildService);
            _atomicCreateService = new AtomicCreateService(_objectService, _writeService, _propertyService, _saveSpecifyOrchestrator, _historyService);
            _dataViewService = new DataViewService(_kbService, _objectService);
            _generatorReferenceService = new GeneratorReferenceService(_kbService);
            _conversionService = new ConversionService(_objectService);
            _selfTestService = new SelfTestService(_kbService, _searchService, _linterService);
            _kbValidationService = new KbValidationService(_indexCacheService, _objectService, _patternAnalysisService);
            _validatePayloadService = new ValidatePayloadService(_objectService);
            _exportObjectService = new ExportObjectService(_objectService);
            _diffService = new DiffService(_objectService);
            _applyTemplateService = new ApplyTemplateService(_writeService);
            _editAndBuildOrchestrator = new EditAndBuildOrchestrator(_writeService, _analyzeService, _buildService);
            _previewService = new PreviewService(_objectService, _buildService);
            _popupTemplateService = new PopupTemplateService(_objectService, _writeService);
            _undoService = new UndoService(_objectService, _writeService, _indexCacheService);
            _securityAuditService = new SecurityAuditService(_kbService);
            _orientService = new OrientService(_kbService);
            _dbDriftService = new DbDriftService(_buildService);
            _dbOptimizeService = new DbOptimizeService(_kbService, _objectService, _indexCacheService);
            _webFormEditService = new WebFormEditService(_objectService, _writeService);
            _runObjectService = new RunObjectService(_objectService, _kbService, _previewService);
            _explainService = new ExplainService(_kbService, _objectService);
            _generatedDiffService = new GeneratedDiffService(_kbService);
            _kbReadmeService = new KbReadmeService(_kbService, _indexCacheService);
            _saveAsService = new SaveAsService(new SdkObjectCloner(_objectService, _writeService, _patternApplyService));
            _ocrScreenshotService = new OcrScreenshotService();
            _prDescriptionService = new PrDescriptionService(_kbService);
            _screenshotPublishService = new ScreenshotPublishService(_kbService);
            _frictionLogService = new FrictionLogService(_kbService);
            _memoryService = new MemoryService(_kbService);
            _wcagCheckService = new WcagCheckService(_objectService);
            _learningReportService = new LearningReportService(_kbService);
            _gxServerSyncService = new GxServerSyncService(_kbService);
            _compareService = new CompareService(_kbService, _objectService);
            _moduleService = new ModuleService(_kbService, _objectService);
            _gamService = new GamService(_kbService);
            _mergeToolService = new MergeToolService(_kbService, _objectService);
            _kbVersionService = new KbVersionService(_kbService);
            _transferService = new TransferService(_kbService, _objectService, _indexCacheService, _writeService);
            _deployService = new DeployService(_kbService);
            _reorgImpactService = new ReorgImpactService(_kbService, _objectService);
            // B15: give drift_check the authoritative reorg-needed signal.
            _dbDriftService.SetReorgImpact(_reorgImpactService);
            _kbStatsService = new KbStatsService(_kbService);
            _securityScanService = new SecurityScanService(_kbService);
            _ciPipelineService = new CiPipelineService(_kbService);
            _tableRelationsService = new TableRelationsService(_kbService, _objectService);
            _userControlsListService = new UserControlsListService(_kbService);
            _curlProcService = new CurlProcService(_kbService, _objectService);
            _designSystemService = new DesignSystemService(_kbService, _objectService);
            _sdPanelService = new SdPanelService(_objectService, _writeService);
            _multiAgentLockService = new MultiAgentLockService(_kbService);
            _whatIfService = new WhatIfService(_analyzeService, _objectService);
            _tutorialService = new TutorialService();
            _playbookService = new PlaybookService();
            _githubService = new GithubService(_kbService);
            _aiCompleteService = new AiCompleteService();
            _timeTravelService = new TimeTravelService(_kbService, _objectService);
            _voiceIntentService = new VoiceIntentService();
            _autoTestService = new AutoTestService();
            _reversePatternService = new ReversePatternService(_objectService, _uiService);
            _crossBrowserService = new CrossBrowserService(_runObjectService);
            _blameService = new BlameService(_kbService, _objectService);
            _kbExplorerService = new KbExplorerService(_objectService, _indexCacheService);
            _navigationViewService = new NavigationViewService(_navigationService, _kbService);
            _kbStartupService = new KbStartupService(_kbService, _objectService);
            // Wave-3 browser-verify pipeline wiring.
            _browserDriverInvoker = new DefaultBrowserDriverInvoker();
            _browserCaptureService = new BrowserCaptureService(_objectService, _browserDriverInvoker);
            _smokeTestService = new SmokeTestService(_browserCaptureService);
            _a11yAuditService = new A11yAuditService(_browserDriverInvoker);
            _visualVerifyService = new VisualVerifyService(_kbService, _objectService);
            _buildPlanService = new BuildPlanService(_indexCacheService, _objectService, callerGraphService);
            _doctorService = new DoctorService(_kbService, _indexCacheService, null);
            _apiIntrospectService = new ApiIntrospectService(_kbService, _objectService, _indexCacheService);
            _typeIntrospectService = new TypeIntrospectService(_kbService, _objectService);
            _profileService = new ProfileService();
            _sdkProbeService = new SdkProbeService();

            // Phase 2: Late Linking
            _kbService.SetBuildService(_buildService);
            _buildService.SetKbService(_kbService);
            _buildService.SetIndexCacheService(_indexCacheService);
            _indexCacheService.SetBuildService(_buildService);
            _validationService.SetObjectService(_objectService);
            _writeService.SetValidationService(_validationService);
            _objectService.SetWriteService(_writeService);
            _objectService.SetDataInsightService(_dataInsightService);
            _objectService.SetUIService(_uiService);
            _objectService.SetPatternAnalysisService(_patternAnalysisService);
            _linterService.SetWriteService(_writeService);

            _mutationEngine = new MutationEngine(_writeService, _patchService, _objectService);
            _changeSetService = new ChangeSetService(_mutationEngine);
            _compilationPipeline = new CompilationPipeline(_buildService, _kbService);
            _objectInspectionModule = new ObjectInspectionModule(_objectService, _analyzeService, _kbService, _batchService);
            _commandTable = BuildCommandTable();
            _registry = BuildCommandHandlerRegistry();
        }

        private CommandHandlerRegistry BuildCommandHandlerRegistry()
        {
            var registry = new CommandHandlerRegistry();

            // Direct canonical tool registrations
            registry.RegisterTool("genexus_read", ctx => _objectInspectionModule.Inspect(ctx.Target ?? ctx.ArgStr("name"), InspectionDepth.Source, ctx.Args));
            registry.RegisterTool("genexus_edit", ctx => _mutationEngine.Mutate(ctx.ArgStr("mode", "xml"), ctx.Target ?? ctx.ArgStr("name"), ctx.Args, ctx.Payload ?? ctx.ArgStr("content")));
            registry.RegisterTool("genexus_tutorial", ctx => _tutorialService.GetStep(ctx.ArgInt("step") ?? 1));
            registry.RegisterTool("genexus_orient", ctx => _orientService.Welcome());
            registry.RegisterTool("genexus_doctor", ctx => _doctorService.Diagnose());
            registry.RegisterTool("genexus_what_if", ctx => _whatIfService.Simulate(ctx.Args?["change"] as JObject));
            registry.RegisterTool("genexus_reverse_pattern", ctx => _reversePatternService.Infer(ctx.Args?["source"] as JArray));
            registry.RegisterTool("genexus_time_travel", ctx => _timeTravelService.Recover(ctx.Target ?? ctx.ArgStr("name"), ctx.ArgStr("at")));
            registry.RegisterTool("genexus_ai_complete", ctx => _aiCompleteService.Complete(ctx.ArgStr("name"), ctx.ArgStr("part"), ctx.ArgStr("context"), ctx.ArgInt("maxTokens") ?? 200).ToString(Newtonsoft.Json.Formatting.None));
            registry.RegisterTool("genexus_voice", ctx => _voiceIntentService.Map(ctx.ArgStr("transcript")).ToString(Newtonsoft.Json.Formatting.None));
            registry.RegisterTool("genexus_auto_test", ctx => _autoTestService.Generate(ctx.ArgStr("path")));
            registry.RegisterTool("genexus_multi_agent_lock", ctx => Handle_MultiAgentLock(ctx.Request, ctx.Method, ctx.Action, ctx.Target, ctx.Payload, ctx.Args));
            registry.RegisterTool("genexus_sd_panel", ctx => Handle_SdPanel(ctx.Request, ctx.Method, ctx.Action, ctx.Target, ctx.Payload, ctx.Args));
            registry.RegisterTool("genexus_data_view", ctx => Handle_DataView(ctx.Request, ctx.Method, ctx.Action, ctx.Target, ctx.Payload, ctx.Args));
            registry.RegisterTool("genexus_generator_reference", ctx => Handle_GeneratorReference(ctx.Request, ctx.Method, ctx.Action, ctx.Target, ctx.Payload, ctx.Args));
            registry.RegisterTool("genexus_wwp", ctx => Handle_WwpAction(ctx.Request, ctx.Method, ctx.Action, ctx.Target, ctx.Payload, ctx.Args));

            return registry;
        }

        public static CommandDispatcher Instance
        {
            get { lock (_lock) { return _instance ?? (_instance = new CommandDispatcher()); } }
        }

        public KbService GetKbService() { return _kbService; }
        public IndexCacheService GetIndexCacheService() { return _indexCacheService; }

        // Item 51 (Tier-S, EXPERIMENTAL) — capture IndexCacheService state to disk
        // before a warm reload. Returns a small JObject result the dispatcher
        // stitches into the soft-reload ack. Never throws — failures are surfaced
        // as { saved:false, error:... } so the soft reload still proceeds.
        private Newtonsoft.Json.Linq.JObject TryCaptureWarmSnapshot()
        {
            var result = new Newtonsoft.Json.Linq.JObject();
            try
            {
                string kbPath = _kbService?.GetKbPath();
                if (string.IsNullOrEmpty(kbPath))
                {
                    result["saved"] = false;
                    result["error"] = "no-kb-path";
                    return result;
                }
                string path = WarmIndexSnapshot.DefaultPath(kbPath);
                var index = _indexCacheService?.GetIndex();
                if (index == null)
                {
                    result["saved"] = false;
                    result["error"] = "index-not-initialised";
                    return result;
                }
                int objectCount = index.Objects?.Count ?? 0;
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(index.ToJson());
                WarmIndexSnapshot.Save(path, payload, kbPath, objectCount);
                result["saved"] = true;
                result["path"] = path;
                result["objectCount"] = objectCount;
                result["experimental"] = true;
                return result;
            }
            catch (Exception ex)
            {
                result["saved"] = false;
                result["error"] = ex.Message;
                return result;
            }
        }

        public bool IsThreadSafe(string line)
        {
            JObject request;
            try { request = JObject.Parse(line); }
            catch { return false; }
            return IsThreadSafe(request);
        }

        // P3 perf: overload que recebe o comando já parseado. O loop principal
        // (Program.cs) parseia o JSON uma única vez e propaga o JObject pelos
        // caminhos IsThreadSafe → Dispatch → DispatchInternal sem re-parsear.
        public bool IsThreadSafe(JObject request)
        {
            try
            {
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();

                if (string.IsNullOrEmpty(method)) return false;
                // ToLower() é culture-aware: em culturas Turkish-I um método contendo
                // "I" não bate com a whitelist e muda de thread de execução conforme
                // a cultura do SO.
                method = method.ToLowerInvariant();

                // Only allow strictly non-SDK or pure read-cache operations to bypass STA thread
                if (method == "ping" || method == "health")
                    return true;

                // issue #26 P2: 'search' is index-only (in-memory) EXCEPT SearchSource,
                // which reads object Source/WebForm XML via the GeneXus SDK and MUST run
                // on the STA thread. Routing SearchSource to the MTA thread-pool path
                // caused a native COM access-violation that crashed the worker 100% of the time.
                if (method == "search" && !string.Equals(action, "SearchSource", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (method == "list")
                    return true;

                // v2.3.8 (post-Task 7.2 fix): Control:Cancel must run on the parallel
                // dispatch path so it can interleave with an in-flight SDK call and
                // trip the registered CTS. Pure in-memory dictionary lookup, no SDK.
                if (method == "control" && action == "Cancel")
                    return true;

                // GetIndexStatus only reads static volatile fields – no SDK access
                if (method == "kb" && action == "GetIndexStatus")
                    return true;
                // v2.3.8 Task 1.2: GetIndexState reads in-memory IndexCacheService snapshot only – no SDK access
                if (method == "kb" && action == "GetIndexState")
                    return true;
                // Root-cause fix (Table-shadow auto-injection): GetNameTypeMap scans the
                // in-memory index only – no SDK access (see the action body below).
                if (method == "kb" && action == "GetNameTypeMap")
                    return true;

                // D21: health/status polls must never be starved behind an in-flight
                // long SDK op (build/index/edit) on the single STA queue.
                // - doctor: filesystem + loaded-assembly checks only (no KB/COM touch).
                // - build Status/Result: lock-guarded read of the in-memory _tasks
                //   dict (the very "is my build done?" poll that must not queue behind
                //   the build it's asking about).
                if (method == "doctor")
                    return true;
                if (method == "build" && (action == "Status" || action == "Result"))
                    return true;

                
                // Any operation interacting with GeneXus SDK (COM objects) MUST run in the STA thread to prevent corruption
                return false;
            }
            catch { return false; }
        }

        public string Dispatch(string line)
        {
            JObject req0;
            try { req0 = JObject.Parse(line); }
            catch { req0 = null; }

            if (req0 == null)
            {
                // Compatibilidade: com o parse falho, requestId/method0 seriam null ⇒
                // nunca cacheável — o fluxo legado seguia direto para DispatchInternal,
                // que parseava dentro do próprio try e convertia a exceção de parse em
                // envelope DispatcherException. Reproduzir exatamente esse comportamento.
                return DispatchInternal(line);
            }
            return Dispatch(req0, line);
        }

        // P3 perf: fluxo completo sem re-parsear. Idempotência e roteamento leem o
        // request já parseado; rawLine é mantido apenas para o caminho de fallback
        // (não é usado no corpo). Chamado pelo caminho MTA do Program.cs.
        public string Dispatch(JObject request, string rawLine)
        {
            // v2.8.0 — idempotency. When the caller threads a `clientRequestId`
            // through the RPC params, this dispatcher serves a cached response
            // for the same id within a 5-minute TTL. Lets LLM clients retry
            // safely after a socket drop / gateway timeout without double-
            // applying the underlying mutation. Excluded methods (ping, control)
            // skip the cache because they're meta operations.
            string requestId = request["params"]?["clientRequestId"]?.ToString()
                ?? (request["params"]?["params"] as JObject)?["clientRequestId"]?.ToString();
            string method0 = request["method"]?.ToString();

            bool isMeta = string.Equals(method0, "ping", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method0, "control", StringComparison.OrdinalIgnoreCase);
            bool cacheable = !string.IsNullOrEmpty(requestId) && !isMeta;
            if (cacheable)
            {
                // v2.8.0 (#37) — TryServe now waits for any in-flight call
                // with the same id, so a fast LLM retry within the original
                // call's window blocks on the first and gets the same result.
                string replay = GxMcp.Worker.Helpers.IdempotencyCache.TryServe(requestId);
                if (replay != null) return replay;
                // Mark this call as in-flight BEFORE executing so siblings
                // that race in mid-flight block instead of double-applying.
                GxMcp.Worker.Helpers.IdempotencyCache.BeginInflight(requestId);
            }

            string result;
            try
            {
                result = DispatchInternal(request);
            }
            catch
            {
                if (cacheable) GxMcp.Worker.Helpers.IdempotencyCache.AbortInflight(requestId);
                throw;
            }

            if (cacheable && !string.IsNullOrEmpty(result) && IsCacheableSuccessEnvelope(result))
            {
                GxMcp.Worker.Helpers.IdempotencyCache.Store(requestId, result);
            }
            else if (cacheable)
            {
                // A5: envelopes de erro (ou resultados não-parseáveis) NÃO são
                // cacheados — cachear erro faz um retry com o mesmo clientRequestId
                // reproduzir falhas transitórias (KB not open durante warm-up,
                // busy-reject) em vez de re-executar.
                GxMcp.Worker.Helpers.IdempotencyCache.AbortInflight(requestId);
            }
            return result;
        }

        // Statuses que indicam falha (ou estado não-conclusivo) e portanto nunca
        // devem virar replay de idempotência.
        private static readonly HashSet<string> NonCacheableStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Error", "NotFound", "NotImplemented", "WorkerBusy", "Busy", "IndexNotReady",
            "Reindexing", "IndexCold", "Timeout", "Cancelled", "Running"
        };

        // A5: retorna true somente para envelopes de sucesso cacheáveis — sem token
        // top-level "error" e sem "status" em {Error, NotFound, NotImplemented,
        // WorkerBusy, Busy, IndexNotReady, Reindexing, IndexCold, Timeout,
        // Cancelled, Running}. JSON inválido ou vazio retorna false.
        internal static bool IsCacheableSuccessEnvelope(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                using (var sr = new System.IO.StringReader(json))
                using (var reader = new Newtonsoft.Json.JsonTextReader(sr))
                {
                    if (!reader.Read() || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject)
                        return false;

                    int depth = 1;
                    while (reader.Read())
                    {
                        if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject || reader.TokenType == Newtonsoft.Json.JsonToken.StartArray)
                        {
                            depth++;
                        }
                        else if (reader.TokenType == Newtonsoft.Json.JsonToken.EndObject || reader.TokenType == Newtonsoft.Json.JsonToken.EndArray)
                        {
                            depth--;
                            if (depth == 0) break;
                        }
                        else if (depth == 1 && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                        {
                            string propName = reader.Value as string;
                            if (string.Equals(propName, "error", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!reader.Read()) return false;
                                if (reader.TokenType != Newtonsoft.Json.JsonToken.Null)
                                    return false;
                            }
                            else if (string.Equals(propName, "status", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!reader.Read()) return false;
                                string status = reader.Value as string;
                                if (status != null && NonCacheableStatuses.Contains(status))
                                    return false;
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                    }
                    return depth == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private string DispatchInternal(string line)
        {
            try
            {
                return DispatchInternal(JObject.Parse(line));
            }
            catch (Exception ex)
            {
                // Compatibilidade: o parse legado acontecia dentro do try abaixo; um
                // JSON malformado era convertido em envelope DispatcherException aqui.
                return Models.McpResponse.Err(
                    code: "DispatcherException",
                    message: ex.Message,
                    hint: "Inspect worker logs for the full exception chain; this is an unhandled dispatcher error.");
            }
        }

        // P3 perf: recebe o comando já parseado (caminho MTA) — sem re-parse.
        private string DispatchInternal(JObject request)
        {
            try
            {
                string method = request["method"]?.ToString();
                string action = request["action"]?.ToString();
                string target = request["target"]?.ToString();
                var payload = request["payload"]?.ToString();
                var args = (request["params"] as JObject) ?? request;
                // OperationsRouter cases that pass-through original tool args via
                // `@params = args` (apply_pattern, apply_template, bulk_edit, diff)
                // produce a doubly-nested params shape on the RPC envelope:
                //   rpc.params = { module, action, target, params: { <original tool args> } }
                // Action handlers below read tool args off `args` directly (e.g.
                // args["pattern"]), so unwrap once when the inner params object exists.
                // Inner keys win on collision; outer routing keys (module/action/target)
                // are preserved as top-level fallback.
                if (args != null && args["params"] is JObject innerArgs)
                {
                    var merged = new JObject();
                    foreach (var prop in innerArgs.Properties())
                    {
                        merged[prop.Name] = prop.Value;
                    }
                    foreach (var prop in args.Properties())
                    {
                        if (prop.Name == "params") continue;
                        if (merged[prop.Name] == null) merged[prop.Name] = prop.Value;
                    }
                    args = merged;
                }

                string progressToken = request["_meta"] != null
                    ? request["_meta"]["progressToken"]?.ToString()
                    : null;

                Logger.Info(string.Format("[DISPATCHER] Method: {0}, Action: {1}, Target: {2}", method, action, target));

                // v2.6.2 (Item B): blanket-register the cancel token so ANY handler that
                // observes WorkerCancellationRegistry sees a single registration for the whole
                // command. Inner handlers that also Register the same token share the CTS via
                // refcount and only the outermost scope removes the entry. Without this, async
                // builds/edits started by the gateway never registered their job_id and a
                // matching lifecycle action=cancel returned NotFound on the worker side.
                string commandCancelToken = args?["cancelToken"]?.ToString();
                using (GxMcp.Worker.Helpers.WorkerCancellationRegistry.Register(commandCancelToken, out _))
                using (GxMcp.Worker.Helpers.ProgressContext.Use(progressToken))
                {
                    string toolName = request["tool"]?.ToString()
                        ?? request["name"]?.ToString()
                        ?? (request["params"] as JObject)?["name"]?.ToString();

                    var ctx = new CommandContext(request, method, action, target, payload, args, toolName);
                    if (_registry != null && _registry.TryDispatch(ctx, out var regResult))
                    {
                        return regResult;
                    }

                    // Dispatch-table lookup (Plan 005): replaces the former switch(method) with
                    // ~83 cases. Each entry routes to the exact same handler with the exact same
                    // args as before; a null result means "no matching action for this method"
                    // (equivalent to the old per-case `break;`) and falls through to the shared
                    // UnknownMethodOrAction response below.
                    string methodKey = method ?? string.Empty;
                    if (_commandTable.TryGetValue(methodKey, out var handler))
                    {
                        string handlerResult = handler(request, method, action, target, payload, args);
                        if (handlerResult != null) return handlerResult;
                    }

                    return Models.McpResponse.Err(
                        code: "UnknownMethodOrAction",
                        message: string.Format("Unsupported dispatch combination. Method='{0}', Action='{1}'.", method ?? "", action ?? ""),
                        hint: "Call genexus_help action=route goal=<intent> for the right tool, or genexus_orient for an overview.",
                        nextSteps: new JArray(
                            Models.McpResponse.NextStep(
                                tool: "genexus_orient",
                                args: new JObject(),
                                why: "Shows the tool catalog so the right action can be chosen.")),
                        target: target);
                } // end using ProgressContext
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DispatcherException",
                    message: ex.Message,
                    hint: "Inspect worker logs for the full exception chain; this is an unhandled dispatcher error.");
            }
        }

        private delegate string CommandHandler(JObject request, string method, string action, string target, string payload, JObject args);

        private Dictionary<string, CommandHandler> BuildCommandTable()
        {
            return new Dictionary<string, CommandHandler>(StringComparer.OrdinalIgnoreCase)
            {
                ["ping"] = Handle_Ping,
                ["control"] = Handle_Control,
                ["kb"] = Handle_Kb,
                ["batch"] = Handle_Batch,
                ["search"] = Handle_Search,
                ["list"] = Handle_List,
                ["read"] = Handle_Read,
                ["object"] = Handle_Object,
                ["atomiccreate"] = Handle_AtomicCreate,
                ["dataview"] = Handle_DataView,
                ["generatorreference"] = Handle_GeneratorReference,
                ["write"] = Handle_Write,
                ["mutation"] = Handle_Mutation,
                ["editandbuild"] = Handle_EditAndBuild,
                ["semanticops"] = Handle_SemanticOps,
                ["jsonpatch"] = Handle_JsonPatch,
                ["patch"] = Handle_Patch,
                ["analyze"] = Handle_Analyze,
                ["buildplan"] = Handle_BuildPlan,
                ["future"] = Handle_Future,
                ["linter"] = Handle_Linter,
                ["diff"] = Handle_Diff,
                ["export"] = Handle_Export,
                ["forge"] = Handle_Forge,
                ["conversion"] = Handle_Conversion,
                ["pattern"] = Handle_Pattern,
                ["atomicauthoring"] = Handle_AtomicAuthoring,
                ["sdkprobe"] = Handle_SdkProbe,
                ["ui"] = Handle_Ui,
                ["layout"] = Handle_Layout,
                ["structure"] = Handle_Structure,
                ["authoring"] = Handle_Authoring,
                ["build"] = Handle_Build,
                ["validation"] = Handle_Validation,
                ["test"] = Handle_Test,
                ["wiki"] = Handle_Wiki,
                ["visualizer"] = Handle_Visualizer,
                ["health"] = Handle_Health,
                ["history"] = Handle_History,
                ["undo"] = Handle_Undo,
                ["security"] = Handle_Security,
                ["orient"] = Handle_Orient,
                ["property"] = Handle_Property,
                ["asset"] = Handle_Asset,
                ["formatting"] = Handle_Formatting,
                ["refactor"] = Handle_Refactor,
                ["popup"] = Handle_Popup,
                ["dbdrift"] = Handle_DbDrift,
                ["dboptimize"] = Handle_DbOptimize,
                ["webformedit"] = Handle_WebFormEdit,
                ["runobject"] = Handle_RunObject,
                ["explain"] = Handle_Explain,
                ["generateddiff"] = Handle_GeneratedDiff,
                ["kbreadme"] = Handle_KbReadme,
                ["ocr"] = Handle_Ocr,
                ["prdescription"] = Handle_PrDescription,
                ["screenshotpublish"] = Handle_ScreenshotPublish,
                ["frictionlog"] = Handle_FrictionLog,
                ["memory"] = Handle_Memory,
                ["wcagcheck"] = Handle_WcagCheck,
                ["learning"] = Handle_Learning,
                ["gxserver"] = Handle_GxServer,
                ["compare"] = Handle_Compare,
                ["module"] = Handle_Module,
                ["gam"] = Handle_Gam,
                ["merge"] = Handle_Merge,
                ["kbversion"] = Handle_KbVersion,
                ["transfer"] = Handle_Transfer,
                ["deploy"] = Handle_Deploy,
                ["reorgimpact"] = Handle_ReorgImpact,
                ["kbstats"] = Handle_KbStats,
                ["tablerelations"] = Handle_TableRelations,
                ["usercontrols"] = Handle_UserControls,
                ["wwpaction"] = Handle_WwpAction,
                ["curlproc"] = Handle_CurlProc,
                ["designsystem"] = Handle_DesignSystem,
                ["sdpanel"] = Handle_SdPanel,
                ["multiagentlock"] = Handle_MultiAgentLock,
                ["whatif"] = Handle_WhatIf,
                ["tutorial"] = Handle_Tutorial,
                ["playbook"] = Handle_Playbook,
                ["doctor"] = Handle_Doctor,
                ["api"] = Handle_Api,
                ["profile"] = Handle_Profile,
                ["types"] = Handle_Types,
                ["github"] = Handle_Github,
                ["aicomplete"] = Handle_AiComplete,
                ["timetravel"] = Handle_TimeTravel,
                ["voice"] = Handle_Voice,
                ["autotest"] = Handle_AutoTest,
                ["reversepattern"] = Handle_ReversePattern,
                ["crossbrowser"] = Handle_CrossBrowser,
                ["preview"] = Handle_Preview,
                ["kbexplorer"] = Handle_KbExplorer,
                ["navigation"] = Handle_Navigation,
                ["blame"] = Handle_Blame,
                ["browser_capture"] = Handle_BrowserCapture,
                ["smoke_test"] = Handle_SmokeTest,
                ["a11y_audit"] = Handle_A11yAudit,
            };
        }

        private string Handle_Ping(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return Models.McpResponse.Ok(code: "Pong", result: new JObject { ["message"] = "pong" });
        }

        private string Handle_Control(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // v2.3.8 (post-Task 7.2 fix): IPC cancellation side-channel.
            // Gateway sends this on the thread-safe path so it can interleave
            // with a sibling SDK call. Signals the registered CTS keyed by
            // cancelToken (typically the BackgroundJobRegistry job_id).
            if (string.Equals(action, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                string ct = args?["cancelToken"]?.ToString() ?? target;
                bool ok = GxMcp.Worker.Helpers.WorkerCancellationRegistry.Cancel(ct);
                // A7: RunBuild doesn't observe the CTS, so also reap any running
                // build's MSBuild process tree here — otherwise an aborted build
                // leaks orphaned MSBuild.exe /m nodes that accumulate across sessions
                // and contend for node-reuse. Best-effort; independent of the CTS trip.
                int buildsCancelled = 0;
                try { buildsCancelled = _buildService?.CancelAllRunning() ?? 0; }
                catch { /* best-effort */ }
                ok = ok || buildsCancelled > 0;
                // Item 11: wrap in canonical McpResponse envelope.
                return ok
                    ? Models.McpResponse.Ok(code: "Cancelled", result: new JObject
                        {
                            ["cancelToken"] = ct,
                            ["buildsCancelled"] = buildsCancelled,
                            ["message"] = "Cancellation signalled to in-flight command. The handler may still take a few iterations to terminate."
                        })
                    : Models.McpResponse.Err(code: "NotFound",
                        message: "No active command with that cancelToken (already finished or never started).",
                        hint: "The cancelToken may have already expired or the command completed.");
            }
            return null;
        }

        private string Handle_Kb(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Open")
            {
                string result = _kbService.OpenKB(target);
                try
                {
                    var openResult = JObject.Parse(result);
                    // v2.8.0 — KbService.OpenKB now emits the canonical envelope
                    // (status:"ok"). Recognize only the canonical shape; legacy
                    // emissions were removed in this release.
                    if (string.Equals(openResult["status"]?.ToString(), "ok", StringComparison.Ordinal))
                    {
                        Environment.SetEnvironmentVariable("GX_KB_PATH", target);
                    }
                }
                catch
                {
                }

                return result;
            }
            if (action == "BulkIndex")
            {
                bool force = args?["force"]?.ToObject<bool?>() ?? false;
                bool indexDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                if (indexDryRun)
                {
                    string kbPathForDry = null;
                    try { kbPathForDry = _kbService.GetKbPath(); } catch { }
                    return Models.McpResponse.Ok(
                        code: "DryRun",
                        result: new JObject
                        {
                            ["preview"] = new JObject
                            {
                                ["action"] = "index",
                                ["force"] = force,
                                ["kbPath"] = kbPathForDry,
                                ["note"] = "dryRun=true: index rebuild not executed."
                            }
                        });
                }
                return _kbService.BulkIndex(force);
            }
            if (action == "SelfTest") return _selfTestService.RunAllTests();
            if (action == "GetDatabaseInfo")
            {
                return _databaseInfoService.GetInfo();
            }
            // v2.6.6 Stream H (FR#26) — surface active-environment metadata
            // so the gateway can populate KbHandle.ActiveEnvironment on
            // cache miss. Pure SDK read, no side effects.
            if (action == "GetActiveEnvironment")
            {
                string env = _kbService.GetActiveEnvironment();
                string ver = _kbService.GetActiveEnvironmentVersion();
                return new JObject
                {
                    ["environment"] = env,
                    ["version"] = ver,
                    ["webPath"] = _kbService.GetActiveEnvironmentWebPath()
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            if (action == "ListEnvironments")
            {
                return _kbService.ListEnvironments();
            }
            if (action == "SetActiveEnvironment")
            {
                string environment = target ?? args?["environment"]?.ToString();
                return _kbService.SetActiveEnvironment(environment);
            }
            // v2.6.6 Stream H (FR#25) — F5 launcher resolver. Returns the KB's
            // configured startup object (or first IsMain-tagged WebPanel/SDPanel/Procedure).
            if (action == "GetLauncherObject")
            {
                string launcherObj = _kbService.GetLauncherObjectName();
                return new JObject { ["name"] = launcherObj }.ToString(Newtonsoft.Json.Formatting.None);
            }
            // Wave-3: IDE "Set As Startup Object" parity. Read goes
            // through the same SDK shapes as GetLauncherObjectName so
            // get/set agree on the field name the IDE writes to .gxw.
            if (action == "GetStartupObject") return _kbStartupService.GetStartup();
            if (action == "SetStartupObject")
            {
                string startupName = target ?? args?["name"]?.ToString();
                return _kbStartupService.SetStartup(startupName);
            }
            if (action == "GetIndexStatus")
            {
                // issue #25 #1: event-driven wait. When `wait` is given, block
                // until the index state transitions away from `since` (or a walk
                // progress tick lands, or the timeout fires) and return early —
                // no more polling loops. Runs on the non-SDK parallel path so
                // blocking here never stalls the STA thread.
                int waitSec = args?["wait"]?.ToObject<int?>() ?? 0;
                string since = args?["since"]?.ToString();
                if (waitSec > 0)
                {
                    // Issue #27 item 3 (DX): two block modes.
                    //  - since given  → return the moment the state LEAVES `since`
                    //    (event-driven progress poll; legacy behaviour).
                    //  - since absent → block until the index reaches "Ready"
                    //    (or timeout), so an agent can just say "wait until usable"
                    //    without hand-rolling a since-chained poll loop. A Cold+idle
                    //    index simply times out at its current state — the caller
                    //    then knows to trigger an index build.
                    bool waitForReady = string.IsNullOrEmpty(since);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    long budgetMs = waitSec * 1000L;
                    while (true)
                    {
                        _indexCacheService.ArmStateSignal();
                        string cur = _indexCacheService.GetState()?.Status ?? "Cold";
                        bool done = waitForReady
                            ? string.Equals(cur, "Ready", StringComparison.OrdinalIgnoreCase)
                            : !string.Equals(cur, since, StringComparison.OrdinalIgnoreCase);
                        if (done) break;
                        long remaining = budgetMs - sw.ElapsedMilliseconds;
                        if (remaining <= 0) break;
                        // Cap each wait so a missed signal still re-checks promptly.
                        _indexCacheService.WaitStateSignal((int)Math.Min(remaining, 2000));
                    }
                }
                // Merge the state-machine status so callers have a stable field
                // to pass back as `since` (the legacy `status` string is descriptive
                // prose, not a stable enum).
                var statusJson = Newtonsoft.Json.Linq.JObject.Parse(_kbService.GetIndexStatus());
                statusJson["indexStatus"] = _indexCacheService.GetState()?.Status ?? "Cold";
                // Issue #27 item 1: attach the most-recent terminal build outcome so
                // this plain status call answers "did my last build pass?" without a jobId.
                var lastBuild = BuildService.GetLatestBuildSummary();
                if (lastBuild != null) statusJson["lastBuild"] = lastBuild;
                // issue #42 (P2b) — surface in-flight builds so the client's isBusy
                // view is correct while a background build runs (a background build
                // does not hold the SDK-busy flag, so status alone looked idle).
                var activeBuilds = BuildService.GetActiveBuildsSummary();
                statusJson["activeBuilds"] = activeBuilds;
                statusJson["buildBusy"] = activeBuilds.Count > 0;
                // The STA single-flight tracker covers every SDK command, including Undo.
                // Merge it into the public status instead of reporting isBusy=false while
                // the worker is actively restoring snapshots.
                Program.MergeSdkBusyStatus(statusJson, Program.GetSdkBusyStatus());
                // issue #42 (P5) — objects edited via MCP but not yet successfully
                // built this session (their generated .cs is stale relative to the KB).
                try
                {
                    var dirty = EditDirtyTracker.GetDirty(_kbService.GetKbPath());
                    if (dirty != null && dirty.Count > 0)
                        statusJson["staleGenerated"] = new Newtonsoft.Json.Linq.JArray(dirty);
                }
                catch { }
                return statusJson.ToString();
            }
            if (action == "GetIndexState")
            {
                // v2.3.8 Task 1.2: surface unified IndexState from IndexCacheService.
                // Gateway uses this to populate the `index` block in whoami.
                //
                // issue #28 item 4: hydrate the on-disk cache BEFORE reading the
                // state. On a warm/reconnected worker _state starts "Cold" until
                // something calls GetIndex() (lazy disk load → MarkIndexComplete →
                // "Ready"). Because the gateway's SDK-bound short-circuit fast-fails
                // edits on a Cold mirror BEFORE they reach the worker, GetIndex()
                // never ran on the edit path and the state stayed Cold forever
                // ("Index loaded. Objects: 1191" in the log, yet edits blocked with
                // IndexNotReady). Triggering the lazy load here promotes the state to
                // reflect the loaded cache on the first whoami/refresh after reconnect.
                try { _indexCacheService.GetIndex(); } catch { /* load is best-effort; GetState still returns Cold/0 */ }
                var st = _indexCacheService.GetState();
                // v2.8.0 — index state shape is the tool's payload (not an envelope).
                // Renamed top-level "status" to "indexStatus" to avoid colliding with
                // the canonical envelope's "status" once wrapped in McpResponse.Ok.
                var j = new JObject
                {
                    ["indexStatus"] = st.Status ?? "Cold",
                    ["totalObjects"] = st.TotalObjects,
                    ["lastIndexedAt"] = st.LastIndexedAt.HasValue
                        ? (JToken)st.LastIndexedAt.Value.ToUniversalTime().ToString("o")
                        : JValue.CreateNull(),
                    ["progress"] = st.Progress.HasValue ? (JToken)st.Progress.Value : JValue.CreateNull(),
                    ["etaMs"] = st.EtaMs.HasValue ? (JToken)st.EtaMs.Value : JValue.CreateNull(),
                    // PERFORMANCE (W-M2): expose flush-failure telemetry so a silently
                    // failing snapshot (disk full / permission) is visible via whoami.
                    ["flushFailuresConsecutive"] = IndexCacheService.ConsecutiveFlushFailures,
                    ["flushLastSuccessUtc"] = IndexCacheService.LastFlushSuccessUtc == DateTime.MinValue
                        ? JValue.CreateNull()
                        : (JToken)IndexCacheService.LastFlushSuccessUtc.ToString("o"),
                    ["flushLastError"] = IndexCacheService.LastFlushErrorMessage != null
                        ? (JToken)IndexCacheService.LastFlushErrorMessage
                        : JValue.CreateNull()
                };

                // v2.6.8: top-5 recently-changed projection. Cheap O(n) scan
                // over the in-memory index; gateway forwards this into the
                // `whoami.index.recentlyChanged` block so the agent gets a
                // "what's hot" hint on the first call.
                try
                {
                    var idx = _indexCacheService.GetIndex();
                    if (idx != null && idx.Objects.Count > 0)
                    {
                        var top = idx.Objects.Values
                            .Where(e => e.LastUpdate > DateTime.MinValue)
                            .OrderByDescending(e => e.LastUpdate)
                            .Take(5)
                            .ToList();
                        if (top.Count > 0)
                        {
                            var arr = new JArray();
                            foreach (var e in top)
                            {
                                arr.Add(new JObject
                                {
                                    ["name"] = e.Name,
                                    ["type"] = e.Type,
                                    ["lastUpdate"] = e.LastUpdate.ToUniversalTime().ToString("o"),
                                    ["lastModifiedBy"] = e.LastModifiedBy ?? string.Empty
                                });
                            }
                            j["recentlyChanged"] = arr;
                        }
                    }
                }
                catch (Exception ex) { Logger.Debug("[GetIndexState] recentlyChanged failed: " + ex.Message); }

                return Models.McpResponse.Ok(code: "IndexState", result: j);
            }

            if (action == "GetNameTypeMap")
            {
                // Root-cause fix for the gateway's auto-type-injection Table shadow: the
                // gateway primed its name→type map from the top-5 RecentlyChanged window,
                // which cannot establish real uniqueness — a Transaction's physical Table
                // shadow can win that window without the sibling Transaction ever appearing
                // (the design model indexes BOTH under the same name). Expose the FULL
                // name→[distinct types] map from the in-memory index so the gateway can
                // resolve Transaction+Table → Transaction deterministically.
                // O(n) scan of the in-memory dictionary, no SDK access (STA-exempt).
                try { _indexCacheService.GetIndex(); } catch { /* best-effort: empty map below */ }
                var nameTypeMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var idx = _indexCacheService.GetIndex();
                    if (idx != null)
                    {
                        foreach (var e in idx.Objects.Values)
                        {
                            if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Type)) continue;
                            if (!nameTypeMap.TryGetValue(e.Name, out var types))
                                nameTypeMap[e.Name] = types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            types.Add(e.Type);
                        }
                    }
                }
                catch (Exception ex) { Logger.Debug("[GetNameTypeMap] scan failed: " + ex.Message); }

                var map = new JObject();
                foreach (var kv in nameTypeMap)
                {
                    var arr = new JArray();
                    foreach (var t in kv.Value) arr.Add(t);
                    map[kv.Key] = arr;
                }
                return Models.McpResponse.Ok(code: "NameTypeMap", result: new JObject
                {
                    ["nameTypeMap"] = map,
                    ["totalNames"] = nameTypeMap.Count
                });
            }
            if (action == "ValidateConditions") return _kbValidationService.ValidateConditions(args?["limit"]?.ToObject<int?>() ?? 0);
            if (action == "ListPatternSnapshots") return _kbValidationService.ListPatternSnapshots(target);
            if (action == "RestorePatternSnapshot") return _kbValidationService.RestorePatternSnapshot(target, args?["snapshotPath"]?.ToString(), _writeService);
            return null;
        }

        private string Handle_Batch(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "BatchRead") return _batchService.BatchRead(
                args?["items"] as JArray,
                args?["part"]?.ToString() ?? "Source",
                args?["parts"] as JArray);
            if (action == "BatchEdit") return _batchService.BatchEdit(target, args?["changes"] as JArray);
            if (action == "MultiEdit")
            {
                var items = args?["items"] as JArray;
                if (items == null || items.Count == 0)
                    return _batchService.MultiEdit(items);

                // `genexus_edit.targets[]` is the public multi-target contract.
                // Route it through the authoritative mutation engine so every
                // target receives the same preflight version fence, persistence
                // readback, and compensating rollback receipt as direct edits.
                // The legacy MultiEdit code path remains available for callers
                // that use the internal batch action with nested `changes`.
                var mutationArgs = new JObject
                {
                    ["targets"] = items,
                    ["dryRun"] = args?["dryRun"]?.ToObject<bool?>() ?? false,
                    ["rollbackOnFailure"] = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true
                };
                string mutationResponse = _mutationEngine.Mutate("xml", null, mutationArgs);
                try
                {
                    var envelope = JObject.Parse(mutationResponse);
                    if (string.Equals(envelope["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        envelope["code"] = "MultiEditCompleted";
                        if (envelope["result"] is JObject result)
                        {
                            result["objectCount"] = items
                                .OfType<JObject>()
                                .Select(item => item["name"]?.ToString() ?? item["target"]?.ToString())
                                .Where(name => !string.IsNullOrWhiteSpace(name))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count();
                            result["totalChanges"] = items.Count;
                        }
                    }
                    return envelope.ToString(Newtonsoft.Json.Formatting.None);
                }
                catch
                {
                    return mutationResponse;
                }
            }
            if (action == "Process") return _batchService.ProcessBatch(args?["batchAction"]?.ToString(), target, payload);
            return null;
        }

        private string Handle_Search(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Query")
            {
                DateTime sinceArgQ = default(DateTime);
                DateTime modifiedBeforeArgQ = default(DateTime);
                string sinceTokQ = args?["since"]?.ToString();
                string mbTokQ = args?["modifiedBefore"]?.ToString();
                if (!string.IsNullOrEmpty(sinceTokQ))
                    DateTime.TryParse(sinceTokQ, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out sinceArgQ);
                if (!string.IsNullOrEmpty(mbTokQ))
                    DateTime.TryParse(mbTokQ, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out modifiedBeforeArgQ);

                string searchResult = _searchService.Search(
                    target,
                    args?["typeFilter"]?.ToString(),
                    args?["domainFilter"]?.ToString(),
                    args?["limit"]?.ToObject<int?>() ?? 50,
                    args?["exactMatch"]?.ToObject<bool?>() ?? false,
                    args?["sort"]?.ToString(),
                    sinceArgQ,
                    modifiedBeforeArgQ,
                    args?["cursor"]?.ToString()
                );
                int inlineTopSearch = Math.Min(3, args?["inline_read_top"]?.ToObject<int?>() ?? 0);
                return inlineTopSearch > 0
                    ? AppendInlineReads(searchResult, inlineTopSearch)
                    : searchResult;
            }
            if (action == "SearchSource")
            {
                var criteria = new SourceSearchCriteria
                {
                    Callee = args?["callee"]?.ToString(),
                    Pattern = args?["pattern"]?.ToString(),
                    TypeFilter = args?["typeFilter"]?.ToString(),
                    CaseSensitive = args?["caseSensitive"]?.ToObject<bool?>() ?? false,
                    IncludeComments = args?["includeComments"]?.ToObject<bool?>() ?? false,
                    MaxResults = args?["maxResults"]?.ToObject<int?>() ?? 50,
                    // Issue #27 item 4: object scope + tunable timeout + resume cursor.
                    ObjectName = args?["objectName"]?.ToString(),
                    ObjectGuid = args?["guid"]?.ToString(),
                    ObjectEntityKey = args?["entityKey"]?.ToString(),
                    ObjectPath = args?["path"]?.ToString(),
                    StartIndex = args?["startIndex"]?.ToObject<int?>() ?? 0,
                    Cursor = args?["cursor"]?.ToString(),
                    TimeoutMs = args?["timeoutMs"]?.ToObject<int?>() ?? 30000
                };
                if (args?["scope"] is JArray scopeArr)
                    criteria.Scope = scopeArr.Select(t => t.ToString()).ToList();
                // Item 22: fields=[source,caption,description,parmNames]
                if (args?["fields"] is JArray fieldsArr)
                    criteria.Fields = fieldsArr.Select(t => t.ToString()).ToList();
                if (args?["argMatches"] is JObject am)
                {
                    criteria.ArgMatches = new Dictionary<int, string>();
                    foreach (var prop in am.Properties())
                    {
                        if (int.TryParse(prop.Name, out int idx))
                            criteria.ArgMatches[idx] = prop.Value?.ToString();
                    }
                }
                // v2.3.8 (post-Task 7.2 fix): register cancellation if the caller
                // supplied a cancelToken so a sibling Control:Cancel can stop the
                // scan mid-loop. Token typically matches the gateway's job_id.
                string ssCancelToken = args?["cancelToken"]?.ToString();
                string sourceSearchResult;
                using (GxMcp.Worker.Helpers.WorkerCancellationRegistry.Register(ssCancelToken, out var ssCt))
                {
                    sourceSearchResult = _sourceSearchService.SearchAsJson(criteria, ssCt);
                }
                int inlineTopSearchSource = Math.Min(3, args?["inline_read_top"]?.ToObject<int?>() ?? 0);
                return inlineTopSearchSource > 0
                    ? AppendInlineReadsForSourceSearch(sourceSearchResult, inlineTopSearchSource)
                    : sourceSearchResult;
            }
            return null;
        }

        private string Handle_List(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Objects")
            {
                DateTime sinceArg = default(DateTime);
                DateTime modifiedBeforeArg = default(DateTime);
                string sinceTok = args?["since"]?.ToString();
                string mbTok = args?["modifiedBefore"]?.ToString();
                if (!string.IsNullOrEmpty(sinceTok))
                    DateTime.TryParse(sinceTok, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out sinceArg);
                if (!string.IsNullOrEmpty(mbTok))
                    DateTime.TryParse(mbTok, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out modifiedBeforeArg);

                string listResult = _listService.ListObjects(
                    target,
                    args?["limit"]?.ToObject<int?>() ?? 5000,
                    args?["offset"]?.ToObject<int?>() ?? 0,
                    args?["parent"]?.ToString(),
                    args?["typeFilter"]?.ToString(),
                    args?["parentPath"]?.ToString(),
                    args?["verbose"]?.ToObject<bool?>() ?? false,
                    args?["nameFilter"]?.ToString(),
                    args?["descriptionFilter"]?.ToString(),
                    args?["pathPrefix"]?.ToString(),
                    args?["sort"]?.ToString(),
                    sinceArg,
                    modifiedBeforeArg,
                    args?["cursor"]?.ToString()
                );
                int inlineTopList = Math.Min(3, args?["inline_read_top"]?.ToObject<int?>() ?? 0);
                return inlineTopList > 0
                    ? AppendInlineReads(listResult, inlineTopList)
                    : listResult;
            }
            return null;
        }

        private string Handle_Read(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "ExtractFullObject")
            {
                string typeFilter = args?["type"]?.ToString() ?? request?["type"]?.ToString();
                string fullJson = _objectService.ReadFullObject(target, typeFilter,
                    args?["guid"]?.ToString(), args?["entityKey"]?.ToString(), args?["path"]?.ToString());
                try
                {
                    string kbPath = _kbService?.GetKbPath();
                    string objectType = typeFilter;
                    if (string.IsNullOrEmpty(objectType))
                    {
                        try { objectType = _objectService?.FindObject(target, typeFilter)?.TypeDescriptor?.Name; } catch { }
                    }
                    return MemoryService.AttachRelevantMemory(kbPath, fullJson, target, objectType);
                }
                catch
                {
                    return fullJson;
                }
            }
            if (action == "ExtractSource")
            {
                string typeFilter = args?["type"]?.ToString() ?? request?["type"]?.ToString();
                string readJson = _objectService.ReadObjectSource(target, args?["part"]?.ToString(), args?["offset"]?.ToObject<int?>(), args?["limit"]?.ToObject<int?>(), "mcp", false, typeFilter,
                    args?["guid"]?.ToString(), args?["entityKey"]?.ToString(), args?["path"]?.ToString());
                // Phase 2: genexus_read piggyback. Attached here (the tool boundary),
                // not inside ObjectService, so it (a) never pollutes the mcp read
                // cache (which stores the pre-attach payload) and (b) doesn't burn
                // the session dedup budget on internal ReadObjectSource callers
                // (History/Injection/Layout/WriteService/…) that never surface
                // their reads to the agent.
                try
                {
                    string kbPath = _kbService?.GetKbPath();
                    string objectType = typeFilter;
                    if (string.IsNullOrEmpty(objectType))
                    {
                        try { objectType = _objectService?.FindObject(target, typeFilter)?.TypeDescriptor?.Name; } catch { }
                    }
                    return MemoryService.AttachRelevantMemory(kbPath, readJson, target, objectType);
                }
                catch
                {
                    return readJson;
                }
            }
            if (action == "ExtractParts")
            {
                var partsTok = args?["parts"] as JArray;
                var requestedParts = partsTok?.Select(p => p.ToString()) ?? Enumerable.Empty<string>();
                return _objectService.ReadObjectSourceParts(target, requestedParts, args?["type"]?.ToString(),
                    args?["guid"]?.ToString(), args?["entityKey"]?.ToString(), args?["path"]?.ToString());
            }
            if (action == "GetVariables") return _analyzeService.GetVariables(target);
            if (action == "GetAttribute") return _analyzeService.GetAttributeMetadata(target);
            return null;
        }

        private string Handle_AtomicCreate(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Issue #62 — genexus_create action=object_atomic (module AtomicCreate).
            // The service validates the whole definition (variables/rules/parms/properties/source)
            // before the first save, composes the SDK write primitives, and compensates on failure.
            return _atomicCreateService.Run(args ?? new JObject());
        }

        private string Handle_DataView(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _dataViewService.Run(args ?? new JObject());
        }

        private string Handle_Object(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Read") return _objectService.ReadObject(target, args?["type"]?.ToString(), args?["guid"]?.ToString(), args?["entityKey"]?.ToString(), args?["path"]?.ToString());
            if (action == "Create")
            {
                // Item 21 (friction 2026-05-22): dryRun=true returns the planned
                // shape without calling newObj.Save(). args carries the flag.
                var createResp = _objectService.CreateObject(args?["type"]?.ToString(), target, args);
                // issue #60 — validationMode="specify" runs the inline Specify pass against
                // the created object and surfaces structured diagnostics (or rolls back).
                // A new object has no pre-write snapshot, so rollbackOnFailure reports
                // rolledBack=false with a note (delete the object via genexus_delete_object).
                return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(createResp, target, args, "Source");
            }
            if (action == "Delete") return _objectService.DeleteObject(target, args?["type"]?.ToString(), args?["confirm"]?.ToObject<bool?>() ?? false, args?["dryRun"]?.ToObject<bool?>() ?? false, args?["expectedVersion"]?.ToString());
            if (action == "SaveAs") return _saveAsService.SaveAs(args ?? new JObject());
            if (action == "WorkerReload")
            {
                // FR#20 (v2.6.6 Stream B): mode=soft is the new default — drain
                // in-flight commands and exit code 0 so the gateway respawns
                // without losing JobRegistry state. mode=hard preserves the
                // legacy copy-binaries-and-kill flow (still required when the
                // caller passed a sourceDir of fresh bits to lay down).
                string mode = args?["mode"]?.ToString()?.Trim().ToLowerInvariant();
                string srcDir = args?["sourceDir"]?.ToString();
                if (string.IsNullOrEmpty(mode))
                {
                    // Negotiate: hard when sourceDir is supplied (copy-binaries
                    // intent), otherwise soft (clean respawn for state reset).
                    mode = string.IsNullOrWhiteSpace(srcDir) ? "soft" : "hard";
                }
                if (mode == "soft")
                {
                    int drainMs = args?["drainTimeoutMs"]?.ToObject<int?>() ?? 30000;
                    return GxMcp.Worker.Program.PerformSoftReload(drainMs);
                }
                // Item 51 (Tier-S, EXPERIMENTAL) — warm reload. Persist the
                // IndexCacheService state to <kb>/.gx/index-snapshot.bin
                // gated by the worker DLL SHA, then do a soft reload. On
                // the next boot the gateway respawns this worker and a
                // future TryRestoreWarmIndex hook can pick it back up.
                if (mode == "warm")
                {
                    int drainMs = args?["drainTimeoutMs"]?.ToObject<int?>() ?? 30000;
                    var warmAck = TryCaptureWarmSnapshot();
                    var softResp = GxMcp.Worker.Program.PerformSoftReload(drainMs);
                    // Stitch warm metadata into the soft-reload ack so the
                    // caller sees both outcomes in a single response.
                    try
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(softResp);
                        jo["mode"] = "warm";
                        jo["warmSnapshot"] = warmAck;
                        return jo.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    catch { return softResp; }
                }
                return _objectService.WorkerReload(srcDir);
            }
            if (action == "ReadLogs") return _objectService.ReadLogs(
                args?["lines"]?.ToObject<int?>() ?? 100,
                args?["filterCorrelation"]?.ToString(),
                args?["grep"]?.ToString(),
                args?["since"]?.ToString(),
                // Item 32: objectFilter = target param for object-name filtering.
                args?["objectFilter"]?.ToString());
            if (action == "ExportText")
            {
                return _objectService.ExportObjectToText(
                    target,
                    args?["outputPath"]?.ToString() ?? args?["path"]?.ToString(),
                    args?["part"]?.ToString(),
                    args?["type"]?.ToString(),
                    args?["overwrite"]?.ToObject<bool?>() ?? false);
            }
            if (action == "ImportText") return _objectService.ImportObjectFromText(target, args?["inputPath"]?.ToString() ?? args?["path"]?.ToString(), args?["part"]?.ToString(), args?["type"]?.ToString());
            return null;
        }

        private string Handle_Mutation(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "ChangeSet", StringComparison.OrdinalIgnoreCase))
                return _changeSetService.Run(args ?? new JObject());
            return null;
        }

        private string Handle_Write(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "AddVariable")
            {
                bool varDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                if (!string.IsNullOrWhiteSpace(args?["objectType"]?.ToString()))
                {
                    if (!string.Equals(args?["objectType"]?.ToString(), "BusinessComponent", StringComparison.OrdinalIgnoreCase))
                        return Models.McpResponse.Err(code: "UnsupportedObjectVariableType",
                            message: "objectType currently supports BusinessComponent.", target: target);
                    return _writeService.ChangeBusinessComponentVariable("add", target,
                        args?["varName"]?.ToString(), args?["objectName"]?.ToString(),
                        args?["objectModule"]?.ToString(), varDryRun,
                        args?["expectedVersion"]?.ToString(),
                        args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                        args?["collection"]?.ToObject<bool?>());
                }
                // issue #32 item 1: batch form — a `variables` array adds many in one call
                // (one save/flush), avoiding N sequential round-trips + concurrent-write risk.
                var varBatch = (args?["variables"] ?? request["variables"]) as JArray;
                if (varBatch != null)
                {
                    var batchResp = _writeService.AddVariables(target, varBatch, varDryRun);
                    return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(batchResp, target, args, "Variables");
                }
                var addResp = _writeService.AddVariable(
                    target,
                    args?["varName"]?.ToString(),
                    args?["typeName"]?.ToString(),
                    varDryRun,
                    args?["length"]?.ToObject<int?>(),
                    args?["decimals"]?.ToObject<int?>(),
                    args?["collection"]?.ToObject<bool?>(),
                    args?["basedOn"]?.ToString());
                return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(addResp, target, args, "Variables");
            }
            if (action == "DeleteVariable")
            {
                bool varDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                var delResp = _writeService.DeleteVariable(
                    target,
                    args?["varName"]?.ToString(),
                    varDryRun);
                return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(delResp, target, args, "Variables");
            }
            if (action == "ModifyVariable")
            {
                bool varDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                if (!string.IsNullOrWhiteSpace(args?["objectType"]?.ToString()))
                {
                    if (!string.Equals(args?["objectType"]?.ToString(), "BusinessComponent", StringComparison.OrdinalIgnoreCase))
                        return Models.McpResponse.Err(code: "UnsupportedObjectVariableType",
                            message: "objectType currently supports BusinessComponent.", target: target);
                    return _writeService.ChangeBusinessComponentVariable("modify", target,
                        args?["varName"]?.ToString(), args?["objectName"]?.ToString(),
                        args?["objectModule"]?.ToString(), varDryRun,
                        args?["expectedVersion"]?.ToString(),
                        args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                        args?["collection"]?.ToObject<bool?>());
                }
                var modResp = _writeService.ModifyVariable(
                    target,
                    args?["varName"]?.ToString(),
                    args?["typeName"]?.ToString(),
                    args?["basedOn"]?.ToString(),
                    varDryRun,
                    args?["length"]?.ToObject<int?>(),
                    args?["decimals"]?.ToObject<int?>(),
                    args?["collection"]?.ToObject<bool?>());
                return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(modResp, target, args, "Variables");
            }
            if (action == "ValidatePayload")
            {
                return _validatePayloadService.Validate(
                    target,
                    args?["part"]?.ToString(),
                    payload);
            }
            if (action == "Bulk")
            {
                return _writeService.BulkWrite(args ?? request);
            }
            if (action == "ApplyTemplate")
            {
                return _applyTemplateService.Apply(
                    args?["template"]?.ToString(),
                    target,
                    args,
                    args?["dryRun"]?.ToObject<bool?>() ?? false);
            }
            if (action == "ListTemplates")
            {
                return _applyTemplateService.ListTemplates();
            }
            {
                // Friction 2026-05-26 — validate=only is the LLM-facing
                // contract for "run the write in-memory, do NOT persist".
                // Already plumbed for mode=patch and mode=ops; mirror it
                // here so PatternInstance / WebForm full-XML writes can
                // probe SDK validation without touching disk.
                string writeValidate = args?["validate"]?.ToString();
                bool writeDryRun = (args?["dryRun"]?.ToObject<bool?>() ?? false)
                    || string.Equals(writeValidate, "only", StringComparison.OrdinalIgnoreCase);
                bool autoDeclareVars = args?["autoDeclareVariables"]?.ToObject<bool?>() ?? args?["autoInjectVariables"]?.ToObject<bool?>() ?? false;
                // Keep the full-write facade intact. Besides dryRun, it owns optimistic
                // concurrency aliases and rollback intent. The former positional call dropped
                // baseVersion/expectedVersion and made full Source writes weaker than patches.
                var fullWriteArgs = args != null ? (JObject)args.DeepClone() : new JObject();
                fullWriteArgs["part"] = action;
                fullWriteArgs["mode"] = "full";
                fullWriteArgs["content"] = payload ?? fullWriteArgs["content"]?.ToString() ?? string.Empty;
                fullWriteArgs["dryRun"] = writeDryRun;
                fullWriteArgs["autoDeclareVariables"] = autoDeclareVars;
                var writeResp = _writeService.WriteObject(target, fullWriteArgs);
                // issue #60 — validationMode="specify" runs the inline Specify pass against
                // the written object and surfaces structured diagnostics (or rolls back).
                writeResp = _saveSpecifyOrchestrator.MaybeValidateAfterWrite(writeResp, target, args, args?["part"]?.ToString());
                return VisualVerifyResponseHook.MaybeAttach(args, writeResp, _visualVerifyService);
            }
        }

        private string Handle_EditAndBuild(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Orchestrate")
            {
                var orchResp = _editAndBuildOrchestrator.Orchestrate(args ?? new JObject());
                return VisualVerifyResponseHook.MaybeAttach(args, orchResp, _visualVerifyService);
            }
            return null;
        }

        private string Handle_SemanticOps(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Apply")
            {
                var soResp = _writeService.ApplySemanticOps(args ?? request);
                // issue #60 — validationMode="specify" runs the inline Specify pass after the
                // write and surfaces structured diagnostics (or rolls back).
                soResp = _saveSpecifyOrchestrator.MaybeValidateAfterWrite(soResp, target, args ?? request, args?["part"]?.ToString());
                return VisualVerifyResponseHook.MaybeAttach(args ?? request, soResp, _visualVerifyService);
            }
            return null;
        }

        private string Handle_JsonPatch(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Apply")
            {
                var jpResp = _writeService.ApplyJsonPatch(args ?? request);
                // issue #60 — validationMode="specify" runs the inline Specify pass after the
                // write and surfaces structured diagnostics (or rolls back).
                jpResp = _saveSpecifyOrchestrator.MaybeValidateAfterWrite(jpResp, target, args ?? request, args?["part"]?.ToString());
                return VisualVerifyResponseHook.MaybeAttach(args ?? request, jpResp, _visualVerifyService);
            }
            return null;
        }

        private string Handle_Patch(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Apply")
            {
                // v2.6.6 FR#13 follow-up: validate=only is the LLM-facing
                // contract for "run the patch in-memory, do NOT persist".
                // Stream A only plumbed validate through ApplySemanticOps
                // (mode=ops). For mode=patch, validate=only maps to
                // dryRun=true — the PatchService.ApplyPatch dryRun branch
                // returns the exact "Applied / write skipped" envelope the
                // validate=only schema promises. validate=best-effort and
                // validate=strict (the default) both fall through to the
                // current strict path: IsPatchWriteSafe already refuses
                // unsafe writes on NoMatch and surfaces the diagnostic
                // envelope, so the two are observationally equivalent in
                // mode=patch and we don't need a third branch.
                string validateMode = args?["validate"]?.ToString();
                bool dryRunArg = args?["dryRun"]?.ToObject<bool?>() ?? false;
                bool validateOnly = string.Equals(validateMode, "only", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(validateMode, "validate-only", StringComparison.OrdinalIgnoreCase);
                var patchResp = _patchService.ApplyPatch(
                    target,
                    args?["part"]?.ToString(),
                    args?["operation"]?.ToString(),
                    payload,
                    args?["context"]?.ToString(),
                    args?["expectedCount"]?.ToObject<int?>() ?? 1,
                    args?["type"]?.ToString(),
                    dryRunArg || validateOnly,
                    args?["verifyRollback"]?.ToObject<bool?>() ?? false,
                    args?["return_post_state"]?.ToObject<bool?>() ?? true,
                    args?["verbose"]?.ToObject<bool?>() ?? false,
                    args?["replaceAll"]?.ToObject<bool?>() ?? false,
                    args?["verifyMode"]?.ToString(),
                    args?["baseVersion"]?.ToString(),
                    args?["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                    args?["autoDeclareVariables"]?.ToObject<bool?>() ?? args?["autoInjectVariables"]?.ToObject<bool?>() ?? false);
                // issue #60 — validationMode="specify" runs the inline Specify pass after the
                // write and surfaces structured diagnostics (or rolls back).
                patchResp = _saveSpecifyOrchestrator.MaybeValidateAfterWrite(patchResp, target, args, args?["part"]?.ToString());
                return VisualVerifyResponseHook.MaybeAttach(args, patchResp, _visualVerifyService);
            }
            return null;
        }

        private string Handle_Analyze(JObject request, string method, string action, string target, string payload, JObject args)
        {
            var analyzeType = args?["type"]?.ToString();
            if (action == "GetCodeMetrics") return _analyzeService.GetCodeMetrics(analyzeType, args?["top"]?.ToObject<int?>() ?? 25);
            if (action == "GetNavigation") return _navigationService.GetNavigation(target);
            if (action == "GetSqlForNavigation")
            {
                int? levelNumber = args?["levelNumber"]?.ToObject<int?>();
                bool includeExecutionPlan = args?["includeExecutionPlan"]?.ToObject<bool?>() ?? false;
                bool includeIndexAdvisor = args?["includeIndexAdvisor"]?.ToObject<bool?>() ?? false;
                return _navigationSqlService.Generate(target, levelNumber, includeExecutionPlan, includeIndexAdvisor);
            }
            if (action == "GenerateSampleData")
            {
                int rows = args?["rows"]?.ToObject<int?>() ?? 5;
                return _sampleDataService.Generate(target, rows);
            }
            if (action == "QueryRecords" || action == "InsertRecord" || action == "UpdateRecords")
            {
                return _transactionRecordsService.Execute(action, target, args);
            }
            if (action == "TranslationsImport")
            {
                return _translationsService.Import(payload);
            }
            if (action == "GetParameters") return _analyzeService.GetSignature(target, analyzeType);
            if (action == "Get360Context" || action == "GetContext") return _analyzeService.Get360Context(target, analyzeType,
                args?["guid"]?.ToString(), args?["entityKey"]?.ToString(), args?["path"]?.ToString(),
                args?["maxBytes"]?.ToObject<int?>(), args?["cursor"]?.ToString());
            if (action == "GetHierarchy") return _analyzeService.GetHierarchy(target, analyzeType);
            if (action == "GetDataContext") return _dataInsightService.GetDataContext(target);
            if (action == "GetConversionContext")
            {
                string projection = args?["projection"]?.ToString();
                if (string.IsNullOrWhiteSpace(projection))
                    projection = (args?["verbose"]?.ToObject<bool?>() ?? false) ? "verbose" : "standard";
                return _analyzeService.GetConversionContext(target, args?["include"] as JArray, analyzeType, projection,
                    args?["guid"]?.ToString(), args?["entityKey"]?.ToString(), args?["path"]?.ToString());
            }
            if (action == "GetPatternMetadata") return _patternAnalysisService.GetWWPStructure(target);
            if (action == "Summarize") return _summarizeService.Summarize(target, analyzeType);
            if (action == "GetSQL")
            {
                bool includeSub = args?["includeSubordinated"]?.ToObject<bool?>() ?? false;
                return _dataInsightService.GetTableDDL(target, includeSub);
            }
            if (action == "ExplainCode") return _analyzeService.ExplainCode(target, payload);
            if (action == "ParentContext") return _analyzeService.ParentContext(target);
            // Wave-3 SOTA: mode=cross_platform_impact — Web vs SmartDevices divergence.
            if (action == "CrossPlatformImpact") return _analyzeService.CrossPlatformImpact(target);
            // Item 24: mode=callers — per-call-site detail with line + context.
            if (action == "FindCallerSites") return _analyzeService.FindCallerSites(target);
            if (action == "GetEventFlow") return _analyzeService.GetEventFlow(target, analyzeType);
            // Wave-3 item 87: KB-wide dependency heat rankings.
            if (action == "DependencyHeatmap")
            {
                string heatFormat = args?["format"]?.ToString();
                return _analyzeService.DependencyHeatmap(_kbService.GetKbPath(), heatFormat);
            }
            if (action == "ImpactAnalysis")
            {
                // v2.3.8 (Task 1.4): index-aware impact with optional wait-for-index.
                // v2.3.8 (post-Task 7.2): honour cancelToken via WorkerCancellationRegistry
                // so a parallel Control:Cancel stops the BFS mid-walk.
                bool waitForIndex = args?["waitForIndex"]?.ToObject<bool?>() ?? true;
                int waitTimeoutMs = args?["waitTimeoutMs"]?.ToObject<int?>() ?? 30000;
                string ctToken = args?["cancelToken"]?.ToString();
                using (GxMcp.Worker.Helpers.WorkerCancellationRegistry.Register(ctToken, out var iaCt))
                {
                    return _analyzeService.ImpactAnalysis(target, waitForIndex, waitTimeoutMs, iaCt);
                }
            }
            if (action == "InjectContext")
            {
                bool recursive = args?["recursive"]?.ToObject<bool>() ?? false;
                return _injectionService.InjectContext(target, recursive, analyzeType);
            }
            return _analyzeService.Analyze(target, analyzeType);
        }

        private string Handle_BuildPlan(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Wave-3 item 30: GeneratePlan walks the callee graph and emits
            // {nodes, edges, totalEstimatedSeconds, ascii?}.
            if (action == "Generate")
            {
                string planFormat = args?["format"]?.ToString();
                JObject toolStatsP95 = args?["toolStatsP95"] as JObject;
                int maxNodes = args?["maxNodes"]?.ToObject<int?>() ?? 100;
                return _buildPlanService.GeneratePlan(target, planFormat, toolStatsP95, maxNodes);
            }
            return null;
        }

        private string Handle_Future(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Wave-3 doc-flagged long-term / speculative items. The schema is
            // shipped via tool_definitions.json + FutureItemRouter; the body
            // is a single typed deferred envelope. See FutureItemStub.cs.
            if (action == "Deferred")
            {
                int itemNumber = args?["itemNumber"]?.ToObject<int?>() ?? 0;
                string hint = args?["hint"]?.ToString();
                return FutureItemStub.Deferred(itemNumber, hint);
            }
            return null;
        }

        private string Handle_Linter(JObject request, string method, string action, string target, string payload, JObject args)
        {
            bool linterFix = args?["fix"]?.ToObject<bool?>() ?? false;
            if (linterFix) return _linterService.LintAndFix(target);
            return _linterService.Lint(target);
        }

        private string Handle_Diff(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _diffService.Diff(
                args?["mode"]?.ToString() ?? action,
                target,
                args?["part"]?.ToString(),
                args?["left"]?.ToString(),
                args?["right"]?.ToString(),
                args?["context"]?.ToObject<int?>() ?? 3);
        }

        private string Handle_Export(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Unified" || action == "Full")
                return _exportObjectService.Export(target, args?["type"]?.ToString());
            return null;
        }

        private string Handle_Forge(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Scaffold")
            {
                var properties = new JObject();
                if (!string.IsNullOrEmpty(args?["description"]?.ToString())) properties["description"] = args["description"]?.ToString();
                if (!string.IsNullOrEmpty(args?["code"]?.ToString())) properties["code"] = args["code"]?.ToString();

                string scaffoldType = args?["type"]?.ToString() ?? target;
                string scaffoldName = args?["name"]?.ToString() ?? payload;
                return _forgeService.Scaffold(scaffoldType, scaffoldName, properties);
            }
            return null;
        }

        private string Handle_Conversion(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "TranslateTo") return _conversionService.TranslateTo(target, args?["language"]?.ToString());
            return null;
        }

        private string Handle_Pattern(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "GetSample") return _patternService.GetSample(target);
            if (action == "Apply")
            {
                bool reapply = args?["reapply"]?.ToObject<bool?>() ?? false;
                var patSettings = args?["settings"] as JObject;
                string patKey = args?["pattern"]?.ToString();
                if (reapply) return _patternApplyService.ReapplyPattern(target, patSettings);
                return _patternApplyService.ApplyPattern(target, patKey, patSettings);
            }
            if (action == "Diagnose")
            {
                // Item 45: read-only preflight — returns structured reasons without mutating.
                var patSettings = args?["settings"] as JObject;
                string patKey = args?["pattern"]?.ToString();
                return _patternApplyService.DiagnosePattern(target, patKey, patSettings);
            }
            if (action == "ManageActions") return _wwpActionService.Run(target, args ?? new JObject());
            return null;
        }

        private string Handle_AtomicAuthoring(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Run") return _atomicAuthoringService.Run(args ?? request ?? new JObject());
            return null;
        }

        private string Handle_SdkProbe(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Capabilities")
            {
                try
                {
                    return Models.McpResponse.Ok(
                        code: "SdkCapabilitiesRead",
                        result: JObject.Parse(_sdkProbeService.Capabilities()));
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(code: "SdkCapabilitiesError", message: ex.Message,
                        hint: "The capability probe is read-only; inspect the worker SDK load diagnostics and retry.");
                }
            }
            if (action == "Run")
            {
                try
                {
                    var probe = Services.SdkSurfaceProbe.Run(args?["outputDir"]?.ToString());
                    return Models.McpResponse.Ok(
                        code: "SdkProbeCompleted",
                        result: new JObject
                        {
                            ["rawJsonPath"] = probe.RawJsonPath,
                            ["indexMdPath"] = probe.IndexMdPath,
                            ["generatorsMdPath"] = probe.GeneratorsMdPath,
                            ["rawSizeBytes"] = probe.RawSizeBytes,
                            ["assembliesScanned"] = probe.AssembliesScanned,
                            ["typesScanned"] = probe.TypesScanned,
                            ["generatorCandidates"] = probe.GeneratorCandidates,
                            ["warnings"] = new JArray(probe.Warnings),
                            ["note"] = "Inspect rawJsonPath with jq, or read INDEX.md / generators.md for navigable views. See docs/sdk-probe/README.md."
                        });
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(code: "SdkProbeError", message: ex.Message, hint: "Check worker logs for the full stack trace.");
                }
            }
            return null;
        }

        private string Handle_Ui(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "GetUIContext") return _uiService.GetUIContext(target);
            return null;
        }

        private string Handle_Layout(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "GetTree")
            {
                return _layoutService.GetTree(
                    target,
                    args?["control"]?.ToString(),
                    args?["limit"]?.ToObject<int?>() ?? 500);
            }
            if (action == "FindControls")
            {
                return _layoutService.FindControls(
                    target,
                    args?["propertyName"]?.ToString(),
                    args?["query"]?.ToString(),
                    args?["limit"]?.ToObject<int?>() ?? 200);
            }
            if (action == "SetProperty")
            {
                return _layoutService.SetProperty(
                    target,
                    args?["control"]?.ToString(),
                    args?["propertyName"]?.ToString(),
                    args?["value"]?.ToString());
            }
            if (action == "SetProperties")
            {
                return _layoutService.SetProperties(
                    target,
                    args?["changes"] as JArray);
            }
            if (action == "InspectSurface")
            {
                return _layoutService.InspectSurface(target, args?["limit"]?.ToObject<int?>() ?? 50);
            }
            if (action == "GetVisualPreview")
            {
                return _layoutService.GetVisualPreview(target);
            }
            if (action == "ScanMutators")
            {
                return _layoutService.ScanMutators(target, args?["limit"]?.ToObject<int?>() ?? 100);
            }
            if (action == "RenamePrintBlock")
            {
                return _layoutService.RenamePrintBlock(
                    target,
                    args?["currentName"]?.ToString(),
                    args?["newName"]?.ToString());
            }
            if (action == "AddPrintBlock")
            {
                return _layoutService.AddPrintBlock(
                    target,
                    args?["printBlockName"]?.ToString(),
                    args?["height"]?.ToObject<int?>());
            }
            if (action == "DeletePrintBlock")
            {
                return _layoutService.DeletePrintBlock(
                    target,
                    args?["printBlockName"]?.ToString());
            }
            return null;
        }

        private string Handle_Structure(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "GetVisualStructure")
            {
                string typeFilter = args?["type"]?.ToString() ?? request?["type"]?.ToString();
                return _structureService.GetVisualStructure(target, typeFilter);
            }
            if (action == "UpdateVisualStructure")
            {
                var structResp = _structureService.UpdateVisualStructure(target, payload,
                    args?["dryRun"]?.ToObject<bool?>() ?? false,
                    args?["expectedVersion"]?.ToString() ?? args?["baseVersion"]?.ToString(),
                    args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true,
                    args?["transactionModule"]?.ToString());
                // issue #60 — validationMode="specify" runs the inline Specify pass against
                // the edited object and surfaces structured diagnostics (or rolls back).
                return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(structResp, target, args, "Structure");
            }
            if (action == "GetVisualIndexes") return _structureService.GetVisualIndexes(target);
            if (action == "CreateIndex") return _structureService.CreateIndex(target, payload, args);
            if (action == "DropIndex") return _structureService.DropIndex(target, payload);
            if (action == "SetAttributeProperties") return _structureService.SetAttributeProperties(target, payload);
            if (action == "SetLevelProperties") return _structureService.SetLevelProperties(target, payload);
            if (action == "SetDomainProperties") return _structureService.SetDomainProperties(target, payload);
            if (action == "GetLogicStructure") return _structureService.GetLogicStructure(target);
            if (action == "UpdateGroupStructure") return _structureService.UpdateGroupStructure(target, payload);
            if (action == "MoveAttribute") return _structureService.MoveAttribute(target, args);
            // Issue #97: native TransactionLevel.Items removal (preserves the KB-global
            // Attribute and every SubtypeGroup membership) — lets agents remove a
            // misclassified subtype attribute and re-add it to force re-derivation.
            if (action == "RemoveAttribute") return _structureService.RemoveAttribute(target, args);
            // Issue #97 guard-rail: detect subtype attributes classified as stored
            // (SECONDARY) while their same-supertype siblings are derived (INFERRED).
            if (action == "CheckSubtypes") return _structureService.CheckSubtypeClassification(target, args);
            return null;
        }

        private string Handle_Authoring(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "AddExternalMethod") return _authoringService.AddExternalMethod(target, payload);
            if (action == "AddExternalProperty") return _authoringService.AddExternalProperty(target, payload);
            if (action == "AddMenuOption") return _authoringService.AddMenuOption(target, payload);
            if (action == "AddDataSelectorCondition") return _authoringService.AddDataSelectorCondition(target, payload);
            return null;
        }

        private string Handle_Build(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _compilationPipeline.ExecuteBuild(action, target, args, request);
        }

        private string Handle_Validation(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // The routed `action` here is the umbrella verb ("Check"), NOT a part
            // name — passing it through as partName made GetPart(obj,"Check") miss
            // every time and the tool always returned ValidationSkipped. Validate
            // the requested part (default Source; e.g. Rules) instead.
            return _validationService.ValidateCode(target, args?["part"]?.ToString() ?? "Source", payload);
        }

        private string Handle_Test(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _testService.RunTest(target);
        }

        private string Handle_Wiki(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _wikiService.Generate(target);
        }

        private string Handle_Visualizer(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _visualizerService.GenerateGraph(payload ?? target);
        }

        private string Handle_Health(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _healthService.GetHealthReport();
        }

        private string Handle_History(JObject request, string method, string action, string target, string payload, JObject args)
        {
            {
                int verId = args?["versionId"]?.ToObject<int?>() ?? 0;
                // v2.6.6 Stream H (FR#28) — forward discard + snapshot + part
                // so HistoryService can route restore through EditSnapshotStore.
                string partName = args?["part"]?.ToString();
                string snapshotToken = args?["snapshot"]?.ToString();
                bool discard = args?["discard"]?.ToObject<bool?>() ?? false;
                // Item 21 (friction 2026-05-22): dryRun=true returns the
                // would-be diff without writing.
                bool historyDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                return _historyService.Execute(target, action, verId, partName, snapshotToken, discard, historyDryRun);
            }
                    // Item 16 — genexus_undo last=N
        }

        private string Handle_Undo(JObject request, string method, string action, string target, string payload, JObject args)
        {
            {
                int last = args?["last"]?.ToObject<int?>() ?? 1;
                bool undoDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                return _undoService.Undo(last, undoDryRun);
            }
                    // Items 50 + 48 — genexus_security action=audit_gam|scan_secrets
        }

        private string Handle_Security(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "audit_gam", StringComparison.OrdinalIgnoreCase))
                return _securityAuditService.AuditGam();
            if (string.Equals(action, "scan_secrets", StringComparison.OrdinalIgnoreCase))
                return _securityAuditService.ScanSecrets();
            if (string.Equals(action, "scan_native", StringComparison.OrdinalIgnoreCase))
                return _securityScanService.Run(args ?? new JObject());
            return Models.McpResponse.Err(
                code: "UnknownAction",
                message: $"Unsupported security action '{action}'.",
                hint: "Call genexus_security with no action to see the supported list.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_orient",
                        args: new JObject(),
                        why: "Shows the tool catalog so the right action can be chosen.")),
                target: target);
                    // Item 65 — genexus_orient welcome card
        }

        private string Handle_Orient(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Welcome", StringComparison.OrdinalIgnoreCase))
                return _orientService.Welcome();
            return Models.McpResponse.Err(
                code: "UnknownAction",
                message: $"Unsupported orient action '{action}'.",
                hint: "Call genexus_orient with no action to see the supported list.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_orient",
                        args: new JObject(),
                        why: "Shows the tool catalog so the right action can be chosen.")),
                target: target);
        }

        private string Handle_Property(JObject request, string method, string action, string target, string payload, JObject args)
        {
            var propType = args?["type"]?.ToString();
            if (string.Equals(action, "Move", StringComparison.OrdinalIgnoreCase))
            {
                // NOTE: args["module"] is the routing key ("Property"), NOT a destination —
                // the router passes a module destination as "module_"/"destModule".
                string destFolder = args?["folder"]?.ToString();
                string destModule = args?["destModule"]?.ToString() ?? args?["module_"]?.ToString();
                string destination = args?["destination"]?.ToString()
                    ?? args?["targetModule"]?.ToString()
                    ?? (!string.IsNullOrWhiteSpace(destFolder) ? destFolder : destModule)
                    ?? args?["value"]?.ToString();
                string destKind = args?["destKind"]?.ToString()
                    ?? (!string.IsNullOrWhiteSpace(destFolder) ? "Folder"
                        : !string.IsNullOrWhiteSpace(destModule) ? "Module" : null);
                bool moveDry = args?["dryRun"]?.ToObject<bool?>() ?? false;
                string baseVersion = args?["baseVersion"]?.ToString();
                bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
                return _objectService.MoveObject(target, destination, propType, destKind, moveDry, baseVersion, rollbackOnFailure);
            }
            if (action == "Set" || action == "SetMultiple" || (action == "set" && args?["properties"] != null))
            {
                if (args?["properties"] is JObject propsObj && propsObj.Count > 0)
                {
                    var propResp = _propertyService.SetProperties(
                        target,
                        propsObj,
                        args?["control"]?.ToString(),
                        propType);
                    return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(propResp, target, args);
                }

                var singleResp = _propertyService.SetProperty(
                    target,
                    args?["propertyName"]?.ToString(),
                    args?["value"]?.ToString(),
                    args?["control"]?.ToString(),
                    propType);
                // issue #60 — validationMode="specify" runs the inline Specify pass against
                // the edited object and surfaces structured diagnostics (or rolls back).
                return _saveSpecifyOrchestrator.MaybeValidateAfterWrite(singleResp, target, args);
            }
            return _propertyService.GetProperties(target, args?["control"]?.ToString(), propType);
        }

        private string Handle_Asset(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Find")
            {
                return _assetService.Find(
                    args?["pattern"]?.ToString(),
                    args?["relativeRoot"]?.ToString(),
                    args?["limit"]?.ToObject<int?>() ?? 20);
            }

            if (action == "Read")
            {
                return _assetService.Read(
                    target,
                    args?["includeContent"]?.ToObject<bool?>() ?? false,
                    args?["maxBytes"]?.ToObject<int?>());
            }

            if (action == "Write")
            {
                return _assetService.Write(
                    target,
                    args?["contentBase64"]?.ToString());
            }

            return null;
        }

        private string Handle_Formatting(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Format") return _formatService.Format(payload);
            return null;
        }

        private string Handle_Refactor(JObject request, string method, string action, string target, string payload, JObject args)
        {
                    {
            bool refactorDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
            return _refactorService.Refactor(target, action, payload, refactorDryRun);
                    }
        }

        private string Handle_Popup(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Create")
            {
                string popupName = target ?? args?["name"]?.ToString();
                var popupSpec = args?["spec"] as JObject;
                // Item 21 (friction 2026-05-22): dryRun=true previews layout XML without persisting.
                bool popupDryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                return _popupTemplateService.CreatePopup(popupName, popupSpec, popupDryRun);
            }
            return null;
        }

        private string Handle_DbDrift(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Item 41 (mcp-improvements-2026-05-22) — Transaction ↔ DB drift.
            // Bug #2: deep is opt-in; default false runs the cheap timestamp heuristic
            // instead of the build-heavy ISpecifierService.ImpactDatabase.
            bool deep = args?["deep"]?.ToObject<bool?>() ?? false;
            if (string.Equals(action, "Check", StringComparison.OrdinalIgnoreCase))
                return _dbDriftService.Check(target, deep);
            if (string.Equals(action, "Report", StringComparison.OrdinalIgnoreCase))
                return _dbDriftService.Report(target, deep);
            return null;
        }

        private string Handle_DbOptimize(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // SOTA — static index advisor. Walks For each blocks and proposes
            // covering indexes for hot Transaction × where-signature paths.
            if (string.Equals(action, "Analyze", StringComparison.OrdinalIgnoreCase))
                return _dbOptimizeService.Analyze(target);
            if (string.Equals(action, "SuggestIndexes", StringComparison.OrdinalIgnoreCase))
                return _dbOptimizeService.SuggestIndexes(target);
            if (string.Equals(action, "Report", StringComparison.OrdinalIgnoreCase))
                return _dbOptimizeService.Report(args?["format"]?.ToString());
            return null;
        }

        private string Handle_WebFormEdit(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Item 19 (mcp-improvements-2026-05-22) — semantic WebForm edits.
            return _webFormEditService.Execute(action, args);
        }

        private string Handle_RunObject(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Item 11 (mcp-improvements-2026-05-22) — runtime URL + optional GAM cookies.
            if (string.Equals(action, "Resolve", StringComparison.OrdinalIgnoreCase))
            {
                string roName = target ?? args?["name"]?.ToString();
                var roArgs = args?["args"] as JArray;
                var gamToken = args?["gamSession"];
                bool roDryRun = request["dryRun"]?.ToObject<bool?>() ?? false;
                return _runObjectService.Resolve(roName, roArgs, gamToken, roDryRun);
            }
            return null;
        }

        private string Handle_Explain(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Item 68 — PM-readable deterministic summary.
            if (string.Equals(action, "Explain", StringComparison.OrdinalIgnoreCase))
                return _explainService.Explain(target, args?["type"]?.ToString(), args?["depth"]?.ToString());
            return null;
        }

        private string Handle_GeneratedDiff(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Item 12 — unified diff of generated artifacts vs baseline.
            if (string.Equals(action, "Diff", StringComparison.OrdinalIgnoreCase))
                return _generatedDiffService.Diff(target, args?["against"]?.ToString());
            return null;
        }

        private string Handle_KbReadme(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // Item 90 — Markdown README generation.
            if (string.Equals(action, "Generate", StringComparison.OrdinalIgnoreCase))
                return _kbReadmeService.Generate("generate", args?["outputPath"]?.ToString());
            return null;
        }

        private string Handle_Ocr(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase))
                return _ocrScreenshotService.Run(args?["path"]?.ToString());
            return null;
        }

        private string Handle_PrDescription(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Generate", StringComparison.OrdinalIgnoreCase))
            {
                int last = args?["last"]?.ToObject<int?>() ?? 10;
                return _prDescriptionService.Generate(last, args?["workingDir"]?.ToString());
            }
            return null;
        }

        private string Handle_ScreenshotPublish(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Publish", StringComparison.OrdinalIgnoreCase))
                return _screenshotPublishService.Publish(args?["path"]?.ToString());
            return null;
        }

        private string Handle_FrictionLog(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Append", StringComparison.OrdinalIgnoreCase))
            {
                return _frictionLogService.Append(
                    args?["tool"]?.ToString(),
                    args?["message"]?.ToString(),
                    args?["severity"]?.ToString());
            }
            if (string.Equals(action, "Tail", StringComparison.OrdinalIgnoreCase))
            {
                int n = args?["n"]?.ToObject<int?>() ?? 20;
                return _frictionLogService.Tail(n);
            }
            return null;
        }

        private string Handle_Memory(JObject request, string method, string action, string target, string payload, JObject args)
        {
            string objectName = args?["target"]?.ToString();
            string objectType = args?["type"]?.ToString();
            string[] tags = (args?["tags"] as JArray)?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();

            if (string.Equals(action, "save", StringComparison.OrdinalIgnoreCase))
            {
                return _memoryService.Save(args?["fact"]?.ToString(), objectName, objectType, tags);
            }
            if (string.Equals(action, "recall", StringComparison.OrdinalIgnoreCase))
            {
                return _memoryService.Recall(objectName, objectType, tags);
            }
            if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
            {
                return _memoryService.List();
            }
            if (string.Equals(action, "forget", StringComparison.OrdinalIgnoreCase))
            {
                return _memoryService.Forget(args?["id"]?.ToString());
            }
            if (string.Equals(action, "promote", StringComparison.OrdinalIgnoreCase))
            {
                return _memoryService.Promote(args?["message"]?.ToString(), objectName, objectType, tags);
            }
            if (string.Equals(action, "consolidate", StringComparison.OrdinalIgnoreCase))
            {
                bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;
                return _memoryService.Consolidate(objectName, objectType, tags, dryRun);
            }
            return null;
        }

        private string Handle_WcagCheck(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Check", StringComparison.OrdinalIgnoreCase))
                return _wcagCheckService.Check(target ?? args?["target"]?.ToString() ?? args?["name"]?.ToString());
            return null;
        }

        private string Handle_Learning(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Report", StringComparison.OrdinalIgnoreCase))
                return _learningReportService.Report(args?["since"]?.ToString(), args?["until"]?.ToString());
            return null;
        }

        private string Handle_GxServer(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_gxserver — GxServer sync state (SDK-backed) plus
            // write actions (commit/update/lock/resolve), routed inside
            // GxServerSyncService.Run to the sibling GxServerWriteService.
            // P1 #4: pipeline_* actions route to the CI pipeline service.
            var a = args ?? new JObject();
            string gxsAction = a["action"]?.ToString() ?? "";
            if (gxsAction.StartsWith("pipeline", StringComparison.OrdinalIgnoreCase))
                return _ciPipelineService.Run(gxsAction, a);
            return _gxServerSyncService.Run(a);
        }

        private string Handle_Compare(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_compare — read-only IDE "Compare Objects" parity over
            // the SDK's IComparerService. See docs/sdk_coverage_gap_matrix.md P0 #2.
            return _compareService.Run(args ?? new JObject());
        }

        private string Handle_Module(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_module — GeneXus Module Manager (install/update) over
            // the SDK's IModuleManagerService. See ModuleService for the
            // feasibility-gate notes on which overloads are wired.
            return _moduleService.Run(args ?? new JObject());
        }

        private string Handle_Gam(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_gam — GAM / integrated-security provisioning over the
            // SDK's IIntegratedSecurityService. action=status is read-only;
            // define_api/deploy are destructive (see GamService for guards).
            return _gamService.Run(args ?? new JObject());
        }

        private string Handle_Merge(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_merge — WRITE surface over the SDK's IMergeService.
            // dryRun defaults true (see MergeToolService). destructiveHint=true.
            return _mergeToolService.Run(args ?? new JObject());
        }

        private string Handle_KbVersion(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_kb_version — KB model-version management
            // (Create Version / Branch / Activate / Revert) over
            // Artech.Architecture.Common.Helpers.KBVersionHelper.
            return _kbVersionService.Run(args ?? new JObject());
        }

        private string Handle_Transfer(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_transfer — real XPZ export/import over IKnowledgeManagerService.
            // action=export|inspect|import (import destructive; see TransferService guards).
            return _transferService.Run(args ?? new JObject());
        }

        private string Handle_Deploy(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_deploy — deploy application over IDeploymentService/
            // IDeploymentTargetService. list_targets read-only; deploy destructive.
            return _deployService.Run(args ?? new JObject());
        }

        private string Handle_ReorgImpact(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_db action=reorg_impact / action=reorg_preview (issue #61).
            // reorg_impact: cheap timestamp heuristic default; deep=true runs specification.
            // reorg_preview: model-level before/after diff + proposed DDL + warnings.
            if (string.Equals(action, "Preview", StringComparison.OrdinalIgnoreCase))
                return _reorgImpactService.Preview(args ?? new JObject());
            return _reorgImpactService.Run(args ?? new JObject());
        }

        private string Handle_KbStats(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_analyze mode=kb_stats — KB activity & freshness (read-only).
            return _kbStatsService.Run(args ?? new JObject());
        }

        private string Handle_TableRelations(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_analyze mode=table_relations — table↔transaction relations (read-only).
            return _tableRelationsService.Run(args ?? new JObject());
        }

        private string Handle_UserControls(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_layout action=list_controls — control/theme catalog (read-only).
            return _userControlsListService.Run(args ?? new JObject());
        }

        private string Handle_WwpAction(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_wwp — WorkWithPlus Action Group / grid-action editing (issue #58).
            // list|add_action|update_action|move_action|remove_action over the host's
            // PatternInstance XML; dryRun supported; no Security permissions created.
            return _wwpActionService.Run(target, args ?? new JObject());
        }

        private string Handle_GeneratorReference(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _generatorReferenceService.Run(args ?? new JObject());
        }

        private string Handle_CurlProc(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_create action=curl_procedure — scaffold a Procedure from a curl command (write).
            return _curlProcService.Run(args ?? new JObject());
        }

        private string Handle_DesignSystem(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_layout action=design_system — DSO tokens/classes/images (read-only).
            return _designSystemService.Run(args ?? new JObject());
        }

        private string Handle_SdPanel(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _sdPanelService.Dispatch(action, target ?? args?["name"]?.ToString() ?? args?["target"]?.ToString(), args?["params"] as JObject ?? args);
        }

        private string Handle_MultiAgentLock(JObject request, string method, string action, string target, string payload, JObject args)
        {
            return _multiAgentLockService.Dispatch(
                action,
                target ?? args?["target"]?.ToString(),
                args?["part"]?.ToString(),
                args?["ownerId"]?.ToString(),
                args?["ttlSec"]?.ToObject<int?>() ?? 300,
                kbPathOverride: null,
                dryRun: request["dryRun"]?.ToObject<bool?>() ?? false);
        }

        private string Handle_WhatIf(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Simulate", StringComparison.OrdinalIgnoreCase))
                return _whatIfService.Simulate(args?["change"] as JObject);
            return null;
        }

        private string Handle_Tutorial(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Step", StringComparison.OrdinalIgnoreCase))
                return _tutorialService.GetStep(args?["step"]?.ToObject<int?>() ?? 1);
            return null;
        }

        private string Handle_Playbook(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Read", StringComparison.OrdinalIgnoreCase))
                return _playbookService.Read(
                    args?["topic"]?.ToString(),
                    args?["list"]?.ToObject<bool?>() == true);
            return null;
        }

        private string Handle_Doctor(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_doctor — health/triage envelope. No args.
            return _doctorService.Diagnose();
        }

        private string Handle_Api(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_api — REST endpoint introspection + diff vs baseline.
            // Single Run() switches on args.action (list/describe/diff_baseline/snapshot).
            return _apiIntrospectService.Run(args ?? new JObject());
        }

        private string Handle_Profile(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_profile — runtime profiler XML bridge.
            // Single Run() switches on args.action (analyze/hotspots/correlate).
            return _profileService.Run(args ?? new JObject());
        }

        private string Handle_Types(JObject request, string method, string action, string target, string payload, JObject args)
        {
            // genexus_types — Domain/SDT introspection + value validation.
            // Run() switches on args.action (list/describe/validate_value).
            return _typeIntrospectService.Run(args ?? new JObject());
        }

        private string Handle_Github(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "CreatePr", StringComparison.OrdinalIgnoreCase))
                return _githubService.CreatePr(args?["title"]?.ToString(), args?["body"]?.ToString(), args?["base"]?.ToString(), args?["workingDir"]?.ToString(), request["dryRun"]?.ToObject<bool?>() ?? false);
            return null;
        }

        private string Handle_AiComplete(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Complete", StringComparison.OrdinalIgnoreCase))
                return _aiCompleteService.Complete(args?["name"]?.ToString(), args?["part"]?.ToString(), args?["context"]?.ToString(), args?["maxTokens"]?.ToObject<int?>() ?? 200).ToString(Newtonsoft.Json.Formatting.None);
            return null;
        }

        private string Handle_TimeTravel(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Recover", StringComparison.OrdinalIgnoreCase))
                return _timeTravelService.Recover(target ?? args?["name"]?.ToString(), args?["at"]?.ToString());
            return null;
        }

        private string Handle_Voice(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Intent", StringComparison.OrdinalIgnoreCase))
                return _voiceIntentService.Map(args?["transcript"]?.ToString()).ToString(Newtonsoft.Json.Formatting.None);
            return null;
        }

        private string Handle_AutoTest(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Generate", StringComparison.OrdinalIgnoreCase))
                return _autoTestService.Generate(args?["path"]?.ToString());
            return null;
        }

        private string Handle_ReversePattern(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Infer", StringComparison.OrdinalIgnoreCase))
                return _reversePatternService.Infer(args?["source"] as JArray);
            return null;
        }

        private string Handle_CrossBrowser(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase))
                return _crossBrowserService.Run(target ?? args?["target"]?.ToString(), args?["browsers"] as JArray, args?["capture"] as JArray);
            return null;
        }

        private string Handle_Preview(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Render" || action == "Run")
            {
                string previewName = target ?? args?["name"]?.ToString();
                var previewParms = args?["parms"] as JObject;
                string launcher = args?["launcher"]?.ToString() ?? "auto";
                bool buildFirst = args?["buildFirst"]?.ToObject<bool?>() ?? false;
                int waitMs = args?["waitMs"]?.ToObject<int?>() ?? 3000;
                string[] capture = (args?["capture"] as JArray)?.Select(t => t.ToString()).ToArray();
                bool diffBaseline = args?["diffBaseline"]?.ToObject<bool?>() ?? false;
                bool updateBaseline = args?["updateBaseline"]?.ToObject<bool?>() ?? false;
                // Stream G (v2.6.6): GX-aware fill/click + GAM auth.
                var fill = args?["fill"] as JObject;
                string click = args?["click"]?.ToString();
                var auth = args?["auth"] as JObject;
                // Items 39/97: device emulation + network throttle, passed
                // through to chrome-devtools-axi via --emulate / --throttle.
                string emulate = args?["emulate"]?.ToString();
                string network = args?["network"]?.ToString();
                // v2.6.6 Stream H (FR#25): action=Run resolves the
                // KB launcher object when target is omitted; action=Render
                // requires an explicit target as before.
                var previewTask = action == "Run"
                    ? _previewService.RunAsync(previewName, previewParms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline, fill, click, auth, emulate, network)
                    : _previewService.PreviewAsync(previewName, previewParms, launcher, buildFirst, waitMs, capture, diffBaseline, updateBaseline, fill, click, auth, emulate, network);
                previewTask.Wait();
                return previewTask.Result.ToString(Newtonsoft.Json.Formatting.None);
            }
            return null;
                    // Wave-3: IDE right-click parity tools.
        }

        private string Handle_KbExplorer(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Locate", StringComparison.OrdinalIgnoreCase))
            {
                string locName = target ?? args?["name"]?.ToString();
                return _kbExplorerService.Locate(locName);
            }
            return Models.McpResponse.Err(
                code: "UnknownAction",
                message: $"Unsupported kbexplorer action '{action}'.",
                hint: "Call genexus_kbexplorer with no action to see the supported list.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_orient",
                        args: new JObject(),
                        why: "Shows the tool catalog so the right action can be chosen.")),
                target: target);
        }

        private string Handle_Navigation(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "View", StringComparison.OrdinalIgnoreCase))
            {
                string navName = target ?? args?["name"]?.ToString();
                bool latest = args?["latest"]?.ToObject<bool?>() ?? false;
                return _navigationViewService.View(navName, latest);
            }
            return Models.McpResponse.Err(
                code: "UnknownAction",
                message: $"Unsupported navigation action '{action}'.",
                hint: "Call genexus_navigation with no action to see the supported list.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_orient",
                        args: new JObject(),
                        why: "Shows the tool catalog so the right action can be chosen.")),
                target: target);
        }

        private string Handle_Blame(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (string.Equals(action, "Get", StringComparison.OrdinalIgnoreCase))
            {
                var blameReq = new BlameService.BlameRequest
                {
                    Name = target ?? args?["name"]?.ToString(),
                    Part = args?["part"]?.ToString(),
                    FilePath = args?["filePath"]?.ToString(),
                    Line = args?["line"]?.ToObject<int?>() ?? 0,
                    Context = args?["context"]?.ToObject<int?>() ?? 2
                };
                return _blameService.Blame(blameReq);
            }
            return Models.McpResponse.Err(
                code: "UnknownAction",
                message: $"Unsupported blame action '{action}'.",
                hint: "Call genexus_blame with no action to see the supported list.",
                nextSteps: new JArray(
                    Models.McpResponse.NextStep(
                        tool: "genexus_orient",
                        args: new JObject(),
                        why: "Shows the tool catalog so the right action can be chosen.")),
                target: target);
        }

        private string Handle_BrowserCapture(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Capture")
            {
                string bcTarget = target ?? args?["name"]?.ToString();
                var bcKinds = args?["capture"] as JArray;
                return _browserCaptureService.Capture(bcTarget, bcKinds).ToString(Newtonsoft.Json.Formatting.None);
            }
            return null;
        }

        private string Handle_SmokeTest(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Run")
            {
                string stTarget = target ?? args?["name"]?.ToString();
                return _smokeTestService.Run(stTarget).ToString(Newtonsoft.Json.Formatting.None);
            }
            return null;
        }

        private string Handle_A11yAudit(JObject request, string method, string action, string target, string payload, JObject args)
        {
            if (action == "Audit")
            {
                string aaTarget = target ?? args?["name"]?.ToString();
                return _a11yAuditService.Audit(aaTarget).ToString(Newtonsoft.Json.Formatting.None);
            }
            return null;
        }


        public static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>
        /// Post-process a list/query JSON response by appending inline_reads for the top N results.
        /// Reads are capped at 3. ResponseSizeGuard remains the ceiling on total response size.
        /// </summary>
        private string AppendInlineReads(string responseJson, int n)
        {
            return AppendInlineReadsCore(responseJson, n,
                (name, type) => _objectService.ReadFullObject(name, type));
        }

        private string AppendInlineReadsForSourceSearch(string responseJson, int n) =>
            AppendInlineReadsCore(responseJson, n,
                (name, type) => _objectService.ReadFullObject(name, type),
                arrayKey: "hits", nameField: "objectName", dedupe: true);

        /// <summary>
        /// Testable core: merges inline_reads into a response JSON given a reader delegate.
        /// arrayKey/nameField/dedupe let search_source (hits/objectName, dedup repeats) share
        /// this with query/list_objects (results/name, no dedup).
        /// </summary>
        public static string AppendInlineReadsCore(string responseJson, int n, Func<string, string, string> reader,
            string arrayKey = "results", string nameField = "name", bool dedupe = false)
        {
            if (string.IsNullOrEmpty(responseJson) || n <= 0) return responseJson;
            try
            {
                var responseObj = JObject.Parse(responseJson);
                var items = (responseObj[arrayKey] as JArray) ?? (responseObj["items"] as JArray) ?? (responseObj["results"] as JArray);
                if (items == null || items.Count == 0) return responseJson;

                var reads = new JArray();
                var seen = dedupe ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                foreach (var item in items.OfType<JObject>())
                {
                    if (reads.Count >= n) break;
                    string name = item[nameField]?.ToString();
                    string type = item["type"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (seen != null && !seen.Add(name)) continue;
                    try
                    {
                        string content = reader(name, type);
                        reads.Add(new JObject
                        {
                            ["name"] = name,
                            ["type"] = type,
                            ["content"] = JToken.Parse(content)
                        });
                    }
                    catch { /* skip objects that fail to read */ }
                }

                if (reads.Count > 0) responseObj["inline_reads"] = reads;
                return responseObj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return responseJson; }
        }
    }
}

