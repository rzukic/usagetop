using System.Reflection;
using Avalonia;

namespace AiUsage;

public static class Program
{
    private const string Help = """
        usagetop: how much of each AI subscription is left.

          usagetop                 start, and show the panel (a second copy shows the first one's)
          usagetop --hidden        start as the tray icon alone: for autostart
          usagetop toggle          show the panel, or put it away: for a key binding
          usagetop show | hide
          usagetop refresh         ask every provider now
          usagetop status          the reading as text
          usagetop status --json   everything, for a script
          usagetop status --waybar a Waybar custom module's JSON
          usagetop quit
          usagetop doctor          what this desktop offers, as the app sees it

          --sample                 made-up usage in place of your own
          --state <file>           a server's /api/state answer in place of your own
          --snapshot <folder>      draw every page to PNGs and leave (with --sample or --state)
          --version
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        var words = new List<string>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? state = null, snapshot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--state" when i + 1 < args.Length:
                    state = args[++i];
                    break;
                case "--snapshot" when i + 1 < args.Length:
                    snapshot = args[++i];
                    break;
                case var flag when flag.StartsWith("--", StringComparison.Ordinal) || flag is "-h" or "-v":
                    flags.Add(flag);
                    break;
                default:
                    words.Add(args[i].ToLowerInvariant());
                    break;
            }
        }

        if (flags.Contains("--help") || flags.Contains("-h") || words.FirstOrDefault() == "help")
        {
            Console.WriteLine(Help);
            return 0;
        }
        if (flags.Contains("--version") || flags.Contains("-v"))
        {
            Console.WriteLine(Version);
            return 0;
        }

        var sample = flags.Contains("--sample") || state != null;
        if (snapshot != null)
        {
            // Before the single-instance check, so it works beside a running copy.
            BuildAvaloniaApp().SetupWithoutStarting();
            Snapshots.Write(snapshot, sample ? AppModel.Sample(state) : AppModel.Empty());
            return 0;
        }

        var command = words.FirstOrDefault() ?? "start";
        switch (command)
        {
            case "doctor":
                Console.WriteLine(Desktop.Describe());
                return 0;

            case "status":
                var form = flags.Contains("--waybar") ? " --waybar" : flags.Contains("--json") ? " --json" : "";
                return Ask("status" + form);

            case "hide" or "refresh" or "quit":
                return Ask(command);

            case "start" or "show" or "toggle":
                // A running copy does it; with none, this becomes the app.
                if (Ipc.Send(command == "start" ? "show" : command) != null) return 0;
                App.Options = new StartOptions(Hidden: flags.Contains("--hidden"), Sample: sample, StateFile: state);
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

            default:
                Console.Error.WriteLine($"usagetop: unknown command '{command}'. Try usagetop --help.");
                return 2;
        }
    }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>Hands a command to the running copy and prints what it says.</summary>
    private static int Ask(string command)
    {
        if (Ipc.Send(command) is not { } answer)
        {
            Console.Error.WriteLine("usagetop is not running.");
            return 1;
        }
        Console.Write(answer);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        // It is one panel and a tray icon: nothing a session manager could restore.
        .With(new X11PlatformOptions { WmClass = "usagetop", EnableSessionManagement = false })
        .LogToTrace();
}
