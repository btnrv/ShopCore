namespace ShopCore;

internal sealed class BlackjackModuleConfig
{
    public bool UseCorePrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public List<string> StartCommands { get; set; } = [];
    public List<string> HitCommands { get; set; } = [];
    public List<string> StandCommands { get; set; } = [];
    public bool RegisterAsRawCommands { get; set; } = false;
    public string CommandPermission { get; set; } = string.Empty;
    public int MinimumBet { get; set; } = 100;
    public int MaximumBet { get; set; } = 5000;
    public int ActiveHudDurationMs { get; set; } = 12000;
    public int EndHudDurationMs { get; set; } = 3500;
    public string ServerName { get; set; } = "Your Server Name";
    public string ServerIp { get; set; } = "127.0.0.1:27015";
    public bool ShowCardImages { get; set; } = true;
    public string CardImageBaseUrl { get; set; } = "https://raw.githubusercontent.com/vulikit/varkit-resources/refs/heads/main/bj";
}
