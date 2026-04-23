using System.Collections.Concurrent;
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

    private Task? _loopTask;
    private int _blueScore;
    private int _redScore;

    public GameHost(string mapPath, ILogger logger)
    {
        _logger = logger;
        _mapPath = mapPath;
        Map = LoadMapFromFile(mapPath);
        RawMapJson = Map.RawJson;
    }

    public int TickRate => 20;
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
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
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    public async Task HandleClientAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        string playerId;
        string team;
        string name;
        string mapName;
        Vec2 spawn;

        lock (_sync)
        {
            playerId = $"p-{Guid.NewGuid():N}";
            team = ChooseTeam();
            name = team == "blue" ? $"Blue-{_players.Count + 1}" : $"Red-{_players.Count + 1}";
            spawn = FindSpawn(team);
            _players[playerId] = new PlayerRuntime
            {
                Id = playerId,
                Name = name,
                Team = team,
                Position = spawn,
                SpawnPosition = spawn,
                Facing = team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f)
            };
            _clients[playerId] = new ConnectedClient
            {
                PlayerId = playerId,
                Socket = socket
            };
            mapName = Map.Source.Meta.Name;
        }

        _logger.LogInformation("Client connected {PlayerId} ({Team})", playerId, team);

        await SendJsonAsync(socket, new
        {
            type = "welcome",
            playerId,
            team,
            tickRate = TickRate,
            mapName
        }, context.RequestAborted);

        await SendStateToAsync(playerId, context.RequestAborted);

        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, context.RequestAborted);
                if (message is null)
                {
                    break;
                }

                HandleIncomingMessage(playerId, message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            RemoveClient(playerId);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch
                {
                }
            }
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
                    player.Input = new InputState
                    {
                        Up = ReadBool(doc.RootElement, "up"),
                        Down = ReadBool(doc.RootElement, "down"),
                        Left = ReadBool(doc.RootElement, "left"),
                        Right = ReadBool(doc.RootElement, "right")
                    };
                }
                else if (type == "shoot")
                {
                    player.PendingShoot = true;
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
                    ResetMatch();
                    _logger.LogInformation("Match reset requested by {PlayerId}", playerId);
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;
    }

    private async Task GameLoopAsync()
    {
        var tickMs = 1000 / TickRate;
        var last = DateTime.UtcNow;

        while (!_cts.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
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
            List<(string PlayerId, WebSocket Socket, long? PendingPongNonce)> sockets;

            lock (_sync)
            {
                Simulate(dt);
                payload = BuildStatePayload();
                sockets = _clients.Values
                    .Where(c => c.Socket.State == WebSocketState.Open)
                    .Select(c =>
                    {
                        var pendingPongNonce = c.PendingPongNonce;
                        c.PendingPongNonce = null;
                        return (c.PlayerId, c.Socket, pendingPongNonce);
                    })
                    .ToList();
            }

            var deadClients = new ConcurrentBag<string>();
            await Task.WhenAll(sockets.Select(async item =>
            {
                try
                {
                    if (item.PendingPongNonce is not null)
                    {
                        await SendJsonAsync(item.Socket, new
                        {
                            type = "pong",
                            nonce = item.PendingPongNonce.Value
                        }, _cts.Token);
                    }

                    await SendRawJsonAsync(item.Socket, payload, _cts.Token);
                }
                catch
                {
                    deadClients.Add(item.PlayerId);
                }
            }));

            foreach (var playerId in deadClients.Distinct())
            {
                RemoveClient(playerId);
            }

            try
            {
                await Task.Delay(tickMs, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Simulate(float dt)
    {
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

        foreach (var flag in Map.FlagsByTeam.Values)
        {
            flag.ResetToBase();
        }

        foreach (var player in _players.Values)
        {
            player.Position = player.SpawnPosition;
            player.Facing = player.Team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f);
            player.Input = new InputState();
            player.CarryingFlagTeam = null;
            player.ShootCooldownRemaining = 0f;
            player.PendingShoot = false;
        }
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

        player.Position = FindRespawnPosition(player);
        player.Facing = player.Team == "blue" ? new Vec2(1f, 0f) : new Vec2(-1f, 0f);
        player.ShootCooldownRemaining = 0.35f;
        player.PendingShoot = false;
    }

    private Vec2 FindRespawnPosition(PlayerRuntime player)
    {
        var preferred = player.SpawnPosition;
        if (!CollidesWithWorld(preferred, player.Radius) && !CollidesWithPlayers(preferred, player.Radius, player.Id))
        {
            return preferred;
        }

        for (var i = 0; i < 24; i++)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2d);
            var distance = 18f + (float)_random.NextDouble() * 60f;
            var candidate = new Vec2(
                preferred.X + MathF.Cos(angle) * distance,
                preferred.Y + MathF.Sin(angle) * distance);

            if (!CollidesWithWorld(candidate, player.Radius) && !CollidesWithPlayers(candidate, player.Radius, player.Id))
            {
                return candidate;
            }
        }

        return preferred;
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

        if (player.CarryingFlagTeam is null)
        {
            if (enemyFlag.CarriedByPlayerId is null && Geometry.DistanceSquared(player.Position, enemyFlag.Position) <= 28f * 28f)
            {
                player.CarryingFlagTeam = enemyFlag.Team;
                enemyFlag.CarriedByPlayerId = player.Id;
                enemyFlag.Position = player.Position;
            }
            else if (ownFlag.CarriedByPlayerId is null && !ownFlag.IsAtBase && Geometry.DistanceSquared(player.Position, ownFlag.Position) <= 26f * 26f)
            {
                ownFlag.ResetToBase();
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
        var dto = new
        {
            type = "state",
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            scores = new { blue = _blueScore, red = _redScore },
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

    private async Task SendStateToAsync(string playerId, CancellationToken cancellationToken)
    {
        ConnectedClient? client;
        string payload;
        lock (_sync)
        {
            _clients.TryGetValue(playerId, out client);
            payload = BuildStatePayload();
        }

        if (client is not null && client.Socket.State == WebSocketState.Open)
        {
            await SendRawJsonAsync(client.Socket, payload, cancellationToken);
        }
    }

    private static Task SendJsonAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        return SendRawJsonAsync(socket, json, cancellationToken);
    }

    private static Task SendRawJsonAsync(WebSocket socket, string payload, CancellationToken cancellationToken)
    {
        var buffer = Encoding.UTF8.GetBytes(payload);
        return socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
    }

    private void RemoveClient(string playerId)
    {
        lock (_sync)
        {
            _clients.Remove(playerId);
            if (_players.Remove(playerId, out var player))
            {
                if (player.CarryingFlagTeam is not null && Map.FlagsByTeam.TryGetValue(player.CarryingFlagTeam, out var flag))
                {
                    flag.ResetToBase();
                }
            }
        }

        _logger.LogInformation("Client removed {PlayerId}", playerId);
    }

    private string ChooseTeam()
    {
        var blue = _players.Values.Count(p => p.Team == "blue");
        var red = _players.Values.Count(p => p.Team == "red");
        return blue <= red ? "blue" : "red";
    }

    private Vec2 FindSpawn(string team)
    {
        var ownFlag = Map.FlagsByTeam[team];

        for (var i = 0; i < 48; i++)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2d);
            var distance = 80f + (float)_random.NextDouble() * 60f;
            var spawn = new Vec2(
                ownFlag.BasePosition.X + MathF.Cos(angle) * distance,
                ownFlag.BasePosition.Y + MathF.Sin(angle) * distance);

            if (!CollidesWithWorld(spawn, PlayerRadius) && !CollidesWithPlayers(spawn, PlayerRadius))
            {
                return spawn;
            }
        }

        return ownFlag.BasePosition + (team == "blue" ? new Vec2(50f, 0f) : new Vec2(-50f, 0f));
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

        if (source.Objects.Count == 0)
        {
            throw new InvalidOperationException("The map does not contain any objects.");
        }

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

    private sealed record RayPlayerHit(PlayerRuntime Player, float Distance);
}
