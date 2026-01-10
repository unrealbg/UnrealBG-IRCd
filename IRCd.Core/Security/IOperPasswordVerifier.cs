namespace IRCd.Core.Security
{
    public interface IOperPasswordVerifier
    {
        OperPasswordVerifyResult Verify(string providedPassword, string storedPassword, bool requireHashed);
    }
}
