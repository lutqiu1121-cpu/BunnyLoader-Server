using System.IO.Compression;
using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// 一个瓦片。真实存档会用到 &gt;255 的类型、帧、液体，所以都要带上，
/// 否则发出去的分块流会和客户端失步。
/// </summary>
public readonly record struct Tile(
    bool Active,
    ushort Type,
    ushort Wall,
    short FrameX,
    short FrameY,
    byte LiquidFlags,      // flags1 的 0x08 水 / 0x10 岩浆 / 0x18 蜂蜜；0 = 无
    byte LiquidAmount)
{
    public static readonly Tile Air = new(false, 0, 0, 0, 0, 0, 0);

    public static Tile Solid(ushort type, ushort wall = 0) =>
        new(true, type, wall, 0, 0, 0, 0);
}

/// <summary>
/// 消息 10（TileSection）编码器。
///
/// 规范（bybrooklyn/terrustia docs/protocol-notes.md §"Tile sections"）：
///   负载整体是 raw DEFLATE；解压后：
///   i32 xStart, i32 yStart, i16 width, i16 height, RLE 瓦片…,
///   i16 chestCount, i16 signCount, i16 entityCount
///
/// 每瓦片：最多 4 个标志字节（每个 bit0 表示「后面还有下一个标志字节」），
/// 然后按固定顺序写字段：
///   1 类型低字节 [+高字节，flags1 0x20]
///   2 frameX, frameY（仅 frameImportant 类型）
///   3 方块油漆（本服务端不发）
///   4 墙壁低字节
///   5 墙油漆（本服务端不发）
///   6 液体量
///   7 墙壁高字节（flags3 0x40）
///   8 跑长（flags1 0x40 / 0x80）
/// 跑长是「第一个之后还有几个」；0x80 替代 0x40。
/// </summary>
public static class TileSection
{
    public const int SectionWidth = 200;
    public const int SectionHeight = 150;

    public const ushort TypeDirt = 0;
    public const ushort TypeStone = 1;
    public const ushort TypeGrass = 2;

    public static byte[] Encode(int sectionX, int sectionY,
                                Func<int, int, Tile> getTile,
                                Func<ushort, bool> isFrameImportant)
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

            WriteRuns(w, xStart, yStart, getTile, isFrameImportant);

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

    private static void WriteRuns(BinaryWriter writer, int xStart, int yStart,
                                  Func<int, int, Tile> getTile, Func<ushort, bool> isFrameImportant)
    {
        byte[] prevFlags = Array.Empty<byte>();
        byte[] prevBody = Array.Empty<byte>();
        bool hasPrev = false;
        int additional = 0;

        for (int y = 0; y < SectionHeight; y++)
        {
            for (int x = 0; x < SectionWidth; x++)
            {
                Tile tile = getTile(xStart + x, yStart + y);
                (byte[] flags, byte[] body) = EncodeTile(tile, isFrameImportant);

                if (hasPrev && flags.AsSpan().SequenceEqual(prevFlags) && body.AsSpan().SequenceEqual(prevBody))
                {
                    additional++;
                    continue;
                }

                if (hasPrev)
                    WriteOne(writer, prevFlags, prevBody, additional);

                prevFlags = flags;
                prevBody = body;
                additional = 0;
                hasPrev = true;
            }
        }

        if (hasPrev)
            WriteOne(writer, prevFlags, prevBody, additional);
    }

    private static void WriteOne(BinaryWriter writer, byte[] flags, byte[] body, int additional)
    {
        // 跑长位写在 flags1（第一个标志字节）上
        byte flags1 = flags[0];
        if (additional > 0)
            flags1 |= additional <= 255 ? (byte)0x40 : (byte)0x80;

        writer.Write(flags1);

        for (int i = 1; i < flags.Length; i++)
            writer.Write(flags[i]);

        if (body.Length > 0)
            writer.Write(body, 0, body.Length);

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

    private static (byte[] Flags, byte[] Body) EncodeTile(Tile tile, Func<ushort, bool> isFrameImportant)
    {
        byte f1 = 0, f3 = 0;
        var body = new List<byte>(8);

        // 1. 类型
        if (tile.Active)
        {
            f1 |= 0x02;

            if (tile.Type > 255)
            {
                f1 |= 0x20;
                body.Add((byte)(tile.Type & 0xFF));
                body.Add((byte)(tile.Type >> 8));
            }
            else
            {
                body.Add((byte)tile.Type);
            }

            // 2. 帧
            if (isFrameImportant(tile.Type))
            {
                body.Add((byte)(tile.FrameX & 0xFF));
                body.Add((byte)((tile.FrameX >> 8) & 0xFF));
                body.Add((byte)(tile.FrameY & 0xFF));
                body.Add((byte)((tile.FrameY >> 8) & 0xFF));
            }
        }

        // 4. 墙壁低字节
        if (tile.Wall != 0)
        {
            f1 |= 0x04;
            body.Add((byte)(tile.Wall & 0xFF));
        }

        // 6. 液体
        byte liquid = (byte)(tile.LiquidFlags & 0x18);
        if (liquid != 0 && tile.LiquidAmount > 0)
        {
            f1 |= liquid;
            body.Add(tile.LiquidAmount);
        }

        // 7. 墙壁高字节：在液体之后，由 flags3 0x40 表示
        if (tile.Wall > 255)
        {
            f3 |= 0x40;
            body.Add((byte)(tile.Wall >> 8));
        }

        // 组标志链：flags3 存在 → flags2 bit0；flags2 存在 → flags1 bit0
        bool has3 = f3 != 0;
        bool has2 = has3;
        if (has2)
            f1 |= 0x01;

        var flags = new List<byte>(3) { f1 };
        if (has2)
            flags.Add(has3 ? (byte)0x01 : (byte)0x00);   // flags2：bit0 = flags3 跟随
        if (has3)
            flags.Add(f3);

        return (flags.ToArray(), body.ToArray());
    }
}
