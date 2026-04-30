using System.Net;
using System.Text;
using System.Text.Json;
using TheFlag.Server;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateSlimBuilder(args);

const long MaxMapRequestBytes = 1 * 1024 * 1024;
var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "https://server.mcrenox.com",
    "http://server.mcrenox.com",
    "https://www.mcrenox.com.ar",
    "http://www.mcrenox.com.ar",
    "http://127.0.0.1"
};

builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Cors.Infrastructure.CorsService", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Warning);
builder.Logging.AddProvider(new LocalFileLoggerProvider(
    Path.Combine(AppContext.BaseDirectory, "log.txt"),
    maxFileBytes: 5 * 1024 * 1024,
    retainedFileCount: 5));
builder.WebHost.UseUrls("http://0.0.0.0:5770");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(IsAllowedOrigin)
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
var roomManager = new GameRoomManager(mapPath, app.Logger);

app.Lifetime.ApplicationStarted.Register(roomManager.Start);
app.Lifetime.ApplicationStopping.Register(roomManager.Stop);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    activeRooms = roomManager.ActiveRooms,
    maxActiveRooms = roomManager.MaxRooms,
    players = roomManager.TotalPlayers,
    maxPlayersPerRoom = roomManager.MaxPlayersPerRoom,
    tickRate = roomManager.TickRate,
    map = Path.GetFileName(mapPath)
}));

app.MapGet("/api/map", () => Results.Text(roomManager.GetRawMapJson(), "application/json"));

app.MapPut("/api/map", async (HttpRequest request) =>
{
    if (request.ContentLength is > MaxMapRequestBytes)
    {
        return Results.Json(new
        {
            ok = false,
            message = $"The map JSON document is larger than the {MaxMapRequestBytes} byte limit."
        }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    string rawJson;
    try
    {
        rawJson = await ReadBodyWithLimitAsync(request.Body, MaxMapRequestBytes, request.HttpContext.RequestAborted);
    }
    catch (InvalidDataException ex)
    {
        return Results.Json(new
        {
            ok = false,
            message = ex.Message
        }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    var result = roomManager.TryReplaceMap(rawJson);

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

app.MapGet("/api/rooms", () => Results.Ok(roomManager.GetRoomsResponse()));

app.MapPost("/api/rooms", async (HttpRequest request) =>
{
    CreateRoomRequest? createRoomRequest;
    try
    {
        createRoomRequest = await JsonSerializer.DeserializeAsync<CreateRoomRequest>(
            request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            request.HttpContext.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.Json(new
        {
            ok = false,
            message = "Invalid JSON body."
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    var result = roomManager.TryCreateRoom(createRoomRequest?.RoomId);
    if (!result.Success)
    {
        return Results.Json(new
        {
            ok = false,
            message = result.Message
        }, statusCode: result.StatusCode);
    }

    return Results.Json(new
    {
        ok = true,
        roomId = result.RoomId,
        message = result.Message
    }, statusCode: result.StatusCode);
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required");
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();
    if (!IsAllowedWebSocketOrigin(origin, context.Request.Host.Host))
    {
        app.Logger.LogWarning("Rejected WebSocket origin '{Origin}' for request host '{Host}'.", origin, context.Request.Host.Value);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden WebSocket origin");
        return;
    }

    await roomManager.HandleClientAsync(context);
});

await app.RunAsync();

bool IsAllowedOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (IsLoopbackOrigin(uri))
    {
        return true;
    }

    var normalizedOrigin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    return allowedOrigins.Contains(normalizedOrigin);
}

bool IsAllowedWebSocketOrigin(string? origin, string? requestHost)
{
    // Browsers send Origin for WebSocket handshakes. File-based local pages can send "null";
    // accept that only when the target server itself is loopback, never for the public host.
    if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(requestHost))
    {
        return true;
    }

    return IsAllowedOrigin(origin);
}

static bool IsLoopbackOrigin(Uri uri)
{
    return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && IsLoopbackHost(uri.Host);
}

static bool IsLoopbackHost(string? host)
{
    if (string.IsNullOrWhiteSpace(host))
    {
        return false;
    }

    var normalizedHost = host.Trim().Trim('[', ']').ToLowerInvariant();
    if (normalizedHost == "localhost")
    {
        return true;
    }

    return IPAddress.TryParse(normalizedHost, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
}

static async Task<string> ReadBodyWithLimitAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
{
    var buffer = new byte[81920];
    using var ms = new MemoryStream();

    while (true)
    {
        var read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read <= 0)
        {
            break;
        }

        if (ms.Length + read > maxBytes)
        {
            throw new InvalidDataException($"The request body exceeded the {maxBytes} byte limit.");
        }

        await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }

    return Encoding.UTF8.GetString(ms.ToArray());
}
