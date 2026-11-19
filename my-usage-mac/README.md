# usagetop for macOS

A menu bar app: how much of each AI subscription is left, one glance away. Native
SwiftUI, no Dock icon, no window — the whole app is the menu bar item and the
panel it opens.

On its own, it asks the providers itself: add the subscriptions you have in
Settings, sign in or paste a key, and it polls them, keeps their history on this
Mac and holds the sign-ins in the Keychain. Given a server's address instead, it
reads everything from the server, the same one the other apps use, which polls
around the clock and shares one history between your devices.

## Build

```
./build.sh              # release, universal (Apple silicon and Intel)
./build.sh debug        # debug, for this Mac only
./build.sh release run  # build, then launch
```

`build.sh` wraps the SwiftPM executable in `usagetop.app` with `LSUIElement` set,
so it lives in the menu bar only. Needs macOS 26 or newer and Xcode 26. The
app takes the project's version, `<Version>` in
`../my-usage-win/src/AiUsage/AiUsage.csproj`; `VERSION=1.2.3 ./build.sh` sets
another. A release build is signed with the keychain's Developer ID
Application certificate if there is one, and ad hoc otherwise; the released
app is signed and notarized (`../release-mac.sh`).

`dmg/make-dmg.sh usagetop.app usagetop-mac.dmg` packs the app in the disk image
that is released: a window with the app and an arrow to Applications, on a
background `dmg/background.swift` draws. It needs create-dmg (`brew install
create-dmg`) and lets Finder lay out the window.

## What it shows

The panel is one sheet of Liquid Glass hung under the menu bar item. Down its
left edge is a rail: the overview, then every account as its mark inside a ring
of what is left, then settings. Click one, or press ⌘0, ⌘1–9, ⌘, .

- **Overview.** The combined figure, its last week as a chart, and the forecast
  carried on past now: how fast it is burning and when the next window lifts.
  Under it every account, the one about to run out first, with a bar for each
  window. The tick on a bar is where an even spread over the cycle would be; a
  flame means the window is being spent faster than that.
- **An account.** Every window in full: the bar, its pace, its history (a day
  and a half for a 5-hour window, a week for a weekly one, five weeks beyond),
  the forecast as a dashed line, and when it runs dry if it does before the
  reset. Hover a chart to read a moment.

Pace, burn rate and forecast are the server page's, ported in
`Forecast.swift`, so both read a window the same way and word it the same way.

The palette is the one the other apps use, from
`my-usage-server/src/AiUsage.Server/Web/app.css`: chrome takes the system's
own label colours (so it works in light and dark), and a hue only where it means
something. A bar is muted blue while healthy, amber under 40%, red under 15%.

## Sample data

`open usagetop.app --args --demo` shows believable sample readings instead of the
server's; before a server is set, the panel offers the same. `--open` opens the
panel at launch. `--demo --snapshot <folder>` draws every page, dark and light,
to PNGs and quits (glass is stood in for by a flat backdrop there).

## Settings

**Accounts**: every subscription followed and what is stored for it, with Sign
in, Set or Replace key for Mistral and Kimi (with Mistral's fallback budget and
Kimi's China endpoint), Rename, Sign out, Remove, and Add an account. The list
starts empty.

- *Without a server* (`Local/`): the app is the server. Claude, ChatGPT and
  Antigravity sign in in the browser, which is sent back to a port on this Mac
  that catches the code; Muse approves a device code. The providers are the
  twins of `my-usage-win/src/AiUsage.Core/Providers`, asked every five minutes
  (two while a window is being spent), and history is kept for 40 days in
  `~/Library/Application Support/usagetop/history.json`, by the server's rules.
  Sign-ins and keys stay in the Keychain. A Claude or ChatGPT account signed in
  here should not also be signed in on a server: their renewals are single-use.
- *With a server*: the server's accounts. It runs the sign-in (paste back what
  the page shows, or approve a device code), and never hands a key or a session
  back; this only ever replaces one.

`AiUsageMac --local-selftest` runs the no-server mode end to end against a
throwaway account store, and deletes it again.

**Server (optional)**: address and token. The address is ordinary preference; the token goes to the
Keychain. `Test connection` asks the server for one reading before you commit to
it. The menu bar shows either the combined figure in a ring (with or without
the percentage), or the one to four limits closest to running out: each as its
provider's mark beside the limit's name and what is left. Both are template
images, drawn in the menu bar's own colour. A limit that is already empty is left
out: nothing can be done about it but wait. A Glass slider sets how much of the
panel is Liquid Glass, from a flat, opaque panel in the window colour to clear
glass; a Tiles slider, apart from it, how solid the cards on it are, so they can
be made easy to read while the panel around them stays clear.

**Updates**: a copy in the Applications folder keeps itself current from the
GitHub releases. It looks a minute after start and every six hours, or on
`Check now`; a newer `usagetop-mac.dmg` is downloaded in the background,
checked (its SHA-256 against the one the release lists, that it is this app in
that version, that its signature is intact) and goes in on `Restart to update`,
or when the app next quits or starts. A switch adds beta versions. A copy
signed with a Developer ID also requires the download to be signed by the same
team. A copy signed ad hoc has no team to compare, so for it the check says the
download is whole and came from the release, not who built it; and macOS may
ask after an update whether the new version may read the token from the
Keychain. A copy anywhere else (a
build folder, Downloads) leaves itself alone. `USAGETOP_UPDATE_SOURCE`, an
address that answers like `https://api.github.com/repos/rzukic/usagetop`,
stands in for GitHub to try an update without publishing one.

**Order**: drag the accounts into the order wanted; the sidebar and ⌘1–9 follow
it. The overview lists whatever is closest to running out first, or, with that
switched off, follows the same order. Holding ⌘ with the panel open shows each
sidebar item's shortcut. The app can open at login.
