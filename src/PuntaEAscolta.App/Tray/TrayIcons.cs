using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PuntaEAscolta.App.Native;
using Forms = System.Windows.Forms;

namespace PuntaEAscolta.App.Tray;

/// <summary>Icone dell'area di notifica: normale e "in pausa" (la stessa con un piccolo simbolo di pausa).</summary>
internal sealed class TrayIcons : IDisposable
{
    public TrayIcons()
    {
        var size = Forms.SystemInformation.SmallIconSize;
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        Icon normal;
        try
        {
            normal = File.Exists(path) ? new Icon(path, size) : (Icon)SystemIcons.Application.Clone();
        }
        catch (Exception)
        {
            normal = (Icon)SystemIcons.Application.Clone();
        }
        Normal = normal;
        Paused = CreatePaused(normal, size);
    }

    public Icon Normal { get; }

    public Icon Paused { get; }

    private static Icon CreatePaused(Icon baseIcon, Size size)
    {
        try
        {
            using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawIcon(baseIcon, new Rectangle(0, 0, size.Width, size.Height));

                float s = Math.Max(8f, size.Width * 0.6f);
                var badge = new RectangleF(size.Width - s, size.Height - s, s, s);
                using var background = new SolidBrush(Color.FromArgb(240, 45, 45, 45));
                g.FillEllipse(background, badge);
                float barWidth = s * 0.16f, barHeight = s * 0.46f, top = badge.Y + (s - barHeight) / 2f;
                g.FillRectangle(Brushes.White, badge.X + s * 0.30f, top, barWidth, barHeight);
                g.FillRectangle(Brushes.White, badge.X + s * 0.54f, top, barWidth, barHeight);
            }

            nint handle = bitmap.GetHicon();
            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
        catch (Exception)
        {
            return (Icon)baseIcon.Clone();
        }
    }

    public void Dispose()
    {
        Normal.Dispose();
        Paused.Dispose();
    }
}
