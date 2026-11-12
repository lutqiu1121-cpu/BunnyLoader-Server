using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BunnyLoader.Server.Player;
using BunnyLoader.Server.Protocol;
using BunnyLoader.Server.World;

namespace BunnyLoader.Server;

/// <summary>
/// 入口。优先级：命令行参数 &gt; 环境变量 &gt; 配置文件 &gt; 内置默认值。
/// 配置文件可缺失（面板只上传一个二进制也能跑）。
/// </summary>
public static class Program
{
    private const string DefaultConfig = "config/server.json";
    private const string DefaultTestData = "testdata/handshake-vectors.json";

    public static async Task<int> Main(string[] args)
    {
        string configPath = DefaultConfig;
        string testDataPath = DefaultTestData;
        string? captureDirectory = null;
        bool selftest = false;
        bool printConfig = false;
        int? cliPort = null;
        string? cliMsg3 = null;
        string? worldPath = null;
        bool worldInfo = false;
        string? cliProof = null;
        string? playerFilePath = null;   // --players 指定的档案目录
        string? parsePlrPath = null;
        string? syncRawHex = null;
        bool plrSelftest = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (!TryValue(args, ref i, out configPath)) return 2;
                    break;
                case "--testdata":
                    if (!TryValue(args, ref i, out testDataPath)) return 2;
                    break;
                case "--capture":
                    if (!TryValue(args, ref i, out captureDirectory)) return 2;
                    break;
                case "--port":
                    if (!TryValue(args, ref i, out string portText) || !int.TryParse(portText, out int port))
                    {
                        Console.Error.WriteLine("--port 需要一个整数");
                        return 2;
                    }
                    cliPort = port;
                    break;
                case "--msg3":
                    if (!TryValue(args, ref i, out string msg3) || (msg3 != "flags" && msg3 != "slot"))
                    {
                        Console.Error.WriteLine("--msg3 只接受 flags 或 slot");
                        return 2;
                    }
                    cliMsg3 = msg3;
                    break;
                case "--world":
                    if (!TryValue(args, ref i, out string worldArg)) return 2;
                    worldPath = worldArg;
                    break;
                case "--world-info":
                    if (!TryValue(args, ref i, out string infoPath)) return 2;
                    worldPath = infoPath;
                    worldInfo = true;
                    break;
                case "--proof":
                    if (!TryValue(args, ref i, out string proofArg)) return 2;
                    cliProof = proofArg.Trim().ToLowerInvariant();
                    break;
                case "--selftest":
                    selftest = true;
                    break;
                case "--players":
                    if (!TryValue(args, ref i, out playerFilePath)) return 2;
                    break;
                case "--parse-plr":
                    if (!TryValue(args, ref i, out parsePlrPath)) return 2;
                    break;
                case "--sync-raw":
                    if (!TryValue(args, ref i, out syncRawHex)) return 2;
                    break;
                case "--plr-selftest":
                    plrSelftest = true;
                    break;
                case "--print-config":
                    printConfig = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        // .plr 解析是纯离线工具：不读 config、不起监听、不发任何帧。
        if (parsePlrPath is not null)
            return PlrCommand.Run(parsePlrPath, syncRawHex, new JsonLogger("info"));

        if (plrSelftest)
        {
            bool plrOk = PlrCommand.SelfTest(new JsonLogger("info"));
            Console.Error.WriteLine(plrOk ? "plr selftest: PASS" : "plr selftest: FAIL");
            return plrOk ? 0 : 1;
        }

        ServerConfig config;
        bool configFound;
        try
        {
            config = ServerConfig.LoadOrDefault(configPath, out configFound);
            config.ApplyEnvironment();
            if (cliPort.HasValue)
                config.Server.Port = cliPort.Value;
            if (cliMsg3 is not null)
            {
                config.Handshake.Layout = cliMsg3;
                config.Handshake.Auto = false;
            }
            if (cliProof is not null)
                config.Proof.Mode = cliProof;
            config.Validate();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"配置错误: {e.Message}");
            return 2;
        }

        var log = new JsonLogger(config.Logging.Level);

        worldPath ??= string.IsNullOrWhiteSpace(config.World.File) ? null : config.World.File;

        WldWorld? world = null;
        if (worldPath is not null)
        {
            try
            {
                world = WldReader.Load(worldPath);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"世界文件读取失败: {e.Message}");
                return 1;
            }

            foreach (string note in world.Notes)
                log.Info("world.note", ("text", note));

            log.Info("world.loaded",
                ("file", Path.GetFullPath(worldPath)),
                ("name", world.Name),
                ("size", $"{world.MaxTilesX}x{world.MaxTilesY}"),
                ("spawn", $"{world.SpawnTileX},{world.SpawnTileY}"),
                ("surface", (int)world.WorldSurface),
                ("version", world.Version));
        }

        if (worldInfo)
        {
            if (world is null)
            {
                Console.Error.WriteLine("--world-info 需要一个 .wld 路径");
                return 2;
            }

            int active = 0, walls = 0;
            foreach (Tile tile in world.Tiles)
            {
                if (tile.Active) active++;
                if (tile.Wall != 0) walls++;
            }

            long total = (long)world.MaxTilesX * world.MaxTilesY;
            log.Info("world.info",
                ("file", Path.GetFullPath(worldPath!)),
                ("version", world.Version),
                ("name", world.Name),
                ("seed", world.Seed),
                ("size", $"{world.MaxTilesX}x{world.MaxTilesY}"),
                ("spawn", $"{world.SpawnTileX},{world.SpawnTileY}"),
                ("surface", (int)world.WorldSurface),
                ("rockLayer", (int)world.RockLayer),
                ("crimson", world.Crimson),
                ("tiles", total),
                ("active", active),
                ("activePct", total == 0 ? 0 : Math.Round(active * 100.0 / total, 2)),
                ("walls", walls));
            return 0;
        }

        if (printConfig)
        {
            log.Info("config.effective",
                ("configFile", configFound ? Path.GetFullPath(configPath) : "(missing, using defaults)"),
                ("host", config.Server.Host),
                ("port", config.Server.Port),
                ("name", config.Server.Name),
                ("level", config.Logging.Level),
                ("msg3", config.Handshake.Auto
                    ? "auto(flags+bool1 -> slot -> flags+bool0)"
                    : $"{config.Handshake.Layout}+{config.Handshake.SpecialFlag}"),
                ("world", config.World.Name),
                ("skipWelcomePack", config.Handshake.SkipWelcomePack),
                ("msg3Hex", config.Handshake.Msg3Hex),
                ("welcomeHex", config.Handshake.WelcomeHex),
                ("candidates", config.Handshake.Candidates),
                ("proofMode", config.Proof.Mode),
                ("proofTiming", config.Proof.Timing),
                ("proofFraming", config.Proof.Framing),
                ("proofWrapper", config.Proof.Wrapper),
                ("proofNetModuleId", config.Proof.NetModuleId),
                ("proofMessageId", config.Proof.MessageId),
                ("proofSweep", config.Proof.Sweep),
                ("proofWaitReply", config.Proof.WaitReply),
                ("echoControls", config.R5.EchoControls),
                ("proofServerLine", config.Proof.ServerLine),
                ("skipApproved", config.Handshake.SkipApproved),
                ("sendMsg7After3", config.Handshake.SendMsg7After3),
                ("msg7IncludeWelcome", config.Handshake.Msg7IncludeWelcome),
                ("mods", config.Mods.Count),
                ("env", string.Join(",", config.EnvNotes)));
            return 0;
        }

        if (selftest)
        {
            log.Info("config.loaded",
                ("path", configFound ? Path.GetFullPath(configPath) : "(missing, using defaults)"),
                ("host", config.Server.Host),
                ("port", config.Server.Port),
                ("mods", config.Mods.Count),
                ("world", config.World.Name));

            bool ok = ProtocolSelfTest.Run(log, testDataPath);
            if (ok)
                log.Info("selftest.pass");
            else
                log.Error("selftest.fail");
            return ok ? 0 : 1;
        }

        captureDirectory ??= Environment.GetEnvironmentVariable("CAPTURE_DIR");
        if (!configFound)
            log.Warn("config.missing", ("path", Path.GetFullPath(configPath)), ("note", "使用内置默认值；可用环境变量覆盖"));

        IPAddress bind;
        try
        {
            bind = ResolveBind(config.Server.Host);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"绑定地址错误: {e.Message}");
            return 2;
        }

        var listener = new TcpListener(bind, config.Server.Port);
        using var shutdown = new CancellationTokenSource();
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            shutdown.Cancel();
        });
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            log.Error("server.bind.failed",
                ("host", config.Server.Host),
                ("port", config.Server.Port),
                ("error", e.Message));
            return 1;
        }

        log.Info("server.start",
            ("host", config.Server.Host),
            ("port", config.Server.Port),
            ("name", config.Server.Name),
            ("world", config.World.Name),
            ("msg3", config.Handshake.Auto
                ? "auto(flags+bool1 -> slot -> flags+bool0)"
                : $"{config.Handshake.Layout}+{config.Handshake.SpecialFlag}"),
            ("skipWelcomePack", config.Handshake.SkipWelcomePack),
            ("mods", config.Mods.Count),
            ("worldFile", worldPath is null ? "(flat)" : _worldFileName(worldPath)),
            ("capture", captureDirectory ?? "(off)"),
            ("env", string.Join(",", config.EnvNotes)),
            ("note", "R2：握手 + 世界头；方块数据在 R3"));

        int counter = 0;
        try
        {
            // 动态玩家档案（真 SSC）：按玩家名找 Players/<名字>.plr，没有就用模板造开荒档。
            // 找不到模板只记录 ssc.store.disabled，服务照常跑（不下发 SSC）。
            string? playersDir = playerFilePath
                ?? Environment.GetEnvironmentVariable("PLAYERS_DIR");
            PlayerProfileStore? store = PlayerProfileStore.TryCreate(playersDir, log);
            if (store is not null)
            {
                log.Info("ssc.store.ready",
                    ("dir", store.DirectoryPath),
                    ("template", store.TemplatePath),
                    ("note", "收到 id:4 时按玩家名动态加载/创建档案"));
            }

            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException e)
                {
                    log.Warn("accept.error", ("error", e.Message));
                    continue;
                }

                string connectionId = "c" + Interlocked.Increment(ref counter);
                log.Info("conn.accept", ("conn", connectionId), ("peer", client.Client.RemoteEndPoint?.ToString() ?? "?"));

                CaptureWriter? capture = string.IsNullOrWhiteSpace(captureDirectory)
                    ? null
                    : new CaptureWriter(captureDirectory, connectionId, log);

                Session session = new(client, connectionId, counter, config, log, capture, world, store);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await session.RunAsync(shutdown.Token);
                    }
                    finally
                    {
                        capture?.Dispose();
                    }
                });
            }
        }
        finally
        {
            listener.Stop();
            log.Info("server.stop", ("connections", counter));
        }

        return 0;
    }

    private static bool TryValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"{args[index]} 缺少参数");
            value = "";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static string _worldFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return "?";
        }
    }

    private static IPAddress ResolveBind(string host)
    {
        if (host is "0.0.0.0" or "*" or "")
            return IPAddress.Any;
        if (host is "::" or "[::]")
            return IPAddress.IPv6Any;
        if (IPAddress.TryParse(host, out IPAddress? parsed))
            return parsed;
        throw new InvalidDataException($"server.host 只接受 IP 字面量，当前: {host}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("BunnyLoader.Server —— R2（握手 + 世界头）");
        Console.WriteLine();
        Console.WriteLine("命令行参数（优先级最高）:");
        Console.WriteLine("  --config <path>      配置文件，默认 config/server.json（可缺失）");
        Console.WriteLine("  --port <int>         监听端口");
        Console.WriteLine("  --msg3 <flags|slot>  消息 3 的布局（flags=2 字节，slot=1 字节）");
        Console.WriteLine("  --capture <dir>      双向原始字节落盘目录");
        Console.WriteLine("  --testdata <path>    golden vectors 路径");
        Console.WriteLine("  --print-config       打印生效配置后退出");
        Console.WriteLine("  --selftest           配置 + 编解码自检后退出");
        Console.WriteLine("  --world <path>       用原版 .wld 作为真实世界（否则用平坦世界）");
        Console.WriteLine("  --world-info <path>  只解析 .wld 并打印摘要后退出");
        Console.WriteLine("  --proof <auto|on|off> 二开版纯文本握手：auto=先探测（默认）");
        Console.WriteLine("  --players <dir>      玩家档案目录（缺省 <程序目录>/Players）；名字来自 id:4");
        Console.WriteLine("  --parse-plr <path>   只解析 .plr（AES-128 自动解密）并打印字段，退出；不联网");
        Console.WriteLine("  --sync-raw <hex>     配合 --parse-plr：给出 id:4 的完整 payload，做颜色对照");
        Console.WriteLine("  --plr-selftest       用合成 v319 存档自检 .plr 解析器后退出");
        Console.WriteLine();
        Console.WriteLine("环境变量（面板只能改启动命令时用这些）:");
        Console.WriteLine("  SERVER_PORT / PORT   监听端口，默认 7777");
        Console.WriteLine("  SERVER_HOST          监听地址，默认 0.0.0.0");
        Console.WriteLine("  MSG3_SPECIAL_FLAG    那个 bool 的值：true=0x01，false=0x00");
        Console.WriteLine("  MSG3_LAYOUT          布局：flags（2 字节，默认）/ slot（1 字节）");
        Console.WriteLine("  MSG3_VARIANT         便捷开关：auto（每次连接轮换，默认）/ flags1 / flags0 / slot");
        Console.WriteLine("  MSG3_HEX             直接指定消息 3 的负载十六进制，如 0100（覆盖前面的设置）");
        Console.WriteLine("  WELCOME_HEX          直接指定 82 负载十六进制，默认 7fb1c801");
        Console.WriteLine("  SKIP_WELCOME_PACK    true=消息 3 之后不再发送任何包（隔离测试用）");
        Console.WriteLine("  MSG3_CANDIDATES      候选负载列表，逗号分隔；auto=内置 6 个候选，按连接轮换");
        Console.WriteLine("  SKIP_APPROVED        true=连消息 3 都不发（控制组）");
        Console.WriteLine("  SEND_MSG7_AFTER_3    true=消息 3 之后同一次 write 里紧接消息 7");
        Console.WriteLine("  MSG7_INCLUDE_WELCOME 配合上一条：是否先补欢迎包（默认 true，必需）");
        Console.WriteLine("  LOG_LEVEL            debug / info / warn / error");
        Console.WriteLine("  CAPTURE_DIR          抓包目录，默认关闭");
        Console.WriteLine("  WORLD_NAME           世界名");
        Console.WriteLine("  SERVER_NAME          服务器名");
        Console.WriteLine("  WORLD_FILE           原版 .wld 路径（等价 --world）");
        Console.WriteLine("  PROOF_MODE           auto（默认）/ on / off");
        Console.WriteLine("  PROOF_SUFFIX         lf（默认）/ crlf / none");
        Console.WriteLine("  PROOF_TIMEOUT        等客户端 proof 的秒数，默认 3");
        Console.WriteLine("  PROOF_AFTER_HELLO    true=收到 id:1 之后再做 proof");
        Console.WriteLine("  PROOF_WITH_LENGTH    true=proof 带 2 字节长度头");
        Console.WriteLine("  PROOF_IN_NETMODULE   true=proof 封进 id:82（模块 0xB17F）");
        Console.WriteLine("  PROOF_NETMODULE_ID   封进 82 时用的模块号，默认 45439（0xB17F）");
        Console.WriteLine("  PROOF_AS_TEXT        true=预设：82 + 模块 0 + kind 1 + 文本");
        Console.WriteLine("  PROOF_MESSAGE_ID     把文本当某个顶层消息的负载：[u16][id][文本]");
        Console.WriteLine("  PROOF_SWEEP          true=每次连接自动换一种 proof 组合（日志 proof.variant）");
        Console.WriteLine("  PROOF_WAIT_REPLY     true=发完 proof 等回执（默认 false：立刻发 id:3）");
        Console.WriteLine("  NO_WELCOME           true=完全不发欢迎包 82/200（隔离测试）");
        Console.WriteLine("  WORLD_ON_HELLO       full（默认）=收到 Hello 就推 7+9+分块+49");
    }
}
