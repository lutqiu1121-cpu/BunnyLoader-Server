using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// 消息 4（SyncPlayer）的**只读**诊断解析。本文件里没有任何改写：不写回 payload、不改长度。
///
/// 为什么只读（技术主管批注，优先级高于档案）：
///   1. id:4 是角色外观（playerId/skinVariant/hair/name/颜色/标志位）包，不是坐标包；
///      坐标走 id:12 PlayerSpawn 与 id:13 PlayerControls。
///   2. 人类日志里第 16-19 字节的 ff ff ff ff 后面紧跟 bb a9 69 5a（另一个 4 字节值），
///      不符合「相邻 X/Y 两个 f32」的布局，更像连续存储的颜色/标志位区域。
///   3. 在把 40 字节逐字段对照标准 1.4.5.8 SyncPlayer 之前，改写任何字节都可能把颜色写坏。
///
/// 这个类的唯一职责：把 40 字节一次说清楚——完整十六进制、标准布局走位（走不通就如实报断点）、
/// 每个偏移的 u32/f32 候选、以及「正好等于长度的可打印串」这类名字候选。
/// </summary>
public static class SyncPlayer4
{
    /// <summary>候选列表最多打印多少项。</summary>
    public const int MaxReported = 48;

    /// <summary>一条 payload 的诊断结果。</summary>
    public sealed class Report
    {
        public int Length { get; init; }

        /// <summary>完整十六进制，空格分隔，不截断。</summary>
        public string HexSpaced { get; init; } = "-";

        /// <summary>可打印 ASCII 视图，不可打印字节显示成 '.'。</summary>
        public string Ascii { get; init; } = "-";

        // ---- 标准 1.4.5.8 SyncPlayer 走位结果 ----
        public int PlayerId { get; init; } = -1;
        public int SkinVariant { get; init; } = -1;
        public int Hair { get; init; } = -1;
        public int NameOffset { get; init; } = -1;
        public int NamePrefixBytes { get; init; }
        public long NameLengthClaimed { get; init; } = -1;
        public string Name { get; init; } = "";
        public bool StandardLayoutOk { get; init; }

        /// <summary>标准布局在第几个字节断掉、为什么。</summary>
        public string Derail { get; init; } = "-";

        /// <summary>标准布局（颜色 3 字节 RGB）最小字节数。</summary>
        public int MinStd { get; init; } = -1;

        /// <summary>同样是颜色但按 4 字节 RGBA 存的最小字节数。</summary>
        public int MinRgba { get; init; } = -1;

        // ---- 候选读法 ----
        /// <summary>「某个字节的值正好等于其后面那段可打印文本长度」的名字候选。</summary>
        public string NameCandidates { get; init; } = "-";

        /// <summary>对齐到 4 的 u32 读法。</summary>
        public string U32Candidates { get; init; } = "-";

        /// <summary>非平凡 f32 读法（NaN 单独标出）。</summary>
        public string FloatCandidates { get; init; } = "-";

        /// <summary>可打印字节串（长度 ≥ 2）。</summary>
        public string PrintableRuns { get; init; } = "-";

        /// <summary>逐字段清单，未知的一律标注「候选」。</summary>
        public string Fields { get; init; } = "-";

        public int NanPairOffset { get; init; } = -1;
    }

    /// <summary>完整诊断。只读，绝不修改 payload。</summary>
    public static Report Analyze(ReadOnlySpan<byte> payload)
    {
        int length = payload.Length;

        int playerId = length > 0 ? payload[0] : -1;
        int skinVariant = length > 1 ? payload[1] : -1;
        int hair = length > 2 ? payload[2] : -1;

        var fields = new List<string>();
        var nameCandidates = new List<string>();
        var printableRuns = new List<string>();

        fields.Add($"0:playerId=0x{playerId:X2}");
        if (skinVariant >= 0) fields.Add($"1:skinVariant=0x{skinVariant:X2}");
        if (hair >= 0) fields.Add($"2:hair=0x{hair:X2}");

        // ---- 名字字段：标准写法是 7bit 变长字节长度 + UTF-8 ----
        int nameOffset = 3;
        int namePrefixBytes = 0;
        long nameClaimed = -1;
        string name = "";
        bool nameOk = false;
        string derail = "-";

        if (length > nameOffset)
        {
            if (TryRead7BitLength(payload, nameOffset, out int claimed, out int prefixBytes))
            {
                namePrefixBytes = prefixBytes;
                nameClaimed = claimed;

                bool fits = claimed > 0 && nameOffset + prefixBytes + claimed <= length;
                if (!fits)
                {
                    derail = $"name 长度前缀在偏移 {nameOffset} 解出 {claimed} 字节，"
                             + $"但剩余只有 {length - nameOffset - prefixBytes} 字节：标准布局在本 payload 上不成立";
                }
                else
                {
                    ReadOnlySpan<byte> text = payload.Slice(nameOffset + prefixBytes, claimed);
                    if (IsPrintableAscii(text))
                    {
                        name = Encoding.ASCII.GetString(text);
                        nameOk = true;
                        fields.Add($"{nameOffset}..{nameOffset + prefixBytes + claimed - 1}:name=\"{name}\"（{prefixBytes} 字节长度前缀）");
                    }
                    else
                    {
                        derail = $"name 长度前缀解出 {claimed} 字节，但内容含不可打印字节：不是字符串";
                    }
                }
            }
            else
            {
                derail = $"name 长度前缀在偏移 {nameOffset} 无法解出（7bit 变长越界）";
            }
        }
        else
        {
            derail = "payload 短于 4 字节";
        }

        if (!nameOk && nameOffset < length)
            fields.Add($"{nameOffset}..:name=（候选，待人类对照客户端捏脸数据确认；标准解析在此断点：{derail}）");

        // ---- 名字之后的字段：给出「标准布局下它们本该落在哪」的偏移，全部标候选 ----
        // 本轮禁止改写任何字节，所以这里只报位置；人类拿到捏脸数据后能直接对号入座。
        int expected = nameOffset + namePrefixBytes + (nameOk ? name.Length : 0);

        fields.Add($"{expected}:hairDye（候选，待人类对照客户端捏脸数据确认）");
        expected += 1;
        fields.Add($"{expected}..{expected + 20}:7×发色 hairColor[0..6] RGB（候选，待确认）");
        expected += 21;
        fields.Add($"{expected}..{expected + 17}:肤/瞳/上衣/内衣/裤/鞋 6×RGB（候选，待确认）");
        expected += 18;
        fields.Add($"{expected}:difficulty（候选，待确认）");
        expected += 1;
        fields.Add($"{expected}:hideVisuals（候选，待确认）");
        expected += 1;
        fields.Add($"{expected}:hideVisuals2（候选，待确认）");
        expected += 1;
        fields.Add($"{expected}:hideMisc（候选，待确认）");
        expected += 1;

        if (expected > length)
            fields.Add($"标准布局走到 {expected} 字节，实测只有 {length} 字节：差 {expected - length} 字节"
                       + "（这是判断「该客户端是否标准全量 SyncPlayer」的直接证据）");

        // ---- 名字候选：某个字节的值正好等于其后可打印段长度（实测样本里 offset 8 = 0x03 + "Cup"）----
        for (int i = 0; i + 1 < length; i++)
        {
            int runLen = payload[i];
            if (runLen < 2 || runLen > 32)
                continue;
            if (i + 1 + runLen > length)
                continue;

            ReadOnlySpan<byte> text = payload.Slice(i + 1, runLen);
            if (!IsPrintableAscii(text))
                continue;

            if (nameCandidates.Count < MaxReported)
                nameCandidates.Add($"{i}:len={runLen}:\"{Encoding.ASCII.GetString(text)}\"");
        }

        // ---- 可打印段（≥ 2 字节）----
        int runStart = -1;
        for (int i = 0; i <= length; i++)
        {
            bool printable = i < length && payload[i] >= 0x20 && payload[i] <= 0x7E;

            if (printable && runStart < 0)
            {
                runStart = i;
            }
            else if (!printable && runStart >= 0)
            {
                int runLen = i - runStart;
                if (runLen >= 2 && printableRuns.Count < MaxReported)
                    printableRuns.Add($"{runStart}:len={runLen}:\"{Encoding.ASCII.GetString(payload.Slice(runStart, runLen))}\"");
                runStart = -1;
            }
        }

        // ---- 对齐 4 字节的 u32 ----
        var u32 = new List<string>();
        for (int i = 0; i + 4 <= length; i += 4)
        {
            if (u32.Count >= MaxReported)
                break;
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i, 4));
            u32.Add($"{i}:0x{value:X8}");
        }

        // ---- f32：NaN 与「非平凡」值 ----
        var nanOffsets = new List<int>();
        var floats = new List<string>();
        for (int i = 0; i + 4 <= length; i++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(i, 4));

            if (float.IsNaN(value))
            {
                nanOffsets.Add(i);
                if (floats.Count < MaxReported)
                    floats.Add($"{i}:NaN");
                continue;
            }

            float abs = MathF.Abs(value);
            if (abs >= 0.001f && abs <= 10_000_000f && floats.Count < MaxReported)
                floats.Add($"{i}:{value.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        // ---- 标准布局所需最小长度：3 + (1+名字) + 1 + 颜色 + 2 ----
        int nameBytes = nameOk ? namePrefixBytes + name.Length : 4;   // 走不通时按最短名字估
        int minStd = 3 + 1 + nameBytes + 1 + 7 * 3 + 6 * 3 + 2;      // 7 发色 + 6 个身体色（RGB）
        int minRgba = 3 + 1 + nameBytes + 1 + 7 * 4 + 6 * 4 + 2;     // 同上，但每色 RGBA 4 字节

        // ---- 剩下的字节：只能列候选，不能断言语义 ----
        fields.Add("其余 4 字节值：见 u32 候选，颜色/标志位都可能（候选，待人类对照客户端捏脸数据确认）");

        return new Report
        {
            Length = length,
            HexSpaced = ToSpacedHex(payload),
            Ascii = AsciiView(payload),
            PlayerId = playerId,
            SkinVariant = skinVariant,
            Hair = hair,
            NameOffset = nameOffset,
            NamePrefixBytes = namePrefixBytes,
            NameLengthClaimed = nameClaimed,
            Name = name,
            StandardLayoutOk = nameOk,
            Derail = derail,
            MinStd = minStd,
            MinRgba = minRgba,
            NameCandidates = nameCandidates.Count == 0 ? "-" : string.Join(" ", nameCandidates),
            U32Candidates = u32.Count == 0 ? "-" : string.Join(" ", u32),
            FloatCandidates = floats.Count == 0 ? "-" : string.Join(" ", floats),
            PrintableRuns = printableRuns.Count == 0 ? "-" : string.Join(" ", printableRuns),
            Fields = string.Join(" | ", fields),
            NanPairOffset = FirstFullByteRun(payload, 0xFF, 8),
        };
    }

    /// <summary>完整十六进制，空格分隔（不截断）。</summary>
    public static string ToSpacedHex(ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(payload.Length * 3);
        for (int i = 0; i < payload.Length; i++)
        {
            if (i > 0)
                text.Append(' ');
            text.Append(payload[i].ToString("x2"));
        }

        return text.ToString();
    }

    /// <summary>第一个 value 连续 count 次的偏移；没有则 -1。</summary>
    public static int FirstFullByteRun(ReadOnlySpan<byte> payload, byte value, int count)
    {
        for (int i = 0; i + count <= payload.Length; i++)
        {
            bool hit = true;
            for (int k = 0; k < count; k++)
            {
                if (payload[i + k] != value)
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
                return i;
        }

        return -1;
    }

    private static string AsciiView(ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(payload.Length);
        foreach (byte b in payload)
            text.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
        return text.ToString();
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> text)
    {
        foreach (byte b in text)
        {
            if (b < 0x20 || b > 0x7E)
                return false;
        }

        return true;
    }

    private static bool TryRead7BitLength(ReadOnlySpan<byte> payload, int start, out int value, out int prefixBytes)
    {
        value = 0;
        prefixBytes = 0;
        long shift = 0;

        for (int i = start; i < payload.Length && prefixBytes < 5; i++)
        {
            byte b = payload[i];
            value |= (int)((b & 0x7F) << (int)shift);
            prefixBytes++;
            shift += 7;

            if ((b & 0x80) == 0)
                return true;
        }

        value = 0;
        prefixBytes = 0;
        return false;
    }
}