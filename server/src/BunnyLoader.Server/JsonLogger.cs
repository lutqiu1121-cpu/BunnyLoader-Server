using System.Text;
using System.Text.Json;

namespace BunnyLoader.Server;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 每行一个 JSON 的结构化日志器。不用第三方库，线程安全。
/// </summary>
public sealed class JsonLogger
{
    private readonly object _gate = new();
    private readonly TextWriter _out;
    private readonly LogLevel _min;

    public JsonLogger(string level) : this(Parse(level), Console.Out)
    {
    }

    public JsonLogger(LogLevel min, TextWriter? output = null)
    {
        _min = min;
        _out = output ?? Console.Out;
    }

    public bool IsDebug => _min <= LogLevel.Debug;

    public static LogLevel Parse(string level) => level.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    public void Debug(string ev, params (string Key, object? Value)[] fields) => Write(LogLevel.Debug, ev, fields);

    public void Info(string ev, params (string Key, object? Value)[] fields) => Write(LogLevel.Info, ev, fields);

    public void Warn(string ev, params (string Key, object? Value)[] fields) => Write(LogLevel.Warn, ev, fields);

    public void Error(string ev, params (string Key, object? Value)[] fields) => Write(LogLevel.Error, ev, fields);

    private void Write(LogLevel level, string ev, (string Key, object? Value)[] fields)
    {
        if (level < _min)
            return;

        string line;
        using (var buffer = new MemoryStream(256))
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
                writer.WriteString("lvl", Name(level));
                writer.WriteString("ev", ev);
                foreach ((string key, object? value) in fields)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, value);
                }
                writer.WriteEndObject();
            }
            line = Encoding.UTF8.GetString(buffer.ToArray());
        }

        lock (_gate)
        {
            _out.WriteLine(line);
            _out.Flush();
        }
    }

    private static string Name(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        _ => "info",
    };

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            default:
                writer.WriteStringValue(value?.ToString() ?? string.Empty);
                break;
        }
    }
}
