using System.Net.Sockets;
using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server;

/// <summary>
/// 单个连接的帧循环与会话逻辑（R2 范围：握手 + 世界头，方块数据留到 R3）。
/// 未识别的消息按长度安全跳过——因为帧自带长度，跳过是可靠的。
/// </summary>
internal sealed class Session
{
    private const byte PlayerSlot = 1;

    /// <summary>
    /// MSG3_CANDIDATES=auto 时按连接轮换的内置候选。
    /// 先试「槽位 0」（三次自动轮换都用了槽位 1，从未测过 0），
    /// 再试更长的负载（假设移动端消息 3 不止 1~2 字节）。
    /// </summary>
    private static readonly string[] DefaultCandidates =
    {
        // 第一组：超长负载。若崩溃原因是「客户端读得比我们写得多」，补零就能救。
        "00000000000000000000000000000000",                        // 槽位 0 + 15 零字节（16B）
        "01000000000000000000000000000000",                        // 槽位 1 + 15 零字节（16B）

        // 第二组：槽位语义。三次自动轮换都用了槽位 1，槽位 0 从未测过。
        "0000",                                                    // 槽位 0 + bool 0
        "00",                                                      // 槽位 0，无 flag
        "0001",                                                    // 槽位 0 + bool 1

        // 第三组：中等长度，用于在第二组命中后二分定位真正的字段数。
        "00000000",                                                // 4B，槽位 0
        "01000000",                                                // 4B，槽位 1
        "0100000000000000",                                        // 8B，槽位 1
    };

    private readonly TcpClient _client;
    private readonly string _id;
    private readonly int _sequence;
    private readonly ServerConfig _config;
    private readonly JsonLogger _log;
    private readonly CaptureWriter? _capture;
    private bool _suppressOutbound;

    public Session(TcpClient client, string id, int sequence, ServerConfig config, JsonLogger log, CaptureWriter? capture)
    {
        _client = client;
        _id = id;
        _sequence = sequence;
        _config = config;
        _log = log;
        _capture = capture;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string peer = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        _client.NoDelay = true;

        long total = 0;
        var inbox = new List<byte>(16384);
        var buffer = new byte[8192];

        _log.Info("conn.open", ("conn", _id), ("peer", peer));

        try
        {
            using NetworkStream stream = _client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    _log.Info("conn.close", ("conn", _id), ("bytes", total), ("reason", "eof"));
                    return;
                }

                total += read;
                _capture?.FromClient(buffer.AsSpan(0, read));
                for (int i = 0; i < read; i++)
                    inbox.Add(buffer[i]);

                int offset = 0;
                while (inbox.Count - offset >= 3)
                {
                    int frameLength = inbox[offset] | (inbox[offset + 1] << 8);
                    if (frameLength < 3)
                    {
                        _log.Error("frame.invalid", ("conn", _id), ("length", frameLength));
                        return;
                    }

                    if (inbox.Count - offset < frameLength)
                        break;

                    byte id = inbox[offset + 2];
                    byte[] payload = inbox.GetRange(offset + 3, frameLength - 3).ToArray();
                    offset += frameLength;

                    await DispatchAsync(stream, id, payload, cancellationToken);
                }

                if (offset > 0)
                    inbox.RemoveRange(0, offset);
            }

            _log.Info("conn.close", ("conn", _id), ("bytes", total), ("reason", "shutdown"));
        }
        catch (OperationCanceledException)
        {
            _log.Info("conn.close", ("conn", _id), ("bytes", total), ("reason", "shutdown"));
        }
        catch (IOException e)
        {
            _log.Info("conn.close", ("conn", _id), ("bytes", total), ("reason", "io"), ("error", e.Message));
        }
        catch (Exception e)
        {
            _log.Error("conn.error", ("conn", _id), ("type", e.GetType().Name), ("message", e.Message));
        }
        finally
        {
            _client.Dispose();
        }
    }

    private async Task DispatchAsync(NetworkStream stream, byte id, byte[] payload, CancellationToken cancellationToken)
    {
        _log.Info("frame.recv",
            ("conn", _id),
            ("id", id),
            ("name", Messages.Name(id)),
            ("len", payload.Length),
            ("hex", Codec.Hex(payload, 24)));

        switch (id)
        {
            case MessageId.Hello:
            {
                string version = TryReadString(payload) ?? "";
                (string layout, bool specialFlag) = PickMsg3Variant();
                string label = layout == "slot" ? "slot" : (specialFlag ? "flags+bool1" : "flags+bool0");

                _log.Info("hello",
                    ("conn", _id),
                    ("version", version),
                    ("is326", version == "Terraria326"),
                    ("msg3", label),
                    ("connection", _sequence));

                if (_config.Handshake.SkipApproved)
                {
                    _suppressOutbound = true;
                    _log.Warn("approved.suppressed",
                        ("conn", _id),
                        ("note", "SKIP_APPROVED=true：连消息 3 都不发（控制组）"));
                    break;
                }

                byte[] approved = BuildMsg3(layout, specialFlag);
                await SendAsync(stream, approved, cancellationToken);

                if (_config.Handshake.SkipWelcomePack)
                {
                    _suppressOutbound = true;
                    _log.Warn("outbound.suppressed",
                        ("conn", _id),
                        ("note", "SKIP_WELCOME_PACK=true：消息 3 之后不再发送任何包（含 82 与 7）"));
                    break;
                }

                // BL 客户端在等待「欢迎包」；发完消息 3 立刻补一条。
                // 注意：客户端发送消息 6 时会把 welcomed 复位（ModNet.cpp:179），
                // 所以 68 分支里还会再发一次——少了那次，7 到达时会被自踢。
                await SendWelcomeAsync(stream, cancellationToken);
                break;
            }

            case MessageId.ClientUuid:
            {
                string text = TryReadString(payload) ?? "";
                if (text.StartsWith(Codec.HelloPrefix, StringComparison.Ordinal))
                {
                    string json = text[Codec.HelloPrefix.Length..];
                    _log.Info("bl.hello", ("conn", _id), ("json", json));
                    await SendWelcomeAsync(stream, cancellationToken);
                }
                else
                {
                    _log.Info("uuid", ("conn", _id), ("uuid", text));
                }
                break;
            }

            case MessageId.RequestWorldData:
            {
                WorldData world = BuildWorld();
                await SendAsync(stream, Messages.WorldDataPacket(world), cancellationToken);
                _log.Info("world.sent",
                    ("conn", _id),
                    ("name", world.WorldName),
                    ("size", $"{world.MaxTilesX}x{world.MaxTilesY}"),
                    ("spawn", $"{world.SpawnTileX},{world.SpawnTileY}"));
                break;
            }

            case MessageId.SpawnTileData:
                // R3 才发 tile section；这里只记录客户端请求的坐标
                _log.Info("spawn.request", ("conn", _id), ("len", payload.Length), ("hex", Codec.Hex(payload, 16)));
                break;

            case MessageId.NetModules:
                ParseNetModule(payload);
                break;

            case MessageId.Kick:
            {
                string? reason = NetText.Describe(payload);
                _log.Warn("kick.recv", ("conn", _id), ("reason", reason ?? "?"));
                break;
            }

            default:
                break;
        }
    }

    private void ParseNetModule(byte[] payload)
    {
        if (payload.Length < 3)
        {
            _log.Warn("netmodule.short", ("conn", _id), ("len", payload.Length));
            return;
        }

        ushort module = (ushort)(payload[0] | (payload[1] << 8));
        byte kind = payload[2];
        string what = module != Codec.ModuleId
            ? "other-module"
            : kind switch
            {
                Codec.KindWelcome => "welcome",
                Codec.KindSyncPlan => "sync-plan",
                _ => "bl-custom",
            };

        _log.Info("netmodule", ("conn", _id), ("module", $"0x{module:x4}"), ("kind", kind), ("what", what), ("len", payload.Length));
    }

    private async Task SendAsync(NetworkStream stream, byte[] frame, CancellationToken cancellationToken)
    {
        if (_suppressOutbound)
        {
            _log.Warn("frame.send.suppressed",
                ("conn", _id),
                ("id", frame[2]),
                ("name", Messages.Name(frame[2])),
                ("len", frame.Length));
            return;
        }

        await stream.WriteAsync(frame.AsMemory(), cancellationToken);
        _capture?.ToClient(frame);
        _log.Info("frame.send",
            ("conn", _id),
            ("id", frame[2]),
            ("name", Messages.Name(frame[2])),
            ("len", frame.Length),
            ("hex", Codec.Hex(frame.AsSpan(3), 24)));
    }

    /// <summary>
    /// 选本次连接用哪种消息 3。
    /// Auto 时按连接序号轮换，一次现场测试就能把三种组合试完。
    /// </summary>
    private (string Layout, bool SpecialFlag) PickMsg3Variant()
    {
        HandshakeSection handshake = _config.Handshake;
        if (!handshake.Auto)
            return (handshake.Layout, handshake.SpecialFlag);

        int index = (_sequence - 1) % 3;
        return index switch
        {
            1 => ("slot", false),
            2 => ("flags", false),
            _ => ("flags", true),
        };
    }

    private async Task SendWelcomeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte rules = (byte)(_config.Rules.AllowCheats ? 1 : 0);
        string hex = _config.Handshake.WelcomeHex;

        byte[] frame = string.IsNullOrWhiteSpace(hex)
            ? Messages.WelcomePacket(rules)
            : Codec.Frame(MessageId.NetModules, ParseHex(hex));

        await SendAsync(stream, frame, cancellationToken);
        _log.Info("welcome.sent",
            ("conn", _id),
            ("rules", rules),
            ("payload", Codec.Hex(frame.AsSpan(3), 16)));
    }

    /// <summary>消息 3 的负载：MSG3_HEX 优先，否则按 layout/specialFlag 生成。</summary>
    private byte[] BuildMsg3(string layout, bool specialFlag)
    {
        string candidates = _config.Handshake.Candidates;
        if (!string.IsNullOrWhiteSpace(candidates))
        {
            string[] list = candidates == "auto"
                ? DefaultCandidates
                : candidates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (list.Length > 0)
            {
                int index = (_sequence - 1) % list.Length;
                byte[] raw = ParseHex(list[index]);
                _log.Info("msg3.candidate",
                    ("conn", _id),
                    ("index", index + 1),
                    ("of", list.Length),
                    ("payload", Codec.Hex(raw, 16)));
                return Codec.Frame(MessageId.ConnectionApproved, raw);
            }
        }

        string hex = _config.Handshake.Msg3Hex;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            byte[] raw = ParseHex(hex);
            _log.Info("msg3.custom", ("conn", _id), ("payload", Codec.Hex(raw, 16)));
            return Codec.Frame(MessageId.ConnectionApproved, raw);
        }

        return layout == "slot"
            ? Messages.ConnectionApprovedSlotOnlyPacket(PlayerSlot)
            : Messages.ConnectionApprovedPacket(PlayerSlot, specialFlag);
    }

    private static byte[] ParseHex(string text) =>
        Convert.FromHexString(text.Replace(" ", "").Replace("-", ""));

    private WorldData BuildWorld()
    {
        WorldSection settings = _config.World;
        var world = new WorldData
        {
            Release326 = settings.Release326,
            WorldName = settings.Name,
            MaxTilesX = settings.MaxTilesX,
            MaxTilesY = settings.MaxTilesY,
            SpawnTileX = settings.SpawnTileX,
            SpawnTileY = settings.SpawnTileY,
            WorldSurface = settings.WorldSurface,
            RockLayer = settings.RockLayer,
            DungeonX = settings.DungeonX,
            DungeonY = settings.DungeonY,
            GameMode = settings.GameMode,
            WorldId = settings.Seed,
            Time = 0,
            DayTime = true,
            Guid = DeterministicGuid(settings.Seed),
        };

        if (settings.Crimson)
            world.WorldFlags[1] |= 0x20;

        return world;
    }

    /// <summary>同一个 seed 每次得到同一个世界 ID，避免客户端把重连当成另一个世界。</summary>
    private static byte[] DeterministicGuid(int seed)
    {
        var guid = new byte[16];
        uint state = unchecked((uint)seed * 2654435761u + 1u);
        for (int i = 0; i < 16; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            guid[i] = (byte)(state & 0xFF);
        }
        return guid;
    }

    private static string? TryReadString(byte[] payload)
    {
        try
        {
            return Codec.ReadString(payload);
        }
        catch
        {
            return null;
        }
    }
}
