# Exposes your local CineSync sync server (port 5252) to friends over the internet,
# with no port-forwarding / public IP needed (outbound tunnel, same as Owncast).
# Keep this window open during the session. It prints an https://<random>.trycloudflare.com URL.
# Friends use it as their CineSync "Server URL" but with wss:// and /ws, e.g.:
#     wss://<random>.trycloudflare.com/ws
& "C:\Program Files (x86)\cloudflared\cloudflared.exe" tunnel --url http://localhost:5252
