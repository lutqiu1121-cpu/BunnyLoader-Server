using System.Buffers.Binary;
using System.Text;

namespace BunnyLoader.Server.Player;

/// <summary>
/// 一个玩家的服务端权威档案（SSC 的数据源）。
///
/// 工作方式：把 `.plr` **解密后的明文**留在内存里当可写文档，改动只在明文上按字段偏移打补丁，
/// 保存时重新 AES 加密写回。**不重新构造 .plr**（那是另一张网），所以任何未建模的尾部字节都原样保留。
///
/// 依赖 <see cref="PlayerFileReader.Parse"/> 记录的字段偏移（<see cref="PlayerFileData.Fields"/>），
/// 名字字段长度可变：先替换名字，记下长度差 delta，后续偏移统一 +delta。
/// </summary>
public sealed class PlayerProfile
{
    private byte[] _plain;

    public string Name { get; private set; }
    public string Path { get; }
    public PlayerFileData Data { get; private set; }
    public bool IsNew { get; }

    /// <summary>开荒装备：铜短剑 / 铜镐 / 铜斧（原版新角色就是这三样）。</summary>
    public static readonly (int Id, int Count)[] FreshGear = { (3507, 1), (3509, 1), (3506, 1) };

    private PlayerProfile(string path, byte[] plain, PlayerFileData data, bool isNew)
    {
        Path = path;
        _plain = plain;
        Data = data;
        Name = data.Name;
        IsNew = isNew;
    }

    /// <summary>打开已有 .plr（自动识别明文/密文）。</summary>
    public static PlayerProfile Open(string path)
    {
        PlayerFileData data = PlayerFileReader.ReadFromFile(path);
        byte[] raw = File.ReadAllBytes(path);
        byte[] plain = data.Encrypted ? PlayerFileReader.DecryptAesCbc(raw) : raw;

        return new PlayerProfile(path, plain, data, isNew: false);
    }

    /// <summary>
    /// 用模板明文造一个新角色：换名字、100 生命 / 20 魔力、清空全部物品、发铜短剑/镐/斧。
    /// </summary>
    public static PlayerProfile CreateFresh(string path, byte[] templatePlain, string name)
    {
        byte[] plain = (byte[])templatePlain.Clone();

        // 先解析一次拿到偏移（此时名字还是模板里的）
        var data = new PlayerFileData { FilePath = path };
        PlayerFileReader.Parse(plain, data);

        var profile = new PlayerProfile(path, plain, data, isNew: true);
        profile.SetName(name);
        profile.SetLife(100, 100);
        profile.SetMana(20, 20);
        profile.ClearAllItems();
        profile.GiveFreshGear();

        return profile;
    }

    // ------------------------------------------------------------------
    // 补丁
    // ------------------------------------------------------------------

    private PlrField? Field(string name) =>
        Data.Fields.FirstOrDefault(x => x.Name == name);

    /// <summary>替换名字（7bit 长度前缀 + UTF-8）。长度变化会整体平移后续字段。</summary>
    public void SetName(string name)
    {
        PlrField? f = Field("name");
        if (f is null)
            throw new InvalidDataException(".plr 里找不到 name 字段，无法改名");

        byte[] bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > 126)
            throw new InvalidDataException($"玩家名过长（{bytes.Length} 字节）：{name}");

        byte[] span = new byte[1 + bytes.Length];
        span[0] = (byte)bytes.Length;               // 7bit LEB128，<128 就是单字节
        bytes.CopyTo(span, 1);

        _plain = Splice(_plain, f.Offset, f.Length, span);
        Data = Reparse();
        Name = name;
    }

    private void SetI32(string fieldName, int value, int delta = 0)
    {
        PlrField? f = Field(fieldName);
        if (f is null || f.Length != 4)
            return;

        BinaryPrimitives.WriteInt32LittleEndian(_plain.AsSpan(f.Offset + delta, 4), value);
    }

    /// <summary>生命（health / healthMax，均为 i32）。</summary>
    public void SetLife(int life, int lifeMax)
    {
        SetI32("health", life);
        SetI32("healthMax", lifeMax);
        Data = Reparse();
    }

    /// <summary>魔力（mana / manaMax，均为 i32）。</summary>
    public void SetMana(int mana, int manaMax)
    {
        SetI32("mana", mana);
        SetI32("manaMax", manaMax);
        Data = Reparse();
    }

    /// <summary>把某个物品区清零（按字段偏移 + 每槽步长）。</summary>
    public void ClearItems(string fieldName, int slotCount, int stride)
    {
        PlrField? f = Field(fieldName);
        if (f is null || stride <= 0)
            return;

        for (int i = 0; i < slotCount; i++)
        {
            int at = f.Offset + i * stride;
            if (at + stride > _plain.Length)
                break;
            _plain.AsSpan(at, stride).Clear();
        }
    }

    /// <summary>清空背包/钱币/弹药/装备/染料/饰品与猪猪罐等容器（开荒状态）。</summary>
    public void ClearAllItems()
    {
        // 每槽字节数：i32 id + i32 count + u8 prefix (+ bool favorite)
        ClearItems("inventory", 50, 10);
        ClearItems("coins", 4, 10);
        ClearItems("ammo", 4, 10);
        ClearItems("equipment", 20, 5);   // i32 id + u8 prefix
        ClearItems("dye", 10, 5);
        ClearItems("misc(5×装备+染料)", 5, 10);  // 每组 = 装备 5 + 染料 5
        ClearItems("piggyBank", 40, 9);
        ClearItems("safe", 40, 9);
        ClearItems("defendersForge", 40, 9);
        Data = Reparse();
    }

    /// <summary>给开荒三件套：背包 0..2 槽 = 铜短剑 / 铜镐 / 铜斧。</summary>
    public void GiveFreshGear()
    {
        for (int i = 0; i < FreshGear.Length; i++)
            SetSlot(i, FreshGear[i].Id, FreshGear[i].Count, 0);

        Data = Reparse();
    }

    /// <summary>写某个背包槽（0..49）：i32 id + i32 count + u8 prefix + bool favorite。</summary>
    public void SetSlot(int slot, int itemId, int count, int prefix)
    {
        PlrField? f = Field("inventory");
        if (f is null || slot < 0 || slot >= 50)
            return;

        int stride = f.Length / 50;
        if (stride < 10)
            return;

        int at = f.Offset + slot * stride;
        if (at + 10 > _plain.Length)
            return;

        BinaryPrimitives.WriteInt32LittleEndian(_plain.AsSpan(at, 4), itemId);
        BinaryPrimitives.WriteInt32LittleEndian(_plain.AsSpan(at + 4, 4), count);
        _plain[at + 8] = (byte)prefix;
        _plain[at + 9] = 0;
    }

    private PlayerFileData Reparse()
    {
        var data = new PlayerFileData { FilePath = Path };
        PlayerFileReader.Parse(_plain, data);
        data.FilePath = Path;
        return data;
    }

    /// <summary>补零到 AES 块边界（16 字节）。</summary>
    private static byte[] PadToBlock(byte[] plain)
    {
        int pad = (16 - plain.Length % 16) % 16;
        if (pad == 0)
            return plain;

        byte[] padded = new byte[plain.Length + pad];
        plain.CopyTo(padded, 0);
        return padded;
    }

    private static byte[] Splice(byte[] source, int offset, int removeLength, byte[] insert)
    {
        byte[] result = new byte[source.Length - removeLength + insert.Length];
        source.AsSpan(0, offset).CopyTo(result);
        insert.CopyTo(result.AsSpan(offset));
        source.AsSpan(offset + removeLength).CopyTo(result.AsSpan(offset + insert.Length));
        return result;
    }

    /// <summary>
    /// 保存（AES 加密 → 临时文件 → 回读校验 → 原子替换）。
    ///
    /// 注意：改名会让明文长度变化，而 AES-CBC 无填充要求长度是 16 的倍数，
    /// 所以落盘前补零到 16 字节边界（读取端本来就容忍尾部未建模字节）。
    /// </summary>
    public void Save()
    {
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        byte[] cipher = PlayerFileReader.EncryptAesCbc(PadToBlock(_plain));
        string tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, cipher);

        // 复用已验证的解密/解析做一次回读，坏档不落盘
        PlayerFileData check = PlayerFileReader.ReadFromFile(tmp);
        if (!string.Equals(check.Name, Name, StringComparison.Ordinal))
            throw new InvalidDataException($"回读校验失败：{check.Name} != {Name}");

        File.Move(tmp, Path, overwrite: true);
    }

    public int Health => Data.Health;
    public int HealthMax => Data.HealthMax;
    public int Mana => Data.Mana;
    public int ManaMax => Data.ManaMax;
}