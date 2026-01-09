namespace IRCd.Core.Abstractions;

using IRCd.Shared.Options;

public interface IConfigReloadListener
{
    void OnConfigReloaded(IrcOptions oldConfig, IrcOptions newConfig);
}
