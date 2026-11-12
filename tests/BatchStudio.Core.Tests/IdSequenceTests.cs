using BatchStudio.Core.Data;
using Xunit;

namespace BatchStudio.Core.Tests;

public class IdSequenceTests
{
    [Fact]
    public void Gap_after_a_delete_is_closed()
    {
        var ids = new[] { "SUP-055", "SUP-056", "SUP-058", "SUP-059", "SUP-060" };
        var all = Enumerable.Range(1, 54).Select(n => $"SUP-{n:000}").Concat(ids).ToArray();
        var r = IdSequence.Renumber(all, "SUP")!;
        Assert.Equal("SUP-056", r[55]);
        Assert.Equal("SUP-057", r[56]);
        Assert.Equal("SUP-059", r[58]);
        Assert.Equal("SUP-001", r[0]);
    }

    [Fact]
    public void Serial_already_right_changes_nothing() =>
        Assert.Null(IdSequence.Renumber(new[] { "CLI-001", "CLI-002", "CLI-003" }, "CLI"));

    [Fact]
    public void Order_follows_the_numbers_not_the_rows_and_empty_ids_come_last()
    {
        var r = IdSequence.Renumber(new[] { "SUP-004", "", "SUP-001" }, "SUP")!;
        Assert.Equal(new[] { "SUP-002", "SUP-003", "SUP-001" }, r);
    }

    [Fact]
    public void Hand_made_ids_are_left_alone() =>
        Assert.Null(IdSequence.Renumber(new[] { "SUP-001", "ACME", "SUP-005" }, "SUP"));

    [Fact]
    public void Width_is_kept() =>
        Assert.Equal(new[] { "CLI-0001", "CLI-0002" }, IdSequence.Renumber(new[] { "CLI-0001", "CLI-0005" }, "CLI"));
}
