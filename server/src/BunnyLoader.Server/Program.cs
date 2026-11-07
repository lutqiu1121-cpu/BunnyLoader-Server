using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server;

/// <summary>
/// R2 入口：加载配置、监听、按帧分派握手（消息 1/3/68/200/6/7）。
/// 方块数据与玩家生成在 R3。
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

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--config 需要一个路径");
                        return 2;
                    }
                    configPath = args[++i];
                    break;

                case "--testdata":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--testdata 需要一个路径");
                        return 2;
                    }
                    testDataPath = args[++i];
                    break;

                case "--capture":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--capture 需要一个目录");
                        return 2;
                    }
                    captureDirectory = args[++i];
                    break;

                case "--selftest":
                    selftest = true;
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
        try
        {
            config = ServerConfig.Load(configPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"配置错误: {e.Message}");
            return 2;
        }

        var log = new JsonLogger(config.Logging.Level);

        if (selftest)
        {
            log.Info("config.loaded",
                ("path", Path.GetFullPath(configPath)),
                ("host", config.Server.Host),
                ("port", config.Server.Port),
                ("name", config.Server.Name),
                ("mods", config.Mods.Count),
                ("world", config.World.Name));

            bool ok = ProtocolSelfTest.Run(log, testDataPath);
            if (ok)
                log.Info("selftest.pass");
            else
                log.Error("selftest.fail");
            return ok ? 0 : 1;
        }

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
            log.Error("server.bind.failed", ("host", config.Server.Host), ("port", config.Server.Port), ("error", e.Message));
            return 1;
        }

        log.Info("server.start",
            ("host", config.Server.Host),
            ("port", config.Server.Port),
            ("name", config.Server.Name),
            ("world", config.World.Name),
            ("mods", config.Mods.Count),
            ("capture", captureDirectory ?? "(off)"),
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
                CaptureWriter? capture = captureDirectory is null
                    ? null
                    : new CaptureWriter(captureDirectory, connectionId, log);

                Session session = new(client, connectionId, config, log, capture);
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
        Console.WriteLine("用法:");
        Console.WriteLine("  dotnet BunnyLoader.Server.dll [选项]");
        Console.WriteLine();
        Console.WriteLine("  --config <path>    配置文件，默认 config/server.json");
        Console.WriteLine("  --testdata <path>  golden vectors，默认 testdata/handshake-vectors.json");
        Console.WriteLine("  --capture <dir>    把每个连接的双向原始字节写成 cN-c2s.bin / cN-s2c.bin");
        Console.WriteLine("  --selftest         只跑配置加载 + 编解码向量自检，不监听");
        Console.WriteLine("  --help             显示本帮助");
    }
}
