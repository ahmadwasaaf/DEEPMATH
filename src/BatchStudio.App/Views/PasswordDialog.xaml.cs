using System.Windows;
using System.Windows.Input;
using BatchStudio.App.Services;
using BatchStudio.Core.Security;

namespace BatchStudio.App.Views;

/// <summary>
/// The password window of the bank accounts and Secure mode: create a password (typed twice) or enter it, with
/// "Forgot password?" (a fresh Google sign-in with the recovery account, then a new password).
/// </summary>
public partial class PasswordDialog : Window
{
    private bool _create;
    private Func<string, bool>? _check;
    private Func<Task<bool>>? _forgot;
    /// <summary>Create (or reset): the new password.</summary>
    public string? NewPassword { get; private set; }

    private PasswordDialog()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        FlowDirection = LanguageService.CurrentFlow;
        Loaded += (_, _) => First.Focus();
    }

    private void SetCreate(string title, string sub)
    {
        _create = true;
        TitleText.Text = title;
        SubText.Text = sub;
        Glyph.Text = "";
        ConfirmPanel.Visibility = Visibility.Visible;
        ForgotLink.Visibility = Visibility.Collapsed;
        FirstLabel.Text = Loc.T("Pwd.New");
        OkText.Text = Loc.T("Common.Save");
        OkGlyph.Text = "";
        First.Clear(); Second.Clear();
        Error("");
    }

    /// <summary>Asks for a new password (typed twice, at least 4 characters). Null when cancelled.</summary>
    public static string? Create(string title, string subtitle)
    {
        var d = new PasswordDialog();
        d.SetCreate(title, subtitle);
        return d.ShowDialog() == true ? d.NewPassword : null;
    }

    /// <summary>
    /// Asks for the password, checked by <paramref name="check"/>. With <paramref name="forgot"/>: "Forgot password?" runs it
    /// (true = the user proved who they are) and the window asks for a new password, returned in <paramref name="reset"/>.
    /// </summary>
    public static bool Ask(string title, string subtitle, Func<string, bool> check, Func<Task<bool>>? forgot, out string? reset)
    {
        var d = new PasswordDialog { _check = check, _forgot = forgot };
        d.TitleText.Text = title;
        d.SubText.Text = subtitle;
        d.ConfirmPanel.Visibility = Visibility.Collapsed;
        d.ForgotLink.Visibility = forgot == null ? Visibility.Collapsed : Visibility.Visible;
        d.OkText.Text = Loc.T("Pwd.Unlock");
        bool ok = d.ShowDialog() == true;
        reset = d.NewPassword;
        return ok;
    }

    private void Error(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = msg.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_create)
        {
            if (First.Password.Length < PasswordHash.MinLength) { Error(Loc.F("Pwd.TooShort", PasswordHash.MinLength)); First.Focus(); return; }
            if (First.Password != Second.Password) { Error(Loc.T("Pwd.NoMatch")); Second.Clear(); Second.Focus(); return; }
            NewPassword = First.Password;
            DialogResult = true;
            return;
        }
        if (_check != null && _check(First.Password)) { DialogResult = true; return; }
        Error(Loc.T("Pwd.Wrong"));
        First.Clear();
        First.Focus();
    }

    private async void OnForgot(object sender, MouseButtonEventArgs e)
    {
        if (_forgot == null) return;
        IsEnabled = false;
        Error("");
        bool proved;
        try { proved = await _forgot(); }
        finally { IsEnabled = true; Activate(); }
        if (!proved) { Error(Loc.T("Pwd.ForgotFailed")); return; }
        SetCreate(Loc.T("Pwd.ResetTitle"), Loc.T("Pwd.ResetText"));
        First.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // like a Windows PIN: opens as soon as the typed password is right (checked off the screen thread; only the last
    // keystroke counts); "wrong password" only on Enter / Unlock
    private int _typed;
    private async void OnTyped(object sender, RoutedEventArgs e)
    {
        if (_create || _check == null) return;
        if (ErrorText.Visibility == Visibility.Visible) Error("");
        var pw = First.Password;
        int n = ++_typed;
        if (pw.Length < PasswordHash.MinLength) return;
        var check = _check;
        bool ok;
        try { ok = await Task.Run(() => check(pw)); } catch { return; }
        if (ok && n == _typed && IsVisible && !_create) DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (_create && sender == First) Second.Focus(); else OnOk(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is System.Windows.Controls.Border or System.Windows.Controls.StackPanel)
            try { DragMove(); } catch { }
    }
}
