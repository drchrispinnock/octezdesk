namespace OctezDesk.Win;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Windowless helper mode: no WPF application is created.
        if (args.Contains(KeepAliveService.Switch)) return KeepAliveService.Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
