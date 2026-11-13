using System.Windows;
using BatchStudio.App.Services;
using BatchStudio.Core.Invoices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>
/// The conflict window of a blocked document ("View conflict details"): the whole conflict, Copy (its text), Package (the
/// document moved to a Desktop folder named after the conflict), and the right bank accounts entered by the user (the same
/// editor as the supplier form, + for more accounts). Save is the user's decision: the document is matched again.
/// </summary>
public sealed partial class ConflictEditorViewModel : ObservableObject
{
    private readonly MassInvoiceViewModel _owner;
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public AnalysisItemViewModel Item { get; }
    public BankGroupViewModel Banks { get; }
    public string Title => (Item.IsTestDocument ? Loc.T("Analysis.StTestDocument") + " · " : "") + Loc.T("Analysis.StBlocked");
    public string Details => Item.ConflictDetails;
    public string FileName => Item.FileName;

    [ObservableProperty] private string _supplierName;
    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isPackaged;

    /// <summary>The bank data the window opened with: Save stays off until the user really changes it.</summary>
    private readonly string _initial;
    public bool CanSave => Snapshot() != _initial;

    public ConflictEditorViewModel(MassInvoiceViewModel owner, AnalysisItemViewModel item)
    {
        _owner = owner;
        Item = item;
        _supplierName = item.SupplierName;
        // what the engine read is a starting point only: the user replaces it with what the supplier confirmed
        var headers = new List<string> { SupplierBanks.HeaderFor(1) };
        _values[SupplierBanks.HeaderFor(1)] = item.Result.Bank;
        int n = 2;
        foreach (var alt in item.Result.AltBanks)
        {
            headers.Add(SupplierBanks.HeaderFor(n));
            _values[SupplierBanks.HeaderFor(n++)] = alt;
        }
        Banks = new BankGroupViewModel(headers, k => _values.TryGetValue(k, out var v) ? v : "", (k, v) => _values[k] = v, expanded: true);
        _initial = Snapshot();
        Banks.Changed += () =>
        {
            OnPropertyChanged(nameof(CanSave));
            SaveCommand.NotifyCanExecuteChanged();
        };
        IsPackaged = item.MovedTo != null;
        if (IsPackaged) Message = Loc.F("Analysis.Packaged", item.MovedTo!);
    }

    /// <summary>Copy: the whole conflict (title, file, details) in one click.</summary>
    [RelayCommand]
    private async Task Copy()
    {
        var text = Title + "\n" + FileName + "\n\n" + Details;
        try { Clipboard.SetText(text); }
        catch { return; }
        IsCopied = true;
        await Task.Delay(1500);
        IsCopied = false;
    }

    /// <summary>Package: the document moved to the Desktop folder of its conflict, under its own name.</summary>
    [RelayCommand]
    private void Package()
    {
        try
        {
            var dest = _owner.PackageFile(Item);
            IsPackaged = true;
            Message = Loc.F("Analysis.Packaged", dest);
        }
        catch (Exception ex) { Message = Loc.F("Analysis.PackageFailed", ex.Message); }
    }

    [RelayCommand]
    private void OpenFile() => Item.OpenFileCommand.Execute(null);

    /// <summary>
    /// The bank accounts as they stand (text normalised: line ends, spaces, empty lines), and which one is the default:
    /// typing something and undoing it is not a change; Package or a new name alone is not either.
    /// </summary>
    private string Snapshot()
    {
        static string Norm(string t) => string.Join("\n", (t ?? "").Replace("\r", "").Split('\n')
            .Select(l => string.Join(" ", l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0));
        var accounts = Banks.Accounts.Select(a => (a.Number, Text: Norm(a.Value))).Where(a => a.Text.Length > 0).ToList();
        return string.Join("\u0001", accounts.Select(a => a.Text)) + "\u0002" + (accounts.Count > 1 ? Banks.DefaultNumber : 0);
    }

    /// <summary>Save: on the user's responsibility (they checked the details with the supplier), and only after a real change.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave) return;
        var name = SupplierName.Trim();
        if (name.Length == 0) { Message = Loc.T("Analysis.ConflictNoName"); return; }
        // the account printed on invoices (radio button) first: it is the one this invoice is paid to
        var banks = Banks.Accounts.OrderBy(a => a.Number == Banks.DefaultNumber ? 0 : 1).ThenBy(a => a.Number)
            .Select(a => a.Value.Trim()).Where(v => v.Length > 0).ToList();
        if (banks.Count == 0) { Message = Loc.T("Analysis.ConflictNoBank"); return; }
        if (!Dialogs.Confirm(Loc.T("Analysis.ConflictConfirm"))) return;
        _owner.ResolveConflict(Item, name, banks);
    }

    [RelayCommand]
    private void Close() => _owner.ConflictEditor = null;
}
