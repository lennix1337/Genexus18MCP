using System;
using Artech.Common.Diagnostics;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The GeneXus Trial licence caps the objects of a Knowledge Base; the SDK raises
    /// <see cref="MaxObjCountException"/> from <c>KBObject.Save()</c> once the cap is reached.
    /// Retrying, renaming or changing the object type cannot help: only deleting objects does.
    /// </summary>
    public static class TrialObjectLimit
    {
        public static MaxObjCountException Find(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is MaxObjCountException limit) return limit;
            }
            return null;
        }

        public static string Response(string target, MaxObjCountException limit)
        {
            return McpResponse.Err(
                code: "TrialObjectLimitReached",
                message: limit.Message,
                target: target,
                hint: "The GeneXus Trial licence caps the objects in a Knowledge Base. Nothing was saved, and renaming or changing the type does not help: delete objects (genexus_delete_object) or use a licensed GeneXus.",
                errorExtra: new JObject { ["maxCount"] = limit.MaxCount },
                extra: new JObject { ["persisted"] = false, ["maxCount"] = limit.MaxCount },
                retryable: false);
        }
    }
}
