using BunnyLoader.Server.Protocol;

namespace BunnyLoader.Server.World;

/// <summary>可变的服务端世界覆盖层；原始 WldWorld 保持只读。</summary>
public sealed class WorldState
{
    private readonly WldWorld? _base;
    private readonly Func<int, int, Tile>? _fallback;
    private readonly int _flatMaxX;
    private readonly int _flatMaxY;
    private readonly Dictionary<(int X, int Y), Tile> _overlay = new();
    private readonly object _gate = new();

    public WorldState(WldWorld? world, Func<int, int, Tile>? fallback = null,
                      int flatMaxX = 4200, int flatMaxY = 1200)
    {
        _base = world;
        _fallback = fallback;
        _flatMaxX = flatMaxX;
        _flatMaxY = flatMaxY;
    }

    private Tile BaseGet(int x, int y) => _base?.Get(x, y) ?? _fallback?.Invoke(x, y) ?? default;

    public Tile Get(int x, int y)
    {
        lock (_gate)
            return _overlay.TryGetValue((x, y), out Tile tile) ? tile : BaseGet(x, y);
    }

    public bool TrySet(int x, int y, Tile tile, out Tile previous)
    {
        if (x < 0 || y < 0 || x >= MaxX || y >= MaxY)
        {
            previous = default;
            return false;
        }

        lock (_gate)
        {
            previous = _overlay.TryGetValue((x, y), out Tile old) ? old : BaseGet(x, y);
            _overlay[(x, y)] = tile;
            return true;
        }
    }

    public static Tile FlatTile(int x, int y, int maxX, int maxY, short surface, short rock)
    {
        if (x < 0 || y < 0 || x >= maxX || y >= maxY || y < surface)
            return Tile.Air;
        if (y == surface)
            return Tile.Solid(TileSection.TypeGrass);
        return y < rock ? Tile.Solid(TileSection.TypeDirt) : Tile.Solid(TileSection.TypeStone);
    }

    public int MaxX => _base?.MaxTilesX ?? _flatMaxX;
    public int MaxY => _base?.MaxTilesY ?? _flatMaxY;
    public bool IsFrameImportant(ushort type) => _base?.IsFrameImportant(type) ?? false;

    public bool TryRead(int x, int y, out Tile tile)
    {
        if (x < 0 || y < 0 || x >= MaxX || y >= MaxY)
        {
            tile = default;
            return false;
        }
        lock (_gate)
        {
            tile = _overlay.TryGetValue((x, y), out Tile value) ? value : BaseGet(x, y);
            return true;
        }
    }
}
