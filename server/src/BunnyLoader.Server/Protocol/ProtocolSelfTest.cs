using System.Text.Json;

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

        // 人类实测日志里的前 24 字节（其余补零到 40）。它只有一个 4 字节 FF 段。
        byte[] sample = new byte[40];
        Convert.FromHexString("010702d0cc4c3ec40343757000000000ffffffffbba9695a").CopyTo(sample, 0);

        SyncPlayer4.Report report = SyncPlayer4.Analyze(sample);

        ok &= Check(log, "id:4 完整 hex 不截断（40 字节 = 40 个空格分隔 token）",
            report.HexSpaced.Split(' ').Length == 40, report.HexSpaced.Split(' ').Length);
        ok &= Check(log, "id:4 len = 40", report.Length == 40, report.Length);
        ok &= Check(log, "id:4 playerId = 0x01", report.PlayerId == 0x01, report.PlayerId);
        ok &= Check(log, "id:4 skinVariant = 0x07", report.SkinVariant == 0x07, report.SkinVariant);
        ok &= Check(log, "id:4 hair = 0x02", report.Hair == 0x02, report.Hair);

        // 标准布局必须被判定为「走不通」——0xd0 cc 4c 起始的 7bit 长度解出 1254992，远超剩余字节。
        ok &= Check(log, "id:4 标准 name 布局判为不成立", !report.StandardLayoutOk, report.StandardLayoutOk);
        ok &= Check(log, "id:4 name 长度前缀解出 1254992", report.NameLengthClaimed == 1254992, report.NameLengthClaimed);
        ok &= Check(log, "id:4 断点原因非空", report.Derail.Length > 8, report.Derail);

        // 偏移 8 = 0x03 + "Cup"：这是实测样本里唯一像名字的地方，必须被列出来。
        ok &= Check(log, "id:4 名字候选包含 8:len=3:Cup", report.NameCandidates.Contains("8:len=3:\"Cup\""), report.NameCandidates);

        // 16-19 是全 FF，但没有连续 8 个 FF —— 这正是上一轮 coordOffset=-1 的原因。
        ok &= Check(log, "id:4 没有连续 8 字节 FF（upptr 断言的其实是颜色区）", report.NanPairOffset == -1, report.NanPairOffset);
        ok &= Check(log, "id:4 识别到 16 处的 ff ff ff ff",
            SyncPlayer4.FirstFullByteRun(sample, 0xFF, 4) == 16,
            SyncPlayer4.FirstFullByteRun(sample, 0xFF, 4));

        // 标准布局所需最小长度 > 40：说明这个客户端的 SyncPlayer 不是标准全量布局。
        ok &= Check(log, "id:4 标准布局最小长度 > 40", report.MinStd > 40, report.MinStd);

        // ---- 假设枚举：这是本轮「找出 ff ff ff ff 是谁」的核心工具 ----
        // 实测样本里名字唯一可能在偏移 8（0x03 + "Cup"），枚举必须自己找到它并给出 rest=28。
        ok &= Check(log, "id:4 假设枚举给出 rest=28", report.Hypotheses.Contains("rest=28"), report.Hypotheses);
        ok &= Check(log, "id:4 假设枚举找到 name@8", report.Hypotheses.Contains("name@8"), report.Hypotheses);

        // 反过来验证枚举器本身没写错：按标准 1.4.5.8 布局造一个 51 字节的包，必须被精确命中，
        // 且 map 里的 hairDye / 第一个发色要逐字节对上（对不上就说明枚举器在瞎报）。
        byte[] standard = new byte[51];
        int o = 0;
        standard[o++] = 0x01;
        standard[o++] = 0x07;
        standard[o++] = 0x02;
        standard[o++] = 0x03;
        standard[o++] = (byte)'C';
        standard[o++] = (byte)'u';
        standard[o++] = (byte)'p';
        standard[o++] = 0x05;               // hairDye
        standard[o++] = 0x12;               // hairColor[0] = #123456
        standard[o++] = 0x34;
        standard[o++] = 0x56;
        o += 6 * 3;                         // 其余 6 个发色
        o += 6 * 3;                         // 6 个身体色
        standard[o++] = 0x00;               // difficulty
        standard[o++] = 0x0a;               // hideVisuals
        standard[o++] = 0x00;               // hideVisuals2
        standard[o++] = 0x40;               // hideMisc

        ok &= Check(log, "标准布局样本构造为 51 字节", o == standard.Length, $"{o}/{standard.Length}");

        SyncPlayer4.Report std = SyncPlayer4.Analyze(standard);
        ok &= Check(log, "标准布局 51 字节被枚举精确命中（13c3B）",
            std.Hypotheses.Contains("13c3B"), std.Hypotheses);
        ok &= Check(log, "标准布局 map: hairDye@7=0x05", std.Hypotheses.Contains("hairDye@7=0x05"), std.Hypotheses);
        ok &= Check(log, "标准布局 map: c0@8=#123456", std.Hypotheses.Contains("c0@8=#123456"), std.Hypotheses);

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

        // id:12 的负载固定 15 字节（u8 + i16 + i16 + i32 + i16 + i16 + u8 + u8），整帧 18。
        byte[] spawn = Messages.PlayerSpawnPacket(1, 2105, 312);
        ok &= Check(log, "id:12 负载 15 字节 / 整帧 18 字节", spawn.Length == 18 && spawn.Length - 3 == 15, spawn.Length);

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
