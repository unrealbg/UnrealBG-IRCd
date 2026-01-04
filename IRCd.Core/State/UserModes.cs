namespace IRCd.Core.State
{
    [System.Flags]
    public enum UserModes
    {
        None = 0,
        Invisible = 1 << 0,
    }
}
