namespace BunnyLoader.Server;

/// <summary>
/// TickEngine 需要会话提供的能力。抽成接口是为了让 TickEngine 不认识 Session 的细节。
/// </summary>
public interface ISessionTicker
{
    /// <summary>20Hz：把其他玩家的最新状态广播出去。</summary>
    Task OnBroadcastTickAsync(CancellationToken cancellationToken);

    /// <summary>500ms：如果太久没发东西，补一个保活包。</summary>
    Task OnKeepAliveTickAsync(CancellationToken cancellationToken);

    /// <summary>60Hz：超时判定、实体/世界更新钩子、区块窗口检查。</summary>
    Task OnGameTickAsync(double nowMs, CancellationToken cancellationToken);
}

/// <summary>一个玩家在服务端内存里的状态。字段都是「客户端最近一次上报」的值。</summary>
public sealed class PlayerState
{
    public byte Slot { get; init; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public bool Active { get; set; }
    public byte[]? LastControls { get; set; }
    public long LastSeenMs { get; set; }

    /// <summary>上一次广播出去的坐标，用来判断是否需要再发。</summary>
    public int SentTileX { get; set; } = int.MinValue;
    public int SentTileY { get; set; } = int.MinValue;
}
