namespace IRCd.Services.Auth
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Core.Abstractions;

    public sealed class InMemoryAuthState : IAuthState
    {
        private readonly ConcurrentDictionary<string, string> _identified =
            new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<string?> GetIdentifiedAccountAsync(string connectionId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return ValueTask.FromResult<string?>(null);
            }

            return ValueTask.FromResult<string?>(_identified.TryGetValue(connectionId, out var v) ? v : null);
        }

        public ValueTask SetIdentifiedAccountAsync(string connectionId, string? accountName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return ValueTask.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(accountName))
            {
                _identified.TryRemove(connectionId, out _);
                return ValueTask.CompletedTask;
            }

            _identified[connectionId] = accountName.Trim();
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(string connectionId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return ValueTask.CompletedTask;
            }

            _identified.TryRemove(connectionId, out _);
            return ValueTask.CompletedTask;
        }
    }
}
