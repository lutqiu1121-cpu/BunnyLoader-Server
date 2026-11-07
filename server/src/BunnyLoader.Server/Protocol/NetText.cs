using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// NetworkText 的线格式（terrustia protocol-notes；BL 侧同构实现在
/// Bunny-Loader 的 Terraria/Localization/NetworkText.TML.cs:25-57）：
///   [u8 mode][string text]，mode != 0 时再跟 [u8 参数个数] + 递归的 NetworkText。
/// mode：0 字面量，1 格式化，2 本地化键。
/// </summary>
public static class NetText
{
    public const byte ModeLiteral = 0;
    public const byte ModeFormatted = 1;
    public const byte ModeLocalizationKey = 2;

    /// <summary>踢人理由最常用：字面量，无参数。</summary>
    public static byte[] Literal(string text)
    {
        using var ms = new MemoryStream(text.Length + 8);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(ModeLiteral);
            bw.Write(text);
            bw.Flush();
        }
        return ms.ToArray();
    }

    /// <summary>把收到的踢人理由读成可读文本；解析失败返回 null。</summary>
    public static string? Describe(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            byte mode = br.ReadByte();
            string text = br.ReadString();
            if (mode == ModeLiteral)
                return text;
            int count = br.ReadByte();
            return $"[mode={mode} args={count}] {text}";
        }
        catch
        {
            return null;
        }
    }
}
