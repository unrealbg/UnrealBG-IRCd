namespace IRCd.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    public sealed class LusersService
    {
        private readonly Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions> _options;

        public LusersService(Microsoft.Extensions.Options.IOptions<IRCd.Shared.Options.IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask SendOnConnectAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";
            var me = session.Nick ?? "*";

            var users = state.UserCount;
            var unknown = 0;
            var ops = state.GetUsersSnapshot().Count(u => u.IsRegistered && u.Modes.HasFlag(UserModes.Operator));
            var channels = state.GetAllChannels().Count();
            var servers = 0;

            await session.SendAsync($":{serverName} 251 {me} :There are {users} users and {unknown} unknown connections", ct);
            await session.SendAsync($":{serverName} 252 {me} {ops} :operator(s) online", ct);
            await session.SendAsync($":{serverName} 254 {me} {channels} :channels formed", ct);
            await session.SendAsync($":{serverName} 255 {me} :I have {users} clients and {servers} servers", ct);
        }
    }
}
