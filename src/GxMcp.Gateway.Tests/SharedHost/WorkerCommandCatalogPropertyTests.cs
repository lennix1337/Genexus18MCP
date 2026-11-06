using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GxMcp.Common.SharedHost;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests.SharedHost
{
    // Feature: concurrent-multi-session-workers, Property 6: Retry flag of delivered-unanswered errors
    //
    // For any Worker command name and action, including names absent from the Operation_Class table, a
    // delivered-unanswered SHARED_WORKER_RESTARTED error built with the class WorkerCommandCatalog.Classify returns
    // carries retryable=true exactly when that class is Read, and every unknown command classifies as Write. The
    // not-delivered error for the same command is always retryable.
    //
    // **Validates: Requirements 4.5, 12.4**
    //
    // Contract used, and nothing beyond it (see WorkerCommandCatalogTests and ConcurrencyErrorCatalogTests):
    // - WorkerCommandCatalog.Entries: entries with Method (lower case), Action ("" for no action), Class.
    // - WorkerCommandCatalog.Classify(method, action): unknown -> Write; method matched case-insensitively;
    //   a null action means no action.
    // - ConcurrencyErrorCatalog.Build("SHARED_WORKER_RESTARTED", null, ConcurrencyErrorFields) with
    //   QueueWaitMs, RetryAfterMs, DispatchState and OperationClass.
    //
    // Inputs come from three kinds:
    // - known: an entry exactly as the table holds it (a "" action is sometimes passed as null);
    // - unknown: a method absent from the table, a null or empty method, or a known method that has no
    //   no-action entry paired with an action absent for that method;
    // - case variant: a known entry whose method has its letter case changed (the action is kept as the table
    //   holds it, because only method case-insensitivity is part of the pinned contract).
    public class WorkerCommandCatalogPropertyTests
    {
        private const int Iterations = 300;
        private const string PropertyName = "Property 6: Retry flag of delivered-unanswered errors";
        private const string RestartedCode = "SHARED_WORKER_RESTARTED";

        // Allowed queue wait range: Requirement 3 criterion 4.
        private const int QueueWaitMin = 1000;
        private const int QueueWaitMax = 600000;

        private const string Letters = "abcdefghijklmnopqrstuvwxyz";

        private enum InputKind { Known, Unknown, CaseVariant }

        private sealed class Known
        {
            internal Known(string method, string action, OperationClass cls)
            {
                Method = method;
                Action = action;
                Class = cls;
            }

            internal string Method { get; }
            internal string Action { get; }
            internal OperationClass Class { get; }
        }

        private sealed class Generated
        {
            internal InputKind Kind;
            internal string? Method;
            internal string? Action;

            // Null when the kind does not pin an expected class beyond the retry rule itself.
            internal OperationClass? Expected;
        }

        [Fact]
        public void DeliveredUnansweredRetryFlag_EqualsReadClass_AndNotDeliveredIsAlwaysRetryable()
        {
            var table = WorkerCommandCatalog.Entries
                .Select(e => new Known(e.Method, e.Action ?? "", e.Class))
                .ToList();
            Assert.NotEmpty(table);

            var knownMethods = new HashSet<string>(table.Select(e => e.Method), StringComparer.OrdinalIgnoreCase);
            var actionsByMethod = table
                .GroupBy(e => e.Method, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new HashSet<string>(g.Select(e => e.Action), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            // A method with a no-action entry may treat any action as "no action"; that is not pinned, so an unknown
            // action is only paired with methods whose every entry names an action.
            var actionOnlyMethods = actionsByMethod
                .Where(kv => !kv.Value.Contains(""))
                .Select(kv => kv.Key)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

            var caseVariantCandidates = table.Where(e => e.Method.Any(char.IsLetter)).ToList();
            var counts = new Dictionary<InputKind, int>();

            SeededInterleavingRunner.Run(PropertyName, Iterations, c =>
            {
                var input = Generate(c, table, knownMethods, actionsByMethod, actionOnlyMethods, caseVariantCandidates);
                counts[input.Kind] = counts.TryGetValue(input.Kind, out var n) ? n + 1 : 1;

                int queueWait = c.Int(QueueWaitMin, QueueWaitMax);
                int? retryAfter = c.Chance(0.3) ? (int?)null : c.Int(-1000, QueueWaitMax + 1000);
                c.Log("input kind=" + input.Kind + " method=" + Show(input.Method) + " action=" + Show(input.Action)
                    + " expected=" + (input.Expected?.ToString() ?? "<retry rule only>")
                    + " queueWaitMs=" + queueWait.ToString(CultureInfo.InvariantCulture)
                    + " retryAfterMs=" + (retryAfter?.ToString(CultureInfo.InvariantCulture) ?? "<null>"));

                OperationClass cls = WorkerCommandCatalog.Classify(input.Method, input.Action);
                c.Log("classify -> " + cls);

                if (input.Expected.HasValue)
                    Assert.Equal(input.Expected.Value, cls);

                // Delivered-unanswered: retryable exactly when the class is Read.
                var unanswered = Build(ConcurrencyDispatchState.DeliveredUnanswered, cls, queueWait, retryAfter);
                c.Log("delivered-unanswered -> " + unanswered.ToString(Newtonsoft.Json.Formatting.None));
                AssertError(unanswered, "delivered-unanswered", cls == OperationClass.Read, queueWait);

                // Not-delivered: always retryable, whatever the class.
                var notDelivered = Build(ConcurrencyDispatchState.NotDelivered, cls, queueWait, retryAfter);
                c.Log("not-delivered -> " + notDelivered.ToString(Newtonsoft.Json.Formatting.None));
                AssertError(notDelivered, "not-delivered", expectedRetryable: true, queueWait);
            });

            // Guards the generator: a run that never produced one of the kinds would not exercise the property.
            foreach (InputKind kind in Enum.GetValues(typeof(InputKind)))
            {
                if (kind == InputKind.CaseVariant && caseVariantCandidates.Count == 0) continue;
                Assert.True(counts.TryGetValue(kind, out var n) && n > 0, "generator produced no " + kind + " input");
            }
        }

        [Fact]
        public void UnknownCommand_DeliveredUnanswered_IsNeverRetryable()
        {
            var knownMethods = new HashSet<string>(
                WorkerCommandCatalog.Entries.Select(e => e.Method), StringComparer.OrdinalIgnoreCase);

            SeededInterleavingRunner.Run(PropertyName + " (unknown commands)", Iterations, c =>
            {
                string? method = UnknownMethod(c, knownMethods);
                string? action = RandomAction(c);
                c.Log("input method=" + Show(method) + " action=" + Show(action));

                OperationClass cls = WorkerCommandCatalog.Classify(method, action);
                c.Log("classify -> " + cls);
                Assert.Equal(OperationClass.Write, cls);

                var error = Build(ConcurrencyDispatchState.DeliveredUnanswered, cls, QueueWaitMin, retryAfter: null);
                c.Log("delivered-unanswered -> " + error.ToString(Newtonsoft.Json.Formatting.None));
                AssertError(error, "delivered-unanswered", expectedRetryable: false, QueueWaitMin);
            });
        }

        // ---- Generators -------------------------------------------------------------------------

        private static Generated Generate(
            PbtCase c,
            IReadOnlyList<Known> table,
            HashSet<string> knownMethods,
            Dictionary<string, HashSet<string>> actionsByMethod,
            IReadOnlyList<string> actionOnlyMethods,
            IReadOnlyList<Known> caseVariantCandidates)
        {
            int roll = c.Int(0, 2);
            if (roll == 2 && caseVariantCandidates.Count == 0) roll = 0;

            switch (roll)
            {
                case 0:
                {
                    var entry = c.Pick(table);
                    string? action = entry.Action.Length == 0 && c.Bool() ? null : entry.Action;
                    return new Generated { Kind = InputKind.Known, Method = entry.Method, Action = action, Expected = entry.Class };
                }
                case 1:
                {
                    if (actionOnlyMethods.Count > 0 && c.Bool())
                    {
                        string method = c.Pick(actionOnlyMethods);
                        string action = UnknownAction(c, actionsByMethod[method]);
                        if (c.Bool()) method = ChangeCase(c, method);
                        return new Generated { Kind = InputKind.Unknown, Method = method, Action = action, Expected = OperationClass.Write };
                    }

                    return new Generated
                    {
                        Kind = InputKind.Unknown,
                        Method = UnknownMethod(c, knownMethods),
                        Action = RandomAction(c),
                        Expected = OperationClass.Write,
                    };
                }
                default:
                {
                    var entry = c.Pick(caseVariantCandidates);
                    string method = ChangeCase(c, entry.Method);
                    return new Generated { Kind = InputKind.CaseVariant, Method = method, Action = entry.Action, Expected = entry.Class };
                }
            }
        }

        // A method the table does not hold under any letter case, or null, or empty.
        private static string? UnknownMethod(PbtCase c, HashSet<string> knownMethods)
        {
            int roll = c.Int(0, 9);
            if (roll == 0) return null;
            if (roll == 1) return "";

            while (true)
            {
                string candidate = roll == 2
                    ? ChangeCase(c, c.Pick(knownMethods.ToList())) + "x" + RandomWord(c, 1, 4)
                    : RandomWord(c, 1, 12);
                if (c.Bool()) candidate = ChangeCase(c, candidate);
                if (!knownMethods.Contains(candidate)) return candidate;
            }
        }

        // An action the method's entries do not hold under any letter case; never empty.
        private static string UnknownAction(PbtCase c, HashSet<string> knownActions)
        {
            while (true)
            {
                string candidate = c.Chance(0.3) && knownActions.Count > 0
                    ? c.Pick(knownActions.Where(a => a.Length > 0).DefaultIfEmpty("Get").ToList()) + "X" + RandomWord(c, 1, 3)
                    : Capitalize(RandomWord(c, 1, 12));
                if (!knownActions.Contains(candidate)) return candidate;
            }
        }

        private static string? RandomAction(PbtCase c)
        {
            int roll = c.Int(0, 5);
            if (roll == 0) return null;
            if (roll == 1) return "";
            return Capitalize(RandomWord(c, 1, 12));
        }

        private static string RandomWord(PbtCase c, int minLength, int maxLength)
        {
            int length = c.SmallInt(minLength, maxLength);
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append(Letters[c.Int(0, Letters.Length - 1)]);
            return sb.ToString();
        }

        private static string Capitalize(string word) =>
            word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);

        // Changes the case of at least one letter, so the result differs from the input when it has a letter.
        private static string ChangeCase(PbtCase c, string value)
        {
            var chars = value.ToCharArray();
            var letterIndexes = Enumerable.Range(0, chars.Length).Where(i => char.IsLetter(chars[i])).ToList();
            if (letterIndexes.Count == 0) return value;

            int forced = c.Pick(letterIndexes);
            foreach (int i in letterIndexes)
            {
                if (i == forced || c.Bool())
                    chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
            }
            return new string(chars);
        }

        // ---- Assertions -------------------------------------------------------------------------

        private static JObject Build(string dispatchState, OperationClass cls, int queueWait, int? retryAfter)
        {
            var fields = new ConcurrencyErrorFields
            {
                QueueWaitMs = queueWait,
                RetryAfterMs = retryAfter,
                DispatchState = dispatchState,
                OperationClass = cls,
            };
            return ConcurrencyErrorCatalog.Build(RestartedCode, null, fields);
        }

        private static void AssertError(JObject error, string dispatchState, bool expectedRetryable, int queueWait)
        {
            Assert.Equal(RestartedCode, (string?)error["code"]);
            Assert.Equal(dispatchState, (string?)error["dispatchState"]);
            Assert.True(error["retryable"]?.Type == JTokenType.Boolean, "retryable is not a JSON boolean");
            Assert.Equal(expectedRetryable, (bool)error["retryable"]!);

            if (expectedRetryable)
            {
                var token = error["retryAfterMs"];
                Assert.True(token?.Type == JTokenType.Integer, "retryAfterMs missing or not an integer");
                Assert.InRange((int)token!, 0, queueWait);
            }
            else
            {
                Assert.Null(error["retryAfterMs"]);
            }
        }

        private static string Show(string? value) => value == null ? "<null>" : "\"" + value + "\"";
    }
}
