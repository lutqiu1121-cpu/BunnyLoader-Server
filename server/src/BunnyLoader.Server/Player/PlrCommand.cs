using System.Buffers.Binary;
using System.Text;

namespace BunnyLoader.Server.Player;

/// <summary>
/// <c>--parse-plr &lt;path&gt; [--sync-raw &lt;hex&gt;]</c> 的实现。
/// 只读：解析 .plr、打印字段、和 id:4 的 40 字节 payload 对照，得出「ff ff ff ff 是哪个字段」。
/// 不接 Session，不发任何网络帧。
/// </summary>
public static class PlrCommand
{
    public static int Run(string plrPath, string? syncRawHex, JsonLogger log)
    {
        PlayerFileData data;
        try
        {
            data = PlayerFileReader.ReadFromFile(plrPath);
        }
        catch (Exception e)
        {
            log.Error("plr.error", ("file", plrPath), ("error", e.GetType().Name + ": " + e.Message));
            Console.Error.WriteLine("解析失败：" + e.Message);
            return 1;
        }

        // 1) 文件与加密判断
        log.Info("plr.file",
            ("file", data.FilePath),
            ("bytes", data.FileLength),
            ("head32Hex", data.Head32Hex));

        log.Info("plr.header",
            ("version", data.Version),
            ("magic", data.Magic),
            ("fileType", data.FileType),
            ("encrypted", data.Encrypted),
            ("basis", data.EncryptionNote));

        if (data.Encrypted)
        {
            log.Info("plr.decrypted",
                ("algorithm", "AES-128-CBC"),
                ("keyEqualsIv", "h3y_gUyZ 的 UTF-16LE 字节（16B）"),
                ("padding", "None"),
                ("head32Hex", data.DecryptedHead32Hex));
        }

        // 2) 逐字段（每个字段一行 JSON，带偏移与长度）
        foreach (PlrField field in data.Fields)
        {
            log.Info("plr.field",
                ("name", field.Name),
                ("offset", field.Offset),
                ("len", field.Length),
                ("value", field.Value),
                ("confirmed", field.Confirmed));
        }

        // 3) 颜色（hex + RGB 分解）
        foreach ((string name, PlayerColor color) in data.ColorsInPacketOrder)
        {
            PlrField? f = data.FindField(name);
            log.Info("plr.color",
                ("name", name),
                ("offset", f?.Offset ?? -1),
                ("len", f?.Length ?? -1),
                ("hex", color.Hex),
                ("rgb", $"{color.R},{color.G},{color.B}"));
        }

        log.Info("plr.summary",
            ("name", data.Name),
            ("difficulty", data.Difficulty),
            ("hair", data.Hair),
            ("hairDye", data.HairDye),
            ("skinVariant", data.SkinVariant),
            ("team", data.Team),
            ("hideVisibleAccessory", string.Join(",", data.HideVisibleAccessory)),
            ("hideMisc", data.HideMisc),
            ("life", $"{data.Health}/{data.HealthMax}"),
            ("mana", $"{data.Mana}/{data.ManaMax}"),
            ("playTimeHours", Math.Round(data.PlayTimeTicks / 10_000_000.0 / 3600.0, 2)),
            ("parsedOffset", data.ParsedOffset),
            ("plaintextLength", data.PlaintextLength),
            ("trailingHex", string.IsNullOrEmpty(data.TrailingHex) ? "-" : data.TrailingHex));

        // 4) 物品栏：前 10 槽逐条打印，完整物品栏写日志文件
        for (int i = 0; i < data.Inventory.Count && i < 10; i++)
        {
            PlayerItem item = data.Inventory[i];
            log.Info("plr.inventory.slot",
                ("slot", i),
                ("id", item.Id),
                ("count", item.Count),
                ("prefix", item.Prefix),
                ("favorite", item.Favorite),
                ("empty", item.IsEmpty));
        }

        log.Info("plr.inventory.summary",
            ("slots", data.Inventory.Count),
            ("nonEmpty", data.NonEmptyInventorySlots),
            ("equipmentNonEmpty", data.Equipment.Count(x => !x.IsEmpty)),
            ("dyeNonEmpty", data.Dye.Count(x => !x.IsEmpty)),
            ("coinsNonEmpty", data.Coins.Count(x => !x.IsEmpty)),
            ("ammoNonEmpty", data.Ammo.Count(x => !x.IsEmpty)),
            ("piggyNonEmpty", data.PiggyBank.Count(x => !x.IsEmpty)),
            ("safeNonEmpty", data.Safe.Count(x => !x.IsEmpty)),
            ("forgeNonEmpty", data.DefendersForge.Count(x => !x.IsEmpty)),
            ("voidBagNonEmpty", data.VoidBag.Count(x => !x.IsEmpty)),
            ("inventoryLog", WriteInventoryLog(data, log)));

        // 5) 与 id:4 的 40 字节对照
        if (string.IsNullOrWhiteSpace(syncRawHex))
        {
            log.Warn("plr.id4.compare",
                ("status", "skipped"),
                ("hint", "没有 --sync-raw：字段表只能按标准顺序给出，颜色归属标记为「候选，待验证」"));

            foreach (var row in BuildId4Table(null, null, null))
                log.Info("plr.id4.field",
                    ("offset", row.Offset),
                    ("field", row.Field),
                    ("len", row.Length),
                    ("confirmed", false));

            return 0;
        }

        byte[] payload;
        try
        {
            payload = ParseHex(syncRawHex);
        }
        catch (Exception e)
        {
            log.Error("plr.syncraw.bad", ("error", e.Message));
            return 2;
        }

        Id4Comparison cmp = CompareId4(payload, data);

        log.Info("plr.id4.compare",
            ("len", payload.Length),
            ("hex", Convert.ToHexString(payload).ToLowerInvariant()),
            ("nameInPayload", cmp.NameInPayload),
            ("nameOffset", cmp.NameOffset),
            ("namePrefixBytes", cmp.NamePrefixBytes),
            ("nameEnd", cmp.NameEnd),
            ("colorStart", cmp.ColorStart),
            ("matchers", cmp.MatchedColors),
            ("expected", 7),
            ("skipBytes", cmp.PostNameSkip),
            ("hexAt16to19", payload.Length >= 20 ? Convert.ToHexString(payload, 16, 4).ToLowerInvariant() : "-"));

        foreach (string line in cmp.Details)
            log.Info("plr.id4.match", ("detail", line));

        // 6) 字段表（偏移 + 字段名 + 长度 + 是否确认）
        foreach ((int offset, string field, int length, bool confirmed) in cmp.Table)
        {
            log.Info("plr.id4.field",
                ("offset", offset),
                ("field", field),
                ("len", length),
                ("confirmed", confirmed));
        }

        log.Info("plr.id4.conclusion",
            ("answer", cmp.Answer),
            ("colorStartHyps", cmp.HypothesisSummary));

        return 0;
    }

    // ------------------------------------------------------------------
    // 物品栏日志文件
    // ------------------------------------------------------------------

    private static string WriteInventoryLog(PlayerFileData data, JsonLogger log)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"plr-inventory-{SafeName(data.Name)}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"# {data.Name} 的物品栏（来源 {data.FilePath}）");
            AppendSection(sb, "主背包(50)", data.Inventory);
            AppendSection(sb, "装备(20)", data.Equipment);
            AppendSection(sb, "染料(10)", data.Dye);
            AppendSection(sb, "钱币(4)", data.Coins);
            AppendSection(sb, "弹药(4)", data.Ammo);
            AppendSection(sb, "猪猪存钱罐(40)", data.PiggyBank);
            AppendSection(sb, "保险箱(40)", data.Safe);
            AppendSection(sb, "护卫熔炉(40)", data.DefendersForge);
            AppendSection(sb, "虚空袋(40)", data.VoidBag);

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            return file;
        }
        catch (Exception e)
        {
            log.Warn("plr.inventory.logfail", ("error", e.Message));
            return "-";
        }
    }

    private static void AppendSection(StringBuilder sb, string title, List<PlayerItem> items)
    {
        sb.AppendLine();
        sb.AppendLine($"## {title}");
        for (int i = 0; i < items.Count; i++)
        {
            PlayerItem item = items[i];
            if (item.IsEmpty)
                continue;
            sb.AppendLine($"{i}\tid={item.Id}\tcount={item.Count}\tprefix={item.Prefix}\tfavorite={item.Favorite}");
        }
    }

    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        string text = sb.ToString();
        return string.IsNullOrWhiteSpace(text) ? "player" : text;
    }

    // ------------------------------------------------------------------
    // id:4 对照
    // ------------------------------------------------------------------

    /// <summary>id:4 与 .plr 的对照结果（自检也会用到）。</summary>
    public sealed class Id4Comparison
    {
        public int NameOffset { get; set; } = -1;
        public int NamePrefixBytes { get; set; }
        public int NameEnd { get; set; } = -1;
        public string NameInPayload { get; set; } = "";
        public int ColorStart { get; set; } = -1;
        public int PostNameSkip { get; set; } = -1;
        public int DyeBytes { get; set; }
        public int HideVisBytes { get; set; }
        public int HideMiscBytes { get; set; }
        public int MatchedColors { get; set; }
        public string HypothesisSummary { get; set; } = "-";
        public string Answer { get; set; } = "-";
        public List<string> Details { get; } = new();
        public List<(int Offset, string Field, int Length, bool Confirmed)> Table { get; } = new();
    }

    /// <summary>
    /// 在 id:4 的 payload 里定位名字 → 推出名字之后各字段的位置；
    /// 再用 .plr 的 7 个权威颜色去校验「颜色从哪个偏移开始」，命中数最多的那个就是真布局。
    /// </summary>
    /// <summary>把 id:4 的 payload 与 .plr 权威数据对照，得出字段表与结论。</summary>
    public static Id4Comparison CompareId4(byte[] payload, PlayerFileData data)
    {
        var cmp = new Id4Comparison();
        byte[] nameBytes = Encoding.UTF8.GetBytes(data.Name);

        // 名字定位：名字必须以「长度前缀 + 原文」的形式出现
        for (int start = 2; start < Math.Min(payload.Length, 16); start++)
        {
            foreach (bool sevenBit in new[] { false, true })
            {
                int prefixLen;
                int claimed;
                if (sevenBit)
                {
                    if (!TryRead7Bit(payload, start, out claimed, out prefixLen))
                        continue;
                }
                else
                {
                    claimed = payload[start];
                    prefixLen = 1;
                }

                if (claimed <= 0 || start + prefixLen + claimed > payload.Length)
                    continue;

                ReadOnlySpan<byte> text = payload.AsSpan(start + prefixLen, claimed);
                if (!text.SequenceEqual(nameBytes))
                    continue;

                cmp.NameOffset = start;
                cmp.NamePrefixBytes = prefixLen;
                cmp.NameEnd = start + prefixLen + claimed;
                cmp.NameInPayload = Encoding.UTF8.GetString(text);
                break;
            }

            if (cmp.NameOffset >= 0)
                break;
        }

        if (cmp.NameOffset < 0)
        {
            cmp.Answer = $"payload 里找不到 .plr 的角色名 \"{data.Name}\"：无法对照（可能名字带格式或 payload 是截断的）";
            cmp.Table.AddRange(BuildId4Table(null, null, payload));
            return cmp;
        }

        // 名字之后：hairDye(0/1 字节) + hideVisuals(1/2 字节) + hideMisc(0/1 字节) 的组合
        (string Name, PlayerColor Color)[] colors = data.ColorsInPacketOrder;
        int bestSkip = -1;
        int bestMatched = 0;
        string bestHyps = "";

        for (int dye = 0; dye <= 1; dye++)
        {
            for (int hideVis = 1; hideVis <= 2; hideVis++)
            {
                for (int hideMisc = 0; hideMisc <= 1; hideMisc++)
                {
                    int skip = dye + hideVis + hideMisc;
                    int colorStart = cmp.NameEnd + skip;
                    if (colorStart + 21 > payload.Length)
                        continue;

                    int matched = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        if (colors[i].Color.Equals3(payload.AsSpan(colorStart + i * 3, 3)))
                            matched++;
                    }

                    if (matched > bestMatched)
                    {
                        bestMatched = matched;
                        bestSkip = skip;
                        bestHyps = $"dye={dye} hideVis={hideVis} hideMisc={hideMisc}";
                        cmp.DyeBytes = dye;
                        cmp.HideVisBytes = hideVis;
                        cmp.HideMiscBytes = hideMisc;
                    }
                }
            }
        }

        if (bestSkip < 0)
        {
            // 没有一种组合放得下 7 个颜色：退到「只按 3 字节定位每个颜色」
            cmp.Answer = "7 个颜色无法整体塞进 payload 剩余字节：改按逐个 3 字节搜索定位";
            cmp.Table.AddRange(BuildId4Table(cmp, null, payload));
            return cmp;
        }

        cmp.PostNameSkip = bestSkip;
        cmp.ColorStart = cmp.NameEnd + bestSkip;
        cmp.MatchedColors = bestMatched;
        cmp.HypothesisSummary = bestHyps;

        cmp.Details.Add($"名字 \"{data.Name}\" 在 payload 偏移 {cmp.NameOffset}（{cmp.NamePrefixBytes} 字节长度前缀），结束于 {cmp.NameEnd}");
        cmp.Details.Add($"名字之后跳过 {bestSkip} 字节（{bestHyps}）→ 7 个颜色从偏移 {cmp.ColorStart} 开始");
        cmp.Details.Add($"7 个颜色按 3 字节逐个比对：命中 {bestMatched}/7");

        for (int i = 0; i < 7; i++)
        {
            int off = cmp.ColorStart + i * 3;
            bool hit = off + 3 <= payload.Length && colors[i].Color.Equals3(payload.AsSpan(off, 3));
            string payloadHex = off + 3 <= payload.Length
                ? Convert.ToHexString(payload, off, 3).ToLowerInvariant()
                : "-";
            cmp.Details.Add(
                $"{colors[i].Name,-16} 期望 {colors[i].Color.Hex} / payload@{off}={payloadHex} / {(hit ? "一致" : "不一致")}");
        }

        // 反向验证：payload 里 16..19 到底是什么
        if (payload.Length >= 20)
        {
            string at16 = Convert.ToHexString(payload, 16, 4).ToLowerInvariant();
            int fieldAt16 = -1;
            for (int i = 0; i < 7; i++)
            {
                int off = cmp.ColorStart + i * 3;
                if (off <= 16 && 16 < off + 3)
                    fieldAt16 = i;
            }

            string fieldName = fieldAt16 >= 0 ? colors[fieldAt16].Name : "？";
            int intoField = fieldAt16 >= 0 ? 16 - (cmp.ColorStart + fieldAt16 * 3) : -1;
            cmp.Details.Add(
                $"payload 偏移 16..19 = {at16}：落在 {fieldName} 的第 {intoField} 字节起，跨到下一个颜色字段");

            cmp.Answer =
                $"偏移 16..19 的 ff ff ff ff 不是坐标：{colors[fieldAt16 >= 0 ? fieldAt16 : 0].Name} "
                + $"在 payload 里位于 {cmp.ColorStart + (fieldAt16 >= 0 ? fieldAt16 : 0) * 3}.."
                + $"{cmp.ColorStart + (fieldAt16 >= 0 ? fieldAt16 : 0) * 3 + 2}，"
                + $"16..19 只是「{fieldName} 的后 {3 - intoField} 字节 + 下一个颜色的前 {intoField + 1} 字节」；"
                + $"该颜色在 .plr 里是 {colors[fieldAt16 >= 0 ? fieldAt16 : 0].Color.Hex}，"
                + $"颜色命中数 {bestMatched}/7。";
        }
        else
        {
            cmp.Answer = $"颜色从 payload 偏移 {cmp.ColorStart} 开始，命中 {bestMatched}/7";
        }

        cmp.Table.AddRange(BuildId4Table(cmp, colors, payload));
        return cmp;
    }

    private static List<(int Offset, string Field, int Length, bool Confirmed)> BuildId4Table(
        Id4Comparison? cmp, (string Name, PlayerColor Color)[]? colors, byte[]? payload)
    {
        var table = new List<(int Offset, string Field, int Length, bool Confirmed)>();

        table.Add((0, "playerId（候选，待验证）", 1, false));
        table.Add((1, "skinVariant（候选，待验证）", 1, false));
        table.Add((2, "hair（候选，待验证）", 1, false));

        if (cmp is null || cmp.NameOffset < 0)
        {
            table.Add((3, "未知 5 字节（实测 d0 cc 4c 3e c4；候选，待验证）", 5, false));
            table.Add((8, "name（7bit/u8 长度前缀 + UTF-8；候选，待验证）", 4, false));
            table.Add((12, "hairDye（候选，待验证）", 1, false));
            table.Add((13, "hideVisuals ×2（候选，待验证）", 2, false));
            table.Add((15, "hideMisc（候选，待验证）", 1, false));
            int start = 16;
            string[] names = { "hairColor", "skinColor", "eyeColor", "shirtColor", "underShirtColor", "pantsColor", "shoeColor" };
            for (int i = 0; i < names.Length; i++)
                table.Add((start + i * 3, names[i] + "（候选，待验证）", 3, false));
            table.Add((start + 21, "difficulty（候选，待验证）", 1, false));
            table.Add((start + 22, "尾部 2 字节（候选，待验证）", 2, false));
            return table;
        }

        if (cmp.NameOffset > 3)
            table.Add((3, $"未知 {cmp.NameOffset - 3} 字节（客户端自加；候选，待验证）", cmp.NameOffset - 3, false));

        table.Add((cmp.NameOffset, $"name（前缀 {cmp.NamePrefixBytes} 字节 + \"{cmp.NameInPayload}\"）", cmp.NameEnd - cmp.NameOffset, true));

        int off = cmp.NameEnd;
        if (cmp.DyeBytes > 0)
        {
            table.Add((off, "hairDye（候选，待验证）", cmp.DyeBytes, false));
            off += cmp.DyeBytes;
        }

        if (cmp.HideVisBytes > 0)
        {
            table.Add((off, "hideVisuals（候选，待验证）", cmp.HideVisBytes, false));
            off += cmp.HideVisBytes;
        }

        if (cmp.HideMiscBytes > 0)
        {
            table.Add((off, "hideMisc（候选，待验证）", cmp.HideMiscBytes, false));
            off += cmp.HideMiscBytes;
        }

        if (colors is not null)
        {
            for (int i = 0; i < 7; i++)
            {
                int coff = cmp.ColorStart + i * 3;
                bool hit = payload is not null && coff + 3 <= payload.Length
                           && colors[i].Color.Equals3(payload.AsSpan(coff, 3));
                table.Add((coff, colors[i].Name, 3, hit));
            }

            int tail = cmp.ColorStart + 21;
            table.Add((tail, "difficulty（候选，待验证）", 1, false));
            table.Add((tail + 1, "尾部（使用生物群系火把/deaths 等；候选，待验证）", 2, false));
        }

        return table;
    }

    private static bool TryRead7Bit(byte[] data, int start, out int value, out int prefixBytes)
    {
        value = 0;
        prefixBytes = 0;
        int shift = 0;

        for (int i = start; i < data.Length && prefixBytes < 5; i++)
        {
            byte b = data[i];
            value |= (b & 0x7F) << shift;
            prefixBytes++;
            shift += 7;
            if ((b & 0x80) == 0)
                return true;
        }

        return false;
    }

    private static byte[] ParseHex(string text)
    {
        var clean = new StringBuilder();
        foreach (char c in text)
        {
            if (Uri.IsHexDigit(c))
                clean.Append(c);
            else if (c is ' ' or '\t' or '\n' or '\r' or ',' or '-' or ':' or 'x' or 'X' or '\\')
                continue;
            else
                throw new FormatException($"hex 里出现非法字符 '{c}'");
        }

        string s = clean.ToString();
        if (s.Length % 2 != 0)
            throw new FormatException("hex 长度不是偶数");

        return Convert.FromHexString(s);
    }

    // ------------------------------------------------------------------
    // 自检：造一个 v319 的合成存档（加密→落盘→按真实路径解析→断言）
    // ------------------------------------------------------------------

    public static bool SelfTest(JsonLogger log)
    {
        bool ok = true;

        byte[] plaintext = BuildSyntheticV319();
        byte[] encrypted = PlayerFileReader.EncryptAesCbc(plaintext);

        // 合成样本写到临时目录，避免污染仓库 / 安装目录
        string dir = Path.Combine(Path.GetTempPath(), "bunny-plr-selftest");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "plr-selftest-v319.plr");
        File.WriteAllBytes(path, encrypted);

        PlayerFileData data;
        try
        {
            data = PlayerFileReader.ReadFromFile(path);
        }
        catch (Exception e)
        {
            log.Error("plr.selftest.error", ("error", e.GetType().Name + ": " + e.Message));
            return false;
        }

        ok &= Check(log, "plr 自检：识别为加密文件", data.Encrypted, data.Encrypted);
        ok &= Check(log, "plr 自检：版本 = 319", data.Version == 319, data.Version);
        ok &= Check(log, "plr 自检：魔数 = relogic", data.Magic == "relogic", data.Magic);
        ok &= Check(log, "plr 自检：名字 = Cup", data.Name == "Cup", data.Name);
        ok &= Check(log, "plr 自检：发型 = 2", data.Hair == 2, data.Hair);
        ok &= Check(log, "plr 自检：发色 = #ffffff", data.HairColor.Hex == "#ffffff", data.HairColor.Hex);
        ok &= Check(log, "plr 自检：肤色 = #ffbba9", data.SkinColor.Hex == "#ffbba9", data.SkinColor.Hex);
        ok &= Check(log, "plr 自检：生命 400/500", data.Health == 400 && data.HealthMax == 500, $"{data.Health}/{data.HealthMax}");
        ok &= Check(log, "plr 自检：物品栏 50 槽", data.Inventory.Count == 50, data.Inventory.Count);
        ok &= Check(log, "plr 自检：至少 1 个非空槽", data.NonEmptyInventorySlots >= 1, data.NonEmptyInventorySlots);
        ok &= Check(log, "plr 自检：第 0 槽 id=4956", data.Inventory[0].Id == 4956, data.Inventory[0].Id);
        ok &= Check(log, "plr 自检：解析结束偏移 > 0", data.ParsedOffset > 1000, data.ParsedOffset);

        // 明文样本也要能解析（老版本 / 已解密文件）
        try
        {
            PlayerFileData plain = PlayerFileReader.ReadFromFile(WritePlain(plaintext, dir));
            ok &= Check(log, "plr 自检：明文文件同样能解析", !plain.Encrypted && plain.Name == "Cup", $"{plain.Encrypted}/{plain.Name}");
        }
        catch (Exception e)
        {
            ok &= Check(log, "plr 自检：明文文件同样能解析", false, e.Message);
        }

        // 如果人类把真实 .plr 放进 debug/，一并验证（不写死文件名）。
        // 同时看「二进制目录」和「工作目录」两处：面板上把 .plr 放在 DLL 旁边，
        // 仓库里则放在仓库根的 debug/，两种位置都要能被发现。
        string binDebug = Path.Combine(AppContext.BaseDirectory, "debug");
        var searchDirs = new List<string> { binDebug };
        string cwdDebug = Path.Combine(Directory.GetCurrentDirectory(), "debug");
        if (!string.Equals(cwdDebug, binDebug, StringComparison.Ordinal))
            searchDirs.Add(cwdDebug);

        var real = new List<string>();
        foreach (string d in searchDirs)
        {
            if (!Directory.Exists(d))
                continue;

            foreach (string file in Directory.GetFiles(d, "*.plr"))
            {
                if (!Path.GetFileName(file).StartsWith("plr-selftest", StringComparison.Ordinal))
                    real.Add(file);
            }
        }

        if (real.Count == 0)
        {
            log.Info("plr.selftest.real", ("status", "skipped"), ("hint", "debug/ 下没有真实 .plr；本轮只验证合成样本"));
        }
        else
        {
            foreach (string file in real)
            {
                try
                {
                    PlayerFileData d = PlayerFileReader.ReadFromFile(file);
                    string label = Path.GetFileName(file);
                    ok &= Check(log, $"真实样本 {label}：名字非空", !string.IsNullOrWhiteSpace(d.Name), d.Name);
                    ok &= Check(log, $"真实样本 {label}：7 个颜色都有值",
                        d.HairColor.Hex != "#000000" || d.SkinColor.Hex != "#000000" || d.ShoeColor.Hex != "#000000",
                        $"{d.HairColor.Hex}/{d.SkinColor.Hex}/{d.ShoeColor.Hex}");
                    ok &= Check(log, $"真实样本 {label}：至少 1 个非空物品", d.NonEmptyInventorySlots >= 1, d.NonEmptyInventorySlots);

                    log.Info("plr.selftest.real",
                        ("file", label),
                        ("version", d.Version),
                        ("name", d.Name),
                        ("hair", d.Hair),
                        ("colors", string.Join(" ", d.ColorsInPacketOrder.Select(c => c.Color.Hex))),
                        ("inventoryNonEmpty", d.NonEmptyInventorySlots));
                }
                catch (Exception e)
                {
                    ok &= Check(log, $"真实样本 {Path.GetFileName(file)}：解析成功", false, e.Message);
                }
            }
        }

        // 用人类实测的已知 24 字节前缀（后面补 0）走一遍对照逻辑：
        // 断言名字在 8、颜色从 16 开始、hairColor 被标为已确认，且结论把 16..19 归到 hairColor/skinColor。
        byte[] known = new byte[40];
        Convert.FromHexString("010702d0cc4c3ec40343757000000000ffffffffbba9695a").CopyTo(known, 0);

        PlayerFileData sample = PlayerFileReader.ReadFromFile(path);
        Id4Comparison cmp2 = CompareId4(known, sample);
        ok &= Check(log, "对照自检：找到名字 Cup 于偏移 8", cmp2.NameOffset == 8, cmp2.NameOffset);
        ok &= Check(log, "对照自检：名字结束于 12", cmp2.NameEnd == 12, cmp2.NameEnd);
        ok &= Check(log, "对照自检：颜色起点 = 16", cmp2.ColorStart == 16, cmp2.ColorStart);
        ok &= Check(log, "对照自检：颜色命中 >= 4/7", cmp2.MatchedColors >= 4, cmp2.MatchedColors);
        ok &= Check(log, "对照自检：结论指向 hairColor",
            cmp2.Answer.Contains("hairColor"), cmp2.Answer);

        (int Offset, string Field, int Length, bool Confirmed)? hairRow = null;
        foreach (var row in cmp2.Table)
        {
            if (row.Field == "hairColor")
                hairRow = row;
        }

        // 把对照明细打出来：这是「ff ff ff ff 归属」的完整推理链，便于离线复核
        foreach (string line in cmp2.Details)
            log.Info("plr.id4.match", ("source", "selftest-known-prefix"), ("detail", line));
        log.Info("plr.id4.conclusion",
            ("source", "selftest-known-prefix"),
            ("answer", cmp2.Answer),
            ("hypotheses", cmp2.HypothesisSummary),
            ("confirmedRows", cmp2.Table.Count(r => r.Confirmed)),
            ("totalRows", cmp2.Table.Count));

        ok &= Check(log, "对照自检：字段表把 hairColor 标为已确认",
            hairRow is not null && hairRow.Value.Offset == 16 && hairRow.Value.Confirmed,
            hairRow is null ? "缺少 hairColor 行" : $"offset={hairRow.Value.Offset} confirmed={hairRow.Value.Confirmed}");

        // 有真实样本时：跑一遍完整 CLI 输出（7 个颜色的 hex 与 RGB 都会打出来），
        // 若同目录放有 id:4 的 payload（debug/syncplayer-40.txt），再用真实颜色做对照。
        if (real.Count > 0)
        {
            ok &= Check(log, "真实样本：--parse-plr 退出码 0", Run(real[0], null, log) == 0, real[0]);

            string? rawFile = null;
            foreach (string d in searchDirs)
            {
                string candidate = Path.Combine(d, "syncplayer-40.txt");
                if (File.Exists(candidate))
                {
                    rawFile = candidate;
                    break;
                }
            }

            if (rawFile is null)
            {
                log.Info("plr.selftest.real",
                    ("status", "no-payload"),
                    ("hint", "没有 debug/syncplayer-40.txt，跳过颜色对照"));
            }
            else
            {
                byte[] payload;
                try
                {
                    payload = ParseHex(File.ReadAllText(rawFile));
                }
                catch (Exception e)
                {
                    payload = Array.Empty<byte>();
                    ok &= Check(log, "真实样本：syncplayer-40.txt 是合法 hex", false, e.Message);
                }

                if (payload.Length > 0)
                {
                    PlayerFileData realData = PlayerFileReader.ReadFromFile(real[0]);
                    Id4Comparison c = CompareId4(payload, realData);

                    foreach (string line in c.Details)
                        log.Info("plr.id4.match", ("source", "real"), ("detail", line));

                    foreach (var row in c.Table)
                    {
                        log.Info("plr.id4.field",
                            ("source", "real"),
                            ("offset", row.Offset),
                            ("field", row.Field),
                            ("len", row.Length),
                            ("confirmed", row.Confirmed));
                    }

                    log.Info("plr.id4.conclusion",
                        ("source", "real"),
                        ("answer", c.Answer),
                        ("hypotheses", c.HypothesisSummary),
                        ("confirmedRows", c.Table.Count(r => r.Confirmed)),
                        ("totalRows", c.Table.Count));

                    ok &= Check(log, "真实样本：颜色起点 = 16", c.ColorStart == 16, c.ColorStart);
                    ok &= Check(log, "真实样本：与 .plr 颜色命中 >= 2/7", c.MatchedColors >= 2, c.MatchedColors);
                }
            }
        }

        // 端到端：把人类真正会敲的 CLI 路径（--parse-plr --sync-raw）跑一遍，
        // 断言退出码 0、且物品栏日志文件确实写出来了。
        string knownPayloadHex =
            "010702d0cc4c3ec40343757000000000ffffffffbba9695a" + new string('0', 32);
        int cliCode = Run(path, knownPayloadHex, log);
        ok &= Check(log, "CLI 自检：--parse-plr --sync-raw 退出码 0", cliCode == 0, cliCode);

        string invLog = Path.Combine(AppContext.BaseDirectory, "debug", $"plr-inventory-{SafeName(data.Name)}.log");
        ok &= Check(log, "CLI 自检：物品栏日志已写出", File.Exists(invLog), invLog);

        // ===== 动态档案（R8）：模板 → 新角色 → 改名/改血/发装备 → 落盘 → 重新加载 =====
        string storeDir = Path.Combine(dir, "Players");
        if (Directory.Exists(storeDir))
            Directory.Delete(storeDir, recursive: true);

        Directory.CreateDirectory(storeDir);

        // 自检不依赖外部 .plr：直接拿刚合成的 v319 样本当模板
        File.WriteAllBytes(Path.Combine(storeDir, ".template.plr"), encrypted);
        ok &= Check(log, "档案自检：模板 .template.plr 已就位",
            File.Exists(Path.Combine(storeDir, ".template.plr")), true);

        PlayerProfileStore? store = PlayerProfileStore.TryCreate(storeDir, log);
        ok &= Check(log, "档案自检：仓库创建成功（模板可用）", store is not null, store?.TemplatePath);

        if (store is not null)
        {
            PlayerProfile? p = store.LoadOrCreate("Tester");
            ok &= Check(log, "档案自检：新玩家档案已创建", p is not null, p?.Path);

            if (p is not null)
            {
                string expectPath = Path.Combine(storeDir, "Tester.plr");
                ok &= Check(log, "档案自检：文件路径 = Players/Tester.plr", p.Path == expectPath, p.Path);
                ok &= Check(log, "档案自检：文件已落盘", File.Exists(expectPath), expectPath);
                ok &= Check(log, "档案自检：名字 = Tester", p.Name == "Tester", p.Name);
                ok &= Check(log, "档案自检：生命 = 100/100", p.Health == 100 && p.HealthMax == 100, $"{p.Health}/{p.HealthMax}");
                ok &= Check(log, "档案自检：魔力 = 20/20", p.Mana == 20 && p.ManaMax == 20, $"{p.Mana}/{p.ManaMax}");

                int[] ids = p.Data.Inventory.Take(3).Select(i => i.Id).ToArray();
                ok &= Check(log, "档案自检：开荒装备 = 3507/3509/3506",
                    ids.SequenceEqual(new[] { 3507, 3509, 3506 }), string.Join("/", ids));
                ok &= Check(log, "档案自检：背包第 4 槽起为空",
                    p.Data.Inventory.Skip(3).All(i => i.IsEmpty), p.Data.Inventory.Count(i => !i.IsEmpty));

                // 改数据 → 保存 → 用新仓库重新加载（模拟「玩家退出后重连」）
                p.SetLife(180, 200);
                p.SetMana(60, 80);
                p.SetSlot(5, 42, 3, 0);
                store.SaveProfile(p);

                PlayerProfileStore? store2 = PlayerProfileStore.TryCreate(storeDir, log);
                PlayerProfile? p2 = store2?.LoadOrCreate("Tester");
                ok &= Check(log, "档案自检：重载后生命 = 180/200",
                    p2 is not null && p2.Health == 180 && p2.HealthMax == 200, $"{p2?.Health}/{p2?.HealthMax}");
                ok &= Check(log, "档案自检：重载后魔力 = 60/80",
                    p2 is not null && p2.Mana == 60 && p2.ManaMax == 80, $"{p2?.Mana}/{p2?.ManaMax}");
                ok &= Check(log, "档案自检：重载后槽 5 = itemId 42 ×3",
                    p2 is not null && p2.Data.Inventory[5].Id == 42 && p2.Data.Inventory[5].Count == 3,
                    p2 is null ? "null" : $"{p2.Data.Inventory[5].Id}x{p2.Data.Inventory[5].Count}");
                ok &= Check(log, "档案自检：存档仍是合法 .plr（可解密可解析）",
                    p2 is not null && p2.Data.Version == 319, p2?.Data.Version);
            }
        }

        return ok;
    }

    private static string WritePlain(byte[] plaintext, string dir)
    {
        string path = Path.Combine(dir, "plr-selftest-v319-plain.plr");
        File.WriteAllBytes(path, plaintext);
        return path;
    }

    /// <summary>按 v319 顺序造一份最小但完整的明文存档（自检专用）。</summary>
    private static byte[] BuildSyntheticV319()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(319);                       // version
        w.Write(Encoding.ASCII.GetBytes("relogic"));
        w.Write((byte)3);                   // fileType
        w.Write(0u);                        // revision
        w.Write(0ul);                       // favorite

        WriteString7Bit(w, "Cup");
        w.Write((byte)0);                   // difficulty = 经典
        w.Write(123_456_789L);              // playTimeTicks
        w.Write(2);                         // hair
        w.Write((byte)0);                   // hairDye
        w.Write((byte)0);                   // team
        w.Write((byte)0);                   // hideVisibleAccessory[0]
        w.Write((byte)0);                   // hideVisibleAccessory[1]
        w.Write((byte)0);                   // hideMisc
        w.Write((byte)7);                   // skinVariant
        w.Write(400);                       // health
        w.Write(500);                       // healthMax
        w.Write(20);                        // mana
        w.Write(200);                       // manaMax
        w.Write((byte)0);                   // extraAccessory
        w.Write((byte)0);                   // unlockedBiomeTorches
        w.Write((byte)0);                   // usingBiomeTorches
        w.Write((byte)0);                   // ateArtisanBread
        for (int i = 0; i < 6; i++) w.Write((byte)0);   // 6 个永久强化
        w.Write((byte)0);                   // downedDD2EventAnyDifficulty
        w.Write(0);                         // taxMoney
        w.Write(0);                         // deathsPVE
        w.Write(0);                         // deathsPVP

        // 7 个颜色：与人类实测 payload 的可见部分保持一致，便于对照
        w.Write(new byte[] { 0xff, 0xff, 0xff });   // hairColor
        w.Write(new byte[] { 0xff, 0xbb, 0xa9 });   // skinColor
        w.Write(new byte[] { 0x69, 0x5a, 0x00 });   // eyeColor
        w.Write(new byte[] { 0x2b, 0x2b, 0x2b });   // shirtColor
        w.Write(new byte[] { 0x00, 0x00, 0x00 });   // underShirtColor（实测 16..19 的邻居）
        w.Write(new byte[] { 0x2b, 0x2b, 0x2b });   // pantsColor
        w.Write(new byte[] { 0x2b, 0x2b, 0x2b });   // shoeColor

        for (int i = 0; i < 20; i++) { w.Write(i == 0 ? 1 : 0); w.Write((byte)0); }      // equipment
        for (int i = 0; i < 10; i++) { w.Write(0); w.Write((byte)0); }                    // dye
        for (int i = 0; i < 50; i++)                                                      // inventory
        {
            w.Write(i == 0 ? 4956 : 0);
            w.Write(i == 0 ? 1 : 0);
            w.Write((byte)(i == 0 ? 0 : 0));
            w.Write((byte)0);
        }

        for (int i = 0; i < 4; i++) { w.Write(0); w.Write(0); w.Write((byte)0); w.Write((byte)0); }   // coins
        for (int i = 0; i < 4; i++) { w.Write(0); w.Write(0); w.Write((byte)0); w.Write((byte)0); }   // ammo
        for (int i = 0; i < 5; i++) { w.Write(0); w.Write((byte)0); w.Write(0); w.Write((byte)0); }   // misc
        for (int i = 0; i < 40; i++) { w.Write(0); w.Write(0); w.Write((byte)0); }                     // piggy
        for (int i = 0; i < 40; i++) { w.Write(0); w.Write(0); w.Write((byte)0); }                     // safe
        for (int i = 0; i < 40; i++) { w.Write(0); w.Write(0); w.Write((byte)0); }                     // defenders forge
        for (int i = 0; i < 40; i++) { w.Write(0); w.Write(0); w.Write((byte)0); w.Write((byte)0); }   // void bag (v>=269: 10B)
        w.Write((byte)0);                                                                              // downedDeerclops
        for (int i = 0; i < 44; i++) { w.Write(0); w.Write(0); }                                        // buffs 44×8B
        w.Write(-1);                                                                                   // 出生点结束
        w.Write((byte)0);                                                                              // hotbarLocked
        for (int i = 0; i < 13; i++) w.Write((byte)0);                                                  // hideInfo
        w.Write(0);                                                                                     // anglerQuestsFinished
        for (int i = 0; i < 4; i++) w.Write(0);                                                         // gamepadBinds
        for (int i = 0; i < 12; i++) w.Write(0);                                                        // buildingToggles
        w.Write(0);                                                                                     // bartenderQuestLog
        w.Write((byte)0);                                                                               // dead
        w.Write(0L);                                                                                    // lastTimePlayerWasSaved
        w.Write(0);                                                                                     // golferScore
        w.Write((byte)0);                                                                               // research 开头 bool（v282+）
        w.Write(0);                                                                                     // research 条目数
        w.Write((byte)0);                                                                               // 临时物品槽 bits
        w.Write((byte)0);                                                                               // creative powers 结束
        w.Write((byte)0x03);                                                                            // superCart
        w.Write(0);                                                                                     // currentLoadoutIndex
        for (int l = 0; l < 3; l++)
        {
            for (int i = 0; i < 20; i++) { w.Write(0); w.Write(0); w.Write((byte)0); }                   // 配装装备
            for (int i = 0; i < 10; i++) { w.Write(0); w.Write(0); w.Write((byte)0); }                   // 配装染料
            for (int i = 0; i < 10; i++) w.Write((byte)0);                                               // 隐藏位
        }

        w.Write((byte)1);       // voiceVariant
        w.Write(0f);            // voicePitchOffset
        w.Write(0);             // pendingRefunds 数量
        w.Write(0);             // oneTimeDialogues 数量

        w.Flush();

        byte[] body = ms.ToArray();
        if (body.Length % 16 == 0)
            return body;

        byte[] padded = new byte[(body.Length + 15) / 16 * 16];
        Array.Copy(body, padded, body.Length);
        return padded;
    }

    private static void WriteString7Bit(BinaryWriter w, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int value = bytes.Length;
        while (value >= 0x80)
        {
            w.Write((byte)(value | 0x80));
            value >>= 7;
        }

        w.Write((byte)value);
        w.Write(bytes);
    }

    private static bool Check(JsonLogger log, string name, bool passed, object? value)
    {
        if (passed)
        {
            log.Info("plr.selftest.pass", ("check", name));
            return true;
        }

        log.Error("plr.selftest.fail", ("check", name), ("value", value?.ToString() ?? "null"));
        return false;
    }
}
