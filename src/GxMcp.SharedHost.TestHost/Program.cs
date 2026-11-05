using System;
using GxMcp.Worker;

namespace GxMcp.SharedHost.TestHost
{
    // Test-only entry point. With --shared-host it runs the real broker (the same
    // SharedWorkerHost.Run the Worker's Main calls before any SDK code); otherwise it is
    // the scripted fake child the broker starts as its Worker. The harness passes this
    // executable as the Worker executable, so broker and child are one binary, as in
    // production.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (SharedWorkerHost.IsRequested(args))
                return SharedWorkerHost.Run(args);

            return FakeChild.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
        }
    }
}
