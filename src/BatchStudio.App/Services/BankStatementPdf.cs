using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using BatchStudio.Core.Banking;

namespace BatchStudio.App.Services;

/// <summary>One line of the PDF statement, already worded for the user.</summary>
public sealed record StatementPdfLine(string Date, string Type, string TypeCss, string Description, string In, string Out, string Balance, bool Negative);

/// <summary>Everything the PDF statement shows, worded in the user's language.</summary>
public sealed record StatementPdfData(
    bool RightToLeft, string Entity, string Title, string Printed, string Bank, string Number, string Currency,
    string PeriodLabel, string Period, string TypeLabel, string Type, string Movements,
    string OpeningLabel, string Opening, string InLabel, string In, string OutLabel, string Out, string ClosingLabel, string Closing, bool ClosingNegative,
    string DateHead, string TypeHead, string DescriptionHead, string InHead, string OutHead, string BalanceHead,
    string ForwardDate, string ForwardText, string ForwardBalance, IReadOnlyList<StatementPdfLine> Lines, string Accent, string Footer);

/// <summary>
/// The bank statement PDF: the same layout as the Bank Accounts page (account header, the filters, the four figures,
/// the movements oldest first with the running balance) on white paper. Written as HTML and printed to PDF by
/// Microsoft Edge without a window (Arabic and every script shaped correctly); null when Edge is not on the PC.
/// </summary>
public static class BankStatementPdf
{
    public static string? EdgePath()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            var p = Path.Combine(r, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Prints the statement to <paramref name="pdfPath"/>; false when Edge is missing or failed (use the fallback).</summary>
    public static bool TryExport(StatementPdfData d, string pdfPath)
    {
        var edge = EdgePath();
        if (edge == null) return false;
        var work = Path.Combine(Path.GetTempPath(), "DEEPMATH-stmt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var html = Path.Combine(work, "statement.html");
            File.WriteAllText(html, Html(d), new UTF8Encoding(false));
            var tmpPdf = Path.Combine(work, "statement.pdf");
            var psi = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // its own profile folder: never hands the job to an Edge the user has open
            foreach (var a in new[]
                     {
                         "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                         "--user-data-dir=" + Path.Combine(work, "profile"), "--no-pdf-header-footer", "--print-to-pdf-no-header",
                         "--print-to-pdf=" + tmpPdf, new Uri(html).AbsoluteUri,
                     })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(90_000)) { try { p.Kill(true); } catch { } return false; }
            for (int i = 0; i < 20 && !File.Exists(tmpPdf); i++) Thread.Sleep(150);
            if (!File.Exists(tmpPdf) || new FileInfo(tmpPdf).Length < 800) return false;
            File.Copy(tmpPdf, pdfPath, overwrite: false);
            return true;
        }
        catch { return false; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

    public static string Html(StatementPdfData d)
    {
        var sb = new StringBuilder();
        string dir = d.RightToLeft ? "rtl" : "ltr";
        string start = d.RightToLeft ? "right" : "left", end = d.RightToLeft ? "left" : "right";
        sb.Append($$"""
<!doctype html>
<html dir="{{dir}}" lang="{{(d.RightToLeft ? "ar" : "en")}}">
<head>
<meta charset="utf-8">
<title>{{E(d.Title)}}</title>
<style>
  @page { size: A4; margin: 14mm 12mm 16mm 12mm; }
  * { box-sizing: border-box; }
  html, body { margin: 0; background: #fff; }
  body { font-family: "Segoe UI", "Segoe UI Arabic", Tahoma, Arial, sans-serif; color: #1f2937; font-size: 10.5pt;
         -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  .num { direction: ltr; unicode-bidi: isolate; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .mono { font-family: "Cascadia Mono", Consolas, monospace; direction: ltr; unicode-bidi: isolate; }
  .top { display: flex; justify-content: space-between; align-items: center; padding-bottom: 10px; border-bottom: 3px solid {{d.Accent}}; }
  .brand { font-size: 9pt; color: #6b7280; letter-spacing: .06em; text-transform: uppercase; font-weight: 600; }
  .brand b { color: {{d.Accent}}; font-size: 11pt; letter-spacing: .02em; }
  .doc { text-align: {{end}}; }
  .doc .t { font-size: 15pt; font-weight: 700; color: #111827; }
  .doc .p { font-size: 8.5pt; color: #6b7280; }
  .card { margin-top: 16px; border: 1px solid #e5e7eb; border-radius: 14px; padding: 18px 20px 16px; }
  .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
  .bank { font-size: 19pt; font-weight: 700; line-height: 1.2; color: #111827; }
  .acct { margin-top: 3px; color: #4b5563; font-size: 10pt; }
  .cur { background: {{d.Accent}}1f; color: {{d.Accent}}; border-radius: 8px; padding: 4px 12px; font-weight: 700; font-size: 11pt; }
  .filters { display: flex; gap: 8px; margin-top: 14px; align-items: center; flex-wrap: wrap; }
  .chip { border: 1px solid #e5e7eb; background: #f9fafb; border-radius: 8px; padding: 5px 11px; font-size: 9pt; }
  .chip span { color: #6b7280; }
  .chip b { font-weight: 600; }
  .count { margin-{{start}}: auto; color: #6b7280; font-size: 9pt; }
  .figs { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-top: 14px; }
  .fig { border-radius: 11px; padding: 10px 13px; }
  .fig .l { font-size: 8pt; font-weight: 600; color: #6b7280; margin-bottom: 4px; }
  .fig .v { font-size: 12.5pt; font-weight: 700; }
  .f0 { background: #f3f4f6; } .f1 { background: #e8f6ee; } .f1 .v { color: #15803d; }
  .f2 { background: #fdecec; } .f2 .v { color: #c62828; } .f3 { background: {{d.Accent}}1a; } .f3 .v { color: {{d.Accent}}; }
  .f3 .v.neg { color: #c62828; }
  table { width: 100%; border-collapse: collapse; margin-top: 16px; }
  thead { display: table-header-group; }
  th { background: #f3f4f6; color: #374151; font-size: 8.5pt; font-weight: 700; text-align: {{start}}; padding: 8px 9px; border-bottom: 1px solid #e5e7eb; }
  th.r, td.r { text-align: {{end}}; }
  td { padding: 7px 9px; border-bottom: 1px solid #eef0f3; vertical-align: top; }
  tr { page-break-inside: avoid; }
  td.d { white-space: nowrap; color: #374151; }
  td.in { color: #15803d; font-weight: 600; } td.out { color: #c62828; font-weight: 600; }
  td.bal { font-weight: 700; } td.bal.neg { color: #c62828; }
  tr.fwd td { background: #f9fafb; font-style: italic; color: #4b5563; }
  tr.close td { border-top: 2px solid #111827; border-bottom: none; font-weight: 700; background: #fff; }
  .tag { display: inline-block; border-radius: 6px; padding: 1px 8px; font-size: 8pt; font-weight: 600; white-space: nowrap; }
  .tag.dep { background: #e8f6ee; color: #15803d; } .tag.wd { background: #fdecec; color: #c62828; } .tag.tr { background: #eef2ff; color: #4338ca; }
  .foot { margin-top: 18px; display: flex; justify-content: space-between; color: #9ca3af; font-size: 8pt; }
</style>
</head>
<body>
  <div class="top">
    <div class="brand"><b>{{E(d.Entity)}}</b></div>
    <div class="doc"><div class="t">{{E(d.Title)}}</div><div class="p">{{E(d.Printed)}}</div></div>
  </div>
  <div class="card">
    <div class="head">
      <div>
        <div class="bank">{{E(d.Bank)}}</div>
        <div class="acct mono">{{E(d.Number)}}</div>
      </div>
      <div class="cur">{{E(d.Currency)}}</div>
    </div>
    <div class="filters">
      <div class="chip"><span>{{E(d.PeriodLabel)}}:</span> <b>{{E(d.Period)}}</b></div>
      <div class="chip"><span>{{E(d.TypeLabel)}}:</span> <b>{{E(d.Type)}}</b></div>
      <div class="count">{{E(d.Movements)}}</div>
    </div>
    <div class="figs">
      <div class="fig f0"><div class="l">{{E(d.OpeningLabel)}}</div><div class="v num">{{E(d.Opening)}}</div></div>
      <div class="fig f1"><div class="l">&#8593; {{E(d.InLabel)}}</div><div class="v num">{{E(d.In)}}</div></div>
      <div class="fig f2"><div class="l">&#8595; {{E(d.OutLabel)}}</div><div class="v num">{{E(d.Out)}}</div></div>
      <div class="fig f3"><div class="l">{{E(d.ClosingLabel)}}</div><div class="v num{{(d.ClosingNegative ? " neg" : "")}}">{{E(d.Closing)}}</div></div>
    </div>
    <table>
      <thead><tr>
        <th style="width:13%">{{E(d.DateHead)}}</th><th style="width:12%">{{E(d.TypeHead)}}</th><th>{{E(d.DescriptionHead)}}</th>
        <th class="r" style="width:14%">{{E(d.InHead)}}</th><th class="r" style="width:14%">{{E(d.OutHead)}}</th><th class="r" style="width:16%">{{E(d.BalanceHead)}}</th>
      </tr></thead>
      <tbody>
        <tr class="fwd"><td class="d num">{{E(d.ForwardDate)}}</td><td></td><td>{{E(d.ForwardText)}}</td><td></td><td></td><td class="r bal num">{{E(d.ForwardBalance)}}</td></tr>

""");
        foreach (var l in d.Lines)
            sb.Append($"""        <tr><td class="d num">{E(l.Date)}</td><td><span class="tag {l.TypeCss}">{E(l.Type)}</span></td><td>{E(l.Description)}</td><td class="r in num">{E(l.In)}</td><td class="r out num">{E(l.Out)}</td><td class="r bal num{(l.Negative ? " neg" : "")}">{E(l.Balance)}</td></tr>""").Append('\n');
        sb.Append($$"""
        <tr class="close"><td></td><td></td><td>{{E(d.ClosingLabel)}}</td><td class="r in num">{{E(d.In)}}</td><td class="r out num">{{E(d.Out)}}</td><td class="r bal num{{(d.ClosingNegative ? " neg" : "")}}">{{E(d.Closing)}}</td></tr>
      </tbody>
    </table>
  </div>
  <div class="foot"><span>{{E(d.Footer)}}</span><span class="num">{{E(d.Bank)}} · {{E(d.Number)}}</span></div>
</body>
</html>
""");
        return sb.ToString();
    }
}
