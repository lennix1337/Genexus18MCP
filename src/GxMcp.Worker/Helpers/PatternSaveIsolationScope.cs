using System;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    // PatternVirtualPart is a virtual SDK part. Its normal after-save handler
    // may apply a pattern, so a complete Events save must explicitly suppress
    // that handler and prove the flag was restored afterwards.
    internal sealed class PatternSaveIsolationScope : IDisposable
    {
        internal const string SkipApplyPatternFlag = "SkipApplyPattern";
        private readonly KBObject _object;
        private readonly bool _before;
        private bool _disposed;

        internal PatternSaveIsolationScope(KBObject obj)
        {
            _object = obj ?? throw new ArgumentNullException(nameof(obj));
            try
            {
                _before = _object.Flags.Get<bool>(SkipApplyPatternFlag);
                _object.Flags[SkipApplyPatternFlag] = true;
                if (!_object.Flags.Get<bool>(SkipApplyPatternFlag))
                    throw new InvalidOperationException("SDK did not accept SkipApplyPattern=true.");
            }
            catch
            {
                SdkEventSuppressionScope.Poison();
                throw;
            }
        }

        internal bool WasAlreadySet => _before;
        internal bool Restored { get; private set; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _object.Flags[SkipApplyPatternFlag] = _before;
                if (_object.Flags.Get<bool>(SkipApplyPatternFlag) != _before)
                    throw new InvalidOperationException("SDK did not restore SkipApplyPattern.");
                Restored = true;
            }
            catch
            {
                SdkEventSuppressionScope.Poison();
                throw;
            }
        }
    }
}
