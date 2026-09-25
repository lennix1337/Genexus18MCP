using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

// FR#20 (v2.6.6 Stream B): soft-reload must persist BackgroundJobRegistry entries
// across the worker restart so an in-flight task_id stays valid for lifecycle calls.
// These tests stand in for the full round-trip (worker exit → gateway respawn →
// worker startup notification) by exercising SaveTo / LoadFrom directly.
public class BackgroundJobRegistryPersistenceTests
{
    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gxmcp-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "jobs.json");
    }

    [Fact]
    public void SaveTo_PersistsRunningJobs()
    {
        var r = new BackgroundJobRegistry(600);
        var j = r.Start("session-A", "build", 30);
        j.Summary = "snapshot in flight";

        string path = TempPath();
        try
        {
            r.SaveTo(path);
            Assert.True(File.Exists(path));
            string raw = File.ReadAllText(path);
            Assert.Contains(j.Id, raw);
            Assert.Contains("running", raw);
            Assert.Contains("session-A", raw);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void LoadFrom_RehydratesIntoFreshRegistry()
    {
        var src = new BackgroundJobRegistry(600);
        var jA = src.Start("s1", "build", 60);
        var jB = src.Start("s2", "edit", 10);
        src.Complete(jB.Id, true, "done", new JObject { ["ok"] = true });

        string path = TempPath();
        try
        {
            src.SaveTo(path);

            var dst = new BackgroundJobRegistry(600);
            int loaded = dst.LoadFrom(path, deleteAfterRead: true);
            Assert.Equal(2, loaded);

            var aRestored = dst.Get(jA.Id);
            Assert.NotNull(aRestored);
            Assert.Equal("running", aRestored!.Status);
            Assert.Equal("s1", aRestored.Session);

            var bRestored = dst.Get(jB.Id);
            Assert.NotNull(bRestored);
            Assert.Equal("succeeded", bRestored!.Status);
            Assert.NotNull(bRestored.Result);
            Assert.True((bool)bRestored.Result!["ok"]!);

            // deleteAfterRead=true means consumer file gone.
            Assert.False(File.Exists(path));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void LoadFrom_DoesNotResurrectJobsCompletedAfterSnapshot()
    {
        var registry = new BackgroundJobRegistry(600);
        var fence = new OwnershipFence("session", "physical-kb", 1);
        var running = registry.AdmitLifecycle("session", "lifecycle/build", 30,
            fence, "physical-kb", "build|A");
        var queued = registry.AdmitLifecycle("session-2", "lifecycle/build", 30,
            fence, "physical-kb", "build|B");
        string path = TempPath();
        try
        {
            registry.SaveTo(path);
            registry.Complete(running.Job.Id, true, "completed after snapshot");
            registry.Cancel(queued.Job.Id, "cancelled after snapshot");

            registry.LoadFrom(path);

            Assert.Equal("succeeded", registry.Get(running.Job.Id)!.Status);
            Assert.Equal("cancelled", registry.Get(queued.Job.Id)!.Status);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void LoadFrom_CancellationDuringMergeCannotResurrectTerminalJob()
    {
        var registry = new BackgroundJobRegistry(600);
        var job = registry.Start("session", "edit", 30);
        string path = TempPath();
        try
        {
            registry.SaveTo(path);
            // The hook runs inside the merge critical section. Cancel re-enters
            // the per-job monitor, making the terminal transition deterministic
            // rather than scheduler-dependent.
            registry.LoadMergeTransitionForTest = () =>
                Assert.True(registry.Cancel(job.Id, "cancelled during reload"));

            registry.LoadFrom(path, deleteAfterRead: false);

            Assert.Same(job, registry.Get(job.Id));
            Assert.Equal("cancelled", registry.Get(job.Id)!.Status);
        }
        finally
        {
            registry.LoadMergeTransitionForTest = null;
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    [Fact]
    public void LoadFrom_RebuildsLifecycleFifoAndAdmissionSignals()
    {
        var pre = new BackgroundJobRegistry(600);
        var fence = new OwnershipFence("session", "physical-kb", 1);
        var first = pre.AdmitLifecycle("session", "lifecycle/build", 30,
            fence, "physical-kb", "build|A");
        var second = pre.AdmitLifecycle("session-2", "lifecycle/build", 30,
            fence, "physical-kb", "build|B");
        Assert.Equal("running", first.Job.Status);
        Assert.Equal("queued", second.Job.Status);

        string path = TempPath();
        try
        {
            pre.SaveTo(path);
            var post = new BackgroundJobRegistry(600);
            post.LoadFrom(path);

            var third = post.AdmitLifecycle("session-3", "lifecycle/build", 30,
                fence, "physical-kb", "build|C");
            Assert.Equal("queued", third.Job.Status);
            Assert.Equal(2, third.Job.QueuePosition);

            post.Complete(first.Job.Id, true, "done");
            Assert.Equal("running", post.Get(second.Job.Id)!.Status);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public async Task LoadFrom_PreservesLiveAdmissionWaiterWhileRebuildingQueue()
    {
        var registry = new BackgroundJobRegistry(600);
        var fence = new OwnershipFence("session", "physical-kb", 1);
        var running = registry.AdmitLifecycle("session", "lifecycle/build", 30,
            fence, "physical-kb", "build|A");
        var queued = registry.AdmitLifecycle("session-2", "lifecycle/build", 30,
            fence, "physical-kb", "build|B");
        Task<bool> waiter = registry.WaitForLifecycleAdmissionAsync(queued.Job.Id);

        string path = TempPath();
        try
        {
            registry.SaveTo(path);
            registry.LoadFrom(path);

            bool remainedPendingAfterReload = !waiter.IsCompleted;
            registry.Complete(running.Job.Id, true, "done after reload");
            bool admitted = await waiter.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(remainedPendingAfterReload,
                "Loading a live snapshot must not resolve an admission waiter.");
            Assert.True(admitted);
            Assert.Equal("running", registry.Get(queued.Job.Id)!.Status);
        }
        finally
        {
            registry.Cancel(queued.Job.Id, "test cleanup");
            registry.Cancel(running.Job.Id, "test cleanup");
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    [Fact]
    public void LoadFrom_MissingFileReturnsZero()
    {
        var dst = new BackgroundJobRegistry(600);
        int loaded = dst.LoadFrom(Path.Combine(Path.GetTempPath(), "definitely-not-there-" + Guid.NewGuid().ToString("N"), "jobs.json"));
        Assert.Equal(0, loaded);
    }

    [Fact]
    public void SaveTo_IsAtomicAgainstPartialWrites()
    {
        // Surface the .tmp-then-move pattern: after SaveTo, no .tmp leftovers.
        var r = new BackgroundJobRegistry(600);
        r.Start("s1", "build", 30);
        string path = TempPath();
        try
        {
            r.SaveTo(path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void LifecycleStatus_RemainsAddressableAfterSoftReloadRoundTrip()
    {
        // Simulates a soft reload: snapshot → fresh registry → Get(jobId) still works.
        // This is the user-visible quality bar — "Lifecycle status calls for those
        // taskIds continue working across the restart."
        var pre = new BackgroundJobRegistry(600);
        var j = pre.Start("session-X", "build", 45);
        string preserved = j.Id;
        string path = TempPath();
        try
        {
            pre.SaveTo(path);
            var post = new BackgroundJobRegistry(600);
            post.LoadFrom(path);

            var found = post.Get(preserved);
            Assert.NotNull(found);
            Assert.Equal("running", found!.Status);

            // Subsequent Complete on the rehydrated entry promotes status as expected.
            post.Complete(preserved, true, "post-restart");
            Assert.Equal("succeeded", post.Get(preserved)!.Status);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}
