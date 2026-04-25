# THE FLAG PWA

Playable frontend for **THE FLAG**. This folder contains the current game client, built as a static HTML, CSS, and JavaScript app with **PWA** support for desktop and mobile installation.

## Overview

`client-pwa/` is the active frontend of the project. It connects to the authoritative backend over WebSocket, loads the map over HTTP, and renders the match on a 2D canvas.

There is no framework and no build pipeline. The frontend lives entirely in static files that can be served directly or opened in the browser.

## Current capabilities

The app currently includes:

- backend connection through WebSocket
- map loading through `GET /api/map`
- arena rendering in Canvas 2D
- rendering for players, flags, shot traces, and hit effects
- side drawer with match information
- desktop and mobile controls
- live ping measurement
- connection watchdog and automatic reconnect when state snapshots stop arriving
- match reset button usable at any time
- 5-minute countdown timer in the top HUD
- final-result overlay with winner/loser/tie and scores
- installable PWA support
- service worker for app-shell caching
- responsive layout with portrait-specific mobile behavior

## Main files

```text
client-pwa/
  editor/
  icons/
  app.js
  index.html
  logoweb.png
  manifest.webmanifest
  styles.css
  sw.js
```

## Runtime flow

### Initial load

On startup, the app:

1. resolves backend configuration from the URL and current origin
2. loads the map with `GET /api/map`
3. initializes the UI, canvas, and event handlers
4. tries to register the service worker
5. waits for the player to connect

### Match connection

When the player clicks **Connect**:

1. it opens a WebSocket to `/ws`
2. it sends `hello` with the player name
3. it starts sending `input`
4. it receives `welcome`
5. it processes `state` snapshots with players, scores, flags, shots, events, and match timer data
6. it starts the watchdog that expects regular state snapshots from the backend

## Features

### Gameplay

- 8-direction movement
- authoritative shooting
- own-team visual identification
- blue/red scoreboard
- centered match timer between score bubbles
- compact translucent top-edge score/timer HUD to reduce obstruction on mobile
- player names
- shot cooldown display support
- real-time match state updates
- final-result overlay when the server marks the match as finished
- **Reset Match** sends `resetGame`, which resets scores, flags, timer, teams, and positions on the backend
- local player team/accent updates when the backend reassigns teams after reset

### Visual effects

- glowing shot traces
- impact sparks on walls and obstacles
- hit explosions on confirmed `playerHit` events
- team-colored canvas accent

### UI

- slide-out side menu
- connection status
- live ping display
- editable player name
- connect / disconnect button
- PWA install button when supported
- **Reset Match** button in the side menu
- **Reset Match** button in the final-result overlay
- timer changes style when the match is finished

### Responsive and mobile

- dedicated touch controls
- virtual joystick on the left
- firing zone on the right
- layout adapted to smaller screens
- portrait mode uses a vertical camera plus minimap to preserve world awareness

### Connection watchdog

The client tracks the time of the last `state` snapshot. If no state arrives for several seconds while the socket still appears open, the client closes the WebSocket and lets the normal reconnect flow create a new connection.

This protects the UI from staying frozen forever if the network, proxy, or socket enters a half-open state.

## Controls

### Desktop

- `W`, `A`, `S`, `D`
- arrow keys
- `Space` to shoot
- left click to shoot

### Mobile

- virtual joystick on the left half
- shoot by tapping the right half

## Runtime configuration

The app automatically detects how to reach the backend:

- if it runs under `http` or `https`, it uses the current origin
- if it is opened as `file:///`, it defaults to:

```text
http://127.0.0.1:5770
```

### Supported query parameters

- `server` or `apiBase`
- `basePath` or `publicPath`

Examples:

```text
file:///C:/path/client-pwa/index.html?server=http://127.0.0.1:5770
```

```text
https://example.com/theflag/?server=https://example.com&basePath=/theflag
```

When published behind Nginx under `/theflag/`, the client can use:

```text
https://server.mcrenox.com/theflag/
```

and it will route API and WebSocket traffic through the same public prefix.

## Endpoints consumed by the app

### HTTP

- `GET /api/map`

### WebSocket

- `WS /ws`

When deployed under `/theflag/`, these effectively become:

- `GET /theflag/api/map`
- `WS /theflag/ws`

## Network messages

### Client -> server

```json
{ "type": "hello", "name": "Player" }
{ "type": "input", "up": false, "down": true, "left": false, "right": true }
{ "type": "shoot" }
{ "type": "ping", "nonce": 1 }
{ "type": "resetGame" }
```

### Server -> client

```json
{
  "type": "welcome",
  "playerId": "p-123",
  "team": "blue",
  "tickRate": 20,
  "mapName": "Blaze Field"
}
```

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

```json
{
  "type": "pong",
  "nonce": 1
}
```

The client uses the `match` object to render the countdown timer and the final-result overlay.

## Match timer and result display

The server controls the timer. The frontend only displays the `remainingMs` value sent in `state.match`.

- default duration: `300` seconds
- display format: `M:SS`
- when `status` becomes `finished`, movement and shooting are no longer useful until reset
- final overlay shows:
  - winner team
  - loser team
  - tie state if applicable
  - final Blue/Red score
  - reset button to start a new match

## PWA support

The app includes:

- `manifest.webmanifest`
- icons in `icons/`
- `sw.js`
- `beforeinstallprompt` handling

The service worker caches the app shell:

- `index.html`
- `styles.css`
- `app.js`
- `logoweb.png`
- `manifest.webmanifest`
- main icons

It does not cache:

- `/api/`
- `/ws`

This keeps the static shell available offline while gameplay still depends on the live backend.

## How to test it

### Option 1: through the backend

If you run `server/`, the backend serves this app at:

```text
http://127.0.0.1:5770/pwa/
```

This is the easiest way to test it.

### Option 2: as a static site

You can serve `client-pwa/` with any static file server and point it to the backend with query parameters if needed.

It can also be opened as a local file, although PWA behavior depends on the browser and secure-context rules.

## App installation

The **Install App** option appears when:

- the browser supports the PWA install prompt
- the app is not already installed
- it is running in a valid context

On localhost or HTTPS, it can be installed as a standalone app.

## Map integration

The app uses the map JSON to:

- get the logical world width and height
- draw the perimeter
- draw obstacles
- place flags
- adjust camera framing and scene presentation

Spawn zones are not drawn from map JSON. They are computed by the backend and reflected through player positions in the live state snapshots.

## Related folders

- `editor/`: the project's map editor
- `icons/`: current PWA icons

## Current limitations

- no chat
- no authentication
- no room selection
- no player visual customization
- no mouse aim independent from movement
- no advanced interpolation
- gameplay still depends on the backend; offline only preserves the static shell

## Recommended project usage

1. Run the backend in `server/`
2. Open `http://127.0.0.1:5770/pwa/`
3. Connect one or more clients
4. Play until the timer ends, or press **Reset Match** to restart immediately
5. Optionally edit the map from `client-pwa/editor/`

## Related documentation

- [Root README](../README.md)
- [Server README](../server/README.md)
- [Editor README](./editor/README.md)

## Author

**David Jorge Aguirre Grazio**  
Developer
