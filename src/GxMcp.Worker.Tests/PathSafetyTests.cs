using System;
using System.IO;
using GxMcp.Worker.Utils;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PathSafetyTests
    {
        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-pathsafety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        [Fact]
        public void TryResolveWithinRoot_ChildPath_Accepted()
        {
            string root = NewRoot();

            bool ok = PathSafety.TryResolveWithinRoot(root, Path.Combine("sub", "file.txt"), out string fullPath);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "sub", "file.txt")), fullPath);
        }

        [Fact]
        public void TryResolveWithinRoot_EscapingTraversal_Rejected()
        {
            string root = NewRoot();

            bool ok = PathSafety.TryResolveWithinRoot(root, Path.Combine("..", "..", "outside.txt"), out string fullPath);

            Assert.False(ok);
        }

        [Fact]
        public void TryResolveWithinRoot_AbsoluteEscapingPath_Rejected()
        {
            string root = NewRoot();
            string outside = Path.Combine(Path.GetTempPath(), "gxmcp-pathsafety-other-" + Guid.NewGuid().ToString("N"), "file.txt");

            bool ok = PathSafety.TryResolveWithinRoot(root, outside, out string fullPath);

            Assert.False(ok);
        }

        [Fact]
        public void TryResolveWithinRoot_TrailingSeparatorOnRoot_StillWorks()
        {
            string root = NewRoot();
            string rootWithSeparator = root + Path.DirectorySeparatorChar;

            bool ok = PathSafety.TryResolveWithinRoot(rootWithSeparator, "file.txt", out string fullPath);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "file.txt")), fullPath);
        }

        [Fact]
        public void TryResolveWithinRoot_FileSystemRoot_UsesRootBoundary()
        {
            string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
            string candidate = Path.Combine(root, "gxmcp-pathsafety-root-test", "file.txt");

            bool ok = PathSafety.TryResolveWithinRoot(root, candidate, out string fullPath);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(candidate), fullPath);
        }

        [Fact]
        public void TryResolveWithinRoot_SameCasingDifference_IsCaseInsensitive()
        {
            string root = NewRoot();
            string upperRoot = root.ToUpperInvariant();

            bool ok = PathSafety.TryResolveWithinRoot(upperRoot, "file.txt", out string fullPath);

            Assert.True(ok);
        }

        [Fact]
        public void TryResolveWithinRoot_RootItself_Accepted()
        {
            string root = NewRoot();

            bool ok = PathSafety.TryResolveWithinRoot(root, null, out string fullPath);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(root), fullPath);
        }

        [Fact]
        public void MakeRelative_ChildPath_RoundTripsWithForwardSlashes()
        {
            string root = NewRoot();
            string full = Path.Combine(root, "sub", "file.txt");

            string rel = PathSafety.MakeRelative(root, full);

            Assert.Equal("sub/file.txt", rel);
        }

        [Fact]
        public void MakeRelative_SamePath_ReturnsDot()
        {
            string root = NewRoot();

            string rel = PathSafety.MakeRelative(root, root);

            Assert.Equal(".", rel);
        }

        [Fact]
        public void MakeRelative_PathOutsideRoot_ReturnsOriginalUnchanged()
        {
            string root = NewRoot();
            string outside = Path.Combine(Path.GetTempPath(), "gxmcp-pathsafety-other-" + Guid.NewGuid().ToString("N"), "file.txt");

            string rel = PathSafety.MakeRelative(root, outside);

            Assert.Equal(outside, rel);
        }

        [Fact]
        public void MakeRelative_TrailingSeparatorOnRoot_StillStrips()
        {
            string root = NewRoot();
            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            string full = Path.Combine(root, "file.txt");

            string rel = PathSafety.MakeRelative(rootWithSeparator, full);

            Assert.Equal("file.txt", rel);
        }

        [Fact]
        public void MakeRelative_CaseInsensitiveRootMatch()
        {
            string root = NewRoot();
            string full = Path.Combine(root, "file.txt");

            string rel = PathSafety.MakeRelative(root.ToUpperInvariant(), full);

            Assert.Equal("file.txt", rel);
        }
    }
}
