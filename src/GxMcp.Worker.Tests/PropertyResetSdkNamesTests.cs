using System.Reflection;
using Artech.Architecture.Common.Objects;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The rollback calls three SDK members by name, through dynamic: they are the public members a property can be reset
    /// with. These tests read them from the SDK assembly the Worker is compiled against, so a rename in the SDK fails here
    /// instead of making every reset refuse at run time. CanResetProperty belongs to the SDK's internal property manager and
    /// is not on the object, so the rollback must not ask for it.
    /// </summary>
    public class PropertyResetSdkNamesTests
    {
        [Theory]
        [InlineData("IsPropertyDefault")]
        [InlineData("CanResetValue")]
        [InlineData("ResetProperty")]
        public void TheKBObjectDeclaresThePublicResetMemberThatTheRollbackCalls(string name)
        {
            MethodInfo method = typeof(KBObject).GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);

            Assert.NotNull(method);
        }

        [Fact]
        public void TheKBObjectHasNoPublicCanResetPropertyForTheRollbackToCall()
        {
            MethodInfo method = typeof(KBObject).GetMethod("CanResetProperty", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);

            Assert.Null(method);
        }
    }
}
