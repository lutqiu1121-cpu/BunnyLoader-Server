using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server.Experiment;

/// <summary>
/// R6 一次性候选注入实验（v2）：长度扫描 + 字段顺序参考。
///
/// v1 的 3 个候选（22B / 24B / 20B）真机实测结果是「客户端静默丢弃」：
/// 不崩、不回帧、连接持续存活。所以 v2 把它们保留为 idx 1/2/3 作对照
/// （hex 与 v1 逐字节一致），再补 5 个长度变体，一轮扫过常见宽度。
///
/// 字段顺序仅参考公开的 NetMessage / MessageID 文档自行构造，
/// 未复制 TShock / OTAPI / Terraria 任何源码，也不代表协议结论。
/// 这些是「探针」，不是 serializer：真机验证通过前绝不并入正式路径。
///
/// 本文件只造帧，不碰 socket、不碰状态机；发送一律走 Session.SendAsync，
/// 因此同样过 EnsureFrameLength、frame.send 日志与 s2c capture。
/// </summary>
public static class CandidateInjector
{
    public static List<(int Index, byte Id, string Note, byte[] Frame)> Build(int spawnTileX, int spawnTileY)
    {
        var list = new List<(int, byte, string, byte[])>();

        // 出生点像素坐标：tile * 16，稍微偏移避免正好卡在方块里。
        float px = spawnTileX * 16f + 8f;
        float py = spawnTileY * 16f + 8f;

        // ==== idx 1..3：v1 原样保留，用于同包对照 ====
        list.Add((1, MessageId.ItemDrop, "v1 白皮书 22B 推导（对照）",
            Codec.Frame(MessageId.ItemDrop, ItemDrop22(px, py))));
        list.Add((2, MessageId.ItemDrop, "v1 原版 24B 形态（对照）",
            Codec.Frame(MessageId.ItemDrop, ItemDrop24(px, py))));
        list.Add((3, MessageId.NpcUpdate, "v1 20B 占位（对照）",
            Codec.Frame(MessageId.NpcUpdate, NpcUpdate20(px, py))));

        // ==== idx 4..8：v2 长度扫描 ====
        list.Add((4, MessageId.ItemDrop, "25B：24B + u8 owner",
            Codec.Frame(MessageId.ItemDrop, ItemDrop25(px, py))));
        list.Add((5, MessageId.ItemDrop, "26B：itemId 为 i32（模组 ID 可能 >65535）",
            Codec.Frame(MessageId.ItemDrop, ItemDrop26(px, py))));
        list.Add((6, MessageId.ItemDrop, "28B：i32 itemIndex + 尾部 i16",
            Codec.Frame(MessageId.ItemDrop, ItemDrop28(px, py))));
        list.Add((7, MessageId.NpcUpdate, "28B：补 target/type/life/maxLife/flags/dir",
            Codec.Frame(MessageId.NpcUpdate, NpcUpdate28(px, py))));
        list.Add((8, MessageId.NpcUpdate, "36B：28B + f32×2（AI 占位）",
            Codec.Frame(MessageId.NpcUpdate, NpcUpdate36(px, py))));

        return list;
    }

    // ---- id:21 变体 ----
    private static byte[] ItemDrop22(float px, float py)
    {
        var a = new byte[22];
        I16(a, 0, 0); PutF32(a, 2, px); PutF32(a, 6, py);
        PutF32(a, 10, 0f); PutF32(a, 14, 0f);
        I16(a, 18, 1); a[20] = 0; a[21] = 1;          // prefix, itemId(u8)
        return a;
    }

    private static byte[] ItemDrop24(float px, float py)
    {
        var b = new byte[24];
        I16(b, 0, 0); PutF32(b, 2, px); PutF32(b, 6, py);
        PutF32(b, 10, 0f); PutF32(b, 14, 0f);
        I16(b, 18, 1); b[20] = 0; b[21] = 0;           // prefix, noDelay/owner
        I16(b, 22, 1);                                  // itemId(i16)
        return b;
    }

    private static byte[] ItemDrop25(float px, float py)
    {
        var c = new byte[25];
        Buffer.BlockCopy(ItemDrop24(px, py), 0, c, 0, 24);
        c[24] = 0;                                      // owner
        return c;
    }

    private static byte[] ItemDrop26(float px, float py)
    {
        var d = new byte[26];
        I16(d, 0, 0); PutF32(d, 2, px); PutF32(d, 6, py);
        PutF32(d, 10, 0f); PutF32(d, 14, 0f);
        I16(d, 18, 1); d[20] = 0; d[21] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(22, 4), 1);  // itemId(i32)
        return d;
    }

    private static byte[] ItemDrop28(float px, float py)
    {
        var e = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0, 4), 0);    // itemIndex(i32)
        PutF32(e, 4, px); PutF32(e, 8, py); PutF32(e, 12, 0f); PutF32(e, 16, 0f);
        I16(e, 20, 1); e[22] = 0; e[23] = 0;
        I16(e, 24, 1); I16(e, 26, 0);                  // itemId(i16) + extra
        return e;
    }

    // ---- id:23 变体 ----
    private static byte[] NpcUpdate20(float px, float py)
    {
        var a = new byte[20];
        I16(a, 0, 0); PutF32(a, 2, px); PutF32(a, 6, py);
        PutF32(a, 10, 0f); PutF32(a, 14, 0f);
        I16(a, 18, 1);
        return a;
    }

    private static byte[] NpcUpdate28(float px, float py)
    {
        var b = new byte[28];
        I16(b, 0, 0); PutF32(b, 2, px); PutF32(b, 6, py);
        PutF32(b, 10, 0f); PutF32(b, 14, 0f);
        I16(b, 18, 0);      // target
        I16(b, 20, 1);      // type
        I16(b, 22, 100);    // life
        I16(b, 24, 100);    // lifeMax
        b[26] = 0;          // flags
        b[27] = 1;          // direction
        return b;
    }

    private static byte[] NpcUpdate36(float px, float py)
    {
        var c = new byte[36];
        Buffer.BlockCopy(NpcUpdate28(px, py), 0, c, 0, 28);
        PutF32(c, 28, 0f); PutF32(c, 32, 0f);         // ai[0], ai[1]
        return c;
    }

    private static void I16(byte[] dst, int offset, short value)
        => BinaryPrimitives.WriteInt16LittleEndian(dst.AsSpan(offset, 2), value);

    private static void PutF32(byte[] dst, int offset, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(dst.AsSpan(offset, 4), value);
}
