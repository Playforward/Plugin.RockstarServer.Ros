using System.Globalization;
using System.Text;

namespace Plugin.RockstarServer.Ros;

/// <summary>
/// HTTP/1.1 chunked transfer decoding. GTAIV.exe posts every ROS body with
/// Transfer-Encoding: chunked, and NetCoreServer hands the body over still
/// framed ("13f\r\n...\r\n0\r\n\r\n"), so the framing has to come off before
/// the payload can be looked at.
/// </summary>
internal static class ChunkedBody
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();

    /// <summary>
    /// The payload of a chunked body, or null if it is malformed or stops
    /// before the terminating zero-size chunk. Chunk extensions are ignored;
    /// trailers after the last chunk are not part of the payload.
    /// </summary>
    public static byte[]? Decode(ReadOnlySpan<byte> raw, out IReadOnlyList<int> chunkSizes)
    {
        var sizes = new List<int>();
        chunkSizes = sizes;
        using var payload = new MemoryStream();
        int i = 0;
        while (i < raw.Length)
        {
            int lineEnd = raw[i..].IndexOf(CrLf);
            if (lineEnd < 0)
                return null;
            var sizeField = Encoding.ASCII.GetString(raw.Slice(i, lineEnd)).Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeField, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
                return null;
            i += lineEnd + CrLf.Length;
            if (size == 0)
                return payload.ToArray();
            if (i + size + CrLf.Length > raw.Length || !raw.Slice(i + size, CrLf.Length).SequenceEqual(CrLf))
                return null;
            payload.Write(raw.Slice(i, size));
            sizes.Add(size);
            i += size + CrLf.Length;
        }
        return null;
    }
}
