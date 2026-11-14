namespace BatchStudio.App.Services;

/// <summary>"Forgot password?": proving who you are with a fresh sign-in to the Google account linked to the password.</summary>
public static class PasswordRecovery
{
    /// <summary>The Google account in use now (kept with a new password, for a later reset); empty when none.</summary>
    public static string CurrentEmail => GoogleAccount.Instance.ActiveEmail;

    /// <summary>The reset check for a password linked to <paramref name="recoveryEmail"/> (empty: the account in use).</summary>
    public static Func<Task<bool>> For(string recoveryEmail) => async () =>
    {
        var email = recoveryEmail.Length > 0 ? recoveryEmail : CurrentEmail;
        if (email.Length == 0) { Dialogs.Info(Loc.T("Pwd.NeedGoogle")); return false; }
        if (!await GoogleAccount.Instance.SignInAsync(email)) return false;
        return string.Equals(GoogleAccount.Instance.ActiveEmail, email, StringComparison.OrdinalIgnoreCase);
    };
}
