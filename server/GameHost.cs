using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TheFlag.Server;

public sealed class GameHost
{
    private const float PlayerRadius = 14f;
    private const float ShotRange = 420f;
    private const float ShotCooldownSeconds = 0.25f;
    private const float ShotTraceLifetimeSeconds = 0.12f;
    private const float HitEffectLifetimeSeconds = 0.35f;
    private const int MaxIncomingMessageBytes = 16 * 1024;
    private const int MatchDurationSeconds = 5 * 60;
    private const int MaxPlayers = 32;
    private const int MaxMessagesPerRateLimitWindow = 200;
    private const int MinCanvasWidth = 600;
    private const int MinCanvasHeight = 400;
    private const int MaxCanvasWidth = 4096;
    private const int MaxCanvasHeight = 4096;
    private const int MaxMapObjects = 256;
    private const int MaxPointsPerPolygon = 64;
    private const int MaxTotalPolygonPoints = 512;
    private const int MaxHardObstacles = 160;
    private const float MaxCoordinateMargin = 512f;
    private const float MinShapeSize = 1f;
    private const float MinPolygonArea = 4f;
    private static readonly TimeSpan ClientSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MatchDuration = TimeSpan.FromSeconds(MatchDurationSeconds);
    private static readonly TimeSpan MinimumResetElapsedTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxClientIdleTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly string _mapPath;
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly Dictionary<string, PlayerRuntime> _players = [];
    private readonly Dictionary<string, ConnectedClient> _clients = [];
    private readonly List<ShotTraceRuntime> _shotTraces = [];
    private readonly List<HitEffectRuntime> _hitEffects = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _random = new();

    private sealed record ClientRegistration(string PlayerId, string Team, string MapName, ConnectedClient Client);

    private Task? _loopTask;
    private int _blueScore;
    private int _redScore;
    private DateTimeOffset _matchStartedAtUtc;
    private DateTimeOffset _matchEndsAtUtc;
    private bool _matchFinished;
    private string? _winnerTeam;
    private string? _loserTeam;

    public GameHost(string mapPath, ILogger logger)
    {
        _logger = logger;
        _mapPath = mapPath;
        Map = LoadMapFromFile(mapPath);
        RawMapJson = Map.RawJson;
        StartNewMatchClock(DateTimeOffset.UtcNow);
    }

    public int TickRate => 20;
    public int MaxPlayerCount => MaxPlayers;
    public int PlayerCount
    {
        get
        {
            lock (_sync)
            {
                return _players.Count;
            }
        }
    }

    public string GetRawMapJson()
    {
        lock (_sync)
        {
            return RawMapJson;
        }
    }

    public MapReplaceResult TryReplaceMap(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new MapReplaceResult(false, 400, "The request body does not contain a map JSON document.");
        }

        var normalizedRawJson = rawJson.Trim();
        GameMap nextMap;
        try
        {
            nextMap = LoadMapFromJson(normalizedRawJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new MapReplaceResult(false, 400, ex.Message);
        }

        try
        {
            lock (_sync)
            {
                if (_players.Count > 0 || _clients.Count > 0)
                {
                    return new MapReplaceResult(false, 409, "The map cannot be replaced while players are connected. Disconnect everyone and try again.");
                }

                File.WriteAllText(_mapPath, normalizedRawJson);
                Map = nextMap;
                RawMapJson = normalizedRawJson;
                _blueScore = 0;
                _redScore = 0;
                _shotTraces.Clear();
                _hitEffects.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not persist updated map to disk.");
            return new MapReplaceResult(false, 500, "The map could not be saved to disk.");
        }

        _logger.LogInformation("Map replaced successfully: {MapName}", nextMap.Source.Meta.Name);
        return new MapReplaceResult(true, 200, "Map updated successfully on the server.", nextMap.Source.Meta.Name, nextMap.Source.Objects.Count);
    }

    public string RawMapJson { get; private set; }
    public GameMap Map { get; private set; }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(GameLoopAsync);
        _logger.LogInformation("Game loop started.");
    }

    public void Stop()
    {
        _cts.Cancel();

        List<ConnectedClient> clients;
        lock (_sync)
        {
            clients = _clients.Values.ToList();
        }

        foreach (var client in clients)
        {
            try
            {
                client.Stop(abortSocket: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stop WebSocket client resources for {PlayerId}.", client.PlayerId);
            }
        }

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The game loop did not stop cleanly.");
        }
    }

    public async Task HandleClientAsync(HttpContext context)
    {
        var serverWasAlreadyFull = false;
        lock (_sync)
        {
            serverWasAlreadyFull = _players.Count >= MaxPlayers;
        }

        if (serverWasAlreadyFull)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("The game server is full.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        var registration = TryAddClient(socket);
        if (registration is null)
        {
            _logger.LogWarning("Rejected WebSocket client because the server already has {MaxPlayers} players.", MaxPlayers);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "server full", CancellationToken.None);
            return;
        }

        var playerId = registration.PlayerId;
        var team = registration.Team;
        var mapName = registration.MapName;
        var client = registration.Client;

        client.WriterTask = Task.Run(() => ClientWriterLoopAsync(client));
        _logger.LogInformation("Client connected {PlayerId} ({Team})", playerId, team);

        if (!TryQueueJson(client, new
        {
            type = "welcome",
            playerId,
            team,
            tickRate = TickRate,
            mapName
        }))
        {
            _logger.LogWarning("Could not queue welcome message for {PlayerId}. Closing connection.", playerId);
            RemoveClient(playerId, abortSocket: true);
            return;
        }

        SendStateTo(playerId);

        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeDescription = "bye";

        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, context.RequestAborted);
                if (message is null)
                {
                    break;
                }

                if (!TryRegisterClientMessage(playerId))
                {
                    closeStatus = WebSocketCloseStatus.PolicyViolation;
                    closeDescription = "rate limit exceeded";
                    _logger.LogWarning("Rate limit exceeded by {PlayerId}. Closing connection.", playerId);
                    break;
                }

                HandleIncomingMessage(playerId, message);
            }
        }
        catch (InvalidDataException ex)
        {
            closeStatus = WebSocketCloseStatus.MessageTooBig;
            closeDescription = "incoming message too large or invalid";
            _logger.LogWarning(ex, "Rejected WebSocket message from {PlayerId}.", playerId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "WebSocket receive loop canceled for {PlayerId}.", playerId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket receive failed for {PlayerId}.", playerId);
        }
        catch (Exception ex)
        {
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeDescription = "server error";
            _logger.LogError(ex, "Unexpected error while handling WebSocket client {PlayerId}.", playerId);
        }
        finally
        {
            RemoveClient(playerId, abortSocket: false);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(closeStatus, closeDescription, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not close WebSocket cleanly for {PlayerId}.", playerId);
                }
            }
        }
    }

    private ClientRegistration? TryAddClient(WebSocket socket)
    {
        lock (_sync)
        {
            if (_players.Count >= MaxPlayers)
            {
                return null;
            }

            if (_players.Count == 0)
            {
                ResetMatch();
            }

            var playerId = $"p-{Guid.NewGuid():N}";
            var team = ChooseTeam();
            var name = team == "blue" ? $"Blue-{_players.Count + 1}" : $"Red-{_players.Count + 1}";
            var spawn = FindSpawn(team);

            _players[playerId] = new PlayerRuntime
            {
                Id = playerId,
                Name = name,
                Team = team,
                Position = spawn,
                SpawnPosition = spawn,
                Facing = team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f)
            };

            var now = DateTimeOffset.UtcNow;
            var client = new ConnectedClient
            {
                PlayerId = playerId,
                Socket = socket,
                LastReceivedAtUtc = now,
                RateLimitWindowStartedAtUtc = now
            };
            _clients[playerId] = client;

            var mapName = Map.Source.Meta.Name;
            return new ClientRegistration(playerId, team, mapName, client);
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException($"Unsupported WebSocket message type: {result.MessageType}.");
            }

            if (ms.Length + result.Count > MaxIncomingMessageBytes)
            {
                throw new InvalidDataException($"Incoming WebSocket message exceeded {MaxIncomingMessageBytes} bytes.");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void HandleIncomingMessage(string playerId, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            lock (_sync)
            {
                if (!_players.TryGetValue(playerId, out var player) || !_clients.TryGetValue(playerId, out var client))
                {
                    return;
                }

                if (type == "hello")
                {
                    if (doc.RootElement.TryGetProperty("name", out var nameElement))
                    {
                        var requestedName = (nameElement.GetString() ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(requestedName))
                        {
                            player.Name = requestedName.Length > 24 ? requestedName[..24] : requestedName;
                        }
                    }
                }
                else if (type == "input")
                {
                    if (!_matchFinished)
                    {
                        player.Input = new InputState
                        {
                            Up = ReadBool(doc.RootElement, "up"),
                            Down = ReadBool(doc.RootElement, "down"),
                            Left = ReadBool(doc.RootElement, "left"),
                            Right = ReadBool(doc.RootElement, "right")
                        };
                    }
                }
                else if (type == "shoot")
                {
                    if (!_matchFinished)
                    {
                        player.PendingShoot = true;
                    }
                }
                else if (type == "ping")
                {
                    if (doc.RootElement.TryGetProperty("nonce", out var nonceElement) && nonceElement.TryGetInt64(out var nonce))
                    {
                        client.PendingPongNonce = nonce;
                    }
                }
                else if (type == "resetGame")
                {
                    var now = DateTimeOffset.UtcNow;
                    var elapsed = GetMatchElapsedTime(now);
                    if (elapsed >= MinimumResetElapsedTime)
                    {
                        ResetMatch();
                        _logger.LogInformation("Match reset requested by {PlayerId}", playerId);
                    }
                    else
                    {
                        var retryAfterMs = (long)Math.Ceiling((MinimumResetElapsedTime - elapsed).TotalMilliseconds);
                        _logger.LogInformation(
                            "Match reset rejected for {PlayerId}. Reset is available in {RetryAfterMs} ms.",
                            playerId,
                            retryAfterMs);

                        TryQueueJson(client, new
                        {
                            type = "resetRejected",
                            reason = "minimumMatchElapsedTime",
                            retryAfterMs
                        });
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON received from {PlayerId}.", playerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while processing a message from {PlayerId}.", playerId);
        }
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;
    }

    private bool TryRegisterClientMessage(string playerId)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (!_clients.TryGetValue(playerId, out var client))
            {
                return false;
            }

            client.LastReceivedAtUtc = now;
            if (now - client.RateLimitWindowStartedAtUtc >= RateLimitWindow)
            {
                client.RateLimitWindowStartedAtUtc = now;
                client.MessagesInCurrentWindow = 0;
            }

            client.MessagesInCurrentWindow++;
            return client.MessagesInCurrentWindow <= MaxMessagesPerRateLimitWindow;
        }
    }

    private TimeSpan GetMatchElapsedTime(DateTimeOffset now)
    {
        if (now <= _matchStartedAtUtc)
        {
            return TimeSpan.Zero;
        }

        return now - _matchStartedAtUtc;
    }

    private async Task GameLoopAsync()
    {
        var tickMs = 1000 / TickRate;
        var last = DateTimeOffset.UtcNow;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var dt = (float)(now - last).TotalSeconds;
                if (dt <= 0f)
                {
                    dt = tickMs / 1000f;
                }
                if (dt > 0.1f)
                {
                    dt = 0.1f;
                }
                last = now;

                string payload;
                List<(ConnectedClient Client, long? PendingPongNonce)> clients;

                lock (_sync)
                {
                    Simulate(dt);
                    payload = BuildStatePayload();
                    clients = _clients.Values
                        .Select(c =>
                        {
                            var pendingPongNonce = c.PendingPongNonce;
                            c.PendingPongNonce = null;
                            return (Client: c, PendingPongNonce: pendingPongNonce);
                        })
                        .ToList();
                }

                var deadClients = new List<string>();

                foreach (var item in clients)
                {
                    var client = item.Client;
                    if (client.Socket.State != WebSocketState.Open || client.IsStopRequested)
                    {
                        deadClients.Add(client.PlayerId);
                        continue;
                    }

                    if (now - client.LastReceivedAtUtc > MaxClientIdleTime)
                    {
                        _logger.LogInformation("Client {PlayerId} removed after more than {IdleSeconds} seconds without inbound messages.", client.PlayerId, MaxClientIdleTime.TotalSeconds);
                        deadClients.Add(client.PlayerId);
                        continue;
                    }

                    if (item.PendingPongNonce is not null && !TryQueueJson(client, new
                    {
                        type = "pong",
                        nonce = item.PendingPongNonce.Value
                    }))
                    {
                        deadClients.Add(client.PlayerId);
                        continue;
                    }

                    if (!client.TryQueueRawJson(payload))
                    {
                        deadClients.Add(client.PlayerId);
                    }
                }

                foreach (var playerId in deadClients.Distinct())
                {
                    RemoveClient(playerId, abortSocket: true);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in the game loop tick.");
            }

            try
            {
                await Task.Delay(tickMs, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Game loop stopped.");
    }

    private void Simulate(float dt)
    {
        UpdateMatchClock(DateTimeOffset.UtcNow);

        if (_matchFinished)
        {
            foreach (var player in _players.Values)
            {
                player.Input = new InputState();
                player.PendingShoot = false;
            }

            UpdateShotTraces(dt);
            UpdateHitEffects(dt);
            return;
        }

        foreach (var player in _players.Values)
        {
            if (player.ShootCooldownRemaining > 0f)
            {
                player.ShootCooldownRemaining = MathF.Max(0f, player.ShootCooldownRemaining - dt);
            }

            UpdatePlayerMovement(player, dt);
        }

        ResolvePlayerSeparation();
        ProcessPendingShots();
        UpdateCarriedFlags();

        foreach (var player in _players.Values)
        {
            ResolveFlags(player);
        }

        UpdateShotTraces(dt);
        UpdateHitEffects(dt);
    }

    private void UpdatePlayerMovement(PlayerRuntime player, float dt)
    {
        var direction = new Vec2(
            (player.Input.Right ? 1f : 0f) - (player.Input.Left ? 1f : 0f),
            (player.Input.Down ? 1f : 0f) - (player.Input.Up ? 1f : 0f));

        direction = Geometry.Normalize(direction);
        if (Geometry.Length(direction) > 0.001f)
        {
            player.Facing = direction;
        }

        var delta = direction * (player.MoveSpeed * dt);
        if (Geometry.Length(delta) <= 0.001f)
        {
            return;
        }

        player.Position = MoveAgainstWorld(player.Position, delta, player.Radius);
    }

    private void ResetMatch()
    {
        _blueScore = 0;
        _redScore = 0;
        _shotTraces.Clear();
        _hitEffects.Clear();
        StartNewMatchClock(DateTimeOffset.UtcNow);
        ReassignPlayerTeams();

        foreach (var flag in Map.FlagsByTeam.Values)
        {
            flag.ResetToBase();
        }

        foreach (var player in _players.Values)
        {
            var spawn = FindSpawn(player.Team, player.Id);
            player.Position = spawn;
            player.SpawnPosition = spawn;
            player.Facing = player.Team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f);
            player.Input = new InputState();
            player.CarryingFlagTeam = null;
            player.ShootCooldownRemaining = 0f;
            player.PendingShoot = false;
        }
    }

    private void StartNewMatchClock(DateTimeOffset now)
    {
        _matchStartedAtUtc = now;
        _matchEndsAtUtc = now.Add(MatchDuration);
        _matchFinished = false;
        _winnerTeam = null;
        _loserTeam = null;
    }

    private void ReassignPlayerTeams()
    {
        if (_players.Count == 0)
        {
            return;
        }

        var players = _players.Values
            .OrderBy(_ => _random.Next())
            .ToList();

        var firstTeam = _random.Next(2) == 0 ? "blue" : "red";
        var secondTeam = firstTeam == "blue" ? "red" : "blue";

        for (var i = 0; i < players.Count; i++)
        {
            players[i].Team = i % 2 == 0 ? firstTeam : secondTeam;
        }
    }

    private void UpdateMatchClock(DateTimeOffset now)
    {
        if (_matchFinished || now < _matchEndsAtUtc)
        {
            return;
        }

        FinishMatch();
    }

    private void FinishMatch()
    {
        if (_matchFinished)
        {
            return;
        }

        _matchFinished = true;

        if (_blueScore > _redScore)
        {
            _winnerTeam = "blue";
            _loserTeam = "red";
        }
        else if (_redScore > _blueScore)
        {
            _winnerTeam = "red";
            _loserTeam = "blue";
        }
        else
        {
            _winnerTeam = "draw";
            _loserTeam = null;
        }

        foreach (var player in _players.Values)
        {
            player.Input = new InputState();
            player.PendingShoot = false;
            player.ShootCooldownRemaining = 0f;
        }

        _logger.LogInformation(
            "Match finished. Blue {BlueScore} - Red {RedScore}. Winner: {WinnerTeam}",
            _blueScore,
            _redScore,
            _winnerTeam);
    }

    private long GetMatchRemainingMilliseconds(DateTimeOffset now)
    {
        if (_matchFinished)
        {
            return 0;
        }

        var remaining = _matchEndsAtUtc - now;
        return Math.Max(0, (long)remaining.TotalMilliseconds);
    }

    private void ResolvePlayerSeparation()
    {
        if (_players.Count < 2)
        {
            return;
        }

        var players = _players.Values.ToList();
        const int maxIterations = 6;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var changed = false;

            for (var i = 0; i < players.Count - 1; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    if (SeparatePlayers(players[i], players[j]))
                    {
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private bool SeparatePlayers(PlayerRuntime a, PlayerRuntime b)
    {
        var delta = b.Position - a.Position;
        var minDistance = a.Radius + b.Radius;
        var distanceSquared = Geometry.DistanceSquared(a.Position, b.Position);

        if (distanceSquared >= minDistance * minDistance)
        {
            return false;
        }

        Vec2 normal;
        float distance;

        if (distanceSquared <= 0.0001f)
        {
            normal = ComputeFallbackSeparationNormal(a, b);
            distance = 0f;
        }
        else
        {
            distance = MathF.Sqrt(distanceSquared);
            normal = new Vec2(delta.X / distance, delta.Y / distance);
        }

        var overlap = minDistance - distance;
        if (overlap <= 0f)
        {
            return false;
        }

        const float skin = 0.05f;
        var pushDistance = overlap + skin;
        var halfPush = pushDistance * 0.5f;

        var movedA = TryApplyWorldTranslation(a, normal * -halfPush, out var appliedA);
        var movedB = TryApplyWorldTranslation(b, normal * halfPush, out var appliedB);

        var remaining = pushDistance - appliedA - appliedB;
        if (remaining > 0.01f)
        {
            if (!movedA && movedB)
            {
                TryApplyWorldTranslation(b, normal * remaining, out _);
            }
            else if (!movedB && movedA)
            {
                TryApplyWorldTranslation(a, normal * -remaining, out _);
            }
        }

        return movedA || movedB;
    }

    private Vec2 ComputeFallbackSeparationNormal(PlayerRuntime a, PlayerRuntime b)
    {
        var hash = HashCode.Combine(a.Id, b.Id);
        return (hash & 1) == 0 ? new Vec2(1f, 0f) : new Vec2(0f, 1f);
    }

    private void ProcessPendingShots()
    {
        foreach (var player in _players.Values)
        {
            if (!player.PendingShoot)
            {
                continue;
            }

            player.PendingShoot = false;
            FireShot(player);
        }
    }

    private void FireShot(PlayerRuntime shooter)
    {
        if (shooter.ShootCooldownRemaining > 0f)
        {
            return;
        }

        var direction = Geometry.Normalize(shooter.Facing);
        if (Geometry.Length(direction) <= 0.0001f)
        {
            direction = shooter.Team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f);
        }

        var start = shooter.Position + (direction * (shooter.Radius + 4f));
        var maxDistance = FindClosestBlockDistance(start, direction, ShotRange);
        var hit = FindClosestPlayerHit(shooter.Id, start, direction, maxDistance);
        var shotDistance = hit is null ? maxDistance : hit.Distance;
        var end = start + (direction * shotDistance);

        _shotTraces.Add(new ShotTraceRuntime
        {
            Id = $"shot-{Guid.NewGuid():N}",
            ShooterPlayerId = shooter.Id,
            Team = shooter.Team,
            Start = start,
            End = end,
            RemainingLifetime = ShotTraceLifetimeSeconds
        });

        shooter.ShootCooldownRemaining = ShotCooldownSeconds;

        if (hit is not null)
        {
            RegisterHitEffect(shooter, hit.Player, end);
            EliminatePlayer(hit.Player);
        }
    }

    private void RegisterHitEffect(PlayerRuntime shooter, PlayerRuntime victim, Vec2 impactPosition)
    {
        _hitEffects.Add(new HitEffectRuntime
        {
            Id = $"hit-{Guid.NewGuid():N}",
            ShooterPlayerId = shooter.Id,
            VictimPlayerId = victim.Id,
            ShooterTeam = shooter.Team,
            VictimTeam = victim.Team,
            ImpactPosition = impactPosition,
            RemainingLifetime = HitEffectLifetimeSeconds
        });
    }

    private float FindClosestBlockDistance(Vec2 origin, Vec2 direction, float maxDistance)
    {
        var bestDistance = maxDistance;

        if (Geometry.RayIntersectsPolygonDistance(origin, direction, Map.Perimeter.Points, out var perimeterDistance) && perimeterDistance < bestDistance)
        {
            bestDistance = perimeterDistance;
        }

        foreach (var obstacle in Map.Obstacles)
        {
            if (!obstacle.Hard)
            {
                continue;
            }

            var hit = false;
            var candidate = 0f;

            if (obstacle.Type == "rect")
            {
                hit = Geometry.RayIntersectsRectDistance(origin, direction, obstacle, out candidate);
            }
            else if (obstacle.Type == "circle")
            {
                hit = Geometry.RayIntersectsCircleDistance(origin, direction, obstacle.Position, obstacle.Radius, out candidate);
            }
            else if (obstacle.Type == "polygon" && obstacle.Points is not null)
            {
                hit = Geometry.RayIntersectsPolygonDistance(origin, direction, obstacle.Points, out candidate);
            }

            if (hit && candidate < bestDistance)
            {
                bestDistance = candidate;
            }
        }

        return bestDistance;
    }

    private RayPlayerHit? FindClosestPlayerHit(string shooterId, Vec2 origin, Vec2 direction, float maxDistance)
    {
        RayPlayerHit? bestHit = null;

        foreach (var player in _players.Values)
        {
            if (player.Id == shooterId)
            {
                continue;
            }

            if (!Geometry.RayIntersectsCircleDistance(origin, direction, player.Position, player.Radius, out var distance))
            {
                continue;
            }

            if (distance > maxDistance)
            {
                continue;
            }

            if (bestHit is null || distance < bestHit.Distance)
            {
                bestHit = new RayPlayerHit(player, distance);
            }
        }

        return bestHit;
    }

    private void EliminatePlayer(PlayerRuntime player)
    {
        if (player.CarryingFlagTeam is not null && Map.FlagsByTeam.TryGetValue(player.CarryingFlagTeam, out var carriedFlag))
        {
            carriedFlag.CarriedByPlayerId = null;
            carriedFlag.Position = player.Position;
            player.CarryingFlagTeam = null;
        }

        var respawn = FindRespawnPosition(player);
        player.Position = respawn;
        player.SpawnPosition = respawn;
        player.Facing = player.Team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f);
        player.ShootCooldownRemaining = 0.35f;
        player.PendingShoot = false;
    }

    private Vec2 FindRespawnPosition(PlayerRuntime player)
    {
        return FindSpawn(player.Team, player.Id, player.Radius);
    }

    private void UpdateCarriedFlags()
    {
        foreach (var flag in Map.FlagsByTeam.Values)
        {
            if (flag.CarriedByPlayerId is not null && _players.TryGetValue(flag.CarriedByPlayerId, out var carrier))
            {
                flag.Position = carrier.Position;
            }
        }
    }

    private void UpdateShotTraces(float dt)
    {
        for (var i = _shotTraces.Count - 1; i >= 0; i--)
        {
            _shotTraces[i].RemainingLifetime -= dt;
            if (_shotTraces[i].RemainingLifetime <= 0f)
            {
                _shotTraces.RemoveAt(i);
            }
        }
    }

    private void UpdateHitEffects(float dt)
    {
        for (var i = _hitEffects.Count - 1; i >= 0; i--)
        {
            _hitEffects[i].RemainingLifetime -= dt;
            if (_hitEffects[i].RemainingLifetime <= 0f)
            {
                _hitEffects.RemoveAt(i);
            }
        }
    }

    private Vec2 MoveAgainstWorld(Vec2 start, Vec2 delta, float radius)
    {
        if (Geometry.Length(delta) <= 0.001f)
        {
            return start;
        }

        var full = start + delta;
        if (!CollidesWithWorld(full, radius))
        {
            return full;
        }

        var current = start;
        var xOnly = new Vec2(current.X + delta.X, current.Y);
        if (MathF.Abs(delta.X) > 0.001f && !CollidesWithWorld(xOnly, radius))
        {
            current = xOnly;
        }

        var yOnly = new Vec2(current.X, current.Y + delta.Y);
        if (MathF.Abs(delta.Y) > 0.001f && !CollidesWithWorld(yOnly, radius))
        {
            current = yOnly;
        }

        if (current.X != start.X || current.Y != start.Y)
        {
            return current;
        }

        var fractions = new[] { 0.75f, 0.5f, 0.35f, 0.2f, 0.1f };
        foreach (var fraction in fractions)
        {
            var partial = delta * fraction;
            var candidate = start + partial;
            if (!CollidesWithWorld(candidate, radius))
            {
                return candidate;
            }

            xOnly = new Vec2(start.X + partial.X, start.Y);
            if (MathF.Abs(partial.X) > 0.001f && !CollidesWithWorld(xOnly, radius))
            {
                return xOnly;
            }

            yOnly = new Vec2(start.X, start.Y + partial.Y);
            if (MathF.Abs(partial.Y) > 0.001f && !CollidesWithWorld(yOnly, radius))
            {
                return yOnly;
            }
        }

        return start;
    }

    private bool TryApplyWorldTranslation(PlayerRuntime player, Vec2 delta, out float appliedDistance)
    {
        appliedDistance = 0f;
        var length = Geometry.Length(delta);
        if (length <= 0.001f)
        {
            return false;
        }

        var target = MoveAgainstWorld(player.Position, delta, player.Radius);
        var applied = target - player.Position;
        var movedDistance = Geometry.Length(applied);
        if (movedDistance <= 0.001f)
        {
            return false;
        }

        var direction = new Vec2(delta.X / length, delta.Y / length);
        appliedDistance = MathF.Max(0f, applied.X * direction.X + applied.Y * direction.Y);
        player.Position = target;
        return true;
    }

    private bool CollidesWithWorld(Vec2 position, float radius)
    {
        if (!Geometry.IsCircleInsidePerimeter(position, radius, Map.Perimeter.Points))
        {
            return true;
        }

        foreach (var obstacle in Map.Obstacles)
        {
            if (!obstacle.Hard)
            {
                continue;
            }

            if (obstacle.Type == "rect" && Geometry.CircleIntersectsRect(position, radius, obstacle))
            {
                return true;
            }

            if (obstacle.Type == "circle" && Geometry.CircleIntersectsCircle(position, radius, obstacle))
            {
                return true;
            }

            if (obstacle.Type == "polygon" && obstacle.Points is not null && Geometry.CircleIntersectsPolygon(position, radius, obstacle.Points))
            {
                return true;
            }
        }

        return false;
    }

    private bool CollidesWithPlayers(Vec2 position, float radius, string? ignoredPlayerId = null)
    {
        foreach (var otherPlayer in _players.Values)
        {
            if (otherPlayer.Id == ignoredPlayerId)
            {
                continue;
            }

            var minDistance = radius + otherPlayer.Radius;
            if (Geometry.DistanceSquared(position, otherPlayer.Position) < minDistance * minDistance)
            {
                return true;
            }
        }

        return false;
    }

    private void ResolveFlags(PlayerRuntime player)
    {
        var ownFlag = Map.FlagsByTeam[player.Team];
        var enemyTeam = player.Team == "blue" ? "red" : "blue";
        var enemyFlag = Map.FlagsByTeam[enemyTeam];

        if (ownFlag.CarriedByPlayerId is null
            && !ownFlag.IsAtBase
            && Geometry.DistanceSquared(player.Position, ownFlag.Position) <= 26f * 26f)
        {
            ownFlag.ResetToBase();
        }

        if (player.CarryingFlagTeam is null)
        {
            if (enemyFlag.CarriedByPlayerId is null && Geometry.DistanceSquared(player.Position, enemyFlag.Position) <= 28f * 28f)
            {
                player.CarryingFlagTeam = enemyFlag.Team;
                enemyFlag.CarriedByPlayerId = player.Id;
                enemyFlag.Position = player.Position;
            }
        }
        else if (player.CarryingFlagTeam == enemyTeam)
        {
            if (ownFlag.IsAtBase && Geometry.DistanceSquared(player.Position, ownFlag.BasePosition) <= 30f * 30f)
            {
                if (player.Team == "blue")
                {
                    _blueScore++;
                }
                else
                {
                    _redScore++;
                }

                enemyFlag.ResetToBase();
                player.CarryingFlagTeam = null;
            }
        }
    }

    private string BuildStatePayload()
    {
        var now = DateTimeOffset.UtcNow;
        var dto = new
        {
            type = "state",
            serverTime = now.ToUnixTimeMilliseconds(),
            scores = new { blue = _blueScore, red = _redScore },
            match = new
            {
                status = _matchFinished ? "finished" : "running",
                durationSeconds = MatchDurationSeconds,
                startedAt = _matchStartedAtUtc.ToUnixTimeMilliseconds(),
                endsAt = _matchEndsAtUtc.ToUnixTimeMilliseconds(),
                remainingMs = GetMatchRemainingMilliseconds(now),
                winnerTeam = _winnerTeam,
                loserTeam = _loserTeam,
                isTie = _winnerTeam == "draw"
            },
            players = _players.Values.Select(player => new
            {
                id = player.Id,
                name = player.Name,
                team = player.Team,
                x = player.Position.X,
                y = player.Position.Y,
                facingX = player.Facing.X,
                facingY = player.Facing.Y,
                radius = player.Radius,
                carryingFlagTeam = player.CarryingFlagTeam,
                shootCooldown = player.ShootCooldownRemaining
            }).ToArray(),
            flags = Map.FlagsByTeam.Values.Select(flag => new
            {
                id = flag.Id,
                team = flag.Team,
                x = flag.Position.X,
                y = flag.Position.Y,
                baseX = flag.BasePosition.X,
                baseY = flag.BasePosition.Y,
                atBase = flag.IsAtBase,
                carriedByPlayerId = flag.CarriedByPlayerId
            }).ToArray(),
            shots = _shotTraces.Select(shot => new
            {
                id = shot.Id,
                shooterPlayerId = shot.ShooterPlayerId,
                team = shot.Team,
                startX = shot.Start.X,
                startY = shot.Start.Y,
                endX = shot.End.X,
                endY = shot.End.Y,
                life = shot.RemainingLifetime
            }).ToArray(),
            events = _hitEffects.Select(effect => new
            {
                id = effect.Id,
                type = "playerHit",
                shooterPlayerId = effect.ShooterPlayerId,
                victimPlayerId = effect.VictimPlayerId,
                shooterTeam = effect.ShooterTeam,
                victimTeam = effect.VictimTeam,
                impactX = effect.ImpactPosition.X,
                impactY = effect.ImpactPosition.Y,
                life = effect.RemainingLifetime
            }).ToArray()
        };

        return JsonSerializer.Serialize(dto, _jsonOptions);
    }

    private void SendStateTo(string playerId)
    {
        ConnectedClient? client;
        string payload;
        lock (_sync)
        {
            _clients.TryGetValue(playerId, out client);
            payload = BuildStatePayload();
        }

        if (client is not null && client.Socket.State == WebSocketState.Open && !client.TryQueueRawJson(payload))
        {
            _logger.LogWarning("Could not queue initial state for {PlayerId}. Removing client.", playerId);
            RemoveClient(playerId, abortSocket: true);
        }
    }

    private bool TryQueueJson(ConnectedClient client, object payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        return client.TryQueueRawJson(json);
    }

    private async Task ClientWriterLoopAsync(ConnectedClient client)
    {
        try
        {
            await foreach (var payload in client.Outbound.Reader.ReadAllAsync(client.SendCancellation.Token))
            {
                if (client.Socket.State != WebSocketState.Open)
                {
                    break;
                }

                await SendRawJsonDirectAsync(client.Socket, payload, client.SendCancellation.Token);
            }
        }
        catch (OperationCanceledException ex) when (client.IsStopRequested || _cts.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "WebSocket writer stopped for {PlayerId}.", client.PlayerId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "WebSocket send timed out for {PlayerId}.", client.PlayerId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket writer failed for {PlayerId}.", client.PlayerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected WebSocket writer failure for {PlayerId}.", client.PlayerId);
        }
        finally
        {
            if (!client.IsStopRequested)
            {
                RemoveClient(client.PlayerId, abortSocket: true);
            }
        }
    }

    private static async Task SendRawJsonDirectAsync(WebSocket socket, string payload, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ClientSendTimeout);

        var buffer = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, timeoutCts.Token);
    }

    private void RemoveClient(string playerId, bool abortSocket)
    {
        ConnectedClient? client = null;
        PlayerRuntime? removedPlayer = null;

        lock (_sync)
        {
            _clients.Remove(playerId, out client);
            if (_players.Remove(playerId, out var player))
            {
                removedPlayer = player;
                if (player.CarryingFlagTeam is not null && Map.FlagsByTeam.TryGetValue(player.CarryingFlagTeam, out var flag))
                {
                    flag.ResetToBase();
                }
            }
        }

        if (client is not null)
        {
            try
            {
                client.Stop(abortSocket);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stop WebSocket client resources for {PlayerId}.", playerId);
            }
        }

        if (client is not null || removedPlayer is not null)
        {
            _logger.LogInformation("Client removed {PlayerId}", playerId);
        }
    }

    private string ChooseTeam()
    {
        var blue = _players.Values.Count(p => p.Team == "blue");
        var red = _players.Values.Count(p => p.Team == "red");
        return blue <= red ? "blue" : "red";
    }

    private Vec2 FindSpawn(string team, string? ignoredPlayerId = null, float radius = PlayerRadius)
    {
        if (TryFindPreferredSpawn(team, radius, ignoredPlayerId, out var spawn))
        {
            return spawn;
        }

        _logger.LogWarning("Could not find a clear preferred spawn for team {Team}. Falling back to the team's half of the map.", team);
        if (TryFindAnyClearSpawnInTeamHalf(team, radius, ignoredPlayerId, out spawn))
        {
            return spawn;
        }

        _logger.LogWarning("Could not find a clear spawn for team {Team}. Falling back to the legacy flag-adjacent spawn.", team);
        return FindLegacyFlagSpawn(team, radius, ignoredPlayerId);
    }

    private bool TryFindPreferredSpawn(string team, float radius, string? ignoredPlayerId, out Vec2 spawn)
    {
        var anchor = GetTeamSpawnAnchor(team, radius);
        if (IsClearSpawn(anchor, radius, ignoredPlayerId))
        {
            spawn = anchor;
            return true;
        }

        var centralHalfWidth = MathF.Min(MathF.Max(Map.Width * 0.24f, radius * 6f), 460f);
        var verticalHalfHeight = MathF.Min(MathF.Max(Map.Height * 0.15f, radius * 6f), 190f);

        for (var i = 0; i < 160; i++)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2d);
            var distance = MathF.Sqrt((float)_random.NextDouble());
            var candidate = new Vec2(
                anchor.X + MathF.Cos(angle) * centralHalfWidth * distance,
                anchor.Y + MathF.Sin(angle) * verticalHalfHeight * distance);

            if (!IsInsidePreferredSpawnZone(candidate, team, radius))
            {
                continue;
            }

            if (IsClearSpawn(candidate, radius, ignoredPlayerId))
            {
                spawn = candidate;
                return true;
            }
        }

        return TryFindClearSpawnByGrid(team, radius, ignoredPlayerId, preferredZoneOnly: true, out spawn);
    }

    private bool TryFindAnyClearSpawnInTeamHalf(string team, float radius, string? ignoredPlayerId, out Vec2 spawn)
    {
        return TryFindClearSpawnByGrid(team, radius, ignoredPlayerId, preferredZoneOnly: false, out spawn);
    }

    private bool TryFindClearSpawnByGrid(string team, float radius, string? ignoredPlayerId, bool preferredZoneOnly, out Vec2 spawn)
    {
        var anchor = GetTeamSpawnAnchor(team, radius);
        var candidates = new List<Vec2>();
        var step = MathF.Max(radius * 2.4f, 30f);
        var edgeInset = radius + 18f;
        var centerX = Map.Width * 0.5f;

        float minX;
        float maxX;
        float minY;
        float maxY;

        if (preferredZoneOnly)
        {
            var centralHalfWidth = MathF.Min(MathF.Max(Map.Width * 0.28f, radius * 8f), 520f);
            minX = MathF.Max(edgeInset, centerX - centralHalfWidth);
            maxX = MathF.Min(Map.Width - edgeInset, centerX + centralHalfWidth);

            if (team == "red")
            {
                minY = edgeInset;
                maxY = MathF.Min(Map.Height - edgeInset, MathF.Max(edgeInset, Map.Height * 0.38f));
            }
            else
            {
                minY = MathF.Max(edgeInset, MathF.Min(Map.Height - edgeInset, Map.Height * 0.62f));
                maxY = Map.Height - edgeInset;
            }
        }
        else
        {
            minX = edgeInset;
            maxX = Map.Width - edgeInset;

            if (team == "red")
            {
                minY = edgeInset;
                maxY = MathF.Min(Map.Height - edgeInset, Map.Height * 0.5f);
            }
            else
            {
                minY = MathF.Max(edgeInset, Map.Height * 0.5f);
                maxY = Map.Height - edgeInset;
            }
        }

        if (maxX < minX || maxY < minY)
        {
            spawn = default;
            return false;
        }

        for (var y = minY; y <= maxY; y += step)
        {
            for (var x = minX; x <= maxX; x += step)
            {
                candidates.Add(new Vec2(x, y));
            }
        }

        candidates.Sort((a, b) => Geometry.DistanceSquared(a, anchor).CompareTo(Geometry.DistanceSquared(b, anchor)));

        foreach (var candidate in candidates)
        {
            if (preferredZoneOnly && !IsInsidePreferredSpawnZone(candidate, team, radius))
            {
                continue;
            }

            if (IsClearSpawn(candidate, radius, ignoredPlayerId))
            {
                spawn = candidate;
                return true;
            }
        }

        spawn = default;
        return false;
    }

    private Vec2 GetTeamSpawnAnchor(string team, float radius)
    {
        var edgeInset = radius + 28f;
        var x = Math.Clamp(Map.Width * 0.5f, edgeInset, MathF.Max(edgeInset, Map.Width - edgeInset));
        var topY = Math.Clamp(Map.Height * 0.14f, edgeInset, MathF.Max(edgeInset, Map.Height - edgeInset));
        var bottomY = Math.Clamp(Map.Height * 0.86f, edgeInset, MathF.Max(edgeInset, Map.Height - edgeInset));
        return team == "red" ? new Vec2(x, topY) : new Vec2(x, bottomY);
    }

    private bool IsInsidePreferredSpawnZone(Vec2 candidate, string team, float radius)
    {
        var edgeInset = radius + 18f;
        var centerX = Map.Width * 0.5f;
        var centralHalfWidth = MathF.Min(MathF.Max(Map.Width * 0.30f, radius * 8f), 560f);

        if (candidate.X < centerX - centralHalfWidth || candidate.X > centerX + centralHalfWidth)
        {
            return false;
        }

        if (team == "red")
        {
            return candidate.Y >= edgeInset && candidate.Y <= MathF.Max(edgeInset, Map.Height * 0.40f);
        }

        return candidate.Y >= MathF.Min(Map.Height - edgeInset, Map.Height * 0.60f) && candidate.Y <= Map.Height - edgeInset;
    }

    private bool IsClearSpawn(Vec2 spawn, float radius, string? ignoredPlayerId)
    {
        return !CollidesWithWorld(spawn, radius) && !CollidesWithPlayers(spawn, radius, ignoredPlayerId);
    }

    private Vec2 FindLegacyFlagSpawn(string team, float radius, string? ignoredPlayerId)
    {
        var ownFlag = Map.FlagsByTeam[team];

        for (var i = 0; i < 48; i++)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2d);
            var distance = 80f + (float)_random.NextDouble() * 60f;
            var spawn = new Vec2(
                ownFlag.BasePosition.X + MathF.Cos(angle) * distance,
                ownFlag.BasePosition.Y + MathF.Sin(angle) * distance);

            if (IsClearSpawn(spawn, radius, ignoredPlayerId))
            {
                return spawn;
            }
        }

        var fallback = ownFlag.BasePosition + (team == "blue" ? new Vec2(50f, 0f) : new Vec2(-50f, 0f));
        return fallback;
    }

    private static GameMap LoadMapFromFile(string mapPath)
    {
        var rawJson = File.ReadAllText(mapPath);
        return LoadMapFromJson(rawJson);
    }

    private static GameMap LoadMapFromJson(string rawJson)
    {
        var source = JsonSerializer.Deserialize<MapDocument>(rawJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("The map could not be deserialized.");

        ValidateMapDocument(source);

        var perimeterDto = source.Objects.SingleOrDefault(o => o.Type == "perimeter")
            ?? throw new InvalidOperationException("The map must contain a perimeter.");
        if (perimeterDto.Points is null || perimeterDto.Points.Count < 3)
        {
            throw new InvalidOperationException("The perimeter is not valid.");
        }

        var perimeter = new PerimeterShape
        {
            Id = perimeterDto.Id,
            Points = perimeterDto.Points.Select(p => new Vec2(p.X, p.Y)).ToList()
        };

        var flags = source.Objects.Where(o => o.Type == "flag").ToList();
        if (flags.Count != 2 || flags.Any(f => string.IsNullOrWhiteSpace(f.Team) || f.X is null || f.Y is null))
        {
            throw new InvalidOperationException("The map must contain exactly two valid flags.");
        }

        var flagsByTeam = flags.ToDictionary(
            flag => flag.Team!,
            flag => new FlagRuntime
            {
                Id = flag.Id,
                Team = flag.Team!,
                BasePosition = new Vec2(flag.X!.Value, flag.Y!.Value),
                Position = new Vec2(flag.X!.Value, flag.Y!.Value)
            });

        if (!flagsByTeam.ContainsKey("blue") || !flagsByTeam.ContainsKey("red"))
        {
            throw new InvalidOperationException("One blue flag and one red flag are required.");
        }

        var obstacles = source.Objects
            .Where(o => o.Type is "polygon" or "rect" or "circle")
            .Select(dto => new ObstacleShape
            {
                Id = dto.Id,
                Type = dto.Type,
                Hard = dto.Hard,
                Position = new Vec2(dto.X ?? 0f, dto.Y ?? 0f),
                Width = dto.Width ?? 0f,
                Height = dto.Height ?? 0f,
                Radius = dto.Radius ?? 0f,
                Points = dto.Points?.Select(p => new Vec2(p.X, p.Y)).ToList()
            })
            .ToList();

        return new GameMap
        {
            Source = source,
            RawJson = rawJson,
            Perimeter = perimeter,
            Obstacles = obstacles,
            FlagsByTeam = flagsByTeam
        };
    }

    private static void ValidateMapDocument(MapDocument source)
    {
        if (source.Meta is null)
        {
            throw new InvalidOperationException("The map metadata is missing.");
        }

        if (source.Meta.Canvas is null)
        {
            throw new InvalidOperationException("The map canvas metadata is missing.");
        }

        var canvasWidth = source.Meta.Canvas.Width;
        var canvasHeight = source.Meta.Canvas.Height;
        if (canvasWidth < MinCanvasWidth || canvasWidth > MaxCanvasWidth || canvasHeight < MinCanvasHeight || canvasHeight > MaxCanvasHeight)
        {
            throw new InvalidOperationException($"The canvas must be between {MinCanvasWidth}x{MinCanvasHeight} and {MaxCanvasWidth}x{MaxCanvasHeight} pixels.");
        }

        if (source.Objects is null || source.Objects.Count == 0)
        {
            throw new InvalidOperationException("The map does not contain any objects.");
        }

        if (source.Objects.Count > MaxMapObjects)
        {
            throw new InvalidOperationException($"The map contains {source.Objects.Count} objects. The maximum allowed is {MaxMapObjects}.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hardObstacleCount = 0;
        var totalPolygonPoints = 0;
        var perimeterCount = 0;
        var blueFlagCount = 0;
        var redFlagCount = 0;

        for (var i = 0; i < source.Objects.Count; i++)
        {
            var dto = source.Objects[i];
            if (dto is null)
            {
                throw new InvalidOperationException($"Map object #{i + 1} is null.");
            }

            var objectLabel = GetObjectLabel(dto, i);
            if (string.IsNullOrWhiteSpace(dto.Id))
            {
                throw new InvalidOperationException($"{objectLabel} does not have a valid id.");
            }

            if (!seenIds.Add(dto.Id.Trim()))
            {
                throw new InvalidOperationException($"The map contains a duplicated object id: '{dto.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(dto.Type))
            {
                throw new InvalidOperationException($"{objectLabel} does not have a valid type.");
            }

            switch (dto.Type)
            {
                case "perimeter":
                    perimeterCount++;
                    if (!dto.Hard)
                    {
                        throw new InvalidOperationException($"{objectLabel} must be hard.");
                    }

                    ValidatePolygonObject(dto, objectLabel, canvasWidth, canvasHeight, ref totalPolygonPoints);
                    break;

                case "polygon":
                    if (dto.Hard)
                    {
                        hardObstacleCount++;
                    }

                    ValidatePolygonObject(dto, objectLabel, canvasWidth, canvasHeight, ref totalPolygonPoints);
                    break;

                case "rect":
                    if (dto.Hard)
                    {
                        hardObstacleCount++;
                    }

                    ValidateRectObject(dto, objectLabel, canvasWidth, canvasHeight);
                    break;

                case "circle":
                    if (dto.Hard)
                    {
                        hardObstacleCount++;
                    }

                    ValidateCircleObject(dto, objectLabel, canvasWidth, canvasHeight);
                    break;

                case "flag":
                    ValidateFlagObject(dto, objectLabel, canvasWidth, canvasHeight);
                    if (dto.Team == "blue")
                    {
                        blueFlagCount++;
                    }
                    else if (dto.Team == "red")
                    {
                        redFlagCount++;
                    }
                    break;

                default:
                    throw new InvalidOperationException($"{objectLabel} has an unsupported type: '{dto.Type}'.");
            }

            if (hardObstacleCount > MaxHardObstacles)
            {
                throw new InvalidOperationException($"The map contains more than {MaxHardObstacles} hard obstacles.");
            }
        }

        if (perimeterCount != 1)
        {
            throw new InvalidOperationException("The map must contain exactly one hard perimeter.");
        }

        if (blueFlagCount != 1 || redFlagCount != 1)
        {
            throw new InvalidOperationException("The map must contain exactly one blue flag and exactly one red flag.");
        }

        var perimeter = source.Objects.Single(o => o.Type == "perimeter");
        var perimeterPoints = perimeter.Points!.Select(p => new Vec2(p.X, p.Y)).ToList();
        foreach (var flag in source.Objects.Where(o => o.Type == "flag"))
        {
            var flagPosition = new Vec2(flag.X!.Value, flag.Y!.Value);
            if (!Geometry.PointInPolygon(flagPosition, perimeterPoints))
            {
                throw new InvalidOperationException($"Flag '{flag.Id}' must be inside the perimeter.");
            }
        }
    }

    private static void ValidateRectObject(MapObjectDto dto, string objectLabel, int canvasWidth, int canvasHeight)
    {
        var x = RequireFinite(dto.X, $"{objectLabel}.x");
        var y = RequireFinite(dto.Y, $"{objectLabel}.y");
        var width = RequirePositiveFinite(dto.Width, $"{objectLabel}.width");
        var height = RequirePositiveFinite(dto.Height, $"{objectLabel}.height");

        RequireCoordinateInRange(x, canvasWidth, $"{objectLabel}.x");
        RequireCoordinateInRange(y, canvasHeight, $"{objectLabel}.y");
        RequireCoordinateInRange(x + width, canvasWidth, $"{objectLabel}.x + width");
        RequireCoordinateInRange(y + height, canvasHeight, $"{objectLabel}.y + height");
    }

    private static void ValidateCircleObject(MapObjectDto dto, string objectLabel, int canvasWidth, int canvasHeight)
    {
        var x = RequireFinite(dto.X, $"{objectLabel}.x");
        var y = RequireFinite(dto.Y, $"{objectLabel}.y");
        var radius = RequirePositiveFinite(dto.Radius, $"{objectLabel}.radius");

        RequireCoordinateInRange(x, canvasWidth, $"{objectLabel}.x");
        RequireCoordinateInRange(y, canvasHeight, $"{objectLabel}.y");
        RequireCoordinateInRange(x - radius, canvasWidth, $"{objectLabel}.x - radius");
        RequireCoordinateInRange(x + radius, canvasWidth, $"{objectLabel}.x + radius");
        RequireCoordinateInRange(y - radius, canvasHeight, $"{objectLabel}.y - radius");
        RequireCoordinateInRange(y + radius, canvasHeight, $"{objectLabel}.y + radius");
    }

    private static void ValidateFlagObject(MapObjectDto dto, string objectLabel, int canvasWidth, int canvasHeight)
    {
        if (dto.Team is not "blue" and not "red")
        {
            throw new InvalidOperationException($"{objectLabel} must use team 'blue' or 'red'.");
        }

        if (dto.Hard)
        {
            throw new InvalidOperationException($"{objectLabel} must not be hard.");
        }

        var x = RequireFinite(dto.X, $"{objectLabel}.x");
        var y = RequireFinite(dto.Y, $"{objectLabel}.y");
        RequireCoordinateInsideCanvas(x, canvasWidth, $"{objectLabel}.x");
        RequireCoordinateInsideCanvas(y, canvasHeight, $"{objectLabel}.y");
    }

    private static void ValidatePolygonObject(MapObjectDto dto, string objectLabel, int canvasWidth, int canvasHeight, ref int totalPolygonPoints)
    {
        if (dto.Points is null || dto.Points.Count < 3)
        {
            throw new InvalidOperationException($"{objectLabel} must contain at least 3 points.");
        }

        if (dto.Points.Count > MaxPointsPerPolygon)
        {
            throw new InvalidOperationException($"{objectLabel} contains {dto.Points.Count} points. The maximum allowed per polygon is {MaxPointsPerPolygon}.");
        }

        totalPolygonPoints += dto.Points.Count;
        if (totalPolygonPoints > MaxTotalPolygonPoints)
        {
            throw new InvalidOperationException($"The map contains more than {MaxTotalPolygonPoints} polygon points in total.");
        }

        var points = new List<Vec2>(dto.Points.Count);
        for (var i = 0; i < dto.Points.Count; i++)
        {
            var point = dto.Points[i];
            if (point is null)
            {
                throw new InvalidOperationException($"{objectLabel}.points[{i}] is null.");
            }

            var x = RequireFinite(point.X, $"{objectLabel}.points[{i}].x");
            var y = RequireFinite(point.Y, $"{objectLabel}.points[{i}].y");
            RequireCoordinateInRange(x, canvasWidth, $"{objectLabel}.points[{i}].x");
            RequireCoordinateInRange(y, canvasHeight, $"{objectLabel}.points[{i}].y");
            points.Add(new Vec2(x, y));
        }

        if (CountDistinctPoints(points) < 3)
        {
            throw new InvalidOperationException($"{objectLabel} is degenerate because it has fewer than 3 distinct points.");
        }

        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (Geometry.DistanceSquared(a, b) < MinShapeSize * MinShapeSize)
            {
                throw new InvalidOperationException($"{objectLabel} is degenerate because it contains a zero-length edge.");
            }
        }

        if (MathF.Abs(GetSignedPolygonArea(points)) < MinPolygonArea)
        {
            throw new InvalidOperationException($"{objectLabel} is degenerate because its area is too small.");
        }

        if (HasSelfIntersections(points))
        {
            throw new InvalidOperationException($"{objectLabel} is degenerate because it has self-intersections.");
        }
    }

    private static float RequireFinite(float? value, string label)
    {
        if (value is null || !float.IsFinite(value.Value))
        {
            throw new InvalidOperationException($"{label} must be a finite number.");
        }

        return value.Value;
    }

    private static float RequirePositiveFinite(float? value, string label)
    {
        var finiteValue = RequireFinite(value, label);
        if (finiteValue <= 0f)
        {
            throw new InvalidOperationException($"{label} must be positive.");
        }

        return finiteValue;
    }

    private static void RequireCoordinateInRange(float value, int canvasAxisSize, string label)
    {
        if (value < -MaxCoordinateMargin || value > canvasAxisSize + MaxCoordinateMargin)
        {
            throw new InvalidOperationException($"{label} is outside the allowed coordinate range.");
        }
    }

    private static void RequireCoordinateInsideCanvas(float value, int canvasAxisSize, string label)
    {
        if (value < 0f || value > canvasAxisSize)
        {
            throw new InvalidOperationException($"{label} must be inside the canvas.");
        }
    }

    private static int CountDistinctPoints(IReadOnlyList<Vec2> points)
    {
        var count = 0;
        const float epsilon = 0.001f;
        for (var i = 0; i < points.Count; i++)
        {
            var isNew = true;
            for (var j = 0; j < i; j++)
            {
                if (MathF.Abs(points[i].X - points[j].X) < epsilon && MathF.Abs(points[i].Y - points[j].Y) < epsilon)
                {
                    isNew = false;
                    break;
                }
            }

            if (isNew)
            {
                count++;
            }
        }

        return count;
    }

    private static float GetSignedPolygonArea(IReadOnlyList<Vec2> points)
    {
        var sum = 0f;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum * 0.5f;
    }

    private static bool HasSelfIntersections(IReadOnlyList<Vec2> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var a1 = points[i];
            var a2 = points[(i + 1) % points.Count];
            for (var j = i + 1; j < points.Count; j++)
            {
                var edgesAreAdjacent = Math.Abs(i - j) == 1 || (i == 0 && j == points.Count - 1);
                if (edgesAreAdjacent)
                {
                    continue;
                }

                var b1 = points[j];
                var b2 = points[(j + 1) % points.Count];
                if (SegmentsIntersect(a1, a2, b1, b2))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsIntersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);

        if (o1 != o2 && o3 != o4)
        {
            return true;
        }

        return (o1 == 0 && IsPointOnSegment(c, a, b))
            || (o2 == 0 && IsPointOnSegment(d, a, b))
            || (o3 == 0 && IsPointOnSegment(a, c, d))
            || (o4 == 0 && IsPointOnSegment(b, c, d));
    }

    private static int Orientation(Vec2 a, Vec2 b, Vec2 c)
    {
        var value = Geometry.Cross(b - a, c - a);
        if (MathF.Abs(value) < 0.0001f)
        {
            return 0;
        }

        return value > 0f ? 1 : 2;
    }

    private static bool IsPointOnSegment(Vec2 point, Vec2 a, Vec2 b)
    {
        const float epsilon = 0.0001f;
        return point.X <= MathF.Max(a.X, b.X) + epsilon
            && point.X + epsilon >= MathF.Min(a.X, b.X)
            && point.Y <= MathF.Max(a.Y, b.Y) + epsilon
            && point.Y + epsilon >= MathF.Min(a.Y, b.Y);
    }

    private static string GetObjectLabel(MapObjectDto dto, int index)
    {
        return string.IsNullOrWhiteSpace(dto.Id)
            ? $"Map object #{index + 1}"
            : $"Map object '{dto.Id}'";
    }

    private sealed record RayPlayerHit(PlayerRuntime Player, float Distance);
}
