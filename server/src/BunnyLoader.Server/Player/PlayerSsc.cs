using System.Buffers.Binary;

namespace BunnyLoader.Server.Player;

/// <summary>
/// SSC（服务器端角色）：把 .plr 的权威数据下发到客户端。
///
/// 三个消息（**按技术主管给出的客户端实测字节，不是猜的原版布局**）：
///   id:16 PlayerLife   — byte player, int16 life, int16 lifeMax                     = 5B
///                        金标：`01 64 00 64 00`
///   id:42 PlayerMana   — byte player, int16 mana, int16 manaMax                     = 5B
///                        金标：`01 14 00 14 00`
///   id:5  槽位         — byte player, int16 slot, int16 stack, byte prefix,
///                        int16 itemId, byte tail                                   = 9B
///                        金标（客户端发来的真实 9 字节）：`01 00 00 01 00 24 b3 0d 00`
///                        → player=1, slot=0, stack=1, prefix=0x24, itemId=0x0db3(3507), tail=0
///
/// 槽位映射（Player.inventory 索引）：
///   0..49  主背包   ← .plr Inventory(50)
///   50..53 钱币     ← .plr Coins(4)
///   54..57 弹药     ← .plr Ammo(4)
///   59..78 装备/饰品 ← .plr Equipment(20)
///   79..88 染料     ← .plr Dye(10)
///   89..93 杂项装备 ← .plr Misc(5).Equip
///   94..98 杂项染料 ← .plr Misc(5).Dye
///
/// 本模块只**读** .plr、只**构造**下发帧，不改动 id:4 的任何字节。
/// </summary>
public sealed class PlayerSsc
{
    /// <summary>要下发的物品槽位（槽号 → 物品）。顺序即发送顺序。</summary>
    public List<(int Slot, PlayerItem Item)> Slots { get; } = new();

    public PlayerFileData Data { get; }

    public string SourcePath => Data.FilePath;

    private PlayerSsc(PlayerFileData data) => Data = data;

    /// <summary>
    /// 解析 .plr 并建立槽位映射。显式路径优先，否则自动发现（不需要改启动命令）：
    ///   1) --player-file / PLAYER_FILE
    ///   2) player.plr（程序目录 / 当前目录）
    ///   3) debug/*.plr（程序目录 / 当前目录）
    /// 找不到就返回 null（SSC 关闭，服务照常跑）。
    /// </summary>
    public static PlayerSsc? TryLoad(string? explicitPath, JsonLogger log)
    {
        string? path = null;
        string how;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            path = explicitPath;
            how = "显式参数";
        }
        else
        {
            how = "自动发现";
            var candidates = new List<string>();
            foreach (string dir in SearchDirs())
            {
                candidates.Add(Path.Combine(dir, "player.plr"));
            }

            foreach (string dir in SearchDirs())
            {
                string debug = Path.Combine(dir, "debug");
                if (Directory.Exists(debug))
                    candidates.AddRange(Directory.GetFiles(debug, "*.plr").OrderBy(p => p, StringComparer.Ordinal));
            }

            path = candidates.FirstOrDefault(File.Exists);
        }

        if (path is null)
        {
            log.Warn("ssc.disabled", ("reason", "找不到 .plr（可用 --player-file 或 PLAYER_FILE 指定），SSC 不下发"));
            return null;
        }

        PlayerFileData data;
        try
        {
            data = PlayerFileReader.ReadFromFile(path);
        }
        catch (Exception e)
        {
            log.Error("ssc.load.fail", ("file", path), ("error", e.GetType().Name + ": " + e.Message));
            return null;
        }

        var ssc = new PlayerSsc(data);
        ssc.BuildSlots();
        return ssc;
    }

    private static string[] SearchDirs() => new[]
    {
        AppContext.BaseDirectory,
        Directory.GetCurrentDirectory(),
    }.Distinct(StringComparer.Ordinal).ToArray();

    private void BuildSlots()
    {
        Slots.Clear();

        for (int i = 0; i < Data.Inventory.Count; i++)
            Slots.Add((i, Data.Inventory[i]));

        for (int i = 0; i < Data.Coins.Count; i++)
            Slots.Add((50 + i, Data.Coins[i]));

        for (int i = 0; i < Data.Ammo.Count; i++)
            Slots.Add((54 + i, Data.Ammo[i]));

        for (int i = 0; i < Data.Equipment.Count; i++)
            Slots.Add((59 + i, Data.Equipment[i]));

        for (int i = 0; i < Data.Dye.Count; i++)
            Slots.Add((79 + i, Data.Dye[i]));

        for (int i = 0; i < Data.Misc.Count; i++)
        {
            Slots.Add((89 + i, Data.Misc[i].Equip));
            Slots.Add((94 + i, Data.Misc[i].Dye));
        }
    }

    /// <summary>
    /// id:5 的单槽 payload，**9 字节**，字段顺序严格按客户端实测：
    ///   [0] player  [1..2] slot  [3..4] stack  [5] prefix  [6..7] itemId  [8] tail(=0)
    /// </summary>
    public static byte[] InventorySlotPayload(byte player, int slot, PlayerItem item)
    {
        byte[] payload = new byte[9];
        payload[0] = player;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1, 2), (short)slot);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(3, 2), (short)Math.Clamp(item.Count, 0, short.MaxValue));
        payload[5] = (byte)item.Prefix;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(6, 2), (short)item.Id);
        payload[8] = 0;                       // tail，实测恒为 0
        return payload;
    }

    /// <summary>id:16 的 payload，**5 字节**：player, life, lifeMax（金标 `01 64 00 64 00`）。</summary>
    public static byte[] LifePayload(byte player, int life, int lifeMax)
    {
        byte[] payload = new byte[5];
        payload[0] = player;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1, 2), (short)Math.Clamp(life, 0, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(3, 2), (short)Math.Clamp(lifeMax, 0, short.MaxValue));
        return payload;
    }

    /// <summary>id:42 的 payload（5 字节）：魔力。</summary>
    public static byte[] ManaPayload(byte player, int mana, int manaMax)
    {
        byte[] payload = new byte[5];
        payload[0] = player;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1, 2), (short)Math.Clamp(mana, 0, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(3, 2), (short)Math.Clamp(manaMax, 0, short.MaxValue));
        return payload;
    }

    public int NonEmptySlots => Slots.Count(s => !s.Item.IsEmpty);
}