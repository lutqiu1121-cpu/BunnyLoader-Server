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
