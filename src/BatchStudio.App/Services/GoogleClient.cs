namespace BatchStudio.App.Services;

/// <summary>
/// DEEPMATH's OAuth client (Google Cloud project "DEEPMATH", client type Desktop app). For installed apps Google does
/// not treat the secret as confidential (it ships inside every desktop app); the sign-in is protected by PKCE and the
/// loopback redirect, and the user's tokens stay encrypted on the user's computer.
/// </summary>
internal static class GoogleClient
{
    public const string Id = "DEEPMATH_GOOGLE_CLIENT_ID";
    public const string Secret = "DEEPMATH_GOOGLE_CLIENT_SECRET";
}
