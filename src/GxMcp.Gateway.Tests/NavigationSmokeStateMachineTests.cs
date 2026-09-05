using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class NavigationSmokeStateMachineTests
    {
        [Fact]
        public void IndexNotReadyIsRetryable()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'code':'IndexNotReady'}"), isError: false, offset: 0);

            Assert.Equal(NavigationListDecisionKind.Retry, decision.Kind);
        }

        [Fact]
        public void PartialEmptyPageIsRetryable()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'status':'partial','results':[],'hasMore':false}"), isError: false, offset: 0);

            Assert.Equal(NavigationListDecisionKind.Retry, decision.Kind);
        }

        [Fact]
        public void ExplicitEmptyTerminalPageIsExhausted()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'results':[],'hasMore':false}"), isError: false, offset: 100);

            Assert.Equal(NavigationListDecisionKind.Exhausted, decision.Kind);
        }

        [Fact]
        public void HasMoreWithoutForwardOffsetIsRetryable()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'results':[{'name':'P'}],'hasMore':true}"), isError: false, offset: 100);

            Assert.Equal(NavigationListDecisionKind.Retry, decision.Kind);
        }

        [Fact]
        public void HasMoreWithForwardOffsetProcessesThePage()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'results':[{'name':'P'}],'hasMore':true,'nextOffset':150}"), isError: false, offset: 100);

            Assert.Equal(NavigationListDecisionKind.Process, decision.Kind);
            Assert.Equal(150, decision.NextOffset);
        }

        [Fact]
        public void MissingHasMoreDoesNotPretendTheListIsExhausted()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'results':[{'name':'P'}]}"), isError: false, offset: 0);

            Assert.Equal(NavigationListDecisionKind.Retry, decision.Kind);
        }

        [Fact]
        public void NonRetryableErrorFailsClearly()
        {
            var decision = NavigationListStateMachine.Evaluate(
                JObject.Parse("{'code':'PermissionDenied','message':'no access'}"), isError: true, offset: 0);

            Assert.Equal(NavigationListDecisionKind.Fail, decision.Kind);
        }
    }
}
