using System.Diagnostics;

namespace BunnyLoader.Server;

/// <summary>
/// 服务端心跳。每个连接一个，60Hz 驱动三类工作：
///   1. 每 50ms 一次的网络广播（把其他玩家的最新坐标发出去）；
///   2. 每 500ms 一次的保活；
///   3. 每帧的超时判定与游戏逻辑钩子（实体、世界、区块窗口）。
///
/// 单独成文件是有意的：会话只负责「收到什么就更新什么」，
/// 什么时候发由这里统一定时，避免每个消息处理里各写一套发送逻辑。
/// </summary>
public sealed class TickEngine
{
    /// <summary>每帧间隔，60Hz。</summary>
    public const int TickMs = 16;

    /// <summary>网络广播间隔，20Hz。</summary>
    public const int BroadcastMs = 50;

    /// <summary>多久没发东西就补一个保活包。</summary>
    public const int KeepAliveMs = 500;

    /// <summary>多久没收到客户端任何东西就判超时。</summary>
    public const int TimeoutMs = 30_000;

    private readonly ISessionTicker _session;
    private readonly ILogger _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TickEngine(ISessionTicker session, ILogger log)
    {
        _session = session;
        _log = log;
    }

    public bool Running => _loop is { IsCompleted: false };

    public void Start()
    {
        if (_loop is not null)
            return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? loop = _loop;

        _cts = null;
        _loop = null;

        if (cts is null)
            return;

        cts.Cancel();

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception e) { _log.Error("tick.loop.error", ("type", e.GetType().Name), ("message", e.Message)); }
        }

        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        double nextBroadcast = 0;
        double nextKeepAlive = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            double frameStart = clock.Elapsed.TotalMilliseconds;

            try
            {
                if (frameStart >= nextBroadcast)
                {
                    nextBroadcast = frameStart + BroadcastMs;
                    await _session.OnBroadcastTickAsync(cancellationToken).ConfigureAwait(false);
                }

                if (frameStart >= nextKeepAlive)
                {
                    nextKeepAlive = frameStart + KeepAliveMs;
                    await _session.OnKeepAliveTickAsync(cancellationToken).ConfigureAwait(false);
                }

                // 每帧：超时判定 + 实体/世界/区块窗口
                await _session.OnGameTickAsync(frameStart, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _log.Error("tick.step.error", ("type", e.GetType().Name), ("message", e.Message));
            }

            double elapsed = clock.Elapsed.TotalMilliseconds - frameStart;
            int delay = TickMs - (int)elapsed;
            if (delay < 1) delay = 1;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
