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
                                   (--anchor x,y,w,h hangs it under that spot of the screen)
          usagetop show | hide | overview | settings
          usagetop refresh         ask every provider now
          usagetop status          the reading as text
          usagetop status --json   everything, for a script
          usagetop status --waybar a Waybar custom module's JSON
          usagetop quit
          usagetop doctor          what this desktop offers, as the app sees it
          usagetop server <address>  read from your usagetop server; asks for its token
                                   (or reads it from standard input); --off stops

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
        string? state = null, snapshot = null, anchor = null;
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
                case "--anchor" when i + 1 < args.Length:
                    anchor = args[++i];
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
            Snapshots.Write(snapshot, sample ? AppModel.Sample(state) : AppModel.Blank());
            return 0;
        }

        var command = words.FirstOrDefault() ?? "start";
        switch (command)
        {
            case "doctor":
                Console.WriteLine(Desktop.Describe());
                return 0;

            case "server":
                return SetServer(words.ElementAtOrDefault(1), flags.Contains("--off"));

            case "status":
                var form = flags.Contains("--waybar") ? " --waybar" : flags.Contains("--json") ? " --json" : "";
                return Ask("status" + form);

            case "hide" or "refresh" or "quit":
                return Ask(command);

            case "settings" or "overview":
                if (Ipc.Send(command + (anchor == null ? "" : $" --anchor {anchor}")) != null) return 0;
                App.Options = new StartOptions(Hidden: true, Sample: sample, StateFile: state);
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

            case "start" or "show" or "toggle":
                // A running copy does it; with none, this becomes the app.
                var under = anchor == null ? "" : $" --anchor {anchor}";
                if (Ipc.Send((command == "start" ? "show" : command) + under) != null) return 0;
                App.Options = new StartOptions(Hidden: flags.Contains("--hidden"), Sample: sample, StateFile: state);
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

            default:
                Console.Error.WriteLine($"usagetop: unknown command '{command}'. Try usagetop --help.");
                return 2;
        }
    }

    /// <summary>
    /// `usagetop server <address>`: tries the server with the token, saves both
    /// when it answers, and tells a running copy to read from it. The token comes
    /// from standard input, or is asked for without showing it: never on the
    /// command line, where other users could read it.
    /// </summary>
    private static int SetServer(string? address, bool off)
    {
        SecretKey.EnsureFolder();
        var credentials = new Data.Credentials(Data.AppPaths.Credentials, SecretKey.Sealer());
        if (off)
        {
            credentials.SetServer(null, null);
            Ipc.Send("reload");
            Console.WriteLine("Server off.");
            return 0;
        }

        if (Data.ServerClient.NormalizeUrl(address) is not { } url)
        {
            Console.Error.WriteLine("usagetop: give the server's address, e.g. usagetop server usage.example.com");
            return 2;
        }

        var token = ReadToken();
        try
        {
            var state = Data.ServerClient.FetchAsync(url, token, since: DateTimeOffset.UtcNow).GetAwaiter().GetResult();
            var count = state.Snapshot.Usages.Count;
            Console.WriteLine($"Reached {url} · {count} subscription{(count == 1 ? "" : "s")}");
        }
        catch (Data.ServerException problem) when (problem.Message == "Server has no reading yet")
        {
            Console.WriteLine($"Reached {url}; it has no reading yet");
        }
        catch (Data.ServerException problem)
        {
            Console.Error.WriteLine($"usagetop: {problem.Message}. Nothing was saved.");
            return 1;
        }

        credentials.SetServer(url, token);
        Console.WriteLine(Ipc.Send("reload") != null ? "Saved; the running app reads from it now." : "Saved.");
        return 0;
    }

    private static string? ReadToken()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine()?.Trim() is { Length: > 0 } piped ? piped : null;

        Console.Write("Token: ");
        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0) typed.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) typed.Append(key.KeyChar);
        }
        Console.WriteLine();
        return typed.ToString().Trim() is { Length: > 0 } token ? token : null;
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
