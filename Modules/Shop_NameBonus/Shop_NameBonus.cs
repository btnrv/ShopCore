using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_NameBonus",
    Name = "Shop Name Bonus",
    Author = "Codex",
    Version = "1.0.0",
    Description = "ShopCore module that rewards credits for nickname advertisements"
)]
public class Shop_NameBonus : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_NameBonus";
    private const string TemplateFileName = "namebonus_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string FallbackShopPrefix = "[gold]★[red] [Store][default]";

    private readonly Dictionary<ulong, DateTimeOffset> nextEligibleAwardAtBySteam = new();
    private CancellationTokenSource? awardTimerToken;
    private CancellationTokenSource? adMessageTimerToken;
    private IShopCoreApiV2? shopApi;
    private NameBonusModuleConfig config = new();

    public Shop_NameBonus(ISwiftlyCore core) : base(core)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;

        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            StopTimers();
            return;
        }

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            StopTimers();
            Core.Logger.LogWarning(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Name bonus timers will not start.");
            return;
        }

        LoadConfigAndRestartTimers();
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null)
        {
            LoadConfigAndRestartTimers();
        }
    }

    public override void Unload()
    {
        StopTimers();
        nextEligibleAwardAtBySteam.Clear();
    }

    private void LoadConfigAndRestartTimers()
    {
        if (shopApi is null)
        {
            return;
        }

        var loadedConfig = shopApi.LoadModuleConfig<NameBonusModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );

        config = NormalizeConfig(loadedConfig);
        RestartTimers();

        Core.Logger.LogInformation(
            "Shop_NameBonus initialized. Enabled={Enabled}, AdTexts={AdTextCount}, BonusCredits={BonusCredits}, Interval={IntervalSeconds}s, AdMessage={ShowAdMessage}",
            config.Enabled,
            config.AdTexts.Count,
            config.BonusCredits,
            config.IntervalSeconds,
            config.ShowAdMessage
        );
    }

    private void RestartTimers()
    {
        StopTimers();

        if (shopApi is null || !config.Enabled)
        {
            return;
        }

        if (config.IntervalSeconds > 0)
        {
            awardTimerToken = Core.Scheduler.DelayAndRepeatBySeconds(
                config.IntervalSeconds,
                config.IntervalSeconds,
                AwardEligiblePlayers
            );
        }

        if (config.ShowAdMessage && config.AdMessageDelaySeconds > 0)
        {
            adMessageTimerToken = Core.Scheduler.DelayAndRepeatBySeconds(
                config.AdMessageDelaySeconds,
                config.AdMessageDelaySeconds,
                BroadcastAdMessage
            );
        }
    }

    private void StopTimers()
    {
        CancelTimer(ref awardTimerToken);
        CancelTimer(ref adMessageTimerToken);
    }

    private static void CancelTimer(ref CancellationTokenSource? token)
    {
        if (token is null)
        {
            return;
        }

        try
        {
            if (!token.IsCancellationRequested)
            {
                token.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        token.Dispose();
        token = null;
    }

    private void AwardEligiblePlayers()
    {
        if (shopApi is null || !config.Enabled)
        {
            return;
        }

        if (config.BonusCredits <= 0 || config.AdTexts.Count == 0)
        {
            return;
        }

        if (config.DisableInWarmup && IsWarmupPeriod())
        {
            return;
        }

        var players = GetEligibleHumanPlayers().ToList();
        if (players.Count < config.MinPlayers)
        {
            return;
        }

        CleanupCooldownState(players);

        var now = DateTimeOffset.UtcNow;
        var nextWindow = now.AddSeconds(Math.Max(1, config.IntervalSeconds));

        foreach (var player in players)
        {
            var playerName = player.Controller.PlayerName;
            if (!TryGetMatchedAdText(playerName, out var matchedText))
            {
                continue;
            }

            if (!CanReceiveAward(player, now))
            {
                continue;
            }

            if (!shopApi.AddCredits(player, config.BonusCredits))
            {
                Core.Logger.LogWarning(
                    "Failed to add name bonus credits for player {PlayerName} ({SteamID}).",
                    playerName,
                    player.SteamID
                );
                continue;
            }

            nextEligibleAwardAtBySteam[player.SteamID] = nextWindow;
            SendRewardMessage(player, matchedText, config.BonusCredits);
        }
    }

    private void BroadcastAdMessage()
    {
        if (!config.Enabled || !config.ShowAdMessage)
        {
            return;
        }

        var message = config.AdMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        foreach (var player in GetEligibleHumanPlayers())
        {
            if (config.ShowAdMessageToNonAdvertisersOnly &&
                TryGetMatchedAdText(player.Controller.PlayerName, out _))
            {
                continue;
            }

            player.SendChat($"{ResolvePrefix(player)} {message}");
        }
    }

    private IEnumerable<IPlayer> GetEligibleHumanPlayers()
    {
        var players = Core.PlayerManager.GetAllValidPlayers()
            .Where(player => !player.IsFakeClient)
            .Where(player => player.SteamID != 0);

        if (config.RequireAuthorizedPlayers)
        {
            players = players.Where(player => player.IsAuthorized);
        }

        if (config.RequireActiveTeam)
        {
            players = players.Where(player =>
                player.Controller.TeamNum == (int)Team.T ||
                player.Controller.TeamNum == (int)Team.CT);
        }

        return players;
    }

    private void CleanupCooldownState(IReadOnlyCollection<IPlayer> onlinePlayers)
    {
        if (nextEligibleAwardAtBySteam.Count == 0)
        {
            return;
        }

        var onlineSteamIds = onlinePlayers.Select(player => player.SteamID).ToHashSet();
        if (onlineSteamIds.Count == 0)
        {
            nextEligibleAwardAtBySteam.Clear();
            return;
        }

        foreach (var steamId in nextEligibleAwardAtBySteam.Keys.ToList())
        {
            if (!onlineSteamIds.Contains(steamId))
            {
                nextEligibleAwardAtBySteam.Remove(steamId);
            }
        }
    }

    private bool CanReceiveAward(IPlayer player, DateTimeOffset now)
    {
        if (!nextEligibleAwardAtBySteam.TryGetValue(player.SteamID, out var nextEligibleAt))
        {
            return true;
        }

        return now >= nextEligibleAt;
    }

    private bool TryGetMatchedAdText(string playerName, out string matchedText)
    {
        var comparison = config.CaseInsensitiveMatch
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var adText in config.AdTexts)
        {
            if (playerName.Contains(adText, comparison))
            {
                matchedText = adText;
                return true;
            }
        }

        matchedText = string.Empty;
        return false;
    }

    private void SendRewardMessage(IPlayer player, string adText, int credits)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        player.SendChat($"{ResolvePrefix(player)} {loc["namebonus.rewarded", credits, adText]}");
    }

    private string ResolvePrefix(IPlayer player)
    {
        if (config.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        var loc = Core.Translation.GetPlayerLocalizer(player);
        var modulePrefix = loc["shop.prefix"];
        if (string.IsNullOrWhiteSpace(modulePrefix) || string.Equals(modulePrefix, "shop.prefix", StringComparison.Ordinal))
        {
            return FallbackShopPrefix;
        }

        return modulePrefix;
    }

    private bool IsWarmupPeriod()
    {
        return Core.EntitySystem.GetGameRules()?.WarmupPeriod ?? false;
    }

    private static NameBonusModuleConfig NormalizeConfig(NameBonusModuleConfig? source)
    {
        var normalized = source ?? new NameBonusModuleConfig();

        normalized.BonusCredits = Math.Max(0, normalized.BonusCredits);
        normalized.IntervalSeconds = Math.Max(1, normalized.IntervalSeconds);
        normalized.AdMessageDelaySeconds = Math.Max(1, normalized.AdMessageDelaySeconds);
        normalized.MinPlayers = Math.Max(0, normalized.MinPlayers);
        normalized.AdMessage ??= string.Empty;

        normalized.AdTexts = (normalized.AdTexts ?? [])
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized;
    }
}

internal sealed class NameBonusModuleConfig
{
    public bool Enabled { get; set; } = true;
    public bool UseCorePrefix { get; set; } = true;
    public List<string> AdTexts { get; set; } = ["YourAd1", "YourAd2"];
    public int BonusCredits { get; set; } = 100;
    public int IntervalSeconds { get; set; } = 300;
    public bool ShowAdMessage { get; set; } = true;
    public int AdMessageDelaySeconds { get; set; } = 120;
    public string AdMessage { get; set; } = "Add '[blue]YourAd[default]' to your nickname and earn bonus credits!";
    public bool ShowAdMessageToNonAdvertisersOnly { get; set; } = true;
    public bool CaseInsensitiveMatch { get; set; } = true;
    public bool DisableInWarmup { get; set; } = true;
    public int MinPlayers { get; set; } = 1;
    public bool RequireActiveTeam { get; set; } = true;
    public bool RequireAuthorizedPlayers { get; set; } = true;
}
