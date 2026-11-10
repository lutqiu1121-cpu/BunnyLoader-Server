using System.Buffers.Binary;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// 消息 13（PlayerControls）的只读解析。
///
/// 布局（docs 与 .research/p.md 一致）：
///   u8 player | u8 ctrl1..ctrl4 | u8 selectedItem | Vector2 position | [Vector2 velocity] | …
/// 位置固定从偏移 6 开始，单位是像素（格 * 16）。
///
/// 旧实现把 payload[1..3] 当 i16 读成「格坐标」，读到的其实是控制位——只有 0，
/// 于是窗口跟随永远围着 (0,0) 发块。这里只修解析，不改任何发送逻辑。
/// </summary>
public static class PlayerControls13
{
    /// <summary>位置字段的固定偏移：1 字节玩家序号 + 4 字节控制位 + 1 字节选中物品。</summary>
    public const int PositionOffset = 6;

    /// <summary>位置字段至少需要的负载长度（不含可选的 velocity）。</summary>
    public const int MinLength = PositionOffset + 8;

    public static bool TryParsePosition(ReadOnlySpan<byte> payload,
                                        out float pixelX, out float pixelY,
                                        out int tileX, out int tileY)
    {
        pixelX = 0f;
        pixelY = 0f;
        tileX = 0;
        tileY = 0;

        if (payload.Length < MinLength)
            return false;

        float x = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(PositionOffset, 4));
        float y = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(PositionOffset + 4, 4));

        if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
            return false;

        pixelX = x;
        pixelY = y;
        tileX = (int)Math.Floor(x / 16.0);
        tileY = (int)Math.Floor(y / 16.0);
        return true;
    }
}
