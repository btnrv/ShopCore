using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Rewards",
    Name = "Shop Rewards",
    Author = "T3Marius",
    Version = "1.0.1",
    Description = "ShopCore module with rewards system"
)]
public class Shop_Rewards : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Rewards";
    private const string TemplateFileName = "rewards_config.jsonc";
    private const string TemplateSectionName = "Main";
    private RewardsModuleConfig config = new();
    private int? lastRoundWinnerTeam;
    private IShopCoreApiV2? shopApi;
    private readonly Dictionary<int, int> trackedPlaytimeMinutes = new();
    private readonly Dictionary<int, int> trackedNameBonusMinutes = new();
    private readonly Dictionary<ulong, PendingDeathRewardBucket> pendingDeathRewards = new();
    private float nextPlaytimeMinuteTickAt = -1.0f;

    public Shop_Rewards(ISwiftlyCore core) : base(core)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;

        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            return;
        }

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogInformation(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }

        TryLoadConfig();
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnTick += OnTick;
        TryLoadConfig();
    }

    private void TryLoadConfig()
    {
        if (shopApi is null)
        {
            return;
        }

        config = shopApi.LoadModuleConfig<RewardsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig();

        lastRoundWinnerTeam = null;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnMatchEnd(EventCsWinPanelMatch e)
    {
        if (shopApi is null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        if (config.MatchWon <= 0 || !TryResolveWinnerTeam(lastRoundWinnerTeam, out var winnerTeam))
        {
            return HookResult.Continue;
        }

        foreach (var player in GetRewardablePlayersOnTeam(winnerTeam))
        {
            shopApi.AddCredits(player, config.MatchWon);
            SendRewardMessage(player, "reward.match_won", config.MatchWon);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart e)
    {
        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            foreach (var player in onlinePlayers)
            {
                var loc = Core.Translation.GetPlayerLocalizer(player);
                var prefix = loc["shop.prefix"];
                if (config.UseCorePrefix)
                {
                    var corePrefix = shopApi?.GetShopPrefix(player);
                    if (!string.IsNullOrWhiteSpace(corePrefix))
                    {
                        prefix = corePrefix;
                    }
                }
                player.SendChat($"{prefix} + {loc["module.disabled.warmup"]}");
            }
            return HookResult.Continue;
        }
        if (onlinePlayers.Count < config.MinPlayers)
        {
            foreach (var player in onlinePlayers)
            {
                var loc = Core.Translation.GetPlayerLocalizer(player);
                var prefix = loc["shop.prefix"];
                if (config.UseCorePrefix)
                {
                    var corePrefix = shopApi?.GetShopPrefix(player);
                    if (!string.IsNullOrWhiteSpace(corePrefix))
                    {
                        prefix = corePrefix;
                    }
                }

                player.SendChat($"{prefix} + {loc["module.disabled", config.MinPlayers]}");
            }
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnRoundMvp(EventRoundMvp e)
    {
        if (e.UserIdPlayer is not IPlayer player)
        {
            return HookResult.Continue;
        }

        if (shopApi is null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        if (config.MVP > 0 && IsRewardablePlayer(player))
        {
            shopApi.AddCredits(player, config.MVP);
            SendRewardMessage(player, "reward.mvp", config.MVP);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnRoundEnd(EventRoundEnd e)
    {
        lastRoundWinnerTeam = e.Winner;

        if (shopApi is null)
        {
            return HookResult.Continue;
        }

        FlushPendingDeathRewards();

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return HookResult.Continue;
        }

        if (config.RoundWon <= 0 || !TryResolveWinnerTeam(e.Winner, out var winnerTeam))
        {
            return HookResult.Continue;
        }

        foreach (var player in GetRewardablePlayersOnTeam(winnerTeam))
        {
            shopApi.AddCredits(player, config.RoundWon);
            SendRewardMessage(player, "reward.round_won", config.RoundWon);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        if (shopApi is null)
        {
            return HookResult.Continue;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return HookResult.Continue;
        }

        if (config.Kill <= 0 && config.Headshot <= 0 && config.Assist <= 0)
        {
            return HookResult.Continue;
        }

        if (!HasMinimumOnlinePlayers(config.MinPlayers))
        {
            return HookResult.Continue;
        }

        var victimPlayerId = e.UserIdPlayer?.PlayerID;
        if (e.AttackerPlayer is IPlayer attacker &&
            IsRewardablePlayer(attacker) &&
            (!victimPlayerId.HasValue || attacker.PlayerID != victimPlayerId.Value))
        {
            QueueDeathReward(attacker, killCredits: config.Kill > 0 ? config.Kill : 0, headshotCredits: e.Headshot && config.Headshot > 0 ? config.Headshot : 0);
        }

        if (config.Assist > 0 &&
            e.AssisterPlayer is IPlayer assister &&
            IsRewardablePlayer(assister) &&
            (!victimPlayerId.HasValue || assister.PlayerID != victimPlayerId.Value))
        {
            QueueDeathReward(assister, assistCredits: config.Assist);
        }

        return HookResult.Continue;
    }

    public override void Unload()
    {
        Core.Event.OnTick -= OnTick;
        lastRoundWinnerTeam = null;
        trackedPlaytimeMinutes.Clear();
        trackedNameBonusMinutes.Clear();
        pendingDeathRewards.Clear();
        nextPlaytimeMinuteTickAt = -1.0f;
    }

    private void OnTick()
    {
        var currentTime = Core.Engine.GlobalVars.CurrentTime;
        if (currentTime <= 0.0f)
        {
            return;
        }

        if (nextPlaytimeMinuteTickAt < 0.0f || currentTime + 1.0f < nextPlaytimeMinuteTickAt)
        {
            nextPlaytimeMinuteTickAt = currentTime + 60.0f;
            return;
        }

        if (currentTime < nextPlaytimeMinuteTickAt)
        {
            return;
        }

        while (currentTime >= nextPlaytimeMinuteTickAt)
        {
            nextPlaytimeMinuteTickAt += 60.0f;
            RewardTimedCredits();
        }
    }

    private void RewardTimedCredits()
    {
        if (shopApi is null)
        {
            return;
        }

        var playtimeEnabled = config.PlaytimeCredits > 0 && config.PlaytimeMinutes > 0;
        var nameBonusEnabled = config.NameBonusCredits > 0 &&
            config.NameBonusMinutes > 0 &&
            config.NameBonusTexts.Count > 0;

        if (!playtimeEnabled && !nameBonusEnabled)
        {
            return;
        }

        if (IsWarmupPeriod() && config.DisableInWarmup)
        {
            return;
        }

        List<IPlayer> onlinePlayers = Core.PlayerManager.GetAllValidPlayers().ToList();
        if (onlinePlayers.Count < config.MinPlayers)
        {
            return;
        }

        var activePlayerIds = new HashSet<int>();
        foreach (var player in onlinePlayers.Where(IsRewardablePlayer))
        {
            activePlayerIds.Add(player.PlayerID);

            if (playtimeEnabled)
            {
                trackedPlaytimeMinutes.TryGetValue(player.PlayerID, out var playedMinutes);
                playedMinutes++;

                if (playedMinutes >= config.PlaytimeMinutes)
                {
                    shopApi.AddCredits(player, config.PlaytimeCredits);
                    SendRewardMessage(player, "reward.playtime", config.PlaytimeCredits);
                    playedMinutes = 0;
                }

                trackedPlaytimeMinutes[player.PlayerID] = playedMinutes;
            }

            if (nameBonusEnabled)
            {
                trackedNameBonusMinutes.TryGetValue(player.PlayerID, out var nameBonusMinutes);
                nameBonusMinutes++;

                if (nameBonusMinutes >= config.NameBonusMinutes)
                {
                    if (TryGetMatchedNameBonusText(player, out var matchedText))
                    {
                        shopApi.AddCredits(player, config.NameBonusCredits);
                        SendNameBonusMessage(player, matchedText, config.NameBonusCredits);
                    }

                    nameBonusMinutes = 0;
                }

                trackedNameBonusMinutes[player.PlayerID] = nameBonusMinutes;
            }
        }

        foreach (var playerId in trackedPlaytimeMinutes.Keys.Except(activePlayerIds).ToList())
        {
            trackedPlaytimeMinutes.Remove(playerId);
        }

        foreach (var playerId in trackedNameBonusMinutes.Keys.Except(activePlayerIds).ToList())
        {
            trackedNameBonusMinutes.Remove(playerId);
        }
    }

    private IEnumerable<IPlayer> GetRewardablePlayersOnTeam(Team team)
    {
        return Core.PlayerManager
            .GetAllValidPlayers()
            .Where(p => !p.IsFakeClient && p.Controller.Team == team);
    }

    private void QueueDeathReward(IPlayer player, int killCredits = 0, int headshotCredits = 0, int assistCredits = 0)
    {
        if ((killCredits | headshotCredits | assistCredits) <= 0 || !player.IsValid)
        {
            return;
        }

        if (!pendingDeathRewards.TryGetValue(player.SteamID, out var bucket))
        {
            bucket = new PendingDeathRewardBucket(player);
        }

        bucket.KillCredits += killCredits;
        bucket.HeadshotCredits += headshotCredits;
        bucket.AssistCredits += assistCredits;
        pendingDeathRewards[player.SteamID] = bucket;
    }

    private void FlushPendingDeathRewards()
    {
        if (shopApi is null || pendingDeathRewards.Count == 0)
        {
            return;
        }

        foreach (var (steamId, bucket) in pendingDeathRewards.ToList())
        {
            var player = bucket.Player;
            if (!IsRewardablePlayer(player))
            {
                pendingDeathRewards.Remove(steamId);
                continue;
            }

            var totalCredits = bucket.KillCredits + bucket.HeadshotCredits + bucket.AssistCredits;
            if (totalCredits <= 0)
            {
                pendingDeathRewards.Remove(steamId);
                continue;
            }

            if (!shopApi.AddCredits(player, totalCredits))
            {
                Core.Logger.LogWarning(
                    "Failed to flush pending death rewards for player {PlayerName} ({SteamId}). Total={Total}",
                    player.Controller.PlayerName,
                    player.SteamID,
                    totalCredits
                );
                pendingDeathRewards.Remove(steamId);
                continue;
            }

            if (bucket.KillCredits > 0)
            {
                SendRewardMessage(player, "reward.kill", bucket.KillCredits);
            }

            if (bucket.HeadshotCredits > 0)
            {
                SendRewardMessage(player, "reward.headshot", bucket.HeadshotCredits);
            }

            if (bucket.AssistCredits > 0)
            {
                SendRewardMessage(player, "reward.assist", bucket.AssistCredits);
            }

            pendingDeathRewards.Remove(steamId);
        }
    }

    private static bool IsRewardablePlayer(IPlayer player)
    {
        return player.IsValid && !player.IsFakeClient;
    }

    private bool HasMinimumOnlinePlayers(int minimum)
    {
        if (minimum <= 0)
        {
            return true;
        }

        var count = 0;
        foreach (var _ in Core.PlayerManager.GetAllValidPlayers())
        {
            count++;
            if (count >= minimum)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveWinnerTeam(int? rawWinner, out Team winnerTeam)
    {
        winnerTeam = default;
        if (!rawWinner.HasValue)
        {
            return false;
        }

        var value = rawWinner.Value;
        if (value <= 1)
        {
            return false;
        }

        winnerTeam = (Team)value;
        return true;
    }

    private void SendRewardMessage(IPlayer player, string key, int rewardConfig)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        var prefix = loc["shop.prefix"];
        if (config.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                prefix = corePrefix;
            }
        }

        player.SendChat($"{prefix} {loc[key, rewardConfig]}");
    }

    private void SendNameBonusMessage(IPlayer player, string matchedText, int rewardConfig)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        var prefix = loc["shop.prefix"];
        if (config.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                prefix = corePrefix;
            }
        }

        player.SendChat($"{prefix} {loc["reward.name_bonus", rewardConfig, matchedText]}");
    }

    private bool TryGetMatchedNameBonusText(IPlayer player, out string matchedText)
    {
        var playerName = player.Controller.PlayerName ?? string.Empty;
        var comparison = config.NameBonusCaseInsensitiveMatch
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var adText in config.NameBonusTexts)
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

    private void NormalizeConfig()
    {
        config.MinPlayers = Math.Max(0, config.MinPlayers);
        config.PlaytimeCredits = Math.Max(0, config.PlaytimeCredits);
        config.PlaytimeMinutes = Math.Max(0, config.PlaytimeMinutes);
        config.NameBonusCredits = Math.Max(0, config.NameBonusCredits);
        config.NameBonusMinutes = Math.Max(0, config.NameBonusMinutes);

        config.NameBonusTexts = (config.NameBonusTexts ?? [])
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsWarmupPeriod()
    {
        return Core.EntitySystem.GetGameRules()?.WarmupPeriod ?? false;
    }
}

internal sealed class PendingDeathRewardBucket(IPlayer player)
{
    public IPlayer Player { get; } = player;
    public int KillCredits { get; set; }
    public int HeadshotCredits { get; set; }
    public int AssistCredits { get; set; }
}

internal sealed class RewardsModuleConfig
{
    public int MinPlayers { get; set; } = 4;
    public bool DisableInWarmup { get; set; } = true;
    public bool UseCorePrefix { get; set; } = true;
    public int Kill { get; set; } = 2;
    public int Headshot { get; set; } = 5;
    public int Assist { get; set; } = 1;
    public int RoundWon { get; set; } = 5;
    public int MatchWon { get; set; } = 10;
    public int MVP { get; set; } = 15;
    public int PlaytimeCredits { get; set; } = 100;
    public int PlaytimeMinutes { get; set; } = 5;
    public int NameBonusCredits { get; set; } = 100;
    public int NameBonusMinutes { get; set; } = 5;
    public bool NameBonusCaseInsensitiveMatch { get; set; } = true;
    public List<string> NameBonusTexts { get; set; } = ["YourAd1", "YourAd2"];
}
