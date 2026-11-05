namespace ModFramework.Runtime;

/// <summary>
/// ECMA-335 II.6.2.1.3 public key token derivation.
///
/// The token is the <b>last 8 bytes of the SHA-1 hash of the full public key, in reverse
/// order</b>. It is needed here because the trimmed BCL exposes
/// <c>AssemblyName.GetPublicKeyToken()</c> but not <c>AssemblyName.GetPublicKey()</c>: a loaded
/// host assembly only ever reveals its token, while a file read by the preflight reveals the
/// full key. Deriving the token from the key is what makes the two sides comparable at all.
///
/// <para>
/// SHA-1 appears here purely as the <i>encoding</i> the CLR specification prescribes for a
/// public identifier. Nothing in this framework uses it as a security primitive, as a content
/// identity, or as evidence that two assemblies are trustworthy - see
/// <see cref="AssemblyIdentityComparison"/> (identity) and <see cref="ContentIdentity"/>
/// (content). The implementation is deliberately small and self-contained so the target
/// runtime's cryptographic surface is not part of the preflight's correctness.
/// </para>
/// </summary>
internal static class PublicKeyToken
{
    /// <summary>Length of an ECMA-335 public key token.</summary>
    internal const int TokenLength = 8;

    /// <summary>Derives the token of a full public key, or empty when the key is empty.</summary>
    internal static byte[] Derive(byte[] publicKey)
    {
        if (publicKey == null || publicKey.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var digest = Sha1.Hash(publicKey);
        var token = new byte[TokenLength];
        for (var i = 0; i < TokenLength; i++)
        {
            // Last 8 bytes of the digest, reversed.
            token[i] = digest[digest.Length - 1 - i];
        }
        return token;
    }

    /// <summary>
    /// Minimal SHA-1 (FIPS 180-1). Used only by <see cref="Derive"/>; see the type remarks.
    /// </summary>
    internal static class Sha1
    {
        internal static byte[] Hash(byte[] data)
        {
            var h0 = 0x67452301u;
            var h1 = 0xEFCDAB89u;
            var h2 = 0x98BADCFEu;
            var h3 = 0x10325476u;
            var h4 = 0xC3D2E1F0u;

            var padded = Pad(data);
            var w = new uint[80];
            for (var block = 0; block < padded.Length; block += 64)
            {
                for (var i = 0; i < 16; i++)
                {
                    var offset = block + i * 4;
                    w[i] = ((uint)padded[offset] << 24) | ((uint)padded[offset + 1] << 16) |
                           ((uint)padded[offset + 2] << 8) | padded[offset + 3];
                }
                for (var i = 16; i < 80; i++)
                {
                    w[i] = RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
                }

                var a = h0;
                var b = h1;
                var c = h2;
                var d = h3;
                var e = h4;

                for (var i = 0; i < 80; i++)
                {
                    uint f;
                    uint k;
                    if (i < 20)
                    {
                        f = (b & c) | (~b & d);
                        k = 0x5A827999u;
                    }
                    else if (i < 40)
                    {
                        f = b ^ c ^ d;
                        k = 0x6ED9EBA1u;
                    }
                    else if (i < 60)
                    {
                        f = (b & c) | (b & d) | (c & d);
                        k = 0x8F1BBCDCu;
                    }
                    else
                    {
                        f = b ^ c ^ d;
                        k = 0xCA62C1D6u;
                    }

                    var temp = RotateLeft(a, 5) + f + e + k + w[i];
                    e = d;
                    d = c;
                    c = RotateLeft(b, 30);
                    b = a;
                    a = temp;
                }

                h0 += a;
                h1 += b;
                h2 += c;
                h3 += d;
                h4 += e;
            }

            var result = new byte[20];
            WriteWord(result, 0, h0);
            WriteWord(result, 4, h1);
            WriteWord(result, 8, h2);
            WriteWord(result, 12, h3);
            WriteWord(result, 16, h4);
            return result;
        }

        private static byte[] Pad(byte[] data)
        {
            var length = data.Length;
            var total = length + 9;
            var padding = (64 - total % 64) % 64;
            var padded = new byte[length + 1 + padding + 8];
            Array.Copy(data, padded, length);
            padded[length] = 0x80;

            var bitLength = (ulong)length * 8UL;
            for (var i = 0; i < 8; i++)
            {
                padded[padded.Length - 1 - i] = (byte)((bitLength >> (8 * i)) & 0xFF);
            }
            return padded;
        }

        private static uint RotateLeft(uint value, int bits)
        {
            return (value << bits) | (value >> (32 - bits));
        }

        private static void WriteWord(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)((value >> 24) & 0xFF);
            target[offset + 1] = (byte)((value >> 16) & 0xFF);
            target[offset + 2] = (byte)((value >> 8) & 0xFF);
            target[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
