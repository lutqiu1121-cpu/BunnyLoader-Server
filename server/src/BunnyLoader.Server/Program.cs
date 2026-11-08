using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BunnyLoader.Server.Protocol;

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
                case "--selftest":
                    selftest = true;
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
            config.Validate();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"配置错误: {e.Message}");
            return 2;
        }

        var log = new JsonLogger(config.Logging.Level);

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
            ("capture", captureDirectory ?? "(off)"),
            ("env", string.Join(",", config.EnvNotes)),
            ("note", "R2：握手 + 世界头；方块数据在 R3"));

        int counter = 0;
        try
        {
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

                Session session = new(client, connectionId, counter, config, log, capture);
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
    }
}
