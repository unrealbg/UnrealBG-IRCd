using IRCd.Core.Security;

public sealed class OperPasswordVerifierTests
{
    [Fact]
    public void HashedPassword_VerifiesOk_AndRejectsBad()
    {
        var password = "s3cr3t!";
        var hash = Pbkdf2OperPasswordHasher.Hash(password, iterations: 10_000);

        var v = new OperPasswordVerifier();

        Assert.True(v.Verify(password, hash, requireHashed: true).Success);
        Assert.False(v.Verify("wrong", hash, requireHashed: true).Success);
    }

    [Fact]
    public void PlaintextPassword_LegacyMode_AllowsMatch()
    {
        var v = new OperPasswordVerifier();

        var ok = v.Verify("mypass", "mypass", requireHashed: false);
        Assert.True(ok.Success);

        var bad = v.Verify("nope", "mypass", requireHashed: false);
        Assert.False(bad.Success);
    }

    [Fact]
    public void PlaintextPassword_RequireHashed_RejectsClearly()
    {
        var v = new OperPasswordVerifier();

        var r = v.Verify("mypass", "mypass", requireHashed: true);
        Assert.False(r.Success);
        Assert.Equal(OperPasswordVerifyFailure.PlaintextDisallowed, r.Failure);
    }
}
