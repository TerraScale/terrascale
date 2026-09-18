namespace TerraScale.Auth.Crypto;

/// <summary>Base64url (RFC 4648 section 5) without padding helpers, plus Base32 (RFC 4648) for TOTP secrets.</summary>
public static class Encodings
{
    /// <summary>Base64url-encodes without padding.</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url; accepts both padded and unpadded input.</summary>
    public static byte[] Base64UrlDecode(string encoded)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    /// <summary>Hex encoding, lowercase.</summary>
    public static string HexEncode(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    /// <summary>Hex decoding.</summary>
    public static byte[] HexDecode(string encoded) => Convert.FromHexString(encoded);

    /// <summary>Base32 encoding (RFC 4648, A-Z + 2-7) without padding — the TOTP secret format.</summary>
    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new char[(data.Length * 8 + 4) / 5];
        var bits = 0;
        var value = 0;
        var index = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output[index++] = alphabet[(value >> (bits - 5)) & 31];
                bits -= 5;
            }
        }
        if (bits > 0)
            output[index++] = alphabet[(value << (5 - bits)) & 31];
        return new string(output);
    }

    /// <summary>Decodes base32; accepts lowercase and optional padding.</summary>
    public static byte[] Base32Decode(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new byte[encoded.Length * 5 / 8];
        var bits = 0;
        var value = 0;
        var index = 0;
        foreach (var c in encoded)
        {
            if (c is '=' or ' ' or '\n' or '\r')
                continue;
            var digit = alphabet.IndexOf(char.ToUpperInvariant(c));
            if (digit < 0)
                throw new FormatException($"invalid base32 character '{c}'");
            value = (value << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)(value >> (bits - 8));
                bits -= 8;
            }
        }
        return output[..index];
    }
}
