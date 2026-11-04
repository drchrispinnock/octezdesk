namespace OctezDesk.Win;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: Velopack starts the exe with special arguments to run these hooks, then exits.
        // The keep-alive helper runs from the exe being replaced, so an update stops it beforehand.
        // Nothing is started after an update from a hook: Velopack kills every process left running
        // from the app folder once its hooks finish. Instead the update restarts the app, as the
        // helper or the GUI (AppUpdater). Uninstalling stops the helper and removes the sign-in
        // entry; the distro, and with it the node and its data, is left alone.
        Velopack.VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => KeepAliveService.StopAndWait(TimeSpan.FromSeconds(10)))
            .OnBeforeUninstallFastCallback(_ =>
            {
                KeepAliveService.StopAndWait(TimeSpan.FromSeconds(10));
                KeepAliveService.UnregisterAutostart();
            })
            .Run();

        // Windowless helper mode: no WPF application is created.
        if (args.Contains(KeepAliveService.Switch)) return KeepAliveService.Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
