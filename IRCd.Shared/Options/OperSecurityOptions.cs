namespace IRCd.Shared.Options
{
    public sealed class OperSecurityOptions
    {
        /// <summary>
        /// When true, oper passwords must be hashed (plaintext values are rejected).
        /// </summary>
        public bool RequireHashedPasswords { get; set; } = false;
    }
}
