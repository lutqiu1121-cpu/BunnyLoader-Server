using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BunnyLoader.Server.Player;

/// <summary>
/// 只读的 .plr 解析器（第一层：不接网络、不改任何下发逻辑）。
///
/// 文件格式（Terraria 1.4.x，v319 = 1.4.5.x）：
///   整个文件是 AES-128-CBC 密文（无填充），Key = IV = "h3y_gUyZ" 的 UTF-16LE 字节。
///   解密后：int32 版本 + "relogic" + byte 3 + ...（见 Parse 的顺序）。
///
/// 加密判断不靠猜：先尝试按明文解析（第 4..10 字节等于 "relogic"），
/// 不行再按 16 字节对齐解密一次，解密后头部对得上才认。
/// </summary>
public static class PlayerFileReader
{
    /// <summary>Terraria 的固定密钥：字符串 "h3y_gUyZ" 的 UTF-16LE 字节（16 字节，AES-128）。</summary>
    private static readonly byte[] TerrariaKey =
    {
        0x68, 0x00, 0x33, 0x00, 0x79, 0x00, 0x5F, 0x00,
        0x67, 0x00, 0x55, 0x00, 0x79, 0x00, 0x5A, 0x00,
    };

    private const string Magic = "relogic";
    private const int FileTypePlayer = 3;

    public static PlayerFileData ReadFromFile(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var data = new PlayerFileData
        {
            FilePath = Path.GetFullPath(path),
            FileLength = raw.Length,
            Head32Hex = Hex32(raw),
        };

        byte[] plaintext;
        if (LooksLikePlaintext(raw))
        {
            plaintext = raw;
            data.Encrypted = false;
            data.EncryptionNote = $"明文：第 4..10 字节为 \"relogic\"（{Encoding.ASCII.GetString(raw, 4, 7)}），未加密";
        }
        else
        {
            if (raw.Length % 16 != 0)
                throw new InvalidDataException(
                    $"既不是明文（头部没有 relogic），长度 {raw.Length} 也不是 16 的倍数，无法解密");

            plaintext = DecryptAesCbc(raw);
            if (!LooksLikePlaintext(plaintext))
                throw new InvalidDataException(
                    "AES-128-CBC 解密后头部仍不是 relogic —— 密钥/模式假设不成立，停止解析（不猜字段）");

            data.Encrypted = true;
            data.EncryptionNote =
                "加密：AES-128-CBC，Key=IV=\"h3y_gUyZ\" 的 UTF-16LE 字节，无填充；"
                + $"解密后第 4..10 字节为 \"relogic\"（{Encoding.ASCII.GetString(plaintext, 4, 7)}），判定成立";
        }

        data.DecryptedHead32Hex = Hex32(plaintext);
        Parse(plaintext, data);
        return data;
    }

    /// <summary>直接把已解密的明文字节解析进 data（自检用）。</summary>
    public static void Parse(byte[] plaintext, PlayerFileData data)
    {
        var r = new PlrReader(plaintext);
        List<PlrField> f = data.Fields;

        data.PlaintextLength = plaintext.Length;

        data.Version = Field(r, f, "version", x => x.I32());
        data.Magic = Field(r, f, "magic", x => x.Ascii(7));
        if (data.Magic != Magic)
            throw new InvalidDataException($"头部 magic 不是 \"{Magic}\"：{data.Magic}");

        data.FileType = Field(r, f, "fileType", x => x.U8());
        if (data.FileType != FileTypePlayer)
            throw new InvalidDataException($"fileType 应为 {FileTypePlayer}（玩家），实际 {data.FileType}");

        data.Revision = Field(r, f, "revision", x => x.U32());
        data.Favorite = Field(r, f, "favorite", x => x.U64());

        data.Name = Field(r, f, "name", x => x.String7Bit());

        // difficulty：v17+ 是 byte；v10..16 是 bool（true=硬核）
        if (data.Version >= 17)
            data.Difficulty = Field(r, f, "difficulty", x => x.U8());
        else if (data.Version >= 10)
            data.Difficulty = Field(r, f, "hardcore(bool)", x => x.Bool()) ? 2 : 0;

        if (data.Version >= 138)
            data.PlayTimeTicks = Field(r, f, "playTimeTicks", x => x.I64());

        data.Hair = Field(r, f, "hair", x => x.I32());

        if (data.Version >= 82)
            data.HairDye = Field(r, f, "hairDye", x => x.U8());

        if (data.Version >= 283)
            data.Team = Field(r, f, "team", x => x.U8());

        if (data.Version >= 124)
        {
            int o = r.Offset;
            int b0 = r.U8();
            int b1 = r.U8();
            data.HideVisibleAccessory = new[] { b0, b1 };
            f.Add(new PlrField
            {
                Name = "hideVisibleAccessory",
                Offset = o,
                Length = 2,
                Value = $"0x{b0:x2} 0x{b1:x2}",
            });
        }
        else if (data.Version >= 83)
        {
            int b0 = Field(r, f, "hideVisibleAccessory", x => x.U8());
            data.HideVisibleAccessory = new[] { b0 };
        }

        if (data.Version >= 119)
            data.HideMisc = Field(r, f, "hideMisc", x => x.U8());

        if (data.Version >= 107)
        {
            data.SkinVariant = Field(r, f, "skinVariant", x => x.U8());
        }
        else
        {
            bool male = Field(r, f, "male(bool)", x => x.Bool());
            data.SkinVariant = male ? 0 : 4;
        }

        data.Health = Field(r, f, "health", x => x.I32());
        data.HealthMax = Field(r, f, "healthMax", x => x.I32());
        data.Mana = Field(r, f, "mana", x => x.I32());
        data.ManaMax = Field(r, f, "manaMax", x => x.I32());

        if (data.Version >= 125)
            data.ExtraAccessory = Field(r, f, "extraAccessory", x => x.Bool());

        if (data.Version >= 229)
        {
            data.UnlockedBiomeTorches = Field(r, f, "unlockedBiomeTorches", x => x.Bool());
            data.UsingBiomeTorches = Field(r, f, "usingBiomeTorches", x => x.Bool());
        }

        if (data.Version >= 256)
            data.AteArtisanBread = Field(r, f, "ateArtisanBread", x => x.Bool());

        if (data.Version >= 260)
        {
            string[] names =
            {
                "usedAegisCrystal", "usedAegisFruit", "usedArcaneCrystal",
                "usedGalaxyPearl", "usedGummyWorm", "usedAmbrosia",
            };
            for (int i = 0; i < names.Length; i++)
                data.PermanentBoosts[i] = Field(r, f, names[i], x => x.Bool());
        }

        if (data.Version >= 182)
            data.DownedDd2 = Field(r, f, "downedDD2EventAnyDifficulty", x => x.Bool());

        if (data.Version >= 128)
            data.TaxMoney = Field(r, f, "taxMoney", x => x.I32());

        if (data.Version >= 254)
        {
            data.DeathsPve = Field(r, f, "numberOfDeathsPVE", x => x.I32());
            data.DeathsPvp = Field(r, f, "numberOfDeathsPVP", x => x.I32());
        }

        // 7 个颜色，顺序固定：发/肤/瞳/上衣/内衬/裤/鞋
        data.HairColor = Field(r, f, "hairColor", x => x.Rgb());
        data.SkinColor = Field(r, f, "skinColor", x => x.Rgb());
        data.EyeColor = Field(r, f, "eyeColor", x => x.Rgb());
        data.ShirtColor = Field(r, f, "shirtColor", x => x.Rgb());
        data.UnderShirtColor = Field(r, f, "underShirtColor", x => x.Rgb());
        data.PantsColor = Field(r, f, "pantsColor", x => x.Rgb());
        data.ShoeColor = Field(r, f, "shoeColor", x => x.Rgb());

        // ---- 物品区：空槽同样占满字节，所以按固定槽数读 ----
        ReadItems(r, f, "equipment", 20, ItemFormat.IdPrefix, data.Equipment);
        ReadItems(r, f, "dye", 10, ItemFormat.IdPrefix, data.Dye);
        ReadItems(r, f, "inventory", 50, ItemFormat.IdCountPrefixFavorite, data.Inventory);
        ReadItems(r, f, "coins", 4, ItemFormat.IdCountPrefixFavorite, data.Coins);
        ReadItems(r, f, "ammo", 4, ItemFormat.IdCountPrefixFavorite, data.Ammo);

        int miscStart = r.Offset;
        for (int i = 0; i < 5; i++)
        {
            PlayerItem equip = ReadItem(r, ItemFormat.IdPrefix);
            PlayerItem dye = ReadItem(r, ItemFormat.IdPrefix);
            data.Misc.Add((equip, dye));
        }

        f.Add(new PlrField
        {
            Name = "misc(5×装备+染料)",
            Offset = miscStart,
            Length = r.Offset - miscStart,
            Value = $"5 组，每组 {FormatItem(data.Misc[0].Equip)} / {FormatItem(data.Misc[0].Dye)}",
        });

        ReadItems(r, f, "piggyBank", 40, ItemFormat.IdCountPrefix, data.PiggyBank);
        ReadItems(r, f, "safe", 40, ItemFormat.IdCountPrefix, data.Safe);

        if (data.Version >= 182)
            ReadItems(r, f, "defendersForge", 40, ItemFormat.IdCountPrefix, data.DefendersForge);

        if (data.Version >= 198)
        {
            ItemFormat fmt = data.Version >= 269 ? ItemFormat.IdCountPrefixFavorite : ItemFormat.IdCountPrefix;
            ReadItems(r, f, "voidBag", 40, fmt, data.VoidBag);
        }

        if (data.Version >= 200)
            Field(r, f, "downedDeerclops", x => x.Bool());

        int buffCount = data.Version >= 269 ? 44 : 22;
        ReadItems(r, f, "buffs", buffCount, ItemFormat.Buff, new List<PlayerItem>());

        // 出生点：循环读到 x == -1
        int spawnStart = r.Offset;
        int spawnCount = 0;
        while (true)
        {
            int x = r.I32();
            if (x == -1)
                break;
            r.I32();            // y
            r.I32();            // worldId
            r.String7Bit();     // worldName
            spawnCount++;
            if (spawnCount > 1000)
                throw new InvalidDataException("出生点循环超过 1000 条，文件结构假设有误");
        }

        f.Add(new PlrField
        {
            Name = "spawnPoints",
            Offset = spawnStart,
            Length = r.Offset - spawnStart,
            Value = $"{spawnCount} 条（以 x=-1 结束）",
        });

        Field(r, f, "hotbarLocked", x => x.Bool());

        if (data.Version >= 145)
        {
            int o = r.Offset;
            for (int i = 0; i < 13; i++)
                r.Bool();
            f.Add(new PlrField { Name = "hideInfo[13]", Offset = o, Length = r.Offset - o, Value = "13 个 bool" });
        }

        Field(r, f, "anglerQuestsFinished", x => x.I32());

        if (data.Version >= 255)
        {
            int o = r.Offset;
            for (int i = 0; i < 4; i++)
                r.I32();
            f.Add(new PlrField { Name = "gamepadBinds[4]", Offset = o, Length = r.Offset - o, Value = "4×int32" });
        }

        int toggleCount = data.Version >= 230 ? 12 : data.Version >= 200 ? 11 : 10;
        {
            int o = r.Offset;
            for (int i = 0; i < toggleCount; i++)
                r.I32();
            f.Add(new PlrField
            {
                Name = $"buildingToggles[{toggleCount}]",
                Offset = o,
                Length = r.Offset - o,
                Value = "12/11/10×int32（按版本）",
            });
        }

        if (data.Version >= 181)
            Field(r, f, "bartenderQuestLog", x => x.I32());

        if (data.Version >= 200)
        {
            bool dead = Field(r, f, "dead", x => x.Bool());
            if (dead)
                Field(r, f, "respawnTimer", x => x.I32());
        }

        if (data.Version >= 202)
            Field(r, f, "lastTimePlayerWasSaved", x => x.I64());

        if (data.Version >= 206)
            Field(r, f, "golferScoreAccumulated", x => x.I32());

        if (data.Version >= 218)
        {
            if (data.Version >= 282)
                r.U8();     // 1.4.5.x：开头多一个 bool，读掉丢弃

            int researchStart = r.Offset;
            int count = r.I32();
            if (count < 0 || count > 100000)
                throw new InvalidDataException($"研究条目数异常：{count}（文件结构假设有误）");
            for (int i = 0; i < count; i++)
            {
                r.String7Bit();
                r.I32();
            }

            f.Add(new PlrField
            {
                Name = "research",
                Offset = researchStart,
                Length = r.Offset - researchStart,
                Value = $"{count} 条",
            });
        }

        if (data.Version >= 214)
        {
            int o = r.Offset;
            int bits = r.U8();
            for (int i = 0; i < 4; i++)
            {
                if ((bits & (1 << i)) != 0)
                    ReadItem(r, ItemFormat.IdCountPrefix);
            }

            f.Add(new PlrField
            {
                Name = "temporarySlots",
                Offset = o,
                Length = r.Offset - o,
                Value = $"bits=0x{bits:x2}",
            });
        }

        if (data.Version >= 220)
        {
            int o = r.Offset;
            while (true)
            {
                bool has = r.Bool();
                if (!has)
                    break;
                int id = r.U16();
                switch (id)
                {
                    case 5:     // GodmodePower
                    case 11:    // FarPlacementRangePower
                        r.Bool();
                        break;
                    case 14:    // SpawnRateSliderPerPlayerPower
                        r.F32();
                        break;
                    default:
                        throw new InvalidDataException($"未知创造模式权限 id={id}（偏移 {r.Offset}）");
                }

                if (r.Offset - o > 4096)
                    throw new InvalidDataException("创造模式权限循环异常长，文件结构假设有误");
            }

            f.Add(new PlrField
            {
                Name = "creativePowers",
                Offset = o,
                Length = r.Offset - o,
                Value = "以 bool=false 结束",
            });
        }

        if (data.Version >= 253)
            Field(r, f, "superCart(byte)", x => x.U8());

        if (data.Version >= 262)
        {
            Field(r, f, "currentLoadoutIndex", x => x.I32());
            int o = r.Offset;
            for (int loadout = 0; loadout < 3; loadout++)
            {
                for (int i = 0; i < 20; i++)
                    ReadItem(r, ItemFormat.IdCountPrefix);
                for (int i = 0; i < 10; i++)
                    ReadItem(r, ItemFormat.IdCountPrefix);
                for (int i = 0; i < 10; i++)
                    r.Bool();
            }

            f.Add(new PlrField
            {
                Name = "loadouts[3]",
                Offset = o,
                Length = r.Offset - o,
                Value = "3×(20 装备 + 10 染料 + 10 隐藏)",
            });
        }

        if (data.Version >= 280)
            Field(r, f, "voiceVariant", x => x.U8());

        if (data.Version >= 281)
            Field(r, f, "voicePitchOffset", x => x.F32());

        if (data.Version >= 300)
        {
            int o = r.Offset;
            int count = r.I32();
            if (count < 0 || count > 100000)
                throw new InvalidDataException($"待退物品数异常：{count}");
            for (int i = 0; i < count; i++)
                ReadItem(r, ItemFormat.IdCountPrefix);
            f.Add(new PlrField { Name = "pendingRefunds", Offset = o, Length = r.Offset - o, Value = $"{count} 件" });
        }

        if (data.Version >= 310)
        {
            int o = r.Offset;
            int count = r.I32();
            if (count < 0 || count > 100000)
                throw new InvalidDataException($"一次性对话数异常：{count}");
            for (int i = 0; i < count; i++)
                r.String7Bit();
            f.Add(new PlrField { Name = "oneTimeDialogues", Offset = o, Length = r.Offset - o, Value = $"{count} 条" });
        }

        data.ParsedOffset = r.Offset;
        data.TrailingHex = r.Offset < plaintext.Length
            ? Convert.ToHexString(plaintext, r.Offset, plaintext.Length - r.Offset).ToLowerInvariant()
            : "";
    }

    // ------------------------------------------------------------------
    // 物品
    // ------------------------------------------------------------------

    private enum ItemFormat
    {
        IdPrefix,                  // 装备/染料：int32 id + byte prefix（5B）
        IdCountPrefixFavorite,     // 背包/钱币/弹药：int32 + int32 + byte + bool（10B）
        IdCountPrefix,             // 仓库/配装：int32 + int32 + byte（9B）
        Buff,                      // int32 id + int32 时长（8B）
    }

    private static void ReadItems(PlrReader r, List<PlrField> f, string name, int count, ItemFormat format,
        List<PlayerItem> target)
    {
        int start = r.Offset;
        for (int i = 0; i < count; i++)
            target.Add(ReadItem(r, format));

        f.Add(new PlrField
        {
            Name = name,
            Offset = start,
            Length = r.Offset - start,
            Value = $"{count} 槽（非空 {target.Count(x => !x.IsEmpty)}）",
        });
    }

    private static PlayerItem ReadItem(PlrReader r, ItemFormat format)
    {
        switch (format)
        {
            case ItemFormat.IdPrefix:
                return new PlayerItem { Id = r.I32(), Prefix = r.U8() };

            case ItemFormat.IdCountPrefixFavorite:
                return new PlayerItem
                {
                    Id = r.I32(),
                    Count = r.I32(),
                    Prefix = r.U8(),
                    Favorite = r.Bool(),
                };

            case ItemFormat.IdCountPrefix:
                return new PlayerItem
                {
                    Id = r.I32(),
                    Count = r.I32(),
                    Prefix = r.U8(),
                };

            default:
                return new PlayerItem { Id = r.I32(), Count = r.I32() };
        }
    }

    private static string FormatItem(PlayerItem item) => item.ToString();

    // ------------------------------------------------------------------
    // 字段记录辅助
    // ------------------------------------------------------------------

    private static T Field<T>(PlrReader r, List<PlrField> fields, string name, Func<PlrReader, T> read)
    {
        int start = r.Offset;
        T value = read(r);
        fields.Add(new PlrField
        {
            Name = name,
            Offset = start,
            Length = r.Offset - start,
            // 颜色/字符串/数值都走 ToString（PlayerColor 已重写），null 统一显示 "-"
            Value = Convert.ToString((object?)value, System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        });
        return value;
    }

    // ------------------------------------------------------------------
    // 加密
    // ------------------------------------------------------------------

    private static bool LooksLikePlaintext(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 11 && bytes.Slice(4, 7).SequenceEqual(Encoding.ASCII.GetBytes(Magic));

    /// <summary>AES-128-CBC 解密。Terraria 用「无填充」：明文被补 0 到 16 的倍数。</summary>
    public static byte[] DecryptAesCbc(byte[] ciphertext)
    {
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = TerrariaKey;
        aes.IV = TerrariaKey;

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>加密（只给自检造样本用，线上永远不写 .plr）。</summary>
    public static byte[] EncryptAesCbc(byte[] plaintext)
    {
        if (plaintext.Length % 16 != 0)
            throw new ArgumentException("AES 无填充要求长度是 16 的倍数");

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = TerrariaKey;
        aes.IV = TerrariaKey;

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static string Hex32(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> head = bytes.Length <= 32 ? bytes : bytes.Slice(0, 32);
        return string.Join(" ", head.ToArray().Select(b => b.ToString("x2")));
    }

    // ------------------------------------------------------------------
    // 小端读取器
    // ------------------------------------------------------------------

    private sealed class PlrReader
    {
        private readonly byte[] _data;

        public PlrReader(byte[] data) => _data = data;

        public int Offset { get; private set; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Offset + n > _data.Length)
                throw new InvalidDataException($"文件在偏移 {Offset} 处提前结束（需要 {n} 字节，剩余 {_data.Length - Offset}）");

            ReadOnlySpan<byte> span = _data.AsSpan(Offset, n);
            Offset += n;
            return span;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public bool Bool() => U8() != 0;

        public PlayerColor Rgb()
        {
            ReadOnlySpan<byte> s = Take(3);
            return new PlayerColor(s[0], s[1], s[2]);
        }

        public string Ascii(int n) => Encoding.ASCII.GetString(Take(n));

        /// <summary>7bit 变长长度 + UTF-8 字符串（Terraria 的 WriteString）。</summary>
        public string String7Bit()
        {
            int length = Read7BitLength();
            if (length < 0 || length > _data.Length - Offset)
                throw new InvalidDataException($"字符串长度 {length} 在偏移 {Offset} 处越界");

            return Encoding.UTF8.GetString(Take(length));
        }

        private int Read7BitLength()
        {
            int value = 0;
            int shift = 0;
            while (true)
            {
                byte b = U8();
                value |= (b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0)
                    return value;
                if (shift > 28)
                    throw new InvalidDataException("7bit 长度前缀过长，文件结构假设有误");
            }
        }
    }
}
