# THE FLAG

2D multiplayer **capture the flag** prototype built with:

- a playable HTML, CSS, and JavaScript client
- an authoritative C# ASP.NET Core backend using WebSockets
- a web-based map editor
- a JSON-based map format
- PWA support for desktop and mobile installation

## Overview

**THE FLAG** is a simple top-down multiplayer prototype focused on a clear capture-the-flag gameplay loop. The server owns the real match state, while clients connect over WebSocket to receive live game snapshots and send player intent.

The project also includes a dedicated map editor that can create and validate arenas, export them to JSON, and sync them directly with the backend.

## Current project structure

The repository currently contains three main modules:

1. `server/`
   .NET backend that simulates the match, exposes HTTP endpoints, and accepts WebSocket connections.
2. `client-pwa/`
   Active playable frontend. It is a static frontend with no build pipeline.
3. `client-pwa/editor/`
   Map editor compatible with both the backend and the playable client.

## Main features

### Gameplay

- real-time multiplayer match
- automatic `blue` / `red` team assignment
- desktop keyboard movement
- mobile touch controls
- authoritative shooting
- friendly fire enabled
- shot cooldown
- automatic respawn
- flag capture, carry, drop, and return
- scoring on completed captures
- synchronized match reset
- basic ping measurement

### World and collisions

- hard perimeter for the map
- `rect`, `circle`, and `polygon` obstacles
- player collision against the environment
- soft player-to-player separation
- JSON-configurable arena layout

### PWA client

- installable as an app
- service worker for app-shell caching
- desktop and mobile support
- portrait-mode minimap
- responsive UI

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
- processes `shots` and `events` emitted by the server

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
  Graphics/
  Making/
    map.json
    plan_editor_escenario.md
    plan_juego_captura_bandera.md
  server/
    Data/
      map.json
    GameHost.cs
    Geometry.cs
    Models.cs
    Program.cs
    README.md
    TheFlag.Server.csproj
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

## Running the project

### Requirements

- Windows 10/11
- .NET SDK compatible with `net9.0`
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

## Useful endpoints

### HTTP

- `GET /health`
- `GET /api/map`
- `PUT /api/map`

### WebSocket

- `WS /ws`

## Recommended workflow

1. Start the backend.
2. Open the editor and load the current map.
3. Edit the arena.
4. Save the updated map to the server.
5. Open the PWA client and test the match.

## Current limitations

- single global match only
- no authentication
- no persistent match storage
- no multiple rooms
- no bots or AI
- no explicit spawn points in the map format
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

`Making/map.json` is a convenience copy, not the runtime source of truth.

## Internal documentation

For more module-specific details:

- `server/README.md`
- `client-pwa/README.md`
- `client-pwa/editor/README.md`

## Suggested roadmap

- multiple rooms
- explicit team spawns
- better interpolation and reconciliation
- account or player identity system
- richer UI and HUD
- frontend packaging or build pipeline
- production deployment behind a reverse proxy and domain

## Author

**David Jorge Aguirre Grazio**  
Developer
