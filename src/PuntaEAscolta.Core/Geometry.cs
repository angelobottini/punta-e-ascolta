namespace PuntaEAscolta.Core;

/// <summary>Punto sullo schermo in pixel FISICI del desktop virtuale (può essere negativo con più monitor).</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Rettangolo in pixel fisici del desktop virtuale.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;
    public ScreenPoint Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(ScreenPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public static ScreenRect Around(ScreenPoint center, int width, int height) =>
        new(center.X - width / 2, center.Y - height / 2, width, height);

    public static ScreenRect FromLtrb(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public ScreenRect Intersect(ScreenRect other)
    {
        int l = Math.Max(X, other.X), t = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : new ScreenRect(l, t, r - l, b - t);
    }

    public ScreenRect Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    /// <summary>Distanza euclidea dal punto al rettangolo (0 se il punto è dentro).</summary>
    public double DistanceTo(ScreenPoint p)
    {
        double dx = Math.Max(Math.Max(X - p.X, 0), p.X - (Right - 1));
        double dy = Math.Max(Math.Max(Y - p.Y, 0), p.Y - (Bottom - 1));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Rettangolo in coordinate immagine (pixel dell'immagine catturata, origine in alto a sinistra).</summary>
public readonly record struct ImageRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool Contains(double px, double py) => px >= X && px <= Right && py >= Y && py <= Bottom;
}
