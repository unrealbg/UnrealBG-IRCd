namespace IRCd.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;

    public sealed class LusersService
    {
        public async ValueTask SendOnConnectAsync(IClientSession session, ServerState state, CancellationToken ct)
        {
            var serverName = "server";
            var me = session.Nick ?? "*";

            var users = state.UserCount;
            var unknown = 0;
            var ops = 0;
            var channels = state.GetAllChannels().Count();
            var servers = 0;

            await session.SendAsync($":{serverName} 251 {me} :There are {users} users and {unknown} unknown connections", ct);
            await session.SendAsync($":{serverName} 252 {me} {ops} :operator(s) online", ct);
            await session.SendAsync($":{serverName} 254 {me} {channels} :channels formed", ct);
            await session.SendAsync($":{serverName} 255 {me} :I have {users} clients and {servers} servers", ct);
        }
    }
}
