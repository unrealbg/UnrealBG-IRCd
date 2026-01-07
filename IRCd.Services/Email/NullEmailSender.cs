namespace IRCd.Services.Email
{
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class NullEmailSender : IEmailSender
    {
        public bool IsConfigured => false;

        public ValueTask SendAsync(string toAddress, string subject, string body, CancellationToken ct)
        {
            _ = toAddress;
            _ = subject;
            _ = body;
            _ = ct;
            return ValueTask.CompletedTask;
        }
    }
}
