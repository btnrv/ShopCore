namespace ShopCore;

internal readonly record struct BlackjackCard(string Rank, string Suit);

internal sealed class BlackjackGame
{
    public List<BlackjackCard> Deck { get; } = [];
    public List<BlackjackCard> PlayerHand { get; } = [];
    public List<BlackjackCard> DealerHand { get; } = [];
    public decimal BetAmount { get; init; }
}

internal enum BlackjackOutcome
{
    Win,
    Draw,
    Lose,
    Bust
}
