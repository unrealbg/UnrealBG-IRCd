namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Core.State;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class MapHandler : IIrcCommandHandler
    {
        public string Command => "MAP";

        private readonly IOptions<IrcOptions> _options;

        public MapHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, ServerState state, CancellationToken ct)
        {
            var serverName = _options.Value.ServerInfo?.Name ?? "server";

            if (!session.IsRegistered)
            {
                await session.SendAsync($":{serverName} 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick ?? "*";
            var localSid = _options.Value.ServerInfo?.Sid ?? "001";

            var remotes = state.GetRemoteServers();

            var childrenByParent = remotes
                .Where(s => !string.IsNullOrWhiteSpace(s.ParentSid) && !string.IsNullOrWhiteSpace(s.Sid))
                .GroupBy(s => s.ParentSid!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            static string Indent(int depth) => depth <= 0 ? string.Empty : new string(' ', depth * 2);

            await session.SendAsync($":{serverName} 006 {me} :{_options.Value.ServerInfo?.Name ?? "server"} [{localSid}]", ct);

            var stack = new Stack<(string ParentSid, int Depth, int Index, RemoteServer[] Children)>();
            if (childrenByParent.TryGetValue(localSid, out var rootChildren) && rootChildren.Length > 0)
            {
                stack.Push((localSid, 1, 0, rootChildren));
            }

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame.Index >= frame.Children.Length)
                {
                    continue;
                }

                var child = frame.Children[frame.Index];

                stack.Push((frame.ParentSid, frame.Depth, frame.Index + 1, frame.Children));

                var desc = string.IsNullOrWhiteSpace(child.Description) ? string.Empty : $" :{child.Description}";
                await session.SendAsync($":{serverName} 006 {me} :{Indent(frame.Depth)}{child.Name} [{child.Sid}]{desc}", ct);

                if (childrenByParent.TryGetValue(child.Sid, out var grandChildren) && grandChildren.Length > 0)
                {
                    stack.Push((child.Sid, frame.Depth + 1, 0, grandChildren));
                }
            }

            await session.SendAsync($":{serverName} 007 {me} :End of /MAP", ct);
        }
    }
}
