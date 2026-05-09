# THE FLAG PWA

Playable frontend for **THE FLAG**. This folder contains the current game client, built as a static HTML, CSS, and JavaScript app with **PWA** support for desktop and mobile installation.

## Overview

`client-pwa/` is the active frontend of the project. It connects to the authoritative backend over WebSocket, loads the base map over HTTP, and renders the match on a 2D canvas.

There is no framework and no build pipeline. The frontend lives entirely in static files that can be served directly or opened in the browser.

## Current capabilities

The app currently includes:

- backend connection through WebSocket
- room selection before connecting, with `public` as the default room
- client connection options for requested team (`auto`, `blue`, `red`) and spectator mode
- WebSocket room routing through `/ws?room=<roomId>`, optionally adding `team=<blue|red>` or `spectator=true`
- map loading through `GET /api/map`
- arena rendering in Canvas 2D
- rendering for detailed 0.50x top-down player avatars, flags, shot traces, and hit effects
- side drawer with match information
- desktop and mobile controls
- live ping measurement
- connection watchdog and automatic reconnect when state snapshots stop arriving
- match reset button usable at any time, scoped by the backend to the connected room
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
2. resolves the initial room from `?room=`, local storage, or `public`
3. loads the map with `GET /api/map`
4. initializes the UI, canvas, and event handlers
5. tries to register the service worker
6. waits for the player to connect

### Match connection

When the player clicks **Connect**:

1. it normalizes and validates the value in the **Room** field
2. it reads the selected team and spectator settings
3. it opens a WebSocket to `/ws?room=<roomId>` plus only the needed connection settings
4. for regular players, it sends `hello` with the player name and starts sending `input`
5. for spectators, it does not send gameplay input and only observes server snapshots
6. it receives `welcome`, including the server-confirmed `roomId`
7. it processes `state` snapshots with players, scores, flags, shots, events, and match timer data
8. it starts the watchdog that expects regular state snapshots from the backend

### Room selection

The side drawer has **Room**, **Team**, **Spectator mode**, and **Player name** fields.

- The default value is `public`.
- Empty values are normalized back to `public`.
- Room IDs are trimmed and converted to lowercase before connecting.
- The accepted format is `a-z`, `0-9`, `_`, and `-`, up to 32 characters.
- The selected room is stored in local storage after a successful valid entry.
- The room field is locked while the socket is connected, connecting, or reconnecting. Disconnect first to switch rooms.
- The server-confirmed room from `welcome.roomId` is reflected back into the field.

Examples:

```text
public
alpha
test-01
team_blue
```

You can also preselect a room with the URL:

```text
http://127.0.0.1:5770/pwa/?room=alpha
```

### Team selection and spectator mode

The side drawer includes a **Team** selector and a **Spectator mode** checkbox.

Team values:

- `Auto`: preserves backend auto-assignment and does not send a `team` query parameter.
- `Blue`: connects with `team=blue`.
- `Red`: connects with `team=red`.

Spectator mode:

- unchecked: no `spectator` query parameter is sent.
- checked: connects with `spectator=true`.
- when spectator mode is enabled, the team selector is disabled and no gameplay inputs are sent.

The selected team and spectator mode are stored in local storage for convenience. They can also be preselected through the frontend URL:

```text
http://127.0.0.1:5770/pwa/?room=alpha&team=red
```

```text
http://127.0.0.1:5770/pwa/?room=alpha&spectator=true
```

Generated WebSocket examples:

```text
/ws?room=alpha
/ws?room=alpha&team=blue
/ws?room=alpha&team=red
/ws?room=alpha&spectator=true
```

## Features

### Gameplay

- 8-direction movement
- authoritative shooting
- own-team visual identification
- blue/red scoreboard
- centered match timer between score bubbles
- compact translucent top-edge score/timer HUD to reduce obstruction on mobile
- player names
- carried-flag rendering attached to the player avatar when flag-carrier data is present or inferable
- shot cooldown display support
- real-time match state updates with visual interpolation for smoother player movement
- doubled player footstep animation cadence without changing gameplay displacement speed
- final-result overlay when the server marks the match as finished
- **Reset Match** sends `resetGame`, which resets scores, flags, timer, teams, and positions in the current backend room
- local player team/accent updates when the backend reassigns teams after reset

### Multi-room behavior

The frontend joins exactly one backend room per WebSocket connection.

- `/ws` remains compatible with the backend default room, but this client explicitly sends `/ws?room=<roomId>`.
- Players in different rooms do not see or affect each other when the backend multi-room server is used.
- Reset, scores, flags, shots, hits, timer, and match result are room-scoped by the backend.
- The current map is still global and loaded through `GET /api/map`.
- The client does not yet list rooms from `GET /api/rooms`; room names are entered manually.

### Visual effects

- glowing shot traces
- impact sparks on walls and obstacles
- hit explosions on confirmed `playerHit` events
- team-colored canvas accent

### UI

- slide-out side menu
- room field, team selector, spectator-mode checkbox, and player name field
- connection status
- live ping display
- editable player name
- connect / disconnect button
- PWA install button when supported
- **Reset Match** button in the side menu
- **Reset Match** button in the final-result overlay
- timer changes style when the match is finished
- map information line also shows the selected or connected room

### Responsive and mobile

- dedicated touch controls
- virtual joystick on the left
- firing zone on the right
- layout adapted to smaller screens
- portrait mode uses a vertical camera plus minimap to preserve world awareness

### Connection watchdog

The client tracks the time of the last `state` snapshot. If no state arrives for several seconds while the socket still appears open, the client closes the WebSocket and lets the normal reconnect flow create a new connection.

This protects the UI from staying frozen forever if the network, proxy, or socket enters a half-open state. Reconnect attempts keep using the selected room unless the player disconnects and changes it.

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

- `server` or `apiBase`: backend origin
- `basePath` or `publicPath`: public path prefix when the backend is reverse-proxied
- `room`: initial room shown in the Room field
- `team`: initial team selector value; accepted values are `auto`, `blue`, and `red`
- `spectator`: initial spectator-mode value; accepted true values include `true`, `1`, and `yes`

Examples:

```text
file:///C:/path/client-pwa/index.html?server=http://127.0.0.1:5770&room=alpha
```

```text
https://example.com/theflag/?server=https://example.com&basePath=/theflag&room=test-01
```

When published behind Nginx under `/theflag/`, the client can use:

```text
https://server.mcrenox.com/theflag/?room=public
```

and it will route API and WebSocket traffic through the same public prefix.

## Endpoints consumed by the app

### HTTP

- `GET /api/map`

### WebSocket

- `WS /ws?room=<roomId>`
- `WS /ws?room=<roomId>&team=<blue|red>`
- `WS /ws?room=<roomId>&spectator=true`

When deployed under `/theflag/`, these effectively become:

- `GET /theflag/api/map`
- `WS /theflag/ws?room=<roomId>`

## Network messages

### Client -> server

```json
{ "type": "hello", "name": "Player" }
{ "type": "input", "up": false, "down": true, "left": false, "right": true }
{ "type": "shoot" }
{ "type": "ping", "nonce": 1 }
{ "type": "resetGame" }
```

The room, requested team, and spectator mode are not sent as JSON messages. They are selected through the WebSocket URL query string:

```text
/ws?room=alpha
/ws?room=alpha&team=blue
/ws?room=alpha&team=red
/ws?room=alpha&spectator=true
```

When the selector is `Auto`, no `team` parameter is sent. When spectator mode is unchecked, no `spectator` parameter is sent.

### Server -> client

```json
{
  "type": "welcome",
  "roomId": "alpha",
  "playerId": "p-123",
  "team": "blue",
  "spectator": false,
  "tickRate": 20,
  "mapName": "Blaze Field"
}
```

```json
{
  "type": "state",
  "roomId": "alpha",
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
  - reset button to start a new match in the current room

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

The service worker cache version was bumped so browsers fetch the updated room-selection UI after deployment.

## How to test it

### Option 1: through the backend

If you run `server/`, the backend serves this app at:

```text
http://127.0.0.1:5770/pwa/
```

Open two browser tabs with the same room, for example:

```text
http://127.0.0.1:5770/pwa/?room=alpha
```

Both clients should see each other. Then open a third tab with another room:

```text
http://127.0.0.1:5770/pwa/?room=beta
```

The `beta` client should not see players, shots, flags, scores, or reset effects from `alpha`.

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

The map editor remains independent from room selection. It still works against the backend global map endpoints.

## Related folders

- `editor/`: the project's map editor
- `icons/`: current PWA icons

## Current limitations

- no chat
- no authentication
- no room list UI yet
- no explicit room creation UI yet; rooms are created on demand by connecting to a valid room ID when the backend supports it
- no player visual customization
- no mouse aim independent from movement
- no advanced interpolation
- gameplay still depends on the backend; offline only preserves the static shell

## Recommended project usage

1. Run the backend in `server/`
2. Open `http://127.0.0.1:5770/pwa/`
3. Enter a room or keep the default `public`
4. Connect one or more clients to the same room
5. Open another client in a different room to verify isolation
6. Play until the timer ends, or press **Reset Match** to restart the current room immediately
7. Optionally edit the map from `client-pwa/editor/` when no players are connected in any active room

## Author

**David Jorge Aguirre Grazio**  
Developer
