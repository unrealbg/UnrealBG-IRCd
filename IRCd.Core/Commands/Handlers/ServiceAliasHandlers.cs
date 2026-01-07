namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;

    public abstract class ServiceAliasHandlerBase : IIrcCommandHandler
    {
        private readonly IServiceCommandDispatcher? _services;
        private readonly string _target;

        protected ServiceAliasHandlerBase(IServiceCommandDispatcher? services, string target)
        {
            _services = services;
            _target = target;
        }

        public abstract string Command { get; }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var text = BuildServiceText(msg);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "HELP";
            }

            if (_services is not null)
            {
                if (await _services.TryHandlePrivmsgAsync(session, _target, text, state, ct))
                {
                    return;
                }
            }

            await session.SendAsync($":server 421 {session.Nick ?? "*"} {msg.Command} :Unknown command", ct);
        }

        private static string BuildServiceText(IrcMessage msg)
        {
            var head = msg.Params is not null && msg.Params.Count > 0
                ? string.Join(' ', msg.Params.Where(p => !string.IsNullOrWhiteSpace(p)))
                : string.Empty;

            if (string.IsNullOrWhiteSpace(msg.Trailing))
            {
                return head;
            }

            if (string.IsNullOrWhiteSpace(head))
            {
                return msg.Trailing!;
            }

            return head + " " + msg.Trailing;
        }
    }

    public sealed class NsHandler : ServiceAliasHandlerBase
    {
        public NsHandler(IServiceCommandDispatcher? services) : base(services, "NS") { }
        public override string Command => "NS";
    }

    public sealed class NickServCommandHandler : ServiceAliasHandlerBase
    {
        public NickServCommandHandler(IServiceCommandDispatcher? services) : base(services, "NickServ") { }
        public override string Command => "NICKSERV";
    }

    public sealed class CsHandler : ServiceAliasHandlerBase
    {
        public CsHandler(IServiceCommandDispatcher? services) : base(services, "CS") { }
        public override string Command => "CS";
    }

    public sealed class ChanServCommandHandler : ServiceAliasHandlerBase
    {
        public ChanServCommandHandler(IServiceCommandDispatcher? services) : base(services, "ChanServ") { }
        public override string Command => "CHANSERV";
    }

    public sealed class OsHandler : ServiceAliasHandlerBase
    {
        public OsHandler(IServiceCommandDispatcher? services) : base(services, "OS") { }
        public override string Command => "OS";
    }

    public sealed class OperServCommandHandler : ServiceAliasHandlerBase
    {
        public OperServCommandHandler(IServiceCommandDispatcher? services) : base(services, "OperServ") { }
        public override string Command => "OPERSERV";
    }

    public sealed class MsHandler : ServiceAliasHandlerBase
    {
        public MsHandler(IServiceCommandDispatcher? services) : base(services, "MS") { }
        public override string Command => "MS";
    }

    public sealed class MemoServCommandHandler : ServiceAliasHandlerBase
    {
        public MemoServCommandHandler(IServiceCommandDispatcher? services) : base(services, "MemoServ") { }
        public override string Command => "MEMOSERV";
    }

    public sealed class SsHandler : ServiceAliasHandlerBase
    {
        public SsHandler(IServiceCommandDispatcher? services) : base(services, "SS") { }
        public override string Command => "SS";
    }

    public sealed class SeenServCommandHandler : ServiceAliasHandlerBase
    {
        public SeenServCommandHandler(IServiceCommandDispatcher? services) : base(services, "SeenServ") { }
        public override string Command => "SEENSERV";
    }

    public sealed class IsHandler : ServiceAliasHandlerBase
    {
        public IsHandler(IServiceCommandDispatcher? services) : base(services, "IS") { }
        public override string Command => "IS";
    }

    public sealed class InfoServCommandHandler : ServiceAliasHandlerBase
    {
        public InfoServCommandHandler(IServiceCommandDispatcher? services) : base(services, "InfoServ") { }
        public override string Command => "INFOSERV";
    }

    public sealed class StatServCommandHandler : ServiceAliasHandlerBase
    {
        public StatServCommandHandler(IServiceCommandDispatcher? services) : base(services, "StatServ") { }
        public override string Command => "STATSERV";
    }

    public sealed class AdminServCommandHandler : ServiceAliasHandlerBase
    {
        public AdminServCommandHandler(IServiceCommandDispatcher? services) : base(services, "AdminServ") { }
        public override string Command => "ADMINSERV";
    }

    public sealed class DevServCommandHandler : ServiceAliasHandlerBase
    {
        public DevServCommandHandler(IServiceCommandDispatcher? services) : base(services, "DevServ") { }
        public override string Command => "DEVSERV";
    }

    public sealed class HelpServCommandHandler : ServiceAliasHandlerBase
    {
        public HelpServCommandHandler(IServiceCommandDispatcher? services) : base(services, "HelpServ") { }
        public override string Command => "HELPSERV";
    }
}
