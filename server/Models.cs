using System.Text.Json.Serialization;

namespace TheFlag.Server;

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
    public required string Team { get; init; }
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
    public required string PlayerId { get; init; }
    public required System.Net.WebSockets.WebSocket Socket { get; init; }
    public long? PendingPongNonce { get; set; }
}


public sealed record MapReplaceResult(bool Success, int StatusCode, string Message, string? MapName = null, int? ObjectCount = null);

public readonly record struct Vec2(float X, float Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float scalar) => new(a.X * scalar, a.Y * scalar);
}
