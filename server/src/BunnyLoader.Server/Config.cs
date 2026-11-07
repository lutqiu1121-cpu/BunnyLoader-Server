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

    [JsonPropertyName("world")]
    public WorldSection World { get; set; } = new();

    [JsonPropertyName("handshake")]
    public HandshakeSection Handshake { get; set; } = new();

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

        if (string.IsNullOrWhiteSpace(World.Name))
            throw new InvalidDataException("world.name 不能为空");
        if (World.Name.Length > 64)
            throw new InvalidDataException("world.name 过长（>64）");
        if (World.MaxTilesX < 100 || World.MaxTilesX > 8400)
            throw new InvalidDataException($"world.maxTilesX 应在 100..8400，当前 {World.MaxTilesX}");
        if (World.MaxTilesY < 100 || World.MaxTilesY > 2400)
            throw new InvalidDataException($"world.maxTilesY 应在 100..2400，当前 {World.MaxTilesY}");
        if (World.SpawnTileX < 0 || World.SpawnTileX > World.MaxTilesX)
            throw new InvalidDataException($"world.spawnTileX 越界，当前 {World.SpawnTileX}");
        if (World.SpawnTileY < 0 || World.SpawnTileY > World.MaxTilesY)
            throw new InvalidDataException($"world.spawnTileY 越界，当前 {World.SpawnTileY}");

        string variant = Handshake.ConnectionApproved.Trim().ToLowerInvariant();
        if (variant is not ("flags" or "slot"))
            throw new InvalidDataException($"handshake.connectionApproved 只能是 flags / slot，当前: {Handshake.ConnectionApproved}");
        Handshake.ConnectionApproved = variant;
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

/// <summary>
/// 消息 7 的世界头参数。R2 只发世界头，方块数据在 R3。
/// 默认值刻意用 4200x1200（小世界）与 "ProbeTiny" 之外的稳定名字。
/// </summary>
public sealed class WorldSection
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Bunny World";

    [JsonPropertyName("maxTilesX")]
    public short MaxTilesX { get; set; } = 4200;

    [JsonPropertyName("maxTilesY")]
    public short MaxTilesY { get; set; } = 1200;

    [JsonPropertyName("spawnTileX")]
    public short SpawnTileX { get; set; } = 2100;

    [JsonPropertyName("spawnTileY")]
    public short SpawnTileY { get; set; } = 300;

    [JsonPropertyName("worldSurface")]
    public short WorldSurface { get; set; } = 350;

    [JsonPropertyName("rockLayer")]
    public short RockLayer { get; set; } = 500;

    [JsonPropertyName("dungeonX")]
    public short DungeonX { get; set; }

    [JsonPropertyName("dungeonY")]
    public short DungeonY { get; set; }

    /// <summary>true 时 worldFlags 第 2 个字节的 bit5 置位（腐蚀地）。</summary>
    [JsonPropertyName("crimson")]
    public bool Crimson { get; set; }

    [JsonPropertyName("gameMode")]
    public byte GameMode { get; set; }

    /// <summary>release 326 的包 7 末尾多两个 i16（dungeonX/dungeonY）。</summary>
    [JsonPropertyName("release326")]
    public bool Release326 { get; set; } = true;

    /// <summary>世界 ID 的来源，决定 worldId 与 GUID，保证重连是同一个世界。</summary>
    [JsonPropertyName("seed")]
    public int Seed { get; set; } = 1;
}

/// <summary>
/// 握手细节。消息 3 在 1.4.5 里是 `u8 槽位 + u8 bool`，1.4.4 只有一个槽位字节。
/// 移动端到底用哪种尚未实测，所以做成配置项：V1 失败时改这里，不必改代码。
/// </summary>
public sealed class HandshakeSection
{
    /// <summary>
    /// "flags" = 槽位 + bool（桌面 1.4.5）；"slot" = 只有槽位字节（1.4.4 / 可能的移动端）。
    /// </summary>
    [JsonPropertyName("connectionApproved")]
    public string ConnectionApproved { get; set; } = "flags";
}
