using System.Globalization;

namespace LocalDbSharp;

/// <summary>
/// String ordering of SQL Server's default collation, SQL_Latin1_General_CP1_CI_AS.
///
/// That collation sorts varchar and nvarchar data differently:
///  * varchar uses the legacy "SQL sort order 52": punctuation (in code order) sorts before digits, digits
///    before letters; letters compare case-insensitively, with accents as a second-level difference.
///  * nvarchar uses Unicode word sorting: hyphen and apostrophe are ignored on the first pass, so that
///    "co-op" sorts next to "coop".
/// Both ignore trailing spaces. Equality (=, GROUP BY, joins) is the same for both except for a few
/// expansions such as 'ß' = 'ss', which only holds for nvarchar; the engine uses the varchar rule there.
/// </summary>
internal static class SqlCollation
{
    // primary weight = class * 1000 + position; classes: 0 punctuation, 1 digits, 2 letters
    static readonly int[] Primary = new int[256];
    static readonly byte[] Secondary = new byte[256];
    static readonly int[] Expansion = new int[256]; // primary weight of a second collation element, or -1

    static SqlCollation()
    {
        Array.Fill(Expansion, -1);
        for (int c = 0; c < 256; c++) Primary[c] = c; // punctuation and control characters: code order
        for (int c = '0'; c <= '9'; c++) Primary[c] = 1000 + (c - '0');

        // each group is a base letter followed by its accented forms, in the order SQL Server sorts them
        string[] groups =
        [
            "AaÀàÁáÂâÃãÄäÅå", "Bb", "CcÇç", "Dd", "EeÈèÉéÊêËë", "Ff", "Gg", "Hh", "IiÌìÍíÎîÏï", "Jj", "Kk", "Ll", "Mm", "NnÑñ",
            "OoÒòÓóÔôÕõÖöØø", "Pp", "Qq", "Rr", "Ss", "Tt", "UuÙùÚúÛûÜü", "Vv", "Ww", "Xx", "YyÝýÿ", "Zz", "Ðð", "Þþ",
        ];
        for (int g = 0; g < groups.Length; g++)
        {
            string group = groups[g];
            for (int k = 0; k < group.Length; k++)
            {
                Primary[group[k]] = 2000 + g;
                // upper and lower case of the same letter share a secondary weight ("ÿ" has no upper-case partner here)
                Secondary[group[k]] = (byte)(group[k] == 'ÿ' ? 2 : k / 2);
            }
        }
        // ligatures expand to two letters and differ from the plain spelling on the second level only
        foreach (char ae in "Ææ") { Primary[ae] = 2000; Expansion[ae] = 2004; Secondary[ae] = 9; }
        Primary['ß'] = 2018;
        Expansion['ß'] = 2018;
        Secondary['ß'] = 9;
        BuildCanonical();
    }

    static bool Next(ReadOnlySpan<char> s, ref int i, ref int pending, out int primary, out int secondary)
    {
        if (pending >= 0)
        {
            primary = pending;
            secondary = 0;
            pending = -1;
            return true;
        }
        if (i >= s.Length)
        {
            primary = secondary = 0;
            return false;
        }
        char c = s[i++];
        if (c < 256)
        {
            primary = Primary[c];
            secondary = Secondary[c];
            pending = Expansion[c];
        }
        else
        {
            // outside Latin-1: letters after all Latin letters, everything else after the Latin-1 punctuation
            primary = char.IsLetter(c) ? 100000 + char.ToUpperInvariant(c) : 10000 + c;
            secondary = 0;
        }
        return true;
    }

    /// <summary>varchar ordering (SQL sort order 52).</summary>
    public static int CompareLatin1(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = a.TrimEnd(' ');
        b = b.TrimEnd(' ');
        int i = 0, j = 0, pendingA = -1, pendingB = -1, second = 0;
        while (true)
        {
            bool hasA = Next(a, ref i, ref pendingA, out int pa, out int sa);
            bool hasB = Next(b, ref j, ref pendingB, out int pb, out int sb);
            if (!hasA || !hasB)
            {
                if (hasA) return 1;
                if (hasB) return -1;
                return second;
            }
            if (pa != pb) return pa < pb ? -1 : 1;
            if (second == 0 && sa != sb) second = sa < sb ? -1 : 1;
        }
    }

    /// <summary>Single-character equality: case-insensitive, accent-sensitive.</summary>
    public static bool CharEquals(char a, char b)
    {
        if (a == b) return true;
        if (a < 256 && b < 256) return Primary[a] == Primary[b] && Secondary[a] == Secondary[b] && Expansion[a] == Expansion[b] && Primary[a] >= 2000;
        return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }

    static (int Primary, int Expansion, int Secondary) Weights(char c)
        => c < 256 ? (Primary[c], Expansion[c], Secondary[c]) : (char.IsLetter(c) ? 100000 + char.ToUpperInvariant(c) : 10000 + c, -1, 0);

    /// <summary>Single-character ordering, used for LIKE ranges such as [a-f].</summary>
    public static int CompareChars(char a, char b)
    {
        var (pa, ea, sa) = Weights(a);
        var (pb, eb, sb) = Weights(b);
        if (pa != pb) return pa.CompareTo(pb);
        if (ea != eb) return ea.CompareTo(eb);
        return sa.CompareTo(sb);
    }

    static readonly char[] Canonical = new char[256]; // filled by the static constructor, after the weights

    static void BuildCanonical()
    {
        // one representative per class of characters that compare equal (case variants of the same letter)
        var canon = Canonical;
        for (int c = 0; c < 256; c++)
        {
            canon[c] = (char)c;
            if (Primary[c] < 2000) continue;
            for (int o = 0; o < c; o++)
                if (Primary[o] == Primary[c] && Secondary[o] == Secondary[c] && Expansion[o] == Expansion[c]) { canon[c] = (char)o; break; }
        }
    }

    /// <summary>
    /// Hash key of a string: two strings get the same key exactly when <see cref="CompareLatin1"/> calls them equal.
    /// </summary>
    public static string KeyOf(string s)
    {
        var span = s.AsSpan().TrimEnd(' ');
        return string.Create(span.Length, span.ToString(), static (dest, src) =>
        {
            for (int i = 0; i < dest.Length; i++)
            {
                char c = src[i];
                if (c < 256) dest[i] = Canonical[c];
                else
                {
                    // outside Latin-1 only letters fold case, and never onto a Latin-1 character
                    char upper = char.IsLetter(c) ? char.ToUpperInvariant(c) : c;
                    dest[i] = upper < 256 ? c : upper;
                }
            }
        });
    }

    static readonly CompareInfo Invariant = CultureInfo.InvariantCulture.CompareInfo;

    static string StripIgnorable(ReadOnlySpan<char> s, out int removed)
    {
        removed = 0;
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
        {
            if (c is '-' or '\'') removed++;
            else buffer[n++] = c;
        }
        return new string(buffer[..n]);
    }

    /// <summary>
    /// nvarchar ordering (Unicode word sort): hyphen and apostrophe only break ties, everything else
    /// follows the same weights as varchar data.
    /// </summary>
    public static int CompareUnicode(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = a.TrimEnd(' ');
        b = b.TrimEnd(' ');
        if (a.IndexOfAny('-', '\'') < 0 && b.IndexOfAny('-', '\'') < 0) return CompareLatin1(a, b);
        string sa = StripIgnorable(a, out int ra), sb = StripIgnorable(b, out int rb);
        int c = CompareLatin1(sa, sb);
        if (c != 0) return c;
        if (ra != rb) return ra < rb ? -1 : 1;
        return a.SequenceCompareTo(b);
    }
}
