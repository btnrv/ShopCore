using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Blackjack",
    Name = "Shop Blackjack",
    Author = "OpenAI",
    Version = "1.0.0",
    Description = "ShopCore blackjack betting module"
)]
public class Shop_Blackjack : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Blackjack";
    private const string TemplateFileName = "blackjack_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string FallbackShopPrefix = "[gold]★[red] [Store][default]";
    private const string UiPrimary = "#CFE7FF";
    private const string UiSecondary = "#8EC5FF";
    private const string UiMuted = "#78A8D8";
    private const string UiAccent = "#58B5FF";
    private const string UiDealer = "#7FC8FF";
    private const string UiPlayer = "#B9E2FF";
    private const string UiCommand = "#6CC7FF";
    private const string UiCommandAccent = "#A6F0FF";
    private const string UiWin = "#7CFFB6";
    private const string UiDraw = "#FFE49A";
    private const string UiLose = "#FF9DA7";
    private const int DealerHiddenBackImageIndex = 53;

    private readonly Dictionary<ulong, BlackjackGame> activeGames = new();
    private readonly List<Guid> registeredCommands = new();
    private IShopCoreApiV2? shopApi;
    private BlackjackModuleConfig settings = new();

    public Shop_Blackjack(ISwiftlyCore core) : base(core) { }

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
            Core.Logger.LogWarning(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Blackjack commands will not be registered.");
            return;
        }

        LoadConfigAndRegisterCommands();
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null)
        {
            LoadConfigAndRegisterCommands();
        }
    }

    public override void Unload()
    {
        UnregisterCommands();
        activeGames.Clear();
    }

    private void LoadConfigAndRegisterCommands()
    {
        if (shopApi is null)
        {
            return;
        }

        var loaded = shopApi.LoadModuleConfig<BlackjackModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );

        var needsBootstrapConfig =
            !HasAnyNonEmpty(loaded.StartCommands) &&
            !HasAnyNonEmpty(loaded.HitCommands) &&
            !HasAnyNonEmpty(loaded.StandCommands);

        settings = loaded;
        NormalizeConfig(settings);

        if (needsBootstrapConfig)
        {
            settings = CreateDefaultConfig();
            _ = shopApi.SaveModuleConfig(
                ModulePluginId,
                settings,
                TemplateFileName,
                TemplateSectionName,
                overwrite: true
            );
        }

        UnregisterCommands();
        RegisterCommands();

        Core.Logger.LogInformation(
            "Shop_Blackjack initialized. StartCommands={StartCount}, HitCommands={HitCount}, StandCommands={StandCount}, MinBet={MinBet}, MaxBet={MaxBet}",
            settings.StartCommands.Count,
            settings.HitCommands.Count,
            settings.StandCommands.Count,
            settings.MinimumBet,
            settings.MaximumBet
        );
    }

    private void RegisterCommands()
    {
        RegisterCommandGroup(settings.StartCommands, HandleStartCommand);
        RegisterCommandGroup(settings.HitCommands, HandleHitCommand);
        RegisterCommandGroup(settings.StandCommands, HandleStandCommand);
    }

    private void RegisterCommandGroup(IEnumerable<string> commands, ICommandService.CommandListener handler)
    {
        foreach (var command in commands)
        {
            if (Core.Command.IsCommandRegistered(command))
            {
                Core.Logger.LogWarning("Cannot register blackjack command '{Command}' because it is already registered.", command);
                continue;
            }

            var id = Core.Command.RegisterCommand(
                command,
                handler,
                settings.RegisterAsRawCommands,
                settings.CommandPermission
            );

            registeredCommands.Add(id);
        }
    }

    private void UnregisterCommands()
    {
        foreach (var id in registeredCommands)
        {
            Core.Command.UnregisterCommand(id);
        }

        registeredCommands.Clear();
    }

    private void HandleStartCommand(ICommandContext context)
    {
        if (shopApi is null)
        {
            context.Reply("ShopCore API is unavailable.");
            return;
        }

        if (!settings.Enabled)
        {
            Reply(context, "blackjack.disabled");
            return;
        }

        if (!TryGetPlayablePlayer(context, out var player))
        {
            return;
        }

        if (context.Args.Length != 1)
        {
            Reply(context, "blackjack.usage", FormatCommandDisplay(settings.StartCommands.FirstOrDefault() ?? "blackjack"));
            return;
        }

        if (!int.TryParse(context.Args[0], out var parsedBet))
        {
            Reply(context, "blackjack.invalid_bet", settings.MinimumBet, settings.MaximumBet);
            return;
        }

        if (parsedBet < settings.MinimumBet)
        {
            Reply(context, "blackjack.min_bet", settings.MinimumBet);
            return;
        }

        if (parsedBet > settings.MaximumBet)
        {
            Reply(context, "blackjack.max_bet", settings.MaximumBet);
            return;
        }

        if (activeGames.ContainsKey(player.SteamID))
        {
            Reply(context, "blackjack.already_playing");
            RenderActiveGame(player);
            return;
        }

        var bet = (decimal)parsedBet;
        if (!shopApi.HasCredits(player, bet))
        {
            Reply(context, "blackjack.not_enough_credits", FormatCredits(shopApi.GetCredits(player)));
            return;
        }

        if (!shopApi.SubtractCredits(player, bet))
        {
            Reply(context, "blackjack.internal_error");
            return;
        }

        var game = new BlackjackGame { BetAmount = bet };
        game.Deck.AddRange(CreateDeck());
        game.PlayerHand.Add(DrawCard(game));
        game.PlayerHand.Add(DrawCard(game));
        game.DealerHand.Add(DrawCard(game));

        activeGames[player.SteamID] = game;

        Reply(context, "blackjack.started", FormatCredits(bet));
        RenderActiveGame(player);
    }

    private void HandleHitCommand(ICommandContext context)
    {
        if (!TryGetPlayablePlayer(context, out var player))
        {
            return;
        }

        if (!activeGames.TryGetValue(player.SteamID, out var game))
        {
            Reply(context, "blackjack.no_active_game");
            return;
        }

        game.PlayerHand.Add(DrawCard(game));
        var total = CalculateHand(game.PlayerHand);

        if (total > 21)
        {
            FinalizeGame(player, game, BlackjackOutcome.Bust);
            return;
        }

        Reply(context, "blackjack.hit");
        RenderActiveGame(player);
    }

    private void HandleStandCommand(ICommandContext context)
    {
        if (shopApi is null)
        {
            context.Reply("ShopCore API is unavailable.");
            return;
        }

        if (!TryGetPlayablePlayer(context, out var player))
        {
            return;
        }

        if (!activeGames.TryGetValue(player.SteamID, out var game))
        {
            Reply(context, "blackjack.no_active_game");
            return;
        }

        while (CalculateHand(game.DealerHand) < 17)
        {
            game.DealerHand.Add(DrawCard(game));
        }

        var playerTotal = CalculateHand(game.PlayerHand);
        var dealerTotal = CalculateHand(game.DealerHand);
        var outcome = dealerTotal > 21 || (playerTotal <= 21 && playerTotal > dealerTotal)
            ? BlackjackOutcome.Win
            : playerTotal == dealerTotal
                ? BlackjackOutcome.Draw
                : BlackjackOutcome.Lose;

        Reply(context, "blackjack.stand");
        FinalizeGame(player, game, outcome);
    }

    private void FinalizeGame(IPlayer player, BlackjackGame game, BlackjackOutcome outcome)
    {
        if (shopApi is null)
        {
            return;
        }

        if (!activeGames.Remove(player.SteamID))
        {
            return;
        }

        decimal payout = 0m;
        string resultTitleKey;
        string chatResultKey;
        string resultColor;

        switch (outcome)
        {
            case BlackjackOutcome.Win:
                payout = game.BetAmount * 2m;
                resultTitleKey = "blackjack.ui.result_win";
                chatResultKey = "blackjack.won";
                resultColor = UiWin;
                break;
            case BlackjackOutcome.Draw:
                payout = game.BetAmount;
                resultTitleKey = "blackjack.ui.result_draw";
                chatResultKey = "blackjack.draw";
                resultColor = UiDraw;
                break;
            case BlackjackOutcome.Bust:
                resultTitleKey = "blackjack.ui.result_bust";
                chatResultKey = "blackjack.bust";
                resultColor = UiLose;
                break;
            default:
                resultTitleKey = "blackjack.ui.result_lose";
                chatResultKey = "blackjack.lost";
                resultColor = UiLose;
                break;
        }

        var payoutApplied = true;
        decimal? balanceBeforePayout = null;
        if (payout > 0m)
        {
            balanceBeforePayout = shopApi.GetCredits(player);
            payoutApplied = shopApi.AddCredits(player, payout);
            if (!payoutApplied)
            {
                Core.Logger.LogError(
                    "Blackjack payout failed for player {SteamId}. Outcome={Outcome}, Bet={Bet}, Payout={Payout}",
                    player.SteamID,
                    outcome,
                    game.BetAmount,
                    payout
                );
            }
        }

        if (!payoutApplied)
        {
            Reply(player, "blackjack.payout_failed");
            player.SendCenterHTML(
                BuildGameHtml(player, game, revealDealer: true, "blackjack.ui.result_error", "blackjack.ui.result_error_desc", UiLose),
                settings.EndHudDurationMs
            );
            return;
        }

        var balance = payout > 0m && balanceBeforePayout.HasValue
            ? balanceBeforePayout.Value + payout
            : shopApi.GetCredits(player);

        switch (outcome)
        {
            case BlackjackOutcome.Win:
                Reply(player, chatResultKey, FormatCredits(payout), FormatCredits(balance));
                break;
            case BlackjackOutcome.Draw:
                Reply(player, chatResultKey, FormatCredits(game.BetAmount), FormatCredits(balance));
                break;
            case BlackjackOutcome.Bust:
                Reply(player, chatResultKey, FormatCredits(balance));
                break;
            default:
                Reply(player, chatResultKey, FormatCredits(balance));
                break;
        }

        player.SendCenterHTML(
            BuildGameHtml(
                player,
                game,
                revealDealer: true,
                resultTitleKey,
                outcome == BlackjackOutcome.Win
                    ? "blackjack.ui.result_win_desc"
                    : outcome == BlackjackOutcome.Draw
                        ? "blackjack.ui.result_draw_desc"
                        : outcome == BlackjackOutcome.Bust
                            ? "blackjack.ui.result_bust_desc"
                            : "blackjack.ui.result_lose_desc",
                resultColor
            ),
            settings.EndHudDurationMs
        );
    }

    private void RenderActiveGame(IPlayer player)
    {
        if (!player.IsValid)
        {
            return;
        }

        if (!activeGames.TryGetValue(player.SteamID, out var game))
        {
            return;
        }

        player.SendCenterHTML(BuildGameHtml(player, game, revealDealer: false, null, null, null), settings.ActiveHudDurationMs);
    }

    private string BuildGameHtml(
        IPlayer player,
        BlackjackGame game,
        bool revealDealer,
        string? resultTitleKey,
        string? resultBodyKey,
        string? resultColor)
    {
        var loc = Core.Translation.GetPlayerLocalizer(player);
        var title = loc["blackjack.ui.title"];
        var betLabel = loc["blackjack.ui.bet"];
        var dealerTotalLabel = loc["blackjack.ui.dealer_total"];
        var playerTotalLabel = loc["blackjack.ui.player_total"];
        var commandsLabel = loc["blackjack.ui.commands"];
        var hitLabel = loc["blackjack.ui.hit"];
        var standLabel = loc["blackjack.ui.stand"];
        var hiddenLabel = loc["blackjack.ui.hidden"];
        var serverName = EscapeHtml(settings.ServerName);
        var serverIp = EscapeHtml(settings.ServerIp);

        var playerTotal = CalculateHand(game.PlayerHand);
        var dealerTotal = CalculateHand(game.DealerHand);
        var dealerVisibleTotal = revealDealer ? dealerTotal : CalculateHand([game.DealerHand[0]]);

        var playerCards = BuildCardsHtml(game.PlayerHand);
        var dealerCards = revealDealer
            ? BuildCardsHtml(game.DealerHand)
            : BuildHiddenDealerCardsHtml(game.DealerHand[0]);

        var hitCommand = EscapeHtml(FormatCommandDisplay(settings.HitCommands.FirstOrDefault() ?? "hit"));
        var standCommand = EscapeHtml(FormatCommandDisplay(settings.StandCommands.FirstOrDefault() ?? "stand"));

        var sb = new StringBuilder(1536);
        sb.Append("<center>");
        sb.Append("<span class=\"fontSize-sm\" color=\"").Append(UiMuted).Append("\">").Append(serverName).Append("</span><br>");
        sb.Append("<span class=\"fontSize-l fontWeight-bold\" color=\"").Append(UiPrimary).Append("\">").Append(EscapeHtml(title)).Append("</span>");
        sb.Append(" <span class=\"fontSize-sm\" color=\"").Append(UiAccent).Append("\">(").Append(EscapeHtml(betLabel)).Append(": ").Append(EscapeHtml(FormatCredits(game.BetAmount))).Append(")</span><br>");

        sb.Append(dealerCards).Append("<br>");
        sb.Append("<span class=\"fontSize-sm\" color=\"").Append(UiLose).Append("\">")
            .Append(EscapeHtml(dealerTotalLabel)).Append(": ").Append(EscapeHtml(revealDealer ? dealerTotal.ToString() : $"{dealerVisibleTotal} {hiddenLabel}"))
            .Append("</span><br>");

        sb.Append(playerCards).Append("<br>");
        sb.Append("<span class=\"fontSize-sm\" color=\"").Append(UiPrimary).Append("\">")
            .Append(EscapeHtml(playerTotalLabel)).Append(": ").Append(EscapeHtml(playerTotal.ToString()))
            .Append("</span>");

        if (string.IsNullOrWhiteSpace(resultTitleKey))
        {
            sb.Append("<br><span class=\"fontSize-sm\" color=\"").Append(UiCommand).Append("\">")
                .Append(EscapeHtml(commandsLabel)).Append(": ")
                .Append(EscapeHtml(hitLabel)).Append(" ")
                .Append("<span color=\"").Append(UiCommandAccent).Append("\">(").Append(hitCommand).Append(")</span>")
                .Append(" / ")
                .Append(EscapeHtml(standLabel)).Append(" ")
                .Append("<span color=\"").Append(UiCommandAccent).Append("\">(").Append(standCommand).Append(")</span>")
                .Append("</span>");
        }
        else
        {
            var resolvedResultTitle = EscapeHtml(loc[resultTitleKey]);
            var resolvedResultBody = string.IsNullOrWhiteSpace(resultBodyKey) ? string.Empty : EscapeHtml(loc[resultBodyKey]);

            sb.Append("<br><span class=\"fontSize-l fontWeight-bold\" color=\"").Append(resultColor ?? UiPrimary).Append("\">")
                .Append(resolvedResultTitle)
                .Append("</span>");

            if (!string.IsNullOrWhiteSpace(resolvedResultBody))
            {
                sb.Append("<br><span class=\"fontSize-sm\" color=\"").Append(UiSecondary).Append("\">")
                    .Append(resolvedResultBody)
                    .Append("</span>");
            }
        }

        sb.Append("<br><span color=\"").Append(UiMuted).Append("\">").Append(serverIp).Append("</span>");
        sb.Append("</center>");
        return sb.ToString();
    }

    private string BuildCardsHtml(IReadOnlyList<BlackjackCard> hand)
    {
        if (!settings.ShowCardImages)
        {
            return BuildCardTextHtml(hand);
        }

        var sb = new StringBuilder(512);
        for (var i = 0; i < hand.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append("<img src=\"")
                .Append(EscapeHtml(GetCardImageUrl(hand[i])))
                .Append("\" width=\"46\" height=\"64\">");
        }

        return sb.ToString();
    }

    private string BuildHiddenDealerCardsHtml(BlackjackCard visibleCard)
    {
        if (!settings.ShowCardImages)
        {
            return BuildCardTextHtml([visibleCard]) + " <span color=\"" + UiMuted + "\">[Hidden]</span>";
        }

        return
            "<img src=\"" + EscapeHtml(GetCardImageUrl(visibleCard)) + "\" width=\"46\" height=\"64\">" +
            " " +
            "<img src=\"" + EscapeHtml(GetCardBackImageUrl()) + "\" width=\"46\" height=\"64\">";
    }

    private string BuildCardTextHtml(IReadOnlyList<BlackjackCard> hand)
    {
        if (hand.Count == 0)
        {
            return "<span color=\"" + UiMuted + "\">-</span>";
        }

        var joined = string.Join(" ", hand.Select(card => $"{card.Rank}{card.Suit}"));
        return "<span color=\"" + UiSecondary + "\">" + EscapeHtml(joined) + "</span>";
    }

    private static List<BlackjackCard> CreateDeck()
    {
        string[] suits = ["♣", "♦", "♥", "♠"];
        string[] ranks = ["A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K"];

        var deck = new List<BlackjackCard>(52);
        foreach (var suit in suits)
        {
            foreach (var rank in ranks)
            {
                deck.Add(new BlackjackCard(rank, suit));
            }
        }

        Shuffle(deck);
        return deck;
    }

    private static void Shuffle(List<BlackjackCard> deck)
    {
        for (var i = deck.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    private static BlackjackCard DrawCard(BlackjackGame game)
    {
        if (game.Deck.Count == 0)
        {
            game.Deck.AddRange(CreateDeck());
        }

        var card = game.Deck[0];
        game.Deck.RemoveAt(0);
        return card;
    }

    private static int CalculateHand(IReadOnlyCollection<BlackjackCard> hand)
    {
        var total = 0;
        var aces = 0;

        foreach (var card in hand)
        {
            switch (card.Rank)
            {
                case "A":
                    total += 11;
                    aces++;
                    break;
                case "K":
                case "Q":
                case "J":
                    total += 10;
                    break;
                default:
                    total += int.Parse(card.Rank);
                    break;
            }
        }

        while (total > 21 && aces > 0)
        {
            total -= 10;
            aces--;
        }

        return total;
    }

    private string GetCardImageUrl(BlackjackCard card)
    {
        var baseUrl = settings.CardImageBaseUrl.TrimEnd('/');
        return $"{baseUrl}/{GetCardImageIndex(card)}.jpg";
    }

    private string GetCardBackImageUrl()
    {
        var baseUrl = settings.CardImageBaseUrl.TrimEnd('/');
        return $"{baseUrl}/{DealerHiddenBackImageIndex}.jpg";
    }

    private static int GetCardImageIndex(BlackjackCard card)
    {
        var suitIndex = card.Suit switch
        {
            "♣" => 0,
            "♦" => 1,
            "♥" => 2,
            "♠" => 3,
            _ => 0
        };

        var rankIndex = card.Rank switch
        {
            "A" => 0,
            "2" => 1,
            "3" => 2,
            "4" => 3,
            "5" => 4,
            "6" => 5,
            "7" => 6,
            "8" => 7,
            "9" => 8,
            "10" => 9,
            "J" => 10,
            "Q" => 11,
            "K" => 12,
            _ => 0
        };

        return (suitIndex * 13) + rankIndex + 1;
    }

    private bool TryGetPlayablePlayer(ICommandContext context, out IPlayer player)
    {
        if (context.Sender is not IPlayer p || !p.IsValid || p.IsFakeClient)
        {
            context.Reply("This command is available only in-game.");
            player = null!;
            return false;
        }

        player = p;
        return true;
    }

    private void Reply(ICommandContext context, string key, params object[] args)
    {
        var player = context.Sender as IPlayer;
        var message = BuildPrefixedMessage(player, key, args);

        if (player is not null && player.IsValid)
        {
            player.SendChat(message);
            return;
        }

        context.Reply(message);
    }

    private void Reply(IPlayer player, string key, params object[] args)
    {
        if (!player.IsValid)
        {
            return;
        }

        player.SendChat(BuildPrefixedMessage(player, key, args));
    }

    private string BuildPrefixedMessage(IPlayer? player, string key, params object[] args)
    {
        string body;
        string prefix;

        if (player is not null && player.IsValid)
        {
            var loc = Core.Translation.GetPlayerLocalizer(player);
            body = args.Length == 0 ? loc[key] : loc[key, args];
            prefix = ResolvePrefix(player);
            return $"{prefix} {body}";
        }

        body = args.Length == 0 ? Core.Localizer[key] : Core.Localizer[key, args];
        prefix = Core.Localizer["shop.prefix"];
        if (string.IsNullOrWhiteSpace(prefix) || string.Equals(prefix, "shop.prefix", StringComparison.Ordinal))
        {
            prefix = FallbackShopPrefix;
        }

        return $"{prefix} {body}";
    }

    private string ResolvePrefix(IPlayer player)
    {
        if (settings.UseCorePrefix)
        {
            var corePrefix = shopApi?.GetShopPrefix(player);
            if (!string.IsNullOrWhiteSpace(corePrefix))
            {
                return corePrefix;
            }
        }

        var loc = Core.Translation.GetPlayerLocalizer(player);
        var prefix = loc["shop.prefix"];
        if (string.IsNullOrWhiteSpace(prefix) || string.Equals(prefix, "shop.prefix", StringComparison.Ordinal))
        {
            return FallbackShopPrefix;
        }

        return prefix;
    }

    private string FormatCommandDisplay(string command)
    {
        var normalized = (command ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return settings.RegisterAsRawCommands ? "blackjack" : "!blackjack";
        }

        if (settings.RegisterAsRawCommands)
        {
            return normalized;
        }

        return normalized[0] is '!' or '/' ? normalized : $"!{normalized}";
    }

    private static string FormatCredits(decimal amount)
    {
        return decimal.Truncate(amount) == amount
            ? decimal.Truncate(amount).ToString()
            : amount.ToString("0.##");
    }

    private static string EscapeHtml(string? value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static bool HasAnyNonEmpty(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    private static void NormalizeConfig(BlackjackModuleConfig config)
    {
        config.StartCommands = NormalizeCommandList(config.StartCommands, ["blackjack", "bj"]);
        config.HitCommands = NormalizeCommandList(config.HitCommands, ["hit"]);
        config.StandCommands = NormalizeCommandList(config.StandCommands, ["stand"]);

        if (config.MinimumBet < 1)
        {
            config.MinimumBet = 1;
        }

        if (config.MaximumBet < config.MinimumBet)
        {
            config.MaximumBet = config.MinimumBet;
        }

        config.ActiveHudDurationMs = Math.Clamp(config.ActiveHudDurationMs, 1000, 60000);
        config.EndHudDurationMs = Math.Clamp(config.EndHudDurationMs, 1000, 15000);
        config.CommandPermission ??= string.Empty;
        config.ServerName = string.IsNullOrWhiteSpace(config.ServerName) ? "Your Server Name" : config.ServerName.Trim();
        config.ServerIp = string.IsNullOrWhiteSpace(config.ServerIp) ? "127.0.0.1:27015" : config.ServerIp.Trim();
        config.CardImageBaseUrl = string.IsNullOrWhiteSpace(config.CardImageBaseUrl)
            ? "https://raw.githubusercontent.com/vulikit/varkit-resources/refs/heads/main/bj"
            : config.CardImageBaseUrl.Trim().TrimEnd('/');
    }

    private static List<string> NormalizeCommandList(List<string>? values, IEnumerable<string> fallback)
    {
        var normalized = (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return fallback.ToList();
    }

    private static BlackjackModuleConfig CreateDefaultConfig()
    {
        return new BlackjackModuleConfig
        {
            UseCorePrefix = true,
            Enabled = true,
            StartCommands = ["blackjack", "bj"],
            HitCommands = ["hit"],
            StandCommands = ["stand"],
            RegisterAsRawCommands = false,
            CommandPermission = string.Empty,
            MinimumBet = 100,
            MaximumBet = 999999,
            ActiveHudDurationMs = 12000,
            EndHudDurationMs = 3500,
            ServerName = "Your Server Name",
            ServerIp = "127.0.0.1:27015",
            ShowCardImages = true,
            CardImageBaseUrl = "https://raw.githubusercontent.com/vulikit/varkit-resources/refs/heads/main/bj"
        };
    }
}
