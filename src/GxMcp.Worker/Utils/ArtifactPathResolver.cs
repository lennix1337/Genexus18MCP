using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GxMcp.Worker.Utils
{
    public sealed class ArtifactPathException : InvalidOperationException
    {
        public ArtifactPathException(string message)
            : base(message)
        {
        }
    }

    public enum ArtifactKind
    {
        Documentation,
        Html
    }

    /// <summary>
    /// The durable output locations for generated documentation artifacts belonging to one KB.
    /// </summary>
    public sealed class ArtifactPaths
    {
        internal ArtifactPaths(string rootDirectory, string kbScopeDirectory, string documentationDirectory, string htmlDirectory, string kbIdentity)
        {
            RootDirectory = rootDirectory;
            KbScopeDirectory = kbScopeDirectory;
            DocumentationDirectory = documentationDirectory;
            HtmlDirectory = htmlDirectory;
            KbIdentity = kbIdentity;
        }

        public string RootDirectory { get; }
        public string KbScopeDirectory { get; }
        public string DocumentationDirectory { get; }
        public string HtmlDirectory { get; }
        public string KbIdentity { get; }
    }

    /// <summary>
    /// Resolves generated documentation output outside the Worker installation and inside a
    /// stable per-KB scope. A configured root is still scoped by the KB identity; configuring a
    /// common root therefore cannot make two KBs silently share docs or HTML files.
    /// </summary>
    public sealed class ArtifactPathResolver
    {
        public const string OutputDirectoryEnvironmentVariable = "GXMCP_ARTIFACT_OUTPUT_DIR";

        private readonly Func<string> _kbPathProvider;
        private readonly string _configuredRoot;
        private readonly string _baseDirectory;
        private readonly string _localAppData;

        public ArtifactPathResolver(
            Func<string> kbPathProvider,
            string configuredRoot = null,
            string baseDirectory = null,
            string localAppData = null)
        {
            _kbPathProvider = kbPathProvider ?? throw new ArgumentNullException(nameof(kbPathProvider));
            _configuredRoot = configuredRoot;
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
            _localAppData = string.IsNullOrWhiteSpace(localAppData)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : localAppData;
        }

        public ArtifactPaths Resolve()
        {
            string kbPath = _kbPathProvider();
            if (string.IsNullOrWhiteSpace(kbPath))
                throw new ArtifactPathException("A Knowledge Base must be open before resolving documentation artifacts.");

            string canonicalKbPath = CanonicalizeKbPath(kbPath);
            string root = ResolveRootDirectory();
            string identity = ComputeKbIdentity(canonicalKbPath);
            string scope = Path.Combine(root, "kb-" + identity);

            string resolvedScope;
            if (!PathSafety.TryResolveWithinRoot(root, scope, out resolvedScope))
                throw new ArtifactPathException("The generated artifact KB scope escaped its configured output directory.");

            return new ArtifactPaths(
                root,
                resolvedScope,
                Path.Combine(resolvedScope, "docs"),
                Path.Combine(resolvedScope, "html"),
                identity);
        }

        /// <summary>
        /// Resolves one file beneath the selected artifact kind and rejects path components rather
        /// than replacing them. Rejection keeps containment explicit and avoids collisions caused by
        /// mapping two different object names to the same sanitized filename.
        /// </summary>
        public string ResolveFile(ArtifactKind kind, string fileName)
        {
            ArtifactPaths paths = Resolve();
            string directory;
            switch (kind)
            {
                case ArtifactKind.Documentation:
                    directory = paths.DocumentationDirectory;
                    break;
                case ArtifactKind.Html:
                    directory = paths.HtmlDirectory;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            string safeName = ValidateFileName(fileName);
            string candidate = Path.Combine(directory, safeName);
            string fullPath;
            if (!PathSafety.TryResolveWithinRoot(directory, candidate, out fullPath)
                || !string.Equals(Path.GetFileName(fullPath), safeName, StringComparison.Ordinal))
            {
                throw new ArtifactPathException("The generated artifact filename is outside its output directory.");
            }
            return fullPath;
        }

        public static string ValidateFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArtifactPathException("The generated artifact filename is empty.");
            if (fileName == "." || fileName == ".."
                || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0
                || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            {
                throw new ArtifactPathException("The generated artifact filename must be a single path component.");
            }

            foreach (char c in fileName)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                    throw new ArtifactPathException("The generated artifact filename contains an invalid character.");
            }
            return fileName;
        }

        private string ResolveRootDirectory()
        {
            string configured = string.IsNullOrWhiteSpace(_configuredRoot)
                ? Environment.GetEnvironmentVariable(OutputDirectoryEnvironmentVariable)
                : _configuredRoot;

            string root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(_localAppData, "GxMcp", "Artifacts")
                : Environment.ExpandEnvironmentVariables(configured.Trim());

            if (!Path.IsPathRooted(root))
                root = Path.Combine(_baseDirectory, root);
            string full = Path.GetFullPath(root);
            string pathRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(pathRoot)
                && string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase))
                return full;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string CanonicalizeKbPath(string kbPath)
        {
            string canonical = kbPath;
            try { canonical = Path.GetFullPath(canonical); } catch { }
            if (canonical.EndsWith(".gxw", StringComparison.OrdinalIgnoreCase))
            {
                try { canonical = Path.GetDirectoryName(canonical) ?? canonical; } catch { }
            }
            return canonical.TrimEnd('\\', '/').ToLowerInvariant();
        }

        private static string ComputeKbIdentity(string canonicalKbPath)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalKbPath ?? string.Empty));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).Substring(0, 16);
            }
        }
    }
}
