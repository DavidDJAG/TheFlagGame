# THE FLAG Server

Authoritative ASP.NET Core backend for **THE FLAG**, a real-time multiplayer capture-the-flag prototype. The server owns the game simulation, exposes the HTTP API, accepts WebSocket clients, loads the base map from `server/Data/map.json`, supports team preference and spectator connections, can emit training telemetry when `trainingMode=true`, and can optionally serve the PWA client from `../client-pwa`.

## Current capabilities

- Multi-room game hosting with independent room state.
- Default room support through `/ws`, equivalent to `/ws?room=public`.
- Automatic room creation when a valid room is requested.
- Per-room player limit: `32` players.
- Global active-room limit: `24` rooms.
- Automatic cleanup of empty rooms after a short retention period.
- Authoritative movement, collision, shooting, flag capture, scoring, match timer, match finish, and reset logic.
- Team preference on connect through `team=auto|blue|red`.
- Spectator connections through `spectator=true`.
- Reset logic that preserves fixed `blue` / `red` requested teams and only rebalances `auto` players.
- Training-oriented runtime mode through `server-runtime.json` with telemetry for synthetic AI training datasets.
- Global base map API for the map editor.
- Safe map replacement only when no players or WebSocket clients are active in any room.
- Room listing and explicit room creation API.
- Local file logging with rotation.

## Project layout

```text
server/
  Data/
    map.json
  Properties/
    launchSettings.json
  GameHost.cs
  GameRoomManager.cs
  Geometry.cs
  LocalFileLoggerProvider.cs
  MapLoader.cs
  Models.cs
  Program.cs
  server-runtime.json
  TheFlag.Server.csproj
  TheFlag.Server.sln
  icon.ico
  README.md
```

The broader workspace can also include:

```text
client-pwa/
  Playable HTML/CSS/JavaScript PWA client.

client-pwa/editor/
  Static editor used to create, modify, validate, and save map JSON.

nginx.conf
  Production reverse-proxy configuration for HTTP API, frontend, and WebSocket routing.
```

## Architecture

### `GameRoomManager`

`GameRoomManager` owns the room registry and global coordination responsibilities:

- normalizes and validates room IDs;
- creates rooms on demand;
- enforces `MaxActiveRooms`;
- routes WebSocket connections to the target room;
- exposes global room metrics;
- lists active rooms;
- schedules cleanup for empty rooms;
- coordinates global map replacement;
- starts and stops all active rooms during server lifecycle events.

### `GameRoom`

Each `GameRoom` is an isolated authoritative match instance. A room contains its own:

- players;
- WebSocket clients;
- current teams and original requested team preferences;
- positions;
- inputs;
- scores;
- runtime flag state;
- flag carriers;
- shots;
- hit events;
- match timer;
- match-finished state;
- reset behavior.

Players in one room do not appear, collide, shoot, score, carry flags, receive events, or reset the match state of any other room.

### `MapLoader`

`MapLoader` loads and validates `Data/map.json`. The base map file is global, but every room receives its own runtime map instance. Mutable objects such as flag positions and `CarriedByPlayerId` are not shared across rooms.

## Running locally

### Requirements

- .NET SDK compatible with the project target framework.
- Modern browser for the PWA client.

### Start the server

From the `server/` folder:

```powershell
dotnet run --project .\TheFlag.Server.csproj
```

The server binds to:

```text
http://0.0.0.0:5770
```

### Useful local URLs

- Health: `http://127.0.0.1:5770/health`
- Map JSON: `http://127.0.0.1:5770/api/map`
- Rooms API: `http://127.0.0.1:5770/api/rooms`
- Default WebSocket room: `ws://127.0.0.1:5770/ws`
- Named WebSocket room: `ws://127.0.0.1:5770/ws?room=alpha`
- Team-selected WebSocket player: `ws://127.0.0.1:5770/ws?room=alpha&team=blue`
- Spectator WebSocket client: `ws://127.0.0.1:5770/ws?room=alpha&spectator=true`
- PWA client served by the backend: `http://127.0.0.1:5770/pwa/`

## PWA serving behavior

`Program.cs` tries to serve two static clients when their folders exist:

- `../client-web` at `/`;
- `../client-pwa` at `/pwa`.

If `client-web` is not present, the active browser client is the PWA under `/pwa/`.

## Rooms

### Default room

A client that connects without a room is assigned to `public`:

```text
/ws
```

is equivalent to:

```text
/ws?room=public
```

This keeps older clients functional because they can still connect to `/ws` without a query string.

### Joining a room

The WebSocket endpoint accepts a `room` query parameter:

```text
/ws?room=<roomId>
```

Examples:

```text
/ws?room=public
/ws?room=alpha
/ws?room=test-01
```

If the room does not exist and the active-room limit has not been reached, it is created automatically.

### Selecting a team on connect

The WebSocket endpoint also accepts an optional `team` query parameter:

```text
/ws?team=<auto|blue|red>
/ws?room=<roomId>&team=<auto|blue|red>
```

Supported values:

- `auto`: default behavior; the server assigns the player to the currently smaller team.
- `blue`: force the new player into the blue team.
- `red`: force the new player into the red team.

Missing or empty `team` is treated as `auto`, so older clients keep the same behavior.

Examples:

```text
/ws
/ws?team=auto
/ws?team=blue
/ws?room=alpha&team=red
```

Invalid `team` values are rejected with `400 Bad Request` before the WebSocket is accepted. The accepted request is stored in `PlayerRuntime.RequestedTeam`; this is intentionally separate from the mutable `Team` value so the reset flow can preserve fixed selections.

### Joining as spectator

The WebSocket endpoint accepts an optional `spectator` query parameter:

```text
/ws?spectator=<true|false>
/ws?room=<roomId>&spectator=<true|false>
```

Supported truthy values: `true`, `1`, `yes`.
Supported falsy values: `false`, `0`, `no`.

Missing or empty `spectator` is treated as `false`, so older clients keep joining as players.

Examples:

```text
/ws?spectator=true
/ws?room=alpha&spectator=true
/ws?room=alpha&spectator=false&team=blue
```

Spectator clients:

- receive the initial `welcome` message and all regular `state` snapshots;
- do not create a player entity;
- do not count against `MaxPlayersPerRoom`;
- are allowed to join full rooms;
- can receive `pong` responses to `ping`;
- cannot move, shoot, rename a player, or reset the match.

When `spectator=true`, the `team` query parameter is ignored. Invalid `spectator` values are rejected with `400 Bad Request` before the WebSocket is accepted.

### Room ID validation

The server normalizes room IDs by trimming whitespace and converting to lowercase. Empty or missing values become `public`.

Valid room IDs must match:

```regex
^[a-z0-9_-]{1,32}$
```

Invalid room IDs are rejected with `400 Bad Request` and logged.

### Limits

- `MaxPlayersPerRoom = 32`
- `MaxActiveRooms = 24`

If an existing room is full, new WebSocket connections to that room are rejected with `429 Too Many Requests` and the message:

```text
The room is full.
```

If a new room is requested after `MaxActiveRooms` has been reached, creation is rejected with `429 Too Many Requests` and the message:

```text
The maximum number of active rooms has been reached.
```

### Empty room cleanup

Rooms that become empty are scheduled for cleanup. The current retention period is 3 minutes. Empty rooms can also be purged before new rooms are created, which helps keep the room count under the active-room limit.

## Match simulation

Each room runs the same authoritative simulation rules:

- fixed `20` Hz tick rate in production mode;
- automatic balanced `blue` / `red` team assignment on connect by default;
- optional fixed `team=blue` or `team=red` preference on connect;
- reset-time team handling that preserves fixed requested teams and only rebalances players whose requested team is `auto`;
- 5-minute match clock owned by the server;
- match-finished state when the timer reaches zero;
- winner, loser, or tie calculation from final scores;
- movement driven by discrete directional input;
- world collision against perimeter, rectangles, circles, and polygons;
- wall sliding when diagonal movement hits hard geometry, so players can keep moving along the wall instead of getting stuck on contact;
- soft separation between overlapping players;
- independent room-local scoring, flags, shots, events, and timer.


### Team selection and reset behavior

The connection-level team preference is parsed before the WebSocket is accepted:

```text
/ws?room=alpha&team=auto
/ws?room=alpha&team=blue
/ws?room=alpha&team=red
```

For regular players, the accepted value is persisted in `PlayerRuntime.RequestedTeam`.

- `RequestedTeam = "blue"`: the player is restored to blue during `resetGame`.
- `RequestedTeam = "red"`: the player is restored to red during `resetGame`.
- `RequestedTeam = "auto"`: the player may be assigned to whichever team has fewer players during connect or reset.

`ReassignPlayerTeams()` first restores all fixed `blue` / `red` players, counts those assignments, then distributes `auto` players to the smaller team. This prevents a reset from overriding the user's fixed selection.

### Spawn rules

- Red players spawn in a narrow band near the upper edge of the map.
- Blue players spawn in a narrow band near the lower edge of the map.
- The server first tries the preferred team zone.
- If the ideal area is blocked, it searches nearby collision-free points.
- If the preferred zone is unavailable, it searches the corresponding team half.
- The legacy flag-adjacent spawn is used only as a final fallback.
- Spawn checks avoid hard world collisions and occupied players.
- Spawn zones are procedural and are not currently stored in `map.json`.

### Objective and rules

- Each team owns a home flag.
- A player can pick up the enemy flag by moving close to it.
- A player carrying the enemy flag can still return their own dropped flag by touching it.
- A team scores when a player brings the enemy flag back while the home flag is at base.
- If a player dies while carrying a flag, the flag drops at the elimination point.
- If a player disconnects while carrying a flag, that flag is reset to base in the same room only.
- `resetGame` resets only the sender's room.
- `resetGame` resets scores, flags, inputs, cooldowns, shots, effects, timer, team assignment, and player positions in that room. Fixed requested teams are restored first; `auto` players are then distributed to the smaller team.
- `resetGame` can be used during an active match or after a match has finished.

### Combat

- Shooting is fully authoritative.
- Shot range is `420` world units.
- Shot cooldown is `0.25` seconds.
- Shot traces are ephemeral and exposed through `shots`.
- Hit events are ephemeral and exposed through `events[].type === "playerHit"`.
- Respawn on hit is immediate.
- Friendly fire is enabled. The server does not filter teammates when resolving hits.

## Network reliability

The backend uses a non-blocking WebSocket broadcast model:

- each room builds one state payload per tick;
- each connected client has its own bounded outbound queue;
- each connected client has a dedicated writer task;
- the game loop enqueues snapshots instead of awaiting every `SendAsync`;
- if a client is slow, the queue drops the oldest outbound state instead of freezing the room;
- each WebSocket send has a 2-second timeout;
- clients with failed or timed-out writers are removed without stopping the room;
- incoming WebSocket messages are limited to 16 KB;
- invalid JSON and unexpected message-processing failures are logged.

## HTTP API

### `GET /health`

Returns global server status with room metrics.

Example response:

```json
{
  "status": "ok",
  "activeRooms": 2,
  "maxActiveRooms": 24,
  "players": 5,
  "maxPlayersPerRoom": 32,
  "tickRate": 20,
  "map": "map.json"
}
```

### `GET /api/map`

Returns the raw JSON of the global base map currently stored at:

```text
server/Data/map.json
```

Response content type:

```text
application/json
```

### `PUT /api/map`

Replaces the global base map only when there are no active players, clients, or WebSocket connection scopes in any room.

Request rules:

- body must contain the full map JSON document;
- body size is limited to 1 MB;
- map structure is validated before persistence;
- accepted maps are written to `server/Data/map.json`;
- empty existing rooms are stopped and cleared after a successful replacement;
- future rooms are initialized from the new map.

Example success response:

```json
{
  "ok": true,
  "message": "Map updated successfully on the server.",
  "mapName": "Blaze Field",
  "objectCount": 54
}
```

If any players or WebSocket clients are active in any room, the server returns `409 Conflict`:

```json
{
  "ok": false,
  "message": "The map cannot be replaced while players are connected in active rooms. Disconnect everyone and try again."
}
```

### `GET /api/rooms`

Lists active rooms and global room-manager metrics.

Example response:

```json
{
  "activeRooms": 2,
  "maxActiveRooms": 24,
  "totalPlayers": 5,
  "maxPlayersPerRoom": 32,
  "rooms": [
    {
      "roomId": "public",
      "playerCount": 3,
      "maxPlayers": 32,
      "mapName": "Blaze Field",
      "matchStatus": "running"
    },
    {
      "roomId": "alpha",
      "playerCount": 2,
      "maxPlayers": 32,
      "mapName": "Blaze Field",
      "matchStatus": "running"
    }
  ]
}
```

### `POST /api/rooms`

Creates a room explicitly, or returns success if the room already exists.

Example request:

```http
POST /api/rooms
Content-Type: application/json

{
  "roomId": "alpha"
}
```

Example created response:

```json
{
  "ok": true,
  "roomId": "alpha",
  "message": "Room created."
}
```

Example existing-room response:

```json
{
  "ok": true,
  "roomId": "alpha",
  "message": "Room already exists."
}
```

If the active-room limit has been reached, the server returns `429 Too Many Requests`:

```json
{
  "ok": false,
  "message": "The maximum number of active rooms has been reached."
}
```

## WebSocket contract

### Endpoint

```text
/ws?room=<roomId>&team=<auto|blue|red>
/ws?room=<roomId>&spectator=<true|false>
```

All query parameters are optional. Missing or empty `room` uses `public`. Missing or empty `team` uses `auto`, preserving balanced auto-assignment. Missing or empty `spectator` uses `false`. When `spectator=true`, the connection observes the room without creating a player and any `team` parameter is ignored.

### Client to server

```json
{ "type": "hello", "name": "Player1" }
{ "type": "input", "up": false, "down": true, "left": false, "right": true }
{ "type": "shoot" }
{ "type": "ping", "nonce": 123 }
{ "type": "resetGame" }
```

Details:

- `hello` changes the visible player name.
- Names are truncated to 24 characters.
- `input` updates the current directional state while the room match is running.
- `shoot` queues a shot for the next simulation tick while the room match is running.
- `ping` is answered with `pong` only to the same client.
- `resetGame` starts a fresh match only in the sender's room; it restores fixed requested teams first and rebalances only `auto` players. Spectator connections cannot reset the match.
- Movement and shooting are ignored after the match has finished until `resetGame` is received.

### Server to client

Initial message:

```json
{
  "type": "welcome",
  "roomId": "alpha",
  "connectionId": "p-123",
  "playerId": "p-123",
  "role": "player",
  "spectator": false,
  "team": "blue",
  "teamSelection": {
    "requested": "auto",
    "autoAssigned": true
  },
  "tickRate": 20,
  "mapName": "Blaze Field",
  "training": {
    "enabled": false,
    "timeScale": 1,
    "runAsFastAsPossible": false,
    "maxSimulationStepSeconds": 0.016666667,
    "maxSimulationSubstepsPerTick": 3,
    "matchClockDisabled": false
  }
}
```

For spectators, `connectionId` is generated with an `s-` prefix, `playerId` and `team` are `null`, `role` is `spectator`, and `spectator` is `true`:

```json
{
  "type": "welcome",
  "roomId": "alpha",
  "connectionId": "s-456",
  "playerId": null,
  "role": "spectator",
  "spectator": true,
  "team": null,
  "teamSelection": null,
  "tickRate": 20,
  "mapName": "Blaze Field",
  "training": {
    "enabled": false,
    "timeScale": 1,
    "runAsFastAsPossible": false,
    "maxSimulationStepSeconds": 0.016666667,
    "maxSimulationSubstepsPerTick": 3,
    "matchClockDisabled": false
  }
}
```

State snapshot:

```json
{
  "type": "state",
  "roomId": "alpha",
  "sequence": 123,
  "matchId": "m-123",
  "serverTime": 1710000000000,
  "scores": { "blue": 0, "red": 0 },
  "match": {
    "status": "running",
    "durationSeconds": 300,
    "startedAt": 1710000000000,
    "endsAt": 1710000300000,
    "remainingMs": 299500,
    "winnerTeam": null,
    "loserTeam": null,
    "isTie": false
  },
  "players": [],
  "flags": [],
  "shots": [],
  "events": [],
  "playerStats": [],
  "training": {
    "enabled": false,
    "timeScale": 1,
    "runAsFastAsPossible": false,
    "maxSimulationStepSeconds": 0.016666667,
    "maxSimulationSubstepsPerTick": 3
  }
}
```

Finished match snapshot example:

```json
{
  "roomId": "alpha",
  "match": {
    "status": "finished",
    "durationSeconds": 300,
    "remainingMs": 0,
    "winnerTeam": "blue",
    "loserTeam": "red",
    "isTie": false
  },
  "scores": { "blue": 3, "red": 1 }
}
```

Tie example:

```json
{
  "roomId": "alpha",
  "match": {
    "status": "finished",
    "remainingMs": 0,
    "winnerTeam": "draw",
    "loserTeam": null,
    "isTie": true
  }
}
```

Latency event:

```json
{
  "type": "pong",
  "nonce": 123
}
```

### `events`

Confirmed hits are always exposed as ephemeral visual events so the client can render feedback. When `trainingMode=true`, additional training events are also included in the same `events` array: `matchReset`, `matchFinished`, `playerJoined`, `playerLeft`, `shotFired`, `playerHit`, `playerEliminated`, `flagPickedUp`, `flagDropped`, `flagReturned`, and `flagCaptured`.

```json
{
  "id": "hit-...",
  "type": "playerHit",
  "shooterPlayerId": "p-1",
  "victimPlayerId": "p-2",
  "shooterTeam": "blue",
  "victimTeam": "red",
  "impactX": 742.5,
  "impactY": 381.2,
  "life": 0.31
}
```

This lets the client render visual feedback while still relying on the server's authoritative result. Training collectors can additionally consume `playerStats` for aggregate per-player counters.

## Map structure expected by the server

The backend deserializes the document into `MapDocument` and requires:

- at least one object;
- exactly one valid `perimeter` object with `points`;
- exactly two `flag` objects;
- one `blue` flag;
- one `red` flag.

It also consumes these obstacle types:

- `rect`;
- `circle`;
- `polygon`.

`meta.canvas.width` and `meta.canvas.height` define the logical world size.

Spawn zones are not part of the current map schema. They are calculated by the backend from the canvas dimensions and team color, using an upper/lower edge band instead of a central vertical band.

## Included map

The repository currently ships with:

- name: `Blaze Field`;
- canvas: `1800 x 950`;
- generated at: `2026-04-21T11:35:31.337Z`.

## Map editor behavior

The editor continues to work with the global base map:

```text
GET /api/map
PUT /api/map
```

Expected workflow:

1. The editor loads the map through `GET /api/map`.
2. The user modifies the document.
3. The editor validates the minimum structure and warnings.
4. The editor saves through `PUT /api/map`.
5. The server replaces `server/Data/map.json` only when no clients are active in any room.

Room-specific maps are not implemented yet.

## Security hardening

The server includes the following defensive controls:

- CORS is restricted to the approved origins configured in `Program.cs`.
- Local loopback origins are allowed for development, including `localhost`, `127.0.0.1`, and `::1` with arbitrary ports.
- WebSocket handshakes validate the `Origin` header before accepting the connection.
- `Origin: null` is accepted only for loopback requests.
- Incoming WebSocket messages are rate-limited per client in production mode.
- Idle WebSocket clients are disconnected after 30 seconds without inbound messages in production mode.
- Incoming WebSocket messages are limited to 16 KB.
- Slow or failed WebSocket writers are removed.
- `resetGame` is accepted only after at least 60 seconds of match time have elapsed in production mode; training mode can set `resetCooldownSeconds` to `0`.
- `PUT /api/map` rejects request bodies larger than 1 MB.
- Server-side map validation enforces strict limits before accepting or persisting a new map.
- Room creation is protected by strict room ID validation and `MaxActiveRooms`.

## Logging

`Program.cs` registers `LocalFileLoggerProvider`, which writes to:

```text
<exe-folder>/log.txt
```

The logger records:

- startup and room-manager lifecycle messages;
- room creation and removal;
- client connection and removal events with room IDs;
- rejected invalid room IDs;
- rejected room creation when the active-room limit is reached;
- rejected connections to full rooms;
- WebSocket receive and writer failures;
- rejected oversized or invalid WebSocket messages;
- unexpected game-loop tick exceptions;
- room-local match reset events;
- room-local match finished events;
- global map replacement events;
- rejected map replacement while players or WebSocket clients are active.

Log rotation is enabled by `LocalFileLoggerProvider`: `log.txt` rotates at 5 MB and keeps 5 archived files, `log.1.txt` through `log.5.txt`.

## Nginx deployment

The included root `nginx.conf` expects the backend at:

```text
http://127.0.0.1:5770
```

The relevant public routes are:

```text
https://server.mcrenox.com/theflag/
https://server.mcrenox.com/theflag/api/...
wss://server.mcrenox.com/theflag/ws
```

The WebSocket route forwards `Upgrade` and `Connection`, disables proxy buffering, and uses long read/send timeouts.

## Quick local tests

Connect two clients to the same room:

```js
const socketA = new WebSocket("ws://127.0.0.1:5770/ws?room=alpha");
const socketB = new WebSocket("ws://127.0.0.1:5770/ws?room=alpha");
```

Connect another client to an isolated room:

```js
const socketC = new WebSocket("ws://127.0.0.1:5770/ws?room=beta");
```

Connect fixed-team players and a spectator:

```js
const bluePlayer = new WebSocket("ws://127.0.0.1:5770/ws?room=alpha&team=blue");
const redPlayer = new WebSocket("ws://127.0.0.1:5770/ws?room=alpha&team=red");
const spectator = new WebSocket("ws://127.0.0.1:5770/ws?room=alpha&spectator=true");
```

Expected results:

- `alpha` sees only players from `alpha`.
- `beta` sees only players from `beta`.
- Shots, hits, flags, scores, timers, and resets do not cross rooms.
- `/ws` joins `public`.
- `/api/rooms` lists active rooms and player counts.
- `PUT /api/map` returns `409 Conflict` while any room has active players or WebSocket clients.
- Fixed `team=blue` and `team=red` players remain on their requested teams after `resetGame`; only `auto` players are rebalanced.
- The spectator receives `welcome` and `state` snapshots but does not create a player and cannot move, shoot, or reset.

## Current limitations

- Match state is lost when the process restarts.
- There is no persistent match storage.
- There is no authentication.
- There is no replay or match history.
- There are no built-in AI-controlled players or bots; `trainingMode` emits telemetry for external bot/model pipelines.
- Spawn points are procedural and are not explicit objects in the map JSON.
- Friendly fire is enabled.
- There is no advanced interpolation or reconciliation in the backend.
- There is no hot map reload while players are connected.
- The map is global, not room-specific.
- The map editor does not select or edit a specific room.
- There is no global player limit; only per-room and active-room limits are enforced.

## Operational notes

- `Making/map.json`, if present in older working copies, is not the runtime source of truth.
- `launchSettings.json` contains Visual Studio URLs, but the actual code uses `UseUrls("http://0.0.0.0:5770")`.
- `log.txt` is created in `AppContext.BaseDirectory`, which is normally the folder that contains the executable or published app binaries.

## Training-oriented server mode

The authoritative server can run either with production-safe runtime settings or with training-oriented settings for synthetic AI-training data generation. This mode does not spawn bots by itself; it exposes deterministic episode metadata, event streams, and player statistics for external bot clients, collectors, or model-training harnesses.

Runtime settings are read from `server-runtime.json` in `AppContext.BaseDirectory`. Environment variables are not used.

Behavior is intentionally fail-safe:

- If `server-runtime.json` is missing, the server uses `ServerRuntimeOptions.Production` (`trainingMode=false`).
- If `server-runtime.json` exists but has `"trainingMode": false`, all other runtime values in the JSON are ignored and production settings are used.
- If `server-runtime.json` has `"trainingMode": true`, the server uses the JSON values after clamping unsafe numeric ranges.

Default production `server-runtime.json`:

```json
{
  "trainingMode": false,
  "tickRate": 20,
  "timeScale": 1,
  "runAsFastAsPossible": false,
  "matchDurationSecondsOverride": null,
  "resetCooldownSeconds": 60,
  "autoResetFinishedMatches": false,
  "disableClientIdleTimeout": false,
  "maxMessagesPerRateLimitWindow": 200,
  "maxSimulationStepSeconds": 0.016666667,
  "maxSimulationSubstepsPerTick": 3
}
```

Example training preset:

```json
{
  "trainingMode": true,
  "tickRate": 60,
  "timeScale": 8,
  "runAsFastAsPossible": false,
  "matchDurationSecondsOverride": 300,
  "resetCooldownSeconds": 0,
  "autoResetFinishedMatches": true,
  "disableClientIdleTimeout": true,
  "maxMessagesPerRateLimitWindow": 0,
  "maxSimulationStepSeconds": 0.008333333,
  "maxSimulationSubstepsPerTick": 64
}
```

Relevant settings when `trainingMode=true`:

- `tickRate`: simulation snapshots per second.
- `timeScale`: multiplies the amount of simulation time processed per wall-clock tick. Movement/collision are split into physics substeps so accelerated training does not create large single-frame jumps.
- `maxSimulationStepSeconds`: maximum physics step used internally. `0.008333333` is 120 Hz physics. Lower values reduce tunneling risk but cost more CPU.
- `maxSimulationSubstepsPerTick`: safety cap for how many physics substeps may be processed for a single network tick.
- `runAsFastAsPossible`: when `true`, the loop does not wait between ticks and advances by a fixed `1 / tickRate` base step, still split through the physics substepper. This is CPU-intensive and intended for local/headless experiments.
- `matchDurationSecondsOverride`: set `0` to disable the match timer. Use `null` to use the normal five-minute match length.
- `resetCooldownSeconds`: set `0` to allow immediate `resetGame` messages.
- `disableClientIdleTimeout`: disables the 30 second WebSocket idle removal.
- `maxMessagesPerRateLimitWindow`: set `0` to disable the inbound message rate limit. This only disables the limit when `trainingMode=true`.

State snapshots always include top-level `sequence` and `matchId`, plus `match.id`.

When `trainingMode=true`, state snapshots also include populated `events` and `playerStats` training telemetry. When `trainingMode=false`, `playerStats` is empty and the server avoids generating training event/stat data, while still emitting short-lived client-compatible visual events such as `playerHit` for gameplay effects.

Training events include:

- `matchReset`
- `matchFinished`
- `playerJoined`
- `playerLeft`
- `shotFired`
- `playerHit`
- `playerEliminated`
- `flagPickedUp`
- `flagDropped`
- `flagReturned`
- `flagCaptured`

`playerHit` preserves the old client-compatible fields `impactX`, `impactY`, `shooterPlayerId`, `victimPlayerId`, `shooterTeam`, `victimTeam` and `life`.

Example `playerStats` payload:

```json
{
  "playerId": "p-...",
  "name": "QL-03",
  "team": "blue",
  "shotsFired": 14,
  "hitsDealt": 4,
  "hitsTaken": 2,
  "eliminations": 4,
  "deaths": 2,
  "flagPickups": 2,
  "flagDrops": 1,
  "flagReturns": 1,
  "flagCaptures": 1,
  "carrySeconds": 12.4,
  "distanceTravelled": 3902.8
}
```

## Training run preset

For validation runs, enable `trainingMode=true` and use:

```json
{
  "trainingMode": true,
  "matchDurationSecondsOverride": 300
}
```

That creates 5-minute episodes. With a bot/client harness using `maxRuntimeSeconds: 900`, a validation run should produce about three match summaries in 15 minutes. Set `matchDurationSecondsOverride` to `0` for an infinite match. If `trainingMode=false`, the package intentionally ignores this override and uses production settings.

## Training episodes v3

This package adds `autoResetFinishedMatches` to `server-runtime.json`.

Recommended 15-minute validation settings:

```json
{
  "trainingMode": true,
  "matchDurationSecondsOverride": 300,
  "resetCooldownSeconds": 0,
  "autoResetFinishedMatches": true
}
```

When a match reaches `matchDurationSecondsOverride`, the server emits one `matchFinished` state/event, then automatically creates the next `matchId` and emits `matchReset`. This keeps long training runs split into clean episodes.

## Movement collision update

The player movement resolver now uses surface-aware sliding before falling back to axis-only movement. When a move collides with a hard obstacle, the server sweeps to the last free point, probes the impacted surface normal, offsets the player slightly away from the surface, and projects the remaining movement onto the tangent. This specifically improves glancing collisions against circular obstacles and sharp polygon/rectangle corners so players keep sliding instead of getting stuck until they walk away from the wall.

## Author

**David Jorge Aguirre Grazio**  
Developer
