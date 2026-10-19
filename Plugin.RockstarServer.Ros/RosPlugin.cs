using ModdableWebServer.Interfaces;
using ServerShared.Controllers;
using ServerShared.Plugins;
using ServerShared.Titles;
using System.Reflection;

namespace Plugin.RockstarServer.Ros;

/// <summary>
/// Local stand-in for Rockstar Online Services (*.ros.rockstargames.com), the
/// backend GTA IV 1.2.0.59 talks to in place of Games for Windows LIVE.
/// </summary>
public class RosPlugin : ServerPlugin<RosSettings>
{
    /// <summary>The loaded config, for the static route handlers.</summary>
    internal static RosSettings Settings { get; private set; } = new();

    private static Dictionary<string, RosCrypto> _crypto = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The crypto for a ROS title name, or null if it has no usable platform key.</summary>
    internal static RosCrypto? CryptoFor(string rosTitle) => _crypto.GetValueOrDefault(rosTitle);

    public override uint Priority => 10;

    public override string Name => "RockstarServer.Ros";

    public override void Start()
    {
        Settings = Config;

        var titles = TitleRegistry.All
            .Where(t => string.Equals(t.ProtocolFamily, "ros", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (titles.Count == 0)
            Log.Warning("[ros] no title in Configs/{File} has ProtocolFamily \"ros\"; requests will be captured but not answered",
                TitleRegistry.ConfigFileName);
        foreach (var t in titles)
            Log.Information("[ros] serving {Title}: responses from {Dir}", t,
                string.IsNullOrWhiteSpace(t.ResourceDir) ? "(no ResourceDir set)" : Path.Combine(t.ResourceDir, "ros"));
        foreach (var (rosTitle, id) in Settings.TitleMap)
            Log.Information("[ros] ROS title '{RosTitle}' -> {Id}", rosTitle, id);

        var crypto = new Dictionary<string, RosCrypto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rosTitle, key) in Settings.PlatformKeys)
        {
            var c = RosCrypto.FromPlatformKey(key, out var problem);
            if (c is null)
                Log.Warning("[ros] platform key for '{RosTitle}' is unusable ({Problem}); its bodies stay encrypted", rosTitle, problem);
            else
            {
                crypto[rosTitle] = c;
                Log.Information("[ros] platform key loaded for '{RosTitle}'; request bodies will be decrypted", rosTitle);
            }
        }
        if (crypto.Count == 0)
            Log.Information("[ros] no PlatformKeys in {Config}; request bodies are captured encrypted", ConfigFileName);
        _crypto = crypto;

        foreach (var server in ServerController.Servers)
        {
            if (server.Server is IHttpServer http)
                http.HTTPAttributeToMethods.Merge(Assembly.GetExecutingAssembly());
        }
    }

    public override void Stop() { }
}
