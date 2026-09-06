using System;
using System.Collections.Generic;
using System.Text;

namespace GxMcp.Worker.Services
{
    // Conservative fallback for rename operations when the SDK does not expose a
    // proven semantic refactor API. It recognizes identifiers only in executable
    // text, preserving comments, quoted strings, and larger identifiers such as
    // CustomerId when renaming Customer.
    public sealed class SymbolOccurrence
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    public static class SymbolRenameTokenizer
    {
        public static IReadOnlyList<SymbolOccurrence> Find(string source, string symbol)
            => FindInternal(source, symbol, requireAmpersand: false);

        public static IReadOnlyList<SymbolOccurrence> FindPrefixed(string source, string symbol)
            => FindInternal(source, symbol, requireAmpersand: true);

        private static IReadOnlyList<SymbolOccurrence> FindInternal(string source, string symbol, bool requireAmpersand)
        {
            var matches = new List<SymbolOccurrence>();
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(symbol)) return matches;

            Scan(source, token =>
            {
                if (string.Equals(token.Text, symbol, StringComparison.OrdinalIgnoreCase)
                    && (!requireAmpersand || token.Offset > 0 && source[token.Offset - 1] == '&'))
                {
                    int offset = requireAmpersand ? token.Offset - 1 : token.Offset;
                    matches.Add(new SymbolOccurrence
                    {
                        Offset = offset,
                        Length = token.Text.Length + (requireAmpersand ? 1 : 0),
                        Line = token.Line,
                        Column = token.Column - (requireAmpersand ? 1 : 0)
                    });
                }
            });
            return matches;
        }

        public static string Rewrite(string source, string oldSymbol, string newSymbol, out int replacements)
        {
            replacements = 0;
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(oldSymbol)
                || newSymbol == null || string.Equals(oldSymbol, newSymbol, StringComparison.Ordinal))
                return source;

            var matches = Find(source, oldSymbol);
            if (matches.Count == 0) return source;

            var updated = new StringBuilder(source.Length + matches.Count * Math.Max(0, newSymbol.Length - oldSymbol.Length));
            int cursor = 0;
            foreach (var match in matches)
            {
                updated.Append(source, cursor, match.Offset - cursor);
                updated.Append(newSymbol);
                cursor = match.Offset + match.Length;
                replacements++;
            }
            updated.Append(source, cursor, source.Length - cursor);
            return updated.ToString();
        }

        public static string RewritePrefixed(string source, string oldSymbol, string newSymbol, out int replacements)
        {
            replacements = 0;
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(oldSymbol) || newSymbol == null)
                return source;

            var matches = FindPrefixed(source, oldSymbol);
            if (matches.Count == 0) return source;

            string replacement = "&" + newSymbol.TrimStart('&');
            var updated = new StringBuilder(source.Length + matches.Count * Math.Max(0, replacement.Length - oldSymbol.Length - 1));
            int cursor = 0;
            foreach (var match in matches)
            {
                updated.Append(source, cursor, match.Offset - cursor);
                updated.Append(replacement);
                cursor = match.Offset + match.Length;
                replacements++;
            }
            updated.Append(source, cursor, source.Length - cursor);
            return updated.ToString();
        }

        private sealed class Token
        {
            public string Text;
            public int Offset;
            public int Line;
            public int Column;
        }

        private enum LexState
        {
            Code,
            LineComment,
            BlockComment,
            SingleQuoted,
            DoubleQuoted
        }

        private static void Scan(string source, Action<Token> onIdentifier)
        {
            LexState state = LexState.Code;
            int line = 1;
            int column = 1;

            for (int i = 0; i < source.Length;)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (state == LexState.LineComment)
                {
                    if (c == '\r' || c == '\n') state = LexState.Code;
                    Advance(source, ref i, ref line, ref column);
                    continue;
                }
                if (state == LexState.BlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        Advance(source, ref i, ref line, ref column);
                        Advance(source, ref i, ref line, ref column);
                        state = LexState.Code;
                    }
                    else Advance(source, ref i, ref line, ref column);
                    continue;
                }
                if (state == LexState.SingleQuoted || state == LexState.DoubleQuoted)
                {
                    char quote = state == LexState.SingleQuoted ? '\'' : '"';
                    if (c == '\\' && i + 1 < source.Length)
                    {
                        Advance(source, ref i, ref line, ref column);
                        Advance(source, ref i, ref line, ref column);
                        continue;
                    }
                    if (c == quote)
                    {
                        if (next == quote)
                        {
                            Advance(source, ref i, ref line, ref column);
                            Advance(source, ref i, ref line, ref column);
                            continue;
                        }
                        state = LexState.Code;
                    }
                    Advance(source, ref i, ref line, ref column);
                    continue;
                }

                if (c == '/' && next == '/')
                {
                    Advance(source, ref i, ref line, ref column);
                    Advance(source, ref i, ref line, ref column);
                    state = LexState.LineComment;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    Advance(source, ref i, ref line, ref column);
                    Advance(source, ref i, ref line, ref column);
                    state = LexState.BlockComment;
                    continue;
                }
                if (c == '\'')
                {
                    state = LexState.SingleQuoted;
                    Advance(source, ref i, ref line, ref column);
                    continue;
                }
                if (c == '"')
                {
                    state = LexState.DoubleQuoted;
                    Advance(source, ref i, ref line, ref column);
                    continue;
                }
                if (IsIdentifierStart(c))
                {
                    int offset = i;
                    int tokenLine = line;
                    int tokenColumn = column;
                    Advance(source, ref i, ref line, ref column);
                    while (i < source.Length && IsIdentifierPart(source[i]))
                        Advance(source, ref i, ref line, ref column);
                    onIdentifier(new Token
                    {
                        Text = source.Substring(offset, i - offset),
                        Offset = offset,
                        Line = tokenLine,
                        Column = tokenColumn
                    });
                    continue;
                }

                Advance(source, ref i, ref line, ref column);
            }
        }

        private static bool IsIdentifierStart(char c)
            => c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c)
            => c == '_' || char.IsLetterOrDigit(c);

        private static void Advance(string source, ref int index, ref int line, ref int column)
        {
            if (index >= source.Length) return;
            char c = source[index++];
            if (c == '\n')
            {
                line++;
                column = 1;
            }
            else if (c == '\r')
            {
                // Treat CRLF as one logical line while still consuming both
                // characters in the surrounding scanner.
                if (index < source.Length && source[index] == '\n') index++;
                line++;
                column = 1;
            }
            else column++;
        }
    }
}
