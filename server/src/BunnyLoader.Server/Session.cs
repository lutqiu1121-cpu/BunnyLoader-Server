using System.Buffers.Binary;
using System.Text;
using System.Net.Sockets;
using BunnyLoader.Server.Protocol;
using BunnyLoader.Server.World;

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
    private readonly WldWorld? _wld;
    private bool _suppressOutbound;
    private bool _spawnCorrected;
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

            // 纯文本 proof 握手（二开版 "protocol 1"）。必须在帧协议之前。
            await ProveAsync(stream, inbox, cancellationToken);

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


    /// <summary>从 inbox 里切出完整帧并分发。</summary>
    private async Task ProcessFramesAsync(NetworkStream stream, List<byte> inbox, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// 纯文本 proof 握手。
    ///   on   ：立刻发一行，等客户端回一行（最多 timeout 秒），然后无论成败进入帧协议；
    ///   off  ：不发，直接帧协议；
    ///   auto ：先等一小会儿。若先到的是合法帧，判定为老客户端，绝不发文本。
    /// </summary>
    private async Task ProveAsync(NetworkStream stream, List<byte> inbox, CancellationToken cancellationToken)
    {
        ProofSection proof = _config.Proof;

        if (proof.Mode == "off")
        {
            _log.Info("proof.skipped", ("conn", _id), ("reason", "mode=off"));
            return;
        }

        if (proof.Mode == "auto")
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(200);
            while (DateTime.UtcNow < deadline && inbox.Count < 3)
                await ReadIntoAsync(stream, inbox, cancellationToken, 100);

            if (inbox.Count >= 3 && LooksLikeFrame(inbox))
            {
                _log.Info("proof.skipped", ("conn", _id), ("reason", "legacy-frame", "note", "老客户端直接发帧，不发文本"));
                return;
            }
        }

        string suffix = proof.Suffix switch
        {
            "crlf" => "\r\n",
            "none" => "",
            _ => "\n",
        };

        byte[] greeting = Encoding.ASCII.GetBytes(proof.ServerLine + suffix);
        await stream.WriteAsync(greeting, cancellationToken);
        _capture?.ToClient(greeting);
        _log.Info("proof.sent", ("conn", _id), ("text", proof.ServerLine), ("suffix", proof.Suffix));

        DateTime end = DateTime.UtcNow.AddSeconds(proof.TimeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            string? line = TakeLine(inbox);
            if (line is not null)
            {
                _log.Info("proof.received", ("conn", _id), ("text", line));
                if (line.Trim() == proof.ClientLine)
                {
                    _log.Info("proof.accepted", ("conn", _id));
                    return;
                }

                _log.Warn("proof.unexpected", ("conn", _id), ("expected", proof.ClientLine), ("got", line));
            }

            await ReadIntoAsync(stream, inbox, cancellationToken, 200);
        }

        _log.Warn("proof.timeout", ("conn", _id), ("seconds", proof.TimeoutSeconds));
    }

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
            // 只是这一段等超时，继续下一轮
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

    /// <summary>从 inbox 头部取一行（含 \n 或 \r\n）；没有完整行返回 null。</summary>
    private static string? TakeLine(List<byte> inbox)
    {
        int newline = inbox.IndexOf((byte)0x0A);
        if (newline < 0)
            return null;

        string line = Encoding.ASCII.GetString(inbox.GetRange(0, newline).ToArray()).TrimEnd('\r');
        inbox.RemoveRange(0, newline + 1);
        return line;
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
                else
                {
                    bundle.Add(BuildWelcomeFrame());
                }

                await SendBundleAsync(stream, bundle, cancellationToken);
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
                    // 客户端在知道自己位置之前会发 (-1,-1)。按它算会把分块发到世界左上角，
                    // 玩家脚下就没有方块。这里一律回退到服务端出生点。
                    _log.Warn("spawn.fallback", ("conn", _id), ("requested", $"{reqX},{reqY}"), ("using", $"{spawnX},{spawnY}"));
                    reqX = spawnX;
                    reqY = spawnY;
                }

                _log.Info("spawn.request", ("conn", _id), ("x", reqX), ("y", reqY));

                // 同一个坐标 1.5 秒内只发一次，避免客户端下坠时刷屏
                DateTime now = DateTime.UtcNow;
                if (reqX == _lastSectionX && reqY == _lastSectionY
                    && (now - _lastSectionAt).TotalMilliseconds < 1500)
                {
                    _log.Info("sections.skipped", ("conn", _id), ("x", reqX), ("y", reqY));
                    break;
                }
                _lastSectionX = reqX;
                _lastSectionY = reqY;
                _lastSectionAt = now;

                var frames = new List<byte[]>
                {
                    Messages.WorldDataPacket(BuildWorld()),          // 7：按规范再发一次
                    Messages.StatusTextPacket(0, "Bunny Loader", 0), // 9：加载界面状态文本
                };

                int sections = 0;
                foreach ((int sx, int sy) in SectionBlock(reqX, reqY))
                {
                    byte[] compressed = TileSection.Encode(sx, sy, GetTile, IsFrameImportant);
                    frames.Add(Messages.TileSectionPacket(compressed));
                    sections++;
                }

                frames.Add(Messages.StartPlayingPacket());            // 49：可以开始了
                await SendBundleAsync(stream, frames, cancellationToken);

                _log.Info("world.sections",
                    ("conn", _id),
                    ("sections", sections),
                    ("frames", frames.Count),
                    ("bytes", frames.Sum(f => f.Length)));
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
                    _log.Info("player.spawn.ignored", ("conn", _id));
                }
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

        await stream.WriteAsync(buffer.AsMemory(), cancellationToken);
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
    private bool IsFrameImportant(ushort type) => _wld?.IsFrameImportant(type) ?? false;

    private int WorldMaxX => _wld?.MaxTilesX ?? _config.World.MaxTilesX;
    private int WorldMaxY => _wld?.MaxTilesY ?? _config.World.MaxTilesY;
    private int WorldSpawnX => _wld?.SpawnTileX ?? _config.World.SpawnTileX;
    private int WorldSpawnY => _wld?.SpawnTileY ?? _config.World.SpawnTileY;

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
