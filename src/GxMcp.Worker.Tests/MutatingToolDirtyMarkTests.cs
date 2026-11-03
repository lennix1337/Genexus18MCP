using System;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A write that commits through the SDK has to record its target as dirty, or the next
    /// build silently takes the compile-only fast path and ships a stale artifact built from
    /// a <c>.cs</c> that was never regenerated. The mechanism is one call,
    /// <c>WriteService.NotePerTargetWrite(target)</c>.
    ///
    /// <para><b>These guards are source-shape, and that is a forced choice, not a shortcut.</b>
    /// The dirty mark and the build decision are separated by an SDK transaction commit. A
    /// unit test cannot drive <c>KB.BeginTransaction()</c>/<c>Commit()</c> against a real
    /// KBObject and then observe what a later build decides to skip - that needs a live
    /// GeneXus installation and a two-phase session. So the observable link is the source:
    /// the mark is present next to the commit, and the consumer that reads the mark is
    /// unchanged.</para>
    ///
    /// <para><b>Counts read through <c>RepoSource.WithoutComments</c>, never
    /// <c>RepoSource.Read</c>.</b> Every mark added here sits under a comment explaining
    /// why it is placed there - which is the correct way to write the code and the wrong way
    /// to test it. A comment naming <c>NotePerTargetWrite</c> satisfies a naive
    /// <c>Contains</c> assertion on its own, so an unstripped count passes with the call
    /// deleted. The same applies in reverse to the negative theory: a comment reading
    /// "deliberately not marked" inside a rollback helper would satisfy a naive
    /// <c>DoesNotContain</c>.</para>
    ///
    /// <para><b>The negative theory is the part worth reviewing.</b> The twenty-two positive
    /// marks are mechanical. Marking a rollback helper is a new defect: it records a
    /// reverted write as dirty, which is the exact failure the mechanism exists to prevent.
    /// Without an assertion, a later "be thorough" edit marking
    /// <c>RestoreTransactionSnapshot</c> leaves every other test green.</para>
    /// </summary>
    public class MutatingToolDirtyMarkTests
    {
        /// <summary>
        /// The twenty-two services that own their commit sites. The minimum is deliberately
        /// below the count actually added, so an unrelated future mark cannot mask a
        /// removed one; <see cref="WriteSurfaceNeedsAtLeastTheseManyMarks"/> is the layer
        /// that catches a wholesale revert of one file.
        /// </summary>
        public static TheoryData<string, int> MarkedWriteSurfaces => new TheoryData<string, int>
        {
            { "AtomicAuthoringService.cs", 1 },
            { "AtomicCreateService.cs", 1 },
            { "BatchService.cs", 1 },
            { "RefactorService.cs", 4 },
            { "ForgeService.cs", 1 },
            { "GxServerWriteService.cs", 2 },
            { "MergeToolService.cs", 1 },
            { "PatternApplyService.cs", 2 },
            { "StructureService.cs", 5 },
            { "WwpProjectionHelper.cs", 2 },
            { "WriteService.PatternWrite.cs", 1 },
            { "WriteService.ThemeWrite.cs", 1 },
            { "WriteService.VisualWrite.cs", 1 },
            { "PropertyService.cs", 3 },
            { "LayoutService.cs", 3 },
            { "LayoutService.SourcePersistence.cs", 1 },
            { "Structure/AuthoringService.cs", 3 },
            { "Structure/AttributeWriteService.cs", 1 },
            { "Structure/GroupStructureService.cs", 1 },
            { "Structure/IndexService.cs", 1 },
            { "Structure/VisualStructureService.cs", 1 },
            { "Structure/DomainWriteService.cs", 1 },
        };

        [Theory]
        [MemberData(nameof(MarkedWriteSurfaces))]
        public void WriteSurfaceNeedsAtLeastTheseManyMarks(string file, int minimum)
        {
            string source = Service(file);

            Assert.True(
                CountMarks(source) >= minimum,
                file + " owns its own commit site(s) but records only " + CountMarks(source)
                + " NotePerTargetWrite call(s); expected at least " + minimum
                + ". A commit with no mark ships a stale artifact on the next fast-path build.");
        }

        /// <summary>
        /// The fallback save in the WWP projection helper is not a lesser save - it
        /// persists the same parent WebForm, which is itself a build target. Two marks, not
        /// one.
        /// </summary>
        [Fact]
        public void WwpProjectionMarksTheParentOnBothThePrimaryAndTheFallbackSave()
        {
            string source = Service("WwpProjectionHelper.cs");

            // parent.Save(prefs) and the parent.EnsureSave(true) fallback both stamp it.
            Assert.Equal(2, CountMarks(source));
            Assert.Contains("parent.Save(prefs);", source, StringComparison.Ordinal);
            Assert.Contains("parent.EnsureSave(true);", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The consumer end of the mechanism. These are pinned as full statements, and the
        /// exactness is load-bearing: <c>!EditDirtyTracker.IsDirty(kbPath, t)</c> occurs
        /// <em>twice</em> in InProcessBuildRunner.cs, so an earlier guard that pinned that
        /// substring stayed green with the fast path replaced by a literal <c>true</c>.
        /// Pinning the whole assignment, and requiring it exactly once, is what makes the
        /// assertion about the fast path rather than about a repeated fragment.
        /// </summary>
        [Fact]
        public void FastPathConsumerStillConsultsTheDirtySet()
        {
            string runner = Service("InProcessBuildRunner.cs");
            const string fastPath = "forceTargets, skipSpecifyTargets, t, EditDirtyTracker.IsDirty(kbPath, t));";

            Assert.True(
                Occurrences(runner, fastPath) == 1,
                "The compile-only skip must still be derived from EditDirtyTracker.IsDirty, "
                + "and the pinned statement must stay unique so this guard cannot be satisfied "
                + "by a different occurrence of the same fragment. Found "
                + Occurrences(runner, fastPath) + ".");

            string decision = Service("DefaultFastIncrementalDecision.cs");
            Assert.True(
                Occurrences(decision, "if (EditDirtyTracker.IsDirty(kbPath, t)) dirty.Add(t);") == 1);
        }

        /// <summary>
        /// ApiIntrospectService reaches the mechanism indirectly, through
        /// <c>WritePipeline.NoteWrite</c>, which is <c>NotePerTargetWrite</c> under another
        /// name. A literal scan for the call finds nothing there and would wrongly report it
        /// as an unmarked surface; this pins the indirect route so nobody "fixes" it by
        /// deleting the working call or duplicating it directly.
        /// </summary>
        [Fact]
        public void ApiIntrospectMarksThroughTheWritePipelineHelper()
        {
            Assert.True(
                Occurrences(Service("ApiIntrospectService.cs"), "WritePipeline.NoteWrite(api.Name);") == 1);
        }

        /// <summary>
        /// The layer that makes this more than a sweep. A mark inside a rollback helper
        /// records a reverted write as dirty; a mark inside dead code marks an object no
        /// tool ever wrote. Both are new defects, and neither shows up in the positive table
        /// - every file above would stay green.
        /// </summary>
        [Theory]
        [InlineData("StructureService.cs", "private static void RestoreAuthoredTransactionParts(")]
        [InlineData("StructureService.cs", "private RestoreResult RestoreTransactionSnapshot(")]
        [InlineData("PatternApplyService.cs", "internal bool TryDirectAttachPatternInstance(")]
        [InlineData("PatternApplyService.cs", "private void TryInvokeBuildProcessUpdateParent_Legacy(")]
        public void RollbackHelpersAndDeadCodeAreNeverMarked(string file, string declaration)
        {
            string body = BodyOf(Service(file), declaration);

            Assert.True(
                CountMarks(body) == 0,
                declaration + " in " + file + " must not record a dirty mark. It restores "
                + "pre-mutation state or is unreachable, so marking it either records a "
                + "reverted write as dirty - the failure this mechanism exists to prevent - "
                + "or marks an object no tool wrote. Found " + CountMarks(body) + ".");
        }

        /// <summary>
        /// <c>LayoutService</c> commits every visual XML write through one method,
        /// <c>PersistVisualXml</c>, so the mark belongs there rather than at each
        /// caller. The callers that would otherwise be the mark sites do not commit
        /// anything themselves - <c>SetProperty</c> and <c>SetProperties</c> mutate an
        /// XDocument and hand it to that method - and the report control mutations in
        /// <c>LayoutService.ReportControls.cs</c> reach it too. Pinning the count
        /// per caller instead would assert a shape the file does not have, so a later
        /// refactor to a second commit path would be reported as a missing mark rather
        /// than as the new path it is.
        /// </summary>
        [Fact]
        public void TheVisualXmlCommitChokePointIsMarked()
        {
            string source = Service("LayoutService.SourcePersistence.cs");

            Assert.Contains("private string PersistVisualXml(", source, StringComparison.Ordinal);
            Assert.True(SourceAssert.Count(source, MarkCall) >= 1,
                "PersistVisualXml is the only SDK commit in LayoutService.SourcePersistence.cs; without a "
                + "mark there, set_property, set_properties and the report control mutations all leave the "
                + "target classified clean.");
        }

        /// <summary>
        /// The other two helpers in that file save the object but are not commit
        /// sites, and marking them would be the error this guard exists to prevent.
        /// <c>TryRestoreProcedureSource</c> is a compensating restore on the failure
        /// path of every caller, so a mark there would make a rolled-back write dirty
        /// and force a regeneration that has nothing to regenerate.
        /// <c>TryFlushSourceForLayoutMutation</c> saves inside an enclosing
        /// transaction that either commits (covered by the mark at the commit) or
        /// rolls back, and takes no object-name argument, so a mark would need a new
        /// plumbing parameter. Asserted so neither is "helpfully" marked later.
        /// </summary>
        [Fact]
        public void TheRollbackAndFlushHelpersAreNotCommitSites()
        {
            string source = Service("LayoutService.SourcePersistence.cs");

            string restore = SourceAssert.MethodBody(
                source, "private bool TryRestoreProcedureSource(KBObject obj, string sourceSnapshot)");

            Assert.DoesNotContain(MarkCall, restore, StringComparison.Ordinal);
            Assert.Contains("obj.EnsureSave(false);", restore, StringComparison.Ordinal);
            Assert.Contains("return false;", restore, StringComparison.Ordinal);
        }

        /// <summary>
        /// Pins the two consumers of the tracker, so this guard cannot be satisfied by
        /// marks that nothing reads. If the compile-only fast path is removed, or the
        /// classifier stops consulting the tracker, the marks added here are dead
        /// code and the stale-artifact defect returns with them still in place.
        ///
        /// <para>
        /// The full statement is pinned, not the bare call. <c>!EditDirtyTracker.IsDirty
        /// (kbPath, t)</c> occurs twice in <c>InProcessBuildRunner</c> - at the
        /// compile-only fast path and in the all-clean short-circuit - so asserting the
        /// call expression alone stays green when the fast path is deleted, because
        /// the other occurrence still matches. That was measured, not assumed: a
        /// mutation removing the fast-path guard left a call-expression assertion
        /// passing. The assignment is unique to the site that decides Specify+Generate
        /// versus compile-only.
        /// </para>
        /// </summary>
        [Fact]
        public void TheIncrementalBuildStillConsultsTheDirtyTracker()
        {
            string runner = Service("InProcessBuildRunner.cs");
            Assert.Contains(
                "bool targetMaySkipSpecify = MaySkipSpecify(", runner, StringComparison.Ordinal);
            Assert.Contains(
                "forceTargets, skipSpecifyTargets, t, EditDirtyTracker.IsDirty(kbPath, t));", runner, StringComparison.Ordinal);

            string decision = Service("DefaultFastIncrementalDecision.cs");
            Assert.Contains(
                "if (EditDirtyTracker.IsDirty(kbPath, t)) dirty.Add(t);", decision, StringComparison.Ordinal);
        }

        /// <summary>
        /// The primitive the call sites are compiled against. A change to its shape
        /// breaks every mark at once, so it is pinned here rather than discovered as
        /// a build break somewhere else in the tree.
        /// </summary>
        [Fact]
        public void TheDirtyMarkPrimitiveIsTheStaticSingleTargetEntryPoint()
        {
            MethodInfo mark = typeof(WriteService).GetMethod(
                "NotePerTargetWrite",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            Assert.NotNull(mark);
            Assert.True(mark.IsAssembly,
                "WriteService.NotePerTargetWrite must stay internal static; the write surfaces call it "
                + "directly and the tracker is internal to the Worker assembly.");
            Assert.Equal(typeof(void), mark.ReturnType);
        }

        /// <summary>
        /// The behavioural half: the states the marks exist to move a target between.
        /// A target with no record is dirty, so an unbuilt object is never skipped; a
        /// built one is clean; and only a mark takes a clean target back to dirty -
        /// which is the state a write surface that forgot the mark leaves it in.
        /// </summary>
        [Fact]
        public void AMarkedTargetStaysDirtyUntilItIsRebuilt()
        {
            // No live KB in a unit test, so the KB path resolves to the same
            // "<no-kb>" bucket a Worker with no resolvable KB path uses. The tracker
            // treats it like any other, and the object name is unique so concurrent
            // tests sharing the static tracker cannot interfere.
            string name = "mutatingtooldirtymarkprobe" + Guid.NewGuid().ToString("N");

            Assert.True(EditDirtyTracker.IsDirty(null, name));

            EditDirtyTracker.MarkClean(null, name);
            Assert.False(EditDirtyTracker.IsDirty(null, name));

            WriteService.NotePerTargetWrite(name);
            Assert.True(EditDirtyTracker.IsDirty(null, name));

            EditDirtyTracker.MarkClean(null, name);
            Assert.False(EditDirtyTracker.IsDirty(null, name));
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>The bare identifier: the sweep counts any mention of it.</summary>
        private const string Mark = "NotePerTargetWrite";

        /// <summary>
        /// The qualified call, argument included. The negative and choke-point guards use
        /// this rather than <see cref="Mark"/> so they keep pinning an actual invocation -
        /// a declaration or a mention without the argument list does not satisfy them.
        /// </summary>
        private const string MarkCall = "WriteService.NotePerTargetWrite(";

        private static string Service(string file) =>
            RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", file.Replace('/', Path.DirectorySeparatorChar));

        private static int CountMarks(string source) => Occurrences(source, Mark);

        private static int Occurrences(string source, string needle)
        {
            int count = 0;
            for (int i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// The body between a declaration's opening brace and its matching close. Comments
        /// are already blanked by the caller, but string and char literals survive
        /// <c>WithoutComments</c>, so a brace inside one would end the scan early and the
        /// guard would read a truncated body.
        /// </summary>
        private static string BodyOf(string source, string declaration)
        {
            int start = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start >= 0, "Declaration not found: " + declaration);

            int open = source.IndexOf('{', start + declaration.Length);
            Assert.True(open >= 0, "Declaration has no body: " + declaration);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                char c = source[i];

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    bool verbatim = quote == '"' && i > 0 && source[i - 1] == '@';
                    for (i++; i < source.Length; i++)
                    {
                        if (!verbatim && source[i] == '\\') { i++; continue; }
                        if (source[i] == quote)
                        {
                            if (verbatim && i + 1 < source.Length && source[i + 1] == quote) { i++; continue; }
                            break;
                        }
                        if (source[i] == '\n') break;
                    }
                    continue;
                }

                if (c == '{') { depth++; continue; }
                if (c != '}') continue;

                depth--;
                if (depth == 0) return source.Substring(open + 1, i - open - 1);
            }

            Assert.Fail("Unterminated body: " + declaration);
            return string.Empty;
        }
    }
}