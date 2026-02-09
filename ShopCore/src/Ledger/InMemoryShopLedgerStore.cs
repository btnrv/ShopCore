using ShopCore.Contract;

namespace ShopCore;

internal sealed class InMemoryShopLedgerStore : IShopLedgerStore
{
    private readonly object sync = new();
    private readonly LinkedList<ShopLedgerEntry> entries = [];
    private readonly int maxEntries;

    public InMemoryShopLedgerStore(int maxEntries)
    {
        this.maxEntries = maxEntries < 1 ? 1 : maxEntries;
    }

    public string Mode => "InMemory";

    public void Record(ShopLedgerEntry entry)
    {
        lock (sync)
        {
            entries.AddFirst(entry);
            while (entries.Count > maxEntries)
            {
                entries.RemoveLast();
            }
        }
    }

    public IReadOnlyCollection<ShopLedgerEntry> GetRecent(int maxEntries)
    {
        if (maxEntries <= 0)
        {
            return Array.Empty<ShopLedgerEntry>();
        }

        lock (sync)
        {
            return entries.Take(maxEntries).ToArray();
        }
    }

    public IReadOnlyCollection<ShopLedgerEntry> GetRecentForSteamId(ulong steamId, int maxEntries)
    {
        if (steamId == 0 || maxEntries <= 0)
        {
            return Array.Empty<ShopLedgerEntry>();
        }

        lock (sync)
        {
            return entries
                .Where(entry => entry.SteamId == steamId)
                .Take(maxEntries)
                .ToArray();
        }
    }

    public void Dispose()
    {
    }
}
