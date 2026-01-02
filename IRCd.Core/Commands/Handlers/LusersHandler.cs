using IRCd.Core.Abstractions;
using IRCd.Core.Commands.Contracts;
using IRCd.Core.Protocol;
using IRCd.Core.Services;
using IRCd.Core.State;

public sealed class LusersHandler : IIrcCommandHandler
{
    public string Command => "LUSERS";
    private readonly LusersService _svc;

    public LusersHandler(LusersService svc) => _svc = svc;

    public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
    {
        if (!session.IsRegistered)
        {
            await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
            return;
        }

        await _svc.SendOnConnectAsync(session, state, ct);
    }
}
