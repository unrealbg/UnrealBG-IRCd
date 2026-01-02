namespace IRCd.Core.Protocol
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;
    using IRCd.Shared.Options;

    internal static class MotdSender
    {
        public static async ValueTask SendMotdAsync(IClientSession session, IrcOptions options, CancellationToken ct)
        {
            var serverName = "server";
            var me = session.Nick ?? "*";

            var motd = options.Motd;
            var lines = await LoadMotdLinesAsync(motd, ct);

            if (lines.Count == 0)
            {
                await session.SendAsync($":{serverName} 422 {me} :MOTD File is missing", ct);
                return;
            }

            await session.SendAsync($":{serverName} 375 {me} :- {serverName} Message of the Day -", ct);

            foreach (var line in lines)
            {
                await session.SendAsync($":{serverName} 372 {me} :- {line}", ct);
            }

            await session.SendAsync($":{serverName} 376 {me} :End of /MOTD command.", ct);
        }

        private static async Task<List<string>> LoadMotdLinesAsync(MotdOptions motd, CancellationToken ct)
        {
            if (motd.Lines is { Length: > 0 })
            {
                var list = new List<string>();
                foreach (var s in motd.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s.Trim());
                }

                return list;
            }

            if (string.IsNullOrWhiteSpace(motd.FilePath))
                return new List<string>();

            try
            {
                if (!File.Exists(motd.FilePath))
                {
                    return new List<string>();
                }

                var fileLines = await File.ReadAllLinesAsync(motd.FilePath, ct);
                var list = new List<string>();

                foreach (var s in fileLines)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s.Trim());
                    }
                }

                return list;
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
