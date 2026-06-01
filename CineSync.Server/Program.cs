using System.Net.WebSockets;
using CineSync.Server;
using CineSync.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddSingleton<Relay>();

var app = builder.Build();

app.UseWebSockets();

app.Map(Net.Path, async (HttpContext ctx, Relay relay) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await relay.HandleSocket(socket);
});

app.MapGet("/", () => Results.Text($"CineSync server is running. WebSocket endpoint: {Net.Path}"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
