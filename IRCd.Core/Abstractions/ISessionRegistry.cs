namespace IRCd.Core.Abstractions
{
    using System.Collections.Generic;

    public interface ISessionRegistry
    {
        bool TryGetSession(string connectionId, out IClientSession? session);

        IReadOnlyCollection<IClientSession> GetAll();
    }
}
