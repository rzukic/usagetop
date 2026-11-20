using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiUsage.Linux.Tests;

public sealed class UpdaterTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"usagetop-updater-tests-{Guid.NewGuid():N}");

    public UpdaterTests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    // ---- SemanticVersion ----

    [Theory]
    [InlineData("1.2.1", "1.2.1", false)]
    [InlineData("v1.2.1", "1.2.1", false)]
    [InlineData("1.3.0-beta.2", "1.3.0-beta.2", true)]
    [InlineData("v2.0.0-rc.1+abc123", "2.0.0-rc.1", true)]
    public void SemanticVersion_ParsesValidStrings(string raw, string expectedText, bool expectedPre)
    {
        Assert.True(SemanticVersion.TryParse(raw, out var version));
        Assert.Equal(expectedText, version.Text);
        Assert.Equal(expectedPre, version.IsPrerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("not-a-version")]
    public void SemanticVersion_RejectsInvalidStrings(string? raw)
    {
        Assert.False(SemanticVersion.TryParse(raw, out _));
    }

    [Fact]
    public void SemanticVersion_OrdersAccordingToSemVer()
    {
        var ordered = new[]
        {
            "1.2.0",
            "1.2.1-alpha.1",
            "1.2.1-alpha.2",
            "1.2.1-beta.1",
            "1.2.1-beta.2",
            "1.2.1-beta.10",
            "1.2.1",
            "1.10.0",
        }.Select(s =>
        {
            Assert.True(SemanticVersion.TryParse(s, out var v));
            return v;
        }).ToList();

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            Assert.True(ordered[i] < ordered[i + 1], $"Expected {ordered[i]} < {ordered[i + 1]}");
        }
    }

    // ---- Install detection ----

    [Fact]
    public void DetectInstall_AcceptsXdgAppAndUserApplicationsAndWritableOpt()
    {
        var home = Path.Combine(_temp, "home");
        var xdgData = Path.Combine(home, ".local", "share");

        var xdgApp = Updater.DetectInstall(
            Path.Combine(xdgData, "usagetop", "app", "usagetop"),
            out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true);
        Assert.NotNull(xdgApp);
        Assert.Equal(Path.Combine(xdgData, "usagetop", "app"), xdgApp!.AppDir);
        Assert.Equal(Path.Combine(xdgData, "usagetop", "updates"), xdgApp.UpdatesDir);

        var userApps = Updater.DetectInstall(
            Path.Combine(home, "Applications", "usagetop", "usagetop"),
            out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true);
        Assert.NotNull(userApps);
        Assert.Equal(Path.Combine(home, "Applications", ".usagetop-updates"), userApps!.UpdatesDir);

        var opt = Updater.DetectInstall(
            "/opt/usagetop/usagetop",
            out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true);
        Assert.NotNull(opt);
    }

    [Fact]
    public void IsInside_CountsOnlyWhatIsInThePackage()
    {
        // Started from a terminal inside T3 Code's AppImage: APPDIR is T3's mount.
        Assert.False(Updater.IsInside("/home/u/.local/share/usagetop/app/usagetop", "/tmp/.mount_T3-CoabcDE"));
        Assert.True(Updater.IsInside("/tmp/.mount_usagetXYZ/usr/bin/usagetop", "/tmp/.mount_usagetXYZ"));
        Assert.True(Updater.IsInside("/snap/usagetop/12/usagetop", "/snap/usagetop/12/"));
        Assert.False(Updater.IsInside("/snap/usagetop-other/usagetop", "/snap/usagetop"));
        Assert.False(Updater.IsInside("/home/u/.local/share/usagetop/app/usagetop", null));
        Assert.False(Updater.IsInside("/home/u/.local/share/usagetop/app/usagetop", ""));
        Assert.False(Updater.IsInside(null, "/snap/usagetop/12"));
    }

    [Fact]
    public void DetectInstall_RejectsSystemBuildDownloadsAndSandbox()
    {
        var home = Path.Combine(_temp, "home");
        var xdgData = Path.Combine(home, ".local", "share");

        // /usr/bin
        Assert.Null(Updater.DetectInstall("/usr/bin/usagetop", out var usrReason,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true));
        Assert.Contains("outside your home folder", usrReason);

        // ~/.local/bin directly (shared bin folder)
        Assert.Null(Updater.DetectInstall(Path.Combine(home, ".local", "bin", "usagetop"), out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true));

        // Build output
        Assert.Null(Updater.DetectInstall(Path.Combine(home, "src", "bin", "Release", "net9.0", "linux-x64", "usagetop"), out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true));

        // Flatpak/Snap/AppImage
        Assert.Null(Updater.DetectInstall(Path.Combine(xdgData, "usagetop", "app", "usagetop"), out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: true, isWritable: _ => true));

        // Read-only install dir
        Assert.Null(Updater.DetectInstall(Path.Combine(xdgData, "usagetop", "app", "usagetop"), out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => false));

        // Non-Linux
        Assert.Null(Updater.DetectInstall(Path.Combine(xdgData, "usagetop", "app", "usagetop"), out _,
            isLinux: false, homeDir: home, xdgDataHome: xdgData, isContainerized: false, isWritable: _ => true));
    }

    [Fact]
    public void DetectInstall_FollowsSymlinkFromLocalBinToXdgInstall()
    {
        var home = Path.Combine(_temp, "symlink-home");
        var xdgData = Path.Combine(home, ".local", "share");
        var appDir = Path.Combine(xdgData, "usagetop", "app");
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(binDir);

        var realBinary = Path.Combine(appDir, "usagetop");
        File.WriteAllText(realBinary, "#!/bin/sh\n");
        var link = Path.Combine(binDir, "usagetop");
        File.CreateSymbolicLink(link, realBinary);

        var location = Updater.DetectInstall(link, out _,
            isLinux: true, homeDir: home, xdgDataHome: xdgData, isContainerized: false);
        Assert.NotNull(location);
        Assert.Equal(Path.GetFullPath(appDir), location!.AppDir);
    }

    // ---- Archive extraction ----

    [Fact]
    public void ExtractTarGz_StripsTopLevelFolderAndPreservesExecutableBit()
    {
        var archive = Path.Combine(_temp, "release.tar.gz");
        WriteTarGz(archive,
        [
            ("usagetop/usagetop", Encoding.UTF8.GetBytes("#!/bin/sh\necho 1.6.1\n"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            ("usagetop/lib/data.txt", Encoding.UTF8.GetBytes("hello"), UnixFileMode.UserRead | UnixFileMode.UserWrite),
        ]);

        var dest = Path.Combine(_temp, "unpacked");
        Updater.ExtractTarGz(archive, dest);

        var bin = Path.Combine(dest, "usagetop");
        Assert.True(File.Exists(bin));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "lib", "data.txt")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(bin).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void ExtractTarGz_StripsTopLevelFolderWhenArchiveIncludesDirectoryEntry()
    {
        var archive = Path.Combine(_temp, "release-with-dir.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "usagetop/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "usagetop/usagetop")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho 1.6.1\n")),
            });
        }

        var dest = Path.Combine(_temp, "unpacked-with-dir");
        Updater.ExtractTarGz(archive, dest);
        Assert.True(File.Exists(Path.Combine(dest, "usagetop")));
    }

    [Fact]
    public void ExtractTarGz_RejectsPathTraversal()
    {
        var archive = Path.Combine(_temp, "evil.tar.gz");
        WriteTarGz(archive,
        [
            ("usagetop/../../escape.txt", Encoding.UTF8.GetBytes("bad"), UnixFileMode.UserRead | UnixFileMode.UserWrite),
        ]);

        var dest = Path.Combine(_temp, "unpacked-evil");
        Assert.ThrowsAny<Exception>(() => Updater.ExtractTarGz(archive, dest));
        Assert.False(File.Exists(Path.Combine(_temp, "escape.txt")));
    }

    // ---- End-to-end updater flow with mock HTTP ----

    [Fact]
    public async Task CheckAndInstall_DownloadsVerifiesAndAtomicallySwaps()
    {
        var (location, appDir) = MakeInstalledFixture("1.6.0");
        var archiveBytes = MakeArchiveBytes("1.6.1");
        var sha256 = Sha256Hex(archiveBytes);

        var relaunchedPath = (string?)null;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    tag_name = "v1.6.1",
                    draft = false,
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "usagetop-linux-x64.tar.gz",
                            browser_download_url = "https://mock.test/download/usagetop-linux-x64.tar.gz",
                            digest = $"sha256:{sha256}",
                        },
                    },
                });
            }
            if (req.RequestUri.AbsolutePath.EndsWith("/usagetop-linux-x64.tar.gz", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var updater = new Updater(
            version: "1.6.0",
            installed: location,
            offReason: null,
            assetName: "usagetop-linux-x64.tar.gz",
            sourceOverride: "https://mock.test",
            http: new HttpClient(handler),
            getBeta: () => false,
            onBetaChanged: null,
            verify: (folder, expected, _) =>
            {
                var marker = File.ReadAllText(Path.Combine(folder, "version.txt")).Trim();
                return Task.FromResult<string?>(marker == expected ? null : $"got {marker}");
            },
            launchReplacement: (bin, _) => relaunchedPath = bin);

        await updater.CheckAsync();
        Assert.Equal(Updater.Phase.Ready, updater.State);
        Assert.Equal("1.6.1", updater.Target);
        Assert.Equal(100, updater.Progress);

        // Apply and verify atomic swap + relaunch path.
        Assert.True(updater.RestartToUpdate());
        Assert.Equal("1.6.1", File.ReadAllText(Path.Combine(appDir, "version.txt")).Trim());
        Assert.Equal(Path.Combine(appDir, "usagetop"), relaunchedPath);
        Assert.False(Directory.Exists(location.BackupDir));
        Assert.False(Directory.Exists(location.UpdatesDir));
    }

    [Fact]
    public async Task CheckAsync_RejectsBadSha256()
    {
        var (location, _) = MakeInstalledFixture("1.6.0");
        var archiveBytes = MakeArchiveBytes("1.6.1");

        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    tag_name = "v1.6.1",
                    draft = false,
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "usagetop-linux-x64.tar.gz",
                            browser_download_url = "https://mock.test/download/usagetop-linux-x64.tar.gz",
                            digest = "sha256:" + new string('0', 64),
                        },
                    },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) };
        });

        var updater = new Updater(
            version: "1.6.0",
            installed: location,
            offReason: null,
            assetName: "usagetop-linux-x64.tar.gz",
            sourceOverride: "https://mock.test",
            http: new HttpClient(handler),
            getBeta: () => false,
            onBetaChanged: null,
            verify: (_, _, _) => Task.FromResult<string?>(null));

        await updater.CheckAsync();
        Assert.Equal(Updater.Phase.Failed, updater.State);
        Assert.Contains("damaged", updater.Failure);
        Assert.Contains("SHA-256", updater.FailureDetail);
    }

    [Fact]
    public async Task CheckAsync_BetaToggleDownloadsPrereleaseAndDiscardsWhenTurnedOff()
    {
        var (location, _) = MakeInstalledFixture("1.6.0");
        var betaArchive = MakeArchiveBytes("1.7.0-beta.1");
        var beta = true;

        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/releases?per_page=30", StringComparison.Ordinal))
            {
                return JsonResponse(new object[]
                {
                    new
                    {
                        tag_name = "v1.7.0-beta.1",
                        draft = false,
                        prerelease = true,
                        assets = new[]
                        {
                            new
                            {
                                name = "usagetop-linux-arm64.tar.gz",
                                browser_download_url = "https://mock.test/beta.tar.gz",
                                digest = $"sha256:{Sha256Hex(betaArchive)}",
                            },
                        },
                    },
                    new
                    {
                        tag_name = "v1.6.0",
                        draft = false,
                        prerelease = false,
                        assets = Array.Empty<object>(),
                    },
                });
            }
            if (req.RequestUri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    tag_name = "v1.6.0",
                    draft = false,
                    prerelease = false,
                    assets = Array.Empty<object>(),
                });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(betaArchive) };
        });

        var updater = new Updater(
            version: "1.6.0",
            installed: location,
            offReason: null,
            assetName: "usagetop-linux-arm64.tar.gz",
            sourceOverride: "https://mock.test",
            http: new HttpClient(handler),
            getBeta: () => beta,
            onBetaChanged: null,
            verify: (_, _, _) => Task.FromResult<string?>(null));

        await updater.CheckAsync();
        Assert.Equal(Updater.Phase.Ready, updater.State);
        Assert.Equal("1.7.0-beta.1", updater.Target);

        // Turning beta off discards the staged prerelease so it is not installed behind the user's back.
        beta = false;
        await updater.CheckAsync();
        Assert.Equal(Updater.Phase.UpToDate, updater.State);
        Assert.Null(updater.Target);
        Assert.False(Directory.Exists(location.UpdatesDir));
    }

    [Fact]
    public async Task CheckAsync_KeepsReadyUpdateWhenSubsequentLookFails()
    {
        var (location, _) = MakeInstalledFixture("1.6.0");
        var archiveBytes = MakeArchiveBytes("1.6.1");
        var failNetwork = false;

        var handler = new StubHttpHandler(req =>
        {
            if (failNetwork) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    tag_name = "v1.6.1",
                    draft = false,
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "usagetop-linux-x64.tar.gz",
                            browser_download_url = "https://mock.test/release.tar.gz",
                            digest = $"sha256:{Sha256Hex(archiveBytes)}",
                        },
                    },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) };
        });

        var updater = new Updater(
            version: "1.6.0",
            installed: location,
            offReason: null,
            assetName: "usagetop-linux-x64.tar.gz",
            sourceOverride: "https://mock.test",
            http: new HttpClient(handler),
            getBeta: () => false,
            onBetaChanged: null,
            verify: (_, _, _) => Task.FromResult<string?>(null));

        await updater.CheckAsync();
        Assert.Equal(Updater.Phase.Ready, updater.State);

        failNetwork = true;
        await updater.CheckAsync();
        Assert.Equal(Updater.Phase.Ready, updater.State);
        Assert.Equal("1.6.1", updater.Target);
    }

    [Fact]
    public void Start_AppliesPendingUpdateFromEarlierRun()
    {
        var (location, appDir) = MakeInstalledFixture("1.6.0");
        var pendingDir = Path.Combine(location.UpdatesDir, "1.6.2", "usagetop");
        Directory.CreateDirectory(pendingDir);
        File.WriteAllText(Path.Combine(pendingDir, "version.txt"), "1.6.2");
        File.WriteAllText(Path.Combine(pendingDir, "usagetop"), "#!/bin/sh\n");

        string? relaunched = null;
        bool? hiddenArg = null;
        var updater = new Updater(
            version: "1.6.0",
            installed: location,
            offReason: null,
            assetName: "usagetop-linux-x64.tar.gz",
            sourceOverride: null,
            http: new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            getBeta: () => false,
            onBetaChanged: null,
            verify: (folder, expected, _) =>
            {
                var actual = File.ReadAllText(Path.Combine(folder, "version.txt")).Trim();
                return Task.FromResult<string?>(actual == expected ? null : $"got {actual}");
            },
            launchReplacement: (bin, hidden) => { relaunched = bin; hiddenArg = hidden; });

        Assert.True(updater.Start(hidden: true));
        Assert.Equal("1.6.2", File.ReadAllText(Path.Combine(appDir, "version.txt")).Trim());
        Assert.Equal(Path.Combine(appDir, "usagetop"), relaunched);
        Assert.True(hiddenArg);
    }

    // ---- Helpers ----

    private (Updater.InstallLocation Location, string AppDir) MakeInstalledFixture(string version)
    {
        var root = Path.Combine(_temp, $"install-{Guid.NewGuid():N}");
        var appDir = Path.Combine(root, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "version.txt"), version);
        File.WriteAllText(Path.Combine(appDir, "usagetop"), "#!/bin/sh\n");
        var location = new Updater.InstallLocation(appDir, Path.Combine(root, "updates"), Path.Combine(root, ".usagetop-old"));
        return (location, appDir);
    }

    private byte[] MakeArchiveBytes(string version)
    {
        var path = Path.Combine(_temp, $"rel-{version}-{Guid.NewGuid():N}.tar.gz");
        WriteTarGz(path,
        [
            ("usagetop/usagetop", Encoding.UTF8.GetBytes("#!/bin/sh\n"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            ("usagetop/version.txt", Encoding.UTF8.GetBytes(version), UnixFileMode.UserRead | UnixFileMode.UserWrite),
        ]);
        return File.ReadAllBytes(path);
    }

    private static void WriteTarGz(string path, IEnumerable<(string Name, byte[] Data, UnixFileMode Mode)> entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (name, data, mode) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = mode,
                DataStream = new MemoryStream(data),
            };
            tar.WriteEntry(entry);
        }
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
