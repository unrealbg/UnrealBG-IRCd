namespace IRCd.Core.Commands.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Core.Commands.Contracts;
    using IRCd.Core.Protocol;
    using IRCd.Shared.Options;

    using Microsoft.Extensions.Options;

    public sealed class MotdHandler : IIrcCommandHandler
    {
        public string Command => "MOTD";

        private readonly IOptions<IrcOptions> _options;

        public MotdHandler(IOptions<IrcOptions> options)
        {
            _options = options;
        }

        public async ValueTask HandleAsync(IClientSession session, IrcMessage msg, State.ServerState state, CancellationToken ct)
        {
            if (!session.IsRegistered)
            {
                await session.SendAsync($":server 451 {(session.Nick ?? "*")} :You have not registered", ct);
                return;
            }

            var me = session.Nick!;
            var motd = _options.Value.Motd;

            var lines = await LoadMotdLinesAsync(motd, ct);

            if (lines.Count == 0)
            {
                await session.SendAsync($":server 422 {me} :MOTD File is missing", ct);
                return;
            }

            await session.SendAsync($":server 375 {me} :- server Message of the Day -", ct);

            foreach (var line in lines)
            {
                await session.SendAsync($":server 372 {me} :- {line}", ct);
            }

            await session.SendAsync($":server 376 {me} :End of /MOTD command.", ct);
        }

        private static async Task<List<string>> LoadMotdLinesAsync(MotdOptions motd, CancellationToken ct)
        {
            if (motd.Lines is { Length: > 0 })
            {
                return motd.Lines
                    .WhereNotNullOrWhiteSpace()
                    .ToList();
            }

            if (string.IsNullOrWhiteSpace(motd.FilePath))
            {
                return new List<string>();
            }

            try
            {
                if (!File.Exists(motd.FilePath))
                {
                    return new List<string>();
                }

                var fileLines = await File.ReadAllLinesAsync(motd.FilePath, ct);

                return fileLines
                    .WhereNotNullOrWhiteSpace()
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }

    internal static class MotdLineExtensions
    {
        public static IEnumerable<string> WhereNotNullOrWhiteSpace(this IEnumerable<string?> source)
        {
            foreach (var s in source)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    yield return s!;
                }
            }
        }
    }
}
