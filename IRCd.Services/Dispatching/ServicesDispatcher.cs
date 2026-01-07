namespace IRCd.Services.Dispatching
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.State;
    using IRCd.Services.Agent;
    using IRCd.Services.AdminServ;
    using IRCd.Services.BotServ;
    using IRCd.Services.ChanServ;
    using IRCd.Services.DevServ;
    using IRCd.Services.HelpServ;
    using IRCd.Services.InfoServ;
    using IRCd.Services.HostServ;
    using IRCd.Services.MemoServ;
    using IRCd.Services.NickServ;
    using IRCd.Services.OperServ;
    using IRCd.Services.RootServ;
    using IRCd.Services.StatServ;
    using IRCd.Services.SeenServ;

    public sealed class ServicesDispatcher : IServiceCommandDispatcher
    {
        private readonly NickServService _nickServ;
        private readonly ChanServService _chanServ;
        private readonly OperServService _operServ;
        private readonly MemoServService _memoServ;
        private readonly SeenServService _seenServ;
        private readonly InfoServService _infoServ;
        private readonly StatServService _statServ;
        private readonly AdminServService _adminServ;
        private readonly DevServService _devServ;
        private readonly HelpServService _helpServ;
        private readonly RootServService _rootServ;
        private readonly HostServService _hostServ;
        private readonly BotServService _botServ;
        private readonly AgentService _agent;

        public ServicesDispatcher(
            NickServService nickServ,
            ChanServService chanServ,
            OperServService operServ,
            MemoServService memoServ,
            SeenServService seenServ,
            InfoServService infoServ,
            StatServService statServ,
            AdminServService adminServ,
            DevServService devServ,
            HelpServService helpServ,
            RootServService rootServ,
            HostServService hostServ,
            BotServService botServ,
            AgentService agent)
        {
            _nickServ = nickServ;
            _chanServ = chanServ;
            _operServ = operServ;
            _memoServ = memoServ;
            _seenServ = seenServ;
            _infoServ = infoServ;
            _statServ = statServ;
            _adminServ = adminServ;
            _devServ = devServ;
            _helpServ = helpServ;
            _rootServ = rootServ;
            _hostServ = hostServ;
            _botServ = botServ;
            _agent = agent;
        }

        public async ValueTask<bool> TryHandlePrivmsgAsync(IClientSession session, string target, string text, ServerState state, CancellationToken ct)
        {
            if (IsNickServTarget(target))
            {
                await _nickServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsChanServTarget(target))
            {
                await _chanServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsOperServTarget(target))
            {
                await _operServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsMemoServTarget(target))
            {
                await _memoServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsSeenServTarget(target))
            {
                await _seenServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsInfoServTarget(target))
            {
                await _infoServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsStatServTarget(target))
            {
                await _statServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsAdminServTarget(target))
            {
                await _adminServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsDevServTarget(target))
            {
                await _devServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsHelpServTarget(target))
            {
                await _helpServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsRootServTarget(target))
            {
                await _rootServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsHostServTarget(target))
            {
                await _hostServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsBotServTarget(target))
            {
                await _botServ.HandleAsync(session, text, state, ct);
                return true;
            }

            if (IsAgentTarget(target))
            {
                await _agent.HandleAsync(session, text, state, ct);
                return true;
            }

            return false;
        }

        public async ValueTask<bool> TryHandleNoticeAsync(IClientSession session, string target, string text, ServerState state, CancellationToken ct)
        {
            return await TryHandlePrivmsgAsync(session, target, text, state, ct);
        }

        private static bool IsNickServTarget(string target)
        {
            return IsAny(target, NickServMessages.ServiceName, "NS", "NICKSERV");
        }

        private static bool IsChanServTarget(string target)
        {
            return IsAny(target, ChanServMessages.ServiceName, "CS", "CHANSERV");
        }

        private static bool IsOperServTarget(string target)
        {
            return IsAny(target, OperServMessages.ServiceName, "OS", "OPERSERV");
        }

        private static bool IsMemoServTarget(string target)
        {
            return IsAny(target, MemoServMessages.ServiceName, "MS", "MEMOSERV");
        }

        private static bool IsSeenServTarget(string target)
        {
            return IsAny(target, SeenServMessages.ServiceName, "SS", "SEENSERV");
        }

        private static bool IsInfoServTarget(string target)
        {
            return IsAny(target, InfoServMessages.ServiceName, "IS", "INFOSERV");
        }

        private static bool IsStatServTarget(string target)
        {
            return IsAny(target, StatServMessages.ServiceName, "STATSERV");
        }

        private static bool IsAdminServTarget(string target)
        {
            return IsAny(target, AdminServMessages.ServiceName, "ADMINSERV");
        }

        private static bool IsDevServTarget(string target)
        {
            return IsAny(target, DevServMessages.ServiceName, "DEVSERV");
        }

        private static bool IsHelpServTarget(string target)
        {
            return IsAny(target, HelpServMessages.ServiceName, "HELPSERV");
        }

        private static bool IsRootServTarget(string target)
        {
            return IsAny(target, RootServMessages.ServiceName, "RS", "ROOTSERV");
        }

        private static bool IsHostServTarget(string target)
        {
            return IsAny(target, HostServMessages.ServiceName, "HS", "HOSTSERV");
        }

        private static bool IsBotServTarget(string target)
        {
            return IsAny(target, BotServMessages.ServiceName, "BS", "BOTSERV");
        }

        private static bool IsAgentTarget(string target)
        {
            return IsAny(target, AgentMessages.ServiceName, "AG", "AGENT");
        }

        private static bool IsAny(string? target, params string[] names)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            var t = target.Trim().Trim('*');
            foreach (var n in names)
            {
                if (!string.IsNullOrWhiteSpace(n) && string.Equals(t, n, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
