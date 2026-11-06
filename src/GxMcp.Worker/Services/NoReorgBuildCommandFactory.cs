using System;
using Artech.Common.Framework.Commands;
using Artech.Genexus.Common.Commands;

namespace GxMcp.Worker.Services
{
    // BuildOne hardcodes its BuildOptions and exposes no reorg policy. Guard
    // command construction instead, without replacing its target/scope logic.
    internal sealed class NoReorgBuildCommandFactory : BuildCommandFactory, IDisposable
    {
        private readonly BuildCommandFactory _previous;
        private readonly BuildCommandFactory _inner;

        internal NoReorgBuildCommandFactory()
        {
            _previous = HasFactory ? Factory : null;
            _inner = _previous ?? new BuildCommandFactory();
            Factory = this;
        }

        public override IGxCommand Create(BuildArgs args)
        {
#if GXMCP_SDK_EV3
            // Without FailIfReorg the SDK would build past a pending reorganization;
            // refusing keeps the no-reorg guarantee instead of weakening it.
            throw new InvalidOperationException("GeneXus X Evolution 3 has no BuildOptions.FailIfReorg; the no-reorganization build guard cannot be enforced, so the build was not started.");
#else
            args.Options |= BuildOptions.FailIfReorg | BuildOptions.DoNotExecuteReorg;
            return _inner.Create(args);
#endif
        }

        public void Dispose()
        {
            Factory = _previous;
        }
    }
}
