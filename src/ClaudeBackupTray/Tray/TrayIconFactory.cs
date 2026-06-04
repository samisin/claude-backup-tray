using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeBackupTray.State;

namespace ClaudeBackupTray.Tray;

/// <summary>
/// Provides the four status icons. Loads the user-supplied <c>.ico</c> files that are embedded
/// from the <c>Resources</c> folder; if one can't be loaded it falls back to a drawn coloured
/// circle so the app always has an icon. Icons are created once and reused for the app lifetime.
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    // status -> embedded .ico file name + fallback colour if the resource can't be loaded
    private static readonly (BackupStatus Status, string Resource, Color Fallback)[] Definitions =
    [
        (BackupStatus.Idle,    "claude-backup-gray.ico",   Color.FromArgb(150, 150, 150)), // gray – idle
        (BackupStatus.Running, "claude-backup-orange.ico", Color.FromArgb(245, 150, 35)),  // orange – running
        (BackupStatus.Success, "claude-backup-green.ico",  Color.FromArgb(60, 180, 75)),   // green – succeeded
        (BackupStatus.Failed,  "claude-backup-red.ico",    Color.FromArgb(220, 50, 47)),   // red – failed
    ];

    public static Dictionary<BackupStatus, Icon> CreateAll()
    {
        var icons = new Dictionary<BackupStatus, Icon>();
        foreach ((BackupStatus status, string resource, Color fallback) in Definitions)
            icons[status] = LoadEmbedded(resource) ?? Create(fallback);
        return icons;
    }

    private static Icon? LoadEmbedded(string fileName)
    {
        try
        {
            Assembly assembly = typeof(TrayIconFactory).Assembly;
            string? resourceName = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                return null;

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            // Keep the full multi-resolution .ico so the shell can pick the right size per DPI.
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null; // fall back to a drawn icon
        }
    }

    private static Icon Create(Color color)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var circle = new Rectangle(3, 3, size - 7, size - 7);
            using var fill = new SolidBrush(color);
            using var highlight = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
            using var outline = new Pen(Color.FromArgb(90, 0, 0, 0), 1.5f);

            g.FillEllipse(fill, circle);
            g.DrawEllipse(outline, circle);
            // small specular highlight for a bit of depth
            g.FillEllipse(highlight, new Rectangle(circle.X + 6, circle.Y + 5, 9, 7));
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            // Clone to an independent managed icon that owns its own data, so we can free the GDI handle now.
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static void Destroy(Icon icon) => icon.Dispose();
}
