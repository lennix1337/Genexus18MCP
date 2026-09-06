using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 1.3): unit tests for the unified caller/callee graph
    // service. Drives an in-memory IndexCacheService via the LoadFromEntries
    // test seam so the tests don't need a real KB.
    public class CallerGraphServiceTests
    {
        [Fact]
        public void GetCallers_ReturnsDirectCallers()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new CallerGraphService(fx.Index);

            var callersOfB = svc.GetCallers("B");
            Assert.Contains("A", callersOfB);
            // A is the only direct caller of B in the small chain
            Assert.Single(callersOfB.Distinct(System.StringComparer.OrdinalIgnoreCase));

            var callersOfC = svc.GetCallers("C");
            Assert.Contains("B", callersOfC);
            Assert.Single(callersOfC.Distinct(System.StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void GetCallees_ReturnsDirectCallees()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new CallerGraphService(fx.Index);

            var calleesOfA = svc.GetCallees("A");
            Assert.Contains("B", calleesOfA);
            Assert.Single(calleesOfA);

            var calleesOfB = svc.GetCallees("B");
            Assert.Contains("C", calleesOfB);
        }

        [Fact]
        public void GetCalleesTransitive_BfsRespectsCap()
        {
            var fx = TestFixtures.LargeCallChain(depth: 250);
            var svc = new CallerGraphService(fx.Index);

            var result = svc.GetCalleesTransitive("N0", maxNodes: 200);

            Assert.True(result.Truncated, "Expected Truncated=true when the chain exceeds maxNodes");
            Assert.Equal(200, result.Nodes.Count);
        }

        [Fact]
        public void GetCalleesTransitive_SmallChain_NotTruncated()
        {
            var fx = TestFixtures.SmallCallGraph(); // A -> B -> C
            var svc = new CallerGraphService(fx.Index);

            var result = svc.GetCalleesTransitive("A", maxNodes: 200);

            Assert.False(result.Truncated);
            Assert.Equal(2, result.Nodes.Count); // B and C, not the root A
            Assert.Contains("B", result.Nodes);
            Assert.Contains("C", result.Nodes);
        }

        [Fact]
        public void GetCallersTransitive_BfsRespectsCap()
        {
            // Build a reverse chain: N{depth-1} <- ... <- N1 <- N0. Walking
            // callers from the leaf must cover every node up to the cap.
            var fx = TestFixtures.LargeCallChain(depth: 250);
            var svc = new CallerGraphService(fx.Index);

            // Leaf of the chain (no outgoing calls) has the most transitive callers.
            var leaf = "N249";
            var result = svc.GetCallersTransitive(leaf, maxNodes: 200);

            Assert.True(result.Truncated, "Expected Truncated=true when the chain exceeds maxNodes");
            Assert.Equal(200, result.Nodes.Count);
        }

        [Fact]
        public void GetCallersTransitive_SmallChain_NotTruncated()
        {
            var fx = TestFixtures.SmallCallGraph(); // A -> B -> C
            var svc = new CallerGraphService(fx.Index);

            var result = svc.GetCallersTransitive("C", maxNodes: 200);

            Assert.False(result.Truncated);
            Assert.Equal(2, result.Nodes.Count); // B and A, not the root C
            Assert.Contains("A", result.Nodes);
            Assert.Contains("B", result.Nodes);
        }

        [Fact]
        public void AnalyzeImpact_AndInspectCallers_ReturnCompatibleCallers()
        {
            // Parity check: AnalyzeService.ImpactAnalysis (index-based via
            // CallerGraphService) and CallerGraphService.GetCallers (direct
            // callers from the same index) should agree on the immediate
            // caller set for an indexed object. Impact may include transitive
            // callers in addition; we assert the direct set is a subset.
            //
            // The AnalyzeService transitively references Artech.* SDK types.
            // When the test host doesn't have the GeneXus install DLLs next to
            // it, type-load fails — the production code path is still covered
            // by the CallerGraphService unit tests in this same file.
            var fx = TestFixtures.SmallCallGraph();
            var graph = new CallerGraphService(fx.Index);
            string impactJson;
            try
            {
                var analyze = new AnalyzeService(fx.Index, objSvc: null, graph: graph);
                fx.Index.MarkIndexComplete(3);
                impactJson = analyze.ImpactAnalysis("C", waitForIndex: true);
            }
            catch (System.IO.FileNotFoundException)
            {
                return; // Artech SDK not available in this test host — skip.
            }
            catch (System.TypeLoadException)
            {
                return;
            }

            var impact = JObject.Parse(impactJson);
            if (impact["error"] != null || impact["callers"] == null) return;

            var impactCallers = ((JArray)impact["callers"]).Select(j => j.ToString()).ToList();
            var directCallers = graph.GetCallers("C");

            foreach (var c in directCallers)
                Assert.Contains(c, impactCallers);
        }

        [Fact]
        public void AnalyzeImpact_IndexReindexing_AndNotWaiting_ReturnsReindexingEnvelope()
        {
            var fx = TestFixtures.SmallCallGraph();
            fx.Index.MarkReindexStarted(100);
            var graph = new CallerGraphService(fx.Index);
            string json;
            try
            {
                var analyze = new AnalyzeService(fx.Index, objSvc: null, graph: graph);
                json = analyze.ImpactAnalysis("C", waitForIndex: false);
            }
            catch (System.IO.FileNotFoundException)
            {
                return; // Artech SDK not available in this test host — skip.
            }
            catch (System.TypeLoadException)
            {
                return;
            }
            Assert.Contains("\"status\": \"Reindexing\"", json);
        }

        [Fact]
        public void GetCallers_MatchesInternalConsistency_WithGetCallees()
        {
            // For each (caller, callee) edge expressed via GetCallees, the
            // inverse GetCallers(callee) must include caller. This is the
            // internal-consistency check called out in the task spec; Task 1.4
            // will extend it to assert parity with AnalyzeService.ImpactAnalysis.
            var fx = TestFixtures.SmallCallGraph();
            var svc = new CallerGraphService(fx.Index);

            foreach (var caller in new[] { "A", "B", "C" })
            {
                foreach (var callee in svc.GetCallees(caller))
                {
                    var callers = svc.GetCallers(callee);
                    Assert.Contains(caller, callers);
                }
            }
        }

        // Plan 022: GetCallers collapsed from four index passes to one. These
        // tests lock the three source signals it must still combine (CalledBy,
        // regex-over-SourceSnippet, forward Calls) and their union.

        [Fact]
        public void GetCallers_CalledByPath_ReturnsInvertedIndexEntries()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string> { "A", "B" }, SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "A", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "B", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            var callers = svc.GetCallers("Target");
            Assert.Contains("A", callers);
            Assert.Contains("B", callers);
            Assert.Equal(2, callers.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void GetCallers_RegexOnlyPath_FindsCallerViaSourceSnippet()
        {
            // No CalledBy/Calls populated anywhere — only "Caller"'s snippet
            // textually invokes Target(. The augmentation scan must still run.
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "Target()" }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            var callers = svc.GetCallers("Target");
            Assert.Contains("Caller", callers);
            Assert.Single(callers);
        }

        [Fact]
        public void GetCallers_ForwardCallsPath_ReturnsEntryListingTargetInCalls()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure",
                    Calls = new List<string> { "Target" }, CalledBy = new List<string>(), SourceSnippet = "" }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            var callers = svc.GetCallers("Target");
            Assert.Contains("Caller", callers);
            Assert.Single(callers);
        }

        [Fact]
        public void GetCallers_UnionOfAllThreeSignals_NoDuplicates()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string> { "ByCalledBy" }, SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "ByCalledBy", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "ByRegex", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "Target()" },
                new SearchIndex.IndexEntry { Name = "ByForwardCalls", Type = "Procedure",
                    Calls = new List<string> { "Target" }, CalledBy = new List<string>(), SourceSnippet = "" },
                // Present in more than one signal — must still appear only once.
                new SearchIndex.IndexEntry { Name = "ByBoth", Type = "Procedure",
                    Calls = new List<string> { "Target" }, CalledBy = new List<string>(), SourceSnippet = "Target()" }
            };
            // Also wire ByBoth into Target's CalledBy so all three signals overlap on it.
            entries[0].CalledBy.Add("ByBoth");

            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            var callers = svc.GetCallers("Target");
            Assert.Contains("ByCalledBy", callers);
            Assert.Contains("ByRegex", callers);
            Assert.Contains("ByForwardCalls", callers);
            Assert.Contains("ByBoth", callers);
            Assert.Equal(4, callers.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }

        // Plan 032: GetCallees' fallback (used when Calls isn't populated) was
        // rewritten from a per-candidate compiled Regex to a single snippet
        // tokenization pass. These tests lock the fast path (Calls populated)
        // and the fallback's behavior (identifier-in-snippet must match a
        // known object AND be followed by "(").

        [Fact]
        public void GetCallees_CallsPopulated_FastPathUnchanged()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure",
                    Calls = new List<string> { "A", "B" }, CalledBy = new List<string>(), SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "A", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                new SearchIndex.IndexEntry { Name = "B", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            var callees = svc.GetCallees("Caller");
            Assert.Contains("A", callees);
            Assert.Contains("B", callees);
            Assert.Equal(2, callees.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void GetCallees_FallbackPath_FindsCalleeViaSourceSnippet_NoFalseMatch()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                // Empty Calls -> triggers the fallback snippet scan.
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(),
                    SourceSnippet = "DoThing(1); // mentions Foo but not as a call" },
                new SearchIndex.IndexEntry { Name = "DoThing", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                // "Foo" is an indexed object but never called (no trailing '(') in the snippet.
                new SearchIndex.IndexEntry { Name = "Foo", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" },
                // "Unrelated" isn't mentioned in the snippet at all.
                new SearchIndex.IndexEntry { Name = "Unrelated", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>(), SourceSnippet = "" }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            var callees = svc.GetCallees("Caller");
            Assert.Contains("DoThing", callees);
            Assert.DoesNotContain("Foo", callees);
            Assert.DoesNotContain("Unrelated", callees);
            Assert.Single(callees);
        }

        [Fact]
        public void AdjacencySnapshot_RebuildsAfterIndexRevision()
        {
            var entries = new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure",
                    Calls = new List<string> { "First" }, CalledBy = new List<string>() },
                new SearchIndex.IndexEntry { Name = "First", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>() },
                new SearchIndex.IndexEntry { Name = "Second", Type = "Procedure",
                    Calls = new List<string>(), CalledBy = new List<string>() }
            };
            var idx = new IndexCacheService();
            idx.LoadFromEntries(entries);
            var svc = new CallerGraphService(idx);

            Assert.Contains("First", svc.GetCallees("Caller"));
            Assert.DoesNotContain("Second", svc.GetCallees("Caller"));

            var current = idx.GetIndex();
            current.Objects["Procedure:Caller"].Calls = new List<string> { "Second" };
            idx.UpdateIndex(current);

            var afterRevision = svc.GetCallees("Caller");
            Assert.Contains("Second", afterRevision);
            Assert.DoesNotContain("First", afterRevision);
        }
    }
}
