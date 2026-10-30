using System;
using System.Reflection;
using Artech.Architecture.Common.Events;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Microsoft.Build.Framework;

namespace GxMcp.Worker.Tests
{
    // The SDK broker and reflected task type are process-global. Callers must use
    // the nonparallel InProcessSdkReflection collection and restore both together.
    internal sealed class BuildAllTaskFixture : IDisposable
    {
        private static readonly FieldInfo TaskType = typeof(InProcessBuildRunner)
            .GetField("_typeBuildAll", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object _originalType = TaskType.GetValue(null);
        private readonly bool _originalSuspended = EventsService.Events.EventsSuspended;

        internal BuildAllTaskFixture(bool suspended, Type taskType = null)
        {
            TaskType.SetValue(null, taskType ?? typeof(EventTask));
            EventsService.Events.EventsSuspended = suspended;
        }

        internal bool Execute(Action<string, bool> sink = null)
        {
            sink = sink ?? ((line, error) => { });
            var execute = typeof(InProcessBuildRunner).GetMethod("ExecuteBuildAll", BindingFlags.Static | BindingFlags.NonPublic);
            return (bool)execute.Invoke(null, new object[] { new object(), new InProcessBuildEngine(sink), sink });
        }

        public void Dispose()
        {
            EventsService.Events.EventsSuspended = _originalSuspended;
            TaskType.SetValue(null, _originalType);
            EventTask.ExecuteCallback = null;
        }

        public sealed class EventTask
        {
            public static Func<object> ExecuteCallback;
            public object KB { get; set; }
            public bool FailIfReorg { get; set; }
            public bool DoNotExecuteReorg { get; set; }
            public IBuildEngine BuildEngine { get; set; }
            public object Execute() => ExecuteCallback();
        }

        public sealed class ConstructorFailureTask
        {
            public ConstructorFailureTask() => throw new InvalidOperationException("synthetic root cause");
        }

        public sealed class MissingExecuteTask
        {
            public bool FailIfReorg { get; set; }
            public bool DoNotExecuteReorg { get; set; }
        }
    }
}
