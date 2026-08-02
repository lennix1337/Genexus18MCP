using Newtonsoft.Json.Linq;
using Xunit;
using System;
using System.Threading.Tasks;

namespace GxMcp.Gateway.Tests
{
    public class McpRouterTests
    {
        [Fact]
        public void IsJsonRpcNotification_ShouldSuppressMissingOrNullIds()
        {
            Assert.True(Program.IsJsonRpcNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")));
            Assert.True(Program.IsJsonRpcNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}")));
            Assert.False(Program.IsJsonRpcNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}")));
        }

        [Fact]
        public void Handle_Initialize_ShouldExposeCurrentProtocolVersion()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"initialize"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal(McpRouter.SupportedProtocolVersion, json["protocolVersion"]?.ToString());
        }

        [Fact]
        public void Handle_Initialize_ShouldEchoKnownRequestedProtocolVersion()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");

            var result = McpRouter.Handle(request);

            Assert.Equal("2025-03-26", JObject.FromObject(result!)["protocolVersion"]?.ToString());
        }

        [Fact]
        public void Handle_Initialize_ShouldNotAcceptModernPerRequestVersion()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"protocolVersion":"2026-07-28"}}"""
            );

            Assert.Null(McpRouter.Handle(request));
        }

        [Fact]
        public void Handle_ServerDiscover_ShouldAdvertiseModernAndLegacyVersions()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"discover","method":"server/discover"}""");

            var result = JObject.FromObject(McpRouter.Handle(request)!);

            var versions = result["supportedVersions"] as JArray;
            Assert.NotNull(versions);
            Assert.Contains(McpRouter.ModernProtocolVersion, versions!.Values<string>());
            Assert.Contains(McpRouter.SupportedProtocolVersion, versions.Values<string>());
            Assert.Equal("genexus-mcp-server", result["_meta"]?["io.modelcontextprotocol/serverInfo"]?["name"]?.ToString());
            Assert.True(result["ttlMs"]!.Value<int>() > 0);
        }

        [Fact]
        public void Handle_PromptsList_ShouldExposeWorkflowCatalog()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"prompts/list"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var prompts = (JArray)json["prompts"]!;
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_convert_object");
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_trace_dependencies");
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_agent_ship_change");
            Assert.Contains(prompts, prompt => prompt?["name"]?.ToString() == "gx_bootstrap_llm");
        }

        [Fact]
        public void Handle_PromptsGet_ShouldBuildPromptSpecificMessage()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_convert_object","arguments":{"name":"InvoiceEntry","targetLanguage":"TypeScript"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var firstMessage = json["messages"]![0]!;
            var text = firstMessage["content"]?["text"]?.ToString() ?? "";
            Assert.Contains("InvoiceEntry", text);
            Assert.Contains("TypeScript", text);
            Assert.Contains("conversion-context", text);
        }

        [Fact]
        public void Handle_PromptsGet_ShouldRejectMissingRequiredPromptArgument()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_agent_ship_change","arguments":{"objectName":"InvoiceEntry"}}}"""
            );

            var result = McpRouter.Handle(request);

            var error = Assert.IsType<McpRouterError>(result);
            Assert.Equal(-32602, error.Code);
            Assert.Contains("Missing required argument 'goal'", error.Message);
        }

        [Fact]
        public async Task ProcessMcpRequest_PromptsGetInvalidArguments_ShouldReturnJsonRpcError()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_agent_ship_change","arguments":{"objectName":"InvoiceEntry"}}}"""
            );

            var response = await Program.ProcessMcpRequest(request);

            Assert.Equal(-32602, response?["error"]?["code"]?.Value<int>());
            Assert.Contains("Missing required argument 'goal'", response?["error"]?["message"]?.ToString());
        }

        [Fact]
        public void Handle_ResourcesList_ShouldExposeAgentPlaybook()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/list"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var resources = (JArray)json["resources"]!;
            Assert.Contains(resources, resource => resource?["uri"]?.ToString() == "genexus://kb/agent-playbook");
            Assert.Contains(resources, resource => resource?["uri"]?.ToString() == "genexus://kb/llm-playbook");
        }

        [Fact]
        public void Handle_ResourcesRead_ShouldReturnAgentPlaybookContents()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://kb/agent-playbook"}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("complete", json["resultType"]?.ToString());
            Assert.True(json["ttlMs"]!.Value<int>() > 0);
            Assert.Equal("public", json["cacheScope"]?.ToString());
            var contents = (JArray)json["contents"]!;
            var first = (JObject)contents[0]!;
            Assert.Equal("genexus://kb/agent-playbook", first["uri"]?.ToString());
            Assert.Equal("text/markdown", first["mimeType"]?.ToString());
            Assert.Contains("GeneXus Agent Playbook", first["text"]?.ToString());
            Assert.Contains("Git-friendly", first["text"]?.ToString());
        }

        [Fact]
        public void Handle_ResourcesRead_ShouldReturnLlmPlaybookContents()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://kb/llm-playbook"}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("complete", json["resultType"]?.ToString());
            Assert.True(json["ttlMs"]!.Value<int>() > 0);
            Assert.Equal("public", json["cacheScope"]?.ToString());
            var contents = (JArray)json["contents"]!;
            var first = (JObject)contents[0]!;
            Assert.Equal("genexus://kb/llm-playbook", first["uri"]?.ToString());
            Assert.Equal("text/markdown", first["mimeType"]?.ToString());
            Assert.Contains("LLM CLI+MCP Playbook", first["text"]?.ToString());
            Assert.Contains("mcp-axi/2", first["text"]?.ToString());
        }

        [Fact]
        public void Handle_PromptsGet_ShouldBuildBootstrapLlmMessage()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_bootstrap_llm","arguments":{}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var text = json["messages"]![0]!["content"]?["text"]?.ToString() ?? string.Empty;
            Assert.Contains("genexus://kb/llm-playbook", text);
            Assert.Contains("tools/list", text);
            Assert.Contains("prompts/list", text);
        }

        [Fact]
        public void Handle_PromptsGet_ShouldIncludeGoalWhenProvidedForBootstrapLlm()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"prompts/get","params":{"name":"gx_bootstrap_llm","arguments":{"goal":"Refactor invoice flow"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var text = json["messages"]![0]!["content"]?["text"]?.ToString() ?? string.Empty;
            Assert.Contains("Refactor invoice flow", text);
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestPromptNames()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"prompts/get"},"argument":{"name":"prompt","value":"gx_"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "gx_explain_object");
            Assert.Contains(values, value => value?["value"]?.ToString() == "gx_generate_tests");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestPromptArgumentAllowedValues()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/prompt","name":"gx_convert_object"},"argument":{"name":"targetLanguage","value":"Ty"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "TypeScript");
        }

        [Fact]
        public void Handle_ResourcesTemplatesList_ShouldExposeIndexesAndLogicStructureTemplates()
        {
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":"1","method":"resources/templates/list"}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var templates = (JArray)json["resourceTemplates"]!;
            Assert.Contains(templates, template => template?["uriTemplate"]?.ToString() == "genexus://objects/{name}/indexes");
            Assert.Contains(templates, template => template?["uriTemplate"]?.ToString() == "genexus://objects/{name}/logic-structure");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestStructureActions()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_structure"},"argument":{"name":"action","value":"get_"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "get_visual");
            Assert.Contains(values, value => value?["value"]?.ToString() == "get_indexes");
            Assert.Contains(values, value => value?["value"]?.ToString() == "get_logic");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestAssetActions()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_asset"},"argument":{"name":"action","value":"r"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "read");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestLifecycleResultAction()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_lifecycle"},"argument":{"name":"action","value":"res"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "result");
        }

        [Fact]
        public void Handle_CompletionComplete_ShouldSuggestPatternParts()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"completion/complete","params":{"ref":{"type":"ref/tool","name":"genexus_read"},"argument":{"name":"part","value":"Pattern"}}}"""
            );

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            var values = (JArray)json["completion"]!["values"]!;
            Assert.Contains(values, value => value?["value"]?.ToString() == "PatternInstance");
            Assert.Contains(values, value => value?["value"]?.ToString() == "PatternVirtual");
        }

        [Fact]
        public void ConvertResourceCall_ShouldMapIndexesResource()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://objects/Customer/indexes"}}"""
            );

            var result = McpRouter.ConvertResourceCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("GetVisualIndexes", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertResourceCall_ShouldMapLogicStructureResource()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://objects/Customer/logic-structure"}}"""
            );

            var result = McpRouter.ConvertResourceCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("GetLogicStructure", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertResourceCall_ShouldMapPatternInstancePartResource()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"resources/read","params":{"uri":"genexus://objects/ControleExtensaoHoras/part/PatternInstance"}}"""
            );

            var result = McpRouter.ConvertResourceCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Read", json["module"]?.ToString());
            Assert.Equal("ExtractSource", json["action"]?.ToString());
            Assert.Equal("ControleExtensaoHoras", json["target"]?.ToString());
            Assert.Equal("PatternInstance", json["part"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapCreateObjectTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_create_object","arguments":{"type":"Procedure","name":"InvoiceHelper"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("Create", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal("Procedure", json["type"]?.ToString());
        }

        // issue #50: a requested folder/module destination is forwarded to the worker (as
        // folder/destModule/parentPath) so the object actually lands there — since v2.35.0 the
        // worker creates in Root Module and then moves, reporting the outcome under `placement`,
        // instead of the earlier up-front rejection. `module` is remapped to destModule to avoid
        // colliding with the routing `module=Object` field.
        [Fact]
        public void ConvertToolCall_CreateObject_ForwardsFolderDestination()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_create","arguments":{"action":"object","type":"Procedure","name":"TesteMcpGx","folder":"eSocialSMT","module":"MyModule","parentPath":"Root Module/eSocialSMT"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("Create", json["action"]?.ToString());
            Assert.Equal("eSocialSMT", json["folder"]?.ToString());
            Assert.Equal("MyModule", json["destModule"]?.ToString());
            Assert.Equal("Root Module/eSocialSMT", json["parentPath"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_RemovedOpenKbToolMapsToNull()
        {
            // genexus_open_kb was removed in v2.3.0; it now lives in RemovedToolsRegistry
            // pointing at genexus_kb. Direct ConvertToolCall returns null (handled earlier
            // by the -32601 short-circuit in ProcessMcpRequest).
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_open_kb","arguments":{"path":"C:\\KBs\\SampleKB"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);
            Assert.Null(result);
            Assert.True(RemovedToolsRegistry.Map.ContainsKey("genexus_open_kb"));
            Assert.Equal("genexus_kb", RemovedToolsRegistry.Map["genexus_open_kb"].ReplacedBy);
        }

        [Fact]
        public void ConvertToolCall_ShouldMapExportObjectTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_export_object","arguments":{"name":"InvoiceHelper","outputPath":"exports\\InvoiceHelper.txt","part":"Rules","type":"Procedure","overwrite":true}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("ExportText", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal(@"exports\InvoiceHelper.txt", json["outputPath"]?.ToString());
            Assert.Equal("Rules", json["part"]?.ToString());
            Assert.Equal("Procedure", json["type"]?.ToString());
            Assert.True(json["overwrite"]?.Value<bool>() == true);
        }

        [Fact]
        public void ConvertToolCall_ShouldMapImportObjectTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_import_object","arguments":{"name":"InvoiceHelper","inputPath":"imports\\InvoiceHelper.txt","part":"Source","type":"Procedure"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Object", json["module"]?.ToString());
            Assert.Equal("ImportText", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal(@"imports\InvoiceHelper.txt", json["inputPath"]?.ToString());
            Assert.Equal("Source", json["part"]?.ToString());
            Assert.Equal("Procedure", json["type"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapPatchExpectedCountAndDryRun()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_edit","arguments":{"name":"InvoiceHelper","part":"Events","mode":"patch","operation":"Replace","context":"old","content":"new","expectedCount":2,"dryRun":true}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Patch", json["module"]?.ToString());
            Assert.Equal("Apply", json["action"]?.ToString());
            Assert.Equal("InvoiceHelper", json["target"]?.ToString());
            Assert.Equal(2, json["expectedCount"]?.Value<int>());
            Assert.True(json["dryRun"]?.Value<bool>() == true);
        }

        [Fact]
        public void ConvertToolCall_ShouldMapRefactorRenameVariableTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_refactor","arguments":{"action":"RenameVariable","objectName":"InvoiceProc","target":"&oldVar","newName":"&newVar"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Refactor", json["module"]?.ToString());
            Assert.Equal("RenameVariable", json["action"]?.ToString());
            Assert.Equal("InvoiceProc", json["target"]?.ToString());
            Assert.Contains("&oldVar", json["payload"]?.ToString());
            Assert.Contains("&newVar", json["payload"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapPropertiesSetTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_properties","arguments":{"action":"set","name":"Customer","propertyName":"Description","value":"Updated"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Property", json["module"]?.ToString());
            Assert.Equal("Set", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
            Assert.Equal("Description", json["propertyName"]?.ToString());
            Assert.Equal("Updated", json["value"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapFormatTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_format","arguments":{"code":"for each\ncustomerid = 1\nendfor"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Formatting", json["module"]?.ToString());
            Assert.Equal("Format", json["action"]?.ToString());
            Assert.Contains("for each", json["payload"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapAssetReadTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_asset","arguments":{"action":"read","path":"Web/Relatorios/RelControleExtensaoHoras.xlsx"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Asset", json["module"]?.ToString());
            Assert.Equal("Read", json["action"]?.ToString());
            Assert.Equal("Web/Relatorios/RelControleExtensaoHoras.xlsx", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapQueryFilters()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_query","arguments":{"query":"parentPath:\"ModuloA/Procs\" @quick","limit":5000,"typeFilter":"Folder","domainFilter":"Academic"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Search", json["module"]?.ToString());
            Assert.Equal("Query", json["action"]?.ToString());
            Assert.Equal("parentPath:\"ModuloA/Procs\" @quick", json["target"]?.ToString());
            Assert.Equal("Folder", json["typeFilter"]?.ToString());
            Assert.Equal("Academic", json["domainFilter"]?.ToString());
            Assert.Equal(5000, json["limit"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapListObjectsParentPath()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_list_objects","arguments":{"filter":"","limit":200,"offset":20,"parent":"Procs","parentPath":"ModuloA/Procs","typeFilter":"Procedure"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("List", json["module"]?.ToString());
            Assert.Equal("Objects", json["action"]?.ToString());
            Assert.Equal("Procs", json["parent"]?.ToString());
            Assert.Equal("ModuloA/Procs", json["parentPath"]?.ToString());
            Assert.Equal("Procedure", json["typeFilter"]?.ToString());
            Assert.Equal(200, json["limit"]?.Value<int>());
            Assert.Equal(20, json["offset"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapStructureGetVisualTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_structure","arguments":{"action":"get_visual","name":"Customer"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Structure", json["module"]?.ToString());
            Assert.Equal("GetVisualStructure", json["action"]?.ToString());
            Assert.Equal("Customer", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutSetPropertyTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"set_property","name":"ComissaoParecerPDF","control":"printBlock1","propertyName":"Caption","value":"Novo Caption"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("SetProperty", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal("printBlock1", json["control"]?.ToString());
            Assert.Equal("Caption", json["propertyName"]?.ToString());
            Assert.Equal("Novo Caption", json["value"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutFindControlsTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"find_controls","name":"ComissaoParecerPDF","propertyName":"Caption","query":"VALOR","limit":30}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("FindControls", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal("Caption", json["propertyName"]?.ToString());
            Assert.Equal("VALOR", json["query"]?.ToString());
            Assert.Equal(30, json["limit"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutTargetAliasWhenNameIsMissing()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"get_tree","target":"ComissaoParecerPDF","limit":30}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("GetTree", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal(30, json["limit"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutSetPropertiesTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"set_properties","name":"ComissaoParecerPDF","changes":[{"control":"printBlock1","propertyName":"Caption","value":"A"},{"control":"printBlock2","propertyName":"Caption","value":"B"}]}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("SetProperties", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal(2, json["changes"]?.Value<JArray>()?.Count);
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutInspectSurfaceTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"inspect_surface","name":"ComissaoParecerPDF"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("InspectSurface", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutScanMutatorsTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"scan_mutators","name":"ComissaoParecerPDF","limit":50}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("ScanMutators", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal(50, json["limit"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutRenamePrintBlockTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"rename_printblock","name":"ComissaoParecerPDF","currentName":"printBlock3","newName":"printBlock3Renamed"}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("RenamePrintBlock", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal("printBlock3", json["currentName"]?.ToString());
            Assert.Equal("printBlock3Renamed", json["newName"]?.ToString());
        }

        [Fact]
        public void ConvertToolCall_ShouldMapLayoutAddPrintBlockTool()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_layout","arguments":{"action":"add_printblock","name":"ComissaoParecerPDF","printBlockName":"printBlockMcp","height":60}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("Layout", json["module"]?.ToString());
            Assert.Equal("AddPrintBlock", json["action"]?.ToString());
            Assert.Equal("ComissaoParecerPDF", json["target"]?.ToString());
            Assert.Equal("printBlockMcp", json["printBlockName"]?.ToString());
            Assert.Equal(60, json["height"]?.Value<int>());
        }

        [Fact]
        public void ConvertToolCall_ShouldPreserveHistoryVersionId()
        {
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"genexus_history","arguments":{"action":"get_source","name":"DebugGravar","versionId":102}}}"""
            );

            var result = McpRouter.ConvertToolCall(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("History", json["module"]?.ToString());
            Assert.Equal("get_source", json["action"]?.ToString());
            Assert.Equal("DebugGravar", json["target"]?.ToString());
            Assert.Equal(102, json["versionId"]?.Value<int>());
        }

        [Fact]
        public void GatewayProcessLease_ShouldBuildStableInstanceKey()
        {
            var config = new Configuration
            {
                Server = new ServerConfig { HttpPort = 5000 },
                GeneXus = new GeneXusConfig { InstallationPath = @"C:\GeneXus\GX18" },
                Environment = new EnvironmentConfig { KBPath = @"C:\KBs\Sample", GX_SHADOW_PATH = @"C:\KBs\Sample\.gx_mirror" }
            };

            var key = GatewayProcessLease.BuildInstanceKey(config);

            Assert.Equal("port=5000|kb=c:\\kbs\\sample|program=c:\\genexus\\gx18|shadow=c:\\kbs\\sample\\.gx_mirror", key);
        }

        [Fact]
        public void GatewayProcessLease_ShouldMarkFreshCurrentProcessLeaseAsActive()
        {
            var lease = new GatewayLeaseRecord
            {
                InstanceKey = "test",
                ProcessId = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow
            };

            Assert.True(GatewayProcessLease.IsLeaseActive(lease));
        }

        [Fact]
        public void GatewayProcessLease_ShouldRejectStaleLease()
        {
            var lease = new GatewayLeaseRecord
            {
                InstanceKey = "test",
                ProcessId = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow - GatewayProcessLease.LeaseStaleAfter - TimeSpan.FromSeconds(1)
            };

            Assert.False(GatewayProcessLease.IsLeaseActive(lease));
        }

        [Fact]
        public void ToolHelpCatalog_HasEntriesForTrimmedTools()
        {
            string[] expected = { "genexus_query", "genexus_lifecycle", "genexus_edit", "genexus_analyze", "genexus_read" };
            foreach (var name in expected)
            {
                var help = ToolHelpCatalog.Get(name);
                Assert.False(string.IsNullOrWhiteSpace(help), $"No help text for {name}");
                Assert.True(help!.Length >= 200, $"Help for {name} should be more detailed than the trimmed description");
            }
        }

        [Fact]
        public void ToolHelpCatalog_ReturnsNullForUnknownTool()
        {
            Assert.Null(ToolHelpCatalog.Get("genexus_unknown_tool"));
        }

        [Fact]
        public void ToolHelpCatalog_ResolvesCanonicalNamesAfterUmbrellaConsolidation()
        {
            var createHelp = ToolHelpCatalog.Get("genexus_create");
            Assert.False(string.IsNullOrWhiteSpace(createHelp), "No help text for genexus_create");

            var dbHelp = ToolHelpCatalog.Get("genexus_db");
            Assert.False(string.IsNullOrWhiteSpace(dbHelp), "No help text for genexus_db");
        }

        [Fact]
        public void ToolHelpCatalog_ResolvesLegacyAliasViaCanonicalFallback()
        {
            // genexus_create_object is a pre-consolidation legacy name; TryRewriteLegacyTool
            // maps it to genexus_create (action=object), so Get should resolve it there too.
            var legacyHelp = ToolHelpCatalog.Get("genexus_create_object");
            Assert.False(string.IsNullOrWhiteSpace(legacyHelp), "No help text for legacy alias genexus_create_object");
            Assert.Equal(ToolHelpCatalog.Get("genexus_create"), legacyHelp);
        }

        [Fact]
        public void ResourcesRead_ToolHelp_ReturnsMarkdownForKnownTool()
        {
            var request = JObject.Parse(@"{
                ""method"": ""resources/read"",
                ""params"": { ""uri"": ""genexus://kb/tool-help/genexus_query"" }
            }");

            var result = McpRouter.Handle(request);
            Assert.NotNull(result);

            var json = JObject.FromObject(result!);
            var contents = (JArray)json["contents"]!;
            var first = (JObject)contents[0];
            Assert.Equal("genexus://kb/tool-help/genexus_query", first["uri"]!.ToString());
            Assert.Equal("text/markdown", first["mimeType"]!.ToString());
            Assert.Contains("Query prefixes", first["text"]!.ToString());
        }

        [Fact]
        public void ResourcesRead_ToolHelp_ReturnsNullForUnknownTool()
        {
            var request = JObject.Parse(@"{
                ""method"": ""resources/read"",
                ""params"": { ""uri"": ""genexus://kb/tool-help/genexus_does_not_exist"" }
            }");
            var result = McpRouter.Handle(request);
            Assert.Null(result);
        }

        [Fact]
        public void ResourcesTemplatesList_IncludesToolHelpTemplate()
        {
            var request = JObject.Parse(@"{ ""method"": ""resources/templates/list"" }");
            var result = McpRouter.Handle(request);
            Assert.NotNull(result);

            var json = JObject.FromObject(result!);
            var templates = (JArray)json["resourceTemplates"]!;
            Assert.Contains(templates, t =>
                string.Equals(t["uriTemplate"]?.ToString(), "genexus://kb/tool-help/{name}", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void HealthResource_IncludesSpawnAndSdkInitBlocks()
        {
            var request = JObject.Parse(@"{
                ""method"": ""resources/read"",
                ""params"": { ""uri"": ""genexus://kb/health"" }
            }");

            var result = McpRouter.Handle(request);
            Assert.NotNull(result);

            var json = JObject.FromObject(result!);
            var contents = (JArray)json["contents"]!;
            var first = (JObject)contents[0];
            var body = first["text"]!.ToString();

            Assert.Contains("spawnMs", body);
            Assert.Contains("sdkInitMs", body);
        }

        [Fact]
        public void GenexusEditAndBuild_RoutesToEditAndBuildModule()
        {
            var args = JObject.Parse(@"{
                ""name"": ""InvoiceProc"",
                ""part"": ""Source"",
                ""content"": ""@@ -1 +1 @@\n-old\n+new"",
                ""mode"": ""patch""
            }");

            var router = new GxMcp.Gateway.Routers.ObjectRouter();
            var converted = router.ConvertToolCall("genexus_edit_and_build", args);

            var json = JObject.FromObject(converted!);
            Assert.Equal("EditAndBuild", json["module"]?.ToString());
            Assert.Equal("Orchestrate",  json["action"]?.ToString());
            Assert.Equal("InvoiceProc",  json["target"]?.ToString());
            Assert.NotNull(json["args"]);
        }

        // Fix 6d — protocol version negotiation
        [Fact]
        public void Handle_Initialize_EchosKnownClientVersion()
        {
            // When the client requests a version we know about, echo it back.
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal("2025-03-26", json["protocolVersion"]?.ToString());
        }

        [Fact]
        public void Handle_Initialize_FallsBackForUnknownClientVersion()
        {
            // When the client requests a version we don't know about, use our latest.
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal(McpRouter.SupportedProtocolVersion, json["protocolVersion"]?.ToString());
        }

        [Fact]
        public void Handle_Initialize_FallsBackWhenNoVersionInParams()
        {
            // When client omits protocolVersion, use our latest.
            var request = JObject.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

            var result = McpRouter.Handle(request);

            var json = JObject.FromObject(result!);
            Assert.Equal(McpRouter.SupportedProtocolVersion, json["protocolVersion"]?.ToString());
        }

        [Fact]
        public void Handle_UnknownMethod_ReturnsNull()
        {
            // Handle() returns null for unknown methods; ProcessMcpRequest wraps it in -32601.
            var request = JObject.Parse("""{"jsonrpc":"2.0","id":1,"method":"bogus/method"}""");

            var result = McpRouter.Handle(request);

            Assert.Null(result);
        }
    }
}
