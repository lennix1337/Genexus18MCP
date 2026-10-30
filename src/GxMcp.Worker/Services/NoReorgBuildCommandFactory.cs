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
            args.Options |= BuildOptions.FailIfReorg | BuildOptions.DoNotExecuteReorg;
            return _inner.Create(args);
        }

        public void Dispose()
        {
            Factory = _previous;
        }
    }
}
