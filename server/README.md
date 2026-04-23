# THE FLAG Server

Authoritative backend for the **THE FLAG** multiplayer prototype. This folder contains the ASP.NET Core minimal API server that:

- loads the map from `server/Data/map.json`
- keeps the state of a single in-memory match
- simulates movement, collisions, flags, shooting, and respawn
- exposes `GET /health`, `GET /api/map`, `PUT /api/map`, and `WS /ws`
- serves the current PWA client under `/pwa` when the `client-pwa` folder exists

## Current state

The project currently consists of three main pieces:

1. `server/`
   .NET 9 backend with WebSocket and authoritative simulation.
2. `client-pwa/`
   Playable HTML/CSS/JavaScript frontend. This is the active client.
3. `client-pwa/editor/`
   Static editor used to create or modify `map.json` and sync it with the server.

Additional material in the repository:

- `Making/map.json`: convenience copy of the current map. The runtime still uses `server/Data/map.json`.
- `Graphics/`: logos, icons, and historical variants.
- `client-pwa/old/`: legacy client kept for reference.
- `Making/plan_editor_escenario.md`, `Making/plan_juego_captura_bandera.md`, `Making/scene.png`: planning notes and visual support files.

## What the backend currently does

### Match simulation

- fixed `20` Hz tick rate
- single global in-memory match
- automatic `blue` / `red` team assignment
- initial spawn calculated around the team's own flag
- respawn around the original spawn when the preferred point is occupied
- movement driven by discrete directional input
- collisions against:
  - perimeter
  - rectangles
  - circles
  - polygons
- soft separation between overlapping players

### Objective and rules

- each team owns a home flag
- a player can pick up the enemy flag by moving close to it
- the home flag can be returned if it was dropped
- a team scores when a player brings the enemy flag back while the home flag is at base
- if a player dies while carrying a flag, the flag drops at the elimination point
- `resetGame` resets scores, flags, inputs, cooldowns, and positions

### Combat

- fully authoritative shooting
- fixed range of `420` world units
- shot cooldown of `0.25` seconds
- ephemeral shot traces (`shots`)
- ephemeral hit events (`events[].type === "playerHit"`)
- immediate respawn on hit
- **friendly fire is enabled**: the server does not filter teammates when resolving hits

### Network state

- `welcome` message on connect with `playerId`, `team`, `tickRate`, and `mapName`
- `state` snapshots containing:
  - `scores`
  - `players`
  - `flags`
  - `shots`
  - `events`
  - `serverTime`
- basic latency support through `ping` and `pong`

## Actual folder structure

```text
server/
  Data/
    map.json
  Properties/
    launchSettings.json
    PublishProfiles/
  GameHost.cs
  Geometry.cs
  Models.cs
  Program.cs
  TheFlag.Server.csproj
  TheFlag.Server.sln
  icon.ico
```

## Running locally

### Requirements

- Windows 10/11
- .NET 9 SDK or newer capable of building `net9.0`
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
- resets scores, shot traces, and hit effects
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
- `input` updates the current directional state
- `shoot` queues a shot for the next simulation tick
- `ping` is answered with `pong`
- `resetGame` resets the match for everyone

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
  "players": [],
  "flags": [],
  "shots": [],
  "events": []
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

## Included map

The repository currently ships with:

- name: `Blaze Field`
- canvas: `1800 x 950`
- generated at: `2026-04-21T11:35:31.337Z`

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

## Relationship with the editor

The intended current workflow is:

1. the editor loads the map through `GET /api/map`
2. the user modifies the document
3. the editor validates minimum structure and warnings
4. the editor saves through `PUT /api/map`
5. the server replaces `server/Data/map.json` only when no clients are active

## Operational notes

- match state is lost when the process restarts
- `Making/map.json` is not the runtime source of truth
- `launchSettings.json` contains Visual Studio URLs, but the actual code uses `UseUrls("http://0.0.0.0:5770")`
- full build validation could not be completed inside this sandbox due to `NuGet.Config` access restrictions, not because of an observed code error

## Author

**David Jorge Aguirre Grazio**  
Developer
