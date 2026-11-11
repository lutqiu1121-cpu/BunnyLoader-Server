using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// id:4（SyncPlayer）的**已实测布局**读取器（魔改协议，不是原版布局）。
///
/// 结论来自真实数据交叉验证：手机客户端发来的 40 字节
///   `01 07 02 d0 cc 4c 3e c4 03 43 75 70 00 00 00 00 ff ff ff ff bb a9 69 5a 4b
///    ff ff ff ff ff ff ab c0 ab ff ff ff 00 10 00`
/// 与本角色 .plr 存档逐字节对上：
///   [1]=0x07 skinVariant=7、[7]=0xc4 hair=196、[8..11]=`03 "Cup"`、
///   [12..15]=hairDye/hideVisuals×2/hideMisc 全 0、[16..36] 7 个颜色 7/7 命中、
///   [37]=difficulty=0、[38..39]=`10 00`。
///   [3..6]=`d0 cc 4c 3e` 是 f32 0.2（0x3e4ccccd）——客户端自加字段。
///
/// 本类**不做任何猜测**：长度不是 40 就报 LengthOk=false，字段一律按固定偏移读。
/// 回传一律 <see cref="BuildEcho"/> 原样 Clone（只改第 0 字节 whoAmI）。
/// </summary>
public static class SyncPlayer4
{
    /// <summary>魔改 id:4 的固定长度。发出去必须正好这么多字节。</summary>
    public const int PayloadLength = 40;

    // 固定偏移（实测）
    public const int OffPlayerId = 0;
    public const int OffSkinVariant = 1;
    public const int OffUnknown2 = 2;
    public const int OffFloatField = 3;      // f32 0.2
    public const int OffHair = 7;
    public const int OffName = 8;            // 1 字节长度前缀 + UTF-8
    public const int OffHairDye = 12;
    public const int OffHideVisual = 13;
    public const int OffHideMisc = 15;
    public const int OffColors = 16;          // 7 × 3 字节 RGB
    public const int OffDifficulty = 37;
    public const int OffTail = 38;

    public sealed class Report
    {
        public int Length { get; init; }
        public bool LengthOk { get; init; }
        public string HexSpaced { get; init; } = "-";
        public string Ascii { get; init; } = "-";

        public int PlayerId { get; init; }
        public int SkinVariant { get; init; }
        public int Unknown2 { get; init; }
        public float FloatField { get; init; }
        public int Hair { get; init; }

        public int NameOffset { get; init; } = OffName;
        public int NamePrefixBytes { get; init; }
        public string Name { get; init; } = "";

        public int HairDye { get; init; }
        public int HideVisual0 { get; init; }
        public int HideVisual1 { get; init; }
        public int HideMisc { get; init; }

        public string HairColor { get; init; } = "-";
        public string SkinColor { get; init; } = "-";
        public string EyeColor { get; init; } = "-";
        public string ShirtColor { get; init; } = "-";
        public string UnderShirtColor { get; init; } = "-";
        public string PantsColor { get; init; } = "-";
        public string ShoeColor { get; init; } = "-";

        public int Difficulty { get; init; }
        public string Tail { get; init; } = "-";

        /// <summary>一行紧凑字段表，日志里肉眼核对用。</summary>
        public string Fields { get; init; } = "-";

        /// <summary>7 个颜色按 id:4 顺序排列。</summary>
        public (string Name, string Hex)[] Colors => new[]
        {
            ("hairColor", HairColor),
            ("skinColor", SkinColor),
            ("eyeColor", EyeColor),
            ("shirtColor", ShirtColor),
            ("underShirtColor", UnderShirtColor),
            ("pantsColor", PantsColor),
            ("shoeColor", ShoeColor),
        };
    }

    public static Report Analyze(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            // 长度不对：不猜字段，只把原始字节交出去
            return new Report
            {
                Length = payload.Length,
                LengthOk = false,
                HexSpaced = ToSpacedHex(payload),
                Ascii = AsciiView(payload),
                Fields = $"长度 {payload.Length} ≠ {PayloadLength}：不做字段解析（不猜）",
            };
        }

        int nameLen = payload[OffName];
        string name = nameLen > 0 && OffName + 1 + nameLen <= payload.Length
            ? Encoding.UTF8.GetString(payload.Slice(OffName + 1, nameLen))
            : "";

        float floatField = BitConverter.Int32BitsToSingle(
            payload[OffFloatField]
            | (payload[OffFloatField + 1] << 8)
            | (payload[OffFloatField + 2] << 16)
            | (payload[OffFloatField + 3] << 24));

        var r = new Report
        {
            Length = payload.Length,
            LengthOk = true,
            HexSpaced = ToSpacedHex(payload),
            Ascii = AsciiView(payload),
            PlayerId = payload[OffPlayerId],
            SkinVariant = payload[OffSkinVariant],
            Unknown2 = payload[OffUnknown2],
            FloatField = floatField,
            Hair = payload[OffHair],
            NamePrefixBytes = 1,
            Name = name,
            HairDye = payload[OffHairDye],
            HideVisual0 = payload[OffHideVisual],
            HideVisual1 = payload[OffHideVisual + 1],
            HideMisc = payload[OffHideMisc],
            HairColor = HexAt(payload, OffColors + 0),
            SkinColor = HexAt(payload, OffColors + 3),
            EyeColor = HexAt(payload, OffColors + 6),
            ShirtColor = HexAt(payload, OffColors + 9),
            UnderShirtColor = HexAt(payload, OffColors + 12),
            PantsColor = HexAt(payload, OffColors + 15),
            ShoeColor = HexAt(payload, OffColors + 18),
            Difficulty = payload[OffDifficulty],
            Tail = Convert.ToHexString(payload.Slice(OffTail, payload.Length - OffTail)).ToLowerInvariant(),
        };

        return new Report
        {
            Length = r.Length,
            LengthOk = r.LengthOk,
            HexSpaced = r.HexSpaced,
            Ascii = r.Ascii,
            PlayerId = r.PlayerId,
            SkinVariant = r.SkinVariant,
            Unknown2 = r.Unknown2,
            FloatField = r.FloatField,
            Hair = r.Hair,
            NameOffset = r.NameOffset,
            NamePrefixBytes = r.NamePrefixBytes,
            Name = r.Name,
            HairDye = r.HairDye,
            HideVisual0 = r.HideVisual0,
            HideVisual1 = r.HideVisual1,
            HideMisc = r.HideMisc,
            HairColor = r.HairColor,
            SkinColor = r.SkinColor,
            EyeColor = r.EyeColor,
            ShirtColor = r.ShirtColor,
            UnderShirtColor = r.UnderShirtColor,
            PantsColor = r.PantsColor,
            ShoeColor = r.ShoeColor,
            Difficulty = r.Difficulty,
            Tail = r.Tail,
            Fields =
                $"id={r.PlayerId} skinVariant={r.SkinVariant} unk2=0x{r.Unknown2:x2} "
                + $"f32@3={r.FloatField:0.####} hair={r.Hair} name@{OffName}='{r.Name}' "
                + $"hairDye={r.HairDye} hideVis={r.HideVisual0},{r.HideVisual1} hideMisc={r.HideMisc} "
                + $"colors@16={r.HairColor}/{r.SkinColor}/{r.EyeColor}/{r.ShirtColor}/"
                + $"{r.UnderShirtColor}/{r.PantsColor}/{r.ShoeColor} "
                + $"difficulty@37={r.Difficulty} tail@38={r.Tail}",
        };
    }

    /// <summary>
    /// 原样 Clone 回传：除第 0 字节 whoAmI 换成服务器分配的槽位外，其它 39 字节一个不动。
    /// 客户端认的就是它自己发来的这份数据。
    /// </summary>
    public static byte[] BuildEcho(ReadOnlySpan<byte> payload, byte whoAmI)
    {
        byte[] echo = payload.ToArray();
        if (echo.Length > 0)
            echo[0] = whoAmI;

        return echo;
    }

    private static string HexAt(ReadOnlySpan<byte> payload, int offset) =>
        offset + 3 <= payload.Length
            ? $"#{payload[offset]:x2}{payload[offset + 1]:x2}{payload[offset + 2]:x2}"
            : "-";

    public static string ToSpacedHex(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return "";

        var text = new StringBuilder(payload.Length * 3);
        for (int i = 0; i < payload.Length; i++)
        {
            if (i > 0)
                text.Append(' ');
            text.Append(payload[i].ToString("x2"));
        }

        return text.ToString();
    }

    public static string AsciiView(ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(payload.Length);
        foreach (byte b in payload)
            text.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');

        return text.ToString();
    }
}