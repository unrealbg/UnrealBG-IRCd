namespace IRCd.Services.Email
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface IEmailSender
    {
        ValueTask SendAsync(string toAddress, string subject, string body, CancellationToken ct);

        bool IsConfigured { get; }
    }
}
