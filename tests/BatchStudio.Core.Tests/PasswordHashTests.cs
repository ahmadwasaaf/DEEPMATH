using BatchStudio.Core.Security;
using Xunit;

namespace BatchStudio.Core.Tests;

public class PasswordHashTests
{
    [Fact]
    public void Verifies_only_the_right_password()
    {
        var h = PasswordHash.Create("كلمة سر 123");
        Assert.True(PasswordHash.Verify("كلمة سر 123", h));
        Assert.False(PasswordHash.Verify("كلمة سر 124", h));
        Assert.False(PasswordHash.Verify("", h));
        Assert.False(PasswordHash.Verify("x", ""));
        Assert.False(PasswordHash.Verify("x", "garbage"));
        Assert.NotEqual(h, PasswordHash.Create("كلمة سر 123"));   // salted
    }
}
