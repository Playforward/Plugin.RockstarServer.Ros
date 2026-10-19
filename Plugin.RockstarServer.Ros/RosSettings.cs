namespace Plugin.RockstarServer.Ros;

/// <summary>
/// Read from Configs/RockstarServer.RosConfig.json.
/// </summary>
public sealed class RosSettings
{
    /// <summary>
    /// The ROS title name in a request path (the first segment of
    /// /&lt;title&gt;/&lt;version&gt;/gameservices/...) mapped to the TitleId in
    /// Configs/Titles.json. GTA IV's ROS title is "gta4" (the string at
    /// 0xF6EDCC that the rlRosTitleId constructor copies in; version 11).
    /// An unmapped name falls back to the only title whose ProtocolFamily is
    /// "ros", with a warning, so a title not listed here is still served.
    /// </summary>
    public Dictionary<string, string> TitleMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gta4"] = "grand-theft-auto-iv",
    };

    /// <summary>
    /// Every ROS request is written to this directory (request line, headers,
    /// raw body) so the protocol can be worked out offline. Empty disables it.
    /// </summary>
    public string CaptureDir { get; set; } = "Captures/ros";

    /// <summary>
    /// Status sent when no response file exists for a call yet. 404 keeps the
    /// client on its own failure path instead of feeding it a guessed body.
    /// </summary>
    public int MissingResponseStatus { get; set; } = 404;

    /// <summary>
    /// ROS title name to that title's platform key: the base64 string the
    /// game decodes into its ROS crypto keys (GTA IV: the 88-character string
    /// at 0xF6ED38 in GTAIV.exe 1.2.0.59). It is Rockstar's secret, taken from
    /// the operator's own copy of the game, so it is never shipped in code or
    /// in the titles repo. Without one, bodies are captured but not decrypted.
    /// </summary>
    public Dictionary<string, string> PlatformKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// matchmaking.asmx (Advertise, Update, Unadvertise, Find), answered from
    /// live state instead of files. See docs/PROTOCOL.md, "Matchmaking".
    /// </summary>
    public MatchmakingSettings Matchmaking { get; set; } = new();
}

public sealed class MatchmakingSettings
{
    /// <summary>
    /// Answer the four matchmaking calls from the in-memory match list. Off,
    /// they fall back to response files like every other call.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A match nobody has advertised or updated for this long is dropped, so
    /// a host that crashed without Unadvertise stops being offered. Every
    /// Update refreshes it, but GTA IV sends no heartbeat: Update comes only
    /// when the match changes, and a live host was seen silent for 8 minutes
    /// (2026-10-15). Too short hides live matches; a host's next Update
    /// brings its match back either way.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 3600;

    /// <summary>
    /// Most matches held per ROS title. Advertise is refused (with a ROS
    /// error) beyond it rather than evicting a live match.
    /// </summary>
    public int MaxMatches { get; set; } = 1024;

    /// <summary>
    /// Attribute value meaning "unset / any" on either side of a Find
    /// filter. GTA IV advertises -1 for every MMATTR_* it does not set, and
    /// its Find asks for MMATTR_MATCH_STATUS 1 while hosts advertise -1, so
    /// without a wildcard a plain equality filter would find nobody.
    /// </summary>
    public long WildcardValue { get; set; } = -1;

    /// <summary>
    /// Attributes a Find filter is not matched on at all. Rockstar defined
    /// each named filter ("Main") server side and those rules were never
    /// published, so this is the knob for trial and error.
    /// </summary>
    public List<string> IgnoredFilterAttributes { get; set; } = [];

    /// <summary>
    /// The attribute holding a match's state. GTA IV advertises -1, then 0
    /// in the lobby, then 1 and 2 as a match runs, and its Find always asks
    /// for 1, so this is the attribute <see cref="JoinInProgressAttribute"/>
    /// can waive.
    /// </summary>
    public string MatchStatusAttribute { get; set; } = "MMATTR_MATCH_STATUS";

    /// <summary>
    /// A match advertising this attribute as 1 accepts players while running,
    /// so Find offers it whatever its <see cref="MatchStatusAttribute"/>.
    /// Free mode stays at status 0 with this at 1, and would otherwise never
    /// be found. Empty turns the waiver off.
    /// </summary>
    public string JoinInProgressAttribute { get; set; } = "MMATTR_JOIN_IN_PROGRESS";
}
