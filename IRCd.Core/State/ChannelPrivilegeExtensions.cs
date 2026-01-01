namespace IRCd.Core.State
{
    public static class ChannelPrivilegeExtensions
    {
        public static char? ToPrefix(this ChannelPrivilege p) => p switch
        {
            ChannelPrivilege.Owner => '*',
            ChannelPrivilege.Admin => '&',
            ChannelPrivilege.Op => '@',
            ChannelPrivilege.HalfOp => '%',
            ChannelPrivilege.Voice => '+',
            _ => null
        };

        public static bool IsAtLeast(this ChannelPrivilege p, ChannelPrivilege min) => p >= min;
    }
}
