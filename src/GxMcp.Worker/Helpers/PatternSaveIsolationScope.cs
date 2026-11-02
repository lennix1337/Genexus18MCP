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
        private readonly ISaveFlags _flags;
        private readonly Action _poison;
        private readonly bool _before;
        private bool _disposed;

        internal PatternSaveIsolationScope(KBObject obj)
            : this(ObjectSaveFlags.Of(obj ?? throw new ArgumentNullException(nameof(obj))))
        {
        }

        // The flags are injected so the scope is testable without a Knowledge Base. A flag the SDK refuses to set or to restore
        // poisons the SDK event suppression, as the object scope always did.
        internal PatternSaveIsolationScope(ISaveFlags flags, Action poison = null)
        {
            _flags = flags ?? throw new ArgumentNullException(nameof(flags));
            _poison = poison ?? SdkEventSuppressionScope.Poison;
            try
            {
                _before = _flags.Get(SkipApplyPatternFlag);
                _flags.Set(SkipApplyPatternFlag, true);
                if (!_flags.Get(SkipApplyPatternFlag))
                    throw new InvalidOperationException("SDK did not accept SkipApplyPattern=true.");
            }
            catch
            {
                _poison();
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
                _flags.Set(SkipApplyPatternFlag, _before);
                if (_flags.Get(SkipApplyPatternFlag) != _before)
                    throw new InvalidOperationException("SDK did not restore SkipApplyPattern.");
                Restored = true;
            }
            catch
            {
                _poison();
                throw;
            }
        }
    }

    /// <summary>The save flags of an object, read and written by name.</summary>
    internal interface ISaveFlags
    {
        bool Get(string name);
        void Set(string name, bool value);
    }

    /// <summary>The save flags of an SDK object, through its Flags.</summary>
    internal static class ObjectSaveFlags
    {
        internal static ISaveFlags Of(KBObject obj)
            => new DelegateSaveFlags(name => obj.Flags.Get<bool>(name), (name, value) => obj.Flags[name] = value);

        private sealed class DelegateSaveFlags : ISaveFlags
        {
            private readonly Func<string, bool> _get;
            private readonly Action<string, bool> _set;

            internal DelegateSaveFlags(Func<string, bool> get, Action<string, bool> set)
            {
                _get = get;
                _set = set;
            }

            public bool Get(string name) => _get(name);

            public void Set(string name, bool value) => _set(name, value);
        }
    }
}
