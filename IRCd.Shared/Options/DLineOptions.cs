namespace IRCd.Shared.Options
{
    public sealed class DLineOptions
    {
        /// <summary>
        /// IP mask, e.g. "203.0.113.10" or "203.0.113.*".
        /// </summary>
        public string Mask { get; set; } = string.Empty;

        public string Reason { get; set; } = "Banned";
    }
}
