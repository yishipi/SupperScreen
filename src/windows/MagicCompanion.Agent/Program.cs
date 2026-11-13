namespace MagicCompanion.Agent;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System
            .Windows
            .Interop
            .RenderMode
            .SoftwareOnly;
        using var instance = new System.Threading.Mutex(
            true,
            @"Local\MagicCompanion.Agent",
            out var created
        );
        if (!created)
        {
            System.Windows.MessageBox.Show(
                "SupperScreen 已在运行，可从系统托盘打开控制台。",
                "SupperScreen"
            );
            return;
        }
        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new System.Uri("/SupperScreen;component/DesktopTheme.xaml", System.UriKind.Relative),
        });
        var main = new MainWindow(args);
        app.Run(main);
    }
}
