using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Parachute",
    Name = "Shop Parachute",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore parachute module"
)]
public class Shop_Parachute : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_Parachute";
    private const string TemplateFileName = "items_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Movement/Parachute";
    private const float VelocityUpdateIntervalSeconds = 0.05f;
    private const float VelocityEpsilon = 0.5f;

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, ParachuteItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, bool> parachuteActiveByPlayer = new();
    private readonly Dictionary<int, float> nextVelocityUpdateAtByPlayer = new();

    public Shop_Parachute(ISwiftlyCore core) : base(core) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        shopApi = null;

        if (!interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            return;
        }

        try
        {
            shopApi = interfaceManager.GetSharedInterface<IShopCoreApiV1>(ShopCoreInterfaceKey);
        }
        catch (Exception ex)
        {
            Core.Logger.LogInformation(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Parachute items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnTick += OnTick;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        Core.Event.OnTick -= OnTick;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            RestoreDefaultGravity(player);
        }

        parachuteActiveByPlayer.Clear();
        nextVelocityUpdateAtByPlayer.Clear();
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is not null && player.IsValid && !player.IsFakeClient)
        {
            parachuteActiveByPlayer[player.PlayerID] = false;
            nextVelocityUpdateAtByPlayer.Remove(player.PlayerID);
            RestoreDefaultGravity(player);
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is not null && player.IsValid && !player.IsFakeClient)
        {
            parachuteActiveByPlayer[player.PlayerID] = false;
            nextVelocityUpdateAtByPlayer.Remove(player.PlayerID);
            RestoreDefaultGravity(player);
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        parachuteActiveByPlayer.Remove(@event.PlayerId);
        nextVelocityUpdateAtByPlayer.Remove(@event.PlayerId);
    }

    private void OnTick()
    {
        if (shopApi is null || !handlersRegistered || registeredItemOrder.Count == 0)
        {
            return;
        }

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            if (!TryGetEnabledParachute(player, out var runtime))
            {
                if (IsParachuteActive(player))
                {
                    DeactivateParachute(player);
                }
                continue;
            }

            ApplyParachutePhysics(player, runtime);
        }
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleTemplateConfig<ParachuteModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(moduleConfig);

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            _ = shopApi.SaveModuleTemplateConfig(
                ModulePluginId,
                moduleConfig,
                TemplateFileName,
                TemplateSectionName,
                overwrite: true
            );
        }

        var registeredCount = 0;
        foreach (var itemTemplate in moduleConfig.Items)
        {
            if (!TryCreateDefinition(itemTemplate, category, out var definition, out var runtime))
            {
                continue;
            }

            if (!shopApi.RegisterItem(definition))
            {
                Core.Logger.LogWarning("Failed to register parachute item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            itemRuntimeById[definition.Id] = runtime;
            registeredCount++;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemToggled += OnItemToggled;
        shopApi.OnItemSold += OnItemSold;
        shopApi.OnItemExpired += OnItemExpired;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_Parachute initialized. RegisteredItems={RegisteredItems}",
            registeredCount
        );
    }

    private void UnregisterItemsAndHandlers()
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        shopApi.OnBeforeItemPurchase -= OnBeforeItemPurchase;
        shopApi.OnItemToggled -= OnItemToggled;
        shopApi.OnItemSold -= OnItemSold;
        shopApi.OnItemExpired -= OnItemExpired;

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        itemRuntimeById.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!registeredItemIds.Contains(context.Item.Id))
        {
            return;
        }

        if (!itemRuntimeById.TryGetValue(context.Item.Id, out var runtime))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(runtime.RequiredPermission))
        {
            return;
        }

        if (Core.Permission.PlayerHasPermission(context.Player.SteamID, runtime.RequiredPermission))
        {
            return;
        }

        context.BlockLocalized(
            "module.parachute.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!registeredItemIds.Contains(item.Id) || shopApi is null)
        {
            return;
        }

        if (enabled)
        {
            foreach (var otherItemId in registeredItemOrder)
            {
                if (string.Equals(otherItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!shopApi.IsItemEnabled(player, otherItemId))
                {
                    continue;
                }

                _ = shopApi.SetItemEnabled(player, otherItemId, false);
            }
        }

        if (!enabled)
        {
            DeactivateParachute(player);
        }
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal creditedAmount)
    {
        if (registeredItemIds.Contains(item.Id))
        {
            DeactivateParachute(player);
        }
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (registeredItemIds.Contains(item.Id))
        {
            DeactivateParachute(player);
        }
    }

    private void ApplyParachutePhysics(IPlayer player, ParachuteItemRuntime runtime)
    {
        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid || !player.IsAlive)
        {
            if (IsParachuteActive(player))
            {
                DeactivateParachute(player);
            }
            return;
        }

        var pressingUse = IsUsePressed(player);
        var isGrounded = pawn.GroundEntity.IsValid;
        var velocity = pawn.AbsVelocity;

        if (!pressingUse || isGrounded || velocity.Z >= 0f)
        {
            if (IsParachuteActive(player))
            {
                DeactivateParachute(player);
            }
            return;
        }

        var targetFallSpeed = -MathF.Abs(runtime.FallSpeed);
        var currentTime = Core.Engine.GlobalVars.CurrentTime;
        if (!nextVelocityUpdateAtByPlayer.TryGetValue(player.PlayerID, out var nextApplyTime) ||
            currentTime >= nextApplyTime)
        {
            var newZ = runtime.Linear || runtime.FallDecrease <= 0f
                ? targetFallSpeed
                : Math.Max(velocity.Z + MathF.Abs(runtime.FallDecrease), targetFallSpeed);

            if (MathF.Abs(velocity.Z - newZ) > VelocityEpsilon)
            {
                velocity.Z = newZ;
                pawn.AbsVelocity = velocity;
                pawn.VelocityUpdated();
            }

            nextVelocityUpdateAtByPlayer[player.PlayerID] = currentTime + VelocityUpdateIntervalSeconds;
        }

        var gravityScale = Math.Clamp(runtime.GravityScale, 0.01f, 1.0f);
        if (!IsParachuteActive(player) || MathF.Abs(pawn.GravityScale - gravityScale) > 0.001f)
        {
            pawn.GravityScale = gravityScale;
        }

        parachuteActiveByPlayer[player.PlayerID] = true;
    }

    private void DeactivateParachute(IPlayer player)
    {
        parachuteActiveByPlayer[player.PlayerID] = false;
        nextVelocityUpdateAtByPlayer.Remove(player.PlayerID);
        RestoreDefaultGravity(player);
    }

    private bool IsParachuteActive(IPlayer player)
    {
        return parachuteActiveByPlayer.TryGetValue(player.PlayerID, out var active) && active;
    }

    private static void RestoreDefaultGravity(IPlayer player)
    {
        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
        {
            return;
        }

        if (MathF.Abs(pawn.GravityScale - 1.0f) > 0.001f)
        {
            pawn.GravityScale = 1.0f;
        }
    }

    private static bool IsUsePressed(IPlayer player)
    {
        try
        {
            // Source input bit for +use (IN_USE).
            const ulong useMask = 1UL << 5;
            var rawButtons = Convert.ToUInt64(player.PressedButtons);
            return (rawButtons & useMask) != 0;
        }
        catch
        {
            // Fallback for enum name changes across framework versions.
            return player.PressedButtons.ToString().Contains("Use", StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool TryGetEnabledParachute(IPlayer player, out ParachuteItemRuntime runtime)
    {
        runtime = default;

        if (shopApi is null)
        {
            return false;
        }

        foreach (var itemId in registeredItemOrder)
        {
            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            if (!itemRuntimeById.TryGetValue(itemId, out runtime))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryCreateDefinition(
        ParachuteItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out ParachuteItemRuntime runtime)
    {
        definition = default!;
        runtime = default;

        if (string.IsNullOrWhiteSpace(itemTemplate.Id))
        {
            return false;
        }

        var itemId = itemTemplate.Id.Trim();

        if (itemTemplate.Price <= 0)
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Price must be greater than 0.", itemId);
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Type, ignoreCase: true, out ShopItemType itemType))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Type '{Type}' is invalid.", itemId, itemTemplate.Type);
            return false;
        }

        if (itemType == ShopItemType.Consumable)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because parachute items cannot use Type '{Type}'.",
                itemId,
                itemType
            );
            return false;
        }

        if (!Enum.TryParse(itemTemplate.Team, ignoreCase: true, out ShopItemTeam team))
        {
            team = ShopItemTeam.Any;
        }

        TimeSpan? duration = null;
        if (itemTemplate.DurationSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(itemTemplate.DurationSeconds);
        }

        if (itemType == ShopItemType.Temporary && !duration.HasValue)
        {
            Core.Logger.LogWarning(
                "Skipping item '{ItemId}' because Temporary items require DurationSeconds > 0.",
                itemId
            );
            return false;
        }

        decimal? sellPrice = null;
        if (itemTemplate.SellPrice.HasValue && itemTemplate.SellPrice.Value >= 0)
        {
            sellPrice = itemTemplate.SellPrice.Value;
        }

        definition = new ShopItemDefinition(
            Id: itemId,
            DisplayName: ResolveDisplayName(itemTemplate),
            Category: category,
            Price: itemTemplate.Price,
            SellPrice: sellPrice,
            Duration: duration,
            Type: itemType,
            Team: team,
            Enabled: itemTemplate.Enabled,
            CanBeSold: itemTemplate.CanBeSold
        );

        runtime = new ParachuteItemRuntime(
            ItemId: itemId,
            FallSpeed: Math.Max(1f, itemTemplate.FallSpeed),
            FallDecrease: Math.Max(0f, itemTemplate.FallDecrease),
            Linear: itemTemplate.Linear,
            GravityScale: Math.Clamp(itemTemplate.GravityScale, 0.01f, 1.0f),
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(ParachuteItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? Core.Localizer[key]
                : Core.Localizer[key, FormatDuration(itemTemplate.DurationSeconds)];
            if (!string.Equals(localized, key, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName))
        {
            return itemTemplate.DisplayName.Trim();
        }

        return itemTemplate.Id.Trim();
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0 Seconds";
        }

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
        {
            var hours = (int)ts.TotalHours;
            var minutes = ts.Minutes;
            return minutes > 0
                ? $"{hours} Hour{(hours == 1 ? "" : "s")} {minutes} Minute{(minutes == 1 ? "" : "s")}"
                : $"{hours} Hour{(hours == 1 ? "" : "s")}";
        }

        if (ts.TotalMinutes >= 1)
        {
            var minutes = (int)ts.TotalMinutes;
            var seconds = ts.Seconds;
            return seconds > 0
                ? $"{minutes} Minute{(minutes == 1 ? "" : "s")} {seconds} Second{(seconds == 1 ? "" : "s")}"
                : $"{minutes} Minute{(minutes == 1 ? "" : "s")}";
        }

        return $"{ts.Seconds} Second{(ts.Seconds == 1 ? "" : "s")}";
    }

    private static void NormalizeConfig(ParachuteModuleConfig config)
    {
        config.Settings ??= new ParachuteModuleSettings();
        config.Items ??= [];
    }

    private static ParachuteModuleConfig CreateDefaultConfig()
    {
        return new ParachuteModuleConfig
        {
            Settings = new ParachuteModuleSettings
            {
                Category = DefaultCategory
            },
            Items =
            [
                new ParachuteItemTemplate
                {
                    Id = "parachute_basic_hourly",
                    DisplayNameKey = "module.parachute.item.basic.name",
                    Price = 500,
                    SellPrice = 250,
                    DurationSeconds = 3600,
                    FallSpeed = 85f,
                    FallDecrease = 15f,
                    GravityScale = 0.2f,
                    Linear = true,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new ParachuteItemTemplate
                {
                    Id = "parachute_premium_weekly",
                    DisplayNameKey = "module.parachute.item.premium.name",
                    Price = 1500,
                    SellPrice = 750,
                    DurationSeconds = 604800,
                    FallSpeed = 50f,
                    FallDecrease = 10f,
                    GravityScale = 0.1f,
                    Linear = true,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                },
                new ParachuteItemTemplate
                {
                    Id = "parachute_permanent",
                    DisplayNameKey = "module.parachute.item.permanent.name",
                    Price = 8000,
                    SellPrice = 4000,
                    DurationSeconds = 0,
                    FallSpeed = 40f,
                    FallDecrease = 5f,
                    GravityScale = 0.08f,
                    Linear = true,
                    Type = nameof(ShopItemType.Permanent),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true
                }
            ]
        };
    }
}

internal readonly record struct ParachuteItemRuntime(
    string ItemId,
    float FallSpeed,
    float FallDecrease,
    bool Linear,
    float GravityScale,
    string RequiredPermission
);

internal sealed class ParachuteModuleConfig
{
    public ParachuteModuleSettings Settings { get; set; } = new();
    public List<ParachuteItemTemplate> Items { get; set; } = [];
}

internal sealed class ParachuteModuleSettings
{
    public string Category { get; set; } = "Movement/Parachute";
}

internal sealed class ParachuteItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public int Price { get; set; } = 0;
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; } = 0;
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public float FallSpeed { get; set; } = 85f;
    public float FallDecrease { get; set; } = 15f;
    public bool Linear { get; set; } = true;
    public float GravityScale { get; set; } = 0.2f;
    public string RequiredPermission { get; set; } = string.Empty;
}
