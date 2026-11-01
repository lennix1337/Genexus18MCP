using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XmlReader = System.Xml.XmlReader;
using XmlReaderSettings = System.Xml.XmlReaderSettings;
using DtdProcessing = System.Xml.DtdProcessing;
using XmlException = System.Xml.XmlException;

namespace GxMcp.Worker.Services
{
    public sealed partial class ObjectTextService
    {
        private string Export(string target, JObject args, CancellationToken ct)
            => RunLegacyExport(target, args ?? new JObject(), ct);

        private string Import(string target, JObject args, CancellationToken ct)
            => RunLegacyImport(target, args ?? new JObject(), ct);

        private string Validate(string target, JObject args, CancellationToken ct)
            => RunLegacyValidation(target, args ?? new JObject(), ct);

        private string Delete(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("delete", null, new JArray(), 0, 0, 0);
            if (!TextBatchOptions.TryParse(args, false, false, false, true, false, false, true, out TextBatchOptions options, out string optionError))
                return McpResponse.Err(code: "InvalidTextOperationOption", message: optionError);
            bool dryRun = options.DryRun;
            bool confirm = options.Confirm;
            if (!dryRun && !confirm)
                return McpResponse.Err(code: "ConfirmRequired", message: "delete_kb_objects requires confirm=true.", hint: "Run dryRun=true first, then repeat with confirm=true to delete.");

            bool stopOnError = options.StopOnError;
            bool includeChildren = options.IncludeChildren;
            int skip = options.Skip;

            List<SearchIndex.IndexEntry> entries;
            string selectionError;
            if (!TrySelectEntries(target, args, allowAll: false, out entries, out selectionError, includeChildren))
                return McpResponse.Err(code: "ObjectSelectionFailed", message: selectionError, target: target);
            if (entries.Count == 0)
                return McpResponse.Err(code: "NoObjectsMatched", message: "No KB objects matched the delete selector.", hint: "Use targets[] or name with the object's Type:Name; a GUID or EntityKey selects nothing here (export by identity with format=native).");

            var results = new JArray();
            int succeeded = 0;
            int failed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (ct.IsCancellationRequested)
                    return BuildCancelled("delete", null, results, succeeded, failed, entries.Count);

                if (i < skip)
                {
                    results.Add(new JObject
                    {
                        ["name"] = entries[i].Name,
                        ["type"] = entries[i].Type,
                        ["status"] = "skipped",
                        ["reason"] = "skip"
                    });
                    continue;
                }

                var item = entries[i];
                JObject parsed;
                try
                {
                    parsed = ParseResult(_objectService.DeleteObject(
                        item.Type + ":" + item.Name, item.Type, confirm, dryRun));
                }
                catch (Exception ex)
                {
                    parsed = new JObject
                    {
                        ["status"] = "error",
                        ["code"] = "ObjectTextDeleteFailed",
                        ["message"] = ex.Message
                    };
                }
                results.Add(BuildItemResult(item, null, parsed));
                if (IsSuccess(parsed)) succeeded++;
                else
                {
                    failed++;
                    if (stopOnError) break;
                }
            }

            var result = BuildAggregateResult("delete", null, results, entries.Count, succeeded, failed);
            result["dryRun"] = dryRun;
            result["includeChildren"] = includeChildren;
            result["skipped"] = Math.Min(skip, entries.Count);
            result["remaining"] = Math.Max(0, entries.Count - Math.Min(skip, entries.Count) - succeeded - failed);
            result["stoppedOnError"] = stopOnError && failed > 0;
            return failed > 0
                ? McpResponse.Partial(null, "ObjectTextDeletePartial", result)
                : McpResponse.Ok(code: dryRun ? "ObjectTextDeleteDryRun" : "ObjectTextDeleteCompleted", result: result);
        }

    }
}
