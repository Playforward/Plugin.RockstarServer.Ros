namespace Plugin.RockstarServer.Ros.Routes;

/// <summary>
/// Every ROS web-service call, answered from files. Nothing about GTA IV's
/// ROS traffic has been captured from a real server (Rockstar never ran
/// multiplayer for the 1.2 build), so responses are built by trial and error:
/// the client's request is captured, a response file is dropped into
/// Titles/&lt;Title&gt;/ros/&lt;root&gt;/&lt;service&gt;/&lt;method&gt;.xml, and the next
/// attempt is served it; a call without a file gets
/// <see cref="RosSettings.MissingResponseStatus"/>. The one exception is
/// matchmaking.asmx, whose replies depend on who else is hosting and so come
/// from <see cref="Matchmaking.MatchmakingService"/>.
/// </summary>
internal static class GameServices
{
    private const string Pattern = "/{rosTitle}/{rosVersion}/{rosRoot}/{rosService}/{rosMethod}?{!args}";

    [HTTP("POST", Pattern)]
    [HTTP("GET", Pattern)]
    public static bool Call(ServerSender s)
    {
        var call = RosCall.From(s);
        if (call is null)
            return false;

        call.Capture();

        var live = Matchmaking.MatchmakingService.TryAnswer(call);
        if (live is not null)
        {
            Respond(s, call, live, XmlContentType, "matchmaking (live)");
            return true;
        }

        var response = call.FindResponse();
        if (response is null)
        {
            if (MissingResponses.First($"{call.Title?.TitleId}{call}", () => { }))
                Log.Warning("[ros] no response for {Call} yet: put {Method}.xml (or .json/.bin) in {Dir}; answering {Status}",
                    call.ToString(), call.Method, call.ResponseDir, RosPlugin.Settings.MissingResponseStatus);
            s.Response.MakeErrorResponse(RosPlugin.Settings.MissingResponseStatus, "");
            s.SendResponse();
            return true;
        }

        var (plain, contentType, path) = response.Value;
        Respond(s, call, plain, contentType, path);
        return true;
    }

    private const string XmlContentType = "text/xml; charset=utf-8";

    /// <summary>
    /// Sends a 200 with <paramref name="plain"/>, encrypted when the request
    /// was, and writes it beside the request's capture.
    /// </summary>
    private static void Respond(ServerSender s, RosCall call, byte[] plain, string contentType, string source)
    {
        var body = call.ResponseBody(plain, out var note);
        Log.Information("[ros] {Call} <- {Source} ({Len} bytes, {Note})", call.ToString(), source, plain.Length, note);
        call.CaptureResponse(200, contentType, source, plain, body);
        s.Response.Clear()
            .SetBegin(200)
            .SetHeader("Content-Type", contentType)
            .SetBody(body);
        s.SendResponse();
    }

    private static readonly LogOnce MissingResponses = new(512);
}
