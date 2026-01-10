namespace IRCd.Tests
{
    using System;
    using System.Collections.Generic;

    using IRCd.Core.Protocol;

    using Microsoft.Extensions.Logging;

    using Xunit;

    public sealed class SecretRedactionLoggingTests
    {
        [Fact]
        public void SafeIrcLogger_BadInboundLine_DoesNotLogSecretsOrException()
        {
            var logger = new CapturingLogger();
            var redactor = new DefaultIrcLogRedactor();

            var line = "PASS hunter2";
            var ex = new InvalidOperationException("parser failed for PASS hunter2");

            SafeIrcLogger.LogBadInboundLine(logger, redactor, "c1", line, ex);

            Assert.Single(logger.Entries);
            Assert.DoesNotContain("hunter2", logger.Entries[0].Message, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", logger.Entries[0].Message, StringComparison.Ordinal);
            Assert.Null(logger.Entries[0].Exception);
        }

        [Fact]
        public void SafeIrcLogger_DispatchException_DoesNotLogSecretsOrException()
        {
            var logger = new CapturingLogger();
            var ex = new Exception("boom AUTHENTICATE dGVzdA==");

            SafeIrcLogger.LogDispatchException(logger, "c1", "AUTHENTICATE", ex);

            Assert.Single(logger.Entries);
            Assert.DoesNotContain("dGVzdA==", logger.Entries[0].Message, StringComparison.Ordinal);
            Assert.Null(logger.Entries[0].Exception);
        }

        private sealed class CapturingLogger : ILogger
        {
            public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

            public List<Entry> Entries { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose() { }
            }
        }
    }
}
