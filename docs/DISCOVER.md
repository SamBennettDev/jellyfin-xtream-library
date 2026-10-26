# Discover

Browse every popular English title from TMDB inside Jellyfin, and play one by streaming a release found through
Radarr or Sonarr. IPTV is then only needed for live TV.

## How it works

1. **Catalog.** A scheduled task ("Sync Discover catalog", every 12 hours) lists the most popular English movies and
   series on TMDB and writes a STRM placeholder for each into `/config/discover`, in two libraries it creates:
   **Discover Movies** and **Discover Shows**. Jellyfin fills in posters and metadata from the `[tmdbid-N]` folder
   name. Anything Radarr or Sonarr already holds on disk is left out, since it lives in your real libraries.
2. **Version picker.** When a client asks how to play a Discover title, the plugin asks Radarr or Sonarr to search
   (the title is added unmonitored, nothing downloads) and offers the releases as versions, such as
   `1080p WEB-DL · 6.2 GB · 311 seeders · EZTV`.
3. **Streaming.** Picking a version, or just pressing play (which picks the best one), loads the torrent into
   TorrServer and plays its HTTP stream. TorrServer answers range requests and fetches the pieces around a seek
   first, so scrubbing and "continue from" work.
4. **Keeping.** With "Also download and keep what I stream" on, the release you play is also sent to Radarr or
   Sonarr, which download and import it. The next catalog sync drops the placeholder.

## Setup

- Run TorrServer (`ghcr.io/yourok/torrserver`) on the same Docker network as Jellyfin.
- Open Dashboard, Plugins, **Xtream Discover**. Fill in the TMDB key, the Radarr, Sonarr and TorrServer addresses,
  tick Enable, Save, then Test connections and Sync catalog now.

## Limits

- Series that Sonarr already holds any episode of are left out whole.
- A torrent with few seeders can fail to start; the plugin tries the next release down the list.
- The placeholder address is only answered to requests from the server itself, because it starts searches and
  torrents.
