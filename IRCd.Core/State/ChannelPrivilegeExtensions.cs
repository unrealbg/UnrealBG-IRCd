namespace IRCd.Core.State
{
    public static class ChannelPrivilegeExtensions
    {
        public static char? ToPrefix(this ChannelPrivilege p) => p switch
        {
            ChannelPrivilege.Owner => '@',
            ChannelPrivilege.Admin => '@',
            ChannelPrivilege.Op => '@',
            ChannelPrivilege.HalfOp => '@',
            ChannelPrivilege.Voice => '+',
            _ => null
        };

        public static string ToAllPrefixes(this ChannelPrivilege p)
        {
            var prefixes = new System.Text.StringBuilder();
            
            if (p >= ChannelPrivilege.Op)
            {
                prefixes.Append('@');
            }
            
            if (p == ChannelPrivilege.Voice || p >= ChannelPrivilege.HalfOp)
            {
                if (p == ChannelPrivilege.Voice)
                    prefixes.Append('+');
            }
            
            return prefixes.ToString();
        }

        public static bool IsAtLeast(this ChannelPrivilege p, ChannelPrivilege min) => p >= min;
    }
}
