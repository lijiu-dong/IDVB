namespace IDVBuff.Features.Maps;

// Only producers are coalesced. There is no retained Task<T> or resource graph
// after the last caller leaves, and consumers always own independent readers.
internal static class PreparationGate
{
    private sealed class Entry { internal readonly object Sync = new(); internal int Users; }
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    internal static IDisposable Enter(string key)
    {
        Entry entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new());
            entry.Users++;
        }
        Monitor.Enter(entry.Sync);
        return new Scope(key, entry);
    }
    private sealed class Scope(string key, Entry entry) : IDisposable
    {
        private int _closed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Monitor.Exit(entry.Sync);
            lock (Gate) { if (--entry.Users == 0) Entries.Remove(key); }
        }
    }
}
