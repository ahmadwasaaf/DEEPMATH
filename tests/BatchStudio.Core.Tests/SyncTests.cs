using BatchStudio.Core.Data;

namespace BatchStudio.Core.Tests;

public class SyncTests
{
    private static TableData T(params string[][] rows)
    {
        var t = new TableData { KeyColumn = 0 };
        t.Headers.AddRange(["Supplier ID", "Supplier Name (English)"]);
        t.Kinds.AddRange([ColumnKind.Text, ColumnKind.Text]);
        foreach (var r in rows) t.Rows.Add(r);
        return t;
    }

    [Fact]
    public void Changes_on_different_rows_merge_automatically()
    {
        var snap = T(["SUP-001", "A"], ["SUP-002", "B"]);
        var local = T(["SUP-001", "A edited in app"], ["SUP-002", "B"]);
        var excel = T(["SUP-001", "A"], ["SUP-002", "B edited in Excel"], ["SUP-003", "C added in Excel"]);
        var m = SyncMerger.Merge(snap, local, excel);
        Assert.Empty(m.Conflicts);
        var t = m.Build();
        Assert.Equal(new[] { "A edited in app", "B edited in Excel", "C added in Excel" }, t.Rows.Select(r => r[1]).ToArray());
    }

    [Fact]
    public void Same_row_changed_on_both_sides_is_a_conflict()
    {
        var snap = T(["SUP-001", "A"]);
        var local = T(["SUP-001", "mine"]);
        var excel = T(["SUP-001", "excel"]);
        var m = SyncMerger.Merge(snap, local, excel);
        var c = Assert.Single(m.Conflicts);
        Assert.Equal("mine", m.Build().Rows[0][1]);
        c.Choice = ConflictChoice.TakeExcel;
        Assert.Equal("excel", m.Build().Rows[0][1]);
    }

    [Fact]
    public void Deletes_are_merged()
    {
        var snap = T(["SUP-001", "A"], ["SUP-002", "B"]);
        var local = T(["SUP-002", "B"]);                 // deleted SUP-001 in app
        var excel = T(["SUP-001", "A"]);                 // deleted SUP-002 in Excel
        var t = SyncMerger.Merge(snap, local, excel).Build();
        Assert.Empty(t.Rows);
    }

    [Fact]
    public void Column_added_in_excel_is_kept()
    {
        var snap = T(["SUP-001", "A"]);
        var local = T(["SUP-001", "A"], ["SUP-002", "new in app"]);
        var excel = new TableData { KeyColumn = 0 };
        excel.Headers.AddRange(["Supplier ID", "Supplier Name (English)", "Phone"]);
        excel.Kinds.AddRange([ColumnKind.Text, ColumnKind.Text, ColumnKind.Text]);
        excel.Rows.Add(["SUP-001", "A", "123"]);
        var t = SyncMerger.Merge(snap, local, excel).Build();
        Assert.Equal(3, t.Headers.Count);
        Assert.Equal("123", t.Rows[0][2]);
        Assert.Equal("new in app", t.Rows[1][1]);
    }

    [Fact]
    public void NextId_follows_existing_pattern()
    {
        var t = T(["SUP-001", "A"], ["SUP-051", "B"]);
        Assert.Equal("SUP-052", t.NextId("SUP"));
    }

    [Fact]
    public void Save_and_reload_roundtrip_with_external_edit()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            DatabaseLayout.EnsureStructure(root, "Test");
            Assert.Empty(DatabaseLayout.Validate(root));

            var session = new DataSetSession(root, DataSets.Items);
            session.Load();
            var t = session.Current.Clone();
            t.Rows.Add(["ITM-001", "Oil Filter", "55", "AUT", "Active"]);
            t.Kinds[2] = ColumnKind.Number;
            session.SetCurrent(t);
            session.Save();
            Assert.Single(Directory.GetFiles(Path.Combine(DatabaseLayout.Backups(root), "items")));

            // our own save is ignored by the watcher handler
            Assert.Equal(ExternalChangeOutcome.Ignored, session.OnFileChanged());

            // simulate an edit in Excel
            var other = ExcelTableStore.Load(DataSets.Items.FullPath(root), "Items");
            other.Rows.Add(["ITM-002", "Air Filter", "75", "AUT", "Active"]);
            ExcelTableStore.Save(DataSets.Items.FullPath(root), "Items", other);

            Assert.Equal(ExternalChangeOutcome.Reloaded, session.OnFileChanged());
            Assert.Equal(2, session.Current.Rows.Count);
            Assert.Equal(ColumnKind.Number, session.Current.Kinds[2]);
            Assert.Equal("55", session.Current.Rows[0][2]);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
