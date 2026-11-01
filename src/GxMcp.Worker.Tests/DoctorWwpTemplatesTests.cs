using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DoctorWwpTemplatesTests
    {
        private const string SynthManifestPath = "C:\\SynthPatterns\\WorkWithPlus.Pattern";

        // A registry in which the WorkWithPlus manifest was discovered on disk.
        private static PatternRegistry InstalledRegistry()
        {
            return new PatternRegistry(new List<PatternManifest>
            {
                new PatternManifest { Id = PatternRegistry.WorkWithPlusPatternId, Name = "WorkWithPlus", ManifestPath = SynthManifestPath }
            });
        }

        // A registry without a discovered manifest: it holds only the placeholder.
        private static PatternRegistry NotInstalledRegistry()
        {
            return new PatternRegistry(new List<PatternManifest>());
        }

        [Fact]
        public void Without_an_open_kb_the_block_says_KbNotOpen()
        {
            var block = new DoctorService(null, null).BuildWwpTemplatesBlock(InstalledRegistry());

            Assert.False(block["available"]?.ToObject<bool>());
            Assert.Equal("KbNotOpen", block["reason"]?.ToString());
        }

        [Fact]
        public void Without_WorkWithPlus_installed_the_block_says_so_and_has_no_count_to_warn_about()
        {
            var block = new DoctorService(null, null).BuildWwpTemplatesBlock(NotInstalledRegistry());

            Assert.False(block["available"]?.ToObject<bool>());
            Assert.Equal("WorkWithPlusNotInstalled", block["reason"]?.ToString());
            Assert.Null(block["invalid"]);
        }

        [Fact]
        public void A_manifest_without_a_path_is_not_an_installation()
        {
            Assert.False(DoctorService.IsWorkWithPlusInstalled(null));
            Assert.False(DoctorService.IsWorkWithPlusInstalled(new PatternManifest { Id = PatternRegistry.WorkWithPlusPatternId }));
            Assert.True(DoctorService.IsWorkWithPlusInstalled(new PatternManifest
            {
                Id = PatternRegistry.WorkWithPlusPatternId,
                ManifestPath = SynthManifestPath
            }));
        }

        [Fact]
        public void A_KB_handle_that_is_not_the_SDK_model_says_UnsupportedDriver()
        {
            var service = new DoctorService(null, null);
            service.SetKbProviderForTest(() => new object());

            var block = service.BuildWwpTemplatesBlock(InstalledRegistry());

            Assert.Equal("UnsupportedDriver", block["reason"]?.ToString());
        }

        [Fact]
        public void The_action_answers_a_command_envelope_whose_result_is_the_block()
        {
            // The install state of the machine decides the reason, so only the envelope and availability are fixed here.
            var json = JObject.Parse(new DoctorService(null, null).WwpTemplatesResponse());

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("DoctorWwpTemplatesOk", json["code"]?.ToString());
            Assert.False(json["result"]?["available"]?.ToObject<bool>());
            Assert.NotNull(json["result"]?["reason"]);
        }

        [Fact]
        public void Reading_the_block_with_the_KB_closed_does_not_start_the_background_open()
        {
            // With GX_KB_PATH set, GetKB() on a closed KB starts an open that fails for this path and counts the failure.
            // The counter stays at zero only when the block read leaves the KB alone.
            string previous = Environment.GetEnvironmentVariable("GX_KB_PATH");
            Environment.SetEnvironmentVariable("GX_KB_PATH", @"C:\NotAnExistingKb\Synthetic");
            try
            {
                var kb = new KbService(null);

                var block = new DoctorService(kb, null).BuildWwpTemplatesBlock(InstalledRegistry());

                Assert.Equal("KbNotOpen", block["reason"]?.ToString());
                Thread.Sleep(500);
                var failures = typeof(KbService).GetField("_autoOpenFailCount", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.Equal(0, (int)failures.GetValue(kb));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_KB_PATH", previous);
            }
        }
    }
}
