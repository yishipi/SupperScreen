using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MagicCompanion.Agent;

internal static class ScreenSnapshots
{
    public static int Save(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new AgentException("INVALID_ARGUMENT", "截图目录不可用，请在电脑重新选择");

        var screens = Screen.AllScreens.OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        if (screens.Length == 0)
            throw new AgentException("SCREEN_UNAVAILABLE", "电脑没有活动屏幕");

        var captures = new List<Bitmap>(screens.Length);
        var written = new List<string>(screens.Length);
        var prefix = $"SupperScreen_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}";
        try
        {
            foreach (var screen in screens)
            {
                var bounds = screen.Bounds;
                var capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                captures.Add(capture);
                using var graphics = Graphics.FromImage(capture);
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            for (var index = 0; index < captures.Count; index++)
            {
                var file = Path.Combine(directory, $"{prefix}_屏幕{index + 1:00}.png");
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                written.Add(file);
                captures[index].Save(stream, ImageFormat.Png);
            }
            return written.Count;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            foreach (var file in written)
                try { File.Delete(file); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new AgentException("CAPTURE_FAILED", "截屏或保存失败，请检查活动屏幕和目录写入权限");
        }
        finally
        {
            foreach (var capture in captures)
                capture.Dispose();
        }
    }
}
