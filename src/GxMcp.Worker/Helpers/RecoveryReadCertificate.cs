using System;
using System.Text;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// What the Worker observed while it re-read one object part for a post-timeout
    /// recovery. Plain strings and numbers on purpose: the certificate is derived from
    /// this, never from an SDK type, so every refusal path can be exercised without a
    /// Knowledge Base.
    /// </summary>
    internal sealed class RecoveryReadObservation
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Guid { get; set; }
        public string EntityKey { get; set; }
        /// <summary>Qualified path of the object when the index knows one (best effort, may be null).</summary>
        public string Path { get; set; }
        /// <summary>The part exactly as the caller named it (may be the generic "Source").</summary>
        public string RequestedPart { get; set; }
        /// <summary>The part the SDK actually read after the Worker resolved the alias.</summary>
        public string ResolvedPart { get; set; }
        /// <summary>
        /// The complete text the SDK returned, before pagination and before any
        /// presentation budget. <c>null</c> when the part has no text form.
        /// </summary>
        public string Text { get; set; }
        /// <summary>The token the read produced for <see cref="Text"/>.</summary>
        public string VersionToken { get; set; }
        /// <summary>Offset/limit the caller asked for; they decide whether the read counts as complete.</summary>
        public int? RequestOffset { get; set; }
        public int? RequestLimit { get; set; }
        /// <summary>Per-target write stamp and object revision, sampled before and after the read.</summary>
        public string WriteStampBefore { get; set; }
        public string WriteStampAfter { get; set; }
        public string RevisionBefore { get; set; }
        public string RevisionAfter { get; set; }
        /// <summary>Set when the SDK read itself could not be performed; the certificate then refuses.</summary>
        public string UnavailableReason { get; set; }
    }

    /// <summary>
    /// The pre-cut certificate of a recovery read (<c>gx-recovery-read/1</c>): identity,
    /// SDK coverage, size, hash and version of what the SDK returned, computed on the
    /// complete text before the Gateway paginates, budgets or replaces it. It carries no
    /// content, so its size does not depend on the part's, and it is built by the Worker
    /// from its own observation — nothing in it comes from a caller's declaration.
    /// </summary>
    /// <remarks>
    /// <c>complete</c> is a conjunction and every clause refuses by default: a page that
    /// did not start at line 0 or stopped short, a part without text, a token that was
    /// not derived from the very text that was hashed, an identity the SDK did not give,
    /// and an object or per-target write stamp that moved while the part was being read.
    /// The Gateway still decides whether the certificate matches a fence; this only says
    /// what was read.
    /// </remarks>
    internal static class RecoveryReadCertificate
    {
        internal const string Schema = "gx-recovery-read/1";
        internal const string CoverageComplete = "sdk-complete";
        internal const string CoveragePartial = "sdk-partial";
        internal const string CoverageUnavailable = "sdk-unavailable";
        /// <summary>Value of <c>sha256Basis</c> when <c>sha256</c> is that of the visual XML with its regenerated ids neutralised.</summary>
        internal const string Sha256BasisCanonicalVisualXml = "canonical-visual-xml";

        internal static JObject Build(RecoveryReadObservation observation)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));

            string text = observation.Text;
            // The fingerprint the read's token ends with, which is what the Gateway requires of `sha256`: the hash of the
            // text itself for every part but a visual one, whose token ignores the GUID ids the SDK regenerates on each
            // load (VisualTokenCanonicalizer). `sha256Basis` below says so when the two differ.
            string sha256 = text == null ? null : WriteService.ComputePartContentFingerprint(observation.ResolvedPart, text);
            int totalLines = 0;
            bool windowCoversPart = false;
            if (text != null)
            {
                // The same pagination the read applies, so "complete" means the caller's
                // window, not a guess about it: offset 0 and every line, with no default
                // 200-line / 16 KB page cutting in.
                var page = ReadPagination.ApplyDefault(text, observation.RequestOffset, observation.RequestLimit, "mcp");
                totalLines = page.TotalLines;
                windowCoversPart = !page.Truncated && page.Offset == 0 && page.LinesReturned == page.TotalLines;
            }

            // Whether the object or its per-target write stamp moved while the part was being
            // read (or could not be sampled at all). Kept apart from the reason chain below:
            // an earlier refusal must not hide a race from the Gateway, which vetoes any
            // route that would clear a fence from a read that raced a write.
            bool readPerformed = string.IsNullOrWhiteSpace(observation.UnavailableReason) && text != null;
            bool movedDuringRead = readPerformed
                && (string.IsNullOrWhiteSpace(observation.WriteStampBefore)
                    || string.IsNullOrWhiteSpace(observation.RevisionBefore)
                    || string.IsNullOrWhiteSpace(observation.RevisionAfter)
                    || !string.Equals(observation.WriteStampBefore, observation.WriteStampAfter, StringComparison.Ordinal)
                    || !string.Equals(observation.RevisionBefore, observation.RevisionAfter, StringComparison.Ordinal));

            string coverage = CoverageComplete;
            string reason = null;
            if (!string.IsNullOrWhiteSpace(observation.UnavailableReason))
            {
                coverage = CoverageUnavailable;
                reason = observation.UnavailableReason;
            }
            else if (text == null)
            {
                coverage = CoverageUnavailable;
                reason = "part-has-no-text";
            }
            else if (!windowCoversPart)
            {
                coverage = CoveragePartial;
                reason = "window-does-not-cover-part";
            }
            else if (string.IsNullOrWhiteSpace(observation.VersionToken))
            {
                reason = "version-token-unavailable";
            }
            else if (!observation.VersionToken.EndsWith(":" + sha256, StringComparison.Ordinal))
            {
                // The token and the hash must describe the same bytes; otherwise the part
                // was projected or minimized between them and neither proves the other.
                reason = "version-token-not-derived-from-text";
            }
            else if (string.IsNullOrWhiteSpace(observation.Name)
                || string.IsNullOrWhiteSpace(observation.Guid)
                || string.IsNullOrWhiteSpace(observation.ResolvedPart))
            {
                reason = "identity-incomplete";
            }
            else if (movedDuringRead)
            {
                reason = "changed-during-read";
            }

            var identity = new JObject
            {
                ["name"] = observation.Name,
                ["type"] = observation.Type,
                ["guid"] = observation.Guid,
                ["entityKey"] = observation.EntityKey,
                ["path"] = observation.Path,
                ["part"] = observation.ResolvedPart,
                ["requestedPart"] = observation.RequestedPart
            };

            var certificate = new JObject
            {
                ["schema"] = Schema,
                ["complete"] = reason == null,
                ["coverage"] = coverage,
                ["authoritative"] = string.IsNullOrWhiteSpace(observation.UnavailableReason),
                ["stable"] = !movedDuringRead,
                ["origin"] = "fresh-sdk-read",
                ["identity"] = identity,
                ["totalLines"] = totalLines,
                ["totalChars"] = text == null ? 0 : text.Length,
                // UTF-8 size of the text. Not the read payload's "totalBytes", which counts characters.
                ["utf8Bytes"] = text == null ? 0 : Encoding.UTF8.GetByteCount(text),
                ["sha256"] = sha256,
                ["versionToken"] = observation.VersionToken,
                ["targetWriteStamp"] = observation.WriteStampBefore
            };
            if (reason != null) certificate["reason"] = reason;
            // The hash above is not the SHA-256 of the text when the part is a visual one whose ids were neutralised (for
            // every other part it is the same hash): a caller that hashes its own copy of the text must know it will not match.
            if (text != null
                && !string.Equals(sha256, WriteService.ComputeContentFingerprint(text), StringComparison.Ordinal))
                certificate["sha256Basis"] = Sha256BasisCanonicalVisualXml;
            return certificate;
        }
    }
}
