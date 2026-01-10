namespace IRCd.IntegrationTests.Infrastructure
{
    using System;
    using System.IO;

    public static class RepoRoot
    {
        public static string Find()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                var marker = Path.Combine(dir.FullName, "UnrealBG-IRCd.slnx");
                if (File.Exists(marker))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Failed to locate repo root (UnrealBG-IRCd.slnx not found).");
        }
    }
}
