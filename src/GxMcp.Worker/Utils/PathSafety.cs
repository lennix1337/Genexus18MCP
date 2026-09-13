using System;
using System.IO;

namespace GxMcp.Worker.Utils
{
    /// <summary>
    /// Shared "is this path inside the KB/repo root" and "make this path relative to root"
    /// helpers. Consolidates logic that used to be reimplemented (with subtly different edge
    /// cases) in AssetService, BlameService, TimeTravelService and GeneratedDiffService.
    /// </summary>
    internal static class PathSafety
    {
        /// <summary>
        /// Resolves <paramref name="candidate"/> against <paramref name="root"/> (candidate may be
        /// absolute or relative to root; null/empty resolves to root itself) and checks whether the
        /// resolved path is root itself or a descendant of it. Comparison is case-insensitive and
        /// tolerant of a trailing directory separator on <paramref name="root"/>.
        /// </summary>
        /// <param name="root">The directory the resolved path must stay within.</param>
        /// <param name="candidate">Absolute path, or a path relative to <paramref name="root"/>.</param>
        /// <param name="fullPath">The resolved full path, set even when the result is false (the
        /// caller can use it for diagnostics but must not act on it when the return value is false).</param>
        /// <returns>true if the resolved path is root or a descendant of root; false if it escapes root.</returns>
        public static bool TryResolveWithinRoot(string root, string candidate, out string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("root is required.", nameof(root));

            string rootFull = NormalizeRoot(root);

            string resolved = string.IsNullOrWhiteSpace(candidate)
                ? rootFull
                : (Path.IsPathRooted(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(Path.Combine(rootFull, candidate)));

            fullPath = resolved;

            string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            bool isSamePath = string.Equals(resolved, rootFull, StringComparison.OrdinalIgnoreCase);
            bool isChildPath = resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
            return isSamePath || isChildPath;
        }

        private static string NormalizeRoot(string root)
        {
            string full = Path.GetFullPath(root);
            string pathRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(pathRoot)
                && string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase))
                return full;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Expresses <paramref name="fullPath"/> relative to <paramref name="root"/> using forward
        /// slashes. Returns "." when the two paths are the same directory, and returns
        /// <paramref name="fullPath"/> unchanged (fallback, not an error) when it isn't under root.
        /// </summary>
        public static string MakeRelative(string root, string fullPath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return fullPath;

            string rootFull;
            string resolved;
            try
            {
                rootFull = NormalizeRoot(root);
                resolved = Path.GetFullPath(fullPath);
            }
            catch
            {
                return fullPath;
            }

            if (string.Equals(rootFull, resolved, StringComparison.OrdinalIgnoreCase))
                return ".";

            string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            return resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                ? resolved.Substring(rootWithSeparator.Length).Replace('\\', '/')
                : fullPath;
        }
    }
}
