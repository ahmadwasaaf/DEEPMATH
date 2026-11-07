namespace BatchStudio.Core.Data;

public enum ConflictChoice { KeepMine, TakeExcel }

/// <summary>The same record was changed in the program and in the Excel file since the last sync.</summary>
public sealed class MergeConflict
{
    public required string Key { get; init; }
    /// <summary>Program version (null = deleted in the program).</summary>
    public string[]? Local { get; init; }
    /// <summary>Excel version (null = deleted in Excel).</summary>
    public string[]? External { get; init; }
    public ConflictChoice Choice { get; set; } = ConflictChoice.KeepMine;
}

public sealed class MergeResult
{
    public required List<string> Headers { get; init; }
    public required List<ColumnKind> Kinds { get; init; }
    public required int KeyColumn { get; init; }
    public required List<MergeConflict> Conflicts { get; init; }
    public required List<(string Key, string[]? Row)> Plan { get; init; }
    public int TakenFromExcel { get; init; }

    /// <summary>Builds the final table using each conflict's <see cref="MergeConflict.Choice"/>.</summary>
    public TableData Build()
    {
        var t = new TableData { KeyColumn = KeyColumn };
        t.Headers.AddRange(Headers);
        t.Kinds.AddRange(Kinds);
        var choice = Conflicts.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, row) in Plan)
        {
            if (choice.TryGetValue(key, out var c))
            {
                var pick = c.Choice == ConflictChoice.KeepMine ? c.Local : c.External;
                if (pick != null) t.Rows.Add(pick);
            }
            else if (row != null) t.Rows.Add(row);
        }
        return t;
    }
}

/// <summary>
/// Three-way merge used by the two-way sync:
/// snapshot = table as last read from / written to Excel,
/// local    = table currently edited in the program,
/// external = table just re-read from Excel after it changed on disk.
/// Records changed on one side only are merged automatically; records changed on both sides become conflicts.
/// </summary>
public static class SyncMerger
{
    public static MergeResult Merge(TableData snapshot, TableData local, TableData external)
    {
        var headers = new List<string>(external.Headers);
        var kinds = new List<ColumnKind>(external.Kinds);
        for (int i = 0; i < local.Headers.Count; i++)
            if (!headers.Contains(local.Headers[i], StringComparer.OrdinalIgnoreCase))
            {
                headers.Add(local.Headers[i]);
                kinds.Add(i < local.Kinds.Count ? local.Kinds[i] : ColumnKind.Text);
            }

        Dictionary<string, string[]> Proj(TableData t) =>
            t.ByKey().ToDictionary(kv => kv.Key, kv => t.Project(kv.Value, headers), StringComparer.OrdinalIgnoreCase);

        var S = Proj(snapshot);
        var L = Proj(local);
        var E = Proj(external);
        static bool Same(string[]? a, string[]? b) =>
            a == null ? b == null : b != null && TableData.RowSignature(a) == TableData.RowSignature(b);

        var plan = new List<(string, string[]?)>();
        var conflicts = new List<MergeConflict>();
        int fromExcel = 0;

        // Excel order first, then rows that exist only in the program.
        var order = new List<string>(E.Keys);
        foreach (var k in L.Keys) if (!E.ContainsKey(k)) order.Add(k);
        foreach (var k in S.Keys) if (!E.ContainsKey(k) && !L.ContainsKey(k)) order.Add(k);

        foreach (var key in order)
        {
            S.TryGetValue(key, out var s);
            L.TryGetValue(key, out var l);
            E.TryGetValue(key, out var e);
            bool localChanged = !Same(l, s);
            bool externalChanged = !Same(e, s);

            if (!externalChanged) plan.Add((key, l));
            else if (!localChanged) { plan.Add((key, e)); fromExcel++; }
            else if (Same(l, e)) plan.Add((key, l));
            else
            {
                conflicts.Add(new MergeConflict { Key = key, Local = l, External = e });
                plan.Add((key, l));
            }
        }

        return new MergeResult
        {
            Headers = headers,
            Kinds = kinds,
            KeyColumn = local.KeyColumn,
            Conflicts = conflicts,
            Plan = plan,
            TakenFromExcel = fromExcel,
        };
    }
}
