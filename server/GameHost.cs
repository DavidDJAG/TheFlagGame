using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TheFlag.Server;

public sealed class GameRoom
{
    private const float PlayerRadius = 14f;
    private const float ShotRange = 420f;
    private const float ShotCooldownSeconds = 0.25f;
    private const float ShotTraceLifetimeSeconds = 0.12f;
    private const float HitEffectLifetimeSeconds = 0.35f;
    private const int MaxIncomingMessageBytes = 16 * 1024;
    private const int MatchDurationSeconds = 5 * 60;
    public const int MaxPlayersPerRoom = 32;
    private const int MaxPlayers = MaxPlayersPerRoom;
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
    private const float SpawnAnchorEdgeRatio = 0.075f;
    private const float PreferredSpawnEdgeBandRatio = 0.24f;
    private const float PreferredSpawnScatterVerticalRatio = 0.075f;
    private const float MovementEpsilon = 0.001f;
    private const float MovementCollisionContactTolerance = 0.75f;
    private const float MovementSlideSkin = 0.5f;
    private const float MovementSlideProbeMargin = 2f;
    private const int MovementSlidePasses = 5;
    private const int MovementSweepIterations = 12;
    private static readonly TimeSpan ClientSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxClientIdleTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly string _mapPath;
    private readonly ServerRuntimeOptions _runtimeOptions;
    private readonly Action<string>? _roomEmptyCallback;
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
    private readonly Dictionary<string, PlayerStatsRuntime> _playerStats = [];
    private readonly List<GameEventRuntime> _frameEvents = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _random = new();

    private sealed record ClientRegistration(
        string ConnectionId,
        string? PlayerId,
        string? Team,
        string MapName,
        ConnectedClient Client,
        bool IsSpectator);

    private Task? _loopTask;
    private int _stopRequested;
    private int _blueScore;
    private int _redScore;
    private DateTimeOffset _matchStartedAtUtc;
    private DateTimeOffset _matchEndsAtUtc;
    private bool _matchFinished;
    private DateTimeOffset? _matchFinishedAtUtc;
    private bool _finishedStateBroadcasted;
    private string? _winnerTeam;
    private string? _loserTeam;
    private string _matchId = CreateMatchId();
    private long _stateSequence;
    private long _eventSequence;

    public GameRoom(
        string roomId,
        string mapPath,
        ILogger logger,
        Action<string>? roomEmptyCallback = null,
        ServerRuntimeOptions? runtimeOptions = null)
    {
        RoomId = roomId;
        _logger = logger;
        _mapPath = mapPath;
        _roomEmptyCallback = roomEmptyCallback;
        _runtimeOptions = runtimeOptions ?? ServerRuntimeOptions.Production;
        Map = LoadMapFromFile(mapPath);
        RawMapJson = Map.RawJson;
        StartNewMatchClock(DateTimeOffset.UtcNow);
    }

    public string RoomId { get; }
    public int TickRate => Math.Max(1, _runtimeOptions.TickRate);
    private bool IsTrainingTelemetryEnabled => _runtimeOptions.TrainingMode;
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

    public int ClientCount
    {
        get
        {
            lock (_sync)
            {
                return _clients.Count;
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _players.Count == 0 && _clients.Count == 0;
            }
        }
    }

    public string MapName
    {
        get
        {
            lock (_sync)
            {
                return Map.Source.Meta.Name;
            }
        }
    }

    public string MatchStatus
    {
        get
        {
            lock (_sync)
            {
                return _matchFinished ? "finished" : "running";
            }
        }
    }

    public RoomSummary GetSummary()
    {
        lock (_sync)
        {
            return new RoomSummary(
                RoomId,
                _players.Count,
                MaxPlayers,
                Map.Source.Meta.Name,
                _matchFinished ? "finished" : "running");
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

        _logger.LogInformation("Map replaced successfully for room {RoomId}: {MapName}", RoomId, nextMap.Source.Meta.Name);
        return new MapReplaceResult(true, 200, "Map updated successfully on the server.", nextMap.Source.Meta.Name, nextMap.Source.Objects.Count);
    }

    public string RawMapJson { get; private set; }
    public GameMap Map { get; private set; }

    private TimeSpan CurrentMatchDuration => TimeSpan.FromSeconds(Math.Max(1, _runtimeOptions.MatchDurationSecondsOverride ?? MatchDurationSeconds));
    private TimeSpan CurrentResetCooldown => TimeSpan.FromSeconds(Math.Max(0, _runtimeOptions.ResetCooldownSeconds));
    private bool IsMatchClockDisabled => _runtimeOptions.MatchDurationSecondsOverride is <= 0;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(GameLoopAsync);
        _logger.LogInformation("Game loop started for room {RoomId}.", RoomId);
    }

    public void Stop(bool waitForLoop = true)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

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

        if (waitForLoop && _loopTask is not null && Task.CurrentId != _loopTask.Id)
        {
            try
            {
                _loopTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The game loop for room {RoomId} did not stop cleanly.", RoomId);
            }
        }
    }

    public async Task HandleClientAsync(HttpContext context, string requestedTeam, bool isSpectator)
    {
        var roomWasAlreadyFull = false;
        if (!isSpectator)
        {
            lock (_sync)
            {
                roomWasAlreadyFull = _players.Count >= MaxPlayers;
            }
        }

        if (roomWasAlreadyFull)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("The room is full.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        var registration = TryAddClient(socket, requestedTeam, isSpectator);
        if (registration is null)
        {
            _logger.LogWarning("Room {RoomId} is full. Rejected WebSocket client because the room already has {MaxPlayers} players.", RoomId, MaxPlayers);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "room full", CancellationToken.None);
            return;
        }

        var connectionId = registration.ConnectionId;
        var playerId = registration.PlayerId;
        var team = registration.Team;
        var mapName = registration.MapName;
        var client = registration.Client;
        var role = registration.IsSpectator ? "spectator" : "player";

        client.WriterTask = Task.Run(() => ClientWriterLoopAsync(client));
        _logger.LogInformation(
            "Client connected {ConnectionId} in room {RoomId} as {Role} ({Team}, requested: {RequestedTeam})",
            connectionId,
            RoomId,
            role,
            team ?? "none",
            requestedTeam);

        if (!TryQueueJson(client, new
        {
            type = "welcome",
            roomId = RoomId,
            connectionId,
            playerId,
            role,
            spectator = registration.IsSpectator,
            team,
            teamSelection = registration.IsSpectator
                ? null
                : new
                {
                    requested = requestedTeam,
                    autoAssigned = requestedTeam == "auto"
                },
            tickRate = TickRate,
            mapName,
            training = new
            {
                enabled = _runtimeOptions.TrainingMode,
                timeScale = _runtimeOptions.TimeScale,
                runAsFastAsPossible = _runtimeOptions.RunAsFastAsPossible,
                maxSimulationStepSeconds = _runtimeOptions.MaxSimulationStepSeconds,
                maxSimulationSubstepsPerTick = _runtimeOptions.MaxSimulationSubstepsPerTick,
                matchClockDisabled = IsMatchClockDisabled
            }
        }))
        {
            _logger.LogWarning("Could not queue welcome message for {ConnectionId}. Closing connection.", connectionId);
            RemoveClient(connectionId, abortSocket: true);
            return;
        }

        SendStateTo(connectionId);

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

                if (!TryRegisterClientMessage(connectionId))
                {
                    closeStatus = WebSocketCloseStatus.PolicyViolation;
                    closeDescription = "rate limit exceeded";
                    _logger.LogWarning("Rate limit exceeded by {ConnectionId} in room {RoomId}. Closing connection.", connectionId, RoomId);
                    break;
                }

                HandleIncomingMessage(connectionId, message);
            }
        }
        catch (InvalidDataException ex)
        {
            closeStatus = WebSocketCloseStatus.MessageTooBig;
            closeDescription = "incoming message too large or invalid";
            _logger.LogWarning(ex, "Rejected WebSocket message from {ConnectionId}.", connectionId);
        }
        catch (OperationCanceledException) when (IsExpectedWebSocketStop(client, socket, context))
        {
            _logger.LogInformation("WebSocket receive loop ended for {ConnectionId}.", connectionId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Unexpected WebSocket receive cancellation for {ConnectionId}.", connectionId);
        }
        catch (WebSocketException ex) when (IsExpectedWebSocketClose(ex, client, socket, context))
        {
            _logger.LogInformation("WebSocket closed without clean handshake for {ConnectionId}.", connectionId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket receive failed for {ConnectionId}.", connectionId);
        }
        catch (Exception ex)
        {
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeDescription = "server error";
            _logger.LogError(ex, "Unexpected error while handling WebSocket client {ConnectionId}.", connectionId);
        }
        finally
        {
            RemoveClient(connectionId, abortSocket: false);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(closeStatus, closeDescription, CancellationToken.None);
                }
                catch (OperationCanceledException) when (IsExpectedWebSocketStop(client, socket, context))
                {
                    _logger.LogInformation("WebSocket close handshake canceled for {ConnectionId}.", connectionId);
                }
                catch (WebSocketException ex) when (IsExpectedWebSocketClose(ex, client, socket, context))
                {
                    _logger.LogInformation("WebSocket close handshake was not completed for {ConnectionId}.", connectionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not close WebSocket cleanly for {ConnectionId}.", connectionId);
                }
            }
        }
    }

    private ClientRegistration? TryAddClient(WebSocket socket, string requestedTeam, bool isSpectator)
    {
        lock (_sync)
        {
            if (!isSpectator && _players.Count >= MaxPlayers)
            {
                return null;
            }

            if (!isSpectator && _players.Count == 0)
            {
                ResetMatch();
            }

            var connectionId = $"{(isSpectator ? "s" : "p")}-{Guid.NewGuid():N}";
            string? playerId = null;
            string? team = null;

            if (!isSpectator)
            {
                var assignedPlayerId = connectionId;
                var assignedTeam = ChooseTeam(requestedTeam);
                playerId = assignedPlayerId;
                team = assignedTeam;
                var name = assignedTeam == "blue" ? $"Blue-{_players.Count + 1}" : $"Red-{_players.Count + 1}";
                var spawn = FindSpawn(assignedTeam);

                var player = new PlayerRuntime
                {
                    Id = assignedPlayerId,
                    Name = name,
                    Team = assignedTeam,
                    RequestedTeam = requestedTeam,
                    Position = spawn,
                    SpawnPosition = spawn,
                    Facing = assignedTeam == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f)
                };

                _players[assignedPlayerId] = player;
                if (IsTrainingTelemetryEnabled)
                {
                    _playerStats[assignedPlayerId] = new PlayerStatsRuntime
                    {
                        PlayerId = assignedPlayerId,
                        Name = name,
                        Team = assignedTeam
                    };
                }

                EmitGameEvent("playerJoined", player: player, team: assignedTeam, x: spawn.X, y: spawn.Y);
            }

            var now = DateTimeOffset.UtcNow;
            var client = new ConnectedClient
            {
                PlayerId = connectionId,
                Socket = socket,
                RoomId = RoomId,
                IsSpectator = isSpectator,
                LastReceivedAtUtc = now,
                RateLimitWindowStartedAtUtc = now
            };
            _clients[connectionId] = client;

            var mapName = Map.Source.Meta.Name;
            return new ClientRegistration(connectionId, playerId, team, mapName, client, isSpectator);
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

    private void HandleIncomingMessage(string connectionId, string message)
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
                if (!_clients.TryGetValue(connectionId, out var client))
                {
                    return;
                }

                if (type == "ping")
                {
                    if (doc.RootElement.TryGetProperty("nonce", out var nonceElement) && nonceElement.TryGetInt64(out var nonce))
                    {
                        client.PendingPongNonce = nonce;
                    }

                    return;
                }

                if (client.IsSpectator)
                {
                    return;
                }

                if (!_players.TryGetValue(connectionId, out var player))
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
                            if (_playerStats.TryGetValue(player.Id, out var stats))
                            {
                                stats.Name = player.Name;
                            }
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
                else if (type == "resetGame")
                {
                    var now = DateTimeOffset.UtcNow;
                    var elapsed = GetMatchElapsedTime(now);
                    var resetCooldown = CurrentResetCooldown;
                    if (elapsed >= resetCooldown)
                    {
                        ResetMatch();
                        _logger.LogInformation("Match reset requested by {PlayerId} in room {RoomId}", connectionId, RoomId);
                    }
                    else
                    {
                        var retryAfterMs = (long)Math.Ceiling((resetCooldown - elapsed).TotalMilliseconds);
                        _logger.LogInformation(
                            "Match reset rejected for {PlayerId}. Reset is available in {RetryAfterMs} ms.",
                            connectionId,
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
            _logger.LogWarning(ex, "Invalid JSON received from {ConnectionId}.", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while processing a message from {ConnectionId}.", connectionId);
        }
    }

    public static bool TryReadTeamPreference(HttpContext context, out string requestedTeam, out string? error)
    {
        var rawTeam = context.Request.Query["team"].ToString();
        if (string.IsNullOrWhiteSpace(rawTeam))
        {
            requestedTeam = "auto";
            error = null;
            return true;
        }

        requestedTeam = rawTeam.Trim().ToLowerInvariant();
        if (requestedTeam is "auto" or "blue" or "red")
        {
            error = null;
            return true;
        }

        error = "Invalid team preference. Use 'auto', 'blue', or 'red'.";
        return false;
    }

    public static bool TryReadSpectatorMode(HttpContext context, out bool isSpectator, out string? error)
    {
        var rawSpectator = context.Request.Query["spectator"].ToString();
        if (string.IsNullOrWhiteSpace(rawSpectator))
        {
            isSpectator = false;
            error = null;
            return true;
        }

        var normalizedSpectator = rawSpectator.Trim().ToLowerInvariant();
        if (normalizedSpectator is "true" or "1" or "yes")
        {
            isSpectator = true;
            error = null;
            return true;
        }

        if (normalizedSpectator is "false" or "0" or "no")
        {
            isSpectator = false;
            error = null;
            return true;
        }

        isSpectator = false;
        error = "Invalid spectator mode. Use 'true' or 'false'.";
        return false;
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
            if (_runtimeOptions.TrainingMode && _runtimeOptions.MaxMessagesPerRateLimitWindow <= 0)
            {
                return true;
            }

            var messageLimit = _runtimeOptions.MaxMessagesPerRateLimitWindow > 0
                ? _runtimeOptions.MaxMessagesPerRateLimitWindow
                : MaxMessagesPerRateLimitWindow;
            return client.MessagesInCurrentWindow <= messageLimit;
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
                var dt = _runtimeOptions.RunAsFastAsPossible
                    ? 1f / TickRate
                    : (float)(now - last).TotalSeconds;
                if (dt <= 0f)
                {
                    dt = tickMs / 1000f;
                }
                if (!_runtimeOptions.RunAsFastAsPossible && dt > 0.1f)
                {
                    dt = 0.1f;
                }
                dt *= MathF.Max(0.001f, _runtimeOptions.TimeScale);
                last = now;

                string payload;
                List<(ConnectedClient Client, long? PendingPongNonce)> clients;

                lock (_sync)
                {
                    SimulateWithSubsteps(dt);
                    payload = BuildStatePayload();
                    if (_matchFinished)
                    {
                        _finishedStateBroadcasted = true;
                    }
                    _frameEvents.Clear();
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

                    if (!client.IsSpectator && !_runtimeOptions.DisableClientIdleTimeout && now - client.LastReceivedAtUtc > MaxClientIdleTime)
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
                if (!_runtimeOptions.RunAsFastAsPossible)
                {
                    await Task.Delay(tickMs, _cts.Token);
                }
                else
                {
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Game loop stopped for room {RoomId}.", RoomId);
    }

    private void SimulateWithSubsteps(float totalDt)
    {
        if (totalDt <= 0f)
        {
            return;
        }

        var maxStep = MathF.Max(0.001f, _runtimeOptions.MaxSimulationStepSeconds);
        var maxSubsteps = Math.Max(1, _runtimeOptions.MaxSimulationSubstepsPerTick);
        var clampedDt = MathF.Min(totalDt, maxStep * maxSubsteps);
        var steps = Math.Max(1, (int)MathF.Ceiling(clampedDt / maxStep));
        var stepDt = clampedDt / steps;

        for (var i = 0; i < steps; i++)
        {
            Simulate(stepDt);
        }
    }

    private void Simulate(float dt)
    {
        _stateSequence++;
        var now = DateTimeOffset.UtcNow;
        UpdateMatchClock(now);

        if (_matchFinished)
        {
            if (ShouldAutoResetFinishedMatch(now))
            {
                ResetMatch();
            }

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
        }

        foreach (var player in _players.Values)
        {
            if (IsTrainingTelemetryEnabled)
            {
                var stats = GetOrCreateStats(player);
                stats.Name = player.Name;
                stats.Team = player.Team;
                if (player.CarryingFlagTeam is not null)
                {
                    stats.CarrySeconds += dt;
                }
            }

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

        var previousPosition = player.Position;
        player.Position = MoveAgainstWorld(player.Position, delta, player.Radius);
        var movedDistance = MathF.Sqrt(Geometry.DistanceSquared(previousPosition, player.Position));
        if (movedDistance > 0.001f && IsTrainingTelemetryEnabled)
        {
            GetOrCreateStats(player).DistanceTravelled += movedDistance;
        }
    }

    private void ResetMatch()
    {
        _blueScore = 0;
        _redScore = 0;
        _shotTraces.Clear();
        _hitEffects.Clear();
        _frameEvents.Clear();
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
            GetOrCreateStats(player).ResetForMatch(player.Name, player.Team);
        }

        EmitGameEvent("matchReset", blueScore: _blueScore, redScore: _redScore);
    }

    private void StartNewMatchClock(DateTimeOffset now)
    {
        _matchId = CreateMatchId();
        _eventSequence = 0;
        _stateSequence = 0;
        _matchStartedAtUtc = now;
        _matchEndsAtUtc = now.Add(CurrentMatchDuration);
        _matchFinished = false;
        _matchFinishedAtUtc = null;
        _finishedStateBroadcasted = false;
        _winnerTeam = null;
        _loserTeam = null;
    }

    private void ReassignPlayerTeams()
    {
        if (_players.Count == 0)
        {
            return;
        }

        var blueCount = 0;
        var redCount = 0;
        var autoPlayers = new List<PlayerRuntime>();

        foreach (var player in _players.Values)
        {
            if (player.RequestedTeam == "blue")
            {
                player.Team = "blue";
                blueCount++;
            }
            else if (player.RequestedTeam == "red")
            {
                player.Team = "red";
                redCount++;
            }
            else
            {
                autoPlayers.Add(player);
            }
        }

        foreach (var player in autoPlayers.OrderBy(_ => _random.Next()))
        {
            if (blueCount <= redCount)
            {
                player.Team = "blue";
                blueCount++;
            }
            else
            {
                player.Team = "red";
                redCount++;
            }
        }
    }

    private void UpdateMatchClock(DateTimeOffset now)
    {
        if (IsMatchClockDisabled)
        {
            return;
        }

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
        _matchFinishedAtUtc = DateTimeOffset.UtcNow;
        _finishedStateBroadcasted = false;

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

        EmitGameEvent("matchFinished", winnerTeam: _winnerTeam, loserTeam: _loserTeam, blueScore: _blueScore, redScore: _redScore);

        _logger.LogInformation(
            "Match finished in room {RoomId}. Blue {BlueScore} - Red {RedScore}. Winner: {WinnerTeam}",
            RoomId,
            _blueScore,
            _redScore,
            _winnerTeam);
    }


    private bool ShouldAutoResetFinishedMatch(DateTimeOffset now)
    {
        if (!_runtimeOptions.AutoResetFinishedMatches || !_matchFinished || !_finishedStateBroadcasted)
        {
            return false;
        }

        if (_matchFinishedAtUtc is not { } finishedAt)
        {
            return false;
        }

        return now - finishedAt >= CurrentResetCooldown;
    }

    private long GetMatchRemainingMilliseconds(DateTimeOffset now)
    {
        if (IsMatchClockDisabled)
        {
            return -1;
        }

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

        GetOrCreateStats(shooter).ShotsFired++;
        EmitGameEvent("shotFired", player: shooter, team: shooter.Team, x: start.X, y: start.Y);

        shooter.ShootCooldownRemaining = ShotCooldownSeconds;

        if (hit is not null)
        {
            GetOrCreateStats(shooter).HitsDealt++;
            GetOrCreateStats(shooter).Eliminations++;
            GetOrCreateStats(hit.Player).HitsTaken++;
            GetOrCreateStats(hit.Player).Deaths++;
            RegisterHitEffect(shooter, hit.Player, end);
            EliminatePlayer(hit.Player, shooter);
        }
    }

    private void RegisterHitEffect(PlayerRuntime shooter, PlayerRuntime victim, Vec2 impactPosition)
    {
        var id = $"hit-{Guid.NewGuid():N}";
        _hitEffects.Add(new HitEffectRuntime
        {
            Id = id,
            ShooterPlayerId = shooter.Id,
            VictimPlayerId = victim.Id,
            ShooterTeam = shooter.Team,
            VictimTeam = victim.Team,
            ImpactPosition = impactPosition,
            RemainingLifetime = HitEffectLifetimeSeconds
        });

        EmitGameEvent(
            "playerHit",
            id: id,
            shooter: shooter,
            victim: victim,
            x: impactPosition.X,
            y: impactPosition.Y,
            impactX: impactPosition.X,
            impactY: impactPosition.Y,
            life: HitEffectLifetimeSeconds);
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

    private void EliminatePlayer(PlayerRuntime player, PlayerRuntime? eliminatedBy = null)
    {
        if (player.CarryingFlagTeam is not null && Map.FlagsByTeam.TryGetValue(player.CarryingFlagTeam, out var carriedFlag))
        {
            var droppedFlagTeam = player.CarryingFlagTeam;
            carriedFlag.CarriedByPlayerId = null;
            carriedFlag.Position = player.Position;
            player.CarryingFlagTeam = null;
            GetOrCreateStats(player).FlagDrops++;
            EmitGameEvent("flagDropped", player: player, team: player.Team, flagTeam: droppedFlagTeam, x: carriedFlag.Position.X, y: carriedFlag.Position.Y);
        }

        EmitGameEvent("playerEliminated", player: player, victim: player, shooter: eliminatedBy, team: player.Team, x: player.Position.X, y: player.Position.Y);

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
        if (Geometry.Length(delta) <= MovementEpsilon)
        {
            return start;
        }

        var collisionRadius = GetMovementCollisionRadius(radius);
        var full = start + delta;
        if (!CollidesWithWorld(full, collisionRadius))
        {
            return full;
        }

        var surfaceSlide = TrySurfaceSlide(start, delta, collisionRadius);
        if (GetMovementProgress(start, delta, surfaceSlide) > MovementEpsilon)
        {
            return surfaceSlide;
        }

        var axisSlide = TryAxisAlignedSlide(start, delta, collisionRadius);
        if (Geometry.DistanceSquared(axisSlide, start) > MovementEpsilon * MovementEpsilon)
        {
            return axisSlide;
        }

        return TryPartialAxisSlide(start, delta, collisionRadius);
    }

    private static float GetMovementCollisionRadius(float radius) =>
        MathF.Max(0f, radius - MovementCollisionContactTolerance);

    private Vec2 TrySurfaceSlide(Vec2 start, Vec2 delta, float collisionRadius)
    {
        var current = start;
        var remaining = delta;
        var best = start;

        for (var pass = 0; pass < MovementSlidePasses; pass++)
        {
            if (Geometry.Length(remaining) <= MovementEpsilon)
            {
                break;
            }

            var target = current + remaining;
            if (!CollidesWithWorld(target, collisionRadius))
            {
                return ChooseBestMovementCandidate(start, delta, best, target);
            }

            var lastFree = MoveToLastFreePoint(current, remaining, collisionRadius, out var travelledFraction);
            if (Geometry.DistanceSquared(lastFree, current) > MovementEpsilon * MovementEpsilon)
            {
                current = lastFree;
                best = ChooseBestMovementCandidate(start, delta, best, current);
            }

            var residual = remaining * Math.Clamp(1f - travelledFraction, 0f, 1f);
            if (Geometry.Length(residual) <= MovementEpsilon)
            {
                break;
            }

            if (!TryGetSlideNormal(current, target, residual, collisionRadius, out var normal))
            {
                break;
            }

            normal = Geometry.Normalize(normal);
            if (Geometry.Length(normal) <= MovementEpsilon)
            {
                break;
            }

            var nudgedCurrent = OffsetFromCollisionSurface(current, normal, collisionRadius);
            if (Geometry.DistanceSquared(nudgedCurrent, current) > MovementEpsilon * MovementEpsilon)
            {
                current = nudgedCurrent;
                best = ChooseBestMovementCandidate(start, delta, best, current);
            }

            var slide = ProjectMovementOntoSurface(residual, normal);
            if (Geometry.Length(slide) <= MovementEpsilon)
            {
                slide = BuildTangentMovement(residual, normal);
            }

            if (Geometry.Length(slide) <= MovementEpsilon)
            {
                break;
            }

            var next = FindBestSlideCandidate(current, slide, residual, normal, collisionRadius);
            var applied = next - current;
            if (Geometry.Length(applied) <= MovementEpsilon)
            {
                break;
            }

            current = next;
            best = ChooseBestMovementCandidate(start, delta, best, current);
            remaining = slide - applied;
        }

        return best;
    }

    private bool TryGetSlideNormal(Vec2 current, Vec2 target, Vec2 residual, float collisionRadius, out Vec2 normal)
    {
        var probeRadius = collisionRadius + MovementSlideProbeMargin;
        if (TryGetWorldCollisionNormal(target, probeRadius, out normal))
        {
            return true;
        }

        var direction = Geometry.Normalize(residual);
        if (Geometry.Length(direction) > MovementEpsilon &&
            TryGetWorldCollisionNormal(current + direction * MovementSlideProbeMargin, probeRadius, out normal))
        {
            return true;
        }

        return TryGetWorldCollisionNormal(current, probeRadius, out normal);
    }

    private Vec2 OffsetFromCollisionSurface(Vec2 position, Vec2 normal, float collisionRadius)
    {
        var direction = Geometry.Normalize(normal);
        if (Geometry.Length(direction) <= MovementEpsilon)
        {
            return position;
        }

        var best = position;
        for (var i = 1; i <= 4; i++)
        {
            var candidate = position + direction * (MovementSlideSkin * i);
            if (!CollidesWithWorld(candidate, collisionRadius))
            {
                best = candidate;
                break;
            }
        }

        return best;
    }

    private static Vec2 ProjectMovementOntoSurface(Vec2 movement, Vec2 normal)
    {
        var intoSurface = Geometry.Dot(movement, normal);
        return intoSurface < 0f ? movement - normal * intoSurface : movement;
    }

    private static Vec2 BuildTangentMovement(Vec2 movement, Vec2 normal)
    {
        var tangent = new Vec2(-normal.Y, normal.X);
        var tangentAmount = Geometry.Dot(movement, tangent);
        return tangent * tangentAmount;
    }

    private Vec2 FindBestSlideCandidate(Vec2 start, Vec2 slide, Vec2 desiredDelta, Vec2 normal, float collisionRadius)
    {
        var best = start;
        var origins = new[]
        {
            start,
            OffsetFromCollisionSurface(start, normal, collisionRadius),
            start + normal * MovementSlideSkin
        };
        var fractions = new[] { 1f, 0.85f, 0.7f, 0.5f, 0.35f, 0.2f, 0.1f };

        foreach (var origin in origins)
        {
            if (CollidesWithWorld(origin, collisionRadius))
            {
                continue;
            }

            foreach (var fraction in fractions)
            {
                var candidateDelta = slide * fraction;
                var candidate = origin + candidateDelta;
                if (!CollidesWithWorld(candidate, collisionRadius))
                {
                    best = ChooseBestMovementCandidate(start, desiredDelta, best, candidate);
                    if (fraction >= 1f)
                    {
                        return best;
                    }

                    continue;
                }

                var partial = MoveToLastFreePoint(origin, candidateDelta, collisionRadius, out _);
                if (Geometry.DistanceSquared(partial, origin) > MovementEpsilon * MovementEpsilon)
                {
                    best = ChooseBestMovementCandidate(start, desiredDelta, best, partial);
                }
            }
        }

        var axisCandidate = TryAxisAlignedSlide(start, desiredDelta, collisionRadius);
        best = ChooseBestMovementCandidate(start, desiredDelta, best, axisCandidate);
        if (GetMovementProgress(start, desiredDelta, best) > MovementEpsilon)
        {
            return best;
        }

        return TryTangentAlternatives(start, desiredDelta, normal, collisionRadius);
    }

    private Vec2 TryTangentAlternatives(Vec2 start, Vec2 desiredDelta, Vec2 normal, float collisionRadius)
    {
        var tangent = new Vec2(-normal.Y, normal.X);
        var amount = Geometry.Dot(desiredDelta, tangent);
        if (MathF.Abs(amount) <= MovementEpsilon)
        {
            return start;
        }

        var best = start;
        var tangentDelta = tangent * amount;
        var fractions = new[] { 1f, 0.75f, 0.5f, 0.25f };
        foreach (var fraction in fractions)
        {
            var candidate = start + tangentDelta * fraction;
            if (!CollidesWithWorld(candidate, collisionRadius))
            {
                best = ChooseBestMovementCandidate(start, desiredDelta, best, candidate);
                break;
            }
        }

        return best;
    }

    private Vec2 TryAxisAlignedSlide(Vec2 start, Vec2 delta, float collisionRadius)
    {
        var xFirst = TryAxisAlignedSlideSequence(start, delta, collisionRadius, moveXFirst: true);
        var yFirst = TryAxisAlignedSlideSequence(start, delta, collisionRadius, moveXFirst: false);
        return ChooseBestMovementCandidate(start, delta, xFirst, yFirst);
    }

    private Vec2 TryAxisAlignedSlideSequence(Vec2 start, Vec2 delta, float collisionRadius, bool moveXFirst)
    {
        var current = start;
        if (moveXFirst)
        {
            current = TryMoveSingleAxis(current, delta.X, true, collisionRadius);
            current = TryMoveSingleAxis(current, delta.Y, false, collisionRadius);
        }
        else
        {
            current = TryMoveSingleAxis(current, delta.Y, false, collisionRadius);
            current = TryMoveSingleAxis(current, delta.X, true, collisionRadius);
        }

        return current;
    }

    private Vec2 TryMoveSingleAxis(Vec2 start, float amount, bool isXAxis, float collisionRadius)
    {
        if (MathF.Abs(amount) <= MovementEpsilon)
        {
            return start;
        }

        var candidate = isXAxis
            ? new Vec2(start.X + amount, start.Y)
            : new Vec2(start.X, start.Y + amount);

        return CollidesWithWorld(candidate, collisionRadius) ? start : candidate;
    }

    private static Vec2 ChooseBestMovementCandidate(Vec2 start, Vec2 desiredDelta, Vec2 a, Vec2 b)
    {
        var scoreA = GetMovementProgress(start, desiredDelta, a);
        var scoreB = GetMovementProgress(start, desiredDelta, b);
        if (MathF.Abs(scoreA - scoreB) > MovementEpsilon)
        {
            return scoreA > scoreB ? a : b;
        }

        return Geometry.DistanceSquared(a, start) >= Geometry.DistanceSquared(b, start) ? a : b;
    }

    private static float GetMovementProgress(Vec2 start, Vec2 desiredDelta, Vec2 candidate)
    {
        var desiredDirection = Geometry.Normalize(desiredDelta);
        if (Geometry.Length(desiredDirection) <= MovementEpsilon)
        {
            return 0f;
        }

        return Geometry.Dot(candidate - start, desiredDirection);
    }

    private Vec2 TryPartialAxisSlide(Vec2 start, Vec2 delta, float collisionRadius)
    {
        var fractions = new[] { 0.75f, 0.5f, 0.35f, 0.2f, 0.1f };
        var best = start;
        foreach (var fraction in fractions)
        {
            var partial = delta * fraction;
            var candidate = start + partial;
            if (!CollidesWithWorld(candidate, collisionRadius))
            {
                return candidate;
            }

            var axisCandidate = TryAxisAlignedSlide(start, partial, collisionRadius);
            best = ChooseBestMovementCandidate(start, delta, best, axisCandidate);
            if (Geometry.DistanceSquared(best, start) > MovementEpsilon * MovementEpsilon)
            {
                return best;
            }
        }

        return best;
    }

    private Vec2 MoveToLastFreePoint(Vec2 start, Vec2 delta, float collisionRadius, out float travelledFraction)
    {
        travelledFraction = 0f;
        var low = 0f;
        var high = 1f;
        var best = start;

        for (var i = 0; i < MovementSweepIterations; i++)
        {
            var mid = (low + high) * 0.5f;
            var candidate = start + delta * mid;
            if (CollidesWithWorld(candidate, collisionRadius))
            {
                high = mid;
            }
            else
            {
                low = mid;
                best = candidate;
            }
        }

        travelledFraction = low;
        return best;
    }

    private bool TryGetWorldCollisionNormal(Vec2 position, float radius, out Vec2 normal)
    {
        normal = new Vec2(0f, 0f);
        var bestPenetration = float.NegativeInfinity;
        var found = false;

        if (TryGetPerimeterCollisionNormal(position, radius, out var perimeterNormal, out var perimeterPenetration))
        {
            ConsiderCollisionNormal(perimeterNormal, perimeterPenetration, ref normal, ref bestPenetration, ref found);
        }

        foreach (var obstacle in Map.Obstacles)
        {
            if (!obstacle.Hard)
            {
                continue;
            }

            if (obstacle.Type == "rect" && Geometry.CircleIntersectsRect(position, radius, obstacle) &&
                TryGetRectCollisionNormal(position, radius, obstacle, out var rectNormal, out var rectPenetration))
            {
                ConsiderCollisionNormal(rectNormal, rectPenetration, ref normal, ref bestPenetration, ref found);
            }
            else if (obstacle.Type == "circle" && Geometry.CircleIntersectsCircle(position, radius, obstacle) &&
                TryGetCircleCollisionNormal(position, radius, obstacle, out var circleNormal, out var circlePenetration))
            {
                ConsiderCollisionNormal(circleNormal, circlePenetration, ref normal, ref bestPenetration, ref found);
            }
            else if (obstacle.Type == "polygon" && obstacle.Points is not null &&
                Geometry.CircleIntersectsPolygon(position, radius, obstacle.Points) &&
                TryGetPolygonCollisionNormal(position, radius, obstacle.Points, out var polygonNormal, out var polygonPenetration))
            {
                ConsiderCollisionNormal(polygonNormal, polygonPenetration, ref normal, ref bestPenetration, ref found);
            }
        }

        return found;
    }

    private bool TryGetPerimeterCollisionNormal(Vec2 position, float radius, out Vec2 normal, out float penetration)
    {
        normal = new Vec2(0f, 0f);
        penetration = 0f;

        var inside = Geometry.PointInPolygon(position, Map.Perimeter.Points);
        var nearest = FindNearestPointOnPolygon(position, Map.Perimeter.Points, out var distance);
        if (inside && distance >= radius)
        {
            return false;
        }

        var rawNormal = inside ? position - nearest : nearest - position;
        normal = NormalizeOrFallback(rawNormal, new Vec2(0f, inside ? 1f : -1f));
        penetration = inside ? radius - distance : radius + distance;
        return true;
    }

    private static bool TryGetRectCollisionNormal(Vec2 position, float radius, ObstacleShape rect, out Vec2 normal, out float penetration)
    {
        normal = new Vec2(0f, 0f);
        penetration = 0f;

        var minX = rect.Position.X;
        var maxX = rect.Position.X + rect.Width;
        var minY = rect.Position.Y;
        var maxY = rect.Position.Y + rect.Height;
        var nearest = new Vec2(Math.Clamp(position.X, minX, maxX), Math.Clamp(position.Y, minY, maxY));
        var offset = position - nearest;
        var distance = Geometry.Length(offset);
        if (distance > MovementEpsilon)
        {
            normal = offset * (1f / distance);
            penetration = radius - distance;
            return true;
        }

        var left = MathF.Abs(position.X - minX);
        var right = MathF.Abs(maxX - position.X);
        var top = MathF.Abs(position.Y - minY);
        var bottom = MathF.Abs(maxY - position.Y);
        var nearestSide = MathF.Min(MathF.Min(left, right), MathF.Min(top, bottom));

        if (nearestSide == left)
        {
            normal = new Vec2(-1f, 0f);
            penetration = radius + left;
        }
        else if (nearestSide == right)
        {
            normal = new Vec2(1f, 0f);
            penetration = radius + right;
        }
        else if (nearestSide == top)
        {
            normal = new Vec2(0f, -1f);
            penetration = radius + top;
        }
        else
        {
            normal = new Vec2(0f, 1f);
            penetration = radius + bottom;
        }

        return true;
    }

    private static bool TryGetCircleCollisionNormal(Vec2 position, float radius, ObstacleShape circle, out Vec2 normal, out float penetration)
    {
        var offset = position - circle.Position;
        var distance = Geometry.Length(offset);
        normal = NormalizeOrFallback(offset, new Vec2(1f, 0f));
        penetration = radius + circle.Radius - distance;
        return true;
    }

    private static bool TryGetPolygonCollisionNormal(Vec2 position, float radius, List<Vec2> points, out Vec2 normal, out float penetration)
    {
        normal = new Vec2(0f, 0f);
        penetration = 0f;
        if (points.Count == 0)
        {
            return false;
        }

        var inside = Geometry.PointInPolygon(position, points);
        var nearest = FindNearestPointOnPolygon(position, points, out var distance);
        var outward = position - nearest;
        normal = inside
            ? NormalizeOrFallback(new Vec2(-outward.X, -outward.Y), position - GetPolygonCentroid(points))
            : NormalizeOrFallback(outward, position - GetPolygonCentroid(points));
        penetration = inside ? radius + distance : radius - distance;
        return true;
    }

    private static Vec2 FindNearestPointOnPolygon(Vec2 point, List<Vec2> points, out float distance)
    {
        var nearest = points[0];
        distance = float.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var candidate = ClosestPointOnSegment(point, a, b);
            var candidateDistance = Geometry.Length(point - candidate);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private static Vec2 ClosestPointOnSegment(Vec2 point, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var abLengthSq = Geometry.Dot(ab, ab);
        if (abLengthSq <= MovementEpsilon)
        {
            return a;
        }

        var t = Math.Clamp(Geometry.Dot(point - a, ab) / abLengthSq, 0f, 1f);
        return a + ab * t;
    }

    private static Vec2 GetPolygonCentroid(List<Vec2> points)
    {
        var x = 0f;
        var y = 0f;
        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return new Vec2(x / points.Count, y / points.Count);
    }

    private static Vec2 NormalizeOrFallback(Vec2 vector, Vec2 fallback)
    {
        var normalized = Geometry.Normalize(vector);
        if (Geometry.Length(normalized) > MovementEpsilon)
        {
            return normalized;
        }

        normalized = Geometry.Normalize(fallback);
        return Geometry.Length(normalized) > MovementEpsilon ? normalized : new Vec2(1f, 0f);
    }

    private static void ConsiderCollisionNormal(Vec2 candidate, float penetration, ref Vec2 normal, ref float bestPenetration, ref bool found)
    {
        if (Geometry.Length(candidate) <= MovementEpsilon || penetration < bestPenetration)
        {
            return;
        }

        normal = candidate;
        bestPenetration = penetration;
        found = true;
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
            var returnedAt = ownFlag.Position;
            ownFlag.ResetToBase();
            GetOrCreateStats(player).FlagReturns++;
            EmitGameEvent("flagReturned", player: player, team: player.Team, flagTeam: ownFlag.Team, x: returnedAt.X, y: returnedAt.Y);
        }

        if (player.CarryingFlagTeam is null)
        {
            if (enemyFlag.CarriedByPlayerId is null && Geometry.DistanceSquared(player.Position, enemyFlag.Position) <= 28f * 28f)
            {
                player.CarryingFlagTeam = enemyFlag.Team;
                enemyFlag.CarriedByPlayerId = player.Id;
                enemyFlag.Position = player.Position;
                GetOrCreateStats(player).FlagPickups++;
                EmitGameEvent("flagPickedUp", player: player, team: player.Team, flagTeam: enemyFlag.Team, x: player.Position.X, y: player.Position.Y);
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

                GetOrCreateStats(player).FlagCaptures++;
                EmitGameEvent("flagCaptured", player: player, team: player.Team, flagTeam: enemyFlag.Team, x: player.Position.X, y: player.Position.Y, blueScore: _blueScore, redScore: _redScore);

                enemyFlag.ResetToBase();
                player.CarryingFlagTeam = null;
            }
        }
    }


    private PlayerStatsRuntime GetOrCreateStats(PlayerRuntime player)
    {
        if (_playerStats.TryGetValue(player.Id, out var stats))
        {
            return stats;
        }

        stats = new PlayerStatsRuntime
        {
            PlayerId = player.Id,
            Name = player.Name,
            Team = player.Team
        };
        _playerStats[player.Id] = stats;
        return stats;
    }

    private void EmitGameEvent(
        string type,
        string? id = null,
        PlayerRuntime? player = null,
        PlayerRuntime? shooter = null,
        PlayerRuntime? victim = null,
        string? team = null,
        string? flagTeam = null,
        string? winnerTeam = null,
        string? loserTeam = null,
        float? x = null,
        float? y = null,
        float? impactX = null,
        float? impactY = null,
        float? life = null,
        int? blueScore = null,
        int? redScore = null)
    {
        if (!IsTrainingTelemetryEnabled)
        {
            return;
        }

        var primaryPlayer = player ?? shooter ?? victim;
        _frameEvents.Add(new GameEventRuntime
        {
            Id = id ?? $"evt-{Guid.NewGuid():N}",
            Sequence = ++_eventSequence,
            MatchId = _matchId,
            Type = type,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PlayerId = primaryPlayer?.Id,
            PlayerName = primaryPlayer?.Name,
            Team = team ?? primaryPlayer?.Team,
            ShooterPlayerId = shooter?.Id,
            ShooterTeam = shooter?.Team,
            VictimPlayerId = victim?.Id,
            VictimTeam = victim?.Team,
            FlagTeam = flagTeam,
            WinnerTeam = winnerTeam,
            LoserTeam = loserTeam,
            X = x,
            Y = y,
            ImpactX = impactX,
            ImpactY = impactY,
            Life = life,
            BlueScore = blueScore,
            RedScore = redScore
        });
    }

    private static string CreateMatchId()
    {
        return $"m-{Guid.NewGuid():N}";
    }

    private object[] BuildEventsPayload()
    {
        var events = new List<object>();

        if (IsTrainingTelemetryEnabled)
        {
            var activeHitEffectIds = _hitEffects
                .Select(effect => effect.Id)
                .ToHashSet(StringComparer.Ordinal);

            events.AddRange(_frameEvents
                .Where(gameEvent => gameEvent.Type != "playerHit" || !activeHitEffectIds.Contains(gameEvent.Id))
                .Select(gameEvent => (object)gameEvent));
        }

        events.AddRange(_hitEffects.Select(effect => (object)new
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
        }));

        return events.ToArray();
    }

    private object[] BuildPlayerStatsPayload()
    {
        if (!IsTrainingTelemetryEnabled)
        {
            return Array.Empty<object>();
        }

        return _playerStats.Values
            .OrderBy(stats => stats.Team, StringComparer.Ordinal)
            .ThenBy(stats => stats.Name, StringComparer.Ordinal)
            .Select(stats => (object)new
            {
                playerId = stats.PlayerId,
                name = stats.Name,
                team = stats.Team,
                shotsFired = stats.ShotsFired,
                hitsDealt = stats.HitsDealt,
                hitsTaken = stats.HitsTaken,
                eliminations = stats.Eliminations,
                deaths = stats.Deaths,
                flagPickups = stats.FlagPickups,
                flagDrops = stats.FlagDrops,
                flagReturns = stats.FlagReturns,
                flagCaptures = stats.FlagCaptures,
                carrySeconds = MathF.Round(stats.CarrySeconds * 100f) / 100f,
                distanceTravelled = MathF.Round(stats.DistanceTravelled * 10f) / 10f
            })
            .ToArray();
    }

    private string BuildStatePayload()
    {
        var now = DateTimeOffset.UtcNow;
        var dto = new
        {
            type = "state",
            roomId = RoomId,
            sequence = _stateSequence,
            matchId = _matchId,
            serverTime = now.ToUnixTimeMilliseconds(),
            scores = new { blue = _blueScore, red = _redScore },
            match = new
            {
                id = _matchId,
                status = _matchFinished ? "finished" : "running",
                durationSeconds = IsMatchClockDisabled ? 0 : (int)CurrentMatchDuration.TotalSeconds,
                startedAt = _matchStartedAtUtc.ToUnixTimeMilliseconds(),
                endsAt = IsMatchClockDisabled ? (long?)null : _matchEndsAtUtc.ToUnixTimeMilliseconds(),
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
            events = BuildEventsPayload(),
            playerStats = BuildPlayerStatsPayload(),
            training = new
            {
                enabled = _runtimeOptions.TrainingMode,
                timeScale = _runtimeOptions.TimeScale,
                runAsFastAsPossible = _runtimeOptions.RunAsFastAsPossible,
                maxSimulationStepSeconds = _runtimeOptions.MaxSimulationStepSeconds,
                maxSimulationSubstepsPerTick = _runtimeOptions.MaxSimulationSubstepsPerTick
            }
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
        catch (OperationCanceledException) when (IsExpectedWebSocketStop(client, client.Socket))
        {
            _logger.LogInformation("WebSocket writer stopped for {PlayerId}.", client.PlayerId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "WebSocket send timed out for {PlayerId}.", client.PlayerId);
        }
        catch (WebSocketException ex) when (IsExpectedWebSocketClose(ex, client, client.Socket))
        {
            _logger.LogInformation("WebSocket writer ended because the socket was closed for {PlayerId}.", client.PlayerId);
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

    private bool IsExpectedWebSocketStop(ConnectedClient client, WebSocket socket, HttpContext? context = null)
    {
        return client.IsStopRequested
            || _cts.IsCancellationRequested
            || context?.RequestAborted.IsCancellationRequested == true
            || IsTerminalWebSocketState(socket.State);
    }

    private bool IsExpectedWebSocketClose(WebSocketException ex, ConnectedClient client, WebSocket socket, HttpContext? context = null)
    {
        return ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely
            || IsExpectedWebSocketStop(client, socket, context);
    }

    private static bool IsTerminalWebSocketState(WebSocketState state)
    {
        return state is WebSocketState.Aborted or WebSocketState.Closed or WebSocketState.CloseReceived;
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
        bool roomIsEmpty;

        lock (_sync)
        {
            _clients.Remove(playerId, out client);
            if (_players.Remove(playerId, out var player))
            {
                removedPlayer = player;
                if (player.CarryingFlagTeam is not null && Map.FlagsByTeam.TryGetValue(player.CarryingFlagTeam, out var flag))
                {
                    var droppedFlagTeam = player.CarryingFlagTeam;
                    flag.ResetToBase();
                    if (_playerStats.TryGetValue(player.Id, out var stats))
                    {
                        stats.FlagDrops++;
                    }

                    EmitGameEvent("flagDropped", player: player, team: player.Team, flagTeam: droppedFlagTeam, x: player.Position.X, y: player.Position.Y);
                }

                EmitGameEvent("playerLeft", player: player, team: player.Team, x: player.Position.X, y: player.Position.Y);
                _playerStats.Remove(playerId);
            }

            roomIsEmpty = _players.Count == 0 && _clients.Count == 0;
        }

        if (client is not null)
        {
            try
            {
                client.Stop(abortSocket);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stop WebSocket client resources for {PlayerId} in room {RoomId}.", playerId, RoomId);
            }
        }

        if (client is not null || removedPlayer is not null)
        {
            _logger.LogInformation("Client removed {PlayerId} from room {RoomId}", playerId, RoomId);
        }

        if (roomIsEmpty && (client is not null || removedPlayer is not null))
        {
            _roomEmptyCallback?.Invoke(RoomId);
        }
    }

    private string ChooseTeam(string requestedTeam)
    {
        if (requestedTeam is "blue" or "red")
        {
            return requestedTeam;
        }

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
        var verticalHalfHeight = MathF.Min(MathF.Max(Map.Height * PreferredSpawnScatterVerticalRatio, radius * 5f), 110f);

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
                maxY = MathF.Min(Map.Height - edgeInset, MathF.Max(edgeInset, Map.Height * PreferredSpawnEdgeBandRatio));
            }
            else
            {
                minY = MathF.Max(edgeInset, MathF.Min(Map.Height - edgeInset, Map.Height * (1f - PreferredSpawnEdgeBandRatio)));
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
        var maxInside = MathF.Max(edgeInset, Map.Height - edgeInset);
        var x = Math.Clamp(Map.Width * 0.5f, edgeInset, MathF.Max(edgeInset, Map.Width - edgeInset));
        var topY = Math.Clamp(Map.Height * SpawnAnchorEdgeRatio, edgeInset, maxInside);
        var bottomY = Math.Clamp(Map.Height * (1f - SpawnAnchorEdgeRatio), edgeInset, maxInside);
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
            return candidate.Y >= edgeInset && candidate.Y <= MathF.Max(edgeInset, Map.Height * PreferredSpawnEdgeBandRatio);
        }

        return candidate.Y >= MathF.Min(Map.Height - edgeInset, Map.Height * (1f - PreferredSpawnEdgeBandRatio)) && candidate.Y <= Map.Height - edgeInset;
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

    public static GameMap LoadMapFromFile(string mapPath)
    {
        var rawJson = File.ReadAllText(mapPath);
        return LoadMapFromJson(rawJson);
    }

    public static GameMap LoadMapFromJson(string rawJson)
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
