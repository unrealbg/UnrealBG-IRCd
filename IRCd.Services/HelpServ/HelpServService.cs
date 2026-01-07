namespace IRCd.Services.HelpServ
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Services.AdminServ;
    using IRCd.Services.Agent;
    using IRCd.Services.BotServ;
    using IRCd.Services.ChanServ;
    using IRCd.Services.DevServ;
    using IRCd.Services.HostServ;
    using IRCd.Services.InfoServ;
    using IRCd.Services.MemoServ;
    using IRCd.Services.NickServ;
    using IRCd.Services.OperServ;
    using IRCd.Services.RootServ;
    using IRCd.Services.SeenServ;
    using IRCd.Services.StatServ;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class HelpServService
    {
        private readonly IOptions<IrcOptions> _options;

        public HelpServService(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, string text, ServerState state, CancellationToken ct)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                await ReplyAsync(session, HelpServMessages.HelpIntro, ct);
                await ReplyAsync(session, HelpServMessages.HelpList, ct);
                return;
            }

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cmd = parts.Length > 0 ? parts[0] : "HELP";

            if (cmd.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 1)
                {
                    await ReplyAsync(session, HelpServMessages.HelpIntro, ct);
                    await ReplyAsync(session, HelpServMessages.HelpList, ct);
                    await ReplyAsync(session, "Tip: /msg <Service> HELP also works (e.g. /msg NickServ HELP)", ct);
                    return;
                }

                var service = parts[1];
                await SendServiceHelpAsync(session, service, ct);
                return;
            }

            if (cmd.Equals("LIST", StringComparison.OrdinalIgnoreCase) || cmd.Equals("SERVICES", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, HelpServMessages.HelpList, ct);
                return;
            }

            await ReplyAsync(session, "Unknown command. Use HELP [service] or LIST.", ct);
        }

        private async ValueTask SendServiceHelpAsync(IClientSession session, string service, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                await ReplyAsync(session, HelpServMessages.HelpList, ct);
                return;
            }

            // Normalize common aliases so users can ask HelpServ about them.
            var s = service.Trim();
            if (s.Equals("NS", StringComparison.OrdinalIgnoreCase)) s = NickServMessages.ServiceName;
            if (s.Equals("CS", StringComparison.OrdinalIgnoreCase)) s = ChanServMessages.ServiceName;
            if (s.Equals("OS", StringComparison.OrdinalIgnoreCase)) s = OperServMessages.ServiceName;
            if (s.Equals("MS", StringComparison.OrdinalIgnoreCase)) s = MemoServMessages.ServiceName;
            if (s.Equals("SS", StringComparison.OrdinalIgnoreCase)) s = SeenServMessages.ServiceName;
            if (s.Equals("IS", StringComparison.OrdinalIgnoreCase)) s = InfoServMessages.ServiceName;
            if (s.Equals("HS", StringComparison.OrdinalIgnoreCase)) s = HostServMessages.ServiceName;
            if (s.Equals("BS", StringComparison.OrdinalIgnoreCase)) s = BotServMessages.ServiceName;
            if (s.Equals("AG", StringComparison.OrdinalIgnoreCase)) s = AgentMessages.ServiceName;

            if (s.Equals(NickServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "NickServ: nickname registration/identify.", ct);
                await ReplyAsync(session, NickServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg NickServ HELP | REGISTER | IDENTIFY | INFO", ct);
                return;
            }

            if (s.Equals(ChanServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "ChanServ: channel registration and channel management.", ct);
                await ReplyAsync(session, ChanServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg ChanServ HELP | REGISTER | OP/DEOP/VOICE/DEVOICE", ct);
                return;
            }

            if (s.Equals(OperServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "OperServ: operator commands.", ct);
                await ReplyAsync(session, OperServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg OperServ HELP", ct);
                return;
            }

            if (s.Equals(MemoServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "MemoServ: send/receive memos.", ct);
                await ReplyAsync(session, MemoServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg MemoServ HELP", ct);
                return;
            }

            if (s.Equals(SeenServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "SeenServ: see when a nick was last online.", ct);
                await ReplyAsync(session, SeenServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg SeenServ <nick>", ct);
                return;
            }

            if (s.Equals(InfoServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "InfoServ: network/server information.", ct);
                await ReplyAsync(session, InfoServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg InfoServ INFO | ABOUT", ct);
                return;
            }

            if (s.Equals(StatServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "StatServ: server statistics.", ct);
                await ReplyAsync(session, StatServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg StatServ STATS", ct);
                return;
            }

            if (s.Equals(AdminServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "AdminServ: staff management.", ct);
                await ReplyAsync(session, AdminServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg AdminServ HELP", ct);
                return;
            }

            if (s.Equals(DevServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "DevServ: developer/debug commands (not implemented yet).", ct);
                await ReplyAsync(session, DevServMessages.HelpIntro, ct);
                return;
            }

            if (s.Equals(RootServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "RootServ: root control (dangerous).", ct);
                await ReplyAsync(session, RootServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg RootServ HELP", ct);
                return;
            }

            if (s.Equals(HostServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "HostServ: vHost management.", ct);
                await ReplyAsync(session, HostServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg HostServ HELP", ct);
                return;
            }

            if (s.Equals(BotServMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "BotServ: channel bot control.", ct);
                await ReplyAsync(session, BotServMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg BotServ HELP", ct);
                return;
            }

            if (s.Equals(AgentMessages.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(session, "Agent: network settings & control.", ct);
                await ReplyAsync(session, AgentMessages.HelpIntro, ct);
                await ReplyAsync(session, "Usage: /msg Agent HELP", ct);
                return;
            }

            await ReplyAsync(session, $"Unknown service '{service}'.", ct);
            await ReplyAsync(session, HelpServMessages.HelpList, ct);
        }

        private ValueTask ReplyAsync(IClientSession session, string text, CancellationToken ct)
        {
            var server = _options.Value.ServerInfo?.Name ?? "server";
            var to = session.Nick ?? "*";
            var line = $":{HelpServMessages.ServiceName}!services@{server} NOTICE {to} :{text}";
            return session.SendAsync(line, ct);
        }
    }
}
