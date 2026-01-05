namespace IRCd.Core.State
{
    [System.Flags]
    public enum UserModes
    {
        None = 0,
        Invisible = 1 << 0,
        Secure = 1 << 1,
        Operator = 1 << 2,
    }
}
