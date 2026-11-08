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

    /// <summary>
    /// 面板部署时通常只上传一个二进制、不传配置文件。
    /// 所以配置文件是可选的：不存在就用内置默认值（端口 7777、消息 3 带 bool）。
    /// </summary>
    public static ServerConfig LoadOrDefault(string path, out bool found)
    {
        found = File.Exists(Path.GetFullPath(path));
        return found ? Load(path) : new ServerConfig();
    }

    /// <summary>
    /// 环境变量覆盖（优先级：命令行 > 环境变量 > 配置文件 > 默认值）。
    /// 面板用户改不了文件，只能改启动命令，所以决定性开关都放在这里。
    /// </summary>
    public void ApplyEnvironment()
    {
        string? port = FirstEnv("SERVER_PORT", "PORT");
        if (!string.IsNullOrWhiteSpace(port))
        {
            if (!int.TryParse(port.Trim(), out int parsedPort))
                throw new InvalidDataException($"SERVER_PORT 不是整数: {port}");
            Server.Port = parsedPort;
            EnvNotes.Add($"SERVER_PORT={parsedPort}");
        }

        string? host = FirstEnv("SERVER_HOST", "HOST");
        if (!string.IsNullOrWhiteSpace(host))
        {
            Server.Host = host.Trim();
            EnvNotes.Add($"SERVER_HOST={Server.Host}");
        }

        string? name = Env("SERVER_NAME");
        if (!string.IsNullOrWhiteSpace(name))
        {
            Server.Name = name.Trim();
            EnvNotes.Add("SERVER_NAME=<set>");
        }

        string? level = Env("LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(level))
        {
            Logging.Level = level.Trim();
            EnvNotes.Add($"LOG_LEVEL={Logging.Level}");
        }

        string? worldName = Env("WORLD_NAME");
        if (!string.IsNullOrWhiteSpace(worldName))
        {
            World.Name = worldName.Trim();
            EnvNotes.Add("WORLD_NAME=<set>");
        }

        // 消息 3：布局与 bool 的值是两个独立维度，分别覆盖。
        string? layout = Env("MSG3_LAYOUT");
        if (!string.IsNullOrWhiteSpace(layout))
        {
            Handshake.Layout = layout.Trim().ToLowerInvariant();
            EnvNotes.Add($"MSG3_LAYOUT={Handshake.Layout}");
        }

        string? specialFlag = Env("MSG3_SPECIAL_FLAG");
        if (!string.IsNullOrWhiteSpace(specialFlag))
        {
            Handshake.SpecialFlag = ParseBool(specialFlag, "MSG3_SPECIAL_FLAG");
            Handshake.Auto = false;
            EnvNotes.Add($"MSG3_SPECIAL_FLAG={Handshake.SpecialFlag}");
        }

        string? skipWelcome = Env("SKIP_WELCOME_PACK");
        if (!string.IsNullOrWhiteSpace(skipWelcome))
        {
            Handshake.SkipWelcomePack = ParseBool(skipWelcome, "SKIP_WELCOME_PACK");
            EnvNotes.Add($"SKIP_WELCOME_PACK={Handshake.SkipWelcomePack}");
        }

        string? msg3Hex = Env("MSG3_HEX");
        if (!string.IsNullOrWhiteSpace(msg3Hex))
        {
            Handshake.Msg3Hex = msg3Hex.Trim();
            EnvNotes.Add($"MSG3_HEX={Handshake.Msg3Hex}");
        }

        string? welcomeHex = Env("WELCOME_HEX");
        if (!string.IsNullOrWhiteSpace(welcomeHex))
        {
            Handshake.WelcomeHex = welcomeHex.Trim();
            EnvNotes.Add($"WELCOME_HEX={Handshake.WelcomeHex}");
        }

        // 便捷开关：一个变量直接选形态。
        string? variant = Env("MSG3_VARIANT");
        if (!string.IsNullOrWhiteSpace(variant))
        {
            switch (variant.Trim().ToLowerInvariant())
            {
                case "auto":
                    Handshake.Auto = true;
                    break;
                case "flags1":
                    Handshake.Auto = false;
                    Handshake.Layout = "flags";
                    Handshake.SpecialFlag = true;
                    break;
                case "flags0":
                    Handshake.Auto = false;
                    Handshake.Layout = "flags";
                    Handshake.SpecialFlag = false;
                    break;
                case "slot":
                    Handshake.Auto = false;
                    Handshake.Layout = "slot";
                    break;
                default:
                    throw new InvalidDataException($"MSG3_VARIANT 只接受 auto/flags1/flags0/slot，当前: {variant}");
            }
            EnvNotes.Add($"MSG3_VARIANT={variant.Trim().ToLowerInvariant()}");
        }
    }

    /// <summary>被环境变量覆盖过的项，只记名字与安全的值，便于在日志里自证。</summary>
    public List<string> EnvNotes { get; } = new();

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    private static string? FirstEnv(params string[] keys)
    {
        foreach (string key in keys)
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static void ValidateHex(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        string clean = text.Replace(" ", "").Replace("-", "");
        if (clean.Length % 2 != 0 || !clean.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{name} 必须是偶数长度的十六进制，当前: {text}");
    }

    private static bool ParseBool(string text, string name) => text.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => throw new InvalidDataException($"{name} 只接受 true/false/1/0，当前: {text}"),
    };

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

        string variant = Handshake.Layout.Trim().ToLowerInvariant();
        if (variant is not ("flags" or "slot"))
            throw new InvalidDataException($"handshake.layout 只能是 flags / slot，当前: {Handshake.Layout}");
        Handshake.Layout = variant;

        ValidateHex(Handshake.Msg3Hex, "handshake.msg3Hex");
        ValidateHex(Handshake.WelcomeHex, "handshake.welcomeHex");
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
/// <summary>
/// 消息 3 的形态。两个独立维度——之前被错并成一个开关，导致 bool 永远是 0：
///   layout      —— 负载 2 字节（槽位 + bool）还是 1 字节（只有槽位）
///   specialFlag —— 那个 bool 的**值**（true 写 0x01，false 写 0x00）
/// </summary>
public sealed class HandshakeSection
{
    /// <summary>"flags" = 2 字节（1.4.5）；"slot" = 1 字节（1.4.4 / 可能的移动端）。</summary>
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "flags";

    /// <summary>bool 的值。true 发 0x01，false 发 0x00。</summary>
    [JsonPropertyName("specialFlag")]
    public bool SpecialFlag { get; set; } = true;

    /// <summary>
    /// true 时每次连接轮换一种形态，一次现场测试就能把三种组合试完：
    /// 第 1 次 flags+true，第 2 次 slot，第 3 次 flags+false，之后循环。
    /// 日志里的 msg3 字段会写明本次用的是哪种。
    /// </summary>
    [JsonPropertyName("auto")]
    public bool Auto { get; set; } = true;

    /// <summary>
    /// 隔离测试用：为 true 时，发出消息 3 之后**不再发送任何包**（含欢迎包和世界头），保持静默。
    /// 用来单独判断「客户端是否只凭消息 3 就继续」。
    /// </summary>
    [JsonPropertyName("skipWelcomePack")]
    public bool SkipWelcomePack { get; set; }

    /// <summary>
    /// 覆盖消息 3 的负载（十六进制，不含帧头）。留空则按 layout/specialFlag 生成。
    /// 例如 "0100" 或 "01000000"。
    /// </summary>
    [JsonPropertyName("msg3Hex")]
    public string Msg3Hex { get; set; } = "";

    /// <summary>
    /// 覆盖欢迎包的整个 82 负载（十六进制，含模块号）。留空则用 7fb1c801。
    /// 例如 "7fb1c801"。
    /// </summary>
    [JsonPropertyName("welcomeHex")]
    public string WelcomeHex { get; set; } = "";
}
