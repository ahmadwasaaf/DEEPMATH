using System.Windows;
using BatchStudio.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchStudio.App.Views;

/// <summary>One difference between the saved record and the incoming details: the saved value is kept unless the user picks the other.</summary>
public sealed partial class MergeRow : ObservableObject
{
    public required string Header { get; init; }
    public required int Column { get; init; }
    public required string Saved { get; init; }
    public required string Incoming { get; init; }
    /// <summary>A bank paragraph not saved yet: taking it adds an account (the printed account does not change).</summary>
    public bool IsNewBank { get; init; }
    public string Group { get; } = Guid.NewGuid().ToString("N");
    public string SavedText => IsNewBank ? Loc.T("MergeReview.NoAccount") : Saved.Length == 0 ? Loc.T("MergeReview.Empty") : Saved;
    public string IncomingText => IsNewBank ? Loc.T("MergeReview.AddAccount") + "\n" + Incoming : Incoming;

    [ObservableProperty] private bool _takeIncoming;
    public bool KeepSaved { get => !TakeIncoming; set { if (value) TakeIncoming = false; } }
    partial void OnTakeIncomingChanged(bool value) => OnPropertyChanged(nameof(KeepSaved));
}

/// <summary>
/// Updating / merging a saved supplier or client: only the fields that differ, side by side; the saved value is chosen
/// by default and nothing changes unless the user picks the incoming one.
/// </summary>
public partial class MergeReviewDialog : Window
{
    private MergeReviewDialog()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        FlowDirection = LanguageService.CurrentFlow;
    }

    /// <summary>True when the user confirmed (the rows say what to take).</summary>
    public static bool Review(string title, string subtitle, string okText, IReadOnlyList<MergeRow> rows)
    {
        var d = new MergeReviewDialog();
        d.TitleText.Text = title;
        d.SubText.Text = subtitle;
        d.OkText.Text = okText;
        d.RowsList.ItemsSource = rows;
        return d.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
