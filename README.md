# CineSync

A from-scratch, **self-hosted** sync for in-world media "screens" (TVs) in FFXIV — your own
plugin + your own server, fully self-contained with no third-party services.

The host creates a screen in the world; everyone connected to the same server + room code sees
it appear at the same spot, all playing the same media. Pairs naturally with a self-hosted
**Owncast** stream (the plugin just plays a stream URL).

```
   HOST plugin ──ws──►  CineSync.Server (relay)  ──ws──►  FRIEND plugins
      creates screen,   rooms + screen state               render TV at same pos,
      drives playback                                       play the same stream URL
        └────────── everyone pulls video from your Owncast (HLS) ──────────┘
```

## Projects
| Project | What it is | Target |
|---|---|---|
| `CineSync.Shared` | DTOs + wire protocol (envelope/message types) shared by both ends | net9.0;net10.0 |
| `CineSync.Server` | Raw-WebSocket relay (rooms, screen state, playback fan-out) | net9.0 |
| `CineSync.Plugin` | Dalamud plugin (connect, host controls, in-world rendering) | net10.0-windows |
| `_reflect/` | Throwaway tool that dumps Dalamud/ImGui API signatures via MetadataLoadContext | — |

Transport is **raw `System.Net.WebSockets` + JSON** on purpose — no SignalR — so the plugin pulls
zero extra dependencies that could clash with the assemblies Dalamud already loads.

## Run the server
```powershell
dotnet run --project CineSync.Server -c Release
# listens on http://0.0.0.0:5252  (WebSocket endpoint: /ws)
```
Expose it to friends the same way as the Owncast tunnel (Cloudflare tunnel / port-forward / VPS).

## Build + dev-load the plugin
```powershell
dotnet build CineSync.Plugin -c Release
```
In Dalamud: **/xlsettings → Experimental → Dev Plugin Locations**, add
`...\CineSync\CineSync.Plugin\bin\Release`, then enable **CineSync** in the plugin installer.
Open it with `/cinesync`.

## Usage
1. Everyone: set the same **Server URL** (`ws://<host>:5252/ws`) and **Room code**, click **Connect**.
2. Host: stand where you want the TV, set a **Default media URL**, click **Create screen here**.
   It appears for everyone in the room.
3. Everyone: set your own movie volume under **Audio**, or with `/cinesync volume 0-100` (`+10`/`-10`
   nudges it) and `/cinesync mute`. It's per-viewer and never synced, and one level covers every
   screen. Use it instead of the Windows volume mixer, which CineSync overrides.

The media URL goes straight to VLC on every client, so it must be a stream or file URL that
friends can reach, not a web page. For Owncast that's the HLS playlist:
`http://<host>:8080/hls/stream.m3u8`. The Owncast home page (`http://<host>:8080/`) and the
`/embed` pages are HTML, so VLC can't play them. Don't use `localhost` here either: friends would
be pointed at their own machine.

## Roadmap
- **M1 (done): backbone** — server + protocol + plugin connect/sync + world-anchored placeholder quad.
- **M2: rendering** — solid world-space TV (correct facing, near/far culling, click-to-move).
- **M3: media** — LibVLCSharp decodes the stream onto the TV texture; host drives play/pause/seek.
- **M4: polish** — room passwords, multiple screens, reconnect, presets.

## Status
Server + protocol build & run (verified). Plugin builds & packages (`bin\Release\CineSync\latest.zip`).
In-world rendering is the M1 placeholder (a labeled quad) — proves the sync loop before M3 adds video.

## License

CineSync's own code is Copyright (c) 2026 OmegaJackie, licensed
[AGPL-3.0-or-later](LICENSE).

CineSync was MIT up to and including v0.1.2. It moved to the AGPL in v0.2.0 because depth-correct
world rendering links [Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy), which is
AGPL-3.0-or-later. Note the AGPL's network clause applies to the bundled CineSync sync server: if
you run a modified server for others, you must offer them its source.

The release bundles libVLC and LibVLCSharp, which are LGPL-2.1-or-later and keep their own
terms; the VLC Windows package additionally ships a few GPL-licensed modules. Details and the
required notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), and both files ship
inside the plugin zip.
