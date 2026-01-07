namespace IRCd.Services.AdminServ
{
    using System;

    public sealed record AdminStaffEntry
    {
        public string Account { get; init; } = string.Empty;

        public string[] Flags { get; init; } = Array.Empty<string>();

        public string? OperClass { get; init; }
    }
}
