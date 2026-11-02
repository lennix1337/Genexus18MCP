using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The save-time suppression of the pattern: the flag is set for the save and given back afterwards, also when the save
    /// throws. A flag the SDK does not take poisons the suppression. The production scope runs here over an in-memory flag bag,
    /// so these tests need no Knowledge Base.
    /// </summary>
    public class PatternSaveIsolationScopeTests
    {
        private const string Flag = PatternSaveIsolationScope.SkipApplyPatternFlag;

        private sealed class MemoryFlags : ISaveFlags
        {
            internal readonly Dictionary<string, bool> Values = new Dictionary<string, bool>(StringComparer.Ordinal);
            internal bool AcceptSets = true;

            public bool Get(string name) => Values.TryGetValue(name, out bool value) && value;

            public void Set(string name, bool value)
            {
                if (AcceptSets) Values[name] = value;
            }
        }

        [Fact]
        public void TheFlagIsSetForTheSaveAndGivenBackAfterwards()
        {
            var flags = new MemoryFlags();
            int poisoned = 0;

            using (var scope = new PatternSaveIsolationScope(flags, () => poisoned++))
            {
                Assert.True(flags.Get(Flag));
                Assert.False(scope.WasAlreadySet);
            }

            Assert.False(flags.Get(Flag));
            Assert.Equal(0, poisoned);
        }

        [Fact]
        public void AFlagAlreadySetStaysSetAfterTheSave()
        {
            var flags = new MemoryFlags();
            flags.Values[Flag] = true;

            using (var scope = new PatternSaveIsolationScope(flags, () => { }))
            {
                Assert.True(scope.WasAlreadySet);
            }

            Assert.True(flags.Get(Flag));
        }

        [Fact]
        public void TheFlagIsGivenBackWhenTheSaveThrows()
        {
            var flags = new MemoryFlags();

            // The delegate is typed as Action: a block that only throws also fits the obsolete Func<Task> overload.
            Exception failure = Record.Exception(new Action(() =>
            {
                using (new PatternSaveIsolationScope(flags, () => { }))
                {
                    throw new InvalidOperationException("save failed");
                }
            }));

            Assert.IsType<InvalidOperationException>(failure);
            Assert.False(flags.Get(Flag));
        }

        [Fact]
        public void AFlagTheSdkRefusesToSetPoisonsTheSuppression()
        {
            var flags = new MemoryFlags { AcceptSets = false };
            int poisoned = 0;

            Assert.Throws<InvalidOperationException>(() => { _ = new PatternSaveIsolationScope(flags, () => poisoned++); });

            Assert.Equal(1, poisoned);
        }

        [Fact]
        public void AFlagTheSdkRefusesToRestorePoisonsTheSuppression()
        {
            var flags = new MemoryFlags();
            int poisoned = 0;
            var scope = new PatternSaveIsolationScope(flags, () => poisoned++);
            flags.AcceptSets = false;

            Assert.Throws<InvalidOperationException>(() => scope.Dispose());

            Assert.Equal(1, poisoned);
        }
    }
}
