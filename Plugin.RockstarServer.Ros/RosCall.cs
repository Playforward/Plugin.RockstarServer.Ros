using ServerShared.Titles;
using System.Text;

namespace Plugin.RockstarServer.Ros;

/// <summary>
/// One request to a ROS web service. GTAIV.exe builds these URLs with the
/// format string "%s/%d/%s/%s" (title, version, root, service path) on host
/// "&lt;env&gt;.ros.rockstargames.com", where root is "gameservices" or
/// "launcherservices"; .svc services use "%s/%d/wcfgameservices/%s" instead.
/// See docs/PROTOCOL.md.
/// </summary>
internal sealed class RosCall
{
    /// <summary>The path roots GTAIV.exe is seen to use. Anything else is not ROS.</summary>
    public static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        "gameservices", "launcherservices", "wcfgameservices",
    };

    public required string RosTitle { get; init; }
    public required string Version { get; init; }
    public required string Root { get; init; }
    public required string Service { get; init; }
    public required string Method { get; init; }
    public required ServerSender Sender { get; init; }

    public TitleDescriptor? Title { get; private set; }

    /// <summary>
    /// True once <see cref="Capture"/> has decrypted the request with a valid
    /// trailer. The client then expects a 2xx reply encrypted the same way:
    /// it reads 2xx bodies in the mode the request was sent in ([req+0x224],
    /// copied to [req+0x220] at 0x6FFF22) and 4xx/5xx bodies as plaintext.
    /// </summary>
    public bool RequestEncrypted { get; private set; }

    /// <summary>
    /// The request's form fields once <see cref="Capture"/> has read them
    /// (decrypted first when needed); empty for a body that is not a
    /// plaintext form. Matchmaking answers from these.
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> Form { get; private set; } = [];

    /// <summary>The first value of form field <paramref name="key"/>, or null.</summary>
    public string? Field(string key) =>
        Form.Where(f => string.Equals(f.Key, key, StringComparison.Ordinal)).Select(f => f.Value).FirstOrDefault();

    // Capture file stem, so the response can be written beside the request.
    private string? _captureStem;

    public override string ToString() => $"/{RosTitle}/{Version}/{Root}/{Service}/{Method}";

    /// <summary>Null when the matched path is not a ROS call, so the route can decline it.</summary>
    public static RosCall? From(ServerSender sender)
    {
        var p = sender.Parameters;
        if (!p.TryGetValue("rosRoot", out var root) || !Roots.Contains(root))
            return null;
        var call = new RosCall
        {
            RosTitle = p.GetValueOrDefault("rosTitle", ""),
            Version = p.GetValueOrDefault("rosVersion", ""),
            Root = root,
            Service = p.GetValueOrDefault("rosService", ""),
            Method = p.GetValueOrDefault("rosMethod", ""),
            Sender = sender,
        };
        call.Title = call.ResolveTitle();
        return call;
    }

    private TitleDescriptor? ResolveTitle()
    {
        var id = RosPlugin.Settings.TitleMap
            .FirstOrDefault(kv => string.Equals(kv.Key, RosTitle, StringComparison.OrdinalIgnoreCase)).Value;
        if (id is not null)
        {
            var mapped = TitleRegistry.ById(id);
            if (mapped is null)
                Log.Warning("[ros] TitleMap sends '{RosTitle}' to '{Id}', which is not in Configs/{File}",
                    RosTitle, id, TitleRegistry.ConfigFileName);
            return mapped;
        }

        var ros = TitleRegistry.All.Where(t => string.Equals(t.ProtocolFamily, "ros", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ros.Count == 1)
        {
            if (UnmappedTitles.First(RosTitle, () => { }))
                Log.Warning("[ros] ROS title '{RosTitle}' (version {Version}) is not in TitleMap; using {Title}, "
                          + "the only title with ProtocolFamily \"ros\". Add it to TitleMap to make this explicit.",
                    RosTitle, Version, ros[0]);
            return ros[0];
        }
        Log.Warning("[ros] ROS title '{RosTitle}' is not in TitleMap and {Count} titles have ProtocolFamily \"ros\"; "
                  + "cannot tell which one this is", RosTitle, ros.Count);
        return null;
    }

    private static readonly LogOnce UnmappedTitles = new(64);

    private static readonly (string Extension, string ContentType)[] ResponseKinds =
    [
        (".xml", "text/xml; charset=utf-8"),
        (".json", "application/json; charset=utf-8"),
        (".bin", "application/octet-stream"),
    ];

    /// <summary>
    /// The response file for this call, read fresh every time so an edit is
    /// picked up on the client's next attempt without a restart.
    /// </summary>
    public (byte[] Body, string ContentType, string Path)? FindResponse()
    {
        if (Title is null || string.IsNullOrWhiteSpace(Title.ResourceDir))
            return null;
        var dir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), Title.ResourceDir, "ros", Root, Service);
        foreach (var (extension, contentType) in ResponseKinds)
        {
            var path = System.IO.Path.Combine(dir, Method + extension);
            if (File.Exists(path))
                return (File.ReadAllBytes(path), contentType, path);
        }
        return null;
    }

    public string ResponseDir => Title is null || string.IsNullOrWhiteSpace(Title.ResourceDir)
        ? "(no title)"
        : System.IO.Path.Combine(Title.ResourceDir, "ros", Root, Service);

    /// <summary>
    /// Logs the request and writes it to the capture directory. GTAIV.exe
    /// carries GTA V's ROS security headers (ros-SecurityFlags,
    /// ros-SessionTicket, ros-HeadersHmac, ...), so bodies may be encrypted;
    /// until that is worked out the body is kept byte for byte (only the
    /// chunked framing is removed) and flagged when it does not look like text.
    /// </summary>
    public void Capture()
    {
        var request = Sender.Request;
        byte[] body = Body(out string framing);
        Sender.Headers.TryGetValue("host", out var host);
        bool text = LooksLikeText(body);

        Log.Information("[ros] {Method} {Call} host={Host} title={Title} body={Len} bytes ({Kind})",
            request.Method, ToString(), host ?? "?", Title?.ToString() ?? "?", body.Length,
            body.Length == 0 ? "empty" : text ? "text" : "binary, possibly encrypted");
        foreach (var h in Sender.Headers.OrderBy(h => h.Key))
            Log.Debug("[ros]   {Key}: {Value}", h.Key, IsCredential(h.Key) ? $"({h.Value.Length} characters, not logged)" : h.Value);
        var decrypted = Decrypt(body);
        if (decrypted is { } d)
        {
            Log.Information("[ros]   decrypted with the {RosTitle} platform key: {Len} bytes, trailer {Trailer}",
                RosTitle, d.Payload.Length, d.TrailerValid ? "valid" : "INVALID");
            body = d.Payload;
            text = LooksLikeText(body);
            RequestEncrypted = d.TrailerValid;
        }
        var form = FormFields(body, text);
        Form = form;
        foreach (var (key, value) in form)
            Log.Information("[ros]   form {Key} = {Value}", key, IsCredential(key)
                ? $"({value.Length} characters, not logged)"
                : Truncate(value, MaxLoggedFieldLength));
        if (body.Length > 0 && form.Count == 0)
            Log.Debug("[ros]   body: {Body}", text
                ? Truncate(Encoding.UTF8.GetString(body), 2048)
                : Convert.ToHexString(body, 0, Math.Min(body.Length, 256)) + (body.Length > 256 ? " …" : ""));

        var dir = RosPlugin.Settings.CaptureDir;
        if (string.IsNullOrWhiteSpace(dir))
            return;
        try
        {
            Directory.CreateDirectory(dir);
            var stem = System.IO.Path.Combine(dir,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_{Sanitize(Root)}_{Sanitize(Service)}_{Sanitize(Method)}");
            var head = new StringBuilder()
                .Append(request.Method).Append(' ').Append(request.Url).Append(' ').AppendLine(request.Protocol);
            foreach (var h in Sender.Headers.OrderBy(h => h.Key))
                head.Append(h.Key).Append(": ").AppendLine(h.Value);
            head.Append("# body in .req.bin: ").AppendLine(framing);
            if (decrypted is { } dec)
                head.Append("# decrypted into .req.dec: ").Append(dec.Payload.Length)
                    .Append(" bytes, trailer ").AppendLine(dec.TrailerValid ? "valid" : "INVALID");
            foreach (var (key, value) in form)
                head.Append("# form ").Append(key).Append(" = ").AppendLine(value);
            File.WriteAllText(stem + ".req.txt", head.ToString());
            _captureStem = stem;
            File.WriteAllBytes(stem + ".req.bin", Body(out _));
            if (decrypted is { } plain)
                File.WriteAllBytes(stem + ".req.dec", plain.Payload);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[ros] could not write capture to {Dir}", dir);
        }
    }

    /// <summary>
    /// The body to send for a 2xx reply: <paramref name="plain"/> encrypted
    /// when the request was, otherwise as is. <paramref name="note"/> says
    /// which, for the log.
    /// </summary>
    public byte[] ResponseBody(byte[] plain, out string note)
    {
        if (!RequestEncrypted)
        {
            note = "plaintext, as the request was";
            return plain;
        }
        var crypto = RosPlugin.CryptoFor(RosTitle);
        if (crypto is null)
        {
            // Cannot happen while RequestEncrypted (it needed the same key),
            // unless the key was removed between the two.
            note = "plaintext: no platform key to encrypt with (the client will reject it)";
            return plain;
        }
        var wire = crypto.EncryptResponse(plain);
        note = $"encrypted, {wire.Length} bytes on the wire";
        return wire;
    }

    /// <summary>
    /// Writes the response next to the request's capture: .resp.txt (status,
    /// headers and where it came from), .resp.dec (the plaintext) and
    /// .resp.bin (the bytes sent) when it was encrypted.
    /// </summary>
    public void CaptureResponse(int status, string contentType, string source, byte[] plain, byte[] wire)
    {
        if (_captureStem is null)
            return;
        try
        {
            var head = new StringBuilder()
                .Append(status).AppendLine()
                .Append("Content-Type: ").AppendLine(contentType)
                .Append("# from ").AppendLine(source)
                .Append("# plaintext in .resp.dec: ").Append(plain.Length).AppendLine(" bytes");
            if (!ReferenceEquals(plain, wire))
                head.Append("# encrypted in .resp.bin: ").Append(wire.Length).AppendLine(" bytes");
            File.WriteAllText(_captureStem + ".resp.txt", head.ToString());
            File.WriteAllBytes(_captureStem + ".resp.dec", plain);
            if (!ReferenceEquals(plain, wire))
                File.WriteAllBytes(_captureStem + ".resp.bin", wire);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[ros] could not write the response capture for {Call}", ToString());
        }
    }

    /// <summary>
    /// The request payload with any chunked framing removed. A chunked body
    /// that does not decode is returned raw, and <paramref name="framing"/>
    /// says which of the two it is.
    /// </summary>
    public byte[] Body(out string framing)
    {
        byte[] raw = Sender.Request.BodyBytes;
        if (!Sender.Headers.TryGetValue("transfer-encoding", out var te)
            || !te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            framing = $"{raw.Length} bytes as sent";
            return raw;
        }
        var payload = ChunkedBody.Decode(raw, out var sizes);
        if (payload is null)
        {
            Log.Warning("[ros] {Call}: chunked body did not decode ({Len} raw bytes); kept raw", ToString(), raw.Length);
            framing = $"{raw.Length} raw bytes; chunked framing did not decode, kept as received";
            return raw;
        }
        framing = $"{payload.Length} bytes, de-chunked from chunks of {string.Join("+", sizes)}";
        return payload;
    }

    /// <summary>
    /// The body decrypted with this title's platform key, or null when there
    /// is no key for it, the body is plaintext, or it is too short to be a
    /// ROS message.
    /// </summary>
    private RosCrypto.Decrypted? Decrypt(byte[] body)
    {
        if (body.Length == 0 || LooksLikeText(body))
            return null;
        var crypto = RosPlugin.CryptoFor(RosTitle);
        return crypto?.DecryptRequest(body);
    }

    /// <summary>
    /// Longest form value written to the log; the capture file keeps it whole.
    /// </summary>
    private const int MaxLoggedFieldLength = 512;

    /// <summary>
    /// The decoded fields of a plaintext application/x-www-form-urlencoded
    /// body, in order; empty for anything else (an encrypted body included).
    /// </summary>
    public List<(string Key, string Value)> FormFields(byte[] body, bool text)
    {
        var fields = new List<(string, string)>();
        if (!text || body.Length == 0
            || !Sender.Headers.TryGetValue("content-type", out var type)
            || !type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return fields;
        var parsed = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
        foreach (string? key in parsed.AllKeys)
            foreach (var value in parsed.GetValues(key) ?? [])
                fields.Add((key ?? "", value));
        return fields;
    }

    /// <summary>
    /// Form fields and headers that carry a credential (the Social Club
    /// ticket, the ros-SessionTicket header), kept out of the log. The capture
    /// files still hold them.
    /// </summary>
    private static bool IsCredential(string key) =>
        string.Equals(key, "ticket", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "ros-SessionTicket", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeText(byte[] body)
    {
        if (body.Length == 0)
            return true;
        int printable = body.Count(b => b is >= 0x20 and < 0x7f or (byte)'\r' or (byte)'\n' or (byte)'\t');
        return printable >= body.Length * 0.95;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + " …(truncated)" : s;

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
