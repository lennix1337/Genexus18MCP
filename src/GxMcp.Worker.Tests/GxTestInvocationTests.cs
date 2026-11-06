using System;
using System.IO;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GxTestInvocationTests
    {
        // GeneXus installs GXtest as GXtest.msbuild beside Genexus.Tasks.targets; there is no
        // Abstracta.GXtest.Tasks.targets and no MSBuild.exe in the install folder.
        [Fact]
        public void The_GXtest_project_is_found_in_the_install_folder()
        {
            string gx = Path.Combine(Path.GetTempPath(), "gxmcp-gx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(gx);
            try
            {
                Assert.Null(TestService.ResolveGxTestProject(gx));

                File.WriteAllText(Path.Combine(gx, "GXtest.msbuild"), "<Project />");

                Assert.Equal(Path.Combine(gx, "GXtest.msbuild"), TestService.ResolveGxTestProject(gx));
            }
            finally
            {
                Directory.Delete(gx, true);
            }
        }

        [Fact]
        public void An_install_without_GXtest_resolves_to_nothing()
        {
            Assert.Null(TestService.ResolveGxTestProject(null));
            Assert.Null(TestService.ResolveGxTestProject(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"))));
        }

        [Fact]
        public void The_tests_run_through_the_RunTests_target_with_a_semicolon_list()
        {
            string args = TestService.BuildGxTestArguments(@"C:\GX\GXtest.msbuild", @"C:\KBs\KbTeste", "TestA,TestB");

            Assert.Contains("/t:RunTests", args);
            Assert.Contains("\"C:\\GX\\GXtest.msbuild\"", args);
            Assert.Contains("\"/p:KBPath=C:\\KBs\\KbTeste\"", args);
            Assert.Contains("\"/p:TestObjects=TestA;TestB\"", args);
        }
    }
}
