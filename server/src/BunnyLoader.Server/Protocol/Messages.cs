using System.Buffers.Binary;

namespace BunnyLoader.Server.Protocol;

/// <summary>用到的原版消息号。来源：terrustia protocol-notes；68 另有上游 ModNet.cpp:23。</summary>
public static class MessageId
{
    public const byte Hello = 1;
    public const byte Kick = 2;
    public const byte ConnectionApproved = 3;
    public const byte SyncPlayer = 4;
    public const byte SyncEquipment = 5;
    public const byte RequestWorldData = 6;
    public const byte WorldData = 7;
    public const byte SpawnTileData = 8;
    public const byte TileSection = 10;
    public const byte PlayerControls = 13;
    public const byte PlayerActive = 14;
    public const byte PlayerHealth = 16;
    public const byte PlayerMana = 42;
    public const byte StartPlaying = 49;
    public const byte PlayerSpawn = 12;
    public const byte ClientUuid = 68;
    public const byte NetModules = 82;
    public const byte FinishedConnecting = 129;
}

public static class Messages
{
    /// <summary>消息 1：`string "Terraria326"`（1.4.5.8 的 curRelease 是 326）。</summary>
    public static byte[] HelloPacket(string version) => Codec.Frame(MessageId.Hello, Codec.String(version));

    /// <summary>
    /// 消息 3：`u8 playerSlot` + `bool`。1.4.5 起才有那个 bool，1.4.4 只有一个字节。
    /// </summary>
    public static byte[] ConnectionApprovedPacket(byte slot, bool checkBytesInClientLoopThread) =>
        Codec.Frame(MessageId.ConnectionApproved, new[] { slot, checkBytesInClientLoopThread ? (byte)1 : (byte)0 });

    /// <summary>消息 68：`string uuid`。带 BL 前缀时是模组介绍。</summary>
    public static byte[] ClientUuidPacket(string uuid) => Codec.Frame(MessageId.ClientUuid, Codec.String(uuid));

    /// <summary>消息 7：世界数据。</summary>
    public static byte[] WorldDataPacket(WorldData world) => Codec.Frame(MessageId.WorldData, world.Encode());

    /// <summary>消息 82 里的 BL 欢迎包：模块 0xB17F + kind 200 + rules。</summary>
    public static byte[] WelcomePacket(byte rules)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), Codec.ModuleId);
        payload[2] = Codec.KindWelcome;
        payload[3] = rules;
        return Codec.Frame(MessageId.NetModules, payload);
    }

    /// <summary>消息 82 里的同步计划：模块 0xB17F + kind 11 + JSON 字符串。</summary>
    public static byte[] SyncPlanPacket(string json)
    {
        byte[] text = Codec.String(json);
        byte[] payload = new byte[3 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), Codec.ModuleId);
        payload[2] = Codec.KindSyncPlan;
        text.CopyTo(payload, 3);
        return Codec.Frame(MessageId.NetModules, payload);
    }

    /// <summary>消息 2：踢人，负载是 NetworkText。</summary>
    public static byte[] KickPacket(string reason) => Codec.Frame(MessageId.Kick, NetText.Literal(reason));

    public static string Name(byte id) => id switch
    {
        MessageId.Hello => "Hello",
        MessageId.Kick => "Kick",
        MessageId.ConnectionApproved => "ConnectionApproved",
        MessageId.SyncPlayer => "SyncPlayer",
        MessageId.SyncEquipment => "SyncEquipment",
        MessageId.RequestWorldData => "RequestWorldData",
        MessageId.WorldData => "WorldData",
        MessageId.SpawnTileData => "SpawnTileData",
        MessageId.TileSection => "TileSection",
        MessageId.PlayerSpawn => "PlayerSpawn",
        MessageId.PlayerControls => "PlayerControls",
        MessageId.PlayerActive => "PlayerActive",
        MessageId.PlayerHealth => "PlayerHealth",
        MessageId.PlayerMana => "PlayerMana",
        MessageId.StartPlaying => "StartPlaying",
        MessageId.ClientUuid => "ClientUuid",
        MessageId.NetModules => "NetModules",
        MessageId.FinishedConnecting => "FinishedConnecting",
        _ => "?",
    };
}
