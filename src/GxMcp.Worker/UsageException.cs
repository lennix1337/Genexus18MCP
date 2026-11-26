using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    public class UsageException : Exception
    {
        public string Code { get; }
        public JObject Extra { get; }

        public UsageException(string code, string message, JObject extra = null) : base(message)
        {
            Code = code;
            Extra = extra;
        }
    }
}
