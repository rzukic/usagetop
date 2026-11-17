using AiUsage.Data;
using AiUsage.Server;

var options = ServerOptions.FromEnvironment();

// Anything but "run" is a command for the server that is already running.
if (args.Length > 0 && !args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    return await Cli.RunAsync(args, options);
}

Directory.CreateDirectory(options.DataDir);
var apiToken = Secrets.ApiToken(options);

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
// The framework's own per-request chatter drowns out the handful of lines that matter.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

builder.Services.AddSingleton(options);
var credentials = new Credentials(options.CredentialsPath, Secrets.Sealer(options));
builder.Services.AddSingleton(credentials);
builder.Services.AddSingleton(new Store(options.Database));
builder.Services.AddSingleton(new Schedule(
    options.Poll,
    options.ActivePoll,
    DateTimeOffset.UtcNow,
    credentials.Accounts().Select(account => account.Key)));
builder.Services.AddSingleton(services => new Notifier(
    new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
    options.NotifyUrl,
    services.GetRequiredService<ILogger<Notifier>>()));
builder.Services.AddSingleton<Poller>();
builder.Services.AddHostedService(services => services.GetRequiredService<Poller>());
builder.Services.AddSingleton<SignInService>();

var app = builder.Build();
Api.Map(app, apiToken);
WebUi.Map(app);

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AiUsage");
    var configured = credentials.Accounts()
        .Where(account => credentials.For(account.Slot).Token(account.Provider) != null)
        .Select(account => account.DisplayName)
        .ToList();

    log.LogInformation("Listening on {Urls}; data in {Data}", options.Urls, options.DataDir);
    log.LogInformation("Open that address in a browser for the dashboard and the sign-ins");
    log.LogInformation(
        "Providers with credentials: {Providers}",
        configured.Count > 0 ? string.Join(", ", configured) : "none yet — see `aiusage-server help`");
    log.LogInformation(
        "Notifications: {Notify}",
        string.IsNullOrWhiteSpace(options.NotifyUrl) ? "off (set AIUSAGE_NOTIFY_URL)" : "on");

    // The token itself stays out of the log, which tends to be kept and shared.
    log.LogInformation("Clients need the API token: run `aiusage-server token` to print it");
});

app.Run();
return 0;
