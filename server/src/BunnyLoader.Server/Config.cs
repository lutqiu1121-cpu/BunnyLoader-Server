using System.Text.Json;
using System.Text.Json.Serialization;

namespace BunnyLoader.Server;

/// <summary>
/// 外置配置。字段名与 config/server.json 一一对应。
/// 本轮只有「服务端定位 + 日志 + 规则 + 模组清单」四块，不含任何协议字段。
/// </summary>
public sealed class ServerConfig
{
    [JsonPropertyName("server")]
    public ServerSection Server { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingSection Logging { get; set; } = new();

    [JsonPropertyName("rules")]
    public RulesSection Rules { get; set; } = new();

    /// <summary>
    /// 服务端「认定」的模组集，顺序即契约。
    /// 与 BL 的 ServerSyncHooks.Judge 比较的键一致：uid + version，逐位保序。
    /// 本轮只加载与打印，不参与任何判定。
    /// </summary>
    [JsonPropertyName("mods")]
    public List<ModEntry> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ServerConfig Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"配置文件不存在: {full}");

        string json = File.ReadAllText(full);
        ServerConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<ServerConfig>(json, Options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"配置文件不是合法 JSON: {e.Message}");
        }

        if (cfg is null)
            throw new InvalidDataException("配置文件内容为空");

        cfg.Validate();
        return cfg;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server.Host))
            throw new InvalidDataException("server.host 不能为空");
        if (Server.Port < 1 || Server.Port > 65535)
            throw new InvalidDataException($"server.port 越界: {Server.Port}");
        if (string.IsNullOrWhiteSpace(Server.Name))
            throw new InvalidDataException("server.name 不能为空");

        string level = Logging.Level.Trim().ToLowerInvariant();
        if (level is not ("debug" or "info" or "warn" or "error"))
            throw new InvalidDataException($"logging.level 只能是 debug/info/warn/error，当前: {Logging.Level}");
        Logging.Level = level;

        for (int i = 0; i < Mods.Count; i++)
        {
            ModEntry m = Mods[i];
            if (string.IsNullOrWhiteSpace(m.Uid))
                throw new InvalidDataException($"mods[{i}].uid 不能为空");
            if (string.IsNullOrWhiteSpace(m.Version))
                throw new InvalidDataException($"mods[{i}].version 不能为空（BL 判定不接受空版本）");
        }
    }
}

public sealed class ServerSection
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "0.0.0.0";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 7777;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Bunny Loader Server";
}

public sealed class LoggingSection
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    /// <summary>true 时每次收到数据都记一条 conn.bytes（含首 8 字节十六进制）。</summary>
    [JsonPropertyName("dumpBytes")]
    public bool DumpBytes { get; set; } = true;
}

public sealed class RulesSection
{
    /// <summary>对应 HostSettingsMenu 的「Mod sync」。</summary>
    [JsonPropertyName("syncMods")]
    public bool SyncMods { get; set; } = true;

    /// <summary>对应「Allow textures」。</summary>
    [JsonPropertyName("allowTextures")]
    public bool AllowTextures { get; set; } = true;

    /// <summary>对应「Allow cheat menu」，最终落在欢迎包 rules 的 bit0。</summary>
    [JsonPropertyName("allowCheats")]
    public bool AllowCheats { get; set; } = true;

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public sealed class ModEntry
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}
