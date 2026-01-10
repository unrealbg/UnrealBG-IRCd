namespace IRCd.Tests.TestDoubles
{
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    using IRCd.Services.Email;

    public sealed class FakeEmailSender : IEmailSender
    {
        public sealed record SentEmail(string To, string Subject, string Body);

        private readonly ConcurrentQueue<SentEmail> _sent = new();

        public bool IsConfigured => true;

        public ValueTask SendAsync(string toAddress, string subject, string body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _sent.Enqueue(new SentEmail(toAddress, subject, body));
            return ValueTask.CompletedTask;
        }

        public bool TryDequeue(out SentEmail email)
        {
            if (_sent.TryDequeue(out var e))
            {
                email = e;
                return true;
            }

            email = default!;
            return false;
        }
    }
}
