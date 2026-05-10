# THE FLAG

2D multiplayer **capture the flag** prototype built with:

- a playable HTML, CSS, and JavaScript PWA client;
- an authoritative C# ASP.NET Core backend using WebSockets;
- independent multi-room game sessions;
- a web-based map editor;
- a JSON-based map format;
- PWA support for desktop and mobile installation;
- player-side team selection and spectator mode;
- training-oriented runtime mode for AI data generation;
- deployment support behind Nginx under `/theflag/`.

## Overview

**THE FLAG** is a top-down multiplayer capture-the-flag prototype. The server owns the authoritative game simulation, while clients connect over WebSocket to send player intent and receive live state snapshots.

The game now supports multiple independent rooms. Each room behaves as a separate match with isolated players, teams, scores, flags, shots, timer, events, and reset flow. Clients can join the default `public` room or specify a room such as `alpha`, `test-01`, or any other valid room ID.

The project also includes a dedicated map editor that can create and validate arenas, export them to JSON, and sync them with the backend when no active players are connected. The playable client also supports selecting a preferred team (`auto`, `blue`, or `red`) and joining as a spectator. The backend persists each player's requested team so fixed `blue` / `red` selections are respected across match resets, while `auto` players are rebalanced.

## Current project structure

The repository contains three main modules:

1. `server/`  
   Authoritative .NET backend that manages rooms, simulates matches, exposes HTTP endpoints, accepts WebSocket connections, logs locally, loads the base map, supports spectator clients, applies team-selection preferences, exposes training telemetry when enabled, and serves the PWA client under `/pwa/` when available.

2. `client-pwa/`  
   Active playable frontend. It is a static PWA with no build pipeline. It lets players choose a room, preferred team, spectator mode, and player name before connecting.

3. `client-pwa/editor/`  
   Static map editor compatible with both the backend and the playable client.

The root also includes `nginx.conf`, with reverse-proxy routing for `/theflag/`, `/theflag/api/`, and `/theflag/ws`.

## Main features

### Gameplay

- real-time multiplayer capture-the-flag matches;
- independent rooms with isolated match state;
- default room named `public`;
- automatic room creation for valid room IDs;
- player-selectable team preference on connect: `auto`, `blue`, or `red`;
- spectator mode for read-only match observation;
- reset-time team handling that preserves fixed `blue` / `red` choices and only rebalances `auto` players;
- 5-minute match timer controlled by the backend;
- match-finished state with winner, loser, tie support, and final scores;
- desktop keyboard movement;
- mobile touch controls;
- authoritative shooting;
- friendly fire enabled;
- shot cooldown;
- automatic respawn after being hit;
- collision-aware spawn zones:
  - red players spawn around the upper-center area of the map;
  - blue players spawn around the lower-center area of the map;
  - fallback search avoids obstacles, perimeter collisions, and occupied player positions;
- flag capture, carry, drop, return, and score;
- players can return their own dropped flag even while carrying the enemy flag;
- room-scoped match reset available at any time, including after the match ends;
- basic ping measurement;
- optional training telemetry for model-training datasets when the server runs with `trainingMode=true`.

### Multi-room support

- `WS /ws` connects to the default `public` room;
- `WS /ws?room=<roomId>` connects to a specific room;
- `roomId` values are normalized to lowercase;
- valid room IDs match `^[a-z0-9_-]{1,32}$`;
- `MaxPlayersPerRoom = 32`;
- `MaxActiveRooms = 24`;
- empty rooms are cleaned up after a short retention period;
- snapshots are sent only to clients in the same room;
- reset, scoring, flags, shots, hit events, timers, and match-finished state are room-scoped;
- players in different rooms cannot see, collide with, shoot, damage, or affect each other.

### World and collisions

- hard perimeter for the map;
- `rect`, `circle`, and `polygon` obstacles;
- player collision against the environment;
- soft player-to-player separation within the same room;
- JSON-configurable arena layout.

### Backend reliability

- authoritative fixed-rate simulation at 20 Hz per active room;
- non-blocking WebSocket broadcast using one outbound queue per client;
- one dedicated WebSocket writer per client;
- outbound queue uses drop-oldest behavior so slow clients do not freeze a room simulation;
- send timeout per client;
- incoming WebSocket message size limit;
- local `log.txt` written next to the executable;
- origin validation for WebSocket connections;
- CORS restrictions;
- rate-limit and inactivity protections;
- logging for room creation, room removal, connection, disconnection, rejection, reset, match finish, and map replacement flows.

### PWA client

- installable as an app;
- service worker for app-shell caching;
- desktop and mobile support;
- room input shown above **Player name**;
- `public` as the default room;
- support for preselecting a room through `?room=alpha`;
- support for preselecting team and spectator mode through `?team=red` and `?spectator=true`;
- WebSocket connection through `/ws?room=<roomId>`, with optional `team=<blue|red>` or `spectator=true`;
- portrait-mode minimap;
- responsive UI;
- HUD scoreboard for both teams;
- centered countdown timer between score bubbles;
- compact translucent top-edge score/timer HUD for mobile visibility;
- final-result overlay after the timer reaches zero;
- connection watchdog that reconnects when state snapshots stop arriving;
- automatic local team/accent refresh when the backend confirms team changes after reset.

### Map editor

- perimeter creation and editing;
- rectangle, circle, and polygon creation;
- blue and red flag placement;
- fine vertex and corner editing;
- zoom, pan, and grid snap;
- JSON import and export;
- load current map from the server;
- save updated map back to the server;
- basic validation before publishing.

## Architecture

### High-level architecture

```text
Browser / PWA Client
  ├── loads map over HTTP: GET /api/map
  ├── joins a room over WebSocket: /ws?room=<roomId>[&team=<blue|red>|&spectator=true]
  ├── selects room/team/spectator mode via WebSocket query string
  ├── sends player intent: hello, input, shoot, ping, resetGame
  └── renders authoritative state snapshots from the server

ASP.NET Core Server
  ├── Program.cs
  │   ├── HTTP API
  │   ├── WebSocket endpoint
  │   ├── static PWA serving
  │   └── lifecycle wiring
  ├── GameRoomManager
  │   ├── validates and normalizes room IDs
  │   ├── creates rooms on demand
  │   ├── enforces room and player limits
  │   ├── routes WebSocket clients to rooms
  │   ├── lists active rooms
  │   ├── coordinates global map replacement
  │   └── cleans up empty rooms
  ├── GameRoom
  │   ├── owns one independent authoritative match
  │   ├── simulates players, collisions, shooting, flags, score, and timer
  │   ├── broadcasts room-local snapshots
  │   └── handles disconnects and room-local reset
  ├── MapLoader
  │   ├── loads server/Data/map.json
  │   ├── validates the base map
  │   └── provides isolated runtime map state per room
  └── Models / Geometry / Logging
      ├── DTOs and runtime models
      ├── collision primitives
      └── local file logging

Map Editor
  ├── loads the global base map: GET /api/map
  └── replaces the global base map: PUT /api/map, only when no players are connected
```

### Backend

The backend lives in `server/` and:

- loads the global base map from `server/Data/map.json`;
- creates isolated room runtime state from the base map;
- keeps each match in memory inside a `GameRoom`;
- uses `GameRoomManager` as the room registry and routing layer;
- owns scores, flags, players, timer, shots, hit effects, and match-finished state per room;
- exposes HTTP endpoints for health, map management, and room management;
- accepts WebSocket clients through `/ws` and `/ws?room=<roomId>`;
- accepts optional `team=auto|blue|red` and `spectator=true|false` connection preferences;
- stores each player's requested team and preserves fixed `blue` / `red` assignments across `resetGame`;
- can run with `trainingMode=true` to emit event/stat telemetry useful for AI training datasets;
- serves the active PWA client at `http://localhost:5770/pwa/` when `client-pwa/` is available.

### Room isolation model

Every room has its own mutable runtime state:

- players;
- WebSocket clients;
- team assignments;
- positions and inputs;
- scores;
- flags and flag carriers;
- shots;
- hit effects and events;
- match timer;
- match-finished status;
- reset lifecycle.

The base map file is global, but mutable runtime map objects are cloned or rebuilt for each room. This prevents one room from sharing flag position, carrier, dropped-flag, score, shot, or timer state with another room.

### Client

The active client lives in `client-pwa/` and:

- loads the map through `GET /api/map`;
- lets the player enter a room name before connecting;
- lets the player select `auto`, `blue`, or `red` team preference before connecting;
- lets the player enable spectator mode before connecting;
- defaults to `public` if no room is provided;
- supports `?room=<roomId>`, `?team=<auto|blue|red>`, and `?spectator=true` in the page URL;
- connects to the backend over WebSocket using `/ws?room=<roomId>` plus optional team or spectator query parameters;
- renders the arena and players on a 2D canvas;
- processes `shots`, `events`, scores, timer, flags, and match status emitted by the server;
- sends player input, shooting, ping, and reset requests.

### Editor

The editor lives in `client-pwa/editor/` and:

- runs as a static site;
- can be opened directly from `index.html`;
- can load the current global base map with `GET /api/map`;
- can save the full map with `PUT /api/map`;
- cannot replace the map while any room has connected players or active clients.

## Repository layout

```text
the_flag_game/
  client-pwa/
    editor/
    icons/
    app.js
    index.html
    manifest.webmanifest
    styles.css
    sw.js
  server/
    Data/
      map.json
    Properties/
      launchSettings.json
    GameHost.cs          # Contains the GameRoom implementation
    GameRoomManager.cs   # Room registry, routing, limits, cleanup, and map coordination
    Geometry.cs
    LocalFileLoggerProvider.cs
    MapLoader.cs
    Models.cs
    Program.cs
    server-runtime.json
    README.md
    TheFlag.Server.csproj
    TheFlag.Server.sln
  nginx.conf
  README.md
```

## Tech stack

- C#;
- ASP.NET Core Minimal API;
- WebSocket;
- HTML;
- CSS;
- JavaScript;
- Canvas 2D;
- JSON for map definition;
- Nginx reverse proxy for production routing.

## Running the project

### Requirements

- Windows 10/11, Linux, or macOS;
- .NET SDK compatible with the project target framework;
- modern browser.

### 1. Start the backend

From `server/`:

```powershell
dotnet run --project .\TheFlag.Server.csproj
```

The server listens on:

```text
http://127.0.0.1:5770
```

### 2. Open the game

Current PWA client:

```text
http://127.0.0.1:5770/pwa/
```

Use the **Room** field to choose the room before connecting, the **Team** selector to choose `Auto`, `Blue`, or `Red`, and **Spectator mode** to observe without creating a player. The default room value is:

```text
public
```

You can also preselect connection options with:

```text
http://127.0.0.1:5770/pwa/?room=alpha
http://127.0.0.1:5770/pwa/?room=alpha&team=red
http://127.0.0.1:5770/pwa/?room=alpha&spectator=true
```

### 3. Open the editor

You can open:

```text
client-pwa/editor/index.html
```

or serve it as a static site and connect it to the backend.

## Game controls

### Desktop

- `W`, `A`, `S`, `D` or arrow keys to move;
- `Space` or left click to shoot.

### Mobile

- virtual joystick on the left side;
- shoot by tapping the right half.

## Match flow

1. A user selects a room, connection mode, and player name.
2. The PWA connects to `/ws?room=<roomId>` with optional `team=<blue|red>` or `spectator=true`.
3. The backend creates the room automatically if it is valid and there is room capacity.
4. Regular players are assigned according to their requested team: fixed `blue` / `red` when requested, otherwise balanced `auto` assignment.
5. Spectators receive the same state snapshots but do not create a player, move, shoot, or reset the match.
6. The backend starts or continues that room's 5-minute match.
7. Red players spawn near the upper-center area; blue players spawn near the lower-center area.
8. Players can steal the enemy flag and must return it while their own flag is at base.
9. If a player is hit while carrying a flag, the flag drops at the elimination point.
10. A player can return their own dropped flag even while carrying the enemy flag.
11. When the timer reaches zero, the server freezes that room's match and reports the winner or tie.
12. Any connected non-spectator player can press **Reset match** to start a new full match for that room only; fixed team choices are preserved and `auto` players are rebalanced.

## Useful endpoints

### HTTP

- `GET /health`  
  Returns global server health, active room count, total players, room limits, tick rate, map information, and effective training runtime settings.

- `GET /api/map`  
  Returns the global base map JSON used by new rooms and the editor.

- `PUT /api/map`  
  Replaces the global base map only when no players or clients are connected in active rooms. Returns `409 Conflict` if the map cannot be safely replaced.

- `GET /api/rooms`  
  Lists active rooms, total players, room limits, and room summaries.

- `POST /api/rooms`  
  Creates a room explicitly when the room ID is valid and `MaxActiveRooms` has not been reached.

### WebSocket

- `WS /ws`  
  Connects to the default `public` room.

- `WS /ws?room=<roomId>`  
  Connects to a specific room.

- `WS /ws?room=<roomId>&team=<auto|blue|red>`  
  Connects as a regular player with a team preference. `auto` may be omitted.

- `WS /ws?room=<roomId>&spectator=true`  
  Connects as a spectator. Spectators receive snapshots but cannot play or reset.

Examples:

```text
ws://127.0.0.1:5770/ws?room=alpha
ws://127.0.0.1:5770/ws?room=alpha&team=blue
ws://127.0.0.1:5770/ws?room=alpha&spectator=true
```

## Room naming rules

Room IDs are normalized by the backend:

- leading and trailing spaces are removed;
- values are converted to lowercase;
- empty or missing values become `public`.

Valid room IDs must match:

```regex
^[a-z0-9_-]{1,32}$
```

Examples:

```text
public
alpha
test-01
team_blue
```

## Map replacement rules

The map editor works against the global base map:

- `GET /api/map` returns `server/Data/map.json`;
- `PUT /api/map` replaces `server/Data/map.json` only when no room has connected players or active WebSocket clients.

This avoids mixing a new base map with already-running room state. After a map replacement, newly created rooms use the updated map.


## Training-oriented runtime mode

The server reads `server-runtime.json` from `AppContext.BaseDirectory`. It is fail-safe by default: when the file is missing or when `trainingMode` is `false`, production runtime settings are used and training-oriented values are ignored.

When `trainingMode=true`, the server can accelerate or segment simulations for AI-data collection and emits telemetry suitable for synthetic training datasets:

- top-level `sequence` and `matchId` in every state snapshot;
- `match.id` for episode correlation;
- populated `events` for match, combat, flag, join/leave, and reset events;
- populated `playerStats` with shots, hits, eliminations, deaths, flag actions, carry time, and travelled distance;
- optional faster simulation through `tickRate`, `timeScale`, `runAsFastAsPossible`, and physics substep limits;
- optional training conveniences such as `resetCooldownSeconds=0`, `autoResetFinishedMatches=true`, disabled idle timeout, and disabled inbound rate limit.

Important: `trainingMode` does not create AI players by itself. It prepares the authoritative simulation and telemetry stream so external bot clients or model-training harnesses can generate and collect data.

Example training-oriented runtime file:

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

## Deployment notes

For the included Nginx setup:

- frontend URL: `https://server.mcrenox.com/theflag/`;
- API URL: `https://server.mcrenox.com/theflag/api/...`;
- WebSocket URL: `wss://server.mcrenox.com/theflag/ws?room=public`;
- backend upstream: `http://127.0.0.1:5770`.

The PWA detects the `/theflag` public base path and routes HTTP/WebSocket requests through that prefix.

Example room-specific public URL:

```text
https://server.mcrenox.com/theflag/?room=alpha
```

## Recommended workflow

1. Start the backend.
2. Open the editor and load the current map.
3. Edit the arena.
4. Save the updated map to the server while no players are connected.
5. Open the PWA client.
6. Enter a room name or keep `public`.
7. Connect multiple clients to the same room to test shared gameplay.
8. Connect another client to a different room to verify isolation.
9. For production, serve the backend behind Nginx using the included `/theflag/` routes.

## Current limitations

- no authentication;
- no persistent match storage;
- no account or matchmaking system;
- no room browser in the PWA yet;
- no built-in AI-controlled bots yet; training telemetry exists for external AI/model pipelines;
- spawn zones are computed by the server, not configured in the map JSON;
- maps are global, not room-specific;
- the map editor cannot modify a map while active players are connected;
- no modern frontend build pipeline.

## Current map state

The included map is currently:

- name: `Blaze Field`;
- size: `1800 x 950`;
- format: JSON.

The runtime uses:

```text
server/Data/map.json
```

## Internal documentation

For more module-specific details:

- `server/README.md`;
- `client-pwa/README.md`;
- `client-pwa/editor/README.md`.

## Suggested roadmap

- room browser using `GET /api/rooms`;
- export pipeline for training telemetry datasets;
- bot/client harness that consumes `trainingMode` telemetry;
- private/public room visibility;
- shareable room links in the UI;
- map-configurable spawn zones;
- room-specific map selection;
- better interpolation and reconciliation;
- account or player identity system;
- richer UI and HUD;
- frontend packaging or build pipeline;
- production monitoring and log rotation.

## Author

**David Jorge Aguirre Grazio**  
Developer
