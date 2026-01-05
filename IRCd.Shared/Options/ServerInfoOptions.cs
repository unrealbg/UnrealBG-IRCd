namespace IRCd.Shared.Options
{
    public sealed class ServerInfoOptions
    {
        public string Name { get; set; } = "server";

        public string Sid { get; set; } = "001";

        public string Version { get; set; } = "UnrealBG-IRCd";

        public string Description { get; set; } = "IRCd";

        public string Network { get; set; } = "local";

        public string? AdminLocation1 { get; set; }

        public string? AdminLocation2 { get; set; }

        public string? AdminEmail { get; set; }
    }
}
