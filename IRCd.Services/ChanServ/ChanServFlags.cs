namespace IRCd.Services.ChanServ
{
    using System;

    [Flags]
    public enum ChanServFlags
    {
        None = 0,

        Founder = 1 << 0,
        Flags = 1 << 1,
        Op = 1 << 2,
        Voice = 1 << 3,
        Invite = 1 << 4,
        Kick = 1 << 5,
        Ban = 1 << 6,

        All = Founder | Flags | Op | Voice | Invite | Kick | Ban
    }
}
