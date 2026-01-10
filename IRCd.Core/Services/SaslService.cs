namespace IRCd.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Text;

    public sealed class SaslService
    {
        public const int SaslChunkSize = 400;
        public const int MaxPayloadChars = 8192;

        private readonly ConcurrentDictionary<string, SaslState> _stateByConn = new(StringComparer.Ordinal);

        public SaslState GetOrCreate(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                throw new ArgumentException("connectionId is required", nameof(connectionId));
            }

            return _stateByConn.GetOrAdd(connectionId, static _ => new SaslState());
        }

        public bool TryGet(string connectionId, out SaslState state)
        {
            state = default!;
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return false;
            }

            return _stateByConn.TryGetValue(connectionId, out state!);
        }

        public void Clear(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return;
            }

            _stateByConn.TryRemove(connectionId, out _);
        }

        public sealed class SaslState
        {
            public bool InProgress { get; private set; }
            public bool Completed { get; private set; }
            public string? Mechanism { get; private set; }

            private readonly StringBuilder _buffer = new();

            public void Start(string mechanism)
            {
                if (string.IsNullOrWhiteSpace(mechanism))
                {
                    throw new ArgumentException("mechanism is required", nameof(mechanism));
                }

                InProgress = true;
                Completed = false;
                Mechanism = mechanism;
                _buffer.Clear();
            }

            public void Abort()
            {
                InProgress = false;
                Mechanism = null;
                _buffer.Clear();
            }

            public void MarkCompleted()
            {
                Completed = true;
                InProgress = false;
                Mechanism = null;
                _buffer.Clear();
            }

            public bool TryAppendChunk(string chunk, out string? assembledBase64, out string? error)
            {
                assembledBase64 = null;
                error = null;

                if (!InProgress)
                {
                    error = "SASL not in progress";
                    return false;
                }

                if (chunk is null)
                {
                    chunk = string.Empty;
                }

                var isTerminator = chunk == "+" || chunk.Length < SaslChunkSize;

                if (chunk != "+")
                {
                    _buffer.Append(chunk);
                }

                if (_buffer.Length > MaxPayloadChars)
                {
                    error = "SASL payload too long";
                    return false;
                }

                if (!isTerminator)
                {
                    return true;
                }

                assembledBase64 = _buffer.ToString();
                return true;
            }
        }
    }
}
