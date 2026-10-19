using System.Collections.Concurrent;

namespace Plugin.RockstarServer.Ros;

/// <summary>
/// Lets a diagnostic through the first time for each distinct key, and never
/// again.
/// </summary>
internal sealed class LogOnce(int capacity)
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private int _overflowReported;

    /// <summary>
    /// True the first time <paramref name="key"/> is seen, while there is room
    /// to remember it. <paramref name="onOverflow"/> runs once, the first time
    /// there is not.
    /// </summary>
    public bool First(string key, Action onOverflow)
    {
        if (_seen.ContainsKey(key))
            return false;
        if (_seen.Count >= capacity)
        {
            if (Interlocked.Exchange(ref _overflowReported, 1) == 0)
                onOverflow();
            return false;
        }
        return _seen.TryAdd(key, 0);
    }
}
