namespace CreatFlow.Platforms.Douyin.Crypto;

/// <summary>
/// SM3 国密哈希算法 (GM/T 0004-2012)。
///
/// 256-bit 输出，Merkle-Damgård 结构，512-bit 消息块。
/// 对标 gmssl.sm3.sm3_hash，用于 a_bogus 签名的 URL/Body/UA 哈希。
/// </summary>
public static class SM3
{
    private const int BlockSize = 64; // 512 bits

    // SM3 初始向量 (IV)
    private static readonly uint[] IV =
    [
        0x7380166f, 0x4914b2b9, 0x172442d7, 0xda8a0600,
        0xa96f30bc, 0x163138aa, 0xe38dee4d, 0xb0fb0e4e,
    ];

    /// <summary>SM3 哈希，返回 32 字节。</summary>
    public static byte[] Hash(byte[] data)
    {
        var padded = Pad(data);
        var state = new uint[8];
        Array.Copy(IV, state, 8);

        for (int i = 0; i < padded.Length; i += BlockSize)
        {
            var block = new uint[16];
            for (int j = 0; j < 16; j++)
            {
                block[j] = (uint)(padded[i + j * 4] << 24 |
                                   padded[i + j * 4 + 1] << 16 |
                                   padded[i + j * 4 + 2] << 8 |
                                   padded[i + j * 4 + 3]);
            }
            CF(state, block);
        }

        var result = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            result[i * 4]     = (byte)(state[i] >> 24);
            result[i * 4 + 1] = (byte)(state[i] >> 16);
            result[i * 4 + 2] = (byte)(state[i] >> 8);
            result[i * 4 + 3] = (byte)state[i];
        }
        return result;
    }

    /// <summary>SM3 双哈希: SM3(SM3(data))</summary>
    public static byte[] DoubleHash(byte[] data) => Hash(Hash(data));

    // ═══ Message Padding ═══

    private static byte[] Pad(byte[] data)
    {
        long bitLength = data.Length * 8L;
        int padLen = BlockSize - (data.Length + 1 + 8) % BlockSize;
        if (padLen <= 0) padLen += BlockSize;
        var padded = new byte[data.Length + 1 + padLen + 8];
        data.CopyTo(padded, 0);
        padded[data.Length] = 0x80; // 1 bit followed by zeros
        // Append bit length as 64-bit big-endian
        padded[^8] = (byte)(bitLength >> 56);
        padded[^7] = (byte)(bitLength >> 48);
        padded[^6] = (byte)(bitLength >> 40);
        padded[^5] = (byte)(bitLength >> 32);
        padded[^4] = (byte)(bitLength >> 24);
        padded[^3] = (byte)(bitLength >> 16);
        padded[^2] = (byte)(bitLength >> 8);
        padded[^1] = (byte)bitLength;
        return padded;
    }

    // ═══ Compression Function ═══

    private static void CF(uint[] V, uint[] B)
    {
        // Message expansion: W[0..67], W'[0..63]
        var W = new uint[68];
        var W1 = new uint[64];

        for (int j = 0; j < 16; j++)
            W[j] = B[j];

        for (int j = 16; j < 68; j++)
            W[j] = P1(W[j - 16] ^ W[j - 9] ^ Rotl(W[j - 3], 15)) ^ Rotl(W[j - 13], 7) ^ W[j - 6];

        for (int j = 0; j < 64; j++)
            W1[j] = W[j] ^ W[j + 4];

        // Compression
        uint A = V[0], Bv = V[1], C = V[2], D = V[3],
             E = V[4], F = V[5], G = V[6], H = V[7];

        for (int j = 0; j < 64; j++)
        {
            uint Tj = j < 16 ? 0x79cc4519u : 0x7a879d8au;
            uint SS1 = Rotl(Rotl(A, 12) + E + Rotl(Tj, j), 7);
            uint SS2 = SS1 ^ Rotl(A, 12);
            uint TT1 = (j < 16 ? FF1(A, Bv, C) : FF2(A, Bv, C)) + D + SS2 + W1[j];
            uint TT2 = (j < 16 ? GG1(E, F, G) : GG2(E, F, G)) + H + SS1 + W[j];
            D = C;
            C = Rotl(Bv, 9);
            Bv = A;
            A = TT1;
            H = G;
            G = Rotl(F, 19);
            F = E;
            E = P0(TT2);
        }

        V[0] ^= A; V[1] ^= Bv; V[2] ^= C; V[3] ^= D;
        V[4] ^= E; V[5] ^= F; V[6] ^= G; V[7] ^= H;
    }

    // ═══ Helper Functions ═══

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

    private static uint P0(uint x) => x ^ Rotl(x, 9) ^ Rotl(x, 17);
    private static uint P1(uint x) => x ^ Rotl(x, 15) ^ Rotl(x, 23);

    private static uint FF1(uint x, uint y, uint z) => x ^ y ^ z;
    private static uint FF2(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
    private static uint GG1(uint x, uint y, uint z) => x ^ y ^ z;
    private static uint GG2(uint x, uint y, uint z) => (x & y) | (~x & z);
}
