using System.Text;

namespace LocalDbSharp.Parsing;

internal enum Tk { Ident, Quoted, Var, Str, NStr, Int, Dec, Float, Money, Bin, Sym, Eof }

internal readonly record struct Token(Tk Kind, string Text, int Line);

internal static class Lexer
{
    static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '#';
    static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    public static List<Token> Lex(string s)
    {
        var r = new List<Token>();
        int i = 0, line = 1, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
            char next = i + 1 < n ? s[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < n && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (s[i] == '\n') line++;
                    if (s[i] == '/' && i + 1 < n && s[i + 1] == '*') { depth++; i += 2; }
                    else if (s[i] == '*' && i + 1 < n && s[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            if (c == '\'' || ((c == 'N' || c == 'n') && next == '\''))
            {
                bool unicode = c != '\'';
                int startLine = line;
                i += unicode ? 2 : 1;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= n) throw new TSqlException(105, "Unclosed quotation mark after the character string '" + Head(sb) + "'.", 15) { LineNumber = startLine };
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (s[i] == '\n') line++;
                    sb.Append(s[i++]);
                }
                r.Add(new Token(unicode ? Tk.NStr : Tk.Str, sb.ToString(), startLine));
                continue;
            }
            if (c == '[' || c == '"')
            {
                char close = c == '[' ? ']' : '"';
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= n) throw new TSqlException(105, "Unclosed quotation mark after the character string '" + Head(sb) + "'.", 15) { LineNumber = line };
                    if (s[i] == close)
                    {
                        if (i + 1 < n && s[i + 1] == close) { sb.Append(close); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                r.Add(new Token(Tk.Quoted, sb.ToString(), line));
                continue;
            }
            if (c == '@')
            {
                int j = i + 1;
                while (j < n && IsIdentPart(s[j])) j++;
                r.Add(new Token(Tk.Var, s[i..j], line));
                i = j;
                continue;
            }
            if (c == '$' && (char.IsAsciiDigit(next) || next == '.'))
            {
                int j = i + 1;
                while (j < n && (char.IsAsciiDigit(s[j]) || s[j] == '.')) j++;
                r.Add(new Token(Tk.Money, s[(i + 1)..j], line));
                i = j;
                continue;
            }
            if (IsIdentStart(c) || (c == '$' && char.IsLetter(next)))
            {
                int j = i + 1;
                while (j < n && IsIdentPart(s[j])) j++;
                r.Add(new Token(Tk.Ident, s[i..j], line));
                i = j;
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
            {
                if (c == '0' && (next == 'x' || next == 'X'))
                {
                    int j = i + 2;
                    while (j < n && char.IsAsciiHexDigit(s[j])) j++;
                    r.Add(new Token(Tk.Bin, s[(i + 2)..j], line));
                    i = j;
                    continue;
                }
                int k = i;
                var kind = Tk.Int;
                while (k < n && char.IsAsciiDigit(s[k])) k++;
                if (k < n && s[k] == '.')
                {
                    kind = Tk.Dec;
                    k++;
                    while (k < n && char.IsAsciiDigit(s[k])) k++;
                }
                if (k < n && (s[k] == 'e' || s[k] == 'E'))
                {
                    int m = k + 1;
                    if (m < n && (s[m] == '+' || s[m] == '-')) m++;
                    if (m < n && char.IsAsciiDigit(s[m]))
                    {
                        while (m < n && char.IsAsciiDigit(s[m])) m++;
                        kind = Tk.Float;
                        k = m;
                    }
                }
                r.Add(new Token(kind, s[i..k], line));
                i = k;
                continue;
            }
            string two = i + 1 < n ? s.Substring(i, 2) : "";
            if (two is "<>" or "<=" or ">=" or "!=" or "!<" or "!>" or "+=" or "-=" or "*=" or "/=" or "%=" or "::" or "&=" or "|=" or "^=")
            {
                r.Add(new Token(Tk.Sym, two, line));
                i += 2;
                continue;
            }
            if ("()+-*/%=<>,.;&|^~:!{}".Contains(c))
            {
                r.Add(new Token(Tk.Sym, c.ToString(), line));
                i++;
                continue;
            }
            throw new TSqlException(102, $"Incorrect syntax near '{c}'.", 15) { LineNumber = line };
        }
        r.Add(new Token(Tk.Eof, "", line));
        return r;
    }

    static string Head(StringBuilder sb) => sb.Length > 30 ? sb.ToString(0, 30) : sb.ToString();
}
