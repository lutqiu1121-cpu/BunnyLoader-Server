using System.Buffers.Binary;
using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// 线格式基础。全部依据公开资料 + 上游仓库交叉验证：
///   - 帧：[u16 总长][u8 消息号][payload]，长度含自身 2 字节（terrustia protocol-notes）
///   - 字符串：7-bit LEB128 字节长度 + UTF-8（.NET BinaryWriter 语义）
///   - 上游证据：ModNet.cpp:218 的 kickSelf 构造的是 [id][payload]，长度前缀由调用方加
/// </summary>
public static class Codec
{
    /// <summary>BL 模组介绍的前缀；带它的是「介绍」，不带的是原版 UUID。</summary>
    public const string HelloPrefix = "bunnyloader-cs:";

    /// <summary>BL 的 C# 私有包模块号（ModNet.h:11）。</summary>
    public const ushort ModuleId = 0xB17F;

    /// <summary>欢迎包（ModNet.cpp:27,245）。</summary>
    public const byte KindWelcome = 200;

    /// <summary>同步计划（ServerSyncHooks.cs:15）。</summary>
    public const byte KindSyncPlan = 11;

    public static byte[] String(string text)
    {
        using var ms = new MemoryStream(text.Length + 8);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(text);
            bw.Flush();
        }
        return ms.ToArray();
    }

    public static string ReadString(byte[] payload)
    {
        using var ms = new MemoryStream(payload, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        return br.ReadString();
    }

    /// <summary>组一帧：[u16 总长][u8 id][payload]。</summary>
    public static byte[] Frame(byte id, ReadOnlySpan<byte> payload)
    {
        int total = payload.Length + 3;
        if (total > 0xFFFF)
            throw new InvalidOperationException($"帧过大: {total} 字节，上限 65535");

        byte[] frame = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), (ushort)total);
        frame[2] = id;
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    public static byte[] UInt16(ushort value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    public static string Hex(ReadOnlySpan<byte> data, int max = 32)
    {
        int count = Math.Min(data.Length, max);
        var text = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                text.Append(' ');
            text.Append(data[i].ToString("x2"));
        }
        if (data.Length > count)
            text.Append(" …");
        return text.ToString();
    }
}
