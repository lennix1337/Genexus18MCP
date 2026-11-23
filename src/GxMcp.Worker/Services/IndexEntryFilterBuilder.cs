using System;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    // Plan 006: shared predicate factory for the filter chains that SearchService
    // and ListService independently re-implemented over IEnumerable<SearchIndex.IndexEntry>.
    //
    // Scope note (see plans/006-shared-filter-builder.md): only predicates whose
    // logic was byte-for-byte identical in both services were centralized here
    // (description substring, date range). Everything else stayed put:
    //   - Type matching is a DELIBERATE divergence, not an oversight. SearchService
    //     uses the alias/synonym-aware IsTypeMatchAliasAware below ("prc" contains-matches
    //     "Procedure"); ListService matches an exact, caller-supplied type-name set
    //     (`filterTypes.Contains(e.Type)`, still inline in ListService.ListObjects).
    //     Unifying on alias-aware matching would make List's typeFilter over-match
    //     (e.g. "WebForm" would start matching "WebFormAttribute"), which existing
    //     List tests pin against — so both behaviors are preserved, and the
    //     alias-aware algorithm is centralized here (single source of truth) so it
    //     doesn't drift out from under SearchService's four call sites.
    //   - Domain filtering only exists in SearchService (ListService has no
    //     domainFilter parameter at all) — nothing to share, left inline.
    //   - Parent/parentPath equality-as-safety-net only exists in SearchService
    //     (ListService relies solely on the ChildrenByParent index lookup, with no
    //     follow-up equality Where) — nothing to share, left inline.
    //   - The ListService runtime-SDK fallback path (no index available) operates
    //     over RuntimeListEntry/KBObject, not SearchIndex.IndexEntry — out of scope.
    internal static class IndexEntryFilterBuilder
    {
        /// <summary>
        /// Alias/synonym-aware type match — moved verbatim from SearchService.IsTypeMatch.
        /// "prc"/"proc" match any type containing "procedure", "tab"/"table" requires an
        /// exact "table" type, etc.; falls back to case-insensitive substring containment.
        /// </summary>
        public static bool IsTypeMatchAliasAware(string type, string query)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(query)) return false;

            if (string.Equals(query, "prc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "procedure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "proc", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "trn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "transaction", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("transaction", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "tab", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "table", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(type, "table", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(query, "wp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "webpanel", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("webpanel", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "dp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "dataprovider", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("dataprovider", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "sdt", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("sdt", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "attr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "attribute", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool TryPrepareLowerAscii(string needle, out string lowerAscii)
        {
            if (string.IsNullOrEmpty(needle))
            {
                lowerAscii = string.Empty;
                return true;
            }

            bool hasUpper = false;
            for (int i = 0; i < needle.Length; i++)
            {
                char c = needle[i];
                if (c >= 128)
                {
                    lowerAscii = needle;
                    return false;
                }
                if (c >= 'A' && c <= 'Z') hasUpper = true;
            }

            if (!hasUpper)
            {
                lowerAscii = needle;
                return true;
            }

            char[] buf = new char[needle.Length];
            for (int i = 0; i < needle.Length; i++)
            {
                char c = needle[i];
                buf[i] = (c >= 'A' && c <= 'Z') ? (char)(c | 0x20) : c;
            }
            lowerAscii = new string(buf);
            return true;
        }

        /// <summary>
        /// Managed ASCII fast path for <c>haystack.IndexOf(needle, startIndex, StringComparison.OrdinalIgnoreCase) >= 0</c>.
        /// Falls back immediately to <see cref="string.IndexOf(string, int, StringComparison)"/> on any
        /// non-ASCII char (&gt;= 128) so semantics are bit-for-bit identical for all UTF-16 inputs while
        /// avoiding .NET Framework 4.8 FCall overhead on ASCII GeneXus identifiers and descriptions.
        /// </summary>
        internal static bool ContainsIgnoreCaseFast(string haystack, int startIndex, string preparedNeedle, bool needleIsLowerAscii)
        {
            if (haystack == null) return false;
            if (!needleIsLowerAscii)
                return haystack.IndexOf(preparedNeedle, startIndex, StringComparison.OrdinalIgnoreCase) >= 0;

            int needleLen = preparedNeedle.Length;
            if (needleLen == 0) return startIndex <= haystack.Length;
            int lastStart = haystack.Length - needleLen;
            if (startIndex > lastStart) return false;

            char firstLower = preparedNeedle[0];
            char firstUpper = (firstLower >= 'a' && firstLower <= 'z') ? (char)(firstLower - 32) : firstLower;

            for (int i = startIndex; i <= lastStart; i++)
            {
                char ch = haystack[i];
                if (ch >= 128)
                    return haystack.IndexOf(preparedNeedle, i, StringComparison.OrdinalIgnoreCase) >= 0;
                if (ch != firstLower && ch != firstUpper) continue;

                int j = 1;
                for (; j < needleLen; j++)
                {
                    char hc = haystack[i + j];
                    if (hc >= 128)
                        return haystack.IndexOf(preparedNeedle, i, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hc >= 'A' && hc <= 'Z') hc = (char)(hc | 0x20);
                    if (hc != preparedNeedle[j]) break;
                }
                if (j == needleLen) return true;
            }
            return false;
        }

        internal static bool StartsWithIgnoreCaseFast(string haystack, string preparedNeedle, bool needleIsLowerAscii)
        {
            if (haystack == null) return false;
            if (!needleIsLowerAscii)
                return haystack.StartsWith(preparedNeedle, StringComparison.OrdinalIgnoreCase);

            int needleLen = preparedNeedle.Length;
            if (haystack.Length < needleLen) return false;
            for (int j = 0; j < needleLen; j++)
            {
                char hc = haystack[j];
                if (hc >= 128)
                    return haystack.StartsWith(preparedNeedle, StringComparison.OrdinalIgnoreCase);
                if (hc >= 'A' && hc <= 'Z') hc = (char)(hc | 0x20);
                if (hc != preparedNeedle[j]) return false;
            }
            return true;
        }

        /// <summary>Case-insensitive substring match on Description. Empty/null filter matches everything.</summary>
        public static Func<SearchIndex.IndexEntry, bool> DescriptionContains(string descriptionFilter)
        {
            if (string.IsNullOrEmpty(descriptionFilter)) return _ => true;
            bool isAscii = TryPrepareLowerAscii(descriptionFilter, out string prepared);
            return e => e.Description != null && ContainsIgnoreCaseFast(e.Description, 0, prepared, isAscii);
        }

        /// <summary>Since is inclusive: LastUpdate &gt;= since.</summary>
        public static Func<SearchIndex.IndexEntry, bool> SinceInclusive(DateTime since)
        {
            return e => e.LastUpdate >= since;
        }

        /// <summary>ModifiedBefore is exclusive and requires a known LastUpdate: DateTime.MinValue never matches.</summary>
        public static Func<SearchIndex.IndexEntry, bool> ModifiedBeforeExclusive(DateTime modifiedBefore)
        {
            return e => e.LastUpdate > DateTime.MinValue && e.LastUpdate < modifiedBefore;
        }
    }
}
