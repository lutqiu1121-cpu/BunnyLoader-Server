using System.Buffers.Binary;
using System.Collections.Generic;

using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server.Experiment;

/// <summary>
/// R6 一次性候选注入实验：让客户端看见一个掉落物 / 敌怪，从而逼出 c2s 的 id:24 / 44 真帧。
///
/// 这里全部是「未验证假设」，不是协议结论：
///   - A：白皮书 5.1 的 22B 推导 [i16][f32×4][i16][u8][u8]，itemId 压成 u8
///   - B：原版 24B 形态 [i16][f32×4][i16][u8][u8][i16 itemId]
///   - C：id:23 占位，结构无可靠推导，高风险
///
/// 本文件只负责「造帧」，不碰 socket、不碰状态机。发送一律由 Session.SendAsync
/// 统一出口完成（因此同样过 EnsureFrameLength、frame.send 日志与 s2c capture）。
/// 真机验证通过前绝不并入正式协议路径。
/// </summary>
public static class CandidateInjector
{
    public static List<(int Index, byte Id, string Note, byte[] Frame)> Build(int spawnTileX, int spawnTileY)
    {
        var list = new List<(int, byte, string, byte[])>();

        // 出生点像素坐标：tile * 16，稍微偏移避免正好卡在方块里。
        float px = spawnTileX * 16f + 8f;
        float py = spawnTileY * 16f + 8f;

        // ---- A: 22B，白皮书推导（itemId 为 u8）----
        var a = new byte[22];
        BinaryPrimitives.WriteInt16LittleEndian(a.AsSpan(0, 2), 0);   // itemIndex（候选）
        PutF32(a, 2, px);                                             // x
        PutF32(a, 6, py);                                             // y
        PutF32(a, 10, 0f);                                            // vx
        PutF32(a, 14, 0f);                                            // vy
        BinaryPrimitives.WriteInt16LittleEndian(a.AsSpan(18, 2), 1);   // stack
        a[20] = 0;                                                    // prefix / flags（候选）
        a[21] = 1;                                                    // itemId（u8，候选）
        list.Add((1, MessageId.ItemDrop, "白皮书 22B 推导，未验证", Codec.Frame(MessageId.ItemDrop, a)));

        // ---- B: 24B，原版形态（末尾 itemId 为 i16）----
        var b = new byte[24];
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0, 2), 0);   // itemIndex（候选）
        PutF32(b, 2, px);
        PutF32(b, 6, py);
        PutF32(b, 10, 0f);
        PutF32(b, 14, 0f);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(18, 2), 1);   // stack
        b[20] = 0;                                                    // prefix（候选）
        b[21] = 0;                                                    // noDelay / owner（候选）
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22, 2), 1);   // itemId（i16，候选）
        list.Add((2, MessageId.ItemDrop, "原版 24B 形态，未验证", Codec.Frame(MessageId.ItemDrop, b)));

        // ---- C: id:23 占位，无可靠推导，高风险 ----
        // 只放最小骨架，用于观察客户端是否解析该号；崩了就据此剔除。
        var c = new byte[20];
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0, 2), 0);   // npcIndex（占位）
        PutF32(c, 2, px);
        PutF32(c, 6, py);
        PutF32(c, 10, 0f);
        PutF32(c, 14, 0f);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(18, 2), 1);   // npcType（占位）
        list.Add((3, MessageId.NpcUpdate, "无可靠推导，高风险占位", Codec.Frame(MessageId.NpcUpdate, c)));

        return list;
    }

    private static void PutF32(byte[] dst, int offset, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(dst.AsSpan(offset, 4), value);
}
