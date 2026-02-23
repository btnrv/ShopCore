namespace ShopCore;

internal sealed class QuizModuleConfig
{
    public bool UseCorePrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool AskFirstQuestionImmediately { get; set; } = true;
    public bool SkipWhenNoHumanPlayers { get; set; } = true;
    public bool BlockCorrectAnswerChatMessage { get; set; } = true;
    public float QuestionIntervalSeconds { get; set; } = 30f;
    public int MinimumRewardCredits { get; set; } = 5;
    public int MaximumRewardCredits { get; set; } = 25;
    public int MinimumOperand { get; set; } = 1;
    public int MaximumOperand { get; set; } = 20;
    public List<string> Operators { get; set; } = ["+", "-", "x", "/"];
}
