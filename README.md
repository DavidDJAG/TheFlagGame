# THE FLAG

2D multiplayer **capture the flag** prototype built with:

- a playable HTML, CSS, and JavaScript PWA client;
- an authoritative C# ASP.NET Core backend using WebSockets;
- independent multi-room game sessions;
- a web-based map editor;
- a JSON-based map format;
- PWA support for desktop and mobile installation;
- deployment support behind Nginx under `/theflag/`.

## Overview

**THE FLAG** is a top-down multiplayer capture-the-flag prototype. The server owns the authoritative game simulation, while clients connect over WebSocket to send player intent and receive live state snapshots.

The game now supports multiple independent rooms. Each room behaves as a separate match with isolated players, teams, scores, flags, shots, timer, events, and reset flow. Clients can join the default `public` room or specify a room such as `alpha`, `test-01`, or any other valid room ID.

The project also includes a dedicated map editor that can create and validate arenas, export them to JSON, and sync them with the backend when no active players are connected.

## Current project structure

The repository contains three main modules:

1. `server/`  
   Authoritative .NET backend that manages rooms, simulates matches, exposes HTTP endpoints, accepts WebSocket connections, logs locally, loads the base map, and serves the PWA client under `/pwa/` when available.

2. `client-pwa/`  
   Active playable frontend. It is a static PWA with no build pipeline. It lets players choose a room and player name before connecting.

3. `client-pwa/editor/`  
   Static map editor compatible with both the backend and the playable client.

The root also includes `nginx.conf`, with reverse-proxy routing for `/theflag/`, `/theflag/api/`, and `/theflag/ws`.

## Main features

### Gameplay

- real-time multiplayer capture-the-flag matches;
- independent rooms with isolated match state;
- default room named `public`;
- automatic room creation for valid room IDs;
- automatic `blue` / `red` team assignment on connect;
- full team reassignment on **Reset match**;
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
- basic ping measurement.

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
- WebSocket connection through `/ws?room=<roomId>`;
- portrait-mode minimap;
- responsive UI;
- HUD scoreboard for both teams;
- centered countdown timer between score bubbles;
- compact translucent top-edge score/timer HUD for mobile visibility;
- final-result overlay after the timer reaches zero;
- connection watchdog that reconnects when state snapshots stop arriving;
- automatic local team/accent refresh when the backend reassigns teams after reset.

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
  ├── joins a room over WebSocket: /ws?room=<roomId>
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
- defaults to `public` if no room is provided;
- supports `?room=<roomId>` in the page URL;
- connects to the backend over WebSocket using `/ws?room=<roomId>`;
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

Use the **Room** field to choose the room before connecting. The default value is:

```text
public
```

You can also preselect a room with:

```text
http://127.0.0.1:5770/pwa/?room=alpha
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

1. A player selects a room and enters a player name.
2. The PWA connects to `/ws?room=<roomId>`.
3. The backend creates the room automatically if it is valid and there is room capacity.
4. Players connected to the same room are assigned to blue/red teams.
5. The backend starts or continues that room's 5-minute match.
6. Red players spawn near the upper-center area; blue players spawn near the lower-center area.
7. Players can steal the enemy flag and must return it while their own flag is at base.
8. If a player is hit while carrying a flag, the flag drops at the elimination point.
9. A player can return their own dropped flag even while carrying the enemy flag.
10. When the timer reaches zero, the server freezes that room's match and reports the winner or tie.
11. Any connected player can press **Reset match** to start a new full match for that room only.

## Useful endpoints

### HTTP

- `GET /health`  
  Returns global server health, active room count, total players, room limits, tick rate, and map information.

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

Example:

```text
ws://127.0.0.1:5770/ws?room=alpha
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
- no bots or AI;
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
