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

        /// <summary>
        /// 布局假设枚举（纯诊断）：穷举「名字起始偏移 × 名字编码 × hairDye × 颜色个数 × 颜色宽度 × 尾部字节数」，
        /// 只报正好把 payload 用完的组合。人类拿捏脸 RGB 一对就能确定哪个假设为真。
        /// </summary>
        public string Hypotheses { get; init; } = "-";

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
            Hypotheses = BuildHypotheses(payload),
            NanPairOffset = FirstFullByteRun(payload, 0xFF, 8),
        };
    }

    /// <summary>名字的三种候选编码。本轮只做识别，不从中间判断谁对。</summary>
    private enum NameEnc
    {
        SevenBit = 0,
        U8Length = 1,
        NulTerminated = 2,
    }

    /// <summary>
    /// 布局假设枚举（纯诊断，不改 payload）。
    /// 该客户端是二开版，SyncPlayer 未必是标准全量布局：上一版按标准 7bit 前缀解析，
    /// 在偏移 3 解出 1254992 字节，直接证明「名字不在偏移 3」。所以这里改成穷举——
    /// 名字起始偏移(3..12) × 编码(7bit/u8/nul) × 有无 hairDye × 颜色个数(6..13) × 颜色宽度(3|4) × 尾部字节(0..4)，
    /// 只报**正好把 payload 用完**的组合。人类把客户端里的捏脸 RGB 贴过来，对得上的那条就是真布局。
    /// </summary>
    private static string BuildHypotheses(ReadOnlySpan<byte> payload)
    {
        int length = payload.Length;
        if (length < 8)
            return "payload 太短，无法枚举";

        var exact = new List<(int score, int nameLen, int start, string tag, string map)>();
        NameEnc[] encodings = { NameEnc.SevenBit, NameEnc.U8Length, NameEnc.NulTerminated };
        int restSeen = -1;
        int restAt = -1;

        for (int start = 3; start <= Math.Min(12, length - 1); start++)
        {
            // 名字不可能从一段可打印串的中间起头：start>3 时若前一个字节是可见 ASCII，这个起点必是假的。
            // （实测的 "Cup" 在偏移 8，前一字节 0xc4 不可打印，符合；标准布局的 start=3 不受此限。）
            if (start > 3 && payload[start - 1] >= 0x20 && payload[start - 1] <= 0x7E)
                continue;

            foreach (NameEnc enc in encodings)
            {
                if (!TryReadName(payload, start, enc, out string name, out int after))
                    continue;
                if (name.Length < 2)
                    continue;

                int rest = length - after;
                if (restSeen < 0)
                {
                    restSeen = rest;
                    restAt = after;
                }

                for (int dye = 0; dye <= 1; dye++)
                {
                    for (int colors = 6; colors <= 13; colors++)
                    {
                        for (int width = 3; width <= 4; width++)
                        {
                            for (int tail = 0; tail <= 4; tail++)
                            {
                                if (dye + colors * width + tail != rest)
                                    continue;

                                // 越像标准全量布局分越高：有 hairDye、13 色、RGB、尾部 2-4 字节
                                int score = (dye == 1 ? 4 : 0) + (colors == 13 ? 3 : 0)
                                            + (width == 3 ? 2 : 0) + (tail >= 2 ? 1 : 0);

                                exact.Add((
                                    score,
                                    name.Length,
                                    start,
                                    NameTag(enc, start, dye, colors, width, tail),
                                    FieldMap(payload, start, enc, name, after, dye, colors, width, tail)));
                            }
                        }
                    }
                }
            }
        }

        if (exact.Count == 0)
            return $"rest={restSeen}(@{restAt}) 无任何组合能正好用完：说明字段顺序/编码与假设集都不符（候选，待人类对照捏脸数据确认）";

        // 排序必须是确定性的：同分时名字更长的优先，再按起点靠前，
        // 否则同一条 payload 两次跑出来的 map 可能不一样，人类对照时会被误导。
        exact.Sort((a, b) =>
        {
            int cmp = b.score.CompareTo(a.score);
            if (cmp != 0) return cmp;
            cmp = b.nameLen.CompareTo(a.nameLen);
            if (cmp != 0) return cmp;
            cmp = a.start.CompareTo(b.start);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.tag, b.tag);
        });

        var tags = new List<string>();
        for (int i = 0; i < exact.Count && i < 4; i++)
            tags.Add(exact[i].tag);

        // 标准全量布局（hairDye + 13 色 RGB + difficulty/hideVisuals 2 字节）差多少
        int standard = 1 + 13 * 3 + 2;

        // 前两条假设各给一份完整字段图：人类只需要和捏脸颜色对一次。
        var maps = new List<string>();
        for (int i = 0; i < exact.Count && i < 2; i++)
            maps.Add($"map({exact[i].tag})= {exact[i].map}");

        return $"rest={restSeen}(@{restAt}) exact={exact.Count} "
               + $"top=[{string.Join(" ", tags)}] "
               + string.Join(" | ", maps)
               + $" | standard(1+13*3+2={standard}) 差 {restSeen - standard}";
    }

    /// <summary>按给定假设把 payload 走一遍，输出「字段@偏移=值」，供人类逐项对照捏脸数据。</summary>
    private static string FieldMap(ReadOnlySpan<byte> payload, int start, NameEnc enc, string name, int after,
        int dye, int colors, int width, int tail)
    {
        var parts = new List<string> { $"name@{start}({EncTag(enc)})=\"{name}\"" };

        int off = after;

        if (dye == 1)
        {
            parts.Add($"hairDye@{off}=0x{payload[off]:X2}");
            off += 1;
        }

        for (int i = 0; i < colors; i++)
        {
            parts.Add($"c{i}@{off}={ColorHex(payload.Slice(off, width))}");
            off += width;
        }

        if (tail > 0)
            parts.Add($"tail@{off}..{off + tail - 1}（{tail} 字节：difficulty/hideVisuals/hideVisuals2/hideMisc，候选）");

        return string.Join(" ", parts);
    }

    private static string NameTag(NameEnc enc, int start, int dye, int colors, int width, int tail)
        => $"name@{start}/{EncTag(enc)}/{(dye == 1 ? "dye" : "nodye")}/{colors}c{width}B/{tail}t";

    private static string EncTag(NameEnc enc)
    {
        switch (enc)
        {
            case NameEnc.SevenBit: return "7bit";
            case NameEnc.U8Length: return "u8";
            default: return "nul";
        }
    }

    private static string ColorHex(ReadOnlySpan<byte> color)
    {
        var text = new StringBuilder(color.Length + 1);
        text.Append('#');
        for (int i = 0; i < color.Length; i++)
            text.Append(color[i].ToString("x2"));
        return text.ToString();
    }

    /// <summary>按三种候选编码读名字；成功时 after = 名字之后第一个字节的偏移。</summary>
    private static bool TryReadName(ReadOnlySpan<byte> payload, int start, NameEnc enc,
        out string text, out int after)
    {
        text = "";
        after = start;

        if (start < 0 || start >= payload.Length)
            return false;

        switch (enc)
        {
            case NameEnc.SevenBit:
            {
                if (!TryRead7BitLength(payload, start, out int claimed, out int prefixBytes))
                    return false;
                if (claimed <= 0 || start + prefixBytes + claimed > payload.Length)
                    return false;

                ReadOnlySpan<byte> body = payload.Slice(start + prefixBytes, claimed);
                if (!IsPrintableAscii(body))
                    return false;

                text = Encoding.ASCII.GetString(body);
                after = start + prefixBytes + claimed;
                return true;
            }

            case NameEnc.U8Length:
            {
                int claimed = payload[start];
                if (claimed <= 0 || start + 1 + claimed > payload.Length)
                    return false;

                ReadOnlySpan<byte> body = payload.Slice(start + 1, claimed);
                if (!IsPrintableAscii(body))
                    return false;

                text = Encoding.ASCII.GetString(body);
                after = start + 1 + claimed;
                return true;
            }

            default:
            {
                int end = start;
                while (end < payload.Length && payload[end] != 0)
                    end++;

                if (end >= payload.Length || end == start)
                    return false;

                ReadOnlySpan<byte> body = payload.Slice(start, end - start);
                if (!IsPrintableAscii(body))
                    return false;

                text = Encoding.ASCII.GetString(body);
                after = end + 1;
                return true;
            }
        }
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