using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Plugin.RockstarServer.Ros.Matchmaking;

/// <summary>
/// matchmaking.asmx answered from live state. What each call sends and what
/// the client reads back is in docs/PROTOCOL.md, "Response envelope and
/// matchmaking replies"; in short:
///
///   Advertise    numSlots, availableSlots, attrsJson  ->  MatchId (GUID)
///   Update       matchId + the same fields             ->  Status only
///   Unadvertise  matchId                               ->  Status only
///   Find         availableSlots, filterName, filterParamsJson, maxResults
///                                                      ->  Results/R/Data
/// </summary>
internal static class MatchmakingService
{
    private const string Service = "matchmaking.asmx";

    /// <summary>
    /// The longest session info the client accepts in a Find result: 0x6C84B0
    /// refuses a Data element that decodes to more than 0x45 bytes.
    /// </summary>
    private const int MaxSessionInfoBytes = 0x45;

    /// <summary>attrsJson's member holding the session info, not an attribute.</summary>
    private const string SessionInfoAttribute = "Data";

    private static readonly ConcurrentDictionary<string, MatchStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LogOnce Oddities = new(256);

    /// <summary>
    /// The XML reply for a matchmaking call, or null when this is not one (or
    /// matchmaking is off), so the caller falls back to response files.
    /// </summary>
    public static byte[]? TryAnswer(RosCall call)
    {
        var settings = RosPlugin.Settings.Matchmaking;
        if (!settings.Enabled || !string.Equals(call.Service, Service, StringComparison.OrdinalIgnoreCase))
            return null;
        var store = Stores.GetOrAdd(call.RosTitle, _ => new MatchStore(settings, TimeProvider.System));
        return call.Method.ToLowerInvariant() switch
        {
            "advertise" => Advertise(call, store),
            "update" => Update(call, store),
            "unadvertise" => Unadvertise(call, store),
            "find" => Find(call, store),
            _ => null,
        };
    }

    private static byte[] Advertise(RosCall call, MatchStore store)
    {
        if (!TryReadMatch(call, out int numSlots, out int availableSlots, out var attributes, out var sessionInfo, out var error))
            return error;
        var match = store.Advertise(numSlots, availableSlots, attributes, sessionInfo);
        if (match is null)
        {
            Log.Warning("[ros] Advertise refused: {Title} already holds MaxMatches ({Max}) matches",
                call.RosTitle, RosPlugin.Settings.Matchmaking.MaxMatches);
            return Error(call, "LimitExceeded", "MaxMatches");
        }
        Log.Information("[ros] match {Id} advertised: {Available}/{Slots} slots free, GAME_MODE {Mode}; {Count} live",
            match.Id, availableSlots, numSlots, attributes.GetValueOrDefault("GAME_MODE"), store.Count);
        return Success(call, new XElement(Ns(call) + "MatchId", match.Id.ToString("D")));
    }

    private static byte[] Update(RosCall call, MatchStore store)
    {
        if (!TryReadMatchId(call, out var id, out var error)
            || !TryReadMatch(call, out int numSlots, out int availableSlots, out var attributes, out var sessionInfo, out error))
            return error;
        var result = store.Update(id, numSlots, availableSlots, attributes, sessionInfo);
        if (result == MatchStore.UpdateResult.Full)
        {
            Log.Warning("[ros] Update for unknown match {Id} refused: {Title} already holds MaxMatches ({Max})",
                id, call.RosTitle, RosPlugin.Settings.Matchmaking.MaxMatches);
            return Error(call, "LimitExceeded", "MaxMatches");
        }
        Log.Information("[ros] match {Id} {Result}: {Available}/{Slots} slots free",
            id, result == MatchStore.UpdateResult.Recreated ? "recreated by its host's Update" : "updated",
            availableSlots, numSlots);
        return Success(call);
    }

    private static byte[] Unadvertise(RosCall call, MatchStore store)
    {
        if (!TryReadMatchId(call, out var id, out var error))
            return error;
        bool existed = store.Unadvertise(id);
        Log.Information("[ros] match {Id} unadvertised{Note}; {Count} live",
            id, existed ? "" : " (was not listed)", store.Count);
        return Success(call);
    }

    private static byte[] Find(RosCall call, MatchStore store)
    {
        if (!TryReadInt(call, "availableSlots", out int availableSlots, out var error)
            || !TryReadInt(call, "maxResults", out int maxResults, out error))
            return error;
        var filterName = call.Field("filterName") ?? "";
        if (!string.Equals(filterName, "Main", StringComparison.Ordinal)
            && Oddities.First($"filter:{filterName}", () => { }))
            Log.Warning("[ros] Find with filter '{Filter}': only 'Main' has been seen so far; matched the same way", filterName);
        if (!TryReadNumbers(call, "filterParamsJson", out var filter, out _, out error))
            return error;

        var matches = store.Find(availableSlots, filter, maxResults);
        Log.Information("[ros] Find ({Filter}, {Slots} slot(s), max {Max}): {Found} of {Count} live match(es)",
            filterName, availableSlots, maxResults, matches.Count, store.Count);
        var ns = Ns(call);
        var results = new XElement(ns + "Results", new XAttribute("Count", matches.Count),
            matches.Select(m => new XElement(ns + "R", new XElement(ns + SessionInfoAttribute, m.SessionInfo))));
        return Success(call, results);
    }

    private static bool TryReadMatch(RosCall call, out int numSlots, out int availableSlots,
        out IReadOnlyDictionary<string, long> attributes, out string? sessionInfo, out byte[] error)
    {
        attributes = new Dictionary<string, long>();
        sessionInfo = null;
        availableSlots = 0;
        if (!TryReadInt(call, "numSlots", out numSlots, out error)
            || !TryReadInt(call, "availableSlots", out availableSlots, out error)
            || !TryReadNumbers(call, "attrsJson", out var numbers, out var info, out error))
            return false;

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, value) in numbers)
            map[name] = value;
        attributes = map;

        if (info is null)
            Log.Warning("[ros] {Method}: attrsJson has no {Data}; the match will never be offered by Find",
                call.Method, SessionInfoAttribute);
        else if (!IsSessionInfo(info))
            Log.Warning("[ros] {Method}: attrsJson {Data} is not base64 of at most {Max} bytes; the match will never be offered by Find",
                call.Method, SessionInfoAttribute, MaxSessionInfoBytes);
        else
            sessionInfo = info;
        return true;
    }

    private static bool IsSessionInfo(string value)
    {
        Span<byte> decoded = stackalloc byte[MaxSessionInfoBytes];
        return Convert.TryFromBase64String(value, decoded, out int written) && written > 0;
    }

    /// <summary>
    /// The numeric members of a JSON object field, in order and duplicates
    /// kept (GTA IV's Find repeats MMATTR_TEAM_SIZE), plus its "Data" string.
    /// </summary>
    private static bool TryReadNumbers(RosCall call, string field, out List<KeyValuePair<string, long>> numbers,
        out string? sessionInfo, out byte[] error)
    {
        numbers = [];
        sessionInfo = null;
        error = [];
        var json = call.Field(field);
        if (json is null)
        {
            error = Error(call, "MissingArgument", field);
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Error(call, "InvalidArgument", field);
                return false;
            }
            foreach (var member in doc.RootElement.EnumerateObject())
            {
                if (member.NameEquals(SessionInfoAttribute) && member.Value.ValueKind == JsonValueKind.String)
                    sessionInfo = member.Value.GetString();
                else if (member.Value.ValueKind == JsonValueKind.Number && member.Value.TryGetInt64(out long n))
                    numbers.Add(new(member.Name, n));
                else if (Oddities.First($"{call.Method}:{field}:{member.Name}:{member.Value.ValueKind}", () => { }))
                    Log.Warning("[ros] {Method}: {Field} member {Name} is {Kind}, not an integer; ignored",
                        call.Method, field, member.Name, member.Value.ValueKind);
            }
            return true;
        }
        catch (JsonException e)
        {
            Log.Warning("[ros] {Method}: {Field} is not JSON ({Problem})", call.Method, field, e.Message);
            error = Error(call, "InvalidArgument", field);
            return false;
        }
    }

    private static bool TryReadInt(RosCall call, string field, out int value, out byte[] error)
    {
        error = [];
        var text = call.Field(field);
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        error = Error(call, text is null ? "MissingArgument" : "InvalidArgument", field);
        return false;
    }

    private static bool TryReadMatchId(RosCall call, out Guid id, out byte[] error)
    {
        error = [];
        var text = call.Field("matchId");
        if (Guid.TryParse(text, out id))
            return true;
        error = Error(call, text is null ? "MissingArgument" : "InvalidArgument", "matchId");
        return false;
    }

    private static XNamespace Ns(RosCall call) => $"Matchmaking{call.Method}Response";

    private static byte[] Success(RosCall call, params XElement[] content) =>
        Envelope(call, 1, content);

    private static byte[] Error(RosCall call, string code, string codeEx)
    {
        Log.Warning("[ros] {Call}: answering error {Code}/{CodeEx}", call.ToString(), code, codeEx);
        return Envelope(call, 0, new XElement(Ns(call) + "Error", new XAttribute("Code", code), new XAttribute("CodeEx", codeEx)));
    }

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private static byte[] Envelope(RosCall call, int status, params XElement[] content)
    {
        var ns = Ns(call);
        var root = new XElement(ns + "Response",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(XNamespace.Xmlns + "xsd", Xsd),
            new XAttribute("ms", 0),
            new XElement(ns + "Status", status),
            content);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
        return output.ToArray();
    }
}
