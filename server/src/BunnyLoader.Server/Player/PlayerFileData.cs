namespace BunnyLoader.Server.Player;

/// <summary>一个 3 字节 RGB 颜色（Terraria 存档里颜色就是 3 字节，不是 4）。</summary>
public readonly struct PlayerColor
{
    public PlayerColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public bool Equals3(ReadOnlySpan<byte> raw) =>
        raw.Length >= 3 && raw[0] == R && raw[1] == G && raw[2] == B;

    public override string ToString() => $"{Hex}({R},{G},{B})";
}

/// <summary>物品栏槽位。空槽 id=0，但**也必须占满字节**。</summary>
public sealed class PlayerItem
{
    public int Id { get; init; }
    public int Count { get; init; }
    public int Prefix { get; init; }
    public bool Favorite { get; init; }

    public bool IsEmpty => Id == 0;

    public override string ToString() =>
        IsEmpty ? "空" : $"id={Id} count={Count} prefix={Prefix}{(Favorite ? " 收藏" : "")}";
}

/// <summary>一个已解析字段：名字、在文件里的偏移、字节长度、值、是否已确认。</summary>
public sealed class PlrField
{
    public string Name { get; init; } = "";
    public int Offset { get; init; }
    public int Length { get; init; }
    public string Value { get; init; } = "";
    public bool Confirmed { get; init; } = true;
}

/// <summary>
/// .plr 解析结果（只读）。字段顺序按「文件里的实际字节顺序」记录，
/// 每个字段都带偏移与长度，方便人类逐字节核对。
/// </summary>
public sealed class PlayerFileData
{
    // ---- 文件级 ----
    public string FilePath { get; set; } = "";
    public long FileLength { get; set; }
    public string Head32Hex { get; set; } = "-";
    public bool Encrypted { get; set; }
    public string EncryptionNote { get; set; } = "-";
    public string DecryptedHead32Hex { get; set; } = "-";

    // ---- 头部 ----
    public int Version { get; set; }
    public string Magic { get; set; } = "";
    public int FileType { get; set; }
    public uint Revision { get; set; }
    public ulong Favorite { get; set; }

    // ---- 角色 ----
    public string Name { get; set; } = "";
    public int Difficulty { get; set; }
    public long PlayTimeTicks { get; set; }
    public int Hair { get; set; }
    public int HairDye { get; set; }
    public int Team { get; set; }
    public int[] HideVisibleAccessory { get; set; } = Array.Empty<int>();
    public int HideMisc { get; set; }
    public int SkinVariant { get; set; } = -1;

    public int Health { get; set; }
    public int HealthMax { get; set; }
    public int Mana { get; set; }
    public int ManaMax { get; set; }

    public bool ExtraAccessory { get; set; }
    public bool UnlockedBiomeTorches { get; set; }
    public bool UsingBiomeTorches { get; set; }
    public bool AteArtisanBread { get; set; }
    public bool[] PermanentBoosts { get; set; } = new bool[6];
    public bool DownedDd2 { get; set; }
    public int TaxMoney { get; set; }
    public int DeathsPve { get; set; }
    public int DeathsPvp { get; set; }

    // ---- 7 个颜色（顺序与 Terraria 一致）----
    public PlayerColor HairColor { get; set; }
    public PlayerColor SkinColor { get; set; }
    public PlayerColor EyeColor { get; set; }
    public PlayerColor ShirtColor { get; set; }
    public PlayerColor UnderShirtColor { get; set; }
    public PlayerColor PantsColor { get; set; }
    public PlayerColor ShoeColor { get; set; }

    /// <summary>7 个颜色按 id:4 里的顺序排列，方便和 payload 逐字节对照。</summary>
    public (string Name, PlayerColor Color)[] ColorsInPacketOrder => new[]
    {
        ("hairColor", HairColor),
        ("skinColor", SkinColor),
        ("eyeColor", EyeColor),
        ("shirtColor", ShirtColor),
        ("underShirtColor", UnderShirtColor),
        ("pantsColor", PantsColor),
        ("shoeColor", ShoeColor),
    };

    // ---- 物品 ----
    public List<PlayerItem> Equipment { get; } = new();
    public List<PlayerItem> Dye { get; } = new();
    public List<PlayerItem> Inventory { get; } = new();
    public List<PlayerItem> Coins { get; } = new();
    public List<PlayerItem> Ammo { get; } = new();
    public List<(PlayerItem Equip, PlayerItem Dye)> Misc { get; } = new();
    public List<PlayerItem> PiggyBank { get; } = new();
    public List<PlayerItem> Safe { get; } = new();
    public List<PlayerItem> DefendersForge { get; } = new();
    public List<PlayerItem> VoidBag { get; } = new();

    // ---- 尾部 ----
    public int ParsedOffset { get; set; }
    public int PlaintextLength { get; set; }
    public string TrailingHex { get; set; } = "";

    /// <summary>文件里每个字段的偏移/长度/值（按出现顺序）。</summary>
    public List<PlrField> Fields { get; } = new();

    public int NonEmptyInventorySlots => Inventory.Count(i => !i.IsEmpty);

    public PlrField? FindField(string name)
    {
        foreach (PlrField field in Fields)
        {
            if (field.Name == name)
                return field;
        }

        return null;
    }
}
