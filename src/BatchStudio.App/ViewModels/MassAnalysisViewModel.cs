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
        _supplierName = match.Supplier?.Name ?? StripLabel(match.Result.CompanyName);
        _include = IsReady && !IsTestDocument && !SwiftNeedsCheck;
    }

    /// <summary>"Customer: NINGBO ..." → "NINGBO ...": a label read with the name is not part of it.</summary>
    private static string StripLabel(string name) =>
        Regex.Replace(name ?? "", @"^\s*(?:beneficiary\s+)?(?:customer|name|company|beneficiary|account\s*name|payee)\s*[:：]\s*", "", RegexOptions.IgnoreCase).Trim();

    public int No { get; }
    public string FileName => Result.FileName.Length > 0 ? Result.FileName : Path.GetFileName(Result.File);

    [ObservableProperty] private string _amountText;
    /// <summary>The supplier's name (editable for a new supplier: the name it is saved with).</summary>
    [ObservableProperty] private string _supplierName;
    [ObservableProperty] private bool _include;

    public bool IsNewSupplier => !HasConflict && Match.Status == AnalysisStatus.NewSupplier && Match.Owner == null;

    /// <summary>The document says it is a sample / training / synthetic one: shown, but not ticked by default.</summary>
    public bool IsTestDocument => Result.Notes.Contains("test_document");

    /// <summary>
    /// The SWIFT was corrected from what the document shows, or an image leaves O / 0 open in its location / branch part:
    /// financial data is never guessed - not ticked until a person checked it against the document.
    /// </summary>
    public bool SwiftNeedsCheck => Result.Notes.Contains("swift_corrected") || Result.Notes.Contains("swift_check")
                                   || Result.Notes.Contains("multiple_accounts");

    /// <summary>More than one supplier fits: the user picks the right one in the review.</summary>
    public List<Party> Candidates => Match.Candidates;
    public bool HasCandidates => Match.Candidates.Count > 0;
    [ObservableProperty] private Party? _picked;

    partial void OnPickedChanged(Party? value)
    {
        if (value == null) return;
        Match.Supplier = value;
        Match.Owner = null;
        Match.OtherAccount = false;
        Match.MatchedBy = "picked";
        // the document's account: one of the picked supplier's accounts, or a new one for it
        var hit = value.Banks.Where(b => b.Text.Trim().Length > 0).FirstOrDefault(b =>
            Result.AccountIds.Any(a => AnalysisMatcher.AccountIdsIn(b.Text).Any(k => AnalysisMatcher.SameAccount(a, k))));
        if (hit != null) { Match.Status = AnalysisStatus.Matched; Match.BankNumber = hit.Number; }
        else if (Result.AccountIds.Any() && Result.Bank.Length > 0) { Match.Status = AnalysisStatus.NewAccount; Match.BankNumber = null; }
        else { Match.Status = AnalysisStatus.Matched; Match.BankNumber = null; }
        Match.Note = Result.Amount is null or <= 0 ? "Analysis.NoteNoAmount" : "";
        SupplierName = value.Name;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsNewSupplier));
        OnPropertyChanged(nameof(CanEditName));
        Changed();
    }
    public bool CanEditName => IsNewSupplier;

    public decimal? Amount =>
        decimal.TryParse(AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && a > 0 ? a : null;

    /// <summary>
    /// The document contradicts itself: the engine refuses to decide (no tick, cannot be approved here) and says why.
    /// </summary>
    public bool HasConflict => Result.HasConflict;

    /// <summary>Can be put on a row: a supplier (known or new) and an amount, and no contradiction in the document.</summary>
    public bool IsReady => !HasConflict && Match.Status != AnalysisStatus.Attention && Amount != null && SupplierName.Trim().Length > 0;

    /// <summary>ok (known supplier, known account) | acct (new account) | new (new supplier) | warn (attention).</summary>
    public string State => HasConflict ? "block" : IsTestDocument ? "test" : Match.Status switch
    {
        AnalysisStatus.Matched => "ok",
        AnalysisStatus.NewAccount => "acct",
        AnalysisStatus.NewSupplier => Match.OtherAccount ? "acct" : "new",
        _ => "warn",
    };

    public string StatusText => (IsTestDocument ? Loc.T("Analysis.StTestDocument") + " · " : "")
                                + (HasConflict ? Loc.T("Analysis.StBlocked") : BaseStatusText);

    private string BaseStatusText => Match.Status switch
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
    public string Remarks => string.Join(" · ", RemarkList());

    /// <summary>
    /// A blocked document: only why (the kind of conflict), on two lines at most - the conflicting values and what to do
    /// open with "View conflict details".
    /// </summary>
    public string ShortRemarks => HasConflict
        ? string.Join(" · ", Result.Conflicts.Select(c => Application.Current?.TryFindResource("Analysis.ConflictShort." + c.Code) as string ?? c.Code).Distinct())
        : Remarks;

    /// <summary>The conflicts in full: what contradicts what, and what to do.</summary>
    public string ConflictDetails => string.Join("\n", RemarkList().Select(r => "• " + r));

    public bool ShowShortRemarks => HasRemarks && !HasConflict;

    /// <summary>Where the document is now (Package moves it to the Desktop).</summary>
    [ObservableProperty] private string? _movedTo;
    public string FilePath => MovedTo ?? Result.File;

    /// <summary>"View conflict details": the conflict window (details, copy, package, the right bank accounts).</summary>
    [RelayCommand]
    private void ViewConflict() => _owner.OpenConflict(this);

    private List<string> RemarkList()
    {
        var list = new List<string>();
        // the contradictions first: what they are and between what
        foreach (var c in Result.Conflicts)
            if (Application.Current?.TryFindResource("Analysis.Conflict." + c.Code) is string ct)
                list.Add(c.Detail.Length > 0 ? ct + " (" + c.Detail + ")" : ct);
        if (HasConflict)
        {
            // blocked: what else the engine noticed (no SWIFT, new supplier without bank ...) adds nothing to it
            list.Add(Loc.T("Analysis.ConflictManual"));
            if (Result.AltBanks.Count > 0) list.Add(Loc.F("Analysis.AltAccounts", Result.AltBanks.Count));
            return list.Distinct().ToList();
        }
        if (Match.Note.Length > 0 && !(Match.Note == "Analysis.NoteNoAmount" && Amount != null)) list.Add(Loc.T(Match.Note));
        if (Match.MatchedBy == "similar" && Match.Supplier != null) list.Add(Loc.F("Analysis.NoteSimilar", Result.CompanyName));
        if (Match.Candidates.Count > 0 && Picked == null) list.Add(Loc.T("Analysis.PickSupplier"));
        // nothing read: the engine's own remarks (no account, no SWIFT ...) add nothing
        if (Match.Note != "Analysis.NoteNothingRead")
            foreach (var n in Result.Notes)
                if (Application.Current?.TryFindResource("Analysis.Engine." + n) is string text) list.Add(text);
        if (Match.Note != "Analysis.NoteNothingRead" && Result.AltBanks.Count > 0)
            list.Add(Loc.F("Analysis.AltAccounts", Result.AltBanks.Count));
        return list.Distinct().ToList();
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
        if (IsReady && !Include && !IsTestDocument && !SwiftNeedsCheck) Include = true;
        if (!IsReady && Include) Include = false;
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(Remarks));
        OnPropertyChanged(nameof(ShortRemarks));
        OnPropertyChanged(nameof(HasRemarks));
        OnPropertyChanged(nameof(ShowShortRemarks));
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
            if (File.Exists(FilePath)) Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }
}

/// <summary>Mass Invoice › Analysis: a folder of supplier documents read by the engine, reviewed, then put on the rows.</summary>
public sealed partial class MassInvoiceViewModel
{
    public ObservableCollection<AnalysisItemViewModel> ReviewItems { get; } = [];
    [ObservableProperty] private bool _isAnalysing;
    [NotifyPropertyChangedFor(nameof(IsReviewShown), nameof(IsReviewMinimized))]
    [ObservableProperty] private bool _isReviewing;
    /// <summary>Minimized: the review keeps its documents while the user works on the rows, and comes back with one click.</summary>
    [NotifyPropertyChangedFor(nameof(IsReviewShown), nameof(IsReviewMinimized))]
    [ObservableProperty] private bool _reviewHidden;
    public bool IsReviewShown => IsReviewing && !ReviewHidden;
    public bool IsReviewMinimized => IsReviewing && ReviewHidden;
    public string RestoreText => Loc.F("Analysis.Restore", ReviewItems.Count);
    /// <summary>The conflict window of a blocked document (null = closed).</summary>
    [NotifyPropertyChangedFor(nameof(HasConflictEditor))]
    [ObservableProperty] private ConflictEditorViewModel? _conflictEditor;
    public bool HasConflictEditor => ConflictEditor != null;

    [RelayCommand]
    private void MinimizeReview() => ReviewHidden = true;

    [RelayCommand]
    private void RestoreReview() => ReviewHidden = false;

    internal void OpenConflict(AnalysisItemViewModel item) => ConflictEditor = new ConflictEditorViewModel(this, item);

    /// <summary>
    /// The conflict window's Save (on the user's responsibility, after checking with the supplier): the document is matched
    /// again with the name and bank accounts entered, and leaves the blocked state.
    /// </summary>
    internal void ResolveConflict(AnalysisItemViewModel item, string name, IReadOnlyList<string> banks)
    {
        int index = ReviewItems.IndexOf(item);
        if (index < 0) return;
        Refresh();
        var result = item.Result.Verified(item.FilePath, name, banks);
        var match = AnalysisMatcher.Match([result], _parties)[0];
        var fresh = new AnalysisItemViewModel(this, match, item.No) { MovedTo = item.MovedTo };
        fresh.Include = fresh.IsReady;
        ReviewItems[index] = fresh;
        ConflictEditor = null;
        RecountReview();
    }

    /// <summary>
    /// Package: the document is moved (not copied) to the Desktop folder "Rejected files", keeping its name
    /// ("5000.pdf"; "5000 (2).pdf" when that name is taken there). Returns where it went.
    /// </summary>
    internal string PackageFile(AnalysisItemViewModel item)
    {
        var source = item.FilePath;
        if (!File.Exists(source)) throw new FileNotFoundException(Loc.F("Analysis.PackageMissing", Path.GetFileName(source)));
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), RejectedFolder);
        Directory.CreateDirectory(folder);
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            return source;   // already there
        var dest = BankAccountPdf.FreePath(folder, Path.GetFileName(source));
        File.Move(source, dest);
        item.MovedTo = dest;
        return dest;
    }

    /// <summary>The Desktop folder Package moves blocked documents to (one folder for every kind of conflict).</summary>
    public const string RejectedFolder = "Rejected files";
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

        // the folder picker opens on the Desktop (where the documents are usually saved)
        var documents = Dialogs.PickFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
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
        ReviewHidden = false;
        IsReviewing = true;
    }

    /// <summary>Header tick: all ready documents chosen (true), none (false), some (null).</summary>
    public bool? AllChosen
    {
        get
        {
            var ready = ReviewItems.Where(i => i.IsReady).ToList();
            if (ready.Count == 0 || ready.All(i => !i.Include)) return false;
            return ready.All(i => i.Include) ? true : null;
        }
        set
        {
            bool on = value == true;
            _bulk = true;
            foreach (var i in ReviewItems.Where(i => i.IsReady)) i.Include = on;
            _bulk = false;
            RecountReview();
        }
    }

    private bool _bulk;

    public void RecountReview()
    {
        if (_bulk) return;
        OnPropertyChanged(nameof(AllChosen));
        int matched = ReviewItems.Count(i => !i.HasConflict && i.Match.Status == AnalysisStatus.Matched);
        int acct = ReviewItems.Count(i => !i.HasConflict && (i.Match.Status == AnalysisStatus.NewAccount || i.Match.OtherAccount));
        int fresh = ReviewItems.Count(i => i.IsNewSupplier);
        OnPropertyChanged(nameof(RestoreText));
        int blocked = ReviewItems.Count(i => i.HasConflict);
        int attention = ReviewItems.Count(i => !i.IsReady && !i.HasConflict);
        int chosen = ReviewItems.Count(i => i.Include && i.IsReady);
        decimal total = ReviewItems.Where(i => i.Include && i.IsReady).Sum(i => i.Amount ?? 0);
        ReviewSummary = Loc.F("Analysis.Summary", ReviewItems.Count, matched, acct, fresh, attention, chosen,
            total.ToString("N2", CultureInfo.InvariantCulture))
            + (blocked > 0 ? "  ·  " + Loc.F("Analysis.SummaryBlocked", blocked) : "");
    }

    /// <summary>Screenshot smoke test: the review with one document of each kind.</summary>
    /// <summary>Screenshots: the conflict window of the sample's blocked document.</summary>
    public void OpenSampleConflict()
    {
        if (ReviewItems.FirstOrDefault(i => i.HasConflict) is { } blocked) OpenConflict(blocked);
    }

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
            Doc("90,000.pdf", "ORBITAL MACHINE PARTS TRADING LIMITED", "55563751544820", "EHCBHKH0XXX", 90000m, "test_document"),
            new AnalysisResult
            {
                File = @"C:\Docs\18,750.pdf", FileName = "18,750.pdf", Amount = 18750m, Ok = true, CompanyName = "VANTAGE PRECISION INDUSTRIES LIMITED",
                Notes = ["conflicting_instructions"], Conflicts = [new("conflicting_instructions", "HKEUR50187742 / HKUSD50187743")],
            },
        ]);
    }

    [RelayCommand]
    private void CloseReview()
    {
        IsReviewing = false;
        ReviewHidden = false;
        ConflictEditor = null;
        ReviewItems.Clear();
        OnPropertyChanged(nameof(RestoreText));
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
        if (freeRows.Count == 0) { ReviewError = Loc.T("Analysis.NoFreeRows"); return; }
        // more documents than free rows: the first ones (review order) go on the rows, the rest is listed afterwards
        var leftOut = chosen.Skip(freeRows.Count).ToList();
        if (leftOut.Count > 0) chosen = chosen.Take(freeRows.Count).ToList();
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
            int? AddAccount(string id, AnalysisItemViewModel i) => AddBank(id, i.Result.Bank, i.Result.AccountIds.ToList());

            // a banking paragraph as the next free "Banking Details N" of a record (once per account)
            int? AddBank(string id, string bankText, List<string> accountIds)
            {
                var key = accountIds.FirstOrDefault() ?? bankText;
                if (addedAccounts.TryGetValue((id, key), out var existing)) return existing;
                var values = page.RecordValues(id);
                if (values == null) return null;
                // already one of its accounts (written earlier in this approval or before)
                foreach (var b in SupplierBanks.Accounts(page.Headers, values).Where(b => b.Text.Trim().Length > 0))
                    if (accountIds.Any(a => AnalysisMatcher.AccountIdsIn(b.Text).Any(k => AnalysisMatcher.SameAccount(a, k))))
                        return addedAccounts[(id, key)] = b.Number;
                int n = AnalysisRecords.FreeBankNumber(page.Headers, values);
                if (n < 1) return null;
                page.EnsureColumn(SupplierBanks.HeaderFor(n));
                values = page.RecordValues(id)!;
                int col = page.Headers.FindIndex(h => string.Equals(h, SupplierBanks.HeaderFor(n), StringComparison.OrdinalIgnoreCase));
                if (col < 0) col = SupplierBanks.BaseColumn(page.Headers);
                values[col] = bankText;
                page.UpdateRecord(id, values);
                newAccounts++;
                return addedAccounts[(id, key)] = n;
            }

            (string Id, string Name)? Create(AnalysisItemViewModel i)
            {
                var name = i.SupplierName.Trim();
                // a supplier of that name already exists (name edited in the review): it is used instead
                if (FindParty(name) is { } known) return (known.Id, known.Name);
                // already added by an earlier Approve whose save had not finished (no second copy)
                if (page.SameNameId(name) is { Length: > 0 } pendingId)
                    return (pendingId, page.RecordValues(pendingId) is { } rv && rv.Length > 1 ? rv[1] : name);
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

            // the document's other active accounts (USD + EUR ...): saved as further accounts of the supplier, never instead of it
            foreach (var (i, t) in target)
                if (t.Id is { } sid)
                    foreach (var alt in i.Result.AltBanks)
                        AddBank(sid, alt, AnalysisMatcher.AccountIdsIn(alt).ToList());

            if (newSuppliers > 0 || newAccounts > 0 || page.IsDirty)
            {
                // the Suppliers sheet must be saved before the rows use the new suppliers / accounts.
                // A save already running (auto-save) or Excel busy: wait for it, then save again (up to ~90 s).
                ReviewError = Loc.T("Analysis.Saving");
                var until = DateTime.Now.AddSeconds(90);
                while (true)
                {
                    if (!page.IsSaving) await page.SaveAsync();
                    if (!page.IsDirty) break;
                    if (page.Status.StartsWith(Loc.T("Status.Error"), StringComparison.Ordinal) || page.Status == Loc.T("Validate.MissingId"))
                    {
                        ReviewError = page.Status;
                        return;
                    }
                    if (DateTime.Now > until)
                    {
                        ReviewError = Loc.F("Analysis.NotSaved", page.Status);
                        return;
                    }
                    await Task.Delay(700);
                }
                ReviewError = "";
            }
            Refresh();

            int k = 0;
            foreach (var i in chosen)
            {
                if (!target.TryGetValue(i, out var t)) continue;
                var row = freeRows[k++];
                row.PartyName = t.Name;
                row.BankNumber = t.Bank;
                row.SourceFile = i.FilePath;
                row.AmountText = NewInvoiceViewModel.FormatMoney(i.Amount!.Value);
                row.Status = i.FileName;
                row.State = "";
            }
            Recount();
            // only the documents put on rows leave the review; the rest (blocked, not ticked, no free row) stay in it
            foreach (var i in chosen.Where(target.ContainsKey).Take(k).ToList()) ReviewItems.Remove(i);
            if (ReviewItems.Count == 0) CloseReview();
            else RecountReview();
            Status = Loc.F("Analysis.Applied", k, newSuppliers, newAccounts);
            if (leftOut.Count > 0)
                Dialogs.Info(Loc.F("Analysis.LeftOut", leftOut.Count, k) + "\n\n" + string.Join("\n", leftOut.Select(i => "•  " + i.FileName)));
        }
        catch (Exception ex) { ReviewError = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}
