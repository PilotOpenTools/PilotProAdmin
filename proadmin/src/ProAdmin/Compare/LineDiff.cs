namespace ProAdmin.Compare;

public enum DiffKind
{
    Same,
    Removed,
    Added,
}

public sealed record DiffLine(DiffKind Kind, int? OldNo, int? NewNo, string Text);

public sealed record DiffHunk(int OldStart, int NewStart, List<DiffLine> Lines);

public sealed class DiffResult
{
    public List<DiffHunk> Hunks { get; } = new();
    public int Added { get; set; }
    public int Removed { get; set; }
    public int OldLines { get; set; }
    public int NewLines { get; set; }

    /// <summary>Различий слишком много для построчного сравнения - показывается только сводка.</summary>
    public bool TooLarge { get; set; }

    public bool Identical => !TooLarge && Added == 0 && Removed == 0;
}

/// <summary>
/// Построчный diff (алгоритм Майерса, O((N+M)·D)) с группировкой в фрагменты с контекстом.
/// Общие начало и конец отбрасываются заранее, поэтому большие файлы с небольшими правками сравниваются быстро.
/// </summary>
public static class LineDiff
{
    public static DiffResult Compute(string? oldText, string? newText, int context = 3, int maxEdits = 4000)
    {
        var a = Split(oldText);
        var b = Split(newText);
        var result = new DiffResult { OldLines = a.Length, NewLines = b.Length };

        // Строки / числа: сравнение int быстрее строк.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s) => ids.TryGetValue(s, out var id) ? id : ids[s] = ids.Count;
        var ia = a.Select(Id).ToArray();
        var ib = b.Select(Id).ToArray();

        var prefix = 0;
        while (prefix < ia.Length && prefix < ib.Length && ia[prefix] == ib[prefix]) prefix++;
        var suffix = 0;
        while (suffix < ia.Length - prefix && suffix < ib.Length - prefix && ia[^(suffix + 1)] == ib[^(suffix + 1)]) suffix++;

        var ops = new List<DiffLine>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++)
            ops.Add(new DiffLine(DiffKind.Same, i + 1, i + 1, a[i]));

        var middle = Myers(ia[prefix..^suffix], ib[prefix..^suffix], maxEdits);
        if (middle is null)
        {
            result.TooLarge = true;
            result.Removed = ia.Length - prefix - suffix;
            result.Added = ib.Length - prefix - suffix;
            return result;
        }

        int x = prefix, y = prefix;
        foreach (var op in middle)
        {
            switch (op)
            {
                case DiffKind.Same: ops.Add(new DiffLine(DiffKind.Same, x + 1, y + 1, a[x])); x++; y++; break;
                case DiffKind.Removed: ops.Add(new DiffLine(DiffKind.Removed, x + 1, null, a[x])); x++; result.Removed++; break;
                default: ops.Add(new DiffLine(DiffKind.Added, null, y + 1, b[y])); y++; result.Added++; break;
            }
        }
        for (var i = 0; i < suffix; i++, x++, y++)
            ops.Add(new DiffLine(DiffKind.Same, x + 1, y + 1, a[x]));

        BuildHunks(ops, context, result);
        return result;
    }

    private static string[] Split(string? text)
    {
        var t = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        return t.Length == 0 ? Array.Empty<string>() : t.Split('\n');
    }

    /// <summary>Кратчайший сценарий правок; null - правок больше <paramref name="maxEdits"/>.</summary>
    private static List<DiffKind>? Myers(int[] a, int[] b, int maxEdits)
    {
        int n = a.Length, m = b.Length, max = n + m;
        if (max == 0)
            return new List<DiffKind>();

        var v = new int[2 * max + 2];
        var offset = max + 1;
        var trace = new List<int[]>();

        for (var d = 0; d <= Math.Min(max, maxEdits); d++)
        {
            var snapshot = new int[2 * d + 1];
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                snapshot[k + d] = x;

                if (x >= n && y >= m)
                {
                    trace.Add(snapshot);
                    return Backtrack(trace, n, m);
                }
            }
            trace.Add(snapshot);
        }
        return null;
    }

    private static List<DiffKind> Backtrack(List<int[]> trace, int n, int m)
    {
        var ops = new List<DiffKind>();
        int x = n, y = m;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var prev = trace[d - 1];
            int Get(int k) => prev[k + d - 1];
            var k = x - y;
            var prevK = k == -d || (k != d && Get(k - 1) < Get(k + 1)) ? k + 1 : k - 1;
            var prevX = Get(prevK);
            var prevY = prevX - prevK;

            while (x > prevX && y > prevY) { ops.Add(DiffKind.Same); x--; y--; }
            if (x == prevX) { ops.Add(DiffKind.Added); y--; }
            else { ops.Add(DiffKind.Removed); x--; }
        }
        while (x > 0 && y > 0) { ops.Add(DiffKind.Same); x--; y--; }
        ops.Reverse();
        return ops;
    }

    private static void BuildHunks(List<DiffLine> ops, int context, DiffResult result)
    {
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == DiffKind.Same) continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(ops.Count - 1, i + context); j++)
                keep[j] = true;
        }

        DiffHunk? hunk = null;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i])
            {
                hunk = null;
                continue;
            }
            if (hunk is null)
            {
                hunk = new DiffHunk(ops[i].OldNo ?? PrevOld(ops, i), ops[i].NewNo ?? PrevNew(ops, i), new List<DiffLine>());
                result.Hunks.Add(hunk);
            }
            hunk.Lines.Add(ops[i]);
        }
    }

    private static int PrevOld(List<DiffLine> ops, int i)
    {
        for (var j = i; j >= 0; j--) if (ops[j].OldNo is { } n) return n;
        return 1;
    }

    private static int PrevNew(List<DiffLine> ops, int i)
    {
        for (var j = i; j >= 0; j--) if (ops[j].NewNo is { } n) return n;
        return 1;
    }
}
