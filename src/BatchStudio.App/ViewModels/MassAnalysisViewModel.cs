using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using BatchStudio.App.Services;
using BatchStudio.Core.Analysis;
using BatchStudio.Core.Invoices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>One analysed document in the review: what was read, which supplier / account it goes to, its amount.</summary>
public sealed partial class AnalysisItemViewModel : ObservableObject
{
    private readonly MassInvoiceViewModel _owner;
    public AnalysisMatch Match { get; }
    public AnalysisResult Result => Match.Result;

    public AnalysisItemViewModel(MassInvoiceViewModel owner, AnalysisMatch match, int no)
    {
        _owner = owner;
        Match = match;
        No = no;
        _amountText = match.Result.Amount is > 0 ? NewInvoiceViewModel.FormatMoney(match.Result.Amount.Value) : "";
        _supplierName = match.Supplier?.Name ?? match.Result.CompanyName;
        _include = IsReady;
    }

    public int No { get; }
    public string FileName => Result.FileName.Length > 0 ? Result.FileName : Path.GetFileName(Result.File);

    [ObservableProperty] private string _amountText;
    /// <summary>The supplier's name (editable for a new supplier: the name it is saved with).</summary>
    [ObservableProperty] private string _supplierName;
    [ObservableProperty] private bool _include;

    public bool IsNewSupplier => Match.Status == AnalysisStatus.NewSupplier && Match.Owner == null;
    public bool CanEditName => IsNewSupplier;

    public decimal? Amount =>
        decimal.TryParse(AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && a > 0 ? a : null;

    /// <summary>Can be put on a row: a supplier (known or new) and an amount.</summary>
    public bool IsReady => Match.Status != AnalysisStatus.Attention && Amount != null && SupplierName.Trim().Length > 0;

    /// <summary>ok (known supplier, known account) | acct (new account) | new (new supplier) | warn (attention).</summary>
    public string State => Match.Status switch
    {
        AnalysisStatus.Matched => "ok",
        AnalysisStatus.NewAccount => "acct",
        AnalysisStatus.NewSupplier => Match.OtherAccount ? "acct" : "new",
        _ => "warn",
    };

    public string StatusText => Match.Status switch
    {
        AnalysisStatus.Matched => Match.BankNumber is int n && n > 1 ? Loc.F("Analysis.StMatchedN", n) : Loc.T("Analysis.StMatched"),
        AnalysisStatus.NewAccount => Loc.T("Analysis.StNewAccount"),
        AnalysisStatus.NewSupplier when Match.Owner != null => Loc.T(Match.OtherAccount ? "Analysis.StSameNewOther" : "Analysis.StSameNew"),
        AnalysisStatus.NewSupplier => Loc.T("Analysis.StNewSupplier"),
        _ => Loc.T("Analysis.StAttention"),
    };

    /// <summary>Account no. / IBAN and SWIFT read in the document.</summary>
    public string AccountText
    {
        get
        {
            var acc = Result.AccountIds.FirstOrDefault() ?? "";
            var swift = Result.Ids.TryGetValue("swift", out var s) ? s.FirstOrDefault() ?? "" : "";
            return acc.Length > 0 && swift.Length > 0 ? $"{acc} · {swift}" : acc + swift;
        }
    }

    /// <summary>Remarks shown under the status (why it needs a look, what the engine guessed).</summary>
    public string Remarks
    {
        get
        {
            var list = new List<string>();
            if (Match.Note.Length > 0 && !(Match.Note == "Analysis.NoteNoAmount" && Amount != null)) list.Add(Loc.T(Match.Note));
            if (Match.Status == AnalysisStatus.Attention && Result.Error is { Length: > 0 } err && Match.Note == "Analysis.NoteNothingRead") list.Add(err);
            if (Match.MatchedBy == "similar" && Match.Supplier != null) list.Add(Loc.F("Analysis.NoteSimilar", Result.CompanyName));
            if (Match.Candidates.Count > 0) list.Add(string.Join(" / ", Match.Candidates.Select(c => c.Name)));
            foreach (var n in Result.Notes)
                if (Application.Current?.TryFindResource("Analysis.Engine." + n) is string text) list.Add(text);
            return string.Join(" · ", list.Distinct());
        }
    }

    public bool HasRemarks => Remarks.Length > 0;

    /// <summary>Everything read, for the tooltip of the supplier.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { Result.CompanyName };
            if (Result.LocalName.Length > 0) parts.Add(Result.LocalName);
            if (Result.Address.Length > 0) parts.Add(Result.Address);
            if (Result.Bank.Length > 0) parts.Add("\n" + Result.Bank);
            return string.Join("\n", parts.Where(p => p.Length > 0));
        }
    }

    partial void OnAmountTextChanged(string value) => Changed();
    partial void OnSupplierNameChanged(string value) => Changed();
    partial void OnIncludeChanged(bool value) => _owner.RecountReview();

    private void Changed()
    {
        if (IsReady && !Include) Include = true;
        if (!IsReady && Include) Include = false;
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(Remarks));
        OnPropertyChanged(nameof(HasRemarks));
        _owner.RecountReview();
    }

    public void FormatAmount()
    {
        if (Amount is decimal a) AmountText = NewInvoiceViewModel.FormatMoney(a);
    }

    [RelayCommand]
    private void OpenFile()
    {
        try
        {
            if (File.Exists(Result.File)) Process.Start(new ProcessStartInfo(Result.File) { UseShellExecute = true });
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }
}

/// <summary>Mass Invoice › Analysis: a folder of supplier documents read by the engine, reviewed, then put on the rows.</summary>
public sealed partial class MassInvoiceViewModel
{
    public ObservableCollection<AnalysisItemViewModel> ReviewItems { get; } = [];
    [ObservableProperty] private bool _isAnalysing;
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private string _analysisText = "";
    [ObservableProperty] private double _analysisProgress;
    [ObservableProperty] private string _reviewSummary = "";
    [ObservableProperty] private string _reviewError = "";
    private CancellationTokenSource? _analysisCts;
    private string? _lastDocumentsFolder;

    /// <summary>Header button: pick the folder of documents, run the engine, then show the review.</summary>
    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (IsBusy || IsAnalysing || !IsSuppliers) return;
        Status = Summary = "";
        var engine = Main.Settings.AnalysisEngineFolder;
        if (string.IsNullOrWhiteSpace(engine) || !AnalysisRunner.HasScript(engine))
        {
            // first use: look for the engine near the database before sending the user to Settings
            if (AnalysisRunner.Detect(Main.Root) is { } found)
            {
                Main.Settings.AnalysisEngineFolder = engine = found;
                Main.SaveSettings();
            }
            else { Dialogs.Error(Loc.T("Analysis.NoEngine")); return; }
        }
        if (AnalysisRunner.PythonFor(engine) == null) { Dialogs.Error(Loc.T("Analysis.ErrPython")); return; }

        var documents = Dialogs.PickFolder(_lastDocumentsFolder);
        if (documents == null) return;
        _lastDocumentsFolder = documents;
        int free = Rows.Count(r => r.IsEmpty && !r.IsArchived);
        if (free == 0) { Status = Loc.T("Analysis.NoFreeRows"); return; }

        IsAnalysing = true;
        AnalysisProgress = 0;
        AnalysisText = Loc.F("Analysis.Reading", Path.GetFileName(documents));
        _analysisCts = new CancellationTokenSource();
        var ui = Application.Current?.Dispatcher;
        try
        {
            var results = await Task.Run(() => AnalysisRunner.RunAsync(engine!, documents, null,
                (done, total, name) => ui?.Invoke(() =>
                {
                    AnalysisProgress = 100.0 * done / Math.Max(1, total);
                    AnalysisText = Loc.F("Analysis.Progress", done, total, name);
                }), _analysisCts.Token));
            if (results.Count == 0) { Status = Loc.T("Analysis.NoDocuments"); return; }
            ShowReview(results);
        }
        catch (OperationCanceledException) { Status = Loc.T("Analysis.Cancelled"); }
        catch (Exception ex) { Dialogs.Error(Loc.F("Analysis.Failed", ex.Message)); }
        finally
        {
            IsAnalysing = false;
            _analysisCts?.Dispose();
            _analysisCts = null;
        }
    }

    [RelayCommand]
    private void CancelAnalysis() => _analysisCts?.Cancel();

    /// <summary>"TEST 2" before "TEST 10": file names in natural order.</summary>
    private static string SortKey(AnalysisResult r) =>
        Regex.Replace(r.FileName.Length > 0 ? r.FileName : Path.GetFileName(r.File), @"\d+", m => m.Value.PadLeft(12, '0')).ToUpperInvariant();

    private void ShowReview(List<AnalysisResult> results)
    {
        Refresh();
        var ordered = results.OrderBy(SortKey, StringComparer.Ordinal).ToList();
        var matches = AnalysisMatcher.Match(ordered, _parties);
        ReviewItems.Clear();
        int no = 1;
        foreach (var m in matches) ReviewItems.Add(new AnalysisItemViewModel(this, m, no++));
        ReviewError = "";
        RecountReview();
        IsReviewing = true;
    }

    public void RecountReview()
    {
        int matched = ReviewItems.Count(i => i.Match.Status == AnalysisStatus.Matched);
        int acct = ReviewItems.Count(i => i.Match.Status == AnalysisStatus.NewAccount || i.Match.OtherAccount);
        int fresh = ReviewItems.Count(i => i.IsNewSupplier);
        int attention = ReviewItems.Count(i => !i.IsReady);
        int chosen = ReviewItems.Count(i => i.Include && i.IsReady);
        decimal total = ReviewItems.Where(i => i.Include && i.IsReady).Sum(i => i.Amount ?? 0);
        ReviewSummary = Loc.F("Analysis.Summary", ReviewItems.Count, matched, acct, fresh, attention, chosen,
            total.ToString("N2", CultureInfo.InvariantCulture));
    }

    /// <summary>Screenshot smoke test: the review with one document of each kind.</summary>
    public void ShowSampleReview()
    {
        Refresh();
        AnalysisResult Doc(string file, string name, string acc, string swift, decimal? amount, params string[] notes) => new()
        {
            File = @"C:\Docs\" + file, FileName = file, Amount = amount, Ok = name.Length > 0, CompanyName = name,
            Bank = name.Length == 0 ? "" : $"Beneficiary Name: {name}\nAccount Number: {acc}\nSWIFT CODE: {swift}",
            Ids = new(StringComparer.OrdinalIgnoreCase) { ["account"] = [acc], ["swift"] = [swift] }, Notes = [.. notes],
        };
        var known = _parties.FirstOrDefault();
        var knownAcc = known?.Banks.SelectMany(b => AnalysisMatcher.AccountIdsIn(b.Text)).FirstOrDefault() ?? "1234567890";
        ShowReview(
        [
            Doc("30,000.pdf", known?.Name ?? "DEMO SUPPLIER", knownAcc, "DEMOCNSH", 30000m),
            Doc("12,500.jpeg", known?.Name ?? "DEMO SUPPLIER", "62220000999911", "DEMOCNSH", 12500m),
            Doc("48,000.png", "YI WU SHI FENG IMPORT AND EXPORT CO., LIMITED", "NRA99505535358555891509", "ZJTLCNBHXXX", 48000m),
            Doc("7,250.docx", "YI WU SHI FENG IMPORT AND EXPORT CO., LIMITED", "NRA99505535358555891509", "ZJTLCNBHXXX", 7250m),
            Doc("TEST 36.jpeg", "ORION STAR EXPORTS LLP", "3383757556", "KKBKINBBXXX", null, "name_from_signature"),
            Doc("scan.pdf", "", "", "", 9000m),
        ]);
    }

    [RelayCommand]
    private void CloseReview()
    {
        IsReviewing = false;
        ReviewItems.Clear();
    }

    /// <summary>
    /// "Approve all": new suppliers and new accounts are saved to the Suppliers sheet (one save), then every chosen
    /// document goes on a free row with its supplier, amount and account. Generate does the rest.
    /// </summary>
    [RelayCommand]
    private async Task ApproveAllAsync()
    {
        if (IsBusy) return;
        ReviewError = "";
        var chosen = ReviewItems.Where(i => i.Include && i.IsReady).ToList();
        if (chosen.Count == 0) { ReviewError = Loc.T("Analysis.NothingChosen"); return; }
        var freeRows = Rows.Where(r => r.IsEmpty && !r.IsArchived).ToList();
        if (freeRows.Count < chosen.Count) { ReviewError = Loc.F("Analysis.TooMany", chosen.Count, freeRows.Count); return; }
        var page = Main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers");
        if (page == null) { ReviewError = "Suppliers sheet not found."; return; }

        IsBusy = true;
        try
        {
            var category = AnalysisRecords.CategoryText(_categories);
            var template = TemplateNames.FirstOrDefault(t => string.Equals(t, "INVOICE", StringComparison.OrdinalIgnoreCase)) ?? TemplateNames.FirstOrDefault();
            // the record each document goes to (supplier ID) and its account number
            var target = new Dictionary<AnalysisItemViewModel, (string Name, string? Id, int? Bank)>();
            var created = new Dictionary<AnalysisMatch, (string Id, string Name)>();      // owner match → new record
            var addedAccounts = new Dictionary<(string Id, string Account), int>();         // account written in this approval
            int newSuppliers = 0, newAccounts = 0;

            string? AccountKey(AnalysisItemViewModel i) => i.Result.AccountIds.FirstOrDefault();

            // a banking paragraph as the next free "Banking Details N" of a record (once per account)
            int? AddAccount(string id, AnalysisItemViewModel i)
            {
                var key = AccountKey(i) ?? i.Result.Bank;
                if (addedAccounts.TryGetValue((id, key), out var existing)) return existing;
                var values = page.RecordValues(id);
                if (values == null) return null;
                // already one of its accounts (written earlier in this approval or before)
                foreach (var b in SupplierBanks.Accounts(page.Headers, values).Where(b => b.Text.Trim().Length > 0))
                    if (i.Result.AccountIds.Any(a => AnalysisMatcher.AccountIdsIn(b.Text).Any(k => AnalysisMatcher.SameAccount(a, k))))
                        return addedAccounts[(id, key)] = b.Number;
                int n = AnalysisRecords.FreeBankNumber(page.Headers, values);
                if (n < 1) return null;
                page.EnsureColumn(SupplierBanks.HeaderFor(n));
                values = page.RecordValues(id)!;
                int col = page.Headers.FindIndex(h => string.Equals(h, SupplierBanks.HeaderFor(n), StringComparison.OrdinalIgnoreCase));
                if (col < 0) col = SupplierBanks.BaseColumn(page.Headers);
                values[col] = i.Result.Bank;
                page.UpdateRecord(id, values);
                newAccounts++;
                return addedAccounts[(id, key)] = n;
            }

            (string Id, string Name)? Create(AnalysisItemViewModel i)
            {
                var name = i.SupplierName.Trim();
                // a supplier of that name already exists (name edited in the review): it is used instead
                if (FindParty(name) is { } known) return (known.Id, known.Name);
                var id = page.NextId();
                page.AddRecord(AnalysisRecords.NewSupplier(page.Headers, id, i.Result, name, category, template));
                newSuppliers++;
                return (id, name);
            }

            foreach (var i in chosen)
            {
                var m = i.Match;
                switch (m.Status)
                {
                    case AnalysisStatus.Matched:
                        target[i] = (m.Supplier!.Name, m.Supplier.Id, m.BankNumber);
                        break;
                    case AnalysisStatus.NewAccount:
                        target[i] = (m.Supplier!.Name, m.Supplier.Id, AddAccount(m.Supplier.Id, i));
                        break;
                    case AnalysisStatus.NewSupplier:
                    {
                        // the group of documents of one new supplier: the first approved one adds it
                        var owner = m.Owner ?? m;
                        if (!created.TryGetValue(owner, out var rec))
                        {
                            var ownerItem = ReviewItems.FirstOrDefault(x => x.Match == owner && x.Include && x.IsReady) ?? i;
                            if (Create(ownerItem) is not { } made) continue;
                            rec = created[owner] = made;
                            // the creating document's account is account 1
                            addedAccounts[(rec.Id, AccountKey(ownerItem) ?? ownerItem.Result.Bank)] = 1;
                        }
                        int? bank = m.Owner != null && m.OtherAccount ? AddAccount(rec.Id, i) : null;
                        target[i] = (rec.Name, rec.Id, bank);
                        break;
                    }
                }
            }

            if (newSuppliers > 0 || newAccounts > 0)
            {
                await page.SaveAsync();
                if (page.IsDirty) { ReviewError = page.Status; return; }
            }
            Refresh();

            int k = 0;
            foreach (var i in chosen)
            {
                if (!target.TryGetValue(i, out var t)) continue;
                var row = freeRows[k++];
                row.PartyName = t.Name;
                row.BankNumber = t.Bank;
                row.SourceFile = i.Result.File;
                row.AmountText = NewInvoiceViewModel.FormatMoney(i.Amount!.Value);
                row.Status = i.FileName;
                row.State = "";
            }
            Recount();
            IsReviewing = false;
            ReviewItems.Clear();
            Status = Loc.F("Analysis.Applied", k, newSuppliers, newAccounts);
        }
        catch (Exception ex) { ReviewError = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}
