using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsage.Data;
using AiUsage.Ui;

namespace AiUsage;

/// <summary>
/// Keeps an installed Linux copy current from the GitHub releases.
///
/// It looks a minute after start and then every six hours, or when asked in
/// Settings; never more often on its own, because GitHub allows an address 60
/// unauthenticated API calls an hour and each look costs one. A newer version
/// is downloaded in the background, checked and then waits beside the app's
/// folder: "Restart to update" puts it in at once, Quit puts it in as the app
/// leaves, and failing both it goes in the next time the app starts. Nothing
/// here ever pops up: a failed look is a line in Settings.
///
/// What is checked before a download may replace the app: its SHA-256 against
/// the one GitHub lists for the archive, that the archive unpacks cleanly
/// inside its staging folder, and that the unpacked `usagetop --version` runs
/// here and reports the version the release names.
///
/// A copy outside a user-writable install folder (a build folder, Downloads, a
/// system package under /usr, Flatpak, Snap) does nothing at all.
/// USAGETOP_UPDATE_SOURCE, an http(s) address that answers like
/// https://api.github.com/repos/rzukic/usagetop, stands in for GitHub, to try
/// an update end to end without publishing.
/// </summary>
public sealed class Updater
{
    public enum Phase { Off, Waiting, Checking, UpToDate, Downloading, Ready, Failed }

    private const string ApiBase = "https://api.github.com/repos/rzukic/usagetop";
    private const string SourceVariable = "USAGETOP_UPDATE_SOURCE";
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient SharedHttp = CreateHttpClient();

    public static Updater Current { get; } = new();

    private readonly HttpClient _http;
    private readonly InstallLocation? _installed;
    private readonly string? _assetName;
    private readonly Func<bool> _getBeta;
    private readonly Action<Action>? _onBetaChanged;
    private readonly Func<string, string, CancellationToken, Task<string?>> _verify;
    private readonly Action<string, bool>? _launchReplacement;
    private readonly Action<string>? _syncGnomeExtension;

    private Timer? _timer;
    private bool _started;
    private bool _busy;
    private bool _again;
    private bool _handedOver;
    private bool _beta;
    private StagedUpdate? _staged;

    public Phase State { get; private set; }

    /// <summary>The version being downloaded, or downloaded and waiting.</summary>
    public string? Target { get; private set; }

    /// <summary>0-100 while downloading, when the server reports the size.</summary>
    public int Progress { get; private set; }

    /// <summary>When a look last got an answer.</summary>
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>What a failure was, in a line short enough for the status; why, for its tooltip.</summary>
    public string? Failure { get; private set; }
    public string? FailureDetail { get; private set; }

    /// <summary>Why self-updates are off when <see cref="IsInstalled"/> is false.</summary>
    public string? OffReason { get; }

    /// <summary>The testing source in place of GitHub, if one is set.</summary>
    public string? SourceOverride { get; }

    /// <summary>The running version, as the release names it ("1.6.1", "1.6.1-beta.1").</summary>
    public string Version { get; }

    /// <summary>Raised whenever any of the properties above change (may be on a worker thread).</summary>
    public event Action? Changed;

    public bool IsInstalled => _installed != null && _assetName != null;

    /// <summary>A look or a download is under way.</summary>
    public bool Busy => _busy;

    private Updater()
        : this(
            version: string.IsNullOrWhiteSpace(Program.Version) ? "0.0.0" : Program.Version,
            installed: DetectInstall(Environment.ProcessPath, out var reason),
            offReason: reason,
            assetName: AssetFor(RuntimeInformation.OSArchitecture),
            sourceOverride: ReadSourceOverride(),
            http: SharedHttp,
            getBeta: () => PanelPrefs.Current.BetaUpdates,
            onBetaChanged: handler => PanelPrefs.Current.Changed += handler,
            verify: VerifyBinaryAsync,
            launchReplacement: SpawnRelaunch,
            syncGnomeExtension: SyncGnomeExtension)
    {
    }

    internal Updater(
        string version,
        InstallLocation? installed,
        string? offReason,
        string? assetName,
        string? sourceOverride,
        HttpClient http,
        Func<bool> getBeta,
        Action<Action>? onBetaChanged,
        Func<string, string, CancellationToken, Task<string?>> verify,
        Action<string, bool>? launchReplacement = null,
        Action<string>? syncGnomeExtension = null)
    {
        Version = version;
        _installed = installed;
        OffReason = offReason;
        _assetName = assetName;
        SourceOverride = sourceOverride;
        _http = http;
        _getBeta = getBeta;
        _onBetaChanged = onBetaChanged;
        _verify = verify;
        _launchReplacement = launchReplacement;
        _syncGnomeExtension = syncGnomeExtension;
        State = IsInstalled ? Phase.Waiting : Phase.Off;
    }

    /// <summary>One-line summary for `usagetop doctor`.</summary>
    public string Describe() => IsInstalled
        ? $"on ({_installed!.AppDir}, {_assetName}{(SourceOverride != null ? $", source {SourceOverride}" : "")})"
        : $"off ({OffReason ?? "unsupported architecture"})";

    /// <summary>
    /// Starts the schedule. Called once by the app at startup. True when an
    /// update an earlier run left waiting has just gone in and the new version
    /// is starting: the caller should dispose its socket and quit.
    /// </summary>
    public bool Start(bool hidden = false)
    {
        if (!IsInstalled || _started) return false;
        _started = true;
        _beta = _getBeta();

        // Any backup folder left behind by the previous update can go now.
        TryDeleteDirectory(_installed!.BackupDir);

        if (PendingUpdate() is { } pending)
        {
            _staged = pending;
            Target = pending.Version.Text;
            Set(Phase.Ready);
            if (Install(relaunch: true, hidden)) return true;
        }

        _timer = new Timer(_ => _ = CheckAsync(), null, FirstLook, Period);
        _onBetaChanged?.Invoke(() =>
        {
            var wanted = _getBeta();
            if (wanted == _beta) return;
            _beta = wanted;
            _ = CheckAsync();
        });
        return false;
    }

    /// <summary>
    /// One look, and a download if it finds something. A request while one is
    /// running is folded into a second look after it, so the beta switch is
    /// always answered with the setting it ended on.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellation = default)
    {
        if (!IsInstalled || _handedOver) return;
        if (_busy)
        {
            _again = true;
            return;
        }
        _busy = true;
        RaiseChanged();
        try
        {
            do
            {
                _again = false;
                await CheckOnceAsync(cancellation);
            } while (_again && !cancellation.IsCancellationRequested);
        }
        finally
        {
            _busy = false;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Puts the waiting version in and starts it; the caller then quits. False
    /// when there was nothing to put in or it could not be done.
    /// </summary>
    public bool RestartToUpdate(bool hidden = false) => Install(relaunch: true, hidden);

    /// <summary>Puts the waiting version in quietly as the app leaves.</summary>
    public void ApplyOnQuit() => _ = Install(relaunch: false, hidden: false);

    // ---- Looking ----

    private async Task CheckOnceAsync(CancellationToken cancellation)
    {
        var beta = _getBeta();

        // A beta fetched before the switch was turned off is not put in behind the user's back.
        if (!beta && _staged is { Version.IsPrerelease: true })
        {
            DiscardStaged();
        }

        // Keep "ready" on screen while looking for something newer still.
        if (_staged == null) Set(Phase.Checking);

        try
        {
            var newest = await NewestReleaseAsync(beta, cancellation);
            CheckedAt = DateTimeOffset.UtcNow;
            var current = SemanticVersion.TryParse(Version, out var parsed) ? parsed : SemanticVersion.Zero;

            if (newest == null || newest.Value.Version <= current)
            {
                Failure = FailureDetail = null;
                Set(_staged != null ? Phase.Ready : Phase.UpToDate);
                return;
            }

            if (_staged != null && _staged.Version >= newest.Value.Version)
            {
                Failure = FailureDetail = null;
                Set(Phase.Ready);
                return;
            }

            Target = newest.Value.Version.Text;
            Progress = 0;
            Set(Phase.Downloading);

            var folder = await DownloadAsync(newest.Value.Asset, newest.Value.Version.Text, cancellation);
            _staged = new StagedUpdate(folder, newest.Value.Version);
            Progress = 100;
            Failure = FailureDetail = null;
            Set(Phase.Ready);
            WriteLog($"Update {newest.Value.Version.Text} is ready");
        }
        catch (UpdateFailure problem)
        {
            Fail(problem.Line, problem.Detail);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            Fail("Couldn't check for updates", Explain(exception));
        }
    }

    private async Task<(ReleaseAsset Asset, SemanticVersion Version)?> NewestReleaseAsync(bool beta, CancellationToken cancellation)
    {
        var baseUrl = (SourceOverride ?? ApiBase).TrimEnd('/');
        var endpoint = baseUrl + (beta ? "/releases?per_page=30" : "/releases/latest");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new UpdateFailure("Couldn't check for updates", $"{baseUrl} is not an address");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("User-Agent", $"usagetop-linux/{Version}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        var status = response.StatusCode;
        // No release at all yet, or a repository that cannot be seen.
        if (status == HttpStatusCode.NotFound) return null;
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new UpdateFailure("Couldn't check for updates", "GitHub is limiting requests from this address; it will be tried again later");
        }
        if (status != HttpStatusCode.OK)
        {
            throw new UpdateFailure("Couldn't check for updates", $"GitHub answered {(int)status}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellation);
        List<Release> releases;
        if (beta)
        {
            releases = await JsonSerializer.DeserializeAsync<List<Release>>(body, JsonOptions, cancellation) ?? [];
        }
        else
        {
            var single = await JsonSerializer.DeserializeAsync<Release>(body, JsonOptions, cancellation);
            releases = single == null ? [] : [single];
        }

        (ReleaseAsset Asset, SemanticVersion Version)? best = null;
        foreach (var release in releases)
        {
            if (release.Draft == true) continue;
            if (!beta && release.Prerelease == true) continue;
            if (!SemanticVersion.TryParse(release.TagName, out var version)) continue;
            var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, _assetName, StringComparison.Ordinal));
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl)) continue;
            if (best == null || version > best.Value.Version) best = (asset, version);
        }
        return best;
    }

    // ---- Downloading & verifying ----

    private async Task<string> DownloadAsync(ReleaseAsset asset, string versionName, CancellationToken cancellation)
    {
        var updatesDir = _installed!.UpdatesDir;
        Directory.CreateDirectory(updatesDir);
        var tempArchive = Path.Combine(updatesDir, $"download-{Guid.NewGuid():N}.tar.gz");
        var tempStage = Path.Combine(updatesDir, $".stage-{Guid.NewGuid():N}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.Add("User-Agent", $"usagetop-linux/{Version}");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateFailure($"Couldn't download {versionName}", $"The download answered {(int)response.StatusCode}");
            }

            var total = response.Content.Headers.ContentLength;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var remote = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var file = File.Create(tempArchive))
            {
                var buffer = new byte[64 * 1024];
                long received = 0;
                int read;
                while ((read = await remote.ReadAsync(buffer, cancellation)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    hasher.AppendData(buffer, 0, read);
                    received += read;
                    if (total is > 0)
                    {
                        var percent = (int)Math.Clamp(received * 100 / total.Value, 0, 100);
                        if (percent != Progress)
                        {
                            Progress = percent;
                            RaiseChanged();
                        }
                    }
                }
            }

            // GitHub lists each file's SHA-256; a testing source may leave it out.
            if (asset.Digest is { } listed && listed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var expected = listed["sha256:".Length..].Trim().ToLowerInvariant();
                var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    throw new UpdateFailure($"The download of {versionName} was damaged", "Its SHA-256 is not the one the release lists");
                }
            }

            ExtractTarGz(tempArchive, tempStage);

            var reason = await _verify(tempStage, versionName, cancellation);
            if (reason != null)
            {
                throw new UpdateFailure($"The download of {versionName} was not usable", reason);
            }

            // Clear any older staged versions once the new one has passed every check.
            var finalFolder = Path.Combine(updatesDir, versionName);
            var finalApp = Path.Combine(finalFolder, "usagetop");
            foreach (var entry in Directory.EnumerateFileSystemEntries(updatesDir))
            {
                if (string.Equals(entry, tempStage, StringComparison.Ordinal)) continue;
                TryDeletePath(entry);
            }
            Directory.CreateDirectory(finalFolder);
            Directory.Move(tempStage, finalApp);
            return finalApp;
        }
        finally
        {
            TryDeletePath(tempArchive);
            TryDeleteDirectory(tempStage);
        }
    }

    /// <summary>
    /// Extracts a .tar.gz archive into <paramref name="destination"/>, rejecting
    /// path traversal, absolute paths and symlinks that escape the folder. When
    /// every entry lives under a single top-level folder (such as `usagetop/`),
    /// that wrapper is stripped so `destination/usagetop` is the executable.
    /// </summary>
    internal static void ExtractTarGz(string archivePath, string destination)
    {
        // First pass: check whether all entries share one top-level folder (like "usagetop/").
        string? stripPrefix = null;
        var hasEntries = false;
        using (var probeFile = File.OpenRead(archivePath))
        using (var probeGzip = new GZipStream(probeFile, CompressionMode.Decompress))
        using (var probeTar = new TarReader(probeGzip))
        {
            var shared = true;
            while (probeTar.GetNextEntry() is { } entry)
            {
                var normalized = NormalizeTarPath(entry.Name);
                if (normalized.Length == 0) continue;
                hasEntries = true;
                var slash = normalized.IndexOf('/');
                if (slash < 0 && entry.EntryType != TarEntryType.Directory)
                {
                    shared = false;
                    break;
                }
                var top = slash < 0 ? normalized : normalized[..slash];
                if (stripPrefix == null) stripPrefix = top;
                else if (!string.Equals(stripPrefix, top, StringComparison.Ordinal))
                {
                    shared = false;
                    break;
                }
            }
            if (!shared) stripPrefix = null;
        }

        if (!hasEntries) throw new UpdateFailure("The download was damaged", "The archive is empty");

        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            var normalized = NormalizeTarPath(entry.Name);
            if (normalized.Length == 0) continue;

            if (stripPrefix != null)
            {
                if (string.Equals(normalized, stripPrefix, StringComparison.Ordinal)) continue;
                if (normalized.StartsWith(stripPrefix + "/", StringComparison.Ordinal))
                {
                    normalized = normalized[(stripPrefix.Length + 1)..];
                }
            }
            if (normalized.Length == 0) continue;

            var target = Path.GetFullPath(Path.Combine(root, normalized));
            if (!target.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new UpdateFailure("The download was damaged", $"Archive entry '{entry.Name}' escapes the install folder");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var output = File.Create(target))
                    {
                        entry.DataStream?.CopyTo(output);
                    }
                    if (!OperatingSystem.IsWindows())
                    {
                        var mode = entry.Mode != UnixFileMode.None
                            ? entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                          | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                          | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)
                            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                        // Ensure the owner can always read and write their own unpacked files.
                        mode |= UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        File.SetUnixFileMode(target, mode);
                    }
                    break;

                case TarEntryType.SymbolicLink:
                    var linkTarget = entry.LinkName;
                    if (string.IsNullOrEmpty(linkTarget) || Path.IsPathRooted(linkTarget))
                    {
                        throw new UpdateFailure("The download was damaged", $"Archive symlink '{entry.Name}' points outside the install folder");
                    }
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, linkTarget));
                    if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal) && !string.Equals(resolved, root, StringComparison.Ordinal))
                    {
                        throw new UpdateFailure("The download was damaged", $"Archive symlink '{entry.Name}' escapes the install folder");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.CreateSymbolicLink(target, linkTarget);
                    break;

                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;

                default:
                    throw new UpdateFailure("The download was damaged", $"Unexpected archive entry type {entry.EntryType}");
            }
        }
    }

    private static string NormalizeTarPath(string raw)
    {
        var path = raw.Replace('\\', '/').Trim();
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        path = path.TrimEnd('/');
        if (path is "" or ".") return "";
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new UpdateFailure("The download was damaged", $"Archive entry '{raw}' is an absolute path");
        }
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                throw new UpdateFailure("The download was damaged", $"Archive entry '{raw}' traverses outside the install folder");
            }
        }
        return path;
    }

    /// <summary>
    /// Runs `<folder>/usagetop --version` to verify the download is a working
    /// binary for this machine and reports the expected version. Returns null
    /// when valid, or a short reason otherwise.
    /// </summary>
    internal static async Task<string?> VerifyBinaryAsync(string folder, string expectedVersion, CancellationToken cancellation)
    {
        var binary = Path.Combine(folder, "usagetop");
        if (!File.Exists(binary)) return "It holds no usagetop executable";

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(binary);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
            {
                return "usagetop is not marked executable";
            }
        }

        try
        {
            var start = new ProcessStartInfo(binary, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process == null) return "usagetop could not be started";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(VerifyTimeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "usagetop --version timed out";
            }

            var output = (await stdoutTask).Trim();
            _ = await stderrTask;
            if (process.ExitCode != 0) return $"usagetop --version exited with {process.ExitCode}";
            if (!string.Equals(output, expectedVersion, StringComparison.Ordinal))
            {
                return $"It reports version '{output}', expected '{expectedVersion}'";
            }
            return null;
        }
        catch (Exception exception)
        {
            return $"usagetop could not be checked ({exception.Message})";
        }
    }

    /// <summary>
    /// A download an earlier run checked and left waiting, if it is still newer
    /// than what is running and still whole. Anything else in the folder is
    /// cleared away.
    /// </summary>
    internal StagedUpdate? PendingUpdate()
    {
        if (!IsInstalled) return null;
        var updatesDir = _installed!.UpdatesDir;
        if (!Directory.Exists(updatesDir)) return null;

        var current = SemanticVersion.TryParse(Version, out var parsed) ? parsed : SemanticVersion.Zero;
        var beta = _getBeta();

        StagedUpdate? best = null;
        foreach (var folder in Directory.EnumerateDirectories(updatesDir))
        {
            var name = Path.GetFileName(folder);
            if (!SemanticVersion.TryParse(name, out var version)) continue;
            if (version <= current) continue;
            if (!beta && version.IsPrerelease) continue;
            var app = Path.Combine(folder, "usagetop");
            if (!Directory.Exists(app)) continue;
            if (best == null || version > best.Version) best = new StagedUpdate(app, version);
        }

        if (best != null)
        {
            var reason = _verify(best.Folder, best.Version.Text, CancellationToken.None).GetAwaiter().GetResult();
            if (reason == null) return best;
        }

        TryDeleteDirectory(updatesDir);
        return null;
    }

    // ---- Installing ----

    /// <summary>
    /// Swaps the staged folder in for the running one. On Linux a running
    /// process keeps using its open binary and libraries across rename(2), so
    /// the swap is safe live; the next start runs the new version.
    /// </summary>
    internal bool Install(bool relaunch, bool hidden)
    {
        if (!IsInstalled || _staged == null || _handedOver) return false;
        var staged = _staged;
        var appDir = _installed!.AppDir;
        var backupDir = _installed.BackupDir;

        var reason = _verify(staged.Folder, staged.Version.Text, CancellationToken.None).GetAwaiter().GetResult();
        if (reason != null)
        {
            DiscardStaged();
            Fail("Couldn't install the update", reason);
            return false;
        }

        try
        {
            TryDeleteDirectory(backupDir);
            Directory.Move(appDir, backupDir);
            try
            {
                Directory.Move(staged.Folder, appDir);
            }
            catch
            {
                // Put the working copy back before reporting the failure.
                if (!Directory.Exists(appDir) && Directory.Exists(backupDir))
                {
                    try { Directory.Move(backupDir, appDir); } catch { }
                }
                throw;
            }
        }
        catch (Exception exception)
        {
            Fail("Couldn't install the update", exception.Message);
            return false;
        }

        _handedOver = true;
        _staged = null;
        TryDeleteDirectory(_installed.UpdatesDir);
        TryDeleteDirectory(backupDir);
        try { _syncGnomeExtension?.Invoke(appDir); } catch { }
        WriteLog($"Installed {staged.Version.Text} over {Version}");

        if (relaunch)
        {
            _launchReplacement?.Invoke(Path.Combine(appDir, "usagetop"), hidden);
        }
        return true;
    }

    private static void SpawnRelaunch(string binaryPath, bool hidden)
    {
        // Started once this copy has gone, so the two never contend for the IPC socket.
        var script = hidden
            ? "pid=\"$1\"; bin=\"$2\"; while kill -0 \"$pid\" 2>/dev/null; do sleep 0.1; done; exec \"$bin\" --hidden"
            : "pid=\"$1\"; bin=\"$2\"; while kill -0 \"$pid\" 2>/dev/null; do sleep 0.1; done; exec \"$bin\"";
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("usagetop-relaunch");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(binaryPath);
        try
        {
            Process.Start(start);
        }
        catch (Exception exception)
        {
            WriteLog($"Could not start updated usagetop: {exception.Message}");
        }
    }

    /// <summary>
    /// If the release carries the GNOME extension and the user has a regular
    /// (non-symlink) copy installed in ~/.local/share/gnome-shell/extensions,
    /// refresh its files so the top bar stays in step with the app.
    /// </summary>
    private static void SyncGnomeExtension(string appDir)
    {
        const string uuid = "usagetop@rzukic.github.io";
        var source = Path.Combine(appDir, "gnome-extension", uuid);
        if (!Directory.Exists(source)) return;

        var dest = Path.Combine(XdgDataHome(), "gnome-shell", "extensions", uuid);
        if (!Directory.Exists(dest)) return;
        if ((File.GetAttributes(dest) & FileAttributes.ReparsePoint) != 0) return;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ---- Install detection ----

    internal sealed record InstallLocation(string AppDir, string UpdatesDir, string BackupDir);

    internal sealed record StagedUpdate(string Folder, SemanticVersion Version);

    internal static string? AssetFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "usagetop-linux-x64.tar.gz",
        Architecture.Arm64 => "usagetop-linux-arm64.tar.gz",
        _ => null,
    };

    /// <summary>
    /// Where this running copy lives, if it is in a dedicated user-writable
    /// install folder that may be replaced. Build outputs, Downloads, Flatpak,
    /// Snap and system-managed folders (/usr/bin) return null.
    /// </summary>
    internal static InstallLocation? DetectInstall(
        string? processPath,
        out string? offReason,
        bool? isLinux = null,
        string? homeDir = null,
        string? xdgDataHome = null,
        bool? isContainerized = null,
        Func<string, bool>? isWritable = null)
    {
        offReason = null;
        if (!(isLinux ?? OperatingSystem.IsLinux()))
        {
            offReason = "Self-updates run on Linux installs only";
            return null;
        }

        // A Snap or an AppImage passes its variables on to everything it starts,
        // so a copy started from a terminal inside one (T3 Code is an AppImage)
        // would see them too: it counts only when this binary is in the package.
        var binary = string.IsNullOrWhiteSpace(processPath) ? null : ResolveBinary(processPath);
        var sandboxed = isContainerized ?? (File.Exists("/.flatpak-info")
            || IsInside(binary, Environment.GetEnvironmentVariable("SNAP"))
            || IsInside(binary, Environment.GetEnvironmentVariable("APPDIR")));
        if (sandboxed)
        {
            offReason = "Managed by its package format, so it does not update itself";
            return null;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            offReason = "Install usagetop to ~/.local/share/usagetop or ~/Applications and it updates itself";
            return null;
        }

        var resolved = ResolveBinary(processPath);
        if (!string.Equals(Path.GetFileName(resolved), "usagetop", StringComparison.Ordinal))
        {
            offReason = "Install usagetop to ~/.local/share/usagetop or ~/Applications and it updates itself";
            return null;
        }

        var appDir = TrimSlash(Path.GetDirectoryName(resolved) ?? "");
        if (string.IsNullOrEmpty(appDir))
        {
            offReason = "Install usagetop to ~/.local/share/usagetop or ~/Applications and it updates itself";
            return null;
        }

        // Never touch a build output or source checkout.
        var normalized = appDir.Replace('\\', '/');
        if (normalized.Contains("/bin/Debug/", StringComparison.Ordinal)
            || normalized.Contains("/bin/Release/", StringComparison.Ordinal)
            || HasAncestorFile(appDir, "AiUsage.Linux.csproj", maxLevels: 4))
        {
            offReason = "Not an installed copy, so it does not update itself";
            return null;
        }

        var home = TrimSlash(homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var dataHome = TrimSlash(xdgDataHome ?? XdgDataHome(home));

        var xdgApp = Path.Combine(dataHome, "usagetop", "app");
        var xdgRoot = Path.Combine(dataHome, "usagetop");
        var userApps = Path.Combine(home, "Applications", "usagetop");
        const string optApp = "/opt/usagetop";

        var recognized = string.Equals(appDir, xdgApp, StringComparison.Ordinal)
            || string.Equals(appDir, xdgRoot, StringComparison.Ordinal)
            || string.Equals(appDir, userApps, StringComparison.Ordinal)
            || string.Equals(appDir, optApp, StringComparison.Ordinal);

        if (!recognized)
        {
            offReason = normalized.StartsWith("/usr/", StringComparison.Ordinal) || normalized.StartsWith("/bin", StringComparison.Ordinal)
                ? "Managed outside your home folder, so it does not update itself"
                : "Install usagetop to ~/.local/share/usagetop or ~/Applications and it updates itself";
            return null;
        }

        var parentDir = TrimSlash(Path.GetDirectoryName(appDir) ?? "");
        var checkWritable = isWritable ?? CanWriteDirectory;
        if (string.IsNullOrEmpty(parentDir) || !checkWritable(appDir) || !checkWritable(parentDir))
        {
            offReason = "Managed outside your home folder, so it does not update itself";
            return null;
        }

        // Keep staging in the same parent folder so Directory.Move is an atomic same-filesystem rename.
        var updatesDir = string.Equals(appDir, xdgApp, StringComparison.Ordinal)
            ? Path.Combine(parentDir, "updates")
            : Path.Combine(parentDir, ".usagetop-updates");
        var backupDir = Path.Combine(parentDir, ".usagetop-old");
        return new InstallLocation(appDir, updatesDir, backupDir);
    }

    /// <summary>True when <paramref name="path"/> lies inside the folder <paramref name="root"/>.</summary>
    internal static bool IsInside(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(root)) return false;
        var folder = TrimSlash(Path.GetFullPath(root));
        return path.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    private static string ResolveBinary(string path)
    {
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
            {
                return Path.GetFullPath(target.FullName);
            }
        }
        catch (IOException)
        {
        }
        return Path.GetFullPath(path);
    }

    private static bool HasAncestorFile(string folder, string fileName, int maxLevels)
    {
        var current = folder;
        for (var i = 0; i <= maxLevels && !string.IsNullOrEmpty(current); i++)
        {
            if (File.Exists(Path.Combine(current, fileName))) return true;
            current = Path.GetDirectoryName(current) ?? "";
        }
        return false;
    }

    private static bool CanWriteDirectory(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            var probe = Path.Combine(folder, $".usagetop-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string XdgDataHome(string? home = null) =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string TrimSlash(string path) =>
        path.Length > 1 ? path.TrimEnd('/', '\\') : path;

    // ---- Helpers ----

    private void DiscardStaged()
    {
        _staged = null;
        Target = null;
        if (_installed != null) TryDeleteDirectory(_installed.UpdatesDir);
    }

    private void Fail(string line, string detail)
    {
        WriteLog($"{line}: {detail}");
        Failure = line;
        FailureDetail = detail;
        // An update already downloaded is still good; a failed look does not hide it.
        if (_staged != null)
        {
            Target = _staged.Version.Text;
            Set(Phase.Ready);
            return;
        }
        Set(Phase.Failed);
    }

    private static string Explain(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "No releases were found where the app looks for them.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub is limiting requests from this address; it will be tried again later.",
        HttpRequestException { StatusCode: null } or TaskCanceledException => "No connection to the update server.",
        HttpRequestException { StatusCode: { } status } => $"The update server answered {(int)status}.",
        _ => exception.Message,
    };

    private void Set(Phase phase)
    {
        State = phase;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static void WriteLog(string message)
    {
        try
        {
            AppPaths.Ensure();
            File.AppendAllText(AppPaths.Log, $"{DateTimeOffset.Now:O} [update] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string? ReadSourceOverride() =>
        Environment.GetEnvironmentVariable(SourceVariable) is { } value && value.Trim().Length > 0
            ? value.Trim()
            : null;

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(2) };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
        [JsonPropertyName("draft")] public bool? Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool? Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<ReleaseAsset>? Assets { get; init; }
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = "";
        [JsonPropertyName("digest")] public string? Digest { get; init; }
    }

    private sealed class UpdateFailure(string line, string detail) : Exception(detail)
    {
        public string Line { get; } = line;
        public string Detail { get; } = detail;
    }
}

/// <summary>
/// A version as releases name them: "1.2.1", "v1.2.1", "1.3.0-beta.2". Ordered
/// as SemVer 2.0 orders them: a pre-release comes before its release.
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    private readonly string[] _pre;

    public static readonly SemanticVersion Zero = new(0, 0, 0, []);

    private SemanticVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    public bool IsPrerelease => _pre is { Length: > 0 };

    public string Text => IsPrerelease
        ? $"{Major}.{Minor}.{Patch}-{string.Join('.', _pre)}"
        : $"{Major}.{Minor}.{Patch}";

    public static bool TryParse(string? raw, out SemanticVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        if (!int.TryParse(parts[1], out var minor) || minor < 0) return false;
        if (!int.TryParse(parts[2], out var patch) || patch < 0) return false;

        string[] pre = [];
        if (dash >= 0)
        {
            var preText = text[(dash + 1)..];
            if (preText.Length == 0) return false;
            pre = preText.Split('.');
            if (pre.Any(p => p.Length == 0)) return false;
        }

        version = new SemanticVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        var left = _pre ?? [];
        var right = other._pre ?? [];
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        var count = Math.Min(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            if (left[i] == right[i]) continue;
            var leftIsNum = int.TryParse(left[i], out var ln);
            var rightIsNum = int.TryParse(right[i], out var rn);
            if (leftIsNum && rightIsNum) return ln.CompareTo(rn);
            if (leftIsNum) return -1;
            if (rightIsNum) return 1;
            return string.CompareOrdinal(left[i], right[i]);
        }
        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre ?? []));
    public override string ToString() => Text;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
}
