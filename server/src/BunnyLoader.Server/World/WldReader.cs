using System.Text;
using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server.World;

/// <summary>
/// 原版 .wld 读取器。
///
/// 依据（bybrooklyn/terrustia docs/protocol-notes.md §"The .wld save format"，
/// 对 1.4.5.7 真实服务器与本实现做过逐字节比对）：
///
///   i32 version, [7] "relogic", u8 fileType, u32 revision, u64 favorite,
///   i16 sectionCount, i32×count 各段绝对偏移,
///   u16 tileTypeCount, 然后是重要性位图（LSB 优先）
///
///   第 0 段 = 世界头；第 1 段 = 瓦片；第 2 段 = 箱子；第 3 段 = 告示牌
///
/// 瓦片流与网络消息 10 **同一套编码**（标志链 / 字段顺序 / 跑长），
/// 唯一差别：文件是**逐列**遍历，网络分块是逐行。
/// </summary>
public static class WldReader
{
    public static WldWorld Load(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        int version = br.ReadInt32();
        string magic = Encoding.ASCII.GetString(br.ReadBytes(7));
        if (magic != "relogic")
            throw new InvalidDataException($"不是泰拉瑞亚世界文件（magic={magic}）");

        byte fileType = br.ReadByte();
        if (fileType != 2)
            throw new InvalidDataException($"文件类型不是世界（fileType={fileType}）");

        _ = br.ReadUInt32();       // revision
        _ = br.ReadUInt64();       // favorite

        short sectionCount = br.ReadInt16();
        int[] offsets = new int[sectionCount];
        for (int i = 0; i < sectionCount; i++)
            offsets[i] = br.ReadInt32();

        if (sectionCount < 2)
            throw new InvalidDataException($"段数不足（{sectionCount}），无法定位瓦片段");

        // 重要性位图：LSB 优先，每 8 个换一个字节
        int typeCount = br.ReadUInt16();
        var frameImportant = new bool[typeCount];
        byte bits = 0;
        for (int i = 0; i < typeCount; i++)
        {
            if (i % 8 == 0)
                bits = br.ReadByte();
            frameImportant[i] = (bits & (1 << (i % 8))) != 0;
        }

        // ---- 第 0 段：世界头 ----
        fs.Position = offsets[0];

        string name = br.ReadString();
        string seed = version >= 179 ? br.ReadString() : "";
        _ = br.ReadUInt64();                                   // generator version
        if (version >= 181)
            _ = br.ReadBytes(16);                              // 世界唯一 id
        _ = br.ReadInt32();                                    // world id
        _ = br.ReadInt32(); _ = br.ReadInt32(); _ = br.ReadInt32(); _ = br.ReadInt32(); // 像素矩形
        int maxTilesY = br.ReadInt32();                        // ⚠ 高度在前
        int maxTilesX = br.ReadInt32();
        int gameMode = version >= 209 ? br.ReadInt32() : 0;

        bool drunk = false, getGood = false, tenth = false, dontStarve = false;
        bool notTheBees = false, remix = false, noTraps = false, zenith = false, skyblock = false;
        if (version >= 222) drunk = br.ReadBoolean();
        if (version >= 227) getGood = br.ReadBoolean();
        if (version >= 238) tenth = br.ReadBoolean();
        if (version >= 239) dontStarve = br.ReadBoolean();
        if (version >= 241) notTheBees = br.ReadBoolean();
        if (version >= 249) remix = br.ReadBoolean();
        if (version >= 266) noTraps = br.ReadBoolean();
        if (version >= 267) zenith = br.ReadBoolean();
        if (version >= 302) skyblock = br.ReadBoolean();

        if (version >= 141) _ = br.ReadInt64();                // creation time
        if (version >= 284) _ = br.ReadInt64();                // last played

        _ = br.ReadByte();                                     // moon type
        for (int i = 0; i < 3; i++) _ = br.ReadInt32();        // treeX
        for (int i = 0; i < 4; i++) _ = br.ReadInt32();        // treeStyle
        for (int i = 0; i < 3; i++) _ = br.ReadInt32();        // caveBackX
        for (int i = 0; i < 4; i++) _ = br.ReadInt32();        // caveBackStyle
        _ = br.ReadInt32(); _ = br.ReadInt32(); _ = br.ReadInt32(); // ice/jungle/hell

        int spawnX = br.ReadInt32();
        int spawnY = br.ReadInt32();

        double worldSurface = br.ReadDouble();
        double rockLayer = br.ReadDouble();
        double time = br.ReadDouble();
        bool dayTime = br.ReadBoolean();
        _ = br.ReadInt32();                                    // moon phase
        _ = br.ReadBoolean();                                  // blood moon
        _ = br.ReadBoolean();                                  // eclipse
        _ = br.ReadInt32(); _ = br.ReadInt32();                // dungeon
        bool crimson = br.ReadBoolean();

        if (maxTilesX <= 0 || maxTilesY <= 0)
            throw new InvalidDataException($"世界尺寸非法：{maxTilesX}x{maxTilesY}");

        // ---- 第 1 段：瓦片（逐列）----
        fs.Position = offsets[1];
        var tiles = new Tile[maxTilesX * maxTilesY];
        DecodeTiles(br, tiles, maxTilesX, maxTilesY, frameImportant);

        var world = new WldWorld
        {
            Version = version,
            Name = name,
            Seed = seed,
            MaxTilesX = maxTilesX,
            MaxTilesY = maxTilesY,
            SpawnTileX = spawnX,
            SpawnTileY = spawnY,
            WorldSurface = worldSurface,
            RockLayer = rockLayer,
            Crimson = crimson,
            DayTime = dayTime,
            GameMode = gameMode,
            Tiles = tiles,
        };

        if (sectionCount >= 3)
        {
            long after = fs.Position;
            if (after != offsets[2])
                world.Notes.Add($"瓦片段结束于 {after}，但箱子段在 {offsets[2]}（差 {after - offsets[2]} 字节，可能字段有偏差）");
        }

        world.Notes.Add($"世界 {name} {maxTilesX}x{maxTilesY} 出生点 ({spawnX},{spawnY}) 地表 {worldSurface:F0} 版本 {version}");
        return world;
    }

    /// <summary>逐列解码瓦片流；跑长可能与列边界交叉，所以用线性游标写回。</summary>
    private static void DecodeTiles(BinaryReader br, Tile[] tiles, int maxX, int maxY, bool[] frameImportant)
    {
        long total = (long)maxX * maxY;
        long written = 0;

        while (written < total)
        {
            byte flags1 = br.ReadByte();
            byte flags2 = 0, flags3 = 0;

            if ((flags1 & 0x01) != 0)
            {
                flags2 = br.ReadByte();
                if ((flags2 & 0x01) != 0)
                {
                    flags3 = br.ReadByte();
                    if ((flags3 & 0x01) != 0)
                        _ = br.ReadByte();      // flags4：我们不用
                }
            }

            bool active = (flags1 & 0x02) != 0;
            bool hasWall = (flags1 & 0x04) != 0;
            bool liquid = (flags1 & 0x18) != 0;
            bool typeHigh = (flags1 & 0x20) != 0;

            // 1. 方块类型（低字节，必要时高字节）
            int type = 0;
            if (active)
            {
                type = br.ReadByte();
                if (typeHigh)
                    type |= br.ReadByte() << 8;
            }

            // 2. 帧（只有 frameImportant 的方块才有）
            if (active && type < frameImportant.Length && frameImportant[type])
            {
                _ = br.ReadInt16();
                _ = br.ReadInt16();
            }

            // 3. 方块油漆
            if ((flags3 & 0x08) != 0)
                _ = br.ReadByte();

            // 4. 墙壁（低字节）与墙油漆
            int wall = 0;
            if (hasWall)
                wall = br.ReadByte();
            if ((flags3 & 0x10) != 0)
                _ = br.ReadByte();

            // 5. 液体量
            if (liquid)
                _ = br.ReadByte();

            // 6. 墙壁高字节
            if ((flags3 & 0x40) != 0)
                wall |= br.ReadByte() << 8;

            // 7. 跑长
            int run = 1;
            if ((flags1 & 0x40) != 0)
                run += br.ReadByte();
            else if ((flags1 & 0x80) != 0)
                run += br.ReadByte() | (br.ReadByte() << 8);

            var tile = new Tile(active, (byte)Math.Min(type, 255), (byte)Math.Min(wall, 255));

            for (int k = 0; k < run && written < total; k++)
            {
                int x = (int)(written / maxY);
                int y = (int)(written % maxY);
                tiles[y * maxX + x] = tile;
                written++;
            }
        }
    }
}
