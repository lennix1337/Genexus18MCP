using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Both DispatcherException envelopes tell the caller to "inspect worker logs for the
    /// full exception chain", but neither catch wrote anything, so the chain was lost.
    /// Building a CommandDispatcher needs the SDK, so this pins the source shape: each
    /// catch logs before it returns the envelope.
    /// </summary>
    public class DispatcherExceptionLoggingTests
    {
        [Theory]
        [InlineData("private string DispatchInternal(string line)")]
        [InlineData("private string DispatchInternal(JObject request)")]
        public void DispatcherExceptionEnvelopeIsPrecededByAFullLog(string declaration)
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs");
            string body = SourceAssert.MethodBody(source, declaration);

            int catchAt = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(catchAt >= 0, "no catch (Exception ex) in " + declaration);
            string handler = body.Substring(catchAt);

            int logAt = handler.IndexOf("Logger.Error(", StringComparison.Ordinal);
            int envelopeAt = handler.IndexOf("code: \"DispatcherException\"", StringComparison.Ordinal);
            Assert.True(envelopeAt > 0, "the DispatcherException envelope moved out of the last catch");
            Assert.True(logAt > 0 && logAt < envelopeAt, "the catch returns DispatcherException without logging it");
        }
    }
}
