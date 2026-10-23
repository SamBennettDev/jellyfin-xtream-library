# PressBox features

This fork adds the features of the PressBox IPTV server (everything except its navigable TV display and
its 4K upscaling) on top of the plugin's STRM library sync and native tuner. Nothing changes after an
upgrade until a feature is switched on in **Dashboard → Plugins → Xtream Library → PressBox**.

All settings live in `PluginConfiguration.Pressbox` (`PressboxSettings.cs`); per-provider stream limits
are `ProviderConfig.StreamLimitMode` and `ProviderConfig.MaxStreams`.

## What was added

| Feature | What it does | Where |
|---|---|---|
| Default guide | Fetches free guides (epgshare01's US, sports and local-station guides by default) and matches your channels to them **only when there is no doubt**: by guide id, by a local station's call sign, or by an exact cleaned name. Matched channels the provider gives no listings for get the guide's listings; the provider's own guide always wins. Optionally uses the guide's name and logo, in the XMLTV file, the M3U and the tuner's channel list alike. Hand-picked matches always win. | `DefaultGuideService`, `GuideMatcher`, `XmltvGuideReader` |
| Guide resilience | Guides stream through an XML reader (a half-gigabyte file is never held in memory), gzip is detected, a size cap applies, a truncated file is refused whole, a failed fetch retries after 5 min doubling to 6 h, and a lineup that gains a station fetches again. | `XmltvGuideReader`, `ImportBackoff` |
| Category languages | Reads the language a category name says (61 languages; tags, words, places, scripts). The PressBox tab lists them and selects the matching movie, series and (in custom mode) live categories. A category that names no language (a plain genre) is not selected. | `CategoryLanguage`, `PressboxController` |
| Stream limits | Reads each account's connection limit from the panel (or a number you set), shares it between providers on the same account, and refuses a stream that would exceed it with a message instead of letting the provider block the account. Each open stream holds a lease that is kept alive while it plays and given back when Jellyfin closes it. | `StreamBudget`, `StreamLimitService`, `XtreamLiveStream` |
| Quiet sync | The scheduled library sync waits (up to 30 min) for a minute with nobody watching live TV. | `PlaybackActivity`, `SyncLibraryTask` |
| Sports | Today's games from ESPN for the leagues you follow, the channels showing each one (game channels, broadcast networks, guide listings), and guide entries for channels named for a game that have no listings. | `EspnScoreboard`, `GameStreamFinder`, `SportsService` |
| Trending | Ranks what is on now: live games, big networks, and what Google Trends is talking about. Optionally keeps a "Trending Now" collection of synced movies and series. | `TrendingRanker`, `TrendingService`, `TrendingCollectionService`, `TrendingCollectionTask` |
| Multiview | Mixes up to four channels into one picture with ffmpeg and offers it as a Live TV channel (numbers from 9001) that every Jellyfin client can play. Each tile holds a provider stream while it runs, all or none. | `MultiviewPlanner`, `MultiviewService`, `XtreamTunerHost` |

## Where PressBox features map onto Jellyfin

Some PressBox features already exist in Jellyfin or in this plugin, so they were not ported:

| PressBox feature | In this plugin |
|---|---|
| Login, accounts | Jellyfin's users |
| Relay and transcoding to any device | Jellyfin's live TV transcoding (the tuner marks streams as transcodable, not direct-playable) |
| AirPlay, Cast, UPnP | Jellyfin's own clients and cast support |
| Artwork cache, favourites, search, backups | Jellyfin (images, favourites, search, backup) |
| Trakt | The official Jellyfin Trakt plugin |
| Title cleaning, versions, TMDB tagging | Already in this plugin's sync and metadata lookup |
| Measured stream quality | Jellyfin probes streams when it opens them; the tuner also pre-fills stats from the provider |
| Category locks / PINs | Use folder mode (Provider tab) to put categories in their own libraries, then restrict libraries per user in Jellyfin (user → Library access, with parental ratings or per-user library lists). No plugin code is involved. |

Not ported, by request: the navigable TV display / remote control and the 4K upscaling.

## Endpoints

All under `/XtreamLibrary/`, administrator only unless noted.

| Endpoint | Method | Description |
|---|---|---|
| `PressBox/Languages` | GET | Languages found in a provider's categories (`?providerIndex=`) |
| `PressBox/Languages/Apply` | POST | Select the categories in the chosen languages |
| `PressBox/StreamLimits` | GET | Each provider's allowance and use |
| `PressBox/StreamLimits/Refresh` | POST | Ask the panels again |
| `PressBox/Guide` | GET | State of each default guide |
| `PressBox/Guide/Refresh` | POST | Fetch the guides now |
| `PressBox/Guide/Matches` | GET | Matched channels and the rule that matched each |
| `PressBox/Guide/Search` | GET | Search the guides' channels (`?q=`) |
| `PressBox/Guide/Match` | POST | Pin a channel to a guide channel by hand (empty guide id removes it) |
| `PressBox/Sports/Leagues` | GET | Leagues that can be followed |
| `PressBox/Sports/Slate` | GET | Today's games |
| `PressBox/Sports/Games/{id}/Channels` | GET | Channels showing a game |
| `PressBox/Trending` | GET | What is on now, ranked |
| `PressBox/Trending/RefreshCollection` | POST | Update the Trending Now collection |
| `PressBox/Channels` | GET | Search the lineup by name (`?q=`) |
| `PressBox/Multiviews` | GET | Multiviews and their stream addresses |
| `Multiview/{token}/stream.ts` | GET | The mosaic. Anonymous; the token in the address is the secret. |

## Behaviour worth knowing

- **Matching is per guide, in order.** A guide's channels are matched against the lineup together with the
  channels of the guides fetched before it, so a name or call sign two guides both claim is refused only
  once both are held. Put the guide you trust most first.
- **Game listings and the ranking use default-guide listings only.** The provider's own guide is passed
  through as XMLTV and is not parsed into listings, so "what is on now" and "which channel shows this game"
  see the default guides' listings plus channel names, not the provider's.
- **Multiview costs CPU.** Four tiles are decoded, scaled and re-encoded with x264. A multiview is served
  to one viewer at a time. Set the ffmpeg path only if Jellyfin's own is not suitable.
- **Stream limits are per Jellyfin server.** They count streams opened through this plugin; other apps
  using the same account are only visible through what the panel reports.
- **Everything else is unchanged.** STRM sync, the tuner, M3U and XMLTV behave as before when the features
  are off.

## Build status of this fork

The pure logic (`Service/Pressbox/*` except the `*Service` classes) was built under the repository's strict
analyzers and tested. The integration code that touches Jellyfin's own APIs could not be built where this
fork was written (NuGet was unreachable), so it was compiled against stand-in types only, under the same
analyzers. `TrendingCollectionService` (Jellyfin's collection and library interfaces), the tuner host and
`LiveTvService` edits, `SyncLibraryTask` and `PluginServiceRegistrator` have not met the real assemblies:
run `dotnet build -c Release` and `dotnet test -c Release` before relying on them.
