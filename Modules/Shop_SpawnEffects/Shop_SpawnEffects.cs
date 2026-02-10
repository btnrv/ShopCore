using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using Vector = SwiftlyS2.Shared.Natives.Vector;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_SpawnEffects",
    Name = "Shop SpawnEffects",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with spawn effect items."
)]
public class Shop_SpawnEffects : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v1";
    private const string ModulePluginId = "Shop_SpawnEffects";
    private const string TemplateFileName = "items_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Effects/Spawn Effects";

    private IShopCoreApiV1? shopApi;
    private bool handlersRegistered;
    private SpawnEffectsSettings settings = new();

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, SpawnEffectRuntime> runtimeByItemId = new(StringComparer.OrdinalIgnoreCase);

    public Shop_SpawnEffects(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning("ShopCore API is not available. SpawnEffects items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Unload()
    {
        UnregisterItemsAndHandlers();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return HookResult.Continue;
        }

        var player = @event.UserIdPlayer;
        if (player is null || !player.IsValid || player.IsFakeClient || !player.IsAlive)
        {
            return HookResult.Continue;
        }

        if (!TryGetActiveEffect(player, out var runtime))
        {
            return HookResult.Continue;
        }

        var delay = settings.SpawnDelaySeconds;
        if (delay <= 0f)
        {
            Core.Scheduler.NextWorldUpdate(() => TrySpawnEffect(player, runtime));
        }
        else
        {
            _ = Core.Scheduler.DelayBySeconds(delay, () => TrySpawnEffect(player, runtime));
        }

        return HookResult.Continue;
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleTemplateConfig<SpawnEffectsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(moduleConfig);

        settings = moduleConfig.Settings;
        settings.SpawnDelaySeconds = Math.Max(0f, settings.SpawnDelaySeconds);

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            settings = moduleConfig.Settings;
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
                Core.Logger.LogWarning("Failed to register spawn effect item '{ItemId}'.", definition.Id);
                continue;
            }

            _ = registeredItemIds.Add(definition.Id);
            registeredItemOrder.Add(definition.Id);
            runtimeByItemId[definition.Id] = runtime;
            registeredCount++;
        }

        shopApi.OnBeforeItemPurchase += OnBeforeItemPurchase;
        shopApi.OnItemToggled += OnItemToggled;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_SpawnEffects initialized. RegisteredItems={RegisteredItems}",
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

        foreach (var itemId in registeredItemIds)
        {
            _ = shopApi.UnregisterItem(itemId);
        }

        registeredItemIds.Clear();
        registeredItemOrder.Clear();
        runtimeByItemId.Clear();
        handlersRegistered = false;
    }

    private void OnBeforeItemPurchase(ShopBeforePurchaseContext context)
    {
        if (!registeredItemIds.Contains(context.Item.Id))
        {
            return;
        }

        if (!runtimeByItemId.TryGetValue(context.Item.Id, out var runtime))
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
            "module.spawn_effects.error.permission",
            context.Item.DisplayName,
            runtime.RequiredPermission
        );
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (!enabled || shopApi is null || !registeredItemIds.Contains(item.Id))
        {
            return;
        }

        // Keep one active spawn effect per player.
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

    private bool TryGetActiveEffect(IPlayer player, out SpawnEffectRuntime runtime)
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

            if (!runtimeByItemId.TryGetValue(itemId, out runtime))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void TrySpawnEffect(IPlayer player, SpawnEffectRuntime runtime)
    {
        if (!player.IsValid || player.IsFakeClient || !player.IsAlive)
        {
            return;
        }

        var pawn = player.PlayerPawn;
        var sceneNode = pawn?.CBodyComponent?.SceneNode;
        if (pawn is null || !pawn.IsValid || sceneNode is null || !sceneNode.IsValid)
        {
            return;
        }

        try
        {
            var grenade = Core.EntitySystem.CreateEntity<CHEGrenadeProjectile>();

            var position = sceneNode.AbsOrigin;
            position.Z += runtime.VerticalOffset;

            grenade.TicksAtZeroVelocity = 100;
            grenade.TeamNum = pawn.TeamNum;
            grenade.Damage = runtime.Damage;
            grenade.DmgRadius = runtime.DamageRadius;
            grenade.Teleport(position, sceneNode.AbsRotation, new Vector(0f, 0f, -Math.Abs(runtime.DownwardVelocity)));
            grenade.DispatchSpawn();
            grenade.AcceptInput("InitializeSpawnFromWorld", string.Empty, pawn, pawn);
            grenade.DetonateTime.Value = 0f;
            grenade.DetonateTimeUpdated();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed to trigger spawn effect for player {PlayerId}.", player.PlayerID);
        }
    }

    private bool TryCreateDefinition(
        SpawnEffectItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out SpawnEffectRuntime runtime)
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
                "Skipping item '{ItemId}' because spawn effects cannot use Type '{Type}'.",
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
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Temporary items require DurationSeconds > 0.", itemId);
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

        runtime = new SpawnEffectRuntime(
            ItemId: itemId,
            Damage: itemTemplate.Damage,
            DamageRadius: itemTemplate.DamageRadius,
            VerticalOffset: itemTemplate.VerticalOffset,
            DownwardVelocity: itemTemplate.DownwardVelocity,
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(SpawnEffectItemTemplate itemTemplate)
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

    private static void NormalizeConfig(SpawnEffectsModuleConfig config)
    {
        config.Settings ??= new SpawnEffectsSettings();
        config.Items ??= [];
    }

    private static SpawnEffectsModuleConfig CreateDefaultConfig()
    {
        return new SpawnEffectsModuleConfig
        {
            Settings = new SpawnEffectsSettings
            {
                Category = DefaultCategory,
                SpawnDelaySeconds = 0.05f
            },
            Items =
            [
                new SpawnEffectItemTemplate
                {
                    Id = "spawn_effect_hourly",
                    DisplayNameKey = "module.spawn_effects.item.default.name",
                    Price = 1500,
                    SellPrice = 750,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    Damage = 0f,
                    DamageRadius = 0f,
                    VerticalOffset = 10f,
                    DownwardVelocity = 10f,
                    RequiredPermission = string.Empty
                }
            ]
        };
    }
}

internal readonly record struct SpawnEffectRuntime(
    string ItemId,
    float Damage,
    float DamageRadius,
    float VerticalOffset,
    float DownwardVelocity,
    string RequiredPermission
);

internal sealed class SpawnEffectsModuleConfig
{
    public SpawnEffectsSettings Settings { get; set; } = new();
    public List<SpawnEffectItemTemplate> Items { get; set; } = [];
}

internal sealed class SpawnEffectsSettings
{
    public string Category { get; set; } = "Effects/Spawn Effects";
    public float SpawnDelaySeconds { get; set; } = 0.05f;
}

internal sealed class SpawnEffectItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public int Price { get; set; }
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; }
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public float Damage { get; set; }
    public float DamageRadius { get; set; }
    public float VerticalOffset { get; set; } = 10f;
    public float DownwardVelocity { get; set; } = 10f;
    public string RequiredPermission { get; set; } = string.Empty;
}
