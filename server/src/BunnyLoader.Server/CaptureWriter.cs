namespace BunnyLoader.Server;

/// <summary>
/// 双向原始字节落盘，等价于 pcap 的可读替代：每个连接两个文件，c2s / s2c。
/// 之后用 tools/proto_ref.py decode 解析。
/// </summary>
internal sealed class CaptureWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream? _fromClient;
    private readonly FileStream? _toClient;

    public CaptureWriter(string directory, string connectionId, JsonLogger log)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _fromClient = Create(Path.Combine(directory, $"{connectionId}-c2s.bin"));
            _toClient = Create(Path.Combine(directory, $"{connectionId}-s2c.bin"));
            log.Info("capture.open", ("dir", directory), ("conn", connectionId));
        }
        catch (Exception e)
        {
            log.Warn("capture.failed", ("dir", directory), ("error", e.Message));
        }
    }

    private static FileStream Create(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, FileOptions.None);

    public void FromClient(ReadOnlySpan<byte> data)
    {
        if (_fromClient is null)
            return;
        lock (_gate)
        {
            _fromClient.Write(data);
            _fromClient.Flush();
        }
    }

    public void ToClient(ReadOnlySpan<byte> data)
    {
        if (_toClient is null)
            return;
        lock (_gate)
        {
            _toClient.Write(data);
            _toClient.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _fromClient?.Dispose();
            _toClient?.Dispose();
        }
    }
}
