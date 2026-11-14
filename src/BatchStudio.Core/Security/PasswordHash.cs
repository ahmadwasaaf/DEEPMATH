using System.Security.Cryptography;
using System.Text;

namespace BatchStudio.Core.Security;

/// <summary>
/// Passwords are never kept: only a salted PBKDF2-SHA256 hash ("v1$iterations$salt$hash"), checked in constant time.
/// </summary>
public static class PasswordHash
{
    private const int Iterations = 120_000;
    public const int MinLength = 4;

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || password == null) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iter) || iter < 1000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var want = Convert.FromBase64String(parts[3]);
            var got = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, want.Length);
            return CryptographicOperations.FixedTimeEquals(got, want);
        }
        catch (FormatException) { return false; }
    }
}
