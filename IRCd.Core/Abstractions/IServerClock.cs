namespace IRCd.Core.Abstractions
{
    public interface IServerClock
    {
        DateTimeOffset UtcNow { get; }
    }
}
