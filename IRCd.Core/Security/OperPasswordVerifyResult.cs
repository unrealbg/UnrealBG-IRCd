namespace IRCd.Core.Security
{
    public readonly record struct OperPasswordVerifyResult(bool Success, OperPasswordVerifyFailure Failure)
    {
        public static OperPasswordVerifyResult Ok() => new(true, OperPasswordVerifyFailure.None);

        public static OperPasswordVerifyResult Fail(OperPasswordVerifyFailure failure) => new(false, failure);
    }

    public enum OperPasswordVerifyFailure
    {
        None = 0,
        Incorrect = 1,
        PlaintextDisallowed = 2,
        InvalidHashFormat = 3,
    }
}
