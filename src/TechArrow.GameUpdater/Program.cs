using Velopack;
namespace TechArrow.GameUpdater;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--activity-collector") { Services.LauncherActivityCollector.RunHelper(args[1]); return; }
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
