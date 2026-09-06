using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public enum InspectionDepth
    {
        Summary,
        Source,
        Parts,
        Full,
        Context360
    }

    /// <summary>
    /// Deep Object Inspection Module for GeneXus KB objects.
    /// Unifies scattered query, inspection, read, and 360-degree context extraction into a cohesive module.
    /// Reduces multi-turn agent round-trips and consolidates SDK entity resolution.
    /// </summary>
    public sealed class ObjectInspectionModule
    {
        private readonly ObjectService _objectService;
        private readonly AnalyzeService _analyzeService;
        private readonly KbService _kbService;
        private readonly BatchService _batchService;
        private readonly IObjectReader _objectReader;

        public ObjectInspectionModule(ObjectService objectService, AnalyzeService analyzeService, KbService kbService, BatchService batchService = null, IObjectReader objectReader = null)
        {
            _objectService = objectService ?? throw new ArgumentNullException(nameof(objectService));
            _analyzeService = analyzeService;
            _kbService = kbService;
            _batchService = batchService;
            _objectReader = objectReader ?? new ObjectReader(objectService, batchService);
        }

        public IObjectReader Reader => _objectReader;

        public string Inspect(string target, InspectionDepth depth, JObject args = null)
        {
            if (args == null) args = new JObject();

            // Multi-object batch read support (e.g. genexus_read targets=[...])
            if (args["targets"] is JArray targetsArr && targetsArr.Count > 0)
            {
                string partFilter = args["part"]?.ToString() ?? "Source";
                var partsArr = args["parts"] as JArray;
                return _objectReader.Read(new ObjectReadRequest
                {
                    BatchTargets = targetsArr,
                    PartName = partFilter,
                    RequestedParts = partsArr?.Select(p => p.ToString())
                });
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                target = ObjectService.ResolveTargetForIdentity(target, args["guid"]?.ToString(), args["entityKey"]?.ToString(), args["path"]?.ToString());
            }

            if (string.IsNullOrWhiteSpace(target))
                return Models.McpResponse.Err(code: "MissingTarget", message: "Target object name is required for inspection.");

            string typeFilter = args["type"]?.ToString();

            switch (depth)
            {
                case InspectionDepth.Full:
                return _objectReader.Read(new ObjectReadRequest
                {
                    Target = target,
                    FullObject = true,
                    TypeFilter = typeFilter,
                    Guid = args["guid"]?.ToString(),
                    EntityKey = args["entityKey"]?.ToString(),
                    Path = args["path"]?.ToString()
                });

                case InspectionDepth.Parts:
                    var partsTok = args["parts"] as JArray;
                    var requestedParts = partsTok?.Select(p => p.ToString()) ?? Enumerable.Empty<string>();
                    return _objectReader.Read(new ObjectReadRequest
                    {
                        Target = target,
                        RequestedParts = requestedParts,
                        TypeFilter = typeFilter,
                        Guid = args["guid"]?.ToString(),
                        EntityKey = args["entityKey"]?.ToString(),
                        Path = args["path"]?.ToString()
                    });

                case InspectionDepth.Summary:
                    return _analyzeService != null
                        ? _analyzeService.Analyze(target, typeFilter)
                        : _objectReader.Read(new ObjectReadRequest
                        {
                            Target = target,
                            FullObject = true,
                            TypeFilter = typeFilter,
                            Guid = args["guid"]?.ToString(),
                            EntityKey = args["entityKey"]?.ToString(),
                            Path = args["path"]?.ToString()
                        });

                case InspectionDepth.Context360:
                    return _analyzeService != null
                        ? _analyzeService.Get360Context(target, typeFilter,
                            args["guid"]?.ToString(), args["entityKey"]?.ToString(), args["path"]?.ToString(),
                            args["maxBytes"]?.ToObject<int?>(), args["cursor"]?.ToString())
                        : _objectReader.Read(new ObjectReadRequest
                        {
                            Target = target,
                            FullObject = true,
                            TypeFilter = typeFilter,
                            Guid = args["guid"]?.ToString(),
                            EntityKey = args["entityKey"]?.ToString(),
                            Path = args["path"]?.ToString()
                        });

                case InspectionDepth.Source:
                default:
                    string part = args["part"]?.ToString();
                    int? offset = args["offset"]?.ToObject<int?>();
                    int? limit = args["limit"]?.ToObject<int?>();
                    string format = args["format"]?.ToString() ?? "mcp";
                    bool raw = args["raw"]?.ToObject<bool?>() ?? false;
                    // The public read contract is a complete object read when no
                    // part/window was requested. Keep targeted part reads lean.
                    bool completeRead = string.IsNullOrWhiteSpace(part)
                        && !offset.HasValue && !limit.HasValue;
                    return _objectReader.Read(new ObjectReadRequest
                    {
                        Target = target,
                        PartName = part,
                        Offset = offset,
                        Limit = limit,
                        ClientFormat = format,
                        Minimize = raw,
                        TypeFilter = typeFilter,
                        FullObject = completeRead,
                        Guid = args["guid"]?.ToString(),
                        EntityKey = args["entityKey"]?.ToString(),
                        Path = args["path"]?.ToString()
                    });
            }
        }
    }
}
