using System.Buffers.Binary;

namespace BunnyLoader.Server.Player;

/// <summary>
/// SSC 下发帧的构造函数与槽位映射。**纯静态工具**，数据源由 <see cref="PlayerProfile"/> 提供。
///
/// 三个消息（按技术主管给出的客户端实测字节，不是猜的原版布局）：
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
/// </summary>
public static class PlayerSsc
{
    /// <summary>把 .plr 的物品列表映射成 98 个客户端槽位。</summary>
    public static List<(int Slot, PlayerItem Item)> MapSlots(PlayerFileData d)
    {
        var slots = new List<(int, PlayerItem)>(98);

        for (int i = 0; i < d.Inventory.Count && i < 50; i++)
            slots.Add((i, d.Inventory[i]));

        for (int i = 0; i < d.Coins.Count && i < 4; i++)
            slots.Add((50 + i, d.Coins[i]));

        for (int i = 0; i < d.Ammo.Count && i < 4; i++)
            slots.Add((54 + i, d.Ammo[i]));

        for (int i = 0; i < d.Equipment.Count && i < 20; i++)
            slots.Add((59 + i, d.Equipment[i]));

        for (int i = 0; i < d.Dye.Count && i < 10; i++)
            slots.Add((79 + i, d.Dye[i]));

        for (int i = 0; i < d.Misc.Count && i < 5; i++)
        {
            slots.Add((89 + i, d.Misc[i].Equip));
            slots.Add((94 + i, d.Misc[i].Dye));
        }

        return slots;
    }

    /// <summary>
    /// id:5 的单槽 payload，**9 字节**，字段顺序严格按客户端实测：
    ///   [0] player  [1..2] slot  [3..4] stack  [5] prefix  [6..7] itemId  [8] tail(=0)
    /// </summary>
    public static byte[] InventorySlotPayload(byte player, int slot, PlayerItem item)
    {
        byte[] payload = new byte[9];
        if (item.IsEmpty)
            return payload;

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

    /// <summary>id:42 的 payload，**5 字节**：player, mana, manaMax（金标 `01 14 00 14 00`）。</summary>
    public static byte[] ManaPayload(byte player, int mana, int manaMax)
    {
        byte[] payload = new byte[5];
        payload[0] = player;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1, 2), (short)Math.Clamp(mana, 0, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(3, 2), (short)Math.Clamp(manaMax, 0, short.MaxValue));
        return payload;
    }
}