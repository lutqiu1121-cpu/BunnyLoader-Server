using System.Text.Json;

using BunnyLoader.Server.Player;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// 用 golden vectors 自检编解码：向量由 tools/proto_ref.py 生成（Python 侧已验证）。
/// 任何一个失败都说明布局实现与参考实现不一致。
/// </summary>
public static class ProtocolSelfTest
{
    public static bool Run(JsonLogger log, string testDataPath)
    {
        bool ok = true;

        var world = new WorldData { WorldName = "ProbeTiny" };
        byte[] worldFrame = Messages.WorldDataPacket(world);

        ok &= Check(log, "world frame payload = 173", worldFrame.Length - 3 == 173, worldFrame.Length - 3);
        ok &= Check(log, "world frame id = 7", worldFrame[2] == MessageId.WorldData, worldFrame[2]);

        try
        {
            WorldData decoded = WorldData.Decode(worldFrame[3..]);
            ok &= Check(log, "world decode consumes all", decoded.Consumed == worldFrame.Length - 3, decoded.Consumed);
            ok &= Check(log, "world name round-trips", decoded.WorldName == "ProbeTiny", decoded.WorldName);
            byte[] again = Messages.WorldDataPacket(decoded);
            ok &= Check(log, "world re-encode is byte-identical", again.AsSpan().SequenceEqual(worldFrame), again.Length);
        }
        catch (Exception e)
        {
            ok = false;
            log.Error("codec.selftest.error", ("stage", "world-roundtrip"), ("error", e.Message));
        }

        if (!File.Exists(testDataPath))
        {
            log.Warn("codec.selftest.no-vectors", ("path", testDataPath));
            return ok;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(testDataPath));
            foreach (JsonElement testCase in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                string name = testCase.GetProperty("name").GetString() ?? "";
                string expected = testCase.GetProperty("hex").GetString() ?? "";

                byte[]? actual = name switch
                {
                    "hello" => Messages.HelloPacket("Terraria326"),
                    "connection_approved" => Messages.ConnectionApprovedPacket(1, false),
                    "welcome" => Messages.WelcomePacket(1),
                    "world_data" => worldFrame,
                    _ => null,
                };

                if (actual is null)
                    continue;

                string got = Convert.ToHexString(actual).ToLowerInvariant();
                ok &= Check(log, $"vector {name}", got == expected, actual.Length);
            }
        }
        catch (Exception e)
        {
            ok = false;
            log.Error("codec.selftest.error", ("stage", "vectors"), ("error", e.Message));
        }

        ok &= Guard(log, "syncplayer4", () => RunSyncPlayer4(log));
        ok &= Guard(log, "playercontrols13", () => RunPlayerControls13(log));
        ok &= Guard(log, "frame-headers", () => RunFrameHeaders(log, world));

        return ok;
    }

    /// <summary>任何一条新断言抛异常都要变成「一条红色断言」，不能让 --selftest 直接崩掉。</summary>
    private static bool Guard(JsonLogger log, string stage, Func<bool> body)
    {
        try
        {
            return body();
        }
        catch (Exception e)
        {
            log.Error("codec.selftest.error", ("stage", stage), ("error", e.GetType().Name + ": " + e.Message));
            return false;
        }
    }

    /// <summary>
    /// 第 8 章验收 (1)(2) 的自动化部分：id:4 只做**只读**诊断，改由断言锁死。
    /// 本轮按技术主管批注不包含任何 id:4 字节改写逻辑，所以这里断言的是「解析结果」而不是「改写成什么」。
    /// </summary>
    private static bool RunSyncPlayer4(JsonLogger log)
    {
        bool ok = true;

        // ===== 金标：手机客户端实测发来的完整 40 字节（不是构造出来的） =====
        const string hex =
            "01 07 02 d0 cc 4c 3e c4 03 43 75 70 00 00 00 00 ff ff ff ff bb a9 69 5a 4b "
            + "ff ff ff ff ff ff ab c0 ab ff ff ff 00 10 00";

        byte[] sample = Convert.FromHexString(hex.Replace(" ", ""));
        ok &= Check(log, "id:4 金标长度 = 40", sample.Length == SyncPlayer4.PayloadLength, sample.Length);

        SyncPlayer4.Report r = SyncPlayer4.Analyze(sample);

        ok &= Check(log, "id:4 判定长度合法", r.LengthOk, r.LengthOk);
        ok &= Check(log, "id:4 完整 hex 不截断（40 个 token）", r.HexSpaced.Split(' ').Length == 40, r.HexSpaced.Split(' ').Length);

        // 魔改布局逐字段（与 .plr 交叉验证过的偏移）
        ok &= Check(log, "id:4 [0] playerId = 0x01", r.PlayerId == 0x01, r.PlayerId);
        ok &= Check(log, "id:4 [1] skinVariant = 7（.plr 也是 7）", r.SkinVariant == 7, r.SkinVariant);
        ok &= Check(log, "id:4 [3..6] = f32 0.2", Math.Abs(r.FloatField - 0.2f) < 1e-6, r.FloatField);
        ok &= Check(log, "id:4 [7] hair = 196（.plr 也是 196）", r.Hair == 196, r.Hair);
        ok &= Check(log, "id:4 [8..11] name = Cup（1 字节前缀）", r.Name == "Cup" && r.NamePrefixBytes == 1, r.Name);
        ok &= Check(log, "id:4 [12] hairDye = 0", r.HairDye == 0, r.HairDye);
        ok &= Check(log, "id:4 [13..14] hideVisuals = 0,0", r.HideVisual0 == 0 && r.HideVisual1 == 0, $"{r.HideVisual0},{r.HideVisual1}");
        ok &= Check(log, "id:4 [15] hideMisc = 0", r.HideMisc == 0, r.HideMisc);

        // 7 个颜色：与 .plr 逐字节一致（这是布局正确的最硬证据）
        string[] expected = { "#ffffff", "#ffbba9", "#695a4b", "#ffffff", "#ffffff", "#abc0ab", "#ffffff" };
        var colors = r.Colors;
        ok &= Check(log, "id:4 有 7 个颜色", colors.Length == 7, colors.Length);
        for (int i = 0; i < colors.Length && i < expected.Length; i++)
        {
            ok &= Check(log, $"id:4 [16+3×{i}] {colors[i].Name} = {expected[i]}",
                colors[i].Hex == expected[i], colors[i].Hex);
        }

        ok &= Check(log, "id:4 [37] difficulty = 0", r.Difficulty == 0, r.Difficulty);
        ok &= Check(log, "id:4 [38..39] tail = 1000", r.Tail == "1000", r.Tail);

        // ===== 回传：原样 Clone，只改第 0 字节 =====
        byte[] echo = SyncPlayer4.BuildEcho(sample, 0x01);
        ok &= Check(log, "id:4 回传长度 = 40", echo.Length == 40, echo.Length);
        ok &= Check(log, "id:4 回传 [0] = whoAmI", echo[0] == 0x01, echo[0]);
        ok &= Check(log, "id:4 回传 [1..39] 与原包逐字节相同",
            echo.AsSpan(1).SequenceEqual(sample.AsSpan(1)), SyncPlayer4.ToSpacedHex(echo));

        byte[] echo2 = SyncPlayer4.BuildEcho(sample, 0x07);
        ok &= Check(log, "id:4 改 whoAmI 不改其它字节", echo2[0] == 0x07 && echo2.AsSpan(1).SequenceEqual(sample.AsSpan(1)), echo2[0]);

        // ===== R6 服务端外观覆盖：只写已验证字段，不触碰客户端专有字段 =====
        var profile = new PlayerFileData
        {
            Hair = 196,
            HairColor = new PlayerColor(0x11, 0x12, 0x13),
            SkinColor = new PlayerColor(0x21, 0x22, 0x23),
            EyeColor = new PlayerColor(0x31, 0x32, 0x33),
            ShirtColor = new PlayerColor(0x41, 0x42, 0x43),
            UnderShirtColor = new PlayerColor(0x51, 0x52, 0x53),
            PantsColor = new PlayerColor(0x61, 0x62, 0x63),
            ShoeColor = new PlayerColor(0x71, 0x72, 0x73),
        };
        byte[] appearance = (byte[])sample.Clone();
        PlayerSsc.ApplyAppearance(appearance, profile, 1);
        ok &= Check(log, "id:4 hair 来自服务器档案", appearance[SyncPlayer4.OffHair] == 196, appearance[SyncPlayer4.OffHair]);
        ok &= Check(log, "id:4 7 色来自服务器档案",
            appearance.AsSpan(16, 21).SequenceEqual(Convert.FromHexString("111213212223313233414243515253616263717273")),
            Convert.ToHexString(appearance.AsSpan(16, 21)));
        ok &= Check(log, "id:4 不覆盖未知字段",
            appearance[2] == sample[2] && appearance.AsSpan(3, 4).SequenceEqual(sample.AsSpan(3, 4))
            && appearance.AsSpan(38, 2).SequenceEqual(sample.AsSpan(38, 2)),
            SyncPlayer4.ToSpacedHex(appearance));

        byte[] emptySlot = PlayerSsc.InventorySlotPayload(0, 0, new PlayerItem());
        ok &= Check(log, "id:5 空槽严格全零 9B", emptySlot.SequenceEqual(new byte[9]), Convert.ToHexString(emptySlot));

        // ===== 长度不是 40：不许猜字段 =====
        byte[] shortPack = sample.AsSpan(0, 39).ToArray();
        SyncPlayer4.Report bad = SyncPlayer4.Analyze(shortPack);
        ok &= Check(log, "id:4 39 字节被判为非法且不解析字段", !bad.LengthOk && bad.Fields.Contains("不猜"), bad.Fields);

        return ok;
    }

    /// <summary>id:13 位置字段（偏移 6，单位像素）解析：只验证解析，不参与状态改动。</summary>
    private static bool RunPlayerControls13(JsonLogger log)
    {
        bool ok = true;

        byte[] controls = new byte[14];
        controls[0] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(controls.AsSpan(6, 4), 2105 * 16 + 8);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(controls.AsSpan(10, 4), 312 * 16);

        bool parsed = PlayerControls13.TryParsePosition(controls, out float px, out float py, out int tx, out int ty);
        ok &= Check(log, "id:13 偏移 6 可解析", parsed, parsed);
        ok &= Check(log, "id:13 像素 X = 33688", MathF.Abs(px - 33688f) < 0.5f, px);
        ok &= Check(log, "id:13 像素 Y = 4992", MathF.Abs(py - 4992f) < 0.5f, py);
        ok &= Check(log, "id:13 格坐标 = 2105,312", tx == 2105 && ty == 312, $"{tx},{ty}");

        ok &= Check(log, "id:13 短包拒绝解析", !PlayerControls13.TryParsePosition(new byte[10], out _, out _, out _, out _), "10 字节");
        ok &= Check(log, "id:13 NaN 拒绝解析",
            !PlayerControls13.TryParsePosition(new byte[] { 1, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 },
                out _, out _, out _, out _), "NaN");

        return ok;
    }

    /// <summary>第 8 章验收 (4)：任何回包的总长度字段必须等于实际字节数组长度。</summary>
    private static bool RunFrameHeaders(JsonLogger log, WorldData world)
    {
        bool ok = true;

        (byte id, byte[] frame)[] frames =
        {
            (MessageId.Hello, Messages.HelloPacket("Terraria326")),
            (MessageId.ConnectionApproved, Messages.ConnectionApprovedPacket(1, false)),
            (MessageId.NetModules, Messages.WelcomePacket(1)),
            (MessageId.WorldData, Messages.WorldDataPacket(world)),
            (MessageId.StartPlaying, Messages.StartPlayingPacket()),
            (MessageId.PlayerSpawn, Messages.PlayerSpawnPacket(1, 2105, 312)),
            (MessageId.PlayerActive, Codec.Frame(MessageId.PlayerActive, new[] { (byte)1, (byte)1 })),
            (MessageId.FinishedConnecting, Messages.FinishedConnectingPacket()),
            (MessageId.SyncPlayer, Codec.Frame(MessageId.SyncPlayer, new byte[40])),
        };

        foreach ((byte id, byte[] frame) in frames)
        {
            int declared = frame[0] | (frame[1] << 8);
            bool lengthOk = declared == frame.Length;
            bool idOk = frame[2] == id;

            ok &= Check(log, $"帧头 id={id} 长度字段 = 实际长度", lengthOk, $"{declared}/{frame.Length}");
            ok &= Check(log, $"帧头 id={id} 消息号一致", idOk, frame[2]);
        }

        // id:12 的负载固定 9 字节（u8 + i16 + i16 + 4 字节 0），整帧 12。
        // 金标：01 39 08 38 01 00 00 00 00（技术主管实测字节）
        byte[] spawn = Messages.PlayerSpawnPacket(1, 2105, 312);
        ok &= Check(log, "id:12 负载 9 字节 / 整帧 12 字节", spawn.Length == 12 && spawn.Length - 3 == 9, spawn.Length);
        ok &= Check(log, "id:12 金标字节 = 01 39 08 38 01 00 00 00 00",
            Convert.ToHexString(spawn.AsSpan(3)).ToLowerInvariant() == "013908380100000000",
            Convert.ToHexString(spawn.AsSpan(3)).ToLowerInvariant());

        // ===== R7.1 固定长度金标（来自客户端实测字节，不许再变）=====
        byte[] life = PlayerSsc.LifePayload(1, 100, 100);
        ok &= Check(log, "id:16 payload = 5 字节", life.Length == 5, life.Length);
        ok &= Check(log, "id:16 金标 = 01 64 00 64 00",
            Convert.ToHexString(life).ToLowerInvariant() == "0164006400", Convert.ToHexString(life));

        byte[] mana = PlayerSsc.ManaPayload(1, 20, 20);
        ok &= Check(log, "id:42 payload = 5 字节", mana.Length == 5, mana.Length);
        ok &= Check(log, "id:42 金标 = 01 14 00 14 00",
            Convert.ToHexString(mana).ToLowerInvariant() == "0114001400", Convert.ToHexString(mana));

        byte[] slot = PlayerSsc.InventorySlotPayload(1, 0, new PlayerItem { Id = 3507, Count = 1, Prefix = 0x24 });
        ok &= Check(log, "id:5 payload = 9 字节", slot.Length == 9, slot.Length);
        ok &= Check(log, "id:5 金标 = 01 00 00 01 00 24 b3 0d 00",
            Convert.ToHexString(slot).ToLowerInvariant() == "010000010024b30d00", Convert.ToHexString(slot));

        // ===== R6 候选帧 golden（只断言自洽，不代表协议已验证）=====
        var candidates = BunnyLoader.Server.Experiment.CandidateInjector.Build(2105, 312);
        ok &= Check(log, "实验候选数 = 3", candidates.Count == 3, candidates.Count);
        ok &= Check(log, "候选 A id:21 payload = 22B",
            candidates[0].Id == 21 && candidates[0].Frame.Length - 3 == 22, candidates[0].Frame.Length - 3);
        ok &= Check(log, "候选 B id:21 payload = 24B",
            candidates[1].Id == 21 && candidates[1].Frame.Length - 3 == 24, candidates[1].Frame.Length - 3);
        ok &= Check(log, "候选 C id:23 高风险占位",
            candidates[2].Id == 23 && candidates[2].Frame.Length - 3 == 20, candidates[2].Frame.Length - 3);
        foreach (var c in candidates)
        {
            int declared = c.Frame[0] | (c.Frame[1] << 8);
            ok &= Check(log, $"候选 {c.Index} 帧头长度一致",
                declared == c.Frame.Length, $"{declared}/{c.Frame.Length}");
        }

        // ===== R9 真帧 golden（2026-11-20 抓包；日志 hex 带 … 截断，按公式重建）=====
        byte[] uuidPayload = Codec.String("f4ea4156-ddcc-4be1-baf9-66ba6141c849");
        byte[] syncPayload = Codec.String("bunnyloader-sync:{\"mods\":[],\"textures\":0}");
        string uuidText = Codec.ReadString(uuidPayload);
        string syncText = Codec.ReadString(syncPayload);
        ok &= Check(log, "id:68 UUID 帧 payload = 37B", uuidPayload.Length == 37, uuidPayload.Length);
        ok &= Check(log, "id:68 同步串帧 payload = 42B", syncPayload.Length == 42, syncPayload.Length);
        ok &= Check(log, "id:68 两帧分流正确",
            !ModSyncCheck.IsSyncPayload(uuidText) && ModSyncCheck.IsSyncPayload(syncText), syncText);

        var emptyConfig = new BunnyLoader.Server.ServerConfig();
        bool modsMatch = ModSyncCheck.TryEvaluate(syncText, emptyConfig,
            out string clientMods, out string serverMods);
        ok &= Check(log, "mods 空清单对空清单 match = true",
            modsMatch && clientMods == "" && serverMods == "", $"{clientMods}|{serverMods}");

        return ok;
    }

    private static bool Check(JsonLogger log, string name, bool passed, object? value)
    {
        if (passed)
        {
            log.Info("codec.selftest.pass", ("check", name));
            return true;
        }

        log.Error("codec.selftest.fail", ("check", name), ("value", value?.ToString() ?? "null"));
        return false;
    }
}
