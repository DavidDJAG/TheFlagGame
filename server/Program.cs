using TheFlag.Server;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5770");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();

var contentRoot = app.Environment.ContentRootPath;
var clientWebPath = Path.GetFullPath(Path.Combine(contentRoot, "..", "client-web"));
if (Directory.Exists(clientWebPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new PhysicalFileProvider(clientWebPath),
        DefaultFileNames = { "index.html" }
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(clientWebPath)
    });
}


var clientPwaPath = Path.GetFullPath(Path.Combine(contentRoot, "..", "client-pwa"));
if (Directory.Exists(clientPwaPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        RequestPath = "/pwa",
        FileProvider = new PhysicalFileProvider(clientPwaPath),
        DefaultFileNames = { "index.html" }
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        RequestPath = "/pwa",
        FileProvider = new PhysicalFileProvider(clientPwaPath)
    });
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

var mapPath = Path.Combine(contentRoot, "Data", "map.json");
var game = new GameHost(mapPath, app.Logger);

app.Lifetime.ApplicationStarted.Register(game.Start);
app.Lifetime.ApplicationStopping.Register(game.Stop);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    players = game.PlayerCount,
    tickRate = game.TickRate,
    map = Path.GetFileName(mapPath)
}));

app.MapGet("/api/map", () => Results.Text(game.GetRawMapJson(), "application/json"));

app.MapPut("/api/map", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var rawJson = await reader.ReadToEndAsync();
    var result = game.TryReplaceMap(rawJson);

    if (!result.Success)
    {
        return Results.Json(new
        {
            ok = false,
            message = result.Message
        }, statusCode: result.StatusCode);
    }

    return Results.Ok(new
    {
        ok = true,
        message = result.Message,
        mapName = result.MapName,
        objectCount = result.ObjectCount
    });
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required");
        return;
    }

    await game.HandleClientAsync(context);
});

await app.RunAsync();
