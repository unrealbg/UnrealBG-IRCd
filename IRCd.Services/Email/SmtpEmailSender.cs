namespace IRCd.Services.Email
{
    using System;
    using System.Net;
    using System.Net.Mail;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Shared.Options;

    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly IOptions<IrcOptions> _options;
        private readonly ILogger<SmtpEmailSender>? _logger;

        public SmtpEmailSender(IOptions<IrcOptions> options, ILogger<SmtpEmailSender>? logger = null)
        {
            _options = options;
            _logger = logger;
        }

        public bool IsConfigured
        {
            get
            {
                var smtp = _options.Value.Services?.NickServ?.Smtp;
                if (smtp is null)
                    return false;

                return !string.IsNullOrWhiteSpace(smtp.Host)
                    && !string.IsNullOrWhiteSpace(smtp.FromAddress);
            }
        }

        public async ValueTask SendAsync(string toAddress, string subject, string body, CancellationToken ct)
        {
            var smtp = _options.Value.Services?.NickServ?.Smtp;
            if (smtp is null)
                throw new InvalidOperationException("SMTP options not configured.");

            if (string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.FromAddress))
                throw new InvalidOperationException("SMTP host/from not configured.");

            using var msg = new MailMessage();
            msg.To.Add(new MailAddress(toAddress));
            msg.From = string.IsNullOrWhiteSpace(smtp.FromName)
                ? new MailAddress(smtp.FromAddress)
                : new MailAddress(smtp.FromAddress, smtp.FromName);
            msg.Subject = subject;
            msg.Body = body;

            using var client = new SmtpClient(smtp.Host, smtp.Port);
            client.EnableSsl = smtp.UseSsl;

            if (!string.IsNullOrWhiteSpace(smtp.Username))
            {
                client.Credentials = new NetworkCredential(smtp.Username, smtp.Password ?? string.Empty);
            }

            try
            {
#if NET8_0_OR_GREATER
                await client.SendMailAsync(msg, ct);
#else
                ct.ThrowIfCancellationRequested();
                await client.SendMailAsync(msg);
#endif
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SMTP send failed (to={To})", toAddress);
                throw;
            }
        }
    }
}
