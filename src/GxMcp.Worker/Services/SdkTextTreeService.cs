using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// SDK-backed text-tree exchange. The projection is intentionally implemented in this
    /// repository: the Worker never loads GeneXus.KBasText, FileSystemKnowledgeManager or
    /// any GX4A assembly. All KB reads and writes stay behind ObjectService.
    /// </summary>
    public sealed partial class SdkTextTreeService
    {
        internal const string ManifestFileName = "_gxmcp-sdk-text-manifest.json";

        private readonly TextTreeSelectionService _selector;
        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;
        // Part reads cross this delegate (ObjectService.ReadObjectPartText in production). Unit tests
        // replace it at this boundary to check the identity every read receives.
        private readonly Func<string, string, string, string, string, string, string> _readPartText;
        // Multi-part reads cross this delegate in the same way (ObjectService.ReadObjectSourceParts in production).
        private readonly Func<string, IEnumerable<string>, string, string, string, string, string> _readPartsText;
        // An explicit target that the index does not hold is looked up here (the SDK in production).
        private readonly Func<TextSelector, SearchIndex.IndexEntry> _sdkEntryLookup;
        // The properties of a WorkWithPlus template, read when the object has no text part (ObjectService.ReadWwpTemplateProperties).
        private readonly Func<string, string, string, string, string, string> _readTemplateProperties;

        internal SdkTextTreeService(
            TextTreeSelectionService selector,
            ObjectService objectService,
            IndexCacheService indexCacheService)
            : this(selector, objectService, indexCacheService, null, null, null)
        {
        }

        internal SdkTextTreeService(
            TextTreeSelectionService selector,
            ObjectService objectService,
            IndexCacheService indexCacheService,
            Func<string, string, string, string, string, string, string> readPartText)
            : this(selector, objectService, indexCacheService, readPartText, null, null)
        {
        }

        internal SdkTextTreeService(
            TextTreeSelectionService selector,
            ObjectService objectService,
            IndexCacheService indexCacheService,
            Func<string, string, string, string, string, string, string> readPartText,
            Func<TextSelector, SearchIndex.IndexEntry> sdkEntryLookup)
            : this(selector, objectService, indexCacheService, readPartText, sdkEntryLookup, null)
        {
        }

        internal SdkTextTreeService(
            TextTreeSelectionService selector,
            ObjectService objectService,
            IndexCacheService indexCacheService,
            Func<string, string, string, string, string, string, string> readPartText,
            Func<TextSelector, SearchIndex.IndexEntry> sdkEntryLookup,
            Func<string, IEnumerable<string>, string, string, string, string, string> readPartsText,
            Func<string, string, string, string, string, string> readTemplateProperties = null)
        {
            _selector = selector;
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _readPartText = readPartText
                ?? ((target, part, type, guid, entityKey, path) => _objectService.ReadObjectPartText(target, part, type, guid, entityKey, path));
            _readPartsText = readPartsText
                ?? ((target, parts, type, guid, entityKey, path) => _objectService.ReadObjectSourceParts(target, parts, type, guid, entityKey, path));
            _sdkEntryLookup = sdkEntryLookup ?? (explicitTarget => LookupSdkEntry(explicitTarget));
            _readTemplateProperties = readTemplateProperties
                ?? ((target, type, guid, entityKey, path) => _objectService.ReadWwpTemplateProperties(target, type, guid, entityKey, path));
        }

        internal static bool IsNativeFormat(JObject args)
        {
            string format = args?["format"]?.ToString();
            return string.Equals(format, "native", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "sdk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "sdk-tree", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "src-ref", StringComparison.OrdinalIgnoreCase);
        }

        public string Execute(string action, string target, JObject args, CancellationToken ct)
        {
            switch ((action ?? string.Empty).ToLowerInvariant())
            {
                case "exporttextbatch":
                case "export_kb_to_text":
                    return Export(target, args ?? new JObject(), ct);
                case "importtextbatch":
                case "import_text_to_kb":
                    return Import(target, args ?? new JObject(), ct);
                case "validatetextbatch":
                case "validate_kb_text_files":
                    return ObjectTextService.ValidateInMemory(args ?? new JObject(), ct);
                case "list_text_in_memory":
                case "list_text":
                    return ObjectTextService.ListInMemory(args ?? new JObject(), ct);
                default:
                    return McpResponse.Err(
                        code: "UnknownSdkTextTreeAction",
                        message: "Unknown SDK text-tree action: " + action,
                        hint: "Use export_kb_to_text, import_text_to_kb, validate_kb_text_files or list_text_in_memory.");
            }
        }

    }
}
