using System.Text;
using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server.World;

/// <summary>从原版 .wld 读出来的世界（只读，够服务端发分块用）。</summary>
public sealed class WldWorld
{
    public int Version { get; init; }
    public string Name { get; init; } = "";
    public string Seed { get; init; } = "";
    public int MaxTilesX { get; init; }
    public int MaxTilesY { get; init; }
    public int SpawnTileX { get; init; }
    public int SpawnTileY { get; init; }
    public double WorldSurface { get; init; }
    public double RockLayer { get; init; }
    public bool Crimson { get; init; }
    public bool DayTime { get; init; }
    public int GameMode { get; init; }

    /// <summary>行优先：index = y * MaxTilesX + x。</summary>
    public Tile[] Tiles { get; init; } = Array.Empty<Tile>();

    /// <summary>解析过程中的提示/警告，启动时打到日志里。</summary>
    public List<string> Notes { get; } = new();

    public Tile Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= MaxTilesX || y >= MaxTilesY)
            return default;

        return Tiles[y * MaxTilesX + x];
    }
}
