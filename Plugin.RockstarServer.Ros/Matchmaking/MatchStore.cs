namespace Plugin.RockstarServer.Ros.Matchmaking;

/// <summary>
/// One advertised match, as the host last described it.
/// </summary>
/// <param name="Id">The MatchId handed to the host by Advertise.</param>
/// <param name="NumSlots">Total slots.</param>
/// <param name="AvailableSlots">Free slots; Find only offers a match with at least as many as the seeker asks for.</param>
/// <param name="Attributes">The numeric attributes from attrsJson (GAME_MODE, MMATTR_*).</param>
/// <param name="SessionInfo">
/// attrsJson's "Data": base64 of the host's session info, the blob a joining
/// client reads from a Find result. Null if the host sent none, in which case
/// the match is never offered.
/// </param>
internal sealed record Match(
    Guid Id,
    int NumSlots,
    int AvailableSlots,
    IReadOnlyDictionary<string, long> Attributes,
    string? SessionInfo,
    DateTimeOffset LastSeen);

/// <summary>
/// The live matches of one ROS title, held in memory. The game server is the
/// source of truth for a session (Playforward's docs/session-sync-and-moderation.md);
/// nothing here outlives a restart, and a host that is still running recreates
/// its match with its next Update (see <see cref="Update"/>).
///
/// The MatchId is the only credential: Advertise returns it to the host alone,
/// Find hands out session info but never ids, and a random GUID cannot be
/// guessed. The Social Club ticket is not used, because it is Rockstar's to
/// refresh and a refreshed one would lock a host out of its own match.
/// </summary>
internal sealed class MatchStore(MatchmakingSettings settings, TimeProvider clock)
{
    private readonly Dictionary<Guid, Match> _matches = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) { Sweep(); return _matches.Count; } }
    }

    /// <summary>A new match, or null when the store is full.</summary>
    public Match? Advertise(int numSlots, int availableSlots, IReadOnlyDictionary<string, long> attributes, string? sessionInfo)
    {
        lock (_lock)
        {
            Sweep();
            if (_matches.Count >= settings.MaxMatches)
                return null;
            var match = new Match(Guid.NewGuid(), numSlots, availableSlots, attributes, sessionInfo, clock.GetUtcNow());
            _matches[match.Id] = match;
            return match;
        }
    }

    public enum UpdateResult { Updated, Recreated, Full }

    /// <summary>
    /// Replaces a match's description. An unknown id is recreated under that
    /// same id: the host holds it because this server issued it, so the match
    /// was swept or lost to a restart, and the host is evidently still running
    /// it. Refusing would make the game unadvertise a live match.
    /// </summary>
    public UpdateResult Update(Guid id, int numSlots, int availableSlots,
        IReadOnlyDictionary<string, long> attributes, string? sessionInfo)
    {
        lock (_lock)
        {
            Sweep();
            bool known = _matches.ContainsKey(id);
            if (!known && _matches.Count >= settings.MaxMatches)
                return UpdateResult.Full;
            _matches[id] = new Match(id, numSlots, availableSlots, attributes, sessionInfo, clock.GetUtcNow());
            return known ? UpdateResult.Updated : UpdateResult.Recreated;
        }
    }

    /// <summary>True if the match existed. Either way it is gone afterwards.</summary>
    public bool Unadvertise(Guid id)
    {
        lock (_lock)
            return _matches.Remove(id);
    }

    /// <summary>
    /// Matches with at least <paramref name="availableSlots"/> free whose
    /// attributes satisfy <paramref name="filter"/>, most recently refreshed
    /// first, at most <paramref name="maxResults"/>.
    /// </summary>
    public List<Match> Find(int availableSlots, IReadOnlyList<KeyValuePair<string, long>> filter, int maxResults)
    {
        lock (_lock)
        {
            Sweep();
            return _matches.Values
                .Where(m => Offered(m, availableSlots, filter))
                .OrderByDescending(m => m.LastSeen)
                .Take(Math.Max(0, maxResults))
                .ToList();
        }
    }

    /// <summary>
    /// Whether Find offers <paramref name="match"/>; the reason it does not is
    /// logged at Debug, since the filter rules are still being worked out.
    /// </summary>
    private bool Offered(Match match, int availableSlots, IReadOnlyList<KeyValuePair<string, long>> filter)
    {
        string? reason =
            match.SessionInfo is null ? "it has no session info"
            : match.AvailableSlots < availableSlots ? $"it has {match.AvailableSlots} free slot(s), {availableSlots} wanted"
            : Mismatch(match, filter);
        if (reason is not null)
            Log.Debug("[ros] Find skips match {Id}: {Reason}", match.Id, reason);
        return reason is null;
    }

    /// <summary>
    /// Null when every filter attribute equals the match's, except where
    /// either side is <see cref="MatchmakingSettings.WildcardValue"/> or the
    /// attribute is in <see cref="MatchmakingSettings.IgnoredFilterAttributes"/>,
    /// or it is the match status of a match that joins in progress; otherwise
    /// why not. A filter attribute the match never advertised does
    /// not match.
    /// </summary>
    private string? Mismatch(Match match, IReadOnlyList<KeyValuePair<string, long>> filter)
    {
        bool joinInProgress = !string.IsNullOrEmpty(settings.JoinInProgressAttribute)
            && match.Attributes.TryGetValue(settings.JoinInProgressAttribute, out long jip) && jip == 1;
        foreach (var (name, wanted) in filter)
        {
            if (wanted == settings.WildcardValue
                || settings.IgnoredFilterAttributes.Contains(name, StringComparer.Ordinal)
                || (joinInProgress && string.Equals(name, settings.MatchStatusAttribute, StringComparison.Ordinal)))
                continue;
            if (!match.Attributes.TryGetValue(name, out long have))
                return $"it does not advertise {name}";
            if (have != settings.WildcardValue && have != wanted)
                return $"{name} is {have}, filter wants {wanted}";
        }
        return null;
    }

    private void Sweep()
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromSeconds(settings.StaleAfterSeconds);
        foreach (var stale in _matches.Values.Where(m => m.LastSeen < cutoff).Select(m => m.Id).ToList())
        {
            _matches.Remove(stale);
            Log.Information("[ros] match {Id} not refreshed for {Seconds}s; dropped", stale, settings.StaleAfterSeconds);
        }
    }
}
