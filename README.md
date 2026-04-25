# THE FLAG

2D multiplayer **capture the flag** prototype built with:

- a playable HTML, CSS, and JavaScript PWA client
- an authoritative C# ASP.NET Core backend using WebSockets
- a web-based map editor
- a JSON-based map format
- PWA support for desktop and mobile installation
- deployment support behind Nginx under `/theflag/`

## Overview

**THE FLAG** is a top-down multiplayer capture-the-flag prototype. The server owns the authoritative match state, while clients connect over WebSocket to receive live snapshots and send player intent.

The project also includes a dedicated map editor that can create and validate arenas, export them to JSON, and sync them directly with the backend.

## Current project structure

The repository currently contains three main modules:

1. `server/`
   .NET backend that simulates the match, exposes HTTP endpoints, accepts WebSocket connections, logs locally, and serves the PWA client under `/pwa/` when available.
2. `client-pwa/`
   Active playable frontend. It is a static frontend with no build pipeline.
3. `client-pwa/editor/`
   Map editor compatible with both the backend and the playable client.

The root also includes `nginx.conf`, with the current reverse-proxy routing for `/theflag/`, `/theflag/api/`, and `/theflag/ws` while preserving the other services already configured in the server.

## Main features

### Gameplay

- real-time multiplayer match
- automatic `blue` / `red` team assignment on connect
- full team reassignment on **Reset match**
- 5-minute match timer controlled by the backend
- match-finished state with winner, loser, tie support, and final scores
- desktop keyboard movement
- mobile touch controls
- authoritative shooting
- friendly fire enabled
- shot cooldown
- automatic respawn after being hit
- collision-aware spawn zones:
  - red players spawn around the upper-center area of the map
  - blue players spawn around the lower-center area of the map
  - fallback search avoids obstacles, perimeter collisions, and occupied player positions
- flag capture, carry, drop, return, and score
- players can return their own dropped flag even while carrying the enemy flag
- synchronized match reset available at any time, including after the match ends
- basic ping measurement

### World and collisions

- hard perimeter for the map
- `rect`, `circle`, and `polygon` obstacles
- player collision against the environment
- soft player-to-player separation
- JSON-configurable arena layout

### Backend reliability

- authoritative fixed-rate simulation at 20 Hz
- non-blocking WebSocket broadcast using one outbound queue per client
- one dedicated WebSocket writer per client
- outbound queue uses drop-oldest behavior so slow clients do not freeze the global game loop
- send timeout per client
- incoming WebSocket message size limit of 16 KB
- local `log.txt` written next to the executable
- improved exception logging in receive, send, map persistence, client cleanup, and game-loop paths

### PWA client

- installable as an app
- service worker for app-shell caching
- desktop and mobile support
- portrait-mode minimap
- responsive UI
- HUD scoreboard for both teams
- centered countdown timer between score bubbles
- compact translucent top-edge score/timer HUD for mobile visibility
- final-result overlay after the timer reaches zero
- connection watchdog that reconnects when state snapshots stop arriving
- automatic local team/accent refresh when the backend reassigns teams after reset

### Map editor

- perimeter creation and editing
- rectangle, circle, and polygon creation
- blue and red flag placement
- fine vertex and corner editing
- zoom, pan, and grid snap
- JSON import and export
- load current map from the server
- save updated map back to the server
- basic validation before publishing

## Architecture

### Backend

The backend lives in `server/` and currently:

- loads the map from `server/Data/map.json`
- keeps a single global match in memory
- owns scores, flags, players, timer, shots, hit effects, and match-finished state
- exposes:
  - `GET /health`
  - `GET /api/map`
  - `PUT /api/map`
  - `WS /ws`
- serves the active PWA client at:
  - `http://localhost:5770/pwa/`

### Client

The active client lives in `client-pwa/` and:

- loads the map through `GET /api/map`
- connects to the backend over WebSocket
- renders the arena and players on a 2D canvas
- processes `shots`, `events`, scores, timer, flags, and match status emitted by the server
- sends player input, shooting, ping, and reset requests

### Editor

The editor lives in `client-pwa/editor/` and:

- runs as a static site
- can be opened directly from `index.html`
- can load the current map with `GET /api/map`
- can save the full map with `PUT /api/map`

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
    GameHost.cs
    Geometry.cs
    LocalFileLoggerProvider.cs
    Models.cs
    Program.cs
    README.md
    TheFlag.Server.csproj
  nginx.conf
  README.md
```

## Tech stack

- C#
- ASP.NET Core Minimal API
- WebSocket
- HTML
- CSS
- JavaScript
- Canvas 2D
- JSON for map definition
- Nginx reverse proxy for production routing

## Running the project

### Requirements

- Windows 10/11
- .NET SDK compatible with the project target framework
- modern browser

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

### 3. Open the editor

You can open:

```text
client-pwa/editor/index.html
```

or serve it as a static site and connect it to the backend.

## Game controls

### Desktop

- `W`, `A`, `S`, `D` or arrow keys to move
- `Space` or left click to shoot

### Mobile

- virtual joystick on the left side
- shoot by tapping the right half

## Match flow

1. Players connect and are assigned to blue/red teams.
2. The backend starts or continues the active 5-minute match.
3. Red players spawn near the upper-center area; blue players spawn near the lower-center area.
4. Players can steal the enemy flag and must return it while their own flag is at base.
5. If a player is hit while carrying a flag, the flag drops at the elimination point.
6. A player can return their own dropped flag even while carrying the enemy flag.
7. When the timer reaches zero, the server freezes the match and reports the winner or tie.
8. Any connected player can press **Reset match** to start a new full match.

## Useful endpoints

### HTTP

- `GET /health`
- `GET /api/map`
- `PUT /api/map`

### WebSocket

- `WS /ws`

## Deployment notes

For the included Nginx setup:

- frontend URL: `https://server.mcrenox.com/theflag/`
- API URL: `https://server.mcrenox.com/theflag/api/...`
- WebSocket URL: `wss://server.mcrenox.com/theflag/ws`
- backend upstream: `http://127.0.0.1:5770`

The PWA detects the `/theflag` public base path and routes HTTP/WebSocket requests through that prefix.

## Recommended workflow

1. Start the backend.
2. Open the editor and load the current map.
3. Edit the arena.
4. Save the updated map to the server.
5. Open the PWA client and test the match.
6. For production, serve the backend behind Nginx using the included `/theflag/` routes.

## Current limitations

- single global match only
- no authentication
- no persistent match storage
- no multiple rooms
- no bots or AI
- spawn zones are computed by the server, not configured in the map JSON
- no account or matchmaking system
- no modern frontend build pipeline

## Current map state

The included map is currently:

- name: `Blaze Field`
- size: `1800 x 950`
- format: JSON

The runtime uses:

```text
server/Data/map.json
```

## Internal documentation

For more module-specific details:

- `server/README.md`
- `client-pwa/README.md`
- `client-pwa/editor/README.md`

## Suggested roadmap

- multiple rooms
- map-configurable spawn zones
- better interpolation and reconciliation
- account or player identity system
- richer UI and HUD
- frontend packaging or build pipeline
- production monitoring and log rotation

## Author

**David Jorge Aguirre Grazio**  
Developer
