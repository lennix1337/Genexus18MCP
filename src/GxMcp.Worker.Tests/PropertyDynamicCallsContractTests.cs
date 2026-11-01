using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The default-state read of a property, which the property write and its rollback make as a dynamic call on the SDK object. The call
    /// binds at run time, so a fake object with the same member shows what the production call returns for each answer. The SDK object
    /// itself needs a Knowledge Base.
    /// </summary>
    public class PropertyDynamicCallsContractTests
    {
        public sealed class FakePropertyObject
        {
            private readonly Func<string, bool> _answer;

            public FakePropertyObject(Func<string, bool> answer)
            {
                _answer = answer;
            }

            public bool IsPropertyDefault(string name) => _answer(name);
        }

        [Fact]
        public void TheDefaultStateIsTheAnswerOfTheObject()
        {
            var fake = new FakePropertyObject(name => name == "MasterPage");

            Assert.True(PropertyService.IsPropertyDefaultSafe(fake, "MasterPage"));
            Assert.False(PropertyService.IsPropertyDefaultSafe(fake, "URLAccess"));
        }

        [Fact]
        public void AnObjectThatAnswersNotDefaultIsNotDefault()
        {
            var fake = new FakePropertyObject(name => false);

            Assert.False(PropertyService.IsPropertyDefaultSafe(fake, "MasterPage"));
        }

        [Fact]
        public void ADefaultStateThatCannotBeReadIsNotDefault()
        {
            var fake = new FakePropertyObject(name => throw new InvalidOperationException("the property cannot be read"));

            Assert.False(PropertyService.IsPropertyDefaultSafe(fake, "MasterPage"));
        }
    }
}
