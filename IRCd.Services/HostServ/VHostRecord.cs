namespace IRCd.Services.HostServ
{
    using System;

    public sealed class VHostRecord
    {
        public string Nick { get; set; } = string.Empty;

        public string VHost { get; set; } = string.Empty;

        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
