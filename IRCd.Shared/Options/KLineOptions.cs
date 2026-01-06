namespace IRCd.Shared.Options
{
    public sealed class KLineOptions
    {
        /// <summary>
        /// User@host mask, e.g. "*!*@bad.example" or "nick!user@host".
        /// </summary>
        public string Mask { get; set; } = string.Empty;

        public string Reason { get; set; } = "Banned";
    }
}
