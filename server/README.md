# THE FLAG Server

Authoritative backend for the **THE FLAG** multiplayer prototype. This folder contains the ASP.NET Core minimal API server that:

- loads the map from `server/Data/map.json`
- keeps the state of a single in-memory match
- simulates movement, collisions, flags, shooting, respawn, scoring, and match timing
- exposes `GET /health`, `GET /api/map`, `PUT /api/map`, and `WS /ws`
- serves the current PWA client under `/pwa` when the `client-pwa` folder exists
- writes local logs to `log.txt` next to the executable

## Current state

The project currently consists of three main pieces:

1. `server/`
   .NET backend with WebSocket and authoritative simulation.
2. `client-pwa/`
   Playable HTML/CSS/JavaScript frontend. This is the active client.
3. `client-pwa/editor/`
   Static editor used to create or modify `map.json` and sync it with the server.

The root package also includes `nginx.conf`, which routes the production `/theflag/` frontend, `/theflag/api/` HTTP API, and `/theflag/ws` WebSocket endpoint to this backend while preserving other configured services.

## What the backend currently does

### Match simulation

- fixed `20` Hz tick rate
- single global in-memory match
- automatic `blue` / `red` team assignment on connect
- balanced randomized team reassignment on `resetGame`
- 5-minute match clock owned by the server
- match-finished state when the timer reaches zero
- winner/loser/tie calculation from final scores
- movement driven by discrete directional input
- collisions against:
  - perimeter
  - rectangles
  - circles
  - polygons
- soft separation between overlapping players

### Spawn rules

- red players spawn in the upper-center area of the map
- blue players spawn in the lower-center area of the map
- the server first tries the preferred team zone
- if the ideal area is blocked, it searches nearby collision-free points
- if the preferred zone is unavailable, it searches the corresponding team half
- the legacy flag-adjacent spawn is used only as a final fallback
- spawn checks avoid hard world collisions and occupied players
- spawn zones are procedural and are not currently stored in `map.json`

### Objective and rules

- each team owns a home flag
- a player can pick up the enemy flag by moving close to it
- a player carrying the enemy flag can still return their own dropped flag by touching it
- a team scores when a player brings the enemy flag back while the home flag is at base
- if a player dies while carrying a flag, the flag drops at the elimination point
- if a player disconnects while carrying a flag, that flag is reset to base
- `resetGame` fully resets scores, flags, inputs, cooldowns, shots, effects, timer, teams, and player positions
- `resetGame` can be used during an active match or after a match has finished

### Combat

- fully authoritative shooting
- fixed range of `420` world units
- shot cooldown of `0.25` seconds
- ephemeral shot traces (`shots`)
- ephemeral hit events (`events[].type === "playerHit"`)
- immediate respawn on hit
- **friendly fire is enabled**: the server does not filter teammates when resolving hits

### Network reliability

The backend uses a non-blocking WebSocket broadcast model:

- the game loop builds one state payload per tick
- each connected client has its own bounded outbound queue
- each connected client has a dedicated writer task
- the game loop enqueues snapshots instead of awaiting every `SendAsync`
- if a client is slow, the queue drops the oldest outbound state instead of freezing the match
- each WebSocket send has a 2-second timeout
- clients with failed or timed-out writers are removed without stopping the match
- incoming WebSocket messages are limited to 16 KB
- invalid JSON and unexpected message-processing failures are logged instead of being silently discarded

### Logging

`Program.cs` registers `LocalFileLoggerProvider`, which writes to:

```text
<exe-folder>/log.txt
```

The logger records:

- startup and game-loop lifecycle messages
- map persistence errors
- client connect/remove events
- WebSocket receive and writer failures
- rejected oversized or invalid WebSocket messages
- unexpected game-loop tick exceptions
- match reset and match finished events

No log rotation is implemented yet.

## Actual folder structure

```text
server/
  Data/
    map.json
  Properties/
    launchSettings.json
  GameHost.cs
  Geometry.cs
  LocalFileLoggerProvider.cs
  Models.cs
  Program.cs
  TheFlag.Server.csproj
  TheFlag.Server.sln
  icon.ico
  README.md
```

## Running locally

### Requirements

- Windows 10/11
- .NET SDK compatible with the project target framework
- modern browser

### Start the server

From this folder:

```powershell
dotnet run --project .\TheFlag.Server.csproj
```

The code currently binds to:

```text
http://0.0.0.0:5770
```

### Useful URLs

- health: `http://127.0.0.1:5770/health`
- map: `http://127.0.0.1:5770/api/map`
- WebSocket: `ws://127.0.0.1:5770/ws`
- PWA client served by the backend: `http://127.0.0.1:5770/pwa/`

## Important frontend note

`Program.cs` tries to serve two clients:

- `../client-web` at the root path `/`
- `../client-pwa` at `/pwa`

In this workspace, **`client-web` does not exist**, so the active client currently available through the backend is the PWA under `/pwa/`.

## Current HTTP API

### `GET /health`

Returns basic server status:

```json
{
  "status": "ok",
  "players": 0,
  "tickRate": 20,
  "map": "map.json"
}
```

### `GET /api/map`

Returns the raw JSON of the map currently loaded in memory.

- actual source: `server/Data/map.json`
- `Content-Type`: `application/json`

### `PUT /api/map`

Replaces the full map as long as no players are connected.

- requires the full map JSON
- validates minimum structure before persisting
- persists to `server/Data/map.json`
- resets scores, shot traces, hit effects, and match clock
- returns `409 Conflict` if players are currently connected

Example success response:

```json
{
  "ok": true,
  "message": "Map updated successfully on the server.",
  "mapName": "Blaze Field",
  "objectCount": 54
}
```

Example conflict response:

```json
{
  "ok": false,
  "message": "The map cannot be replaced while players are connected. Disconnect everyone and try again."
}
```

## Current WebSocket contract

### Client -> server

```json
{ "type": "hello", "name": "Player1" }
{ "type": "input", "up": false, "down": true, "left": false, "right": true }
{ "type": "shoot" }
{ "type": "ping", "nonce": 123 }
{ "type": "resetGame" }
```

Details:

- `hello` changes the visible player name
- names are truncated to 24 characters
- `input` updates the current directional state while the match is running
- `shoot` queues a shot for the next simulation tick while the match is running
- `ping` is answered with `pong`
- `resetGame` starts a fresh match for everyone and reassigns teams
- movement and shooting are ignored after the match has finished until `resetGame` is received

### Server -> client

Initial message:

```json
{
  "type": "welcome",
  "playerId": "p-123",
  "team": "blue",
  "tickRate": 20,
  "mapName": "Blaze Field"
}
```

State snapshot:

```json
{
  "type": "state",
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
  "events": []
}
```

Finished match snapshot example:

```json
{
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

Confirmed hits are exposed as ephemeral events:

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

This lets the client render visual feedback while still relying on the server's authoritative result.

## Map structure expected by the server

The backend deserializes the document into `MapDocument` and requires:

- at least one object
- exactly one valid `perimeter` with `points`
- exactly two `flag` objects
- one `blue` flag
- one `red` flag

It also consumes these obstacle types:

- `rect`
- `circle`
- `polygon`

`meta.canvas.width` and `meta.canvas.height` define the logical world size.

Spawn zones are not part of the current map schema. They are calculated by the backend from the canvas dimensions and team color.

## Included map

The repository currently ships with:

- name: `Blaze Field`
- canvas: `1800 x 950`
- generated at: `2026-04-21T11:35:31.337Z`

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

## Real limitations today

- no persistent match storage
- no authentication
- no multiple rooms
- no replay or history
- no AI or bots
- no explicit spawn points in the JSON
- no friendly-fire filter
- no advanced interpolation or reconciliation in the backend
- no hot map reload while players are connected
- no log rotation for `log.txt`

## Relationship with the editor

The intended current workflow is:

1. the editor loads the map through `GET /api/map`
2. the user modifies the document
3. the editor validates minimum structure and warnings
4. the editor saves through `PUT /api/map`
5. the server replaces `server/Data/map.json` only when no clients are active

## Operational notes

- match state is lost when the process restarts
- `Making/map.json`, if present in older working copies, is not the runtime source of truth
- `launchSettings.json` contains Visual Studio URLs, but the actual code uses `UseUrls("http://0.0.0.0:5770")`
- `log.txt` is created in `AppContext.BaseDirectory`, which is normally the folder that contains the executable or published app binaries

## Author

**David Jorge Aguirre Grazio**  
Developer
