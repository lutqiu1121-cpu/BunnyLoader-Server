using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Net.Sockets;
using BunnyLoader.Server.Protocol;
using BunnyLoader.Server.World;

namespace BunnyLoader.Server;

/// <summary>
/// 单个连接的帧循环与会话逻辑（R2 范围：握手 + 世界头，方块数据留到 R3）。
/// 未识别的消息按长度安全跳过——因为帧自带长度，跳过是可靠的。
/// </summary>
internal sealed class Session : ISessionTicker
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
    private readonly WldWorld? _wld;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ChunkManager _chunks = new();
    private readonly PlayerState _player;

    private NetworkStream? _stream;
    private TickEngine? _tick;
    private bool _worldReady;
    private long _lastSendMs;
    private long _lastWindowCheckMs;
    private int _controlsReceived;
    private bool _spawnSent;
    private byte[]? _lastHealth;
    private byte[]? _lastMana;
    private int _chunksNoneCount;
    private int _chunksNoneX = int.MinValue;
    private int _chunksNoneY = int.MinValue;
    private bool _suppressOutbound;
    private bool _spawnCorrected;
    private bool _proofDone;
    private bool _startPlayingSent;
    private bool _finishedSent;
    private int _lastSectionX = int.MinValue;
    private int _lastSectionY = int.MinValue;
    private DateTime _lastSectionAt = DateTime.MinValue;

    public Session(TcpClient client, string id, int sequence, ServerConfig config, JsonLogger log,
                   CaptureWriter? capture, WldWorld? worldFile)
    {
        _client = client;
        _id = id;
        _sequence = sequence;
        _config = config;
        _log = log;
        _capture = capture;

        _wld = worldFile;

        // 注意：这里不能用 WorldSpawnX 初始化——_wld 刚赋值，SafeSpawn 要延迟到真正需要时再算。
        _player = new PlayerState { Slot = 0 };
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
            _stream = stream;

            // 纯文本 proof 握手（二开版 "protocol 1"）。必须在帧协议之前。
            await ProveAsync(stream, inbox, cancellationToken);

            _tick = new TickEngine(this, _log);
            _tick.Start();

            // proof 阶段可能已经把第一批帧一起读进来了，先处理掉。
            if (inbox.Count > 0)
                await ProcessFramesAsync(stream, inbox, cancellationToken);

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

                await ProcessFramesAsync(stream, inbox, cancellationToken);
            }

            _log.Info("conn.close", ("conn", _id), ("bytes", total), ("reason", "shutdown"));
        }
        catch (ProofRejectedException)
        {
            _log.Warn("proof.rejected", ("conn", _id), ("note", "验证前收到非回执帧或回执不符，断开"));
            _log.Info("conn.close", ("conn", _id), ("bytes", total), ("reason", "proof-rejected"));
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
            if (_tick is not null)
                await _tick.StopAsync();

            _client.Dispose();
        }
    }


    /// <summary>切出完整帧并分发；帧在分发前就从 inbox 移除，保证 inbox 始终是「下一帧的开头」。</summary>
    private async Task ProcessFramesAsync(NetworkStream stream, List<byte> inbox, CancellationToken cancellationToken)
    {
        while (inbox.Count >= 3)
        {
            int frameLength = inbox[0] | (inbox[1] << 8);
            if (frameLength < 3)
            {
                _log.Error("frame.invalid", ("conn", _id), ("length", frameLength));
                inbox.Clear();
                return;
            }

            if (inbox.Count < frameLength)
                break;

            byte id = inbox[2];
            byte[] payload = inbox.GetRange(3, frameLength - 3).ToArray();
            inbox.RemoveRange(0, frameLength);

            await DispatchAsync(stream, id, payload, inbox, cancellationToken);
        }
    }

    /// <summary>文档规定的 26 字节证明：1A 00 | 52 | 47 4E | "NingLauncher/1/server"。</summary>
    private static readonly byte[] ProofServer26 = BuildProof26();

    private static byte[] BuildProof26()
    {
        byte[] text = Encoding.ASCII.GetBytes("NingLauncher/1/server");
        byte[] frame = new byte[2 + 1 + 2 + text.Length];   // 26
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), (ushort)frame.Length);
        frame[2] = MessageId.NetModules;   // 0x52
        frame[3] = 0x47;                    // 'G'
        frame[4] = 0x4E;                    // 'N'
        text.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>客户端回执的负载（不含长度头）：52 47 4E + "NingLauncher/1/client"。</summary>
    private static readonly byte[] ProofClient24 = BuildProofClient();

    private static byte[] BuildProofClient()
    {
        byte[] text = Encoding.ASCII.GetBytes("NingLauncher/1/client");
        byte[] payload = new byte[3 + text.Length];        // 24
        payload[0] = MessageId.NetModules;
        payload[1] = 0x47;
        payload[2] = 0x4E;
        text.CopyTo(payload, 3);
        return payload;
    }

    /// <summary>
    /// 阶段一：连上立刻发 26 字节证明，然后绝对静默，只等回执。
    /// 回执匹配 → 阶段二；其它任何帧 → 断开。
    /// </summary>
    private async Task ProveAsync(NetworkStream stream, List<byte> inbox, CancellationToken cancellationToken)
    {
        if (_config.Proof.Mode == "off")
        {
            _log.Info("proof.skipped", ("conn", _id), ("reason", "mode=off"));
            return;
        }

        await stream.WriteAsync(ProofServer26, cancellationToken);
        _capture?.ToClient(ProofServer26);

        _log.Info("proof.sent.26bytes",
            ("conn", _id),
            ("bytes", ProofServer26.Length),
            ("hex", Codec.Hex(ProofServer26, 32)));

        DateTime end = DateTime.UtcNow.AddSeconds(_config.Proof.TimeoutSeconds);

        while (DateTime.UtcNow < end)
        {
            ProofState state = CheckProofStrict(inbox);
            if (state == ProofState.Verified)
            {
                _log.Info("proof.verified", ("conn", _id), ("note", "回执匹配成功"));
                await SendServerInfoAsync(stream, cancellationToken);
                return;
            }

            if (state == ProofState.Rejected)
                throw new ProofRejectedException();

            await ReadIntoAsync(stream, inbox, cancellationToken, 200);
        }

        _log.Warn("proof.timeout", ("conn", _id), ("seconds", _config.Proof.TimeoutSeconds));
        throw new ProofRejectedException();
    }

    private enum ProofState { Pending, Verified, Rejected }

    /// <summary>严格比对回执；任何不是回执的帧都判拒绝。</summary>
    private ProofState CheckProofStrict(List<byte> inbox)
    {
        if (inbox.Count < 3)
            return ProofState.Pending;

        // 回执帧：[u16 长度][52 47 4e + "NingLauncher/1/client"]
        int length = inbox[0] | (inbox[1] << 8);

        if (inbox[2] != MessageId.NetModules)
        {
            _log.Warn("proof.rejected-other-frame",
                ("conn", _id),
                ("id", inbox[2]),
                ("name", Messages.Name(inbox[2])));
            return ProofState.Rejected;
        }

        if (length < 5 || length > 512)
            return ProofState.Rejected;

        if (inbox.Count < length)
            return ProofState.Pending;

        for (int i = 0; i < ProofClient24.Length; i++)
        {
            if (inbox[2 + i] != ProofClient24[i])
            {
                _log.Warn("proof.bad-receipt", ("conn", _id),
                    ("got", Codec.Hex(inbox.GetRange(2, Math.Min(24, inbox.Count - 2)).ToArray(), 24)));
                return ProofState.Rejected;
            }
        }

        inbox.RemoveRange(0, length);
        return ProofState.Verified;
    }

    /// <summary>
    /// 阶段二：发移动版 ServerInfo（消息 163）。
    /// 字段顺序（按用户给的规范）：byte 状态版本 | string 服务器名 | string 状态 | byte 最大人数 | byte 当前人数
    /// 字符串用 7bit 长度前缀（LEB128）。
    /// </summary>
    private async Task SendServerInfoAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        if (_config.Proof.Disable163)
        {
            _log.Info("serverinfo.163.disabled", ("conn", _id), ("note", "DISABLE_163=true，跳过"));
            return;
        }

        byte[] payload;
        string hex = _config.Proof.ServerInfo163Hex;

        if (!string.IsNullOrWhiteSpace(hex))
        {
            payload = Convert.FromHexString(hex.Replace(" ", "").Replace("-", ""));
        }
        else
        {
            using var ms = new MemoryStream(64);
            ms.WriteByte(0);                                   // 状态版本
            Write7BitString(ms, _config.Proof.ServerName);      // 服务器名
            Write7BitString(ms, _config.Proof.ServerStatus);    // 状态文本
            ms.WriteByte(16);                                  // 最大人数
            ms.WriteByte(1);                                   // 当前人数
            payload = ms.ToArray();
        }

        byte[] frame = Codec.Frame(163, payload);
        await SendAsync(stream, frame, cancellationToken);

        _log.Info("serverinfo.163.sent",
            ("conn", _id),
            ("bytes", frame.Length),
            ("payloadLen", payload.Length),
            ("hex", Codec.Hex(frame, 40)));
    }

    private static void Write7BitString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int length = bytes.Length;

        do
        {
            byte b = (byte)(length & 0x7F);
            length >>= 7;
            if (length > 0) b |= 0x80;
            stream.WriteByte(b);
        }
        while (length > 0);

        stream.Write(bytes, 0, bytes.Length);
    }

    private sealed class ProofRejectedException : Exception { }

    private async Task ReadIntoAsync(NetworkStream stream, List<byte> inbox, CancellationToken outer, int milliseconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(milliseconds);

        var buffer = new byte[8192];
        try
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
            for (int i = 0; i < read; i++)
                inbox.Add(buffer[i]);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
        }
    }

    /// <summary>是不是一条泰拉瑞亚帧的开头（长度合理且 id 是 Hello）。</summary>
    private static bool LooksLikeFrame(List<byte> inbox)
    {
        if (inbox.Count < 3)
            return false;

        int length = inbox[0] | (inbox[1] << 8);
        return length >= 3 && length <= 4096 && inbox[2] == MessageId.Hello;
    }

    private async Task DispatchAsync(NetworkStream stream, byte id, byte[] payload, List<byte> inbox, CancellationToken cancellationToken)
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

                if (_config.Proof.Msg3DelayMs > 0)
                {
                    _log.Info("approved.delay", ("conn", _id), ("ms", _config.Proof.Msg3DelayMs));
                    await Task.Delay(_config.Proof.Msg3DelayMs, cancellationToken);
                }

                (string layout, bool specialFlag) = PickMsg3Variant();
                string label = layout == "slot" ? "slot" : (specialFlag ? "flags+bool1" : "flags+bool0");

                _log.Info("hello.received",
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

                if (_config.Handshake.SkipWelcomePack)
                {
                    await SendAsync(stream, approved, cancellationToken);
                    _suppressOutbound = true;
                    _log.Warn("outbound.suppressed",
                        ("conn", _id),
                        ("note", "SKIP_WELCOME_PACK=true：消息 3 之后不再发送任何包（含 82 与 7）"));
                    break;
                }

                var bundle = new List<byte[]> { approved };

                if (_config.Handshake.SendMsg7After3)
                {
                    // 假设：客户端处理完消息 3 立刻需要世界头。
                    // ⚠ 若不带欢迎包就发 7，BL 客户端会主动自踢并显示
                    //   "只能与柠版互通"（ModNet.cpp:249-253），那会被误当成崩溃。
                    //   所以默认先补欢迎包。
                    if (_config.Handshake.Msg7IncludeWelcome)
                        bundle.Add(BuildWelcomeFrame());

                    bundle.Add(Messages.WorldDataPacket(BuildWorld()));

                    _log.Info("bundle.msg3_msg7",
                        ("conn", _id),
                        ("frames", bundle.Count),
                        ("withWelcome", _config.Handshake.Msg7IncludeWelcome),
                        ("note", "消息 3 与 7 在同一次 write 里发出"));
                }
                else if (_config.Handshake.WelcomeAfter3)
                {
                    bundle.Add(BuildWelcomeFrame());
                }
                else
                {
                    // 默认：3 之后严格静默，等客户端发 68 再补欢迎包。
                    // 否则 welcomed=false 时收到 7 会自踢（ModNet.cpp:249-253）。
                    _log.Info("approved.silent",
                        ("conn", _id),
                        ("note", "3 之后静默，等 68 再发欢迎包"));
                }

                await SendBundleAsync(stream, bundle, cancellationToken);

                _log.Info("approved.sent",
                    ("conn", _id),
                    ("msg3", label),
                    ("payload", Codec.Hex(bundle[0], 8)),
                    ("note", "回复 id:3"));

                await SendWorldOnHelloAsync(stream, cancellationToken);
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
                _log.Info("world.sent",
                    ("conn", _id),
                    ("name", world.WorldName),
                    ("size", $"{world.MaxTilesX}x{world.MaxTilesY}"),
                    ("spawn", $"{world.SpawnTileX},{world.SpawnTileY}"));

                _worldReady = true;

                await SendBundleAsync(stream, new List<byte[]>
                {
                    Messages.WorldDataPacket(world),
                    Messages.StatusTextPacket(0, "Bunny Loader", 0),
                }, cancellationToken);

                await SendFullAreaAsync(stream, WorldSpawnX, WorldSpawnY, cancellationToken);

                // 40 块发完之后，才发 49，再发 12
                await EnsureStartPlayingAsync(stream, cancellationToken);

                if (!_spawnSent)
                {
                    _spawnSent = true;
                    await SendAsync(stream,
                        Messages.PlayerSpawnPacket(PlayerSlot, (short)WorldSpawnX, (short)WorldSpawnY),
                        cancellationToken);

                    _log.Info("spawn.proactive",
                        ("conn", _id),
                        ("x", WorldSpawnX),
                        ("y", WorldSpawnY),
                        ("note", "分块发完后发 id:12"));
                }
                break;
            }

            case MessageId.SpawnTileData:
            {
                int reqX = 0, reqY = 0;
                if (payload.Length >= 8)
                {
                    reqX = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
                    reqY = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
                }

                int spawnX = WorldSpawnX;
                int spawnY = WorldSpawnY;
                bool outOfRange = reqX < 0 || reqY < 0
                    || reqX >= WorldMaxX || reqY >= WorldMaxY;

                if (outOfRange)
                {
                    _log.Warn("spawn.fallback", ("conn", _id), ("requested", $"{reqX},{reqY}"), ("using", $"{spawnX},{spawnY}"));
                    reqX = spawnX;
                    reqY = spawnY;

                    // 客户端还在 (-1,-1)：整张初始区域再砸一遍
                    await SendFullAreaAsync(stream, spawnX, spawnY, cancellationToken, force: true);
                }

                _log.Info("spawn.request", ("conn", _id), ("x", reqX), ("y", reqY));

                _player.TileX = reqX;
                _player.TileY = reqY;
                _player.LastSeenMs = (long)_clock.Elapsed.TotalMilliseconds;
                _worldReady = true;

                await SendWindowAsync(stream, reqX, reqY, cancellationToken);
                await EnsureStartPlayingAsync(stream, cancellationToken);
                break;
            }

            case MessageId.PlayerSpawn:
            {
                short px = -1, py = -1;
                if (payload.Length >= 5)
                {
                    px = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(1, 2));
                    py = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(3, 2));
                }

                bool invalid = px < 0 || py < 0
                    || px >= WorldMaxX || py >= WorldMaxY;

                _log.Info("player.spawn", ("conn", _id), ("x", px), ("y", py), ("valid", !invalid));

                // 只在坐标无效时纠正一次；否则会和客户端形成 12 <-> 12 的乒乓循环。
                if (invalid && !_spawnCorrected)
                {
                    _spawnCorrected = true;
                    await SendBundleAsync(stream, new List<byte[]>
                    {
                        Messages.PlayerSpawnPacket(PlayerSlot, (short)WorldSpawnX, (short)WorldSpawnY),
                        Messages.FinishedConnectingPacket(),
                    }, cancellationToken);
                    _log.Info("spawn.corrected",
                        ("conn", _id),
                        ("x", WorldSpawnX),
                        ("y", WorldSpawnY));
                }
                else if (!invalid && !_finishedSent)
                {
                    _finishedSent = true;
                    await SendAsync(stream, Messages.FinishedConnectingPacket(), cancellationToken);

                    _log.Info("world.entered", ("conn", _id), ("x", px), ("y", py));
                }
                else
                {
                    _log.Info("player.spawn.ignored", ("conn", _id), ("note", "已有正确坐标，忽略"));
                }
                break;
            }

            case MessageId.PlayerHealth:
            {
                // 客户端上报的血量与上限：只记录 + 原样回显，绝不覆盖（生命水晶/死亡重连都要尊重）
                string hex = Codec.Hex(payload, 12);
                _lastHealth = payload;
                _log.Info("player.health", ("conn", _id), ("hex", hex));
                await SendAsync(stream, Codec.Frame(MessageId.PlayerHealth, payload), cancellationToken);
                break;
            }

            case MessageId.PlayerMana:
            {
                _lastMana = payload;
                _log.Info("player.mana", ("conn", _id), ("hex", Codec.Hex(payload, 12)));
                await SendAsync(stream, Codec.Frame(MessageId.PlayerMana, payload), cancellationToken);
                break;
            }

            case MessageId.SyncPlayer:
            {
                _log.Info("player.sync",
                    ("conn", _id),
                    ("len", payload.Length),
                    ("hex", Codec.Hex(payload, 24)));

                // 把 whoAmI 从 255 改写成分配到的槽位，再回显
                byte[] echo = (byte[])payload.Clone();
                byte original = echo.Length > 0 ? echo[0] : (byte)0xFF;
                if (echo.Length > 0)
                    echo[0] = PlayerSlot;

                await SendAsync(stream, Codec.Frame(MessageId.SyncPlayer, echo), cancellationToken);

                _log.Info("player.sync.echoed",
                    ("conn", _id),
                    ("whoAmI", $"0x{original:X2} -> 0x{PlayerSlot:X2}"));

                // 激活实体
                await SendAsync(stream, Codec.Frame(MessageId.PlayerActive, new[] { PlayerSlot, (byte)1 }), cancellationToken);
                _log.Info("player.active.sent", ("conn", _id), ("slot", PlayerSlot));

                // 之后闭嘴：世界数据、49、12 全部等 id:6
                break;
            }

            case MessageId.PlayerControls:
            {
                // 只更新内存里的状态，**不立刻回发**；由 TickEngine 统一节流广播。
                _player.LastControls = payload;
                _player.LastSeenMs = (long)_clock.Elapsed.TotalMilliseconds;

                if (payload.Length >= 9)
                {
                    _player.TileX = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(1, 2)));
                    _player.TileY = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(3, 2)));
                }

                _controlsReceived++;

                if (_controlsReceived % 60 == 1)
                {
                    _log.Info("controls.update",
                        ("conn", _id),
                        ("x", _player.TileX),
                        ("y", _player.TileY),
                        ("count", _controlsReceived));
                }
                break;
            }

            case MessageId.PlayerActive:
            {
                _player.Active = payload.Length >= 2 && payload[1] != 0;
                _player.LastSeenMs = (long)_clock.Elapsed.TotalMilliseconds;
                _log.Info("player.active", ("conn", _id), ("active", _player.Active));
                break;
            }

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

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frame.AsMemory(), cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }

        _lastSendMs = (long)_clock.Elapsed.TotalMilliseconds;
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
        // 证明模式下固定 flags+bool1（01 01），不轮换
        if (_config.Handshake.Msg3Fixed)
            return ("flags", true);

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

    private byte[] BuildWelcomeFrame()
    {
        byte rules = (byte)(_config.Rules.AllowCheats ? 1 : 0);
        string hex = _config.Handshake.WelcomeHex;

        return string.IsNullOrWhiteSpace(hex)
            ? Messages.WelcomePacket(rules)
            : Codec.Frame(MessageId.NetModules, ParseHex(hex));
    }

    private async Task SendWelcomeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        if (_config.Handshake.NoWelcome)
        {
            _log.Info("welcome.disabled", ("conn", _id), ("reason", "NO_WELCOME"));
            return;
        }

        byte[] frame = BuildWelcomeFrame();
        await SendAsync(stream, frame, cancellationToken);
        _log.Info("welcome.sent", ("conn", _id), ("payload", Codec.Hex(frame.AsSpan(3), 16)));
    }

    /// <summary>把多个帧拼进同一个缓冲区、一次 write 发出。</summary>
    private async Task SendBundleAsync(NetworkStream stream, List<byte[]> frames, CancellationToken cancellationToken)
    {
        int total = 0;
        foreach (byte[] frame in frames)
            total += frame.Length;

        byte[] buffer = new byte[total];
        int offset = 0;
        foreach (byte[] frame in frames)
        {
            frame.CopyTo(buffer, offset);
            offset += frame.Length;
        }

        if (_suppressOutbound)
        {
            _log.Warn("frame.send.suppressed", ("conn", _id), ("frames", frames.Count), ("len", total));
            return;
        }

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(buffer.AsMemory(), cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }

        _lastSendMs = (long)_clock.Elapsed.TotalMilliseconds;
        _capture?.ToClient(buffer);

        foreach (byte[] frame in frames)
        {
            _log.Info("frame.send",
                ("conn", _id),
                ("id", frame[2]),
                ("name", Messages.Name(frame[2])),
                ("len", frame.Length),
                ("hex", Codec.Hex(frame.AsSpan(3), 24)));
        }

        _log.Info("frame.bundle", ("conn", _id), ("frames", frames.Count), ("bytes", total), ("note", "同一次 write"));
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

    /// <summary>
    /// 平坦世界：地表以上空气，地表那一层草地，往下泥土，岩层以下石头。
    /// 只够给客户端画图与站人，不做地形生成。
    /// </summary>
    /// <summary>
    /// 初始加载：8×5 = 40 个分块一次发完（约 305KB）。
    /// force=true 时不看「已发送」集合，整片重发。
    /// </summary>
    private async Task SendFullAreaAsync(NetworkStream stream, int tileX, int tileY,
                                         CancellationToken cancellationToken, bool force = false)
    {
        var frames = new List<byte[]>();
        int skipped = 0;

        foreach ((int sx, int sy) in _chunks.FullAreaFor(tileX, tileY))
        {
            if (sx * ChunkManager.SectionWidth >= WorldMaxX || sy * ChunkManager.SectionHeight >= WorldMaxY)
                continue;

            if (!force && !_chunks.MarkSent(sx, sy))
            {
                skipped++;
                continue;
            }

            _chunks.MarkSent(sx, sy);
            frames.Add(Messages.TileSectionPacket(TileSection.Encode(sx, sy, GetTile, IsFrameImportant)));
        }

        if (frames.Count == 0)
        {
            _log.Warn("chunks.full-empty", ("conn", _id), ("x", tileX), ("y", tileY));
            return;
        }

        await SendBundleAsync(stream, frames, cancellationToken);

        _log.Info("chunks.full",
            ("conn", _id),
            ("x", tileX),
            ("y", tileY),
            ("sections", frames.Count),
            ("skipped", skipped),
            ("force", force),
            ("total", _chunks.SentCount));
    }

    /// <summary>发一次 id:49（开始游戏）；只发一次。</summary>
    private async Task EnsureStartPlayingAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        if (_startPlayingSent)
            return;

        _startPlayingSent = true;
        await SendAsync(stream, Messages.StartPlayingPacket(), cancellationToken);

        _log.Info("startplaying.sent",
            ("conn", _id),
            ("note", "客户端收到后才会解锁 UI 与摇杆"));
    }

    /// <summary>只发玩家窗口里「还没发过」的分块。</summary>
    private async Task SendWindowAsync(NetworkStream stream, int tileX, int tileY, CancellationToken cancellationToken)
    {
        var frames = new List<byte[]>();
        int skipped = 0;

        foreach ((int sx, int sy) in _chunks.WindowFor(tileX, tileY))
        {
            if (sx * ChunkManager.SectionWidth >= WorldMaxX || sy * ChunkManager.SectionHeight >= WorldMaxY)
                continue;

            if (!_chunks.MarkSent(sx, sy))
            {
                skipped++;
                continue;
            }

            frames.Add(Messages.TileSectionPacket(TileSection.Encode(sx, sy, GetTile, IsFrameImportant)));
        }

        if (frames.Count == 0)
        {
            _chunksNoneCount++;

            bool samePlace = tileX == _chunksNoneX && tileY == _chunksNoneY;
            if (!samePlace || _chunksNoneCount % 50 == 0)
            {
                _chunksNoneX = tileX;
                _chunksNoneY = tileY;
                _log.Info("chunks.none",
                    ("conn", _id),
                    ("x", tileX),
                    ("y", tileY),
                    ("sent", _chunks.SentCount),
                    ("count", _chunksNoneCount),
                    ("note", "窗口都已发送过，跳过"));
            }

            return;
        }

        await SendBundleAsync(stream, frames, cancellationToken);

        _log.Info("chunks.sent",
            ("conn", _id),
            ("x", tileX),
            ("y", tileY),
            ("new", frames.Count),
            ("skipped", skipped),
            ("total", _chunks.SentCount));
    }

    // ---------------- ISessionTicker ----------------

    /// <summary>20Hz：广播其他玩家的最新坐标。单人时什么都不发。</summary>
    public Task OnBroadcastTickAsync(CancellationToken cancellationToken)
    {
        // 单人：房间里没有「其他人」，按原版逻辑一个字节都不发。
        // 多人落地后：遍历 room.Others(_player.Slot)，逐个 SendAsync(13, 该玩家最新控制帧)。
        // 绝不回发给发送者本身——那会让客户端反复应用自己的输入。
        return Task.CompletedTask;
    }

    /// <summary>500ms：太久没发东西就补一个保活包（用最近一次的控制帧）。</summary>
    public async Task OnKeepAliveTickAsync(CancellationToken cancellationToken)
    {
        if (_suppressOutbound || _stream is null)
            return;

        long now = (long)_clock.Elapsed.TotalMilliseconds;
        if (now - _lastSendMs < TickEngine.KeepAliveMs - 20)
            return;

        // 用合法的状态包保活，绝不伪造 id:13。
        // 客户端上报过血量就回放它自己的血量；否则尝试世界时间。
        if (_lastHealth is not null)
        {
            await SendAsync(_stream, Codec.Frame(MessageId.PlayerHealth, _lastHealth), cancellationToken);
            _log.Info("keepalive.health", ("conn", _id), ("gapMs", now - _lastSendMs));
            return;
        }

        if (_lastMana is not null)
        {
            await SendAsync(_stream, Codec.Frame(MessageId.PlayerMana, _lastMana), cancellationToken);
            _log.Info("keepalive.mana", ("conn", _id), ("gapMs", now - _lastSendMs));
        }
    }

    /// <summary>60Hz：超时判定 + 区块窗口跟随 + 实体/世界钩子。</summary>
    public async Task OnGameTickAsync(double nowMs, CancellationToken cancellationToken)
    {
        long now = (long)nowMs;

        // 超时判定：30 秒没有任何上行就断开
        if (_player.LastSeenMs > 0 && now - _player.LastSeenMs > TickEngine.TimeoutMs)
        {
            _log.Warn("player.timeout", ("conn", _id), ("silentMs", now - _player.LastSeenMs));
            try { _client.Close(); } catch { }
            return;
        }

        // 预留钩子
        // _room.UpdateEntities(nowMs);
        // _world.Update(nowMs);

        if (!_worldReady || _stream is null || _suppressOutbound)
            return;

        // 客户端还没上报过任何位置时，不要以 (0,0) 为中心乱发
        if (_player.LastSeenMs <= 0)
            return;

        // 5Hz 检查一次窗口，避免每帧都算
        if (now - _lastWindowCheckMs < 200)
            return;

        _lastWindowCheckMs = now;
        await SendWindowAsync(_stream, _player.TileX, _player.TileY, cancellationToken);
    }

    private int? _safeSpawnX;
    private int? _safeSpawnY;

    /// <summary>
    /// 出生点必须是「上方是空气、下方是实心块」的那一格。
    /// 世界自带坐标可能悬空（会摔伤），所以从给定坐标附近向下找最近的地面。
    /// </summary>
    private (int X, int Y) SafeSpawn()
    {
        if (_safeSpawnX.HasValue)
            return (_safeSpawnX.Value, _safeSpawnY!.Value);

        int x = _wld?.SpawnTileX ?? _config.World.SpawnTileX;
        int y = _wld?.SpawnTileY ?? _config.World.SpawnTileY;

        int maxY = WorldMaxY;
        if (x < 0) x = 0;
        if (x >= WorldMaxX) x = WorldMaxX - 1;
        if (y < 0) y = 0;

        // 从出生点上方 60 格开始找，避免跑到屋顶或很远的地表
        int start = Math.Max(0, y - 60);
        int limit = Math.Min(maxY - 2, y + 220);

        for (int yy = start; yy < limit; yy++)
        {
            if (GetTile(x, yy).Active)
                continue;

            if (GetTile(x, yy + 1).Active)
            {
                _safeSpawnX = x;
                _safeSpawnY = yy;

                if (yy != y)
                {
                    _log.Info("spawn.grounded",
                        ("conn", _id),
                        ("from", $"{x},{y}"),
                        ("to", $"{x},{yy}"));
                }

                return (x, yy);
            }
        }

        _safeSpawnX = x;
        _safeSpawnY = Math.Min(y, maxY - 2);
        _log.Warn("spawn.ground-failed", ("conn", _id), ("using", $"{_safeSpawnX},{_safeSpawnY}"));
        return (_safeSpawnX.Value, _safeSpawnY.Value);
    }

    private bool IsFrameImportant(ushort type) => _wld?.IsFrameImportant(type) ?? false;

    private int WorldMaxX => _wld?.MaxTilesX ?? _config.World.MaxTilesX;
    private int WorldMaxY => _wld?.MaxTilesY ?? _config.World.MaxTilesY;
    private int WorldSpawnX => SafeSpawn().X;
    private int WorldSpawnY => SafeSpawn().Y;

    private Tile GetTile(int x, int y)
    {
        if (_wld is not null)
            return _wld.Get(x, y);

        if (x < 0 || y < 0 || x >= _config.World.MaxTilesX || y >= _config.World.MaxTilesY)
            return default;

        short surface = _config.World.WorldSurface;
        short rock = _config.World.RockLayer;

        if (y < surface)
            return default;                                        // 空气

        if (y == surface)
            return Tile.Solid(TileSection.TypeGrass);

        if (y < rock)
            return Tile.Solid(TileSection.TypeDirt);

        return Tile.Solid(TileSection.TypeStone);
    }

    /// <summary>
    /// 请求坐标周围的 8×5 分块（规范：sectionX-2..+5，sectionY-1..+3），裁到世界边界。
    /// 这是客户端画地图与落地要用的那一块。
    /// </summary>
    private IEnumerable<(int X, int Y)> SectionBlock(int tileX, int tileY)
    {
        int sectionsX = (int)Math.Ceiling(WorldMaxX / (double)TileSection.SectionWidth);
        int sectionsY = (int)Math.Ceiling(WorldMaxY / (double)TileSection.SectionHeight);

        int cx = Math.Clamp(tileX / TileSection.SectionWidth, 0, sectionsX - 1);
        int cy = Math.Clamp(tileY / TileSection.SectionHeight, 0, sectionsY - 1);

        for (int sy = cy - 1; sy <= cy + 3; sy++)
        {
            for (int sx = cx - 2; sx <= cx + 5; sx++)
            {
                if (sx < 0 || sy < 0 || sx >= sectionsX || sy >= sectionsY)
                    continue;

                yield return (sx, sy);
            }
        }
    }

    /// <summary>
    /// 把世界数据整包发出去：7（世界头）+ 9（状态文本）+ 10×N（分块）+ 49（可以开始）。
    /// 49 只发一次。
    /// </summary>
    /// <summary>收到 id:1 之后按配置主动发世界数据。</summary>
    private async Task SendWorldOnHelloAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        string mode = _config.Handshake.WorldOnHello;
        if (mode == "off")
        {
            _log.Info("world.on-hello-skipped", ("conn", _id), ("mode", mode));
            return;
        }

        _log.Info("world.on-hello", ("conn", _id), ("mode", mode));
        await SendWorldBundleAsync(stream, WorldSpawnX, WorldSpawnY, cancellationToken, headerOnly: mode == "header");
    }

    private async Task SendWorldBundleAsync(NetworkStream stream, int centerX, int centerY,
                                            CancellationToken cancellationToken,
                                            bool headerOnly = false, bool includeStart = true)
    {
        var frames = new List<byte[]> { Messages.WorldDataPacket(BuildWorld()) };

        int sections = 0;
        if (!headerOnly)
        {
            frames.Add(Messages.StatusTextPacket(0, "Bunny Loader", 0));

            foreach ((int sx, int sy) in SectionBlock(centerX, centerY))
            {
                frames.Add(Messages.TileSectionPacket(TileSection.Encode(sx, sy, GetTile, IsFrameImportant)));
                sections++;
            }

            if (includeStart && !_startPlayingSent)
            {
                frames.Add(Messages.StartPlayingPacket());
                _startPlayingSent = true;
            }
        }

        await SendBundleAsync(stream, frames, cancellationToken);

        _log.Info("world.sections",
            ("conn", _id),
            ("center", $"{centerX},{centerY}"),
            ("sections", sections),
            ("frames", frames.Count),
            ("bytes", frames.Sum(f => f.Length)));
    }

    private WorldData BuildWorld()
    {
        if (_wld is not null)
        {
            var loaded = new WorldData
            {
                Release326 = true,
                WorldName = _wld.Name,
                MaxTilesX = (short)_wld.MaxTilesX,
                MaxTilesY = (short)_wld.MaxTilesY,
                SpawnTileX = (short)_wld.SpawnTileX,
                SpawnTileY = (short)_wld.SpawnTileY,
                WorldSurface = (short)_wld.WorldSurface,
                RockLayer = (short)_wld.RockLayer,
                GameMode = (byte)_wld.GameMode,
                WorldId = _config.World.Seed,
                Time = 0,
                DayTime = _wld.DayTime,
                Guid = DeterministicGuid(_config.World.Seed),
            };

            if (_wld.Crimson)
                loaded.WorldFlags[1] |= 0x20;   // byte1 bit5 = crimson

            return loaded;
        }

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
