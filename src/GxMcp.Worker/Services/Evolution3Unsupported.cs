using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Services whose GeneXus SDK surface does not exist in GeneXus X Evolution 3 (10.3).
    /// The evolution3 build profile compiles these in place of the real services, so each
    /// tool answers with a typed refusal before touching the Knowledge Base.
    /// </summary>
    internal static class Evolution3Unsupported
    {
        internal const string Code = "UNSUPPORTED_IN_GENEXUS_VERSION";

        internal static string Refuse(string tool, string action, string missing)
        {
            return McpResponse.Err(
                code: Code,
                message: tool + " needs " + missing + ", which GeneXus X Evolution 3 does not have.",
                target: string.IsNullOrWhiteSpace(action) ? tool : action,
                hint: "Nothing was read or changed. Use a GeneXus 16, 17 or 18 installation for this tool.",
                errorExtra: new JObject { ["genexusVersion"] = "X Evolution 3", ["major"] = "10.3" },
                extra: new JObject { ["persisted"] = false },
                retryable: false);
        }
    }

    public class ModuleService
    {
        public ModuleService(KbService kb, ObjectService objects, IndexCacheService index = null)
        {
        }

        public string Run(JObject args) => Evolution3Unsupported.Refuse(
            "genexus_module", args?["action"]?.ToString(), "the module package manager (OPC packages)");
    }

}
