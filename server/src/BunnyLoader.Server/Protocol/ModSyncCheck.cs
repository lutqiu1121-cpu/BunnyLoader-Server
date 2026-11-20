using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BunnyLoader.Server.Protocol;

/// <summary>
/// R9 模组清单校验：客户端进服时把「我装了哪些模组」塞进第二帧 id:68。
///
/// 真机证据（2026-11-20 抓包）：
///   帧1 id:68 = "f4ea4156-ddcc-4be1-baf9-66ba6141c849"（标准 UUID）
///   帧2 id:68 = "bunnyloader-sync:{\"mods\":[],\"textures\":0}"（BL 二开私有同步串）
///
/// 注意：Codec.HelloPrefix 是 "bunnyloader-cs:"（模组介绍，走 SendWelcomeAsync），
/// 与本前缀不是同一个东西，千万不要复用、也不要触发欢迎包时序。
///
/// 当前只做「分流 + 解析 + 比对 + 日志」，不匹配仅告警不拒绝，不改握手流程。
/// 本次客户端 mods 为空数组，「装了模组」的字段形态尚无证据，不脑补结构。
/// </summary>
public static class ModSyncCheck
{
    public const string SyncPrefix = "bunnyloader-sync:";

    public static bool IsSyncPayload(string text)
        => text.StartsWith(SyncPrefix, StringComparison.Ordinal);

    /// <summary>返回 uid@version 逐位保序比对结果；解析失败视为 false，绝不抛给帧循环。</summary>
    public static bool TryEvaluate(string text, BunnyLoader.Server.ServerConfig config,
        out string clientMods, out string serverMods)
    {
        var expected = config.Mods.Select(m => m.Uid + "@" + m.Version).ToList();
        serverMods = string.Join(",", expected);
        clientMods = "";

        try
        {
            string json = text.Substring(SyncPrefix.Length);
            using var doc = JsonDocument.Parse(json);
            var actual = new List<string>();

            if (doc.RootElement.TryGetProperty("mods", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object)
                        continue;
                    string uid = m.TryGetProperty("uid", out var u) ? (u.GetString() ?? "") : "";
                    string ver = m.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
                    actual.Add(uid + "@" + ver);
                }
            }

            clientMods = string.Join(",", actual);
            return actual.Count == expected.Count && actual.SequenceEqual(expected);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
