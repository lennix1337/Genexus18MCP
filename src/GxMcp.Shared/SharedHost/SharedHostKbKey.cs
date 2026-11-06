#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GxMcp.Common.SharedHost
{
    // The registry v2 key of a KB directory (design decision A3). This is the single implementation shared by the
    // Gateway and the Worker_Host, so the two processes can never compute different keys for one KB.
    //
    //   normalized = Path.GetFullPath(kbPath), every trailing '\' and '/' removed, ToLowerInvariant()
    //                (kbPath.Trim() instead of Path.GetFullPath when the latter throws)
    //   kbKey      = lowercase hexadecimal SHA-256 of the UTF-8 bytes of normalized (64 characters)
    internal static class SharedHostKbKey
    {
        // Returns string.Empty for a null or whitespace path, matching the Gateway's SharedWorkerIdentity.NormalizePath.
        internal static string NormalizeKbPath(string? kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath))
            {
                return string.Empty;
            }

            string full;
            try
            {
                full = Path.GetFullPath(kbPath);
            }
            catch (Exception)
            {
                // Invalid characters, unsupported format or an over-long path: fall back to the trimmed input.
                full = kbPath!.Trim();
            }

            return full.TrimEnd('\\', '/').ToLowerInvariant();
        }

        internal static string Compute(string? kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath))
            {
                throw new ArgumentException("A KB path is required to compute a kbKey.", nameof(kbPath));
            }

            string normalized = NormalizeKbPath(kbPath);
            byte[] digest;
            using (var sha = SHA256.Create())
            {
                digest = sha.ComputeHash(new UTF8Encoding(false).GetBytes(normalized));
            }

            var key = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest)
            {
                key.Append(value.ToString("x2"));
            }

            return key.ToString();
        }
    }
}
