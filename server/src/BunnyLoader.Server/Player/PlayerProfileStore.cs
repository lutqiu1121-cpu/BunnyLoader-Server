namespace BunnyLoader.Server.Player;

/// <summary>
/// 玩家档案仓库（真正的动态 SSC）：
///   - 按玩家名找 `Players/&lt;名字&gt;.plr`；
///   - 有就加载，没有就用模板造一个开荒档并立刻落盘；
///   - 内存里缓存，退出时保存。
/// 外部模板是可选的；找不到时使用代码内置合法 v319 默认档案。
/// </summary>
public sealed class PlayerProfileStore
{
    private readonly string _dir;
    private readonly byte[]? _templatePlain;
    private readonly string _templatePath;
    private readonly JsonLogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, PlayerProfile> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string DirectoryPath => _dir;
    public string TemplatePath => _templatePath;

    private PlayerProfileStore(string dir, byte[]? templatePlain, string templatePath, JsonLogger log)
    {
        _dir = dir;
        _templatePlain = templatePlain;
        _templatePath = templatePath;
        _log = log;
    }

    /// <summary>
    /// 建仓库：档案目录默认 `&lt;程序目录&gt;/Players`，模板按 `Players/.template.plr` →
    /// `debug/Cup.plr` → `player.plr` → `debug/*.plr` 找。
    /// </summary>
    public static PlayerProfileStore? TryCreate(string? directory, JsonLogger log)
    {
        string[] searchDirs = new[]
        {
            AppContext.BaseDirectory,
            System.IO.Directory.GetCurrentDirectory(),
        }.Distinct(StringComparer.Ordinal).ToArray();

        string dir = !string.IsNullOrWhiteSpace(directory)
            ? directory
            : System.IO.Path.Combine(searchDirs[0], "Players");

        byte[]? template = null;
        string templatePath = "";

        var candidates = new List<string>();
        candidates.Add(System.IO.Path.Combine(dir, ".template.plr"));
        foreach (string baseDir in searchDirs)
        {
            candidates.Add(System.IO.Path.Combine(baseDir, "player.plr"));
            candidates.Add(System.IO.Path.Combine(baseDir, "debug", "Cup.plr"));
            string debugDir = System.IO.Path.Combine(baseDir, "debug");
            if (System.IO.Directory.Exists(debugDir))
                candidates.AddRange(System.IO.Directory.GetFiles(debugDir, "*.plr"));
        }

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                PlayerFileData data = PlayerFileReader.ReadFromFile(candidate);
                byte[] raw = File.ReadAllBytes(candidate);
                template = data.Encrypted ? PlayerFileReader.DecryptAesCbc(raw) : raw;
                templatePath = candidate;
                break;
            }
            catch (Exception e)
            {
                log.Warn("ssc.template.skip",
                    ("file", candidate),
                    ("error", e.GetType().Name + ": " + e.Message));
            }
        }

        if (template is null)
        {
            // R6 兜底：外部模板可选。代码内置合法 v319 明文结构，CreateFresh 会把它改成
            // 100/100、20/20、空容器 + 3507/3509/3506，并立即保存为玩家档案。
            template = PlrCommand.CreateBuiltInDefaultPlaintext();
            templatePath = "<built-in-default-v319>";
            log.Warn("ssc.template.builtin",
                ("dir", dir),
                ("template", templatePath),
                ("note", "外部 .plr 模板不存在，使用代码内置默认档案"));
        }

        try
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            log.Error("ssc.store.fail", ("dir", dir), ("error", e.Message));
            return null;
        }

        return new PlayerProfileStore(dir, template, templatePath, log);
    }

    /// <summary>按名字取档案：有就加载，没有就造一个开荒档并保存。</summary>
    public PlayerProfile? LoadOrCreate(string name)
    {
        string clean = Sanitize(name);
        if (clean.Length == 0)
        {
            _log.Warn("ssc.profile.badname", ("raw", name), ("note", "空名字，无法建档案"));
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(clean, out PlayerProfile? cached))
                return cached;

            string path = System.IO.Path.Combine(_dir, clean + ".plr");

            PlayerProfile? profile = null;

            if (File.Exists(path))
            {
                try
                {
                    profile = PlayerProfile.Open(path);
                    _log.Info("ssc.profile.loaded",
                        ("name", profile.Name),
                        ("file", path),
                        ("life", $"{profile.Health}/{profile.HealthMax}"),
                        ("mana", $"{profile.Mana}/{profile.ManaMax}"),
                        ("items", profile.Data.Inventory.Count(i => !i.IsEmpty)));
                }
                catch (Exception e)
                {
                    _log.Error("ssc.profile.loadfail",
                        ("file", path),
                        ("error", e.GetType().Name + ": " + e.Message),
                        ("note", "坏档不覆盖，本次改用开荒档（内存），退出时不会写回"));
                }
            }

            if (profile is null)
            {
                try
                {
                    profile = PlayerProfile.CreateFresh(path, _templatePlain!, clean);
                    profile.Save();
                    _log.Info("ssc.profile.created",
                        ("name", clean),
                        ("file", path),
                        ("template", _templatePath),
                        ("life", $"{profile.Health}/{profile.HealthMax}"),
                        ("mana", $"{profile.Mana}/{profile.ManaMax}"),
                        ("gear", string.Join("/", PlayerProfile.FreshGear.Select(g => g.Id))));
                }
                catch (Exception e)
                {
                    _log.Error("ssc.profile.createfail",
                        ("name", clean),
                        ("error", e.GetType().Name + ": " + e.Message));
                    return null;
                }
            }

            _cache[clean] = profile;
            return profile;
        }
    }

    /// <summary>把缓存的档案写回磁盘（退出时调用）。</summary>
    public void SaveProfile(PlayerProfile profile)
    {
        lock (_gate)
        {
            try
            {
                profile.Save();
                _log.Info("ssc.profile.saved",
                    ("name", profile.Name),
                    ("file", profile.Path),
                    ("life", $"{profile.Health}/{profile.HealthMax}"),
                    ("mana", $"{profile.Mana}/{profile.ManaMax}"),
                    ("items", profile.Data.Inventory.Count(i => !i.IsEmpty)));
            }
            catch (Exception e)
            {
                _log.Error("ssc.profile.savefail",
                    ("name", profile.Name),
                    ("file", profile.Path),
                    ("error", e.GetType().Name + ": " + e.Message));
            }
        }
    }

    /// <summary>文件名安全化：去掉路径分隔符与 Windows 非法字符，挡掉 `..`。</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            bool bad = c < 0x20
                || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || c == System.IO.Path.DirectorySeparatorChar
                || c == System.IO.Path.AltDirectorySeparatorChar;

            sb.Append(bad ? '_' : c);
        }

        string clean = sb.ToString().Trim().Trim('.');

        // 保留名与空名一律拒绝
        if (clean.Length == 0 || clean is "." or "..")
            return "";

        return clean.Length > 40 ? clean[..40] : clean;
    }
}