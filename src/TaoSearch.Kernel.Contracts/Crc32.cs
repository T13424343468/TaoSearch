namespace TaoSearch.Kernel.Contracts;

/// <summary>
/// CRC-32（IEEE 802.3，多项式 0xEDB88320 反射）：快照与 journal 帧校验。
/// 零第三方依赖令：自研实现，不引 System.IO.Hashing。
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) == 0 ? c >> 1 : 0xEDB88320u ^ (c >> 1);
            table[i] = c;
        }
        return table;
    }

    /// <summary>一次性计算（内部即 Begin→Update→Final）。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFFu;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFF_FFFFu;
    }

    /// <summary>流式更新：crc 传上一轮返回值（首轮 0xFFFFFFFF）。</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>流式收尾：与首字节异或后得到标准 CRC-32 值。</summary>
    public static uint Final(uint crc) => crc ^ 0xFFFF_FFFFu;
}
