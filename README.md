<img src="docs/usagetop.svg" width="88" alt="">

# usagetop

How much of each AI subscription is left: Claude, ChatGPT, Mistral, Kimi and Muse.
Every limit with its pace and a forecast of when it runs out, in a Windows tray
app, a macOS menu bar app, an Android app and widget, and the server's web dashboard.

| Folder | What it is |
|---|---|
| `my-usage-android/` | The Android app and its home-screen widget (Kotlin) |
| `my-usage-win/` | The Windows tray app (WPF, .NET 9), plus `AiUsage.Core`, the UI-free library |
| `my-usage-mac/` | The macOS menu bar app (SwiftUI, SwiftPM) |
| `my-usage-server/` | An always-on poller with a history database and an HTTP API, built on `AiUsage.Core` |

Each app fetches for itself until it is given a server address in its Settings.
From then on it reads everything from the server, which does the fetching, keeps
the history and holds the sign-ins for both. The server also has a page of its
own: open its address in a browser. See `my-usage-server/README.md`.

## Installing

These links always give the latest release; earlier ones and betas are on the
[Releases page](https://github.com/rzukic/usagetop/releases).

- **Windows:** download
  [`usagetop-win-Setup.exe`](https://github.com/rzukic/usagetop/releases/latest/download/usagetop-win-Setup.exe)
  and run it. It installs for your user only (no admin prompt), adds a Start
  menu entry, and fetches the .NET 9 desktop runtime first if it is missing.
  From then on the app updates itself.
- **Android:** download
  [`usagetop-android.apk`](https://github.com/rzukic/usagetop/releases/latest/download/usagetop-android.apk)
  on the phone and open it; Android asks once to allow installing from your
  browser. From then on the app updates itself: it downloads a new release in
  the background, and Settings → Updates has **Install**. The first time,
  Android asks whether usagetop may install apps and to confirm; after that,
  on Android 12 and newer, updates go in by themselves while the app is not
  on screen.
- **Server:** unpack
  [`usagetop-server-linux-x64.tar.gz`](https://github.com/rzukic/usagetop/releases/latest/download/usagetop-server-linux-x64.tar.gz)
  (or
  [`-arm64`](https://github.com/rzukic/usagetop/releases/latest/download/usagetop-server-linux-arm64.tar.gz))
  and start `./usagetop-server/aiusage-server`; nothing else to install. Or
  run the image, built for x64 and arm64:
  `docker run -d -p 8377:8377 -v usagetop-data:/data ghcr.io/rzukic/usagetop-server`.
  For the settings, see `my-usage-server/README.md`.
- **macOS:** download
  [`usagetop-mac.zip`](https://github.com/rzukic/usagetop/releases/latest/download/usagetop-mac.zip),
  unpack it and move `usagetop.app` to Applications. It needs macOS 26 and runs
  on Apple silicon and Intel. The app is signed with a Developer ID and
  notarized by Apple, so it opens like any other download. From then on it
  updates itself, as long as it is in the Applications folder: it downloads a
  new release in the background and puts it in when you restart or quit
  (Settings → Updates).

## Building and testing

```
# Windows app and shared library
dotnet build my-usage-win/src/AiUsage -c Release
dotnet test  my-usage-win/tests/AiUsage.Core.Tests

# Server
dotnet test  my-usage-server/tests/AiUsage.Server.Tests
dotnet run --project my-usage-server/src/AiUsage.Server

# Android (needs JAVA_HOME pointing at a JDK 17+)
cd my-usage-android
./gradlew testDebugUnitTest assembleDebug

# macOS (needs Xcode 26), into my-usage-mac/usagetop.app
cd my-usage-mac
./build.sh

# Windows installer and update packages, into my-usage-win/build/releases
# (needs: dotnet tool install -g vpk --version 1.2.161)
./my-usage-win/pack.ps1
```

## Releasing

One version number covers the whole project, and each version is one GitHub
Release. For now a release is built on the maintainer's machine:

```
# 1. Bump <Version> in my-usage-win/src/AiUsage/AiUsage.csproj, commit and push
# 2. Build and publish it (needs vpk and gh, see the script's header)
./release.ps1
# 3. On a Mac, add the Mac app and the server's image to that release
#    (needs Xcode 26, gh and Docker, see the script's header)
./release-mac.sh
```

`release.ps1` refuses a version that is not SemVer, runs the tests, and builds
every app it can with that version: the Windows installer with a delta package
against the previous release, the signed Android APK, and the server for Linux
on x64 and arm64. Then it pushes the tag `v<version>` and publishes the release
with generated notes. `./release.ps1 -Version 1.2.1-beta.1` makes a pre-release
instead: the apps offer it only to those who opted into betas, and the download
links above keep pointing at the last stable release.

`release-mac.sh` does what only a Mac can, for the latest release or the one
named (`./release-mac.sh 1.2.1`): it builds the Mac app for Apple silicon and
Intel with that version, zips it and attaches `usagetop-mac.zip` to the
release; then it builds the server's image for `linux/amd64` and
`linux/arm64`, starts it once on an empty volume to see that it serves its page
and can write its data, and pushes it to `ghcr.io/rzukic/usagetop-server` as
`<version>` and `latest` (a pre-release leaves `latest` alone). The Mac app is
signed with the Developer ID certificate in the maintainer's keychain and
notarized by Apple before it is zipped; the script's header says what that
needs. A copy built without a certificate is signed ad hoc: it runs on the Mac
that built it, and anywhere else macOS has to be told to open it (System
Settings → Privacy & Security → Open Anyway).

The Android APK is signed with a key that is kept outside the repository (the
script's header says where). Every release must use the same key, or phones
refuse the update.

`.github/workflows/release.yml` builds the Windows part from a pushed tag on
GitHub Actions, and `ci.yml` builds and tests every push and pull request. Both
are switched off in the repository's Actions settings for the time being.

## One rule that cuts across all three

Claude's and ChatGPT's refresh tokens are single-use. Any session must have
exactly one holder that renews it, or whichever renews first signs the other
out. So the Windows app borrows a CLI's access token rather than copying its
refresh token, the Android app shares one fetch between its worker, widget and
screens, and an app that is pointed at a server stops fetching altogether. A
session reaches the server either by signing in there or by the Windows app
moving its own across, after which the app forgets it.

The provider logic exists twice, in Kotlin and in C#. The arithmetic they share
(combined figure, trendline, projection, history thinning, provider parsing) is
covered by the same tests on both sides; change one and its twin should change
too.

## License

MIT, see `LICENSE`. The Windows app bundles the Inter typeface, under the SIL
Open Font License (`my-usage-win/src/AiUsage/Assets/Fonts/OFL.txt`).

This project is not affiliated with or endorsed by Anthropic, OpenAI, Mistral AI,
Moonshot AI or Meta. Their names and logos are trademarks of their owners and are
shown only to tell the accounts apart. The apps read usage through the same
endpoints the providers' own apps and CLIs use, which are not public APIs and can
change or stop working without notice.
