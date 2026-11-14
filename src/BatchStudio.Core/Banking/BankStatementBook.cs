using ClosedXML.Excel;

namespace BatchStudio.Core.Banking;

/// <summary>Text of the statement in the user's language (the app fills it from its strings).</summary>
public sealed record StatementWords(
    string Title, string Entity, string Bank, string Account, string Currency, string Period, string Printed,
    string Date, string Description, string Deposit, string Withdrawal, string Balance,
    string Opening, string TotalIn, string TotalOut, string Closing, string BroughtForward, string Movements);

/// <summary>
/// A bank statement as a tidy one-sheet workbook (title band, account box, four totals, the movements with a running
/// balance and a closing row), set up to print on A4 one page wide; the app turns it into a PDF with Excel.
/// </summary>
public static class BankStatementBook
{
    private static readonly XLColor Ink = XLColor.FromHtml("#1F2937");
    private static readonly XLColor Muted = XLColor.FromHtml("#6B7280");
    private static readonly XLColor Line = XLColor.FromHtml("#E5E7EB");
    private static readonly XLColor Zebra = XLColor.FromHtml("#F8FAFC");
    private static readonly XLColor In = XLColor.FromHtml("#15803D");
    private static readonly XLColor Out = XLColor.FromHtml("#B91C1C");

    public static void Build(string path, BankStatement s, string entity, string periodText, StatementWords w, string accentHex = "#0F766E")
    {
        var accent = XLColor.FromHtml(accentHex);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Statement");
        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 10;
        ws.Style.Font.FontColor = Ink;
        ws.ShowGridLines = false;
        ws.Column(1).Width = 13;   // date
        ws.Column(2).Width = 44;   // description
        ws.Column(3).Width = 15;   // deposit
        ws.Column(4).Width = 15;   // withdrawal
        ws.Column(5).Width = 17;   // balance
        const string num = "#,##0.00;-#,##0.00;\"-\"";

        // title band
        var band = ws.Range(1, 1, 2, 5);
        band.Style.Fill.BackgroundColor = accent;
        ws.Cell(1, 1).Value = w.Title.ToUpperInvariant();
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(18).Font.SetFontColor(XLColor.White);
        ws.Cell(2, 1).Value = entity;
        ws.Cell(2, 1).Style.Font.SetFontColor(XLColor.White).Font.SetFontSize(10.5);
        ws.Cell(1, 5).Value = s.Account.Currency;
        ws.Cell(1, 5).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(XLColor.White);
        ws.Cell(1, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(2, 5).Value = $"{w.Printed} {DateTime.Now:dd MMM yyyy}";
        ws.Cell(2, 5).Style.Font.SetFontColor(XLColor.White).Font.SetFontSize(8.5);
        ws.Cell(2, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Row(1).Height = 30;
        ws.Row(2).Height = 18;

        // account box
        int r = 4;
        void Info(int row, int col, string label, string value)
        {
            ws.Cell(row, col).Value = label.ToUpperInvariant();
            ws.Cell(row, col).Style.Font.SetFontSize(8).Font.SetFontColor(Muted).Font.SetBold();
            ws.Cell(row + 1, col).Value = value;
            ws.Cell(row + 1, col).Style.Font.SetFontSize(11).Font.SetBold();
        }
        Info(r, 1, w.Bank, s.Account.Bank);
        ws.Range(r, 1, r, 2).Merge(); ws.Range(r + 1, 1, r + 1, 2).Merge();
        Info(r, 3, w.Account, s.Account.Number);
        ws.Range(r, 3, r, 4).Merge(); ws.Range(r + 1, 3, r + 1, 4).Merge();
        ws.Cell(r + 1, 3).Style.NumberFormat.Format = "@";
        Info(r, 5, w.Period, periodText);
        ws.Cell(r + 1, 5).Style.Font.SetFontSize(9.5);

        // four totals
        r = 7;
        var totals = new (string Label, decimal Value, XLColor Color)[]
        {
            (w.Opening, s.Opening, Ink), (w.TotalIn, s.TotalIn, In), (w.TotalOut, s.TotalOut, Out), (w.Closing, s.Closing, accent),
        };
        int[] cols = [1, 2, 3, 5];
        for (int i = 0; i < 4; i++)
        {
            int c = cols[i];
            var box = i == 2 ? ws.Range(r, 3, r + 1, 4) : ws.Range(r, c, r + 1, c);
            box.Style.Fill.BackgroundColor = Zebra;
            box.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            box.Style.Border.OutsideBorderColor = Line;
            ws.Cell(r, c).Value = totals[i].Label.ToUpperInvariant();
            ws.Cell(r, c).Style.Font.SetFontSize(8).Font.SetFontColor(Muted).Font.SetBold();
            ws.Cell(r + 1, c).Value = totals[i].Value;
            ws.Cell(r + 1, c).Style.NumberFormat.Format = num;
            ws.Cell(r + 1, c).Style.Font.SetFontSize(12).Font.SetBold().Font.SetFontColor(totals[i].Color);
            ws.Cell(r + 1, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            if (i == 2) { ws.Range(r, 3, r, 4).Merge(); ws.Range(r + 1, 3, r + 1, 4).Merge(); }
        }
        ws.Row(r + 1).Height = 22;

        // movements
        r = 10;
        ws.Cell(r - 1, 1).Value = $"{w.Movements} ({s.Count})";
        ws.Cell(r - 1, 1).Style.Font.SetFontSize(8).Font.SetFontColor(Muted).Font.SetBold();
        string[] heads = [w.Date, w.Description, w.Deposit, w.Withdrawal, w.Balance];
        for (int c = 0; c < 5; c++)
        {
            var cell = ws.Cell(r, c + 1);
            cell.Value = heads[c].ToUpperInvariant();
            cell.Style.Font.SetBold().Font.SetFontSize(8.5).Font.SetFontColor(XLColor.White);
            cell.Style.Fill.BackgroundColor = Ink;
            cell.Style.Alignment.Horizontal = c >= 2 ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
            cell.Style.Alignment.Indent = 1;
        }
        ws.Row(r).Height = 20;
        int first = r;
        r++;
        ws.Cell(r, 1).Value = s.From?.ToString("dd/MM/yyyy") ?? s.Account.OpeningDate.ToString("dd/MM/yyyy");
        ws.Cell(r, 2).Value = w.BroughtForward;
        ws.Cell(r, 2).Style.Font.SetItalic().Font.SetFontColor(Muted);
        ws.Cell(r, 5).Value = s.Opening;
        r++;
        bool zebra = true;
        foreach (var line in s.Lines)
        {
            ws.Cell(r, 1).Value = line.Tx.Date;
            ws.Cell(r, 1).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(r, 2).Value = line.Tx.Description;
            ws.Cell(r, 2).Style.Alignment.WrapText = true;
            if (line.Tx.Kind == BankTxKind.Deposit) { ws.Cell(r, 3).Value = line.Tx.Amount; ws.Cell(r, 3).Style.Font.FontColor = In; }
            else { ws.Cell(r, 4).Value = line.Tx.Amount; ws.Cell(r, 4).Style.Font.FontColor = Out; }
            ws.Cell(r, 5).Value = line.Balance;
            if (zebra) ws.Range(r, 1, r, 5).Style.Fill.BackgroundColor = Zebra;
            zebra = !zebra;
            r++;
        }
        // closing row
        ws.Cell(r, 2).Value = w.Closing;
        ws.Cell(r, 3).Value = s.TotalIn;
        ws.Cell(r, 4).Value = s.TotalOut;
        ws.Cell(r, 5).Value = s.Closing;
        var close = ws.Range(r, 1, r, 5);
        close.Style.Font.SetBold();
        close.Style.Border.TopBorder = XLBorderStyleValues.Medium;
        close.Style.Border.TopBorderColor = Ink;
        ws.Cell(r, 5).Style.Font.FontColor = accent;

        var body = ws.Range(first + 1, 1, r, 5);
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        body.Style.Alignment.Indent = 1;
        body.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        body.Style.Border.BottomBorderColor = Line;
        ws.Range(first + 1, 3, r, 5).Style.NumberFormat.Format = num;
        ws.Range(first + 1, 3, r, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        var ps = ws.PageSetup;
        ps.PaperSize = XLPaperSize.A4Paper;
        ps.PageOrientation = XLPageOrientation.Portrait;
        ps.FitToPages(1, 0);
        ps.Margins.Left = ps.Margins.Right = 0.4;
        ps.Margins.Top = ps.Margins.Bottom = 0.5;
        ps.SetRowsToRepeatAtTop(first, first);
        ps.PrintAreas.Add(1, 1, r, 5);
        ps.Footer.Center.AddText("&P / &N", XLHFOccurrence.AllPages);
        wb.SaveAs(path);
    }
}
