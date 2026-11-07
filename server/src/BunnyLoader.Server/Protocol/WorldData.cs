using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// 消息 7（WorldData）的模型与编解码。
///
/// 字段顺序严格来自 bybrooklyn/terrustia 的 docs/protocol-notes.md（对 1.4.5.7/1.4.5.8
/// 真实服务器逐字节验证过）。release 326 的固定部分为 163 字节；9 字符世界名下负载 173 字节。
///
/// ⚠ 移动端 1.4.5.8 是否与桌面端完全一致，尚未实测（待验证）。
/// </summary>
public sealed class WorldData
{
    /// <summary>release 326 新增了 dungeonX/dungeonY 两个 i16；325 没有。</summary>
    public bool Release326 { get; set; } = true;

    public int Time { get; set; }
    public bool DayTime { get; set; } = true;
    public bool BloodMoon { get; set; }
    public bool Eclipse { get; set; }
    public byte MoonPhase { get; set; }

    public short MaxTilesX { get; set; } = 4200;
    public short MaxTilesY { get; set; } = 1200;
    public short SpawnTileX { get; set; } = 2100;
    public short SpawnTileY { get; set; } = 300;
    public short WorldSurface { get; set; } = 350;
    public short RockLayer { get; set; } = 500;

    public int WorldId { get; set; } = 1;
    public string WorldName { get; set; } = "ProbeTiny";
    public byte GameMode { get; set; }
    public byte[] Guid { get; set; } = new byte[16];
    public ulong WorldGeneratorVersion { get; set; }
    public byte MoonType { get; set; }

    public byte[] BackgroundStyles { get; set; } = new byte[13];
    public byte IceBackStyle { get; set; }
    public byte JungleBackStyle { get; set; }
    public byte HellBackStyle { get; set; }

    public float WindSpeedTarget { get; set; }
    public byte NumClouds { get; set; }

    public int[] TreeX { get; set; } = new int[3];
    public byte[] TreeStyle { get; set; } = new byte[4];
    public int[] CaveBackX { get; set; } = new int[3];
    public byte[] CaveBackStyle { get; set; } = new byte[4];
    public byte[] TreeTops { get; set; } = new byte[13];

    public float MaxRaining { get; set; }

    /// <summary>11 个标志字节。全 0 = 全新世界；byte1 bit5 = crimson。</summary>
    public byte[] WorldFlags { get; set; } = new byte[11];

    public byte SundialCooldown { get; set; }
    public byte MoondialCooldown { get; set; }
    public short[] OreTiers { get; set; } = new short[7];
    public sbyte InvasionType { get; set; }
    public ulong LobbyId { get; set; }
    public float SandstormSeverity { get; set; }
    public List<(short X, short Y)> ExtraSpawns { get; } = new();
    public short DungeonX { get; set; }
    public short DungeonY { get; set; }

    /// <summary>解码时实际消费的字节数，用于判断是否有未解释的尾部。</summary>
    public int Consumed { get; private set; }

    public byte[] Encode()
    {
        using var ms = new MemoryStream(256);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Time);

            byte flags = 0;
            if (DayTime) flags |= 1;
            if (BloodMoon) flags |= 2;
            if (Eclipse) flags |= 4;
            bw.Write(flags);

            bw.Write(MoonPhase);
            bw.Write(MaxTilesX);
            bw.Write(MaxTilesY);
            bw.Write(SpawnTileX);
            bw.Write(SpawnTileY);
            bw.Write(WorldSurface);
            bw.Write(RockLayer);
            bw.Write(WorldId);
            bw.Write(WorldName ?? "");
            bw.Write(GameMode);
            WriteArray(bw, Guid, 16, nameof(Guid));
            bw.Write(WorldGeneratorVersion);
            bw.Write(MoonType);
            WriteArray(bw, BackgroundStyles, 13, nameof(BackgroundStyles));
            bw.Write(IceBackStyle);
            bw.Write(JungleBackStyle);
            bw.Write(HellBackStyle);
            bw.Write(WindSpeedTarget);
            bw.Write(NumClouds);
            WriteInt32s(bw, TreeX, 3, nameof(TreeX));
            WriteArray(bw, TreeStyle, 4, nameof(TreeStyle));
            WriteInt32s(bw, CaveBackX, 3, nameof(CaveBackX));
            WriteArray(bw, CaveBackStyle, 4, nameof(CaveBackStyle));
            WriteArray(bw, TreeTops, 13, nameof(TreeTops));
            bw.Write(MaxRaining);
            WriteArray(bw, WorldFlags, 11, nameof(WorldFlags));
            bw.Write(SundialCooldown);
            bw.Write(MoondialCooldown);
            if (OreTiers.Length != 7)
                throw new InvalidOperationException($"OreTiers 必须是 7 项，当前 {OreTiers.Length}");
            foreach (short tier in OreTiers)
                bw.Write(tier);
            bw.Write(InvasionType);
            bw.Write(LobbyId);
            bw.Write(SandstormSeverity);

            if (ExtraSpawns.Count > 255)
                throw new InvalidOperationException("额外出生点上限 255 个");
            bw.Write((byte)ExtraSpawns.Count);
            foreach ((short x, short y) in ExtraSpawns)
            {
                bw.Write(x);
                bw.Write(y);
            }

            if (Release326)
            {
                bw.Write(DungeonX);
                bw.Write(DungeonY);
            }

            bw.Flush();
        }
        return ms.ToArray();
    }

    public static WorldData Decode(byte[] payload, bool release326 = true)
    {
        using var ms = new MemoryStream(payload, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var w = new WorldData { Release326 = release326 };
        w.Time = br.ReadInt32();
        byte flags = br.ReadByte();
        w.DayTime = (flags & 1) != 0;
        w.BloodMoon = (flags & 2) != 0;
        w.Eclipse = (flags & 4) != 0;
        w.MoonPhase = br.ReadByte();
        w.MaxTilesX = br.ReadInt16();
        w.MaxTilesY = br.ReadInt16();
        w.SpawnTileX = br.ReadInt16();
        w.SpawnTileY = br.ReadInt16();
        w.WorldSurface = br.ReadInt16();
        w.RockLayer = br.ReadInt16();
        w.WorldId = br.ReadInt32();
        w.WorldName = br.ReadString();
        w.GameMode = br.ReadByte();
        w.Guid = ReadExact(br, 16);
        w.WorldGeneratorVersion = br.ReadUInt64();
        w.MoonType = br.ReadByte();
        w.BackgroundStyles = ReadExact(br, 13);
        w.IceBackStyle = br.ReadByte();
        w.JungleBackStyle = br.ReadByte();
        w.HellBackStyle = br.ReadByte();
        w.WindSpeedTarget = br.ReadSingle();
        w.NumClouds = br.ReadByte();
        w.TreeX = ReadInt32s(br, 3);
        w.TreeStyle = ReadExact(br, 4);
        w.CaveBackX = ReadInt32s(br, 3);
        w.CaveBackStyle = ReadExact(br, 4);
        w.TreeTops = ReadExact(br, 13);
        w.MaxRaining = br.ReadSingle();
        w.WorldFlags = ReadExact(br, 11);
        w.SundialCooldown = br.ReadByte();
        w.MoondialCooldown = br.ReadByte();
        w.OreTiers = new short[7];
        for (int i = 0; i < 7; i++)
            w.OreTiers[i] = br.ReadInt16();
        w.InvasionType = br.ReadSByte();
        w.LobbyId = br.ReadUInt64();
        w.SandstormSeverity = br.ReadSingle();
        int extra = br.ReadByte();
        for (int i = 0; i < extra; i++)
            w.ExtraSpawns.Add((br.ReadInt16(), br.ReadInt16()));
        if (release326)
        {
            w.DungeonX = br.ReadInt16();
            w.DungeonY = br.ReadInt16();
        }

        w.Consumed = (int)ms.Position;
        return w;
    }

    private static void WriteArray(BinaryWriter bw, byte[] data, int size, string name)
    {
        if (data.Length != size)
            throw new InvalidOperationException($"{name} 必须是 {size} 字节，当前 {data.Length}");
        bw.Write(data, 0, size);
    }

    private static void WriteInt32s(BinaryWriter bw, int[] data, int size, string name)
    {
        if (data.Length != size)
            throw new InvalidOperationException($"{name} 必须是 {size} 项，当前 {data.Length}");
        foreach (int value in data)
            bw.Write(value);
    }

    private static byte[] ReadExact(BinaryReader br, int size)
    {
        byte[] data = br.ReadBytes(size);
        if (data.Length != size)
            throw new EndOfStreamException($"需要 {size} 字节，只剩 {data.Length}");
        return data;
    }

    private static int[] ReadInt32s(BinaryReader br, int size)
    {
        int[] values = new int[size];
        for (int i = 0; i < size; i++)
            values[i] = br.ReadInt32();
        return values;
    }
}
