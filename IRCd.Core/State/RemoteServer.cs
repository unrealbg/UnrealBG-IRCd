namespace IRCd.Core.State
{
    public sealed class RemoteServer
    {
        public string Name { get; init; } = string.Empty;

        public string Sid { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string ConnectionId { get; init; } = string.Empty;

        public string? ParentSid { get; init; }
    }
}
