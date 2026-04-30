using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheFlag.Server;

public sealed class GameRoomManager
{
    public const string DefaultRoomId = "public";
    public const int MaxActiveRooms = 24;
    private static readonly TimeSpan EmptyRoomRetention = TimeSpan.FromMinutes(3);
    private static readonly Regex RoomIdRegex = new("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly Dictionary<string, GameRoom> _rooms = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _mapPath;
    private readonly ILogger _logger;
    private int _activeConnectionScopes;

    public GameRoomManager(string mapPath, ILogger logger)
    {
        _mapPath = mapPath;
        _logger = logger;
        _ = MapLoader.LoadMapFromFile(mapPath);
    }

    public int TickRate => 20;
    public int MaxPlayersPerRoom => GameRoom.MaxPlayersPerRoom;
    public int MaxRooms => MaxActiveRooms;

    public int ActiveRooms
    {
        get
        {
            lock (_sync)
            {
                return _rooms.Count;
            }
        }
    }

    public int TotalPlayers
    {
        get
        {
            lock (_sync)
            {
                return _rooms.Values.Sum(room => room.PlayerCount);
            }
        }
    }

    public void Start()
    {
        _logger.LogInformation(
            "Game room manager started. Default room: {DefaultRoomId}. Max active rooms: {MaxActiveRooms}. Max players per room: {MaxPlayersPerRoom}.",
            DefaultRoomId,
            MaxActiveRooms,
            GameRoom.MaxPlayersPerRoom);
    }

    public void Stop()
    {
        _cts.Cancel();

        List<GameRoom> rooms;
        lock (_sync)
        {
            rooms = _rooms.Values.ToList();
            _rooms.Clear();
        }

        foreach (var room in rooms)
        {
            room.Stop();
        }

        _logger.LogInformation("Game room manager stopped.");
    }

    public string GetRawMapJson()
    {
        return File.ReadAllText(_mapPath);
    }

    public RoomManagerSummary GetSummary()
    {
        lock (_sync)
        {
            return new RoomManagerSummary(
                _rooms.Count,
                MaxActiveRooms,
                _rooms.Values.Sum(room => room.PlayerCount),
                GameRoom.MaxPlayersPerRoom);
        }
    }

    public object GetRoomsResponse()
    {
        List<RoomSummary> rooms;
        int activeRooms;
        int totalPlayers;

        lock (_sync)
        {
            rooms = _rooms.Values
                .OrderBy(room => room.RoomId, StringComparer.Ordinal)
                .Select(room => room.GetSummary())
                .ToList();
            activeRooms = _rooms.Count;
            totalPlayers = rooms.Sum(room => room.PlayerCount);
        }

        return new
        {
            activeRooms,
            maxActiveRooms = MaxActiveRooms,
            totalPlayers,
            maxPlayersPerRoom = GameRoom.MaxPlayersPerRoom,
            rooms
        };
    }

    public bool TryNormalizeRoomId(string? requestedRoomId, out string roomId, out string? error)
    {
        roomId = string.IsNullOrWhiteSpace(requestedRoomId)
            ? DefaultRoomId
            : requestedRoomId.Trim().ToLowerInvariant();

        if (!RoomIdRegex.IsMatch(roomId))
        {
            error = "Invalid room id. Use 1 to 32 characters: lowercase letters, numbers, hyphen or underscore.";
            return false;
        }

        error = null;
        return true;
    }

    public RoomCreationResult TryCreateRoom(string? requestedRoomId)
    {
        if (!TryNormalizeRoomId(requestedRoomId, out var roomId, out var validationError))
        {
            _logger.LogWarning("Rejected invalid room id '{RoomId}'.", requestedRoomId);
            return new RoomCreationResult(false, 400, validationError ?? "Invalid room id.", null);
        }

        List<GameRoom> roomsToStop = [];
        RoomCreationResult result;

        lock (_sync)
        {
            roomsToStop = RemoveEmptyRoomsLocked();

            if (_rooms.ContainsKey(roomId))
            {
                result = new RoomCreationResult(true, 200, "Room already exists.", roomId);
            }
            else if (_rooms.Count >= MaxActiveRooms)
            {
                _logger.LogWarning("Maximum active rooms reached. Rejected creation of room {RoomId}.", roomId);
                result = new RoomCreationResult(false, 429, "The maximum number of active rooms has been reached.", null);
            }
            else
            {
                var room = new GameRoom(roomId, _mapPath, _logger, ScheduleEmptyRoomCleanup);
                room.Start();
                _rooms[roomId] = room;
                _logger.LogInformation("Room created: {RoomId}", roomId);
                result = new RoomCreationResult(true, 201, "Room created.", roomId);
            }
        }

        StopRooms(roomsToStop);
        return result;
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
            nextMap = MapLoader.LoadMapFromJson(normalizedRawJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new MapReplaceResult(false, 400, ex.Message);
        }

        List<GameRoom> roomsToStop;
        lock (_sync)
        {
            var totalPlayers = _rooms.Values.Sum(room => room.PlayerCount);
            var totalClients = _rooms.Values.Sum(room => room.ClientCount);
            if (totalPlayers > 0 || totalClients > 0 || _activeConnectionScopes > 0)
            {
                _logger.LogWarning(
                    "Rejected map replacement because players or WebSocket connections are active. Players: {Players}. Clients: {Clients}. Connection scopes: {ConnectionScopes}.",
                    totalPlayers,
                    totalClients,
                    _activeConnectionScopes);

                return new MapReplaceResult(
                    false,
                    StatusCodes.Status409Conflict,
                    "The map cannot be replaced while players are connected in active rooms. Disconnect everyone and try again.");
            }

            try
            {
                File.WriteAllText(_mapPath, normalizedRawJson);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not persist updated map to disk.");
                return new MapReplaceResult(false, 500, "The map could not be saved to disk.");
            }

            roomsToStop = _rooms.Values.ToList();
            _rooms.Clear();
        }

        StopRooms(roomsToStop);
        _logger.LogInformation("Global map replaced successfully: {MapName}", nextMap.Source.Meta.Name);
        return new MapReplaceResult(true, 200, "Map updated successfully on the server.", nextMap.Source.Meta.Name, nextMap.Source.Objects.Count);
    }

    public async Task HandleClientAsync(HttpContext context)
    {
        var requestedRoomId = context.Request.Query["room"].FirstOrDefault();
        if (!TryNormalizeRoomId(requestedRoomId, out var roomId, out var validationError))
        {
            _logger.LogWarning("Rejected invalid room id '{RoomId}' from WebSocket request.", requestedRoomId);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(validationError ?? "Invalid room id.");
            return;
        }

        GameRoom? room = null;
        List<GameRoom> roomsToStop = [];
        int? rejectionStatusCode = null;
        string? rejectionMessage = null;

        lock (_sync)
        {
            roomsToStop = RemoveEmptyRoomsLocked(exceptRoomId: roomId);

            if (!_rooms.TryGetValue(roomId, out room))
            {
                if (_rooms.Count >= MaxActiveRooms)
                {
                    _logger.LogWarning("Maximum active rooms reached. Rejected WebSocket room creation for {RoomId}.", roomId);
                    rejectionStatusCode = StatusCodes.Status429TooManyRequests;
                    rejectionMessage = "The maximum number of active rooms has been reached.";
                }
                else
                {
                    room = new GameRoom(roomId, _mapPath, _logger, ScheduleEmptyRoomCleanup);
                    room.Start();
                    _rooms[roomId] = room;
                    _logger.LogInformation("Room created: {RoomId}", roomId);
                }
            }

            if (rejectionStatusCode is null && room is not null && room.PlayerCount >= GameRoom.MaxPlayersPerRoom)
            {
                _logger.LogWarning("Room {RoomId} is full.", roomId);
                rejectionStatusCode = StatusCodes.Status429TooManyRequests;
                rejectionMessage = "The room is full.";
            }

            if (rejectionStatusCode is null)
            {
                _activeConnectionScopes++;
            }
        }

        StopRooms(roomsToStop);

        if (rejectionStatusCode is not null || room is null)
        {
            context.Response.StatusCode = rejectionStatusCode ?? StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync(rejectionMessage ?? "Could not join room.");
            return;
        }

        try
        {
            await room.HandleClientAsync(context);
        }
        finally
        {
            lock (_sync)
            {
                _activeConnectionScopes = Math.Max(0, _activeConnectionScopes - 1);
            }
        }
    }

    private void ScheduleEmptyRoomCleanup(string roomId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EmptyRoomRetention, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }

            GameRoom? roomToStop = null;
            lock (_sync)
            {
                if (_rooms.TryGetValue(roomId, out var room) && room.IsEmpty)
                {
                    _rooms.Remove(roomId);
                    roomToStop = room;
                }
            }

            if (roomToStop is not null)
            {
                _logger.LogInformation("Room removed because it is empty: {RoomId}", roomId);
                roomToStop.Stop();
            }
        }, CancellationToken.None);
    }

    private List<GameRoom> RemoveEmptyRoomsLocked(string? exceptRoomId = null)
    {
        var roomsToStop = new List<GameRoom>();
        foreach (var (roomId, room) in _rooms.ToArray())
        {
            if (roomId == exceptRoomId)
            {
                continue;
            }

            if (!room.IsEmpty)
            {
                continue;
            }

            _rooms.Remove(roomId);
            roomsToStop.Add(room);
            _logger.LogInformation("Room removed because it is empty: {RoomId}", roomId);
        }

        return roomsToStop;
    }

    private static void StopRooms(IEnumerable<GameRoom> rooms)
    {
        foreach (var room in rooms)
        {
            room.Stop();
        }
    }
}

public sealed record RoomCreationResult(bool Success, int StatusCode, string Message, string? RoomId);
