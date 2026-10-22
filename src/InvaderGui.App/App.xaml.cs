using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace InvaderGui.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            SessionDiagnostics.Start(Path.Combine(AppContext.BaseDirectory, "logs", "invader-gui-diagnostics.log"));
            AppDomain.CurrentDomain.FirstChanceException += (_, args) => SessionDiagnostics.FirstChance(args.Exception);
            DispatcherUnhandledException += (_, args) => SessionDiagnostics.Write("Unhandled UI exception", args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                SessionDiagnostics.Write($"Unhandled runtime exception; terminating={args.IsTerminating}", args.ExceptionObject as Exception);
        }
        if (e.Args.Contains("--software-rendering", StringComparer.OrdinalIgnoreCase))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        SessionDiagnostics.Write($"Rendering preference: {RenderOptions.ProcessRenderMode}");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionDiagnostics.Write($"Application exit; code={e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
