namespace IRCd.Shared.Options
{
    public sealed class IsupportOptions
    {
        public string Prefix { get; set; } = "(ov)@+";

        public string StatusMsg { get; set; } = "@+";

        public string ChanTypes { get; set; } = "#";

        public string ChanModes { get; set; } = "b,k,l,imnpst";

        public string CaseMapping { get; set; } = "rfc1459";

        public int NickLen { get; set; } = 20;

        public int ChanLen { get; set; } = 50;

        public int TopicLen { get; set; } = 300;

        public int MaxModes { get; set; } = 12;

        public int AwayLen { get; set; } = 200;

        public string EList { get; set; } = "MNU";

        public int KickLen { get; set; } = 160;
    }
}
