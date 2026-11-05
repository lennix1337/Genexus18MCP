using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Common.SharedHost;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    /// <summary>
    /// Requirement 4 criteria 4 to 6: the Operation_Class table covers every Worker
    /// command the dispatcher registers, with exactly one read-or-write value each,
    /// unknown commands are treated as write, and the known mutating and pure-read
    /// commands land on the right side.
    /// </summary>
    /// <remarks>
    /// The coverage check reads <c>CommandDispatcher.cs</c> as text rather than
    /// reflecting over the Worker assembly: the Worker targets net48 and this project
    /// cannot reference it. The scan takes the methods from <c>BuildCommandTable</c>
    /// and the actions from the literal comparisons on the <c>action</c> parameter in
    /// each handler body and in the dispatcher's <c>method == "m" &amp;&amp; action == "A"</c>
    /// guards. A method whose handler compares no action literal (it ignores the
    /// action or hands it to a service) is expected under the empty action.
    /// </remarks>
    public class WorkerCommandCatalogTests
    {
        private const string TableDeclaration = "private Dictionary<string, CommandHandler> BuildCommandTable()";

        private static readonly Regex RegistrationPattern = new Regex(
            "\\[\"(?<method>[^\"]+)\"\\]\\s*=\\s*(?<handler>Handle_\\w+\\b)?",
            RegexOptions.CultureInvariant);

        // Every comparison form the dispatcher uses against the action parameter.
        private static readonly Regex ActionLiteralPattern = new Regex(
            "\\baction\\s*==\\s*\"(?<a>[^\"]*)\""
            + "|\"(?<a>[^\"]*)\"\\s*==\\s*action\\b"
            + "|string\\.Equals\\(\\s*action\\s*,\\s*\"(?<a>[^\"]*)\""
            + "|\\baction\\s*\\.\\s*Equals\\(\\s*\"(?<a>[^\"]*)\"",
            RegexOptions.CultureInvariant);

        private static readonly Regex MethodGuardPattern = new Regex(
            "\\bmethod\\s*==\\s*\"(?<m>[^\"]+)\"\\s*&&(?<rest>[^\\n]*)",
            RegexOptions.CultureInvariant);

        [Fact]
        public void Scan_FindsTheDispatcherCommandTable()
        {
            // Guards the scan itself: a regex that silently matched nothing would make
            // the coverage test below pass against an empty expectation.
            var commands = RegisteredCommands();
            var methods = commands.Select(c => c.Method).Distinct(StringComparer.Ordinal).ToList();

            Assert.True(methods.Count >= 100, "expected the full dispatcher table, found " + methods.Count + " methods");
            Assert.Contains(("ping", ""), commands);
            Assert.Contains(("object", "Create"), commands);
            Assert.Contains(("object", "Delete"), commands);
            Assert.Contains(("layout", "RenamePrintBlock"), commands);
            Assert.Contains(("build", "Status"), commands);
            Assert.Contains(("dfd", ""), commands);
        }

        [Fact]
        public void Scan_DispatcherHasNoActionSwitchTheScanCannotSee()
        {
            // A `switch (action)` would register actions through `case` labels the scan
            // does not read. Fail loudly so the scan is extended with it.
            string source = DispatcherSource();
            Assert.DoesNotMatch(new Regex("switch\\s*\\(\\s*action\\s*\\)"), source);
        }

        [Fact]
        public void EveryRegisteredMethodAndAction_HasExactlyOneEntry()
        {
            var entries = WorkerCommandCatalog.Entries;
            var problems = new List<string>();

            foreach (var (method, action) in RegisteredCommands())
            {
                int count = entries.Count(e =>
                    string.Equals(e.Method, method, StringComparison.Ordinal)
                    && string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase));

                if (count != 1)
                    problems.Add(method + "/" + (action.Length == 0 ? "<no action>" : action) + ": " + count + " entries");
            }

            Assert.True(problems.Count == 0,
                "Operation_Class table does not match CommandDispatcher.cs:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void EveryEntry_HoldsOneDefinedValueUnderAUniqueLowerCaseKey()
        {
            var entries = WorkerCommandCatalog.Entries;
            Assert.NotEmpty(entries);

            foreach (var entry in entries)
            {
                Assert.False(string.IsNullOrEmpty(entry.Method), "entry with an empty method");
                Assert.Equal(entry.Method.ToLowerInvariant(), entry.Method);
                Assert.NotNull(entry.Action);
                Assert.True(Enum.IsDefined(typeof(OperationClass), entry.Class),
                    entry.Method + "/" + entry.Action + " has no defined Operation_Class value");
            }

            var duplicates = entries
                .GroupBy(e => e.Method + "\u0000" + e.Action.ToLowerInvariant(), StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.First().Method + "/" + g.First().Action)
                .ToList();
            Assert.True(duplicates.Count == 0, "duplicate keys: " + string.Join(", ", duplicates));
        }

        [Fact]
        public void EveryEntry_NamesAMethodTheDispatcherRegisters()
        {
            var registered = new HashSet<string>(
                RegisteredCommands().Select(c => c.Method), StringComparer.Ordinal);

            var stale = WorkerCommandCatalog.Entries
                .Where(e => !registered.Contains(e.Method))
                .Select(e => e.Method + "/" + e.Action)
                .ToList();

            Assert.True(stale.Count == 0, "entries for unregistered methods: " + string.Join(", ", stale));
        }

        [Fact]
        public void Classify_AgreesWithEveryEntry()
        {
            foreach (var entry in WorkerCommandCatalog.Entries)
                Assert.Equal(entry.Class, WorkerCommandCatalog.Classify(entry.Method, entry.Action));
        }

        [Theory]
        [InlineData("nosuchmethod", "")]
        [InlineData("nosuchmethod", "Read")]
        [InlineData("object", "NoSuchAction")]
        [InlineData("list", "NoSuchAction")]
        [InlineData("", "")]
        [InlineData(null, null)]
        [InlineData(null, "Read")]
        public void Classify_UnknownCommand_IsWrite(string? method, string? action)
        {
            Assert.Equal(OperationClass.Write, WorkerCommandCatalog.Classify(method, action));
        }

        [Theory]
        // create
        [InlineData("object", "Create")]
        [InlineData("object", "SaveAs")]
        [InlineData("atomiccreate", "")]
        [InlineData("popup", "Create")]
        [InlineData("structure", "CreateIndex")]
        [InlineData("layout", "AddPrintBlock")]
        // modify
        [InlineData("write", "ModifyVariable")]
        [InlineData("write", "AddVariable")]
        [InlineData("property", "Set")]
        [InlineData("property", "SetMultiple")]
        [InlineData("patch", "Apply")]
        [InlineData("batch", "BatchEdit")]
        [InlineData("object", "ImportText")]
        [InlineData("layout", "SetProperty")]
        [InlineData("kb", "SetStartupObject")]
        [InlineData("kb", "SetActiveEnvironment")]
        // rename
        [InlineData("layout", "RenamePrintBlock")]
        [InlineData("refactor", "")]
        // delete
        [InlineData("object", "Delete")]
        [InlineData("object", "DeleteTextBatch")]
        [InlineData("write", "DeleteVariable")]
        [InlineData("structure", "DropIndex")]
        [InlineData("structure", "RemoveAttribute")]
        [InlineData("layout", "DeletePrintBlock")]
        public void Classify_KnownMutatingCommand_IsWrite(string method, string action)
        {
            Assert.Equal(OperationClass.Write, WorkerCommandCatalog.Classify(method, action));
        }

        [Theory]
        [InlineData("ping", "")]
        [InlineData("health", "")]
        [InlineData("system", "GetSdkBusyStatus")]
        [InlineData("list", "Objects")]
        [InlineData("search", "Query")]
        [InlineData("search", "SearchSource")]
        [InlineData("read", "ExtractSource")]
        [InlineData("read", "ExtractFullObject")]
        [InlineData("object", "Read")]
        [InlineData("property", "Get")]
        [InlineData("kb", "GetIndexStatus")]
        [InlineData("kb", "GetIndexState")]
        [InlineData("layout", "GetTree")]
        [InlineData("structure", "GetLogicStructure")]
        [InlineData("analyze", "GetCodeMetrics")]
        [InlineData("build", "Status")]
        [InlineData("kbversion", "ActiveModel")]
        public void Classify_PureRead_IsRead(string method, string action)
        {
            Assert.Equal(OperationClass.Read, WorkerCommandCatalog.Classify(method, action));
        }

        [Fact]
        public void Classify_NullAndEmptyAction_MeanNoAction()
        {
            Assert.Equal(OperationClass.Read, WorkerCommandCatalog.Classify("ping", null));
            Assert.Equal(OperationClass.Read, WorkerCommandCatalog.Classify("ping", ""));
        }

        [Fact]
        public void Classify_MatchesMethodCaseInsensitively_LikeTheDispatcher()
        {
            // The dispatcher lower-cases the method and looks it up with
            // OrdinalIgnoreCase, so the table must not turn "List" into an unknown write.
            Assert.Equal(OperationClass.Read, WorkerCommandCatalog.Classify("List", "Objects"));
            Assert.Equal(OperationClass.Read, WorkerCommandCatalog.Classify("PING", null));
        }

        private static string DispatcherSource() =>
            SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "CommandDispatcher.cs"));

        private static string ClassifierSource() =>
            SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "CommandLaneClassifier.cs"));

        /// <summary>
        /// Every (lower-case method, action) pair the dispatcher registers, with the empty
        /// action for a method whose handler compares no action literal. Actions are
        /// de-duplicated case-insensitively.
        /// </summary>
        private static List<(string Method, string Action)> RegisteredCommands()
        {
            string source = DispatcherSource();
            string table = SourceAssert.MethodBody(source, TableDeclaration);

            var actionsByMethod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (Match registration in RegistrationPattern.Matches(table))
            {
                string method = registration.Groups["method"].Value.ToLowerInvariant();
                if (actionsByMethod.ContainsKey(method)) continue;

                var actions = new List<string>();
                if (registration.Groups["handler"].Success)
                {
                    string body = SourceAssert.MethodBody(source, "string " + registration.Groups["handler"].Value + "(");
                    actions.AddRange(ActionLiterals(body));
                }

                actionsByMethod[method] = actions;
                order.Add(method);
            }

            // Issue #854 moved the dispatcher's lane guards into CommandLaneClassifier; scan both files so the guard actions stay covered.
            foreach (Match guard in MethodGuardPattern.Matches(source + "\n" + ClassifierSource()))
            {
                string method = guard.Groups["m"].Value.ToLowerInvariant();
                if (actionsByMethod.TryGetValue(method, out var actions))
                    actions.AddRange(ActionLiterals(guard.Groups["rest"].Value));
            }

            var result = new List<(string Method, string Action)>();
            foreach (string method in order)
            {
                var actions = actionsByMethod[method]
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (actions.Count == 0)
                    result.Add((method, ""));
                else
                    result.AddRange(actions.Select(a => (method, a)));
            }
            return result;
        }

        private static IEnumerable<string> ActionLiterals(string code) =>
            ActionLiteralPattern.Matches(code).Cast<Match>().Select(m => m.Groups["a"].Value);
    }
}
