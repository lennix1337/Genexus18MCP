using System.Data.Common;

namespace IBM.Data.DB2.iSeries
{
    // A test-only factory: no IBM provider or global registration is needed.
    public sealed class iDB2Factory : DbProviderFactory
    {
        public static readonly iDB2Factory Instance = new iDB2Factory();
    }
}
