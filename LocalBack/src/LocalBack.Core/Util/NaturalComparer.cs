using System.Globalization;

namespace LocalBack.Core.Util;

/// <summary>
/// Orders names the way Explorer does: case-insensitive, with runs of digits compared as numbers,
/// so "file2" comes before "file10".
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
                if (c != 0) return c;
                // Same number: fewer leading zeros first, like "1" before "01".
                c = (i - si).CompareTo(j - sj);
                if (c != 0) return c;
                continue;
            }
            int ci = i, cj = j;
            while (i < x.Length && !char.IsDigit(x[i])) i++;
            while (j < y.Length && !char.IsDigit(y[j])) j++;
            int r = string.Compare(x, ci, y, cj, Math.Max(i - ci, j - cj), Format.Culture, CompareOptions.IgnoreCase);
            if (r != 0) return r;
            // The chunks compared equal up to the shorter one's length; the shorter chunk sorts first.
            r = (i - ci).CompareTo(j - cj);
            if (r != 0) return r;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
