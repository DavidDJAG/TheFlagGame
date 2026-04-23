# Implementation Plan - Multiplayer Capture-the-Flag Game

## 1. Feasibility

A simple 2D multiplayer game with a visual style similar to the attached reference is fully viable using:

- **Frontend:** HTML + CSS + JavaScript
- **Web server / reverse proxy:** Nginx
- **Game backend / orchestrator:** C# with **.NET 9 Console** on Windows

For an MVP, this stack is appropriate if the goal is:

- simple 2D top-down gameplay
- basic movement
- simple shooting or minimal combat interaction
- flag capture and return
- small matches per room
- simple collisions and maps
- no complex physics or advanced graphics

It is not the fastest stack for iteration compared with engines like Godot or Unity, but it **is a very reasonable architecture** if you want full control over the flow, simple deployment, and a clean technical base.

---

## 2. MVP functional goal

The first deliverable should include:

- anonymous nickname login
- simple lobby or direct room access
- static 2D map with obstacles
- two teams: red and blue
- one flag per base
- movement in 8 directions
- simplified shooting or combat
- simple HP and respawn
- flag capture
- scoring when the enemy flag reaches the home base
- match timer
- on-screen scoreboard
- real-time multiplayer synchronization

---

## 3. Recommended architecture

### 3.1 Overview

```text
[ Browser ]
   |  HTTP/HTTPS (assets)
   |  WebSocket (game state)
   v
[ Nginx ]
   |--> serves static HTML/CSS/JS
   |--> reverse proxy to .NET backend
   v
[ .NET 9 Console / Game Server ]
   |--> game loop
   |--> rooms
   |--> rules
   |--> simple collisions
   |--> state synchronization
```

### 3.2 Main recommendation

Use in the backend:

- **.NET 9 Console + Generic Host**
- **Kestrel** embedded in the console process
- **native WebSockets** or **SignalR**

### Option A - Native WebSockets

Best if you want:

- maximum protocol control
- less overhead
- compact messages
- a very clear game-oriented architecture

### Option B - SignalR

Useful if you want:

- less initial implementation effort
- easier connection management
- built-in reconnect and grouping helpers
- faster iteration on lobby/chat features

### Final recommendation

For this kind of game:

- **MVP:** native WebSockets
- **future lobby/chat:** SignalR as a complementary layer if needed

---

## 4. Suggested technologies

### 4.1 Frontend

#### Minimal base

- **HTML5**
- **CSS3**
- **JavaScript ES2022+**
- **Canvas 2D API**

#### Optional libraries

- **Vite** for more comfortable development
- **TypeScript** to reduce message and entity mistakes
- **Howler.js** for sound effects
- **PixiJS** later if you want cleaner rendering and more performance

#### Practical recommendation

To start:

- **HTML + CSS + JavaScript + Canvas 2D**

For a more maintainable base:

- **Vite + TypeScript + Canvas 2D**

### 4.2 Backend

- **C# .NET 9**
- **Console App**
- **Generic Host**
- **Kestrel**
- **System.Net.WebSockets**
- **System.Text.Json**
- **BackgroundService** for the game loop

#### Useful optional libraries

- **MessagePack for C#** for compact serialization
- **Serilog** for logging
- **FluentValidation** for incoming message validation

#### Practical recommendation

Start with:

- JSON + WebSockets + `System.Text.Json`

Move to MessagePack only if:

- entity counts grow significantly
- snapshot rate increases
- or bandwidth becomes a real problem

### 4.3 Infrastructure

- **Nginx** as reverse proxy
- **Windows Server** or Windows 11 Pro for testing
- **Windows Service** or scheduled task for backend startup
- **HTTPS** with a valid certificate for public access

---

## 5. Technical game design

### 5.1 Simulation model

Use an **authoritative server** model.

That means:

- client sends **intent**: move, shoot, interact
- server decides the **real result**
- client only performs lightweight visual prediction

This avoids:

- fake teleports
- trivial speed hacks
- invalid captures
- client-invented hits

### Recommendation

Avoid P2P. Use:

- **authoritative client-server**

### 5.2 Timing

Suggested rates:

- **server game loop:** 20 ticks per second
- **client rendering:** `requestAnimationFrame` (~60 FPS)
- **state snapshots:** 10-20 per second

Initial values:

- server tick: **20 Hz**
- snapshot rate: **10 Hz**
- client input: only when changed or every **50 ms**

### 5.3 Networking flow

1. Client connects over WebSocket
2. Sends `join_match`
3. Server assigns team/spawn
4. Client sends periodic inputs
5. Server simulates the world
6. Server emits snapshots
7. Client interpolates remote entities

#### Example client -> server messages

```json
{ "type": "join_match", "nickname": "Player1" }
{ "type": "input", "seq": 15, "up": true, "down": false, "left": false, "right": true, "aimX": 120, "aimY": 55, "fire": false }
{ "type": "respawn_request" }
```

#### Example server -> client messages

```json
{ "type": "welcome", "playerId": "p12", "team": "blue" }
{ "type": "snapshot", "tick": 412, "players": [], "projectiles": [], "flags": [], "score": { "red": 2, "blue": 1 }, "timeLeft": 872 }
{ "type": "event", "name": "flag_captured", "team": "blue", "by": "p12" }
```

---

## 6. Main components

### 6.1 Frontend

Suggested modules:

```text
frontend/
  index.html
  styles/
    main.css
  src/
    main.js
    game.js
    renderer.js
    input.js
    network.js
    entities.js
    hud.js
    map.js
```

Responsibilities:

- `renderer.js`: draws map, players, projectiles, flags, and basic HUD
- `input.js`: keyboard + mouse
- `network.js`: WebSocket connection and message handling
- `game.js`: local state and interpolation
- `map.js`: map data and obstacles
- `hud.js`: scoreboard, timer, and flag state

### 6.2 Backend

Suggested modules:

```text
server/
  Program.cs
  Hosting/
    WebServer.cs
  Networking/
    WebSocketConnectionManager.cs
    ClientSession.cs
    MessageRouter.cs
  Game/
    GameLoopService.cs
    MatchManager.cs
    MatchInstance.cs
    GameState.cs
    Systems/
      MovementSystem.cs
      CombatSystem.cs
      CollisionSystem.cs
      FlagSystem.cs
      RespawnSystem.cs
      ScoreSystem.cs
  Domain/
    Player.cs
    Projectile.cs
    Flag.cs
    TeamBase.cs
    Obstacle.cs
  Serialization/
    JsonMessageSerializer.cs
  Config/
    GameSettings.cs
```

Responsibilities:

- `Program.cs`: host boot and configuration
- `WebServer.cs`: HTTP endpoints, health check, and WebSocket upgrade
- `ClientSession.cs`: per-player connection state
- `MatchManager.cs`: creates and recycles matches/rooms
- `MatchInstance.cs`: live match state
- `GameLoopService.cs`: global or per-room tick
- `Systems/*`: gameplay logic split by subsystem

---

## 7. Arena rendering style

The reference image suggests a very suitable MVP style:

- top-down view
- subtle background grid
- white/gray obstacles
- polygonal bases
- simplified players using circles or simple bodies
- minimal text HUD
- visible shot traces or projectiles

### How to reproduce it in Canvas 2D

#### Background

- repeated grid every 32 or 48 px
- subtle gradient or dark flat tone

#### Obstacles

- rounded rectangles
- simple polygons
- light gray fill with soft white outline

#### Players

- circle or capsule body
- orientation indicator line or triangle
- team-based color
- name + HP bar above

#### Flags

- simple pole + triangle
- highlighted base using hexagon or polygon

#### Shots

- short interpolated lines
- minimal impact particles

### Visual recommendation

Do not start with image assets. Use:

- **geometric shapes drawn directly in Canvas**

That speeds up the MVP and keeps the style clean.

---

## 8. Map system

### 8.1 Recommended format

Start with JSON:

```json
{
  "name": "arena_01",
  "width": 2400,
  "height": 1400,
  "gridSize": 40,
  "blueBase": { "x": 180, "y": 700, "radius": 90 },
  "redBase": { "x": 2220, "y": 700, "radius": 90 },
  "obstacles": [
    { "type": "rect", "x": 300, "y": 200, "w": 180, "h": 80 },
    { "type": "rect", "x": 950, "y": 500, "w": 120, "h": 220 }
  ],
  "spawnPoints": {
    "blue": [{ "x": 260, "y": 650 }],
    "red": [{ "x": 2140, "y": 650 }]
  }
}
```

Advantages:

- easy to edit
- easy to validate
- reusable by client and server

Recommended improvement:

- keep a single shared map file between frontend and backend

---

## 9. Minimum gameplay mechanics

### 9.1 Movement

- WASD or arrows
- fixed speed
- AABB or circle-vs-rect collisions
- optional sprint later

### 9.2 Combat

#### Simplest option

- hitscan with cooldown
- fixed damage
- max range
- no reload at first

#### Alternative

- linear projectiles
- more visual, slightly more network logic

#### Recommendation

For the MVP:

- **simple hitscan** or
- **short-lived linear projectile**

### 9.3 Flags

Recommended states:

- `AtBase`
- `Carried`
- `Dropped`
- `Returning`

Basic rules:

- entering enemy base takes the flag
- the flag is attached to the carrier
- if the carrier dies, the flag drops
- if an ally touches the dropped home flag, it returns or starts returning
- to score, the home flag should normally be back at base

### 9.4 Respawn

- fixed timer: 3-5 seconds
- team spawn point
- optional short invulnerability: 0.5-1 second

---

## 10. Recommended data model

### 10.1 Player entity

```text
Player
- Id
- Nickname
- Team
- Position
- Velocity
- Rotation
- Hp
- IsAlive
- RespawnAt
- CarryingFlag
- LastProcessedInputSeq
- ConnectionId
```

### 10.2 Flag entity

```text
Flag
- Team
- State
- Position
- BasePosition
- CarrierPlayerId
- ReturnTimer
```

### 10.3 Match entity

```text
Match
- Id
- Players
- Projectiles
- Flags
- ScoreRed
- ScoreBlue
- TimeLeft
- Status
- Map
```

---

## 11. Synchronization protocol

### Recommended strategy

Do not send the full world every visual frame.

Start with:

- lightweight full snapshot at 10 Hz
- discrete events for important actions

Useful events:

- `player_joined`
- `player_left`
- `player_died`
- `flag_taken`
- `flag_dropped`
- `flag_returned`
- `score_changed`
- `match_started`
- `match_finished`

Future optimization:

- delta compression
- MessagePack
- interest management by proximity

---

## 12. Collisions

### Recommended simplification

Use these primitives:

- player: circle
- obstacle: rectangle
- projectile: point or small circle
- base: circle or approximate polygon

Enough algorithms for MVP:

- circle vs rect
- circle vs circle
- segment vs rect (for hitscan)

No full physics engine is required.

Recommendation:

- avoid Box2D or heavier engines at first

---

## 13. Room organization

### Simple model

- one or more rooms
- capacity: 4, 6, 8, or 10 players
- balanced team auto-assignment

First version:

- one active room
- create another room automatically if full

Later:

- lobby
- room list
- private rooms

---

## 14. Persistence

For the MVP, almost everything can live in memory.

Persist only if needed:

- player name
- global stats
- basic ranking

Options:

- **no database** at first
- **SQLite** for minimal persistence
- **PostgreSQL** if scaling later

Recommendation:

- start with **configuration + in-memory state**
- write logs to file

---

## 15. Deployment with Nginx + .NET

### Recommended flow

- Nginx serves frontend files
- Nginx proxies `/ws` to the .NET process
- backend listens on `localhost:5000`

### Typical Nginx config

```nginx
server {
    listen 80;
    server_name your-domain.local;

    root C:/apps/ctf/www;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;
    }

    location /ws {
        proxy_pass http://127.0.0.1:5000/ws;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_read_timeout 3600;
    }

    location /api/ {
        proxy_pass http://127.0.0.1:5000/api/;
        proxy_set_header Host $host;
    }
}
```

### In .NET

Expose:

- `/ws` -> gameplay WebSocket
- `/api/health` -> health check
- `/api/matchmaking` -> optional

---

## 16. Implementation phases

### Phase 0 - Setup

#### Goal

Prepare repository, architecture, and environment.

#### Tasks

- create solution:
  - `GameServer`
  - `GameShared` (optional)
  - `WebClient`
- configure local Nginx
- configure .NET 9 backend startup
- define map JSON format
- define message protocol
- define gameplay constants document

#### Deliverable

- structured repository
- backend starting
- frontend serving a base page

### Phase 1 - Local visual prototype (no network)

#### Goal

Validate look and basic feel.

#### Tasks

- fullscreen or viewport-fit canvas
- render grid and obstacles
- render bases and flags
- render fake/static players
- camera centered on local player
- HUD: score + timer

#### Deliverable

- visual demo similar to the target reference

### Phase 2 - Local input and movement

#### Goal

Control the local player with collisions.

#### Tasks

- WASD
- mouse orientation
- player-obstacle collision
- camera tracking
- local test shooting

#### Deliverable

- locally playable character in a static map

### Phase 3 - Backend and WebSocket connection

#### Goal

Connect frontend and backend.

#### Tasks

- run Kestrel inside console app
- WebSocket endpoint `/ws`
- basic handshake
- client session state
- `join_match` message
- simple keepalive/ping

#### Deliverable

- client connected and receiving `welcome`

### Phase 4 - Minimal multiplayer state

#### Goal

See multiple players on the map.

#### Tasks

- store players per room
- periodic snapshots
- position synchronization
- remote-player interpolation on the client
- clean disconnect flow

#### Deliverable

- 2+ visible players moving in real time

### Phase 5 - Match rules

#### Goal

Activate the core gameplay loop.

#### Tasks

- red/blue teams
- team spawns
- flags
- capture
- flag drop
- flag return
- score
- timer
- round reset or match end

#### Deliverable

- complete capture-the-flag style match

### Phase 6 - Simple combat

#### Goal

Add tactical pressure.

#### Tasks

- projectile or shot
- damage
- death
- respawn
- cooldown
- minimal visual feedback

#### Deliverable

- functional stable combat

### Phase 7 - Visual polish and UX

#### Goal

Improve readability and game feel.

#### Tasks

- health bars
- player names
- flag carrier indicators
- optional minimap
- simple particles
- sounds
- end-of-match screen
- simple leaderboard

#### Deliverable

- presentable MVP version

### Phase 8 - Hardening

#### Goal

Improve stability.

#### Tasks

- robust message validation
- per-connection rate limiting
- reconnect handling
- structured logging
- basic metrics
- tests with 10-20 simulated clients

#### Deliverable

- build ready for real playtesting

---

## 17. Suggested schedule

### Fast path (1 developer, MVP)

- Week 1: phases 0-2
- Week 2: phases 3-4
- Week 3: phases 5-6
- Week 4: phases 7-8

### Conservative path

- 6 to 8 weeks with testing, refactoring, and proper deployment

---

## 18. Recommended repository layout

```text
ctf-game/
  README.md
  nginx/
    nginx.conf
  shared/
    maps/
      arena_01.json
    protocol/
      messages.md
  server/
    GameServer/
      Program.cs
      appsettings.json
      ...
  client/
    index.html
    package.json
    src/
      ...
```

Important improvement:

define a `shared/` folder from the beginning for:

- maps
- constants
- protocol documentation

That reduces client/server mismatches.

---

## 19. Technical decisions

### 19.1 Canvas vs DOM rendering

#### Canvas

Advantages:

- better visual control
- more natural for games
- scales better with dozens of entities

#### DOM

Advantage:

- easier at the very beginning

Disadvantage:

- becomes awkward quickly for real-time rendering

Recommendation:

- **Canvas 2D**

### 19.2 JavaScript vs TypeScript

#### JavaScript

- faster for quick prototypes

#### TypeScript

- more maintainable
- better for network messages
- fewer mistakes in entities and states

Recommendation:

- **TypeScript** if you can afford a bit more setup
- **JavaScript** if you want to validate the concept this week

### 19.3 JSON vs MessagePack

#### JSON

- easy to debug
- ideal for an MVP

#### MessagePack

- more efficient
- useful once traffic grows

Recommendation:

- start with **JSON**
- keep serialization abstract enough to swap later

### 19.4 Native WebSockets vs SignalR

#### Native WebSockets

- better fit for game loops
- fine protocol control

#### SignalR

- more comfortable for general real-time apps

Recommendation:

- **native WebSockets** for gameplay

---

## 20. Minimum security / anti-cheat

No advanced anti-cheat is needed at first, but you should:

- never accept absolute positions from the client
- never accept client-computed damage
- validate fire cadence
- validate input frequency
- close persistently invalid connections

Useful extras:

- input sequence numbers (`seq`)
- server timestamps
- hard speed limits

---

## 21. Performance

### Reasonable MVP targets

- 10-20 players per instance
- 1 active map per room
- 20 Hz simulation
- moderate CPU load

### Early useful optimizations

- avoid unnecessary allocations per tick
- reuse serialization buffers if needed
- avoid recalculating global collisions unnecessarily
- keep data structures simple

### Future optimization

- spatial partitioning
- delta snapshots
- message compression
- area-of-interest management

---

## 22. Testing

### Backend tests

- flag rule tests
- score tests
- respawn tests
- collision tests
- team assignment tests

### Integration tests

- connect multiple simulated clients
- verify snapshots
- measure average latency
- validate disconnect/reconnect

### Manual tests

- capture with home flag present
- capture with home flag absent
- carrier death
- automatic flag return
- time expiration

---

## 23. Risks and mitigation

### Risk 1 - Client/server desync

Mitigation:

- authoritative snapshots
- client interpolation
- simple protocol

### Risk 2 - Overcoupled backend

Mitigation:

- separate networking, domain, and systems

### Risk 3 - Frontend growing chaotically

Mitigation:

- modularize from the beginning
- centralize client state

### Risk 4 - Annoying visual latency

Mitigation:

- lightweight client-side prediction
- interpolation for remote entities

---

## 24. Final recommended stack

If starting this project today with a balance of simplicity and technical quality:

### Frontend

- **Vite**
- **TypeScript**
- **HTML5 Canvas 2D**
- **simple CSS**
- **Howler.js** optional

### Backend

- **C# .NET 9 Console App**
- **Generic Host + Kestrel**
- **native WebSockets**
- **System.Text.Json**
- **Serilog**

### Infrastructure

- **Nginx** serving frontend and proxying `/ws`
- **Windows** for the server process
- **SQLite** optional later

---

## 25. Exact recommended implementation order

1. Build the map and visual rendering without networking
2. Add local movement and collisions
3. Bring up backend with WebSocket
4. Synchronize multiplayer positions
5. Add flags and score
6. Add combat, death, and respawn
7. Add HUD, sounds, and polish
8. Optimize networking and internal structure

That order reduces rework and validates the gameplay core before adding more network complexity.

---

## 26. Possible future improvements

- basic bots
- minimap
- team chat
- multiple classes
- power-ups
- JSON map editor
- ranking / ELO
- match replay
- spectator mode

---

## 27. Conclusion

Yes, the project is completely viable with the proposed stack.

The strongest combination for this goal is:

- **Canvas 2D frontend**
- **Nginx as static server + reverse proxy**
- **authoritative .NET 9 Console process with WebSockets**

If the scope stays intentionally simple for the MVP, a first playable version is realistic within a few weeks and can later evolve without rewriting the architecture.

---

## 28. Recommended next step

The most valuable next step is a **vertical slice** with exactly these pieces:

- 1 map
- 2 teams
- 2 flags
- 2 to 4 players
- movement
- collision
- flag capture
- score
- active WebSocket synchronization

Once that works, around 80% of the real project risk is already validated.

## Autor

**David Jorge Aguirre Grazio**  
Desarrollador
