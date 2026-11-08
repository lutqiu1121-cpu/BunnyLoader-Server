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

    [JsonPropertyName("proof")]
    public ProofSection Proof { get; set; } = new();

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

        string? proofMode = Env("PROOF_MODE");
        if (!string.IsNullOrWhiteSpace(proofMode))
        {
            Proof.Mode = proofMode.Trim().ToLowerInvariant();
            EnvNotes.Add($"PROOF_MODE={Proof.Mode}");
        }

        string? proofSuffix = Env("PROOF_SUFFIX");
        if (!string.IsNullOrWhiteSpace(proofSuffix))
        {
            Proof.Suffix = proofSuffix.Trim().ToLowerInvariant();
            EnvNotes.Add($"PROOF_SUFFIX={Proof.Suffix}");
        }

        string? proofAfterHello = Env("PROOF_AFTER_HELLO");
        if (!string.IsNullOrWhiteSpace(proofAfterHello))
        {
            Proof.Timing = ParseBool(proofAfterHello, "PROOF_AFTER_HELLO") ? "after-hello" : "before";
            EnvNotes.Add($"PROOF_AFTER_HELLO={Proof.Timing}");
        }

        string? proofWithLength = Env("PROOF_WITH_LENGTH");
        if (!string.IsNullOrWhiteSpace(proofWithLength))
        {
            Proof.Framing = ParseBool(proofWithLength, "PROOF_WITH_LENGTH") ? "frame" : "raw";
            EnvNotes.Add($"PROOF_WITH_LENGTH={Proof.Framing}");
        }

        string? proofNetModule = Env("PROOF_IN_NETMODULE");
        if (!string.IsNullOrWhiteSpace(proofNetModule))
        {
            Proof.Wrapper = ParseBool(proofNetModule, "PROOF_IN_NETMODULE") ? "netmodule" : "plain";
            EnvNotes.Add($"PROOF_IN_NETMODULE={Proof.Wrapper}");
        }

        string? proofKind = Env("PROOF_NETMODULE_KIND");
        if (!string.IsNullOrWhiteSpace(proofKind))
        {
            if (!int.TryParse(proofKind.Trim(), out int kind))
                throw new InvalidDataException($"PROOF_NETMODULE_KIND 不是整数: {proofKind}");
            Proof.NetModuleKind = kind;
            EnvNotes.Add($"PROOF_NETMODULE_KIND={kind}");
        }

        string? worldOnHello = Env("WORLD_ON_HELLO");
        if (!string.IsNullOrWhiteSpace(worldOnHello))
        {
            Handshake.WorldOnHello = worldOnHello.Trim().ToLowerInvariant();
            EnvNotes.Add($"WORLD_ON_HELLO={Handshake.WorldOnHello}");
        }

        string? noWelcome = Env("NO_WELCOME");
        if (!string.IsNullOrWhiteSpace(noWelcome))
        {
            Handshake.NoWelcome = ParseBool(noWelcome, "NO_WELCOME");
            EnvNotes.Add($"NO_WELCOME={Handshake.NoWelcome}");
        }

        string? proofWaitReply = Env("PROOF_WAIT_REPLY");
        if (!string.IsNullOrWhiteSpace(proofWaitReply))
        {
            Proof.WaitReply = ParseBool(proofWaitReply, "PROOF_WAIT_REPLY");
            EnvNotes.Add($"PROOF_WAIT_REPLY={Proof.WaitReply}");
        }

        string? proofSweep = Env("PROOF_SWEEP");
        if (!string.IsNullOrWhiteSpace(proofSweep))
        {
            Proof.Sweep = ParseBool(proofSweep, "PROOF_SWEEP");
            EnvNotes.Add($"PROOF_SWEEP={Proof.Sweep}");
        }

        string? proofNetModuleId = Env("PROOF_NETMODULE_ID");
        if (!string.IsNullOrWhiteSpace(proofNetModuleId))
        {
            if (!int.TryParse(proofNetModuleId.Trim(), out int nmId))
                throw new InvalidDataException($"PROOF_NETMODULE_ID 不是整数: {proofNetModuleId}");
            Proof.NetModuleId = nmId;
            EnvNotes.Add($"PROOF_NETMODULE_ID={nmId}");
        }

        string? proofAsText = Env("PROOF_AS_TEXT");
        if (!string.IsNullOrWhiteSpace(proofAsText) && ParseBool(proofAsText, "PROOF_AS_TEXT"))
        {
            // 预设：封进 id:82、模块 0（NetTextModule）、kind=1
            Proof.Wrapper = "netmodule";
            Proof.NetModuleId = 0;
            Proof.NetModuleKind = 1;
            EnvNotes.Add("PROOF_AS_TEXT=netmodule(0,kind=1)");
        }

        string? proofMessageId = Env("PROOF_MESSAGE_ID");
        if (!string.IsNullOrWhiteSpace(proofMessageId))
        {
            if (!int.TryParse(proofMessageId.Trim(), out int mid))
                throw new InvalidDataException($"PROOF_MESSAGE_ID 不是整数: {proofMessageId}");
            Proof.MessageId = mid;
            EnvNotes.Add($"PROOF_MESSAGE_ID={mid}");
        }

        string? proofTimeout = Env("PROOF_TIMEOUT");
        if (!string.IsNullOrWhiteSpace(proofTimeout))
        {
            if (!int.TryParse(proofTimeout.Trim(), out int t))
                throw new InvalidDataException($"PROOF_TIMEOUT 不是整数: {proofTimeout}");
            Proof.TimeoutSeconds = t;
            EnvNotes.Add($"PROOF_TIMEOUT={t}");
        }

        string? worldFile = Env("WORLD_FILE");
        if (!string.IsNullOrWhiteSpace(worldFile))
        {
            World.File = worldFile.Trim();
            EnvNotes.Add("WORLD_FILE=<set>");
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

        string? candidates = Env("MSG3_CANDIDATES");
        if (!string.IsNullOrWhiteSpace(candidates))
        {
            Handshake.Candidates = candidates.Trim().ToLowerInvariant();
            EnvNotes.Add($"MSG3_CANDIDATES={Handshake.Candidates}");
        }

        string? skipApproved = Env("SKIP_APPROVED");
        if (!string.IsNullOrWhiteSpace(skipApproved))
        {
            Handshake.SkipApproved = ParseBool(skipApproved, "SKIP_APPROVED");
            EnvNotes.Add($"SKIP_APPROVED={Handshake.SkipApproved}");
        }

        string? sendMsg7 = Env("SEND_MSG7_AFTER_3");
        if (!string.IsNullOrWhiteSpace(sendMsg7))
        {
            Handshake.SendMsg7After3 = ParseBool(sendMsg7, "SEND_MSG7_AFTER_3");
            EnvNotes.Add($"SEND_MSG7_AFTER_3={Handshake.SendMsg7After3}");
        }

        string? msg7Welcome = Env("MSG7_INCLUDE_WELCOME");
        if (!string.IsNullOrWhiteSpace(msg7Welcome))
        {
            Handshake.Msg7IncludeWelcome = ParseBool(msg7Welcome, "MSG7_INCLUDE_WELCOME");
            EnvNotes.Add($"MSG7_INCLUDE_WELCOME={Handshake.Msg7IncludeWelcome}");
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

        // 出生点若在地表以上，玩家会悬空下坠。贴到地表上一格（站着而不是埋进地里）。
        // 有 .wld 时这些值会被文件覆盖，这里只对平坦世界生效。
        if (string.IsNullOrWhiteSpace(World.File) && World.SpawnTileY < World.WorldSurface)
            World.SpawnTileY = (short)(World.WorldSurface - 1);

        string variant = Handshake.Layout.Trim().ToLowerInvariant();
        if (variant is not ("flags" or "slot"))
            throw new InvalidDataException($"handshake.layout 只能是 flags / slot，当前: {Handshake.Layout}");
        Handshake.Layout = variant;

        string proofModeValue = Proof.Mode.Trim().ToLowerInvariant();
        if (proofModeValue is not ("auto" or "on" or "off"))
            throw new InvalidDataException($"proof.mode 只能是 auto/on/off，当前: {Proof.Mode}");
        Proof.Mode = proofModeValue;

        string suffix = Proof.Suffix.Trim().ToLowerInvariant();
        if (suffix is not ("lf" or "crlf" or "none"))
            throw new InvalidDataException($"proof.suffix 只能是 lf/crlf/none，当前: {Proof.Suffix}");
        Proof.Suffix = suffix;

        string timing = Proof.Timing.Trim().ToLowerInvariant();
        if (timing is not ("before" or "after-hello"))
            throw new InvalidDataException($"proof.timing 只能是 before / after-hello，当前: {Proof.Timing}");
        Proof.Timing = timing;

        string framing = Proof.Framing.Trim().ToLowerInvariant();
        if (framing is not ("raw" or "frame"))
            throw new InvalidDataException($"proof.framing 只能是 raw / frame，当前: {Proof.Framing}");
        Proof.Framing = framing;

        string wrapper = Proof.Wrapper.Trim().ToLowerInvariant();
        if (wrapper is not ("plain" or "netmodule"))
            throw new InvalidDataException($"proof.wrapper 只能是 plain / netmodule，当前: {Proof.Wrapper}");
        Proof.Wrapper = wrapper;

        if (Proof.NetModuleKind is < 0 or > 255)
            throw new InvalidDataException($"proof.netModuleKind 应在 0..255，当前 {Proof.NetModuleKind}");

        if (Proof.NetModuleId is < 0 or > 65535)
            throw new InvalidDataException($"proof.netModuleId 应在 0..65535，当前 {Proof.NetModuleId}");

        if (Proof.MessageId is < 0 or > 255)
            throw new InvalidDataException($"proof.messageId 应在 0..255，当前 {Proof.MessageId}");

        if (Proof.TimeoutSeconds < 1 || Proof.TimeoutSeconds > 60)
            throw new InvalidDataException($"proof.timeoutSeconds 应在 1..60，当前 {Proof.TimeoutSeconds}");

        ValidateHex(Handshake.Msg3Hex, "handshake.msg3Hex");
        ValidateHex(Handshake.WelcomeHex, "handshake.welcomeHex");

        if (!string.IsNullOrWhiteSpace(Handshake.Candidates) && Handshake.Candidates != "auto")
        {
            foreach (string part in Handshake.Candidates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ValidateHex(part, "handshake.candidates");
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
    public short SpawnTileY { get; set; } = 349;

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

    /// <summary>
    /// 原版 .wld 路径。设置后世界尺寸/出生点/地表高度/瓦片全部以该文件为准，
    /// 手写的平坦世界不再使用。留空 = 用平坦世界。
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";
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

    /// <summary>
    /// 候选负载列表（逗号分隔的十六进制，不含帧头）。非空时按连接序号轮换，
    /// 每次连接试一个，免去反复重启面板。特殊值 "auto" 展开为内置候选表。
    /// </summary>
    [JsonPropertyName("candidates")]
    public string Candidates { get; set; } = "";

    /// <summary>
    /// 收到 id:1 之后是否主动发世界数据：
    ///   "full"（默认）= 7 + 9 + 分块 + 49 一股脑发；
    ///   "header"      = 只发 7；
    ///   "off"         = 等客户端发 6/8 再发。
    /// </summary>
    [JsonPropertyName("worldOnHello")]
    public string WorldOnHello { get; set; } = "full";

    /// <summary>
    /// true 时**完全不发欢迎包**（82/200）。用于隔离测试：确认是不是它把客户端撞掉的。
    /// 注意：关掉之后，客户端收到世界数据(7)时会自踢（ModNet.cpp:249-253）。
    /// </summary>
    [JsonPropertyName("noWelcome")]
    public bool NoWelcome { get; set; }

    /// <summary>控制组：为 true 时连接建立后什么都不发（连消息 3 也不发）。</summary>
    [JsonPropertyName("skipApproved")]
    public bool SkipApproved { get; set; }

    /// <summary>
    /// 为 true 时，在消息 3 之后**同一次写入里**紧接着发消息 7（世界头），
    /// 用于验证「客户端处理完 3 之后立刻需要 7」这个假设。
    /// </summary>
    [JsonPropertyName("sendMsg7After3")]
    public bool SendMsg7After3 { get; set; }

    /// <summary>
    /// SEND_MSG7_AFTER_3 时是否先补欢迎包（默认 true）。
    /// 必须为 true：BL 客户端收到「无欢迎包的消息 7」会主动自踢并显示
    /// "只能与柠版互通"，那会被误当成崩溃。（ModNet.cpp:249-253）
    /// </summary>
    [JsonPropertyName("msg7IncludeWelcome")]
    public bool Msg7IncludeWelcome { get; set; } = true;
}


/// <summary>
/// 二开版制作方提供的纯文本握手（"protocol 1"）。
/// 服务端连上后先发一行裸文本，客户端回一行；两行都对上才进入正常帧协议。
///
/// ⚠ 这段是**裸 TCP 文本**，不是泰拉瑞亚的帧格式。对不支持它的老客户端发送，
/// 会把它的解析器带偏（它会拿 'N','i' 当帧长）。所以默认 auto：先等一小会儿看
/// 客户端是不是直接发帧（老客户端），是就直接走帧协议、绝不发文本。
/// </summary>
public sealed class ProofSection
{
    /// <summary>auto = 先探测；on = 总是发；off = 完全不发。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "auto";

    [JsonPropertyName("serverLine")]
    public string ServerLine { get; set; } = "NingLauncher/1/server";

    [JsonPropertyName("clientLine")]
    public string ClientLine { get; set; } = "NingLauncher/1/client";

    /// <summary>行尾：lf = \n，crlf = \r\n，none = 不加。</summary>
    // 真机抓包确证：21 字节，不带换行
    [JsonPropertyName("suffix")]
    public string Suffix { get; set; } = "none";

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>"before" = 连上就发（默认）；"after-hello" = 收到 id:1 之后再发。</summary>
    [JsonPropertyName("timing")]
    public string Timing { get; set; } = "before";

    /// <summary>"raw" = 裸文本（默认）；"frame" = 加泰拉瑞亚 2 字节长度头。</summary>
    [JsonPropertyName("framing")]
    public string Framing { get; set; } = "raw";

    /// <summary>"plain" = 直接发文本（默认）；"netmodule" = 封进 id:82 的 NetModule。</summary>
    [JsonPropertyName("wrapper")]
    public string Wrapper { get; set; } = "netmodule";

    /// <summary>
    /// true 时发完 proof 会等客户端回执（默认 false）。
    /// 抓包证明客户端不会在明文层回执，所以默认不等：发完立刻发 id:3。
    /// </summary>
    [JsonPropertyName("waitReply")]
    public bool WaitReply { get; set; }

    /// <summary>
    /// true 时忽略上面各项，按连接序号轮换内置的 10 种组合，一次现场测试扫完。
    /// 日志 proof.variant 会写明本次用的是哪一种。
    /// </summary>
    [JsonPropertyName("sweep")]
    public bool Sweep { get; set; }

    /// <summary>wrapper=netmodule 时，模块号后是否再插一个 kind 字节；null = 不插。</summary>
    [JsonPropertyName("netModuleKind")]
    public int? NetModuleKind { get; set; } = 1;

    /// <summary>wrapper=netmodule 时使用的模块号，默认 0xB17F（BL 的 C# 模块）。</summary>
    [JsonPropertyName("netModuleId")]
    public int NetModuleId { get; set; } = 0xB17F;

    /// <summary>
    /// 直接把文本当作某个顶层消息的负载发出去（[u16][id][text]）。
    /// null = 不启用。
    /// </summary>
    [JsonPropertyName("messageId")]
    public int? MessageId { get; set; }
}
