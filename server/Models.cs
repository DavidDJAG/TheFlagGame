using System.Text.Json.Serialization;
using System.Threading.Channels;


namespace TheFlag.Server;


public sealed class ServerRuntimeOptions
{
    public bool TrainingMode { get; init; }
    public int TickRate { get; init; } = 20;
    public float TimeScale { get; init; } = 1f;
    public bool RunAsFastAsPossible { get; init; }
    public int? MatchDurationSecondsOverride { get; init; }
    public int ResetCooldownSeconds { get; init; } = 60;
    public bool AutoResetFinishedMatches { get; init; }
    public bool DisableClientIdleTimeout { get; init; }
    public int MaxMessagesPerRateLimitWindow { get; init; } = 200;
    public float MaxSimulationStepSeconds { get; init; } = 1f / 60f;
    public int MaxSimulationSubstepsPerTick { get; init; } = 3;

    public static ServerRuntimeOptions Production { get; } = new()
    {
        TrainingMode = false,
        TickRate = 20,
        TimeScale = 1f,
        RunAsFastAsPossible = false,
        MatchDurationSecondsOverride = null,
        ResetCooldownSeconds = 60,
        AutoResetFinishedMatches = false,
        DisableClientIdleTimeout = false,
        MaxMessagesPerRateLimitWindow = 200,
        MaxSimulationStepSeconds = 1f / 60f,
        MaxSimulationSubstepsPerTick = 3
    };
}

public sealed class PlayerStatsRuntime
{
    public required string PlayerId { get; init; }
    public required string Name { get; set; }
    public required string Team { get; set; }
    public int ShotsFired { get; set; }
    public int HitsDealt { get; set; }
    public int HitsTaken { get; set; }
    public int Eliminations { get; set; }
    public int Deaths { get; set; }
    public int FlagPickups { get; set; }
    public int FlagDrops { get; set; }
    public int FlagReturns { get; set; }
    public int FlagCaptures { get; set; }
    public float CarrySeconds { get; set; }
    public float DistanceTravelled { get; set; }

    public void ResetForMatch(string name, string team)
    {
        Name = name;
        Team = team;
        ShotsFired = 0;
        HitsDealt = 0;
        HitsTaken = 0;
        Eliminations = 0;
        Deaths = 0;
        FlagPickups = 0;
        FlagDrops = 0;
        FlagReturns = 0;
        FlagCaptures = 0;
        CarrySeconds = 0f;
        DistanceTravelled = 0f;
    }
}

public sealed class GameEventRuntime
{
    public required string Id { get; init; }
    public required long Sequence { get; init; }
    public required string MatchId { get; init; }
    public required string Type { get; init; }
    public required long ServerTime { get; init; }
    public string? PlayerId { get; init; }
    public string? PlayerName { get; init; }
    public string? Team { get; init; }
    public string? ShooterPlayerId { get; init; }
    public string? ShooterTeam { get; init; }
    public string? VictimPlayerId { get; init; }
    public string? VictimTeam { get; init; }
    public string? FlagTeam { get; init; }
    public string? WinnerTeam { get; init; }
    public string? LoserTeam { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? ImpactX { get; init; }
    public float? ImpactY { get; init; }
    public float? Life { get; init; }
    public int? BlueScore { get; init; }
    public int? RedScore { get; init; }
}

public sealed class MapDocument
{
    public MapMeta Meta { get; set; } = new();
    public List<MapObjectDto> Objects { get; set; } = [];
}

public sealed class MapMeta
{
    public string Name { get; set; } = "Map";
    public string Version { get; set; } = "1.0.0";
    public CanvasMeta Canvas { get; set; } = new();
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class CanvasMeta
{
    public int Width { get; set; } = 1400;
    public int Height { get; set; } = 900;
}

public sealed class MapObjectDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Hard { get; set; }
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? Width { get; set; }
    public float? Height { get; set; }
    public float? Radius { get; set; }
    public string? Team { get; set; }
    public List<PointDto>? Points { get; set; }
}

public sealed class PointDto
{
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class GameMap
{
    public required MapDocument Source { get; init; }
    public required string RawJson { get; init; }
    public required PerimeterShape Perimeter { get; init; }
    public required List<ObstacleShape> Obstacles { get; init; }
    public required Dictionary<string, FlagRuntime> FlagsByTeam { get; init; }
    public int Width => Source.Meta.Canvas.Width;
    public int Height => Source.Meta.Canvas.Height;
}

public sealed class ObstacleShape
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public bool Hard { get; init; }
    public List<Vec2>? Points { get; init; }
    public Vec2 Position { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public float Radius { get; init; }
}

public sealed class PerimeterShape
{
    public required string Id { get; init; }
    public required List<Vec2> Points { get; init; }
}

public sealed class FlagRuntime
{
    public required string Id { get; init; }
    public required string Team { get; init; }
    public required Vec2 BasePosition { get; init; }
    public Vec2 Position { get; set; }
    public string? CarriedByPlayerId { get; set; }

    [JsonIgnore]
    public bool IsAtBase => CarriedByPlayerId is null && Geometry.DistanceSquared(Position, BasePosition) < 1f;

    public void ResetToBase()
    {
        Position = BasePosition;
        CarriedByPlayerId = null;
    }
}

public sealed class PlayerRuntime
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Team { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 SpawnPosition { get; set; }
    public Vec2 Facing { get; set; } = new(1f, 0f);
    public InputState Input { get; set; } = new();
    public float Radius { get; init; } = 14f;
    public float MoveSpeed { get; init; } = 210f;
    public string? CarryingFlagTeam { get; set; }
    public float ShootCooldownRemaining { get; set; }
    public bool PendingShoot { get; set; }
}

public sealed class InputState
{
    public bool Up { get; set; }
    public bool Down { get; set; }
    public bool Left { get; set; }
    public bool Right { get; set; }
}

public sealed class ShotTraceRuntime
{
    public required string Id { get; init; }
    public required string ShooterPlayerId { get; init; }
    public required string Team { get; init; }
    public required Vec2 Start { get; init; }
    public required Vec2 End { get; init; }
    public float RemainingLifetime { get; set; }
}

public sealed class HitEffectRuntime
{
    public required string Id { get; init; }
    public required string ShooterPlayerId { get; init; }
    public required string VictimPlayerId { get; init; }
    public required string ShooterTeam { get; init; }
    public required string VictimTeam { get; init; }
    public required Vec2 ImpactPosition { get; init; }
    public float RemainingLifetime { get; set; }
}

public sealed class ConnectedClient
{
    private const int OutboundQueueCapacity = 8;
    private int _stopRequested;

    public ConnectedClient()
    {
        Outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboundQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
    }

    public required string PlayerId { get; init; }
    public required string RoomId { get; init; }
    public required System.Net.WebSockets.WebSocket Socket { get; init; }
    public bool IsSpectator { get; init; }
    public long? PendingPongNonce { get; set; }
    public DateTimeOffset LastReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RateLimitWindowStartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int MessagesInCurrentWindow { get; set; }
    public Channel<string> Outbound { get; }
    public CancellationTokenSource SendCancellation { get; } = new();
    public Task? WriterTask { get; set; }
    public bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

    public bool TryQueueRawJson(string payload)
    {
        if (IsStopRequested)
        {
            return false;
        }

        return Outbound.Writer.TryWrite(payload);
    }

    public void Stop(bool abortSocket)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        Outbound.Writer.TryComplete();
        SendCancellation.Cancel();

        if (abortSocket)
        {
            Socket.Abort();
        }
    }
}

public sealed record RoomSummary(
    string RoomId,
    int PlayerCount,
    int MaxPlayers,
    string MapName,
    string MatchStatus);

public sealed record RoomManagerSummary(
    int ActiveRooms,
    int MaxActiveRooms,
    int TotalPlayers,
    int MaxPlayersPerRoom);

public sealed record CreateRoomRequest(string? RoomId);

public sealed record MapReplaceResult(bool Success, int StatusCode, string Message, string? MapName = null, int? ObjectCount = null);

public readonly record struct Vec2(float X, float Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float scalar) => new(a.X * scalar, a.Y * scalar);
}
