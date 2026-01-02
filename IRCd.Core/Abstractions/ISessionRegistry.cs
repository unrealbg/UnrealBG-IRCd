namespace IRCd.Core.Abstractions
{
    using System.Collections.Generic;

    public interface ISessionRegistry
    {
        void Add(IClientSession session);

        void Remove(string connectionId);

        bool TryGet(string connectionId, out IClientSession? session);

        IEnumerable<IClientSession> All();
    }
}
