using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace BunnyLoader.Server;

/// <summary>
/// 阶段 1 空骨架：只做监听、结构化日志、配置加载。
/// **不解析任何协议，也不发送任何字节**——收到数据只记录长度与首几字节。
/// </summary>
public static class Program
{
    private const string DefaultConfig = "config/server.json";

    public static async Task<int> Main(string[] args)
    {
        string configPath = DefaultConfig;
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
                ("level", config.Logging.Level),
                ("mods", config.Mods.Count));
            foreach (ModEntry mod in config.Mods)
                log.Info("config.mod", ("uid", mod.Uid), ("id", mod.Id), ("version", mod.Version));
            return 0;
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
            ("mods", config.Mods.Count),
            ("level", config.Logging.Level),
            ("note", "阶段1骨架：不解析协议、不应答任何包"));

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

                string connId = "c" + Interlocked.Increment(ref counter);
                _ = Task.Run(() => HandleAsync(client, connId, config, log, shutdown.Token));
            }
        }
        finally
        {
            listener.Stop();
            log.Info("server.stop", ("connections", counter));
        }

        return 0;
    }

    private static async Task HandleAsync(TcpClient client, string connId, ServerConfig config, JsonLogger log, CancellationToken ct)
    {
        string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        client.NoDelay = true;

        long total = 0;
        var buffer = new byte[8192];
        log.Info("conn.open", ("conn", connId), ("peer", peer));

        try
        {
            using NetworkStream stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (n == 0)
                {
                    log.Info("conn.close", ("conn", connId), ("bytes", total), ("reason", "eof"));
                    return;
                }

                total += n;
                if (log.IsDebug && config.Logging.DumpBytes)
                {
                    int show = Math.Min(n, 8);
                    log.Debug("conn.bytes",
                        ("conn", connId),
                        ("len", n),
                        ("total", total),
                        ("head", Hex(buffer, show)));
                }

                // 阶段 1：**故意不解析、不回包**。
                // 消息 1/3/4/6/7/68 与 kind 200/11 的实现在 R2/R3。
            }

            log.Info("conn.close", ("conn", connId), ("bytes", total), ("reason", "shutdown"));
        }
        catch (OperationCanceledException)
        {
            log.Info("conn.close", ("conn", connId), ("bytes", total), ("reason", "shutdown"));
        }
        catch (IOException e)
        {
            log.Info("conn.close", ("conn", connId), ("bytes", total), ("reason", "io"), ("error", e.Message));
        }
        catch (Exception e)
        {
            log.Error("conn.error", ("conn", connId), ("error", e.GetType().Name), ("message", e.Message));
        }
        finally
        {
            client.Dispose();
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

    private static string Hex(byte[] buffer, int count)
    {
        var text = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                text.Append(' ');
            text.Append(buffer[i].ToString("x2"));
        }
        return text.ToString();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("BunnyLoader.Server —— 阶段1 空骨架");
        Console.WriteLine("用法:");
        Console.WriteLine("  dotnet BunnyLoader.Server.dll [--config <path>]");
        Console.WriteLine("  dotnet BunnyLoader.Server.dll --selftest [--config <path>]");
        Console.WriteLine();
        Console.WriteLine("  --config    配置文件路径，默认 config/server.json");
        Console.WriteLine("  --selftest  只加载并打印配置，不监听端口");
    }
}
