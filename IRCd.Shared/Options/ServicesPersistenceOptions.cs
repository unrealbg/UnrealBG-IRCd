namespace IRCd.Shared.Options
{
    public sealed class ServicesPersistenceOptions
    {
        /// <summary>
        /// Minimum interval between saves when the state is changing frequently.
        /// Set to 0 to save on every mutation.
        /// </summary>
        public int SaveIntervalSeconds { get; set; } = 0;

        /// <summary>
        /// Keep last K backups as .bak.N files next to the target file. 0 disables.
        /// </summary>
        public int BackupCount { get; set; } = 3;

        /// <summary>
        /// Best-effort recovery on startup when a leftover .tmp exists.
        /// </summary>
        public bool RecoverTmpOnStartup { get; set; } = true;

        /// <summary>
        /// Named mutex lock timeout for cross-process write exclusion.
        /// </summary>
        public int LockTimeoutMs { get; set; } = 5000;
    }
}
