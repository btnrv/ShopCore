using Microsoft.Extensions.Logging;
using ShopCore.Contract;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace ShopCore;

[PluginMetadata(
    Id = "Shop_Quiz",
    Name = "Shop Quiz",
    Author = "Codex",
    Version = "1.0.0",
    Description = "ShopCore math quiz module"
)]
public class Shop_Quiz : BasePlugin
{
    private const string ShopCoreInterfaceKey = "ShopCore.API.v2";
    private const string ModulePluginId = "Shop_Quiz";
    private const string TemplateFileName = "quiz_config.jsonc";
    private const string TemplateSectionName = "Main";
    private const string FallbackShopPrefix = "[gold]★[red] [Store][default]";
    private const float RewardFlushIntervalSeconds = 0.25f;

    private readonly object stateLock = new();
    private readonly QuizQuestionGenerator questionGenerator = new();
    private readonly List<QuizOperator> enabledOperators = [];
    private readonly Queue<PendingQuizReward> pendingRewards = new();
    private const int MaxSafeOperand = 100000;
    private IShopCoreApiV2? shopApi;
    private QuizModuleConfig config = new();
    private Guid? clientChatHookId;
    private CancellationTokenSource? quizLoopTimer;
    private CancellationTokenSource? rewardFlushTimer;
    private QuizQuestion? activeQuestion;
    private int nextQuestionId;
    private int runtimeVersion;

    public Shop_Quiz(ISwiftlyCore core) : base(core) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        var resolvedApi = default(IShopCoreApiV2);

        if (interfaceManager.HasSharedInterface(ShopCoreInterfaceKey))
        {
            try
            {
                resolvedApi = interfaceManager.GetSharedInterface<IShopCoreApiV2>(ShopCoreInterfaceKey);
            }
            catch (Exception ex)
            {
                Core.Logger.LogWarning(ex, "Failed to resolve shared interface '{InterfaceKey}'.", ShopCoreInterfaceKey);
            }
        }

        shopApi = resolvedApi;

        if (shopApi is null)
        {
            StopRuntime();
        }
    }

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager)
    {
        if (shopApi is null)
        {
            Core.Logger.LogWarning("ShopCore API is not available. Quiz module will stay idle.");
            return;
        }

        ReloadRuntime();
    }

    public override void Load(bool hotReload)
    {
        if (shopApi is not null)
        {
            ReloadRuntime();
        }
    }

    public override void Unload()
    {
        lock (stateLock)
        {
            pendingRewards.Clear();
        }
        StopRuntime();
    }

    private void ReloadRuntime()
    {
        if (shopApi is null)
        {
            return;
        }

        StopRuntime();

        config = shopApi.LoadModuleConfig<QuizModuleConfig>(
            ModulePluginId,
            TemplateFileName,
            TemplateSectionName
        );

        NormalizeConfig(config, enabledOperators);

        EnsureClientChatHook();
        EnsureRewardFlushTimer();
        RestartQuizLoop();

        Core.Logger.LogInformation(
            "Shop_Quiz initialized. Enabled={Enabled}, Interval={Interval}s, Rewards={MinReward}-{MaxReward}, Operands={MinOperand}-{MaxOperand}, Operators={Operators}",
            config.Enabled,
            config.QuestionIntervalSeconds,
            config.MinimumRewardCredits,
            config.MaximumRewardCredits,
            config.MinimumOperand,
            config.MaximumOperand,
            string.Join(",", enabledOperators)
        );
    }

    private void EnsureClientChatHook()
    {
        if (clientChatHookId is not null)
        {
            return;
        }

        clientChatHookId = Core.Command.HookClientChat(OnClientChat);
    }

    private void RestartQuizLoop()
    {
        CancellationTokenSource? previousTimer;
        var version = 0;

        lock (stateLock)
        {
            runtimeVersion++;
            version = runtimeVersion;
            activeQuestion = null;
            nextQuestionId = 0;
            previousTimer = quizLoopTimer;
            quizLoopTimer = null;
        }

        CancelTimer(previousTimer);

        if (!CanRunQuiz())
        {
            return;
        }

        var interval = config.QuestionIntervalSeconds;
        var timer = config.AskFirstQuestionImmediately
            ? Core.Scheduler.RepeatBySeconds(interval, () => QuizLoopTick(version))
            : Core.Scheduler.DelayAndRepeatBySeconds(interval, interval, () => QuizLoopTick(version));

        CancellationTokenSource? timerToCancel = null;
        lock (stateLock)
        {
            if (version != runtimeVersion)
            {
                timerToCancel = timer;
            }
            else
            {
                quizLoopTimer = timer;
            }
        }

        CancelTimer(timerToCancel);
    }

    private void StopRuntime()
    {
        CancellationTokenSource? timer;
        CancellationTokenSource? flushTimer;
        Guid? hookId;

        lock (stateLock)
        {
            runtimeVersion++;
            activeQuestion = null;
            nextQuestionId = 0;

            timer = quizLoopTimer;
            quizLoopTimer = null;
            flushTimer = rewardFlushTimer;
            rewardFlushTimer = null;

            hookId = clientChatHookId;
            clientChatHookId = null;
        }

        CancelTimer(timer);
        CancelTimer(flushTimer);

        if (hookId is Guid chatHookId)
        {
            Core.Command.UnhookClientChat(chatHookId);
        }
    }

    private void EnsureRewardFlushTimer()
    {
        lock (stateLock)
        {
            if (rewardFlushTimer is not null)
            {
                return;
            }
        }

        var timer = Core.Scheduler.RepeatBySeconds(RewardFlushIntervalSeconds, FlushPendingRewards);

        CancellationTokenSource? timerToCancel = null;
        lock (stateLock)
        {
            if (rewardFlushTimer is not null)
            {
                timerToCancel = timer;
            }
            else
            {
                rewardFlushTimer = timer;
            }
        }

        CancelTimer(timerToCancel);
    }

    private void QuizLoopTick(int version)
    {
        if (version != Volatile.Read(ref runtimeVersion))
        {
            return;
        }

        if (shopApi is null || !config.Enabled || enabledOperators.Count == 0)
        {
            return;
        }

        if (config.SkipWhenNoHumanPlayers && !HasHumanPlayersOnline())
        {
            lock (stateLock)
            {
                if (version == runtimeVersion)
                {
                    activeQuestion = null;
                }
            }

            return;
        }

        QuizQuestion question;
        lock (stateLock)
        {
            if (version != runtimeVersion)
            {
                return;
            }

            nextQuestionId++;
            question = questionGenerator.CreateQuestion(nextQuestionId, config, enabledOperators);
            activeQuestion = question;
        }

        Broadcast("module.quiz.question", question.Expression, question.RewardCredits);
    }

    private HookResult OnClientChat(int playerId, string text, bool teamonly)
    {
        if (shopApi is null || !config.Enabled)
        {
            return HookResult.Continue;
        }

        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player is null || !player.IsValid || player.IsFakeClient)
        {
            return HookResult.Continue;
        }

        if (!TryParseAnswer(text, out var answer))
        {
            return HookResult.Continue;
        }

        QuizQuestion wonQuestion;
        lock (stateLock)
        {
            if (activeQuestion is null)
            {
                return HookResult.Continue;
            }

            if (answer != activeQuestion.Answer)
            {
                return HookResult.Continue;
            }

            wonQuestion = activeQuestion;
            activeQuestion = null;
        }

        QueueWinnerReward(player, wonQuestion);

        return config.BlockCorrectAnswerChatMessage ? HookResult.Stop : HookResult.Continue;
    }

    private void QueueWinnerReward(IPlayer player, QuizQuestion question)
    {
        if (!player.IsValid || player.IsFakeClient)
        {
            return;
        }

        lock (stateLock)
        {
            pendingRewards.Enqueue(new PendingQuizReward(player, question.Id, question.RewardCredits, GetPlayerName(player)));
        }
    }

    private void FlushPendingRewards()
    {
        if (shopApi is null)
        {
            return;
        }

        List<PendingQuizReward> batch;
        lock (stateLock)
        {
            if (pendingRewards.Count == 0)
            {
                return;
            }

            batch = [];
            while (pendingRewards.Count > 0)
            {
                batch.Add(pendingRewards.Dequeue());
            }
        }

        var groups = new Dictionary<ulong, PendingQuizRewardGroup>();
        foreach (var reward in batch)
        {
            if (!groups.TryGetValue(reward.Player.SteamID, out var group))
            {
                group = new PendingQuizRewardGroup(reward.Player);
                groups[reward.Player.SteamID] = group;
            }

            group.Rewards.Add(reward);
            group.TotalCredits += reward.RewardCredits;
        }

        foreach (var group in groups.Values)
        {
            var player = group.Player;
            if (!player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            var balanceBeforeReward = shopApi.GetCredits(player);
            if (!shopApi.AddCredits(player, group.TotalCredits))
            {
                foreach (var reward in group.Rewards)
                {
                    SendToPlayer(player, "module.quiz.reward_failed", reward.RewardCredits);
                    Core.Logger.LogWarning(
                        "Shop_Quiz failed to add queued reward credits. PlayerId={PlayerId}, SteamId={SteamId}, QuestionId={QuestionId}, Reward={Reward}",
                        player.PlayerID,
                        player.SteamID,
                        reward.QuestionId,
                        reward.RewardCredits
                    );
                }

                continue;
            }

            var runningBalance = balanceBeforeReward;
            foreach (var reward in group.Rewards)
            {
                runningBalance += reward.RewardCredits;
                Broadcast("module.quiz.winner", reward.WinnerName, reward.RewardCredits, runningBalance);
            }
        }
    }

    private bool HasHumanPlayersOnline()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.IsValid && !player.IsFakeClient)
            {
                return true;
            }
        }

        return false;
    }

    private void Broadcast(string key, params object[] args)
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            var loc = Core.Translation.GetPlayerLocalizer(player);
            player.SendChat($"{GetPrefix(player)} {loc[key, args]}");
        }
    }

    private void SendToPlayer(IPlayer player, string key, params object[] args)
    {
        if (!player.IsValid || player.IsFakeClient)
        {
            return;
        }

        var loc = Core.Translation.GetPlayerLocalizer(player);
        player.SendChat($"{GetPrefix(player)} {loc[key, args]}");
    }

    private string GetPrefix(IPlayer player)
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
        var prefix = loc["shop.prefix"];
        if (string.IsNullOrWhiteSpace(prefix) || string.Equals(prefix, "shop.prefix", StringComparison.Ordinal))
        {
            return FallbackShopPrefix;
        }

        return prefix;
    }

    private static string GetPlayerName(IPlayer player)
    {
        var name = player.Controller.PlayerName;
        return string.IsNullOrWhiteSpace(name) ? $"#{player.PlayerID}" : name;
    }

    private bool CanRunQuiz()
    {
        return shopApi is not null && config.Enabled && enabledOperators.Count > 0;
    }

    private static void CancelTimer(CancellationTokenSource? timer)
    {
        if (timer is null)
        {
            return;
        }

        try
        {
            timer.Cancel();
        }
        catch
        {
        }

        timer.Dispose();
    }

    private static bool TryParseAnswer(string text, out int answer)
    {
        answer = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.Length == 0 || trimmed.Length > 16)
        {
            return false;
        }

        if (!long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        answer = (int)parsed;
        return true;
    }

    private static void NormalizeConfig(QuizModuleConfig config, List<QuizOperator> enabledOperators)
    {
        config.QuestionIntervalSeconds = Math.Max(5f, config.QuestionIntervalSeconds);
        config.MinimumRewardCredits = Math.Max(1, config.MinimumRewardCredits);
        if (config.MaximumRewardCredits < config.MinimumRewardCredits)
        {
            config.MaximumRewardCredits = config.MinimumRewardCredits;
        }

        config.MinimumOperand = Math.Max(0, config.MinimumOperand);
        config.MaximumOperand = Math.Max(config.MinimumOperand, config.MaximumOperand);
        config.MinimumOperand = Math.Min(config.MinimumOperand, MaxSafeOperand);
        config.MaximumOperand = Math.Min(config.MaximumOperand, MaxSafeOperand);
        config.Operators ??= [];

        enabledOperators.Clear();

        foreach (var raw in config.Operators)
        {
            if (TryParseOperator(raw, out var op) && !enabledOperators.Contains(op))
            {
                enabledOperators.Add(op);
            }
        }

        if (enabledOperators.Count == 0)
        {
            enabledOperators.AddRange([QuizOperator.Add, QuizOperator.Subtract, QuizOperator.Multiply, QuizOperator.Divide]);
        }

        if (enabledOperators.Contains(QuizOperator.Divide) && config.MaximumOperand < 1)
        {
            config.MaximumOperand = 1;
        }

        config.Operators = enabledOperators.Select(ToConfigOperator).ToList();
    }

    private static bool TryParseOperator(string? raw, out QuizOperator op)
    {
        switch (raw?.Trim())
        {
            case "+":
                op = QuizOperator.Add;
                return true;
            case "-":
                op = QuizOperator.Subtract;
                return true;
            case "x":
            case "X":
            case "*":
                op = QuizOperator.Multiply;
                return true;
            case "/":
                op = QuizOperator.Divide;
                return true;
            default:
                op = default;
                return false;
        }
    }

    private static string ToConfigOperator(QuizOperator op)
    {
        return op switch
        {
            QuizOperator.Add => "+",
            QuizOperator.Subtract => "-",
            QuizOperator.Multiply => "x",
            QuizOperator.Divide => "/",
            _ => "+"
        };
    }
}

internal sealed record PendingQuizReward(IPlayer Player, int QuestionId, int RewardCredits, string WinnerName);

internal sealed class PendingQuizRewardGroup(IPlayer player)
{
    public IPlayer Player { get; } = player;
    public int TotalCredits { get; set; }
    public List<PendingQuizReward> Rewards { get; } = [];
}
