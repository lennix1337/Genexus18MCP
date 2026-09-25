using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public interface IVariableService
    {
        TypeResolution ResolveType(string typeSpec);
        string AddVariable(string target, string varName, string typeName = null, bool dryRun = false, int? length = null, int? decimals = null, bool? collection = null, string basedOn = null, string basedOnAttribute = null, int? dimensions = null, JArray dimensionSizes = null);
        string AddVariables(string target, JArray variables, bool dryRun = false);
        string DeleteVariable(string target, string varName, bool dryRun = false);
        string DeleteVariables(string target, IEnumerable<string> varNames);
        string ModifyVariable(string target, string varName, string newTypeName, string basedOn = null, bool dryRun = false, int? length = null, int? decimals = null, bool? collection = null, string basedOnAttribute = null, int? dimensions = null, JArray dimensionSizes = null);
        void InjectFromSource(KBObject obj, string sourceCode, SearchIndex index = null);
    }

    /// <summary>
    /// Deep authoritative service managing GeneXus Variable lifecycle, type resolution,
    /// CRUD operations, and source auto-declaration heuristics.
    /// </summary>
    public class VariableService : IVariableService
    {
        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly ITypeBindingEngine _bindingEngine;

        public VariableService(ObjectService objectService, WriteService writeService, ITypeBindingEngine bindingEngine = null)
        {
            _objectService = objectService;
            _writeService = writeService;
            _bindingEngine = bindingEngine ?? new TypeBindingEngine();
        }

        public ITypeBindingEngine BindingEngine => _bindingEngine;

        public TypeResolution ResolveType(string typeSpec)
        {
            return VariableTypeResolver.Resolve(typeSpec);
        }

        public string AddVariable(string target, string varName, string typeName = null, bool dryRun = false, int? length = null, int? decimals = null, bool? collection = null, string basedOn = null, string basedOnAttribute = null, int? dimensions = null, JArray dimensionSizes = null)
        {
            if (_writeService == null) return McpResponse.Err(code: "ServiceUnavailable", message: "WriteService not configured.");
            return _writeService.AddVariable(target, varName, typeName, dryRun, length, decimals, collection, basedOn, basedOnAttribute, dimensions, dimensionSizes);
        }

        public string AddVariables(string target, JArray variables, bool dryRun = false)
        {
            if (_writeService == null) return McpResponse.Err(code: "ServiceUnavailable", message: "WriteService not configured.");
            return _writeService.AddVariables(target, variables, dryRun);
        }

        public string DeleteVariable(string target, string varName, bool dryRun = false)
        {
            if (_writeService == null) return McpResponse.Err(code: "ServiceUnavailable", message: "WriteService not configured.");
            return _writeService.DeleteVariable(target, varName, dryRun);
        }

        public string DeleteVariables(string target, IEnumerable<string> varNames)
        {
            if (_writeService == null) return McpResponse.Err(code: "ServiceUnavailable", message: "WriteService not configured.");
            return _writeService.DeleteVariables(target, varNames);
        }

        public string ModifyVariable(string target, string varName, string newTypeName, string basedOn = null, bool dryRun = false, int? length = null, int? decimals = null, bool? collection = null, string basedOnAttribute = null, int? dimensions = null, JArray dimensionSizes = null)
        {
            if (_writeService == null) return McpResponse.Err(code: "ServiceUnavailable", message: "WriteService not configured.");
            return _writeService.ModifyVariable(target, varName, newTypeName, basedOn, dryRun, length, decimals, collection, basedOnAttribute, dimensions, dimensionSizes);
        }

        public void InjectFromSource(KBObject obj, string sourceCode, SearchIndex index = null)
        {
            if (obj == null || string.IsNullOrWhiteSpace(sourceCode)) return;
            VariableInjector.InjectVariables(obj, sourceCode, index);
        }
    }
}
