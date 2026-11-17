# usagetop for Windows

A native Windows tray app that shows how much of each AI subscription is left.
It is a port of the Android app in `../my-usage-android`: same providers, same
combined figure, same drawdown charts, same dark palette.

WPF on .NET 9. No browser, no Electron, no bundled runtime download.

## Run it

```
run.cmd
```

That launches `src/AiUsage/bin/Release/net9.0-windows/AiUsage.exe`, building it
first if it is missing. After changing the code, quit the tray app and rebuild:

```
dotnet build src/AiUsage -c Release
dotnet test tests/AiUsage.Core.Tests
```

The app starts as a tray icon only. Left-click the icon for the flyout,
right-click for the menu, and pick **Open** for the full window.

| Argument | Effect |
|---|---|
| *(none)* | Tray icon only |
| `--open` | Tray icon plus the main window |
| `--flyout` | Tray icon plus the popup |

Launching it again while it is running does not start a second copy: the request
is handed to the running one, so a desktop shortcut works as a "show me the
window" button.

## What it shows

| Provider | Windows | How it authenticates |
|---|---|---|
| Claude | 5h, 7d, Fable 7d | Browser sign-in, or Claude Code's session on loan |
| ChatGPT | 5h, 7d | Browser sign-in, or Codex's session on loan |
| Mistral | API credit, Vibe | API key from the console |
| Kimi | 5h, monthly | API key from the console |
| Meta (Muse) | 5h, weekly | Device code, or `~/.config/muse/auth.json` |

The **All usage** figure averages the windows you can actually code with. Claude's
Fable window and Mistral's API credit are left out: neither is spendable on
ordinary coding work, so counting them would drag the number down for no reason.

## Signing in, and borrowing from a CLI

There are two ways to give the app a Claude or ChatGPT session.

- **Sign in with the browser.** The app gets a session of its own and renews it
  itself. This is the one to use if you want it to just keep working.
- **Import from installed CLIs.** The app borrows the access token Claude Code or
  Codex already has, and reads it from the CLI's file again before every fetch.
  It never renews it: the refresh token behind it is single-use, so an app that
  renewed its own copy would sign the CLI out. When the CLI has not run for a
  while its token lapses and the card says **CLI token expired — sign in**; use
  the CLI again, or sign in with the browser to stop depending on it.

A session of the app's own is never replaced by an import. A refresh token that
an earlier build copied from a CLI is handed back on the next refresh, as long as
neither side has renewed it yet.

The Claude sign-in asks for the `user:profile` scope only, which is all the usage
endpoint needs: the stored token cannot run models or create API keys.

A rejected token is renewed and tried once more before the card reports it, and
the tray icon gains a dot when a provider needs you: something a retry will not
fix, like a token that has to be signed in again.

## Reading from a server

**Settings → Server** takes the address of a usagetop server
(`../my-usage-server`) and its token. With an address saved, the app asks the
server and nothing else: no provider is called, no token is renewed and no CLI
file is read. The charts take over the server's history and keep any older
readings the app already had. Clear the address to go back to fetching here.

- The refresh button has the server ask the providers again; opening a window
  or the 15-minute timer only collects what the server already has.
- If the server cannot be reached, the last reading stays up and the status
  line says so. A refused token also puts the dot on the tray icon.
- Accounts added on the server (a second Claude, a work ChatGPT) appear as
  cards of their own, e.g. "Claude · Work", with their own detail pages. They
  are added and signed in on the server's page, not here.
- **Move this app's sign-ins to the server** sends the server every key and
  session the app holds. Keys stay here as well; a Claude or ChatGPT session is
  removed from the app once the server has it, because it can only have one
  holder. A session borrowed from a CLI is not the app's to move.

## Background behaviour

- Refreshes every 15 minutes while running.
- Refreshes on opening the flyout or the window, unless the reading is seconds old.
- The refresh button spins while a fetch is in flight.
- **Settings → Background → Start with Windows** adds the Run key entry, so the
  tray icon is there after a reboot.

Windows may still throttle a background app on battery or with Battery Saver on.
The reading is cached in `%APPDATA%\AiUsage\snapshot.json`, so the app opens on
the last known numbers and replaces them when the fetch lands.

## Where things live

```
%APPDATA%\AiUsage\snapshot.json     last reading, shared with the tray tooltip
%APPDATA%\AiUsage\history.json      the points behind every chart: five weeks
                                    per window, a quarter for the combined line
%APPDATA%\AiUsage\credentials.json  tokens, sealed with DPAPI for this user
%APPDATA%\AiUsage\ai-usage.log      unhandled exceptions, appended
```

Tokens are encrypted per Windows user, so copying the file to another account or
machine does not expose them. All three files are written to a temporary name and
renamed into place, so a crash mid-write leaves the old contents.

## Layout

```
src/AiUsage/             the WPF app
  App.xaml.cs            tray bootstrap, single instance, 15-minute timer
  Theme.xaml             palette, control styles, the two card templates
  Ui/                    meter, drawdown chart, provider logos, view models
  Views/                 flyout, main window, provider detail, device code dialog
  Tray/                  NotifyIcon with a ring gauge drawn per severity
src/AiUsage.Core/        everything that is not a window; plain net9.0
  Data/                  models, combined figure, trendline, projection, history,
                         credentials, the shared fetch, the server client
  Providers/             one file per provider, each returning ProviderUsage
  Auth/                  OAuth + PKCE, loopback listener, device code, CLI import
  Net/                   the one HTTP client
tests/AiUsage.Core.Tests/
```

`AiUsage.Core` is also what `../my-usage-server` is built on, so the two cannot
disagree about how a provider is read.
