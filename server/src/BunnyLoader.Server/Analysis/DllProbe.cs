using System.Buffers.Binary;
using System.Reflection;
using System.Text;

namespace BunnyLoader.Server.Analysis;

/// <summary>
/// 离线 PE / CLI 元数据探针（纯 BCL，无 NuGet、无网络、无外部工具）。
///
/// 存在的理由：面板没有 shell 终端，只有一个把输入喂给服务进程 stdin 的命令框，
/// 跑不了 python3 / dotnet --list-sdks / ilspycmd。所以把「读客户端 dll」这件事
/// 编进服务端自身：启动时若实例目录里放了客户端 dll，就自动解析并把结论写成文本。
///
/// 只读标准 PE → Optional Header → Data Directory[14] CLI Header → Metadata Root
/// → #Strings 堆，外加全文件 ASCII / UTF-16LE 指纹扫描。不反编译、不回贴源码、
/// 不修改被分析文件。输出是纯文本报告，二进制不进 git。
///
/// 能力边界（如实写清）：#Strings 只给类型名 / 方法名，能回答「客户端认不认这些 id、
/// 有没有对应处理入口」；id:21/23/24/44 的字段顺序是 IL 级信息，需第二轮定向 dump。
/// 若 CLI Header 为空 → 说明是 native / AOT，托管层没有协议信息，dll 路线到此终止。
/// </summary>
public static class DllProbe
{
    private static readonly string[] InputCandidates =
    {
        "analysis/client.dll",
        "analysis/BunnyLoader.dll",
        "BunnyLoader.dll",
        "client.dll",
    };

    private static readonly string[] Keywords =
    {
        "NetMessage", "GetData", "SendData", "MessageID", "MessageId",
        "ItemDrop", "NpcUpdate", "HitNpc", "Projectile", "SyncItem", "SyncNPC",
        "BunnyLoader", "bunnyloader", "Terraria", "B17F", "ModNet",
        "ServerSyncHooks", "ClientUuid", "syncMods", "textures",
        "ReadByte", "ReadInt16", "ReadInt32", "ReadSingle", "ReadString", "BinaryReader",
    };

    private static readonly string[] Fingerprints =
    {
        "bunnyloader-sync:", "bunnyloader-cs:", "Terraria326", "Terraria", "0xB17F",
    };

    public sealed class Result
    {
        public bool Ok;
        public string Reason = "";
        public string PeMagic = "";
        public int CliRva;
        public int MetaRva;
        public int StringsTotal;
        public List<string> Streams = new();
        public List<string> Hits = new();
        public List<string> Prints = new();
        public long FileSize;
        public string Path = "";
    }

    /// <summary>启动时调用；找不到输入文件就静默跳过，绝不影响服务端。</summary>
    public static void TryRun(ServerConfig config, JsonLogger log)
    {
        if (!config.Analysis.Enabled)
        {
            log.Info("dll.probe.disabled", ("note", "analysis.enabled=false"));
            return;
        }

        string? input = null;
        foreach (string candidate in InputCandidates)
            if (File.Exists(candidate)) { input = candidate; break; }

        if (input is null)
        {
            log.Info("dll.probe.skip", ("tried", string.Join(",", InputCandidates)),
                ("note", "未发现客户端 dll；把文件放进 analysis/ 或根目录后重启即可，不影响服务"));
            return;
        }

        try
        {
            log.Info("dll.probe.start", ("path", Path.GetFullPath(input)),
                ("size", new FileInfo(input).Length));
            Result r = Analyze(input);
            string report = BuildReport(r);
            string output = string.IsNullOrWhiteSpace(config.Analysis.Output)
                ? "analysis/proto_notes.txt" : config.Analysis.Output;
            string? dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(output, report, new UTF8Encoding(false));

            log.Info("dll.probe.done", ("path", Path.GetFullPath(input)),
                ("managed", r.Ok ? "yes" : "no"), ("reason", r.Reason),
                ("pe", r.PeMagic), ("strings", r.StringsTotal),
                ("hits", r.Hits.Count), ("report", Path.GetFullPath(output)),
                ("preview", string.Join(" | ", r.Hits.Take(12))));
        }
        catch (Exception e)
        {
            log.Error("dll.probe.error", ("type", e.GetType().Name),
                ("message", e.Message), ("action", "探针失败，服务端继续启动"));
        }
    }

    /// <summary>--selftest 用：扫自己这个托管 dll，验证解析链路本身可用。</summary>
    public static bool SelfTest(JsonLogger log)
    {
        try
        {
            string self = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(self) || !File.Exists(self))
                self = Path.Combine(AppContext.BaseDirectory, "BunnyLoader.Server.dll");
            if (!File.Exists(self))
            {
                log.Error("dll.probe.selftest.fail", ("reason", "找不到自身 dll"));
                return false;
            }

            Result r = Analyze(self);
            bool ok = r.Ok && r.StringsTotal > 50
                && r.Hits.Any(h => h.Contains("DllProbe", StringComparison.OrdinalIgnoreCase))
                && r.Hits.Any(h => h.Contains("Session", StringComparison.OrdinalIgnoreCase));
            log.Info(ok ? "dll.probe.selftest.pass" : "dll.probe.selftest.fail",
                ("managed", r.Ok), ("strings", r.StringsTotal), ("hits", r.Hits.Count),
                ("pe", r.PeMagic));
            return ok;
        }
        catch (Exception e)
        {
            log.Error("dll.probe.selftest.fail", ("message", e.Message));
            return false;
        }
    }

    public static Result Analyze(string path)
    {
        var r = new Result { Path = path };
        byte[] b;
        try { b = File.ReadAllBytes(path); }
        catch (Exception e) { r.Reason = "读取失败: " + e.Message; return r; }
        r.FileSize = b.Length;

        if (b.Length < 0x40 || b[0] != (byte)'M' || b[1] != (byte)'Z')
        { r.Reason = "不是 PE（无 MZ）"; return r; }

        int lfanew = I32(b, 0x3C);
        if (lfanew <= 0 || lfanew + 24 > b.Length || b[lfanew] != (byte)'P'
            || b[lfanew + 1] != (byte)'E' || b[lfanew + 2] != 0 || b[lfanew + 3] != 0)
        { r.Reason = "无 PE 签名"; return r; }

        int coff = lfanew + 4;
        int nSections = U16(b, coff + 2);
        int sizeOpt = U16(b, coff + 16);
        int opt = coff + 20;
        if (opt + sizeOpt > b.Length) { r.Reason = "Optional Header 越界"; return r; }

        int magic = U16(b, opt);
        bool pe32Plus = magic == 0x20B;
        r.PeMagic = pe32Plus ? "PE32+" : (magic == 0x10B ? "PE32" : $"0x{magic:x}");
        int ddCountOff = opt + (pe32Plus ? 108 : 92);
        int ddOff = opt + (pe32Plus ? 112 : 96);
        if (ddCountOff + 4 > b.Length) { r.Reason = "Data Directory 越界"; return r; }
        int ddCount = I32(b, ddCountOff);
        if (ddCount < 15)
        {
            r.Ok = false;
            r.Reason = "Data Directory 无第 14 项（CLI Header）→ native / AOT，托管层无协议信息";
            return r;
        }

        int cliRva = I32(b, ddOff + 14 * 8);
        r.CliRva = cliRva;
        if (cliRva == 0)
        {
            r.Reason = "CLI Header RVA=0 → native / AOT，托管层无协议信息";
            return r;
        }

        int secOff = opt + sizeOpt;
        var sections = new List<(int Va, int VSz, int RawSz, int RawPtr)>();
        for (int i = 0; i < nSections; i++)
        {
            int p = secOff + i * 40;
            if (p + 40 > b.Length) break;
            sections.Add((I32(b, p + 12), I32(b, p + 8), I32(b, p + 16), I32(b, p + 20)));
        }

        if (!TryRvaToFile(cliRva, sections, out int cliOff) || cliOff + 16 > b.Length)
        { r.Reason = "CLI Header RVA 无法映射"; return r; }

        int metaRva = I32(b, cliOff + 8);
        r.MetaRva = metaRva;
        if (!TryRvaToFile(metaRva, sections, out int metaOff) || metaOff + 20 > b.Length)
        { r.Reason = "Metadata RVA 无法映射"; return r; }

        if (U32(b, metaOff) != 0x424A5342u) { r.Reason = "无 BSJB 元数据根"; return r; }
        int versionLen = I32(b, metaOff + 12);
        int p2 = metaOff + 16 + versionLen;
        if (versionLen < 0 || versionLen > 4096 || p2 + 4 > b.Length)
        { r.Reason = "元数据版本串异常"; return r; }
        int nStreams = U16(b, p2 + 2);
        p2 += 4;

        int stringsOff = -1, stringsSize = 0;
        for (int i = 0; i < nStreams && i < 64; i++)
        {
            if (p2 + 8 > b.Length) break;
            int sOff = I32(b, p2);
            int sSize = I32(b, p2 + 4);
            int nameStart = p2 + 8;
            int nameEnd = nameStart;
            while (nameEnd < b.Length && b[nameEnd] != 0 && nameEnd - nameStart < 64) nameEnd++;
            string name = Encoding.ASCII.GetString(b, nameStart, nameEnd - nameStart);
            r.Streams.Add($"{name}({sSize})");
            if (name == "#Strings") { stringsOff = metaOff + sOff; stringsSize = sSize; }
            int aligned = (nameEnd - nameStart + 1 + 3) & ~3;
            p2 = nameStart + aligned;
        }

        r.Ok = true;
        r.Reason = "managed PE，元数据可读";

        if (stringsOff >= 0 && stringsSize > 0
            && stringsOff + stringsSize <= b.Length && stringsSize < b.Length)
        {
            var uniq = new HashSet<string>(StringComparer.Ordinal);
            int start = stringsOff;
            int end = stringsOff + stringsSize;
            for (int i = start; i < end; i++)
            {
                if (b[i] < 0x20 || b[i] > 0x7E) continue;
                int j = i;
                while (j < end && b[j] >= 0x20 && b[j] <= 0x7E) j++;
                if (j - i >= 4) uniq.Add(Encoding.ASCII.GetString(b, i, j - i));
                i = j;
            }
            r.StringsTotal = uniq.Count;
            r.Hits = uniq.Where(s => Keywords.Any(k =>
                    s.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        foreach (string fp in Fingerprints)
        {
            bool ascii = ContainsBytes(b, Encoding.ASCII.GetBytes(fp));
            bool utf16 = ContainsBytes(b, Encoding.Unicode.GetBytes(fp));
            r.Prints.Add($"{fp}: {(ascii ? "ascii" : "")}{(utf16 ? (ascii ? "+utf16" : "utf16") : "")}{(ascii || utf16 ? "" : "not-found")}");
        }

        return r;
    }

    private static string BuildReport(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DllProbe report (protocol observations only, no source reproduced)");
        sb.AppendLine($"file={r.Path}");
        sb.AppendLine($"size={r.FileSize}");
        sb.AppendLine($"managed={(r.Ok ? "yes" : "no")}");
        sb.AppendLine($"reason={r.Reason}");
        sb.AppendLine($"pe={r.PeMagic} cliRva=0x{r.CliRva:x8} metaRva=0x{r.MetaRva:x8}");
        sb.AppendLine($"streams={string.Join(",", r.Streams)}");
        sb.AppendLine($"strings.total={r.StringsTotal}");
        sb.AppendLine($"hits.count={r.Hits.Count}");
        sb.AppendLine();
        sb.AppendLine("--- fingerprints ---");
        foreach (string p in r.Prints) sb.AppendLine(p);
        sb.AppendLine();
        sb.AppendLine("--- hits (#Strings, names only) ---");
        foreach (string h in r.Hits) sb.AppendLine(h);
        sb.AppendLine();
        sb.AppendLine("--- verdict ---");
        if (!r.Ok)
            sb.AppendLine("CLI header empty / unreadable → native or AOT; managed layer holds no protocol info. Stop the dll route here.");
        else if (r.Hits.Count == 0)
            sb.AppendLine("Managed assembly, but no protocol-ish names in #Strings → likely obfuscated, or logic lives in native code. Need IL-level dump or real-device capture.");
        else
            sb.AppendLine("Protocol-ish names found. This answers WHICH ids/types exist; field order for id:21/23/24/44 still needs a targeted IL dump (round 2) or real bidirectional hex.");
        return sb.ToString();
    }

    private static bool TryRvaToFile(int rva, List<(int Va, int VSz, int RawSz, int RawPtr)> secs, out int off)
    {
        foreach (var s in secs)
        {
            int span = Math.Max(s.VSz, s.RawSz);
            if (rva >= s.Va && rva < s.Va + span)
            {
                off = s.RawPtr + (rva - s.Va);
                return true;
            }
        }
        off = 0;
        return false;
    }

    private static bool ContainsBytes(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > hay.Length) return false;
        int last = hay.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) break;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private static int U16(byte[] b, int p)
        => (p + 2 <= b.Length) ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2)) : 0;

    private static int I32(byte[] b, int p)
        => (p + 4 <= b.Length) ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p, 4)) : 0;

    private static uint U32(byte[] b, int p)
        => (p + 4 <= b.Length) ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)) : 0;
}
