namespace IRCd.Core.Protocol
{
    using System;

    using Microsoft.Extensions.Logging;

    public static class SafeIrcLogger
    {
        public static void LogBadInboundLine(ILogger logger, IIrcLogRedactor redactor, string connectionId, string line, Exception ex)
        {
            var safe = redactor.RedactInboundLine(line);
            logger.LogDebug("Bad line from {ConnId}: {Line} ({ExceptionType})", connectionId, safe, ex.GetType().Name);
        }

        public static void LogDispatchException(ILogger logger, string connectionId, string command, Exception ex)
        {
            logger.LogError(
                "FATAL: DispatchAsync threw exception for {ConnId} command {Command} ({ExceptionType})",
                connectionId,
                command,
                ex.GetType().Name);
        }

        public static void LogClientLoopError(ILogger logger, string connectionId, Exception ex, bool tls)
        {
            if (tls)
            {
                logger.LogWarning("TLS client loop error {ConnId} ({ExceptionType})", connectionId, ex.GetType().Name);
            }
            else
            {
                logger.LogWarning("Client loop error {ConnId} ({ExceptionType})", connectionId, ex.GetType().Name);
            }
        }
    }
}
