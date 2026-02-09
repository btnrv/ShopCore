using ShopCore.Contract;

namespace ShopCore;

internal interface IShopLedgerStore : IDisposable
{
    string Mode { get; }

    void Record(ShopLedgerEntry entry);

    IReadOnlyCollection<ShopLedgerEntry> GetRecent(int maxEntries);

    IReadOnlyCollection<ShopLedgerEntry> GetRecentForSteamId(ulong steamId, int maxEntries);
}
