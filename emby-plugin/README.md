# ClearFrame for Emby

The server half of ClearFrame. This Emby plugin gives each paired Samsung TV a home screen backend:

- **Rows:** Continue Watching, Next Up, On Now (Live TV), and Your Teams (Live TV sports matched against team names you choose per TV).
- **Seerr discovery:** Trending, Popular Movies and Popular Shows, each with its request status.
- **Requests from the TV**, made as the right Seerr user. Emby accounts are linked to Seerr accounts automatically where possible. One Emby login can link to several Seerr accounts for shared logins, in which case the TV asks who is requesting.
- **One-press playback:** ClearFrame tells the server what to play, then launches the Emby app. The plugin waits for the Emby app's session and sends Play, Resume (at the saved position) or Show Details.

Seerr's URL and API key stay on the Emby server. TVs only ever hold their own ClearFrame token.

Tested on Emby Server **4.10.2.0**. CI also builds against 4.9.1.90.

## Install

1. Download `ClearFrame.dll` from the latest [release](https://github.com/CtznSniiips/ClearFrame/releases) tagged `emby-plugin-v…`. For an unreleased build, use the artifact `ClearFrame-emby-4.10.2.0` from the latest **Emby plugin** workflow run.
2. Copy it into your Emby `plugins` folder, e.g. `/config/plugins` in the Docker container or `programdata/plugins`.
3. Restart Emby. **ClearFrame** appears under Dashboard → Plugins.

## Set up

Open Dashboard → Plugins → ClearFrame.

1. **Seerr:** enter the URL and an API key (Seerr → Settings → General), then press **Test connection** and **Save settings**.
2. **Account links:** press **Sync from Seerr**.
   - **auto:** the Seerr account already signs in with that Emby account.
   - **suggested:** a username or email match. Press ✓ to confirm it; suggestions aren't used until confirmed.
   - Add or remove accounts with the dropdown, then press **Save links**. Sync never touches links you added or confirmed.
   - Optionally choose a **Request as** account for Emby users with no link.
3. **TVs:** start pairing in ClearFrame on the TV. Its four-character code appears here. Choose the Emby user the TV acts as and, if needed, the Emby app session on that TV, then press **Approve**.
   - If the Emby app reports the TV's hardware ID (DUID) as its device ID, it's matched automatically and you can leave the Emby app field on *Match automatically*.
   - Otherwise, open Emby on the TV once, press **Refresh**, and pick it from the list.
4. **TV defaults:** choose which rows a new TV shows, items per row, the sports look-ahead and the playback timing. Each TV can then change its own rows, order, timing and teams from ClearFrame's settings on the TV. A TV's card shows whether it has done so, with **Reset TV settings to defaults** to undo it.

## TV API

Every TV call except pairing sends `X-ClearFrame-Token: <token>`. Responses use Emby's PascalCase JSON. Emby image paths are server-relative, so prefix them with the server address and add `&maxWidth=` as needed.

| Method | Path | Body / query | Returns |
| --- | --- | --- | --- |
| POST | `/emby/ClearFrame/Pair` | `{ DeviceName, Duid }` | `{ PairingId, Code, Secret, ExpiresInSeconds }`. Show `Code` on screen. |
| GET | `/emby/ClearFrame/Pair/Status` | `?PairingId=&Secret=` | `{ Status: pending, approved, rejected or expired, Token, TvName }`. `Token` is returned once; store it. |
| GET | `/emby/ClearFrame/Ping` | | `{ TvName, UserName }`. A 401 means the TV was unpaired. |
| GET | `/emby/ClearFrame/Home` | | `{ TvName, UserName, RequestsEnabled, Rows: [{ Id, Title, Layout, Items }], Warnings }` |
| GET | `/emby/ClearFrame/Requesters` | | `{ RequestsEnabled, Requesters: [{ Id, Name, AvatarUrl }], LastRequesterId, UsingFallback }` |
| POST | `/emby/ClearFrame/Request` | `{ TmdbId, MediaType: movie or tv, SeerrUserId }` | `{ Status: requested, approved or failed, Message, RequestId }` |
| POST | `/emby/ClearFrame/Play` | `{ ItemId, Action: play, resume or details, StartPositionTicks? }` | `{ Status: sent, queued or failed, Message }` |
| GET | `/emby/ClearFrame/Settings` | | `{ Rows: [{ Id, Title, Source, Visible, Available, Reason }], RowLimit, SportsLookaheadHours, PlayDelayMs, PlayTimeoutSeconds, SportsTeams, Customized, Ranges }` |
| POST | `/emby/ClearFrame/Settings` | any of `{ RowOrder, HiddenRows, RowLimit, SportsLookaheadHours, PlayDelayMs, PlayTimeoutSeconds, SportsTeams }` | The updated settings. Fields you leave out stay as they are. |
| POST | `/emby/ClearFrame/Settings/Reset` | | Settings back on the server defaults. Sports teams are kept. |
| GET | `/emby/ClearFrame/Sports/Teams` | `?Days=7` (max 14) | `{ Days, Teams: [{ Name, League, Games, NextStartDate, Selected }], Message }`: teams found in the guide's sports listings, for picking with the remote. |

**Row ids:** `continue`, `nextup`, `livenow`, `sports`, `seerr-trending`, `seerr-movies` and `seerr-tv`. `Settings.Rows` lists every row in the TV's order, so the TV can render its settings list straight from it. `Available: false` means the row can't show anything yet: Seerr isn't set up, or no teams are chosen. `Ranges` gives the min, max and server default for each number, for building sliders or steppers.

**Team picker:** `/Sports/Teams` reads team names out of guide listings such as *NHL Hockey: Toronto Maple Leafs at Boston Bruins* or *Premier League: Arsenal v Chelsea*. Saved teams are listed first. To save the chosen names, send them as `SportsTeams` to `/Settings`. A team matches a programme when its name appears in the title, episode title or description.

Home items carry what the TV needs to act on them:

- `PlayItemId` and `DefaultAction` (`resume`, `play` or `request`).
- `ProgressPercent` for progress bars.
- `PosterUrl` and `LandscapeUrl`.
- For Live TV: `ChannelName`, `StartDate`, `EndDate` and `IsAiring`.
- For Seerr items: `TmdbId`, `MediaType` and `RequestStatus` (`none`, `requested`, `processing`, `partial` or `available`).

**Playing from the TV:** call `/ClearFrame/Play` first, then launch the Emby app (`vYmY3ACVaa.emby`). If Emby is already running the command is sent immediately. Otherwise it's queued until the app connects, for up to the TV's `PlayTimeoutSeconds`. It's sent `PlayDelayMs` after the app connects, so the app has time to finish loading.

## Build

The workflow `.github/workflows/emby-plugin.yml` compiles the plugin against the assemblies shipped in the Emby Server release itself (downloaded from Emby's GitHub releases). It treats deprecated-API warnings as errors, and uploads `ClearFrame.dll` as an artifact. Run it manually with a different `emby_version` to check another server version.

Locally, with the .NET 8 SDK:

```sh
cd emby-plugin/ClearFrame
# against an Emby install (or the three MediaBrowser.*.dll files copied from one)
dotnet build -c Release -p:EmbyRefDir=/opt/emby-server/system
# or against the MediaBrowser.Server.Core NuGet package
dotnet build -c Release
```

The DLL is written to `bin/Release/netstandard2.0/ClearFrame.dll`.

## Release

Releases are published by hand from Actions → **Emby plugin release** → Run workflow.

1. Bump `<Version>` (and `AssemblyVersion`/`FileVersion`) in `ClearFrame/ClearFrame.csproj`.
2. Add a `## <version>` section to `CHANGELOG.md`. It becomes the release notes.
3. Merge to `main`, then run the workflow. Choose the Emby version to build against (default 4.10.2.0) and optionally mark it as a pre-release.

The workflow refuses to run if the tag `emby-plugin-v<version>` already exists, or if the changelog has no section for that version. It builds through the regular **Emby plugin** workflow, so the release uses the same checks, then creates the tag and a GitHub Release with `ClearFrame.dll` attached.
