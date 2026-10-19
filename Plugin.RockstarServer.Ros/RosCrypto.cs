using System.Security.Cryptography;

namespace Plugin.RockstarServer.Ros;

/// <summary>
/// A stateful RC4 keystream (the same construction as the Quazal plugin's
/// Rc4Stream; plugins do not share code outside ServerShared).
/// </summary>
internal sealed class Rc4Stream
{
    private readonly byte[] _s = new byte[256];
    private int _i;
    private int _j;

    public Rc4Stream(ReadOnlySpan<byte> key)
    {
        for (int n = 0; n < 256; n++)
            _s[n] = (byte)n;
        for (int n = 0, k = 0; n < 256; n++)
        {
            k = (k + _s[n] + key[n % key.Length]) & 0xFF;
            (_s[n], _s[k]) = (_s[k], _s[n]);
        }
    }

    public byte[] Process(ReadOnlySpan<byte> data)
    {
        byte[] output = new byte[data.Length];
        for (int n = 0; n < data.Length; n++)
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _s[_i]) & 0xFF;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            output[n] = (byte)(data[n] ^ _s[(_s[_i] + _s[_j]) & 0xFF]);
        }
        return output;
    }
}

/// <summary>
/// ROS body crypto as GTAIV.exe 1.2.0.59 does it (and GTA V, whose layout is
/// public in citizenfx/fivem ros-patches-five), keyed by the title's
/// platform key (the 88-character base64 string the rlRosTitleId constructor
/// at 0x6DB790 decodes into 65 bytes). See docs/PROTOCOL.md, "Body crypto".
///
/// Platform key K (65 bytes): K[0] is a version byte, K[1..33] an RC4 key,
/// and K[33..49] / K[49..65] the xor key and hash key, each encrypted with a
/// fresh RC4 stream under that RC4 key (0x6EB3E0 / 0x6EB440).
///
/// A request body is  wire[0..16] | RC4(payload) | SHA-1 trailer, where
///   RC4 key  = wire[0..16] ^ xorKey (^ the session key, which mp_gate.asi's
///              ignore_session_key leaves out; 0x6FEE84 / 0x6FEEE0), and
///   trailer  = SHA-1(everything before it || hashKey).
/// </summary>
internal sealed class RosCrypto
{
    private const int PlatformKeyLength = 65;
    private const int Rc4KeyOffset = 1;
    private const int Rc4KeyLength = 32;
    private const int WrappedKeyLength = 16;
    private const int XorKeyOffset = Rc4KeyOffset + Rc4KeyLength;
    private const int HashKeyOffset = XorKeyOffset + WrappedKeyLength;

    /// <summary>Length of the per-message key at the start of a body.</summary>
    public const int MessageKeyLength = 16;
    private const int TrailerLength = 20;   // SHA-1

    private readonly byte[] _xorKey;
    private readonly byte[] _hashKey;

    private RosCrypto(byte[] xorKey, byte[] hashKey)
    {
        _xorKey = xorKey;
        _hashKey = hashKey;
    }

    /// <summary>Null, with the reason, if <paramref name="base64"/> is not a platform key.</summary>
    public static RosCrypto? FromPlatformKey(string base64, out string? problem)
    {
        byte[] key;
        try { key = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { problem = "not base64"; return null; }
        if (key.Length != PlatformKeyLength)
        {
            problem = $"decodes to {key.Length} bytes, expected {PlatformKeyLength}";
            return null;
        }
        var rc4Key = key.AsSpan(Rc4KeyOffset, Rc4KeyLength);
        problem = null;
        return new RosCrypto(
            new Rc4Stream(rc4Key).Process(key.AsSpan(XorKeyOffset, WrappedKeyLength)),
            new Rc4Stream(rc4Key).Process(key.AsSpan(HashKeyOffset, WrappedKeyLength)));
    }

    public readonly record struct Decrypted(bool TrailerValid, byte[] Payload);

    /// <summary>
    /// Decrypts a whole request body (chunked framing already removed). Null
    /// if it is too short to hold a key and a trailer. A bad trailer is
    /// reported, not refused, so a mismatch can still be looked at.
    /// </summary>
    public Decrypted? DecryptRequest(ReadOnlySpan<byte> body)
    {
        if (body.Length < MessageKeyLength + TrailerLength)
            return null;
        var signed = body[..^TrailerLength];
        bool trailerValid = Trailer(signed).AsSpan().SequenceEqual(body[^TrailerLength..]);

        Span<byte> messageKey = stackalloc byte[MessageKeyLength];
        for (int i = 0; i < MessageKeyLength; i++)
            messageKey[i] = (byte)(body[i] ^ _xorKey[i]);
        var payload = new Rc4Stream(messageKey).Process(signed[MessageKeyLength..]);
        return new Decrypted(trailerValid, payload);
    }

    /// <summary>
    /// Block size the client insists on: 0x6FF2D7 rejects a response whose
    /// size field is anything but 0x400.
    /// </summary>
    private const int ResponseBlockSize = 0x400;

    /// <summary>
    /// Encrypts a 2xx response body the way the client's reader at 0x6FF105
    /// takes it apart (the same layout as GTA V's ROS, see docs/PROTOCOL.md,
    /// "Response crypto"):
    ///
    ///   wire[0..16]                      R ^ xorKey, R random
    ///   RC4(big-endian uint32 0x400)
    ///   per 0x400-byte block:  RC4(block) | SHA-1(that ciphertext || hashKey)
    ///
    /// One RC4 stream keyed with R runs through the size field and every
    /// block. The per-block hash covers only that block's ciphertext; the
    /// challenge (flag 0x01) and HMAC (flag 0x04) variants are not produced,
    /// since mp_gate.asi's disable_security leaves those flags off.
    /// </summary>
    public byte[] EncryptResponse(ReadOnlySpan<byte> payload)
    {
        Span<byte> messageKey = stackalloc byte[MessageKeyLength];
        RandomNumberGenerator.Fill(messageKey);
        var rc4 = new Rc4Stream(messageKey);

        int blocks = (payload.Length + ResponseBlockSize - 1) / ResponseBlockSize;
        using var output = new MemoryStream(MessageKeyLength + 4 + payload.Length + blocks * TrailerLength);
        for (int i = 0; i < MessageKeyLength; i++)
            output.WriteByte((byte)(messageKey[i] ^ _xorKey[i]));

        Span<byte> blockSize = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blockSize, ResponseBlockSize);
        output.Write(rc4.Process(blockSize));

        for (int done = 0; done < payload.Length; done += ResponseBlockSize)
        {
            var block = rc4.Process(payload.Slice(done, Math.Min(ResponseBlockSize, payload.Length - done)));
            output.Write(block);
            output.Write(Trailer(block));
        }
        return output.ToArray();
    }

    private byte[] Trailer(ReadOnlySpan<byte> signed)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(signed);
        sha.AppendData(_hashKey);
        return sha.GetHashAndReset();
    }
}
