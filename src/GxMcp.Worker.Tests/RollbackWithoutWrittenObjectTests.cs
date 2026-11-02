using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The batch rollback when the object it wrote is not there to read back: the written object is null, and the failure comes back
    /// as an envelope instead of escaping. The service is created without its constructor, so the first read of the object fails and
    /// the rollback's catch is what answers. Nothing is written, so the test needs no Knowledge Base.
    /// </summary>
    public class RollbackWithoutWrittenObjectTests
    {
        [Fact]
        public void ARollbackWithoutTheWrittenObjectReturnsAFailureEnvelopeAndDoesNotThrow()
        {
            object service = FormatterServices.GetUninitializedObject(typeof(PropertyService));
            object rollback = NewRollbackContext(new[] { "URLAccess" });
            MethodInfo rollbackMethod = typeof(PropertyService).GetMethod("RollbackPropertySnapshot", BindingFlags.NonPublic | BindingFlags.Instance);

            string response = (string)rollbackMethod.Invoke(service, new object[] { "Target", null, null, rollback, "Reason.", null });

            Assert.Contains("PropertyRollbackFailed", response);
            Assert.Contains("Snapshot restoration failed", response);
        }

        private static object NewRollbackContext(string[] requestedNames)
        {
            Type contextType = typeof(PropertyService).GetNestedType("PropertyRollbackContext", BindingFlags.NonPublic);
            object context = Activator.CreateInstance(contextType, true);
            SetProperty(context, "RequestedNames", requestedNames);
            SetProperty(context, "PreviousValues", new Dictionary<string, string> { ["URLAccess"] = "Yes" });
            SetProperty(context, "NativeNames", new Dictionary<string, string> { ["URLAccess"] = "URLAccess" });
            SetProperty(context, "WereDefault", new HashSet<string>());
            return context;
        }

        // The members of the rollback context are internal setters, so they are set through their setter whatever its access.
        private static void SetProperty(object target, string name, object value)
            => target.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .GetSetMethod(true).Invoke(target, new[] { value });
    }
}
