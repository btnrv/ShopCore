using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.SteamAPI;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_CustomWeapons",
    Name = "Shop CustomWeapons",
    Author = "T3Marius",
    Version = "1.0.0",
    Description = "ShopCore module with weapon skin items"
)]
public class Shop_CustomWeapons : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_CustomWeapons";
    private const string TemplateFileName = "customweapons_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string DefaultCategory = "Visuals/Weapon Skins";
    private const uint CustomFallbackItemHigh = uint.MaxValue;

    private IShopCoreApiV2? shopApi;
    private bool handlersRegistered;

    private readonly HashSet<string> registeredItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> registeredItemOrder = new();
    private readonly Dictionary<string, CustomWeaponItemRuntime> itemRuntimeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, WeaponEconSnapshot> originalWeaponStateByAddress = new();
    private readonly Dictionary<int, WeaponSkinPreviewState> previewStateByPlayerId = new();
    private readonly Dictionary<ulong, DateTimeOffset> previewCooldownBySteam = new();

    private long previewSessionCounter;
    private CustomWeaponsModuleSettings runtimeSettings = new();

    public Shop_CustomWeapons(ISwiftlyCore core) : base(core)
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
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. CustomWeapons items will not be registered.");
            return;
        }

        if (!handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }
    }

    public override void Load(bool hotReload)
    {
        Core.Event.OnEntityCreated += OnEntityCreated;
        Core.Event.OnEntityDeleted += OnEntityDeleted;
        Core.Event.OnEntityParentChanged += OnEntityParentChanged;
        Core.Event.OnWeaponServicesCanUseHook += OnWeaponServicesCanUseHook;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        if (shopApi is not null && !handlersRegistered)
        {
            RegisterItemsAndHandlers();
        }

        if (hotReload)
        {
            RunOnMainThread(RefreshAllPlayersWeaponSkins);
        }
    }

    public override void Unload()
    {
        Core.Event.OnEntityCreated -= OnEntityCreated;
        Core.Event.OnEntityDeleted -= OnEntityDeleted;
        Core.Event.OnEntityParentChanged -= OnEntityParentChanged;
        Core.Event.OnWeaponServicesCanUseHook -= OnWeaponServicesCanUseHook;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;

        try
        {
            RestoreAllKnownWeaponAppearances();
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "Failed restoring weapon appearances during unload.");
        }

        UnregisterItemsAndHandlers();

        previewStateByPlayerId.Clear();
        previewCooldownBySteam.Clear();
        originalWeaponStateByAddress.Clear();
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        var player = Core.PlayerManager.GetPlayer(e.UserId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        RunOnMainThread(() => RefreshPlayerWeaponSkins(player));
        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        previewStateByPlayerId.Remove(e.PlayerId);
    }

    private void OnEntityCreated(IOnEntityCreatedEvent e)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        if (!IsWeaponDesignerName(e.Entity.DesignerName))
        {
            return;
        }

        var entityIndex = e.Entity.Index;
        Core.Scheduler.NextWorldUpdate(() => TrySyncWeaponByEntityIndex(entityIndex));
    }

    private void OnEntityDeleted(IOnEntityDeletedEvent e)
    {
        originalWeaponStateByAddress.Remove(e.Entity.Address);
    }

    private void OnEntityParentChanged(IOnEntityParentChangedEvent e)
    {
        if (!handlersRegistered || shopApi is null)
        {
            return;
        }

        if (!IsWeaponDesignerName(e.Entity.DesignerName))
        {
            return;
        }

        var entityIndex = e.Entity.Index;
        Core.Scheduler.NextWorldUpdate(() => TrySyncWeaponByEntityIndex(entityIndex));
    }

    private void OnWeaponServicesCanUseHook(IOnWeaponServicesCanUseHookEvent e)
    {
        if (!handlersRegistered || shopApi is null || !e.OriginalResult)
        {
            return;
        }

        if (e.Weapon is null || !e.Weapon.IsValid)
        {
            return;
        }

        var player = TryResolvePlayerFromWeaponServices(e.WeaponServices);
        if (player is null)
        {
            return;
        }

        var weaponIndex = e.Weapon.Index;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            TrySyncSpecificWeaponForPlayer(player, weaponIndex);
        });
    }

    private void RegisterItemsAndHandlers()
    {
        if (shopApi is null)
        {
            return;
        }

        UnregisterItemsAndHandlers();

        var moduleConfig = shopApi.LoadModuleConfig<CustomWeaponsModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );
        NormalizeConfig(moduleConfig);
        runtimeSettings = moduleConfig.Settings;

        var category = string.IsNullOrWhiteSpace(moduleConfig.Settings.Category)
            ? DefaultCategory
            : moduleConfig.Settings.Category.Trim();

        if (moduleConfig.Items.Count == 0)
        {
            moduleConfig = CreateDefaultConfig();
            category = moduleConfig.Settings.Category;
            runtimeSettings = moduleConfig.Settings;
            _ = shopApi.SaveModuleConfig(
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
                Core.Logger.LogWarning("Failed to register custom weapon item '{ItemId}'.", definition.Id);
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
        shopApi.OnItemPreview += OnItemPreview;
        handlersRegistered = true;

        Core.Logger.LogInformation(
            "Shop_CustomWeapons initialized. RegisteredItems={RegisteredItems}",
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
        shopApi.OnItemPreview -= OnItemPreview;

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

        var player = context.Player;
        var loc = Core.Translation.GetPlayerLocalizer(player);
        context.Block($"{GetPrefix(player)} {loc["error.permission", context.Item.DisplayName, runtime.RequiredPermission]}");
    }

    private void OnItemToggled(IPlayer player, ShopItemDefinition item, bool enabled)
    {
        if (shopApi is null || !registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (enabled && itemRuntimeById.TryGetValue(item.Id, out var runtime))
        {
            foreach (var otherItemId in registeredItemOrder)
            {
                if (string.Equals(otherItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!itemRuntimeById.TryGetValue(otherItemId, out var otherRuntime))
                {
                    continue;
                }

                if (!string.Equals(otherRuntime.TargetWeaponKey, runtime.TargetWeaponKey, StringComparison.OrdinalIgnoreCase))
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

        RunOnMainThread(() => RefreshPlayerWeaponSkins(player));
    }

    private void OnItemSold(IPlayer player, ShopItemDefinition item, decimal amount)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => RefreshPlayerWeaponSkins(player));
    }

    private void OnItemExpired(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        RunOnMainThread(() => RefreshPlayerWeaponSkins(player));
    }

    private void OnItemPreview(IPlayer player, ShopItemDefinition item)
    {
        if (!registeredItemIds.Contains(item.Id))
        {
            return;
        }

        if (!itemRuntimeById.TryGetValue(item.Id, out var runtime))
        {
            return;
        }

        if (!runtime.AllowPreview || runtimeSettings.PreviewDurationSeconds <= 0f)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (runtimeSettings.PreviewCooldownSeconds > 0 &&
            previewCooldownBySteam.TryGetValue(player.SteamID, out var nextAllowedAt) &&
            now < nextAllowedAt)
        {
            var remaining = (int)Math.Ceiling((nextAllowedAt - now).TotalSeconds);
            SendPreviewMessage(player, "preview.cooldown", remaining);
            return;
        }

        if (runtimeSettings.PreviewCooldownSeconds > 0)
        {
            previewCooldownBySteam[player.SteamID] = now.AddSeconds(runtimeSettings.PreviewCooldownSeconds);
        }

        var sessionId = ++previewSessionCounter;
        previewStateByPlayerId[player.PlayerID] = new WeaponSkinPreviewState(
            SessionId: sessionId,
            ItemId: runtime.ItemId,
            ExpiresAt: Core.Engine.GlobalVars.CurrentTime + runtimeSettings.PreviewDurationSeconds
        );

        RunOnMainThread(() => RefreshPlayerWeaponSkins(player));
        SendPreviewMessage(player, "preview.started", item.DisplayName, (int)MathF.Ceiling(runtimeSettings.PreviewDurationSeconds));

        Core.Scheduler.DelayBySeconds(runtimeSettings.PreviewDurationSeconds, () =>
        {
            RunOnMainThread(() => ExpirePreviewIfCurrent(player.PlayerID, sessionId));
        });
    }

    private void ExpirePreviewIfCurrent(int playerId, long sessionId)
    {
        if (!previewStateByPlayerId.TryGetValue(playerId, out var state))
        {
            return;
        }

        if (state.SessionId != sessionId)
        {
            return;
        }

        previewStateByPlayerId.Remove(playerId);

        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        RefreshPlayerWeaponSkins(player);
    }

    private void TrySyncSpecificWeaponForPlayer(IPlayer player, uint weaponIndex)
    {
        var weapon = Core.EntitySystem.GetEntityByIndex<CBasePlayerWeapon>(weaponIndex);
        if (weapon is null || !weapon.IsValid || !IsWeaponDesignerName(weapon.DesignerName))
        {
            return;
        }

        ApplyResolvedAppearance(player, weapon);
    }

    private void TrySyncWeaponByEntityIndex(uint entityIndex)
    {
        var weapon = Core.EntitySystem.GetEntityByIndex<CBasePlayerWeapon>(entityIndex);
        if (weapon is null || !weapon.IsValid || !IsWeaponDesignerName(weapon.DesignerName))
        {
            return;
        }

        if (TryResolveOwningPlayer(weapon, out var player))
        {
            ApplyResolvedAppearance(player, weapon);
            return;
        }

        TryRestoreWeaponAppearance(weapon);
    }

    private void RefreshAllPlayersWeaponSkins()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            RefreshPlayerWeaponSkins(player);
        }
    }

    private void RefreshPlayerWeaponSkins(IPlayer player)
    {
        if (shopApi is null || player is null || !player.IsValid || player.IsFakeClient)
        {
            return;
        }

        if (!TryGetPlayerWeaponServices(player, out var weaponServices))
        {
            return;
        }

        foreach (var weapon in weaponServices.MyValidWeapons)
        {
            if (weapon is null || !weapon.IsValid)
            {
                continue;
            }

            if (!IsWeaponDesignerName(weapon.DesignerName))
            {
                continue;
            }

            ApplyResolvedAppearance(player, weapon);
        }
    }

    private void ApplyResolvedAppearance(IPlayer player, CBasePlayerWeapon weapon)
    {
        if (shopApi is null)
        {
            return;
        }

        try
        {
            if (TryGetEffectiveRuntimeForWeapon(player, weapon, out var runtime))
            {
                ApplyWeaponSkin(player, weapon, runtime);
                return;
            }

            TryRestoreWeaponAppearance(weapon);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(
                ex,
                "Failed to apply custom weapon appearance for player {PlayerId} on '{DesignerName}'.",
                player.PlayerID,
                weapon.DesignerName
            );
        }
    }

    private bool TryGetEffectiveRuntimeForWeapon(IPlayer player, CBasePlayerWeapon weapon, out CustomWeaponItemRuntime runtime)
    {
        runtime = default;

        if (TryGetPreviewRuntimeForWeapon(player, weapon, out runtime))
        {
            return true;
        }

        return TryGetEnabledRuntimeForWeapon(player, weapon, out runtime);
    }

    private bool TryGetPreviewRuntimeForWeapon(IPlayer player, CBasePlayerWeapon weapon, out CustomWeaponItemRuntime runtime)
    {
        runtime = default;

        if (!previewStateByPlayerId.TryGetValue(player.PlayerID, out var state))
        {
            return false;
        }

        if (state.ExpiresAt <= Core.Engine.GlobalVars.CurrentTime)
        {
            previewStateByPlayerId.Remove(player.PlayerID);
            return false;
        }

        if (!itemRuntimeById.TryGetValue(state.ItemId, out var previewRuntime))
        {
            return false;
        }

        if (!WeaponMatchesRuntime(weapon, previewRuntime))
        {
            return false;
        }

        runtime = previewRuntime;
        return true;
    }

    private bool TryGetEnabledRuntimeForWeapon(IPlayer player, CBasePlayerWeapon weapon, out CustomWeaponItemRuntime runtime)
    {
        runtime = default;

        if (shopApi is null)
        {
            return false;
        }

        foreach (var itemId in registeredItemOrder)
        {
            if (!itemRuntimeById.TryGetValue(itemId, out var itemRuntime))
            {
                continue;
            }

            if (!IsRuntimeAllowedForTeam(itemRuntime, player))
            {
                continue;
            }

            if (!WeaponMatchesRuntime(weapon, itemRuntime))
            {
                continue;
            }

            if (!shopApi.IsItemEnabled(player, itemId))
            {
                continue;
            }

            runtime = itemRuntime;
            return true;
        }

        return false;
    }

    private void ApplyWeaponSkin(IPlayer player, CBasePlayerWeapon weapon, CustomWeaponItemRuntime runtime)
    {
        if (weapon is null || !weapon.IsValid)
        {
            return;
        }

        CaptureOriginalWeaponStateIfNeeded(weapon);

        var econ = weapon.As<CEconEntity>();
        if (econ is null || !econ.IsValid)
        {
            return;
        }

        var itemView = econ.AttributeManager.Item;
        if (itemView is null || !itemView.IsValid)
        {
            return;
        }

        var accountId = new CSteamID(player.SteamID).GetSteamID32();

        itemView.AccountID = accountId;
        itemView.AccountIDUpdated();

        itemView.ItemIDHigh = CustomFallbackItemHigh;
        itemView.ItemIDHighUpdated();

        if (runtime.EntityQualityOverride.HasValue)
        {
            itemView.EntityQuality = runtime.EntityQualityOverride.Value;
            itemView.EntityQualityUpdated();
        }
        else if (runtime.StatTrakValue.HasValue)
        {
            itemView.EntityQuality = 9;
            itemView.EntityQualityUpdated();
        }

        itemView.Initialized = true;
        itemView.InitializedUpdated();

        econ.FallbackPaintKit = runtime.PaintKit;
        econ.FallbackPaintKitUpdated();

        econ.FallbackSeed = runtime.Seed;
        econ.FallbackSeedUpdated();

        econ.FallbackWear = runtime.Wear;
        econ.FallbackWearUpdated();

        econ.FallbackStatTrak = runtime.StatTrakValue ?? -1;
        econ.FallbackStatTrakUpdated();

        econ.AttributeManager.ItemUpdated();
        econ.AttributeManagerUpdated();
    }

    private void CaptureOriginalWeaponStateIfNeeded(CBasePlayerWeapon weapon)
    {
        if (weapon is null || !weapon.IsValid)
        {
            return;
        }

        var address = weapon.Address;
        if (originalWeaponStateByAddress.ContainsKey(address))
        {
            return;
        }

        var econ = weapon.As<CEconEntity>();
        if (econ is null || !econ.IsValid)
        {
            return;
        }

        var itemView = econ.AttributeManager.Item;
        if (itemView is null || !itemView.IsValid)
        {
            return;
        }

        originalWeaponStateByAddress[address] = new WeaponEconSnapshot(
            FallbackPaintKit: econ.FallbackPaintKit,
            FallbackSeed: econ.FallbackSeed,
            FallbackWear: econ.FallbackWear,
            FallbackStatTrak: econ.FallbackStatTrak,
            AccountId: itemView.AccountID,
            ItemIdHigh: itemView.ItemIDHigh,
            ItemIdLow: itemView.ItemIDLow,
            EntityQuality: itemView.EntityQuality,
            Initialized: itemView.Initialized
        );
    }

    private void TryRestoreWeaponAppearance(CBasePlayerWeapon weapon)
    {
        if (weapon is null || !weapon.IsValid)
        {
            return;
        }

        if (!originalWeaponStateByAddress.TryGetValue(weapon.Address, out var snapshot))
        {
            return;
        }

        var econ = weapon.As<CEconEntity>();
        if (econ is null || !econ.IsValid)
        {
            return;
        }

        var itemView = econ.AttributeManager.Item;
        if (itemView is null || !itemView.IsValid)
        {
            return;
        }

        itemView.AccountID = snapshot.AccountId;
        itemView.AccountIDUpdated();

        itemView.ItemIDHigh = snapshot.ItemIdHigh;
        itemView.ItemIDHighUpdated();

        itemView.ItemIDLow = snapshot.ItemIdLow;
        itemView.ItemIDLowUpdated();

        itemView.EntityQuality = snapshot.EntityQuality;
        itemView.EntityQualityUpdated();

        itemView.Initialized = snapshot.Initialized;
        itemView.InitializedUpdated();

        econ.FallbackPaintKit = snapshot.FallbackPaintKit;
        econ.FallbackPaintKitUpdated();

        econ.FallbackSeed = snapshot.FallbackSeed;
        econ.FallbackSeedUpdated();

        econ.FallbackWear = snapshot.FallbackWear;
        econ.FallbackWearUpdated();

        econ.FallbackStatTrak = snapshot.FallbackStatTrak;
        econ.FallbackStatTrakUpdated();

        econ.AttributeManager.ItemUpdated();
        econ.AttributeManagerUpdated();
    }

    private void RestoreAllKnownWeaponAppearances()
    {
        foreach (var weapon in Core.EntitySystem.GetAllEntitiesByClass<CBasePlayerWeapon>())
        {
            if (weapon is null || !weapon.IsValid)
            {
                continue;
            }

            TryRestoreWeaponAppearance(weapon);
        }
    }

    private bool TryResolveOwningPlayer(CBasePlayerWeapon weapon, out IPlayer player)
    {
        player = null!;

        if (weapon is null || !weapon.IsValid)
        {
            return false;
        }

        var ownerEntity = weapon.OwnerEntity.Value;
        if (ownerEntity is not null && ownerEntity.IsValid)
        {
            try
            {
                var ownerPawn = ownerEntity.As<CBasePlayerPawn>();
                var ownerPlayer = Core.PlayerManager.GetPlayerFromPawn(ownerPawn);
                if (ownerPlayer is not null && ownerPlayer.IsValid && !ownerPlayer.IsFakeClient)
                {
                    player = ownerPlayer;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private IPlayer? TryResolvePlayerFromWeaponServices(CCSPlayer_WeaponServices weaponServices)
    {
        if (weaponServices is null || !weaponServices.IsValid)
        {
            return null;
        }

        var pawn = weaponServices.Pawn;
        if (pawn is null || !pawn.IsValid)
        {
            return null;
        }

        var player = Core.PlayerManager.GetPlayerFromPawn(pawn);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return null;
        }

        return player;
    }

    private static bool IsWeaponDesignerName(string? designerName)
    {
        return !string.IsNullOrWhiteSpace(designerName) &&
            designerName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase);
    }

    private bool WeaponMatchesRuntime(CBasePlayerWeapon weapon, CustomWeaponItemRuntime runtime)
    {
        if (weapon is null || !weapon.IsValid)
        {
            return false;
        }

        var rawKey = NormalizeWeaponKey(weapon.DesignerName);
        if (string.Equals(rawKey, runtime.TargetWeaponKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aliasKey = GetAliasWeaponKey(weapon, rawKey);
        if (string.IsNullOrEmpty(aliasKey))
        {
            return false;
        }

        return string.Equals(aliasKey, runtime.TargetWeaponKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAliasWeaponKey(CBasePlayerWeapon weapon, string rawKey)
    {
        if (string.IsNullOrEmpty(rawKey))
        {
            return string.Empty;
        }

        if (rawKey.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase))
        {
            return "weapon_knife";
        }

        ushort itemDefIndex;
        try
        {
            itemDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
        }
        catch
        {
            return rawKey;
        }

        return (rawKey, itemDefIndex) switch
        {
            ("weapon_deagle", 64) => "weapon_revolver",
            ("weapon_m4a1", 60) => "weapon_m4a1_silencer",
            ("weapon_hkp2000", 61) => "weapon_usp_silencer",
            ("weapon_mp7", 23) => "weapon_mp5sd",
            _ => rawKey
        };
    }

    private static string NormalizeWeaponKey(string? weaponKey)
    {
        return string.IsNullOrWhiteSpace(weaponKey)
            ? string.Empty
            : weaponKey.Trim().ToLowerInvariant();
    }

    private bool IsRuntimeAllowedForTeam(CustomWeaponItemRuntime runtime, IPlayer player)
    {
        if (runtime.Team == ShopItemTeam.Any)
        {
            return true;
        }

        var teamNum = player.Controller.TeamNum;
        return runtime.Team switch
        {
            ShopItemTeam.T => teamNum == (int)Team.T,
            ShopItemTeam.CT => teamNum == (int)Team.CT,
            _ => true
        };
    }

    private bool TryGetPlayerWeaponServices(IPlayer player, out CPlayer_WeaponServices weaponServices)
    {
        weaponServices = null!;

        if (player is null || !player.IsValid || player.IsFakeClient || !player.IsAlive)
        {
            return false;
        }

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
        {
            return false;
        }

        var services = pawn.WeaponServices;
        if (services is null || !services.IsValid)
        {
            return false;
        }

        weaponServices = services;
        return true;
    }

    private void RunOnMainThread(Action action)
    {
        Core.Scheduler.NextWorldUpdate(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Shop_CustomWeapons main-thread action failed.");
            }
        });
    }

    private void SendPreviewMessage(IPlayer player, string key, params object[] args)
    {
        RunOnMainThread(() =>
        {
            if (player is null || !player.IsValid || player.IsFakeClient)
            {
                return;
            }

            var loc = Core.Translation.GetPlayerLocalizer(player);
            player.SendChat($"{GetPrefix(player)} {loc[key, args]}");
        });
    }

    private string GetPrefix(IPlayer player)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        if (runtimeSettings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        return loc["shop.prefix"];
    }

    private bool TryCreateDefinition(
        CustomWeaponItemTemplate itemTemplate,
        string category,
        out ShopItemDefinition definition,
        out CustomWeaponItemRuntime runtime)
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

        if (string.IsNullOrWhiteSpace(itemTemplate.Weapon))
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because Weapon is empty.", itemId);
            return false;
        }

        if (itemTemplate.PaintKit <= 0)
        {
            Core.Logger.LogWarning("Skipping item '{ItemId}' because PaintKit must be greater than 0.", itemId);
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
                "Skipping item '{ItemId}' because weapon skin items cannot use Type '{Type}'.",
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
            if (sellPrice.Value > itemTemplate.Price)
            {
                Core.Logger.LogWarning(
                    "Clamping SellPrice for '{ItemId}' from {SellPrice} to {Price} to prevent profit exploits.",
                    itemId,
                    sellPrice.Value,
                    itemTemplate.Price
                );
                sellPrice = itemTemplate.Price;
            }
        }

        var wear = Math.Clamp(itemTemplate.Wear, 0f, 1f);
        var targetWeaponKey = NormalizeWeaponKey(itemTemplate.Weapon);

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
            CanBeSold: itemTemplate.CanBeSold,
            AllowPreview: itemTemplate.AllowPreview
        );

        runtime = new CustomWeaponItemRuntime(
            ItemId: itemId,
            TargetWeaponKey: targetWeaponKey,
            PaintKit: itemTemplate.PaintKit,
            Wear: wear,
            Seed: Math.Max(0, itemTemplate.Seed),
            StatTrakValue: itemTemplate.StatTrak.HasValue && itemTemplate.StatTrak.Value >= 0 ? itemTemplate.StatTrak.Value : null,
            EntityQualityOverride: itemTemplate.EntityQuality.HasValue ? itemTemplate.EntityQuality.Value : null,
            Team: team,
            AllowPreview: itemTemplate.AllowPreview,
            RequiredPermission: itemTemplate.RequiredPermission?.Trim() ?? string.Empty
        );

        return true;
    }

    private string ResolveDisplayName(CustomWeaponItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayNameKey))
        {
            var key = itemTemplate.DisplayNameKey.Trim();
            var displayBase = ResolveBaseSkinName(itemTemplate);
            var localized = itemTemplate.Type.Equals(nameof(ShopItemType.Permanent), StringComparison.OrdinalIgnoreCase)
                ? Core.Localizer[key, displayBase]
                : Core.Localizer[key, displayBase, FormatDuration(itemTemplate.DurationSeconds)];

            if (!string.Equals(localized, key, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!string.IsNullOrWhiteSpace(itemTemplate.DisplayName))
        {
            return itemTemplate.DisplayName.Trim();
        }

        return ResolveBaseSkinName(itemTemplate);
    }

    private static string ResolveBaseSkinName(CustomWeaponItemTemplate itemTemplate)
    {
        if (!string.IsNullOrWhiteSpace(itemTemplate.SkinName))
        {
            return itemTemplate.SkinName.Trim();
        }

        var weapon = string.IsNullOrWhiteSpace(itemTemplate.Weapon) ? "weapon" : itemTemplate.Weapon.Trim();
        return $"{weapon} #{itemTemplate.PaintKit}";
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

    private static void NormalizeConfig(CustomWeaponsModuleConfig config)
    {
        config.Settings ??= new CustomWeaponsModuleSettings();
        config.Items ??= [];

        config.Settings.Category = string.IsNullOrWhiteSpace(config.Settings.Category)
            ? DefaultCategory
            : config.Settings.Category.Trim();

        if (config.Settings.PreviewDurationSeconds < 1f)
        {
            config.Settings.PreviewDurationSeconds = 8f;
        }

        if (config.Settings.PreviewCooldownSeconds < 0)
        {
            config.Settings.PreviewCooldownSeconds = 0;
        }
    }

    private static CustomWeaponsModuleConfig CreateDefaultConfig()
    {
        return new CustomWeaponsModuleConfig
        {
            Settings = new CustomWeaponsModuleSettings
            {
                Category = DefaultCategory,
                PreviewDurationSeconds = 8f,
                PreviewCooldownSeconds = 10
            },
            Items =
            [
                new CustomWeaponItemTemplate
                {
                    Id = "ak47_skin_hourly",
                    SkinName = "AK-47 Skin",
                    DisplayNameKey = "item.temporary.name",
                    Weapon = "weapon_ak47",
                    PaintKit = 711,
                    Wear = 0.08f,
                    Seed = 1,
                    Price = 3500,
                    SellPrice = 1750,
                    DurationSeconds = 3600,
                    Type = nameof(ShopItemType.Temporary),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    AllowPreview = true
                },
                new CustomWeaponItemTemplate
                {
                    Id = "deagle_skin_perm",
                    SkinName = "Deagle Skin",
                    DisplayNameKey = "item.permanent.name",
                    Weapon = "weapon_deagle",
                    PaintKit = 645,
                    Wear = 0.12f,
                    Seed = 7,
                    Price = 9000,
                    SellPrice = 4500,
                    DurationSeconds = 0,
                    Type = nameof(ShopItemType.Permanent),
                    Team = nameof(ShopItemTeam.Any),
                    Enabled = true,
                    CanBeSold = true,
                    AllowPreview = true
                }
            ]
        };
    }
}

internal readonly record struct CustomWeaponItemRuntime(
    string ItemId,
    string TargetWeaponKey,
    int PaintKit,
    float Wear,
    int Seed,
    int? StatTrakValue,
    int? EntityQualityOverride,
    ShopItemTeam Team,
    bool AllowPreview,
    string RequiredPermission
);

internal readonly record struct WeaponEconSnapshot(
    int FallbackPaintKit,
    int FallbackSeed,
    float FallbackWear,
    int FallbackStatTrak,
    uint AccountId,
    uint ItemIdHigh,
    uint ItemIdLow,
    int EntityQuality,
    bool Initialized
);

internal readonly record struct WeaponSkinPreviewState(
    long SessionId,
    string ItemId,
    float ExpiresAt
);

internal sealed class CustomWeaponsModuleConfig
{
    public CustomWeaponsModuleSettings Settings { get; set; } = new();
    public List<CustomWeaponItemTemplate> Items { get; set; } = [];
}

internal sealed class CustomWeaponsModuleSettings
{
    public bool UseCorePrefix { get; set; } = true;
    public string Category { get; set; } = "Visuals/Weapon Skins";
    public float PreviewDurationSeconds { get; set; } = 8f;
    public int PreviewCooldownSeconds { get; set; } = 10;
}

internal sealed class CustomWeaponItemTemplate
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public string SkinName { get; set; } = string.Empty;
    public string Weapon { get; set; } = string.Empty;
    public int PaintKit { get; set; }
    public float Wear { get; set; } = 0.1f;
    public int Seed { get; set; }
    public int? StatTrak { get; set; }
    public int? EntityQuality { get; set; }
    public int Price { get; set; }
    public int? SellPrice { get; set; }
    public int DurationSeconds { get; set; }
    public string Type { get; set; } = nameof(ShopItemType.Temporary);
    public string Team { get; set; } = nameof(ShopItemTeam.Any);
    public bool Enabled { get; set; } = true;
    public bool CanBeSold { get; set; } = true;
    public bool AllowPreview { get; set; } = true;
    public string RequiredPermission { get; set; } = string.Empty;
}
