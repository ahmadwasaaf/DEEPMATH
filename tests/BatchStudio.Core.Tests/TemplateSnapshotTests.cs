using BatchStudio.Core.Templates;

namespace BatchStudio.Core.Tests;

/// <summary>The picture shown while hovering a template is drawn from this reading of the template file.</summary>
public class TemplateSnapshotTests
{
    private static string Book => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Templates.xlsx");

    [Theory]
    [InlineData("INV7", 0xFF2E7D32u)]
    [InlineData("INV9", 0xFF6D28D9u)]
    [InlineData("INV15", 0xFF1E3A8Au)]
    public void A_template_is_read_with_its_theme_colour(string sheet, uint theme)
    {
        var s = TemplateSnapshot.Load(Book, sheet, sheet);
        Assert.NotNull(s);
        Assert.Equal(theme, s!.ThemeBase);
        Assert.False(s.KeepRow1);
        Assert.InRange(s.Width, 500, 1500);
        Assert.InRange(s.Height, 500, 2500);
        Assert.Contains(s.Cells, c => c.Text == "INVOICE");
        Assert.Contains(s.Cells, c => c.Text.Contains("BUYER / CONSIGNEE"));
        // theme-coloured cells exist (they take the supplier colour in the picture)
        Assert.Contains(s.Cells, c => c.Fill == theme);
        // merged blocks are one cell
        Assert.Contains(s.Cells, c => c.ColSpan > 1);
    }

    [Fact]
    public void A_missing_file_gives_no_picture() =>
        Assert.Null(TemplateSnapshot.Load(Path.Combine(Path.GetTempPath(), "no-such-template.xlsx"), null, "INV2"));
}
