namespace IRCd.Core.State
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    public sealed class User
    {
        public required string ConnectionId { get; init; }

        public string? Nick { get; set; }

        public string? UserName { get; set; }

        public string? RealName { get; set; }

        public bool IsRegistered { get; set; }
    }
}
