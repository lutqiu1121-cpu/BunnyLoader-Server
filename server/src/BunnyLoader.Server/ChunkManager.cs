namespace BunnyLoader.Server;

/// <summary>
/// 动态区块加载：只发玩家周围的一小块，并记住已经发过哪些。
///
/// 泰拉瑞亚的分块是 200×150 瓦片。这里用 5×3 的窗口（横向 5 个、纵向 3 个），
/// 覆盖 1000×450 瓦片，足够一屏视野加上一点缓冲。
/// </summary>
public sealed class ChunkManager
{
    public const int SectionWidth = 200;
    public const int SectionHeight = 150;

    private readonly HashSet<(int X, int Y)> _sent = new();

    /// <summary>窗口半宽（格数）：2 表示左右各 2 个分块，共 5 个。</summary>
    public int HalfWindowX { get; init; } = 2;

    /// <summary>窗口半高：1 表示上下各 1 个分块，共 3 个。</summary>
    public int HalfWindowY { get; init; } = 1;

    public int SentCount => _sent.Count;

    /// <summary>已经发过的分块坐标（用于日志/测试）。</summary>
    public IReadOnlyCollection<(int X, int Y)> SentSections => _sent;

    /// <summary>以玩家所在瓦片为中心，算出需要覆盖的分块集合。</summary>
    public List<(int X, int Y)> WindowFor(int tileX, int tileY)
    {
        int cx = tileX / SectionWidth;
        int cy = tileY / SectionHeight;

        var result = new List<(int X, int Y)>((HalfWindowX * 2 + 1) * (HalfWindowY * 2 + 1));

        for (int dy = -HalfWindowY; dy <= HalfWindowY; dy++)
        {
            for (int dx = -HalfWindowX; dx <= HalfWindowX; dx++)
            {
                int sx = cx + dx;
                int sy = cy + dy;

                if (sx < 0 || sy < 0)
                    continue;

                result.Add((sx, sy));
            }
        }

        return result;
    }

    /// <summary>标记为已发送；返回 false 表示之前已经发过。</summary>
    public bool MarkSent(int sx, int sy) => _sent.Add((sx, sy));

    /// <summary>世界尺寸变了（换图）时清空。</summary>
    public void Reset() => _sent.Clear();
}
