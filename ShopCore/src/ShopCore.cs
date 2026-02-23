using Cookies.Contract;
using Economy.Contract;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Database;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Translation;

namespace ShopCore;

[PluginMetadata(
    Id = "ShopCore",
    Version = "v1.0.6",
    Name = "ShopCore",
    Author = "T3Marius",
    Description = "Core shop plugin exposing items and credits API."
)]
public partial class ShopCore : BasePlugin
{
    private const float CookieWarmupGraceSeconds = 1.0f;
    private const float CookieWarmupNoticeCooldownSeconds = 0.75f;

    public const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    public const string PlayerCookiesInterfaceKey = "Cookies.Player.v1";
    public const string PlayerCookiesInterfaceKeyLegacy = "Cookies.Player.V1";
    public const string EconomyInterfaceKey = "Economy.API.v2";
    public const string EconomyInterfaceKeyLegacy = "Economy.API.v1";
    public const string EconomyInterfaceKeyLegacyUpper = "Economy.API.V1";
    public ILocalizer Localizer { get; set; } = null!;

    private readonly ShopCoreApiV2 shopApi;

    public ShopCore(ISwiftlyCore core) : base(core)
    {
        shopApi = new ShopCoreApiV2(this);
    }

    public IPlayerCookiesAPIv1 playerCookies = null!;
    public IEconomyAPIv1 economyApi = null!;

    private readonly Dictionary<ulong, DateTimeOffset> cookieWarmupUntilBySteamId = [];
    private readonly Dictionary<ulong, DateTimeOffset> cookieWarmupNoticeCooldownBySteamId = [];

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<IShopCoreApiV2, ShopCoreApiV2>(ShopCoreInterfaceKey, shopApi);
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        playerCookies = null!;
        economyApi = null!;

        playerCookies = ResolveSharedInterface<IPlayerCookiesAPIv1>(
            interfaceManager,
            [PlayerCookiesInterfaceKey, PlayerCookiesInterfaceKeyLegacy]
        )!;

        economyApi = ResolveSharedInterface<IEconomyAPIv1>(
            interfaceManager,
            [EconomyInterfaceKey, EconomyInterfaceKeyLegacy, EconomyInterfaceKeyLegacyUpper]
        )!;
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        StopTimedIncome();
        UnsubscribeEvents();
        UnregisterConfiguredCommands();

        if (playerCookies is null || economyApi is null)
        {
            var hasCookies = interfaceManager.HasSharedInterface(PlayerCookiesInterfaceKey)
                || interfaceManager.HasSharedInterface(PlayerCookiesInterfaceKeyLegacy);
            var hasEconomy = interfaceManager.HasSharedInterface(EconomyInterfaceKey)
                || interfaceManager.HasSharedInterface(EconomyInterfaceKeyLegacy)
                || interfaceManager.HasSharedInterface(EconomyInterfaceKeyLegacyUpper);

            Core.Logger.LogError(
                "ShopCore dependencies are missing or incompatible. Required interfaces: '{CookiesKey}', '{EconomyKey}'. " +
                "HasSharedInterface(Cookies)={HasCookies}, HasSharedInterface(Economy)={HasEconomy}, " +
                "Expected Cookies contract assembly='{CookiesAssembly}', Expected Economy contract assembly='{EconomyAssembly}'.",
                $"{PlayerCookiesInterfaceKey} | {PlayerCookiesInterfaceKeyLegacy}",
                $"{EconomyInterfaceKey} | {EconomyInterfaceKeyLegacy} | {EconomyInterfaceKeyLegacyUpper}",
                hasCookies,
                hasEconomy,
                typeof(IPlayerCookiesAPIv1).Assembly.FullName,
                typeof(IEconomyAPIv1).Assembly.FullName
            );
            return;
        }

        economyApi.EnsureWalletKind(shopApi.WalletKind);
        shopApi.ConfigureLedgerStore(Settings.Ledger, Core.PluginDataDirectory);
        RegisterConfiguredCommands();
        SubscribeEvents();
        ApplyStartingBalanceToConnectedPlayers();
        StartTimedIncome();
    }

    public override void Load(bool hotReload)
    {
        Localizer = Core.Localizer;
        InitializeConfiguration();
    }

    private T? ResolveSharedInterface<T>(IInterfaceManager interfaceManager, IEnumerable<string> keys) where T : class
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!interfaceManager.HasSharedInterface(key))
            {
                continue;
            }

            try
            {
                var resolved = interfaceManager.GetSharedInterface<T>(key);
                Core.Logger.LogInformation("Resolved shared interface '{InterfaceType}' using key '{InterfaceKey}'.", typeof(T).FullName, key);
                return resolved;
            }
            catch (Exception ex)
            {
                Core.Logger.LogError(ex, "Failed to resolve shared interface '{InterfaceKey}'.", key);
            }
        }

        return null;
    }

    public override void Unload()
    {
        StopTimedIncome();
        UnsubscribeEvents();
        UnregisterConfiguredCommands();
        shopApi.DisposeLedgerStore();
    }

    internal string? GetPluginPath(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        try
        {
            return Core.PluginManager.GetPluginPath(pluginId);
        }
        catch
        {
            return null;
        }
    }

    internal string GetCentralModuleConfigsDirectoryPath()
    {
        return Path.Combine(
            Core.CSGODirectory,
            "addons",
            "swiftlys2",
            "configs",
            "plugins",
            "ShopCore",
            "modules"
        );
    }

    internal string BuildCentralModuleConfigPath(string modulePluginId, string normalizedRelativeConfigPath)
    {
        var normalized = string.IsNullOrWhiteSpace(normalizedRelativeConfigPath)
            ? "items_config.jsonc"
            : normalizedRelativeConfigPath.Trim();

        var fileName = Path.GetFileName(normalized);
        var safeFileName = SanitizeFileNameSegment(fileName);

        return Path.Combine(GetCentralModuleConfigsDirectoryPath(), safeFileName);
    }

    internal string BuildLegacyModuleScopedCentralModuleConfigPath(string modulePluginId, string normalizedRelativeConfigPath)
    {
        var moduleId = string.IsNullOrWhiteSpace(modulePluginId) ? "UnknownModule" : modulePluginId.Trim();
        var normalized = string.IsNullOrWhiteSpace(normalizedRelativeConfigPath)
            ? "items_config.jsonc"
            : normalizedRelativeConfigPath.Trim();

        var safeModuleId = SanitizeFileNameSegment(moduleId);
        var safePath = normalized
            .Replace('/', '_')
            .Replace('\\', '_');
        safePath = SanitizeFileNameSegment(safePath);

        return Path.Combine(GetCentralModuleConfigsDirectoryPath(), $"{safeModuleId}__{safePath}");
    }

    private static string SanitizeFileNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "config";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", "__", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "config" : sanitized;
    }

    internal void LogWarning(string message, params object[] args)
    {
        Core.Logger.LogWarning(message, args);
    }

    internal void LogWarning(Exception exception, string message, params object[] args)
    {
        Core.Logger.LogWarning(exception, message, args);
    }

    internal void LogDebug(string message, params object[] args)
    {
        Core.Logger.LogDebug(message, args);
    }

    internal DatabaseConnectionInfo? TryGetDatabaseConnectionInfo(string connectionName)
    {
        try
        {
            return Core.Database.GetConnectionInfo(connectionName);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to resolve Core.Database connection info for '{ConnectionName}'.", connectionName);
            return null;
        }
    }

    internal void SendLocalizedChat(IPlayer player, string key, params object[] args)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (player is null || !player.IsValid)
                {
                    return;
                }

                var message = Localize(player, key, args);
                var prefix = TryGetChatPrefix(player);

                if (!string.IsNullOrWhiteSpace(prefix) && !message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    player.SendChat($"{prefix} {message}");
                    return;
                }

                player.SendChat(message);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to send localized chat message for key '{TranslationKey}'.", key);
            }
        });
    }

    internal void SendChatRaw(IPlayer player, string message)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                if (player is null || !player.IsValid)
                {
                    return;
                }

                var prefix = TryGetChatPrefix(player);
                if (!string.IsNullOrWhiteSpace(prefix) && !message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    player.SendChat($"{prefix} {message}");
                    return;
                }

                player.SendChat(message);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to send raw chat message.");
            }
        });
    }

    internal string Localize(IPlayer player, string key, params object[] args)
    {
        try
        {
            var localizer = Core.Translation.GetPlayerLocalizer(player);
            return args.Length == 0 ? localizer[key] : localizer[key, args];
        }
        catch
        {
            if (args.Length == 0)
            {
                return key;
            }

            try
            {
                return string.Format(key, args);
            }
            catch
            {
                return key;
            }
        }
    }

    internal void MarkCookieWarmupPending(ulong steamId)
    {
        if (steamId == 0)
        {
            return;
        }

        cookieWarmupUntilBySteamId[steamId] = DateTimeOffset.UtcNow.AddSeconds(CookieWarmupGraceSeconds);
    }

    internal void ClearCookieWarmupState(ulong steamId)
    {
        if (steamId == 0)
        {
            return;
        }

        _ = cookieWarmupUntilBySteamId.Remove(steamId);
        _ = cookieWarmupNoticeCooldownBySteamId.Remove(steamId);
    }

    internal bool RunAfterCookieWarmup(IPlayer player, Action<IPlayer> action, bool notifyIfDelayed = true)
    {
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return false;
        }

        var steamId = player.SteamID;
        if (!TryGetRemainingCookieWarmupDelay(steamId, out var delaySeconds))
        {
            action(player);
            return true;
        }

        if (notifyIfDelayed)
        {
            MaybeNotifyCookieWarmupDelay(player);
        }

        var playerId = player.PlayerID;
        Core.Scheduler.DelayBySeconds(delaySeconds, () =>
        {
            try
            {
                var current = Core.PlayerManager.GetPlayer(playerId);
                if (current is null || !current.IsValid || current.IsFakeClient)
                {
                    return;
                }

                if (steamId != 0 && current.SteamID != steamId)
                {
                    return;
                }

                action(current);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to execute delayed cookie-warmup action for playerId={PlayerId}.", playerId);
            }
        });

        return false;
    }

    internal void FlushPlayerCookiesAsync(ulong steamId)
    {
        if (steamId == 0 || playerCookies is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                playerCookies.Save((long)steamId);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Background cookie flush failed for steamId={SteamId}.", steamId);
            }
        });
    }

    private bool TryGetRemainingCookieWarmupDelay(ulong steamId, out float delaySeconds)
    {
        delaySeconds = 0f;
        if (steamId == 0)
        {
            return false;
        }

        if (!cookieWarmupUntilBySteamId.TryGetValue(steamId, out var readyAt))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (readyAt <= now)
        {
            _ = cookieWarmupUntilBySteamId.Remove(steamId);
            _ = cookieWarmupNoticeCooldownBySteamId.Remove(steamId);
            return false;
        }

        var remaining = readyAt - now;
        delaySeconds = Math.Max(0.05f, (float)remaining.TotalSeconds);
        return true;
    }

    private void MaybeNotifyCookieWarmupDelay(IPlayer player)
    {
        var steamId = player.SteamID;
        if (steamId == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (cookieWarmupNoticeCooldownBySteamId.TryGetValue(steamId, out var cooldownUntil) && cooldownUntil > now)
        {
            return;
        }

        cookieWarmupNoticeCooldownBySteamId[steamId] = now.AddSeconds(CookieWarmupNoticeCooldownSeconds);
        SendChatRaw(player, "Loading shop data...");
    }

    private string TryGetChatPrefix(IPlayer player)
    {
        try
        {
            var localizer = Core.Translation.GetPlayerLocalizer(player);
            var prefix = localizer["shop.prefix"];

            if (string.Equals(prefix, "shop.prefix", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return prefix;
        }
        catch
        {
            return string.Empty;
        }
    }
}
