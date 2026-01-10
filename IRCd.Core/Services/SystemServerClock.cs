namespace IRCd.Core.Services
{
    using System;

    using IRCd.Core.Abstractions;

    public sealed class SystemServerClock : IServerClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
