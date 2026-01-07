namespace IRCd.Services.Agent
{
    using System;
    using System.Collections.Generic;

    using IRCd.Shared.Options;

    public sealed class AgentStateService
    {
        private readonly object _gate = new();
        private string? _logonMessage;
        private readonly HashSet<string> _badwords = new(StringComparer.OrdinalIgnoreCase);

        public AgentStateService(IrcOptions options)
        {
            ReloadFrom(options);
        }

        public void ReloadFrom(IrcOptions options)
        {
            if (options is null)
            {
                return;
            }

            lock (_gate)
            {
                _logonMessage = options.Services?.Agent?.LogonMessage;
                _badwords.Clear();
                var words = options.Services?.Agent?.Badwords ?? Array.Empty<string>();
                foreach (var w in words)
                {
                    if (!string.IsNullOrWhiteSpace(w))
                    {
                        _badwords.Add(w.Trim());
                    }
                }
            }
        }

        public string? GetLogonMessage()
        {
            lock (_gate)
            {
                return _logonMessage;
            }
        }

        public void SetLogonMessage(string? message)
        {
            lock (_gate)
            {
                _logonMessage = string.IsNullOrWhiteSpace(message) ? null : message;
            }
        }

        public IReadOnlyCollection<string> GetBadwords()
        {
            lock (_gate)
            {
                return new List<string>(_badwords);
            }
        }

        public bool AddBadword(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                return false;
            }

            lock (_gate)
            {
                return _badwords.Add(word.Trim());
            }
        }

        public bool RemoveBadword(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                return false;
            }

            lock (_gate)
            {
                return _badwords.Remove(word.Trim());
            }
        }

        public void ClearBadwords()
        {
            lock (_gate)
            {
                _badwords.Clear();
            }
        }
    }
}
