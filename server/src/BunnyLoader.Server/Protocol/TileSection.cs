using System.IO.Compression;
using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>一个瓦片：是否实心、方块类型、墙壁类型。</summary>
public readonly record struct Tile(bool Active, byte Type, byte Wall);

/// <summary>
/// 消息 10（TileSection）的编码器。
///
/// 规范（bybrooklyn/terrustia docs/protocol-notes.md §"Tile sections"）：
///   负载整体是 **raw DEFLATE**（.NET DeflateStream，无 zlib/gzip 头，无「是否压缩」标志字节）
///   解压后：i32 xStart, i32 yStart, i16 width, i16 height, RLE 瓦片…,
///           i16 chestCount, i16 signCount, i16 entityCount
///   每瓦片：若干标志字节，然后按标志决定负载字段，最后是 run length
///   跑长是「第一个之后还有几个相同瓦片」，所以单块不写长度也不置位
/// </summary>
public static class TileSection
{
    public const int SectionWidth = 200;
    public const int SectionHeight = 150;

    // 方块类型
    public const byte TypeDirt = 0;
    public const byte TypeStone = 1;
    public const byte TypeGrass = 2;

    public static byte[] Encode(int sectionX, int sectionY, Func<int, int, Tile> getTile)
    {
        int xStart = sectionX * SectionWidth;
        int yStart = sectionY * SectionHeight;

        using var raw = new MemoryStream(4096);
        using (var w = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(xStart);
            w.Write(yStart);
            w.Write((short)SectionWidth);
            w.Write((short)SectionHeight);

            WriteRuns(w, xStart, yStart, getTile);

            w.Write((short)0); // chests
            w.Write((short)0); // signs
            w.Write((short)0); // tile entities
            w.Flush();
        }

        byte[] plain = raw.ToArray();

        using var compressed = new MemoryStream(Math.Max(64, plain.Length / 2));
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(plain, 0, plain.Length);

        return compressed.ToArray();
    }

    private static void WriteRuns(BinaryWriter writer, int xStart, int yStart, Func<int, int, Tile> getTile)
    {
        byte prevFlags = 0;
        byte[] prevPayload = Array.Empty<byte>();
        bool hasPrev = false;
        int additional = 0;

        for (int y = 0; y < SectionHeight; y++)
        {
            for (int x = 0; x < SectionWidth; x++)
            {
                Tile tile = getTile(xStart + x, yStart + y);
                (byte flags, byte[] payload) = EncodeTile(tile);

                if (hasPrev && flags == prevFlags && payload.AsSpan().SequenceEqual(prevPayload))
                {
                    additional++;
                    continue;
                }

                if (hasPrev)
                    WriteOne(writer, prevFlags, prevPayload, additional);

                prevFlags = flags;
                prevPayload = payload;
                additional = 0;
                hasPrev = true;
            }
        }

        if (hasPrev)
            WriteOne(writer, prevFlags, prevPayload, additional);
    }

    private static void WriteOne(BinaryWriter writer, byte flags, byte[] payload, int additional)
    {
        // 跑长位：0x40 = 1 字节计数，0x80 = 2 字节；规范要求 0x80 是替代 0x40，不是叠加。
        byte flagsWithRun = flags;
        if (additional > 0)
            flagsWithRun |= additional <= 255 ? (byte)0x40 : (byte)0x80;

        writer.Write(flagsWithRun);
        if (payload.Length > 0)
            writer.Write(payload, 0, payload.Length);

        if (additional > 0)
        {
            if (additional <= 255)
            {
                writer.Write((byte)additional);
            }
            else
            {
                writer.Write((byte)(additional & 0xFF));
                writer.Write((byte)(additional >> 8));
            }
        }
    }

    private static (byte Flags, byte[] Payload) EncodeTile(Tile tile)
    {
        byte flags = 0;
        var payload = new List<byte>(3);

        if (tile.Active)
        {
            flags |= 0x02;              // 实心
            payload.Add(tile.Type);     // 我们的类型都 <= 255，不需要 0x20
        }

        if (tile.Wall != 0)
        {
            flags |= 0x04;              // 有墙
            payload.Add(tile.Wall);
        }

        return (flags, payload.ToArray());
    }
}
