# Third-party notices — CineSync

CineSync's own code (CineSync.Plugin, CineSync.Server, CineSync.Shared) is
Copyright (c) 2026 OmegaJackie and licensed AGPL-3.0-or-later — see [LICENSE](LICENSE).

The release build bundles the following third-party components. They keep their
own licenses; CineSync's own license does not apply to them.

## Pictomancy 1.0.10 — AGPL-3.0-or-later

<https://github.com/sourpuh/ffxiv_pictomancy>
Copyright (c) sourpuh and the Pictomancy contributors.
Declared license in the NuGet package: `AGPL-3.0-or-later`.

Pictomancy draws CineSync's screens as real world-space geometry depth-tested
against the game's scene depth buffer, so characters and walls in front of a
screen occlude it. It is statically referenced and linked into the plugin, which
is why CineSync itself is AGPL-3.0-or-later from v0.2.0 onward.

It pulls in the following transitive dependencies, which also ship in the zip:

    KamiToolKit                  (MIT)
    SharpDX 4.2.0                (MIT)
    SharpDX.D3DCompiler 4.2.0    (MIT)
    SharpDX.Direct2D1 4.2.0      (MIT)
    SharpDX.Direct3D11 4.2.0     (MIT)
    SharpDX.DXGI 4.2.0           (MIT)
    SharpDX.Mathematics 4.2.0    (MIT)
    SixLabors.ImageSharp         (Apache-2.0, via KamiToolKit)

## LibVLCSharp 3.9.2 — LGPL-2.1-or-later

<https://code.videolan.org/videolan/LibVLCSharp>
Copyright (c) VideoLAN and the LibVLCSharp authors.
Declared license in the NuGet package: `LGPL-2.1-or-later`.

## VideoLAN.LibVLC.Windows 3.0.21 — LGPL-2.1-or-later, with GPL modules

<https://www.videolan.org/vlc/libvlc.html>
Copyright (c) VideoLAN and the VLC authors.
Declared license in the NuGet package: `LGPL-2.1-or-later`.

The libVLC core and most of its modules are LGPL-2.1-or-later. The Windows
package additionally ships several plugin modules whose upstream sources are
GPL-licensed, among them:

    plugins/codec/libx26410b_plugin.dll        (x264, GPL-2.0-or-later)
    plugins/packetizer/libpacketizer_dts_plugin.dll   (DTS, GPL-2.0-or-later)
    plugins/lua/liblua_plugin.dll              (VLC Lua bindings, GPL-2.0-or-later)
    plugins/access/liblibbluray_plugin.dll     (libbluray; BD-J components)

libVLC is loaded dynamically and is not linked into or modified by CineSync.
Under the LGPL that permits distribution alongside MIT-licensed code, provided
these notices and the corresponding license texts accompany the distribution and
users remain able to replace the libVLC binaries.

Anyone redistributing this build is responsible for satisfying the LGPL (and,
for the modules above, the GPL) obligations that attach to those binaries.
