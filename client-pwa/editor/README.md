# THE FLAG Map Editor

Static web editor used to build and maintain the 2D maps for **THE FLAG**. Its output is compatible with the current backend in `server/` and the playable client in `client-pwa/`.

## Current state

The editor currently supports:

- creating and replacing the hard map perimeter
- creating `polygon`, `rect`, and `circle` obstacles
- placing `blue` and `red` flags
- moving whole objects
- editing vertices and corners of:
  - `perimeter`
  - `polygon`
  - `rect`
- moving circles and flags, but **not** resizing circles through handles
- using a `10 px` grid snap
- zoom, zoom reset, and fit-to-view
- panning with `Ctrl + drag` in selection mode
- exporting to JSON
- importing from JSON
- loading the active map through `GET /api/map`
- saving the edited map through `PUT /api/map`
- validating minimum map structure before saving to the server

## How it fits into the project

The current project workflow is:

1. `server/Data/map.json` defines the runtime map.
2. The backend exposes that content through `GET /api/map`.
3. This editor can load, modify, and send it back.
4. The PWA client consumes the same map schema to render and play the match.

## Main files

```text
client-pwa/editor/
  index.html
  styles.css
  app.js
  README.md
```

## Running the editor

### Option 1: open it as a file

You can open `index.html` directly in the browser.

If opened as `file:///`, it defaults to:

```text
http://127.0.0.1:5770
```

You can also force a different backend:

```text
file:///C:/path/client-pwa/editor/index.html?server=http://127.0.0.1:5770
```

### Option 2: serve it as a static site

Example with Python:

```bash
python -m http.server 8080
```

Then open:

```text
http://localhost:8080
```

If the editor runs under HTTP/HTTPS, it automatically tries to use the same origin for `/api/map`.

### Supported query parameters

- `server` or `apiBase`: backend HTTP base URL
- `basePath` or `publicPath`: public prefix, useful when the backend is published behind something like `/theflag`

Example:

```text
https://example.com/editor/?server=https://example.com&basePath=/theflag
```

## Startup behavior

On startup, the editor:

- initializes the canvas and editor state
- runs `zoomToFit`
- attempts to load the map from the server in the background
- keeps working as a local editor if the backend is unreachable

## Available tools

### Select / move

- selects the topmost object under the cursor
- allows dragging full objects
- allows editing white handle points on the selected object
- rectangles expose four corners
- polygons and perimeters expose all vertices
- circles do not expose resize handles

### Hard perimeter

- adds vertices by clicking
- closes with:
  - a click near the first point
  - `Enter`
  - the **Close shape** button
- if a perimeter already exists, the editor asks for confirmation before replacing it

### Polygon

- same flow as the perimeter
- exported as a `polygon` obstacle

### Rectangle

- click and drag to create
- exported with `x`, `y`, `width`, `height`

### Circle

- click and drag to create
- exported with `x`, `y`, `radius`

### Flag

- click to place
- team is chosen in `flagTeamSelect`

## Canvas navigation

- minimum zoom: `25%`
- maximum zoom: `400%`
- buttons: `-`, `100%`, `Fit`, `+`
- shortcuts: `+`, `-`, `0`
- mouse wheel over the canvas, centered on the cursor
- pan with `Ctrl + drag` when the active tool is selection

## Current validation rules

The current validation in `app.js` checks:

- that a perimeter exists
- that there is not more than one perimeter
- that a blue flag exists
- that a red flag exists
- warns if there is more than one flag per team
- warns if a flag is outside the perimeter

Behavior depends on the save target:

- `Save JSON`: exports even with errors or warnings, but reports the state
- `Save to server`: blocks on errors and asks for confirmation on warnings

## Backend integration

### `GET /api/map`

- loads the current JSON document from the server
- if the editor already has manual content loaded, it asks for confirmation before replacing it
- the automatic startup load is silent

### `PUT /api/map`

- sends the full map JSON
- uses `Content-Type: application/json`
- shows the server message when the save succeeds
- shows an error if the backend returns a conflict or any other failure

Remember that the current backend rejects map saves while players are connected.

## JSON format produced by the editor

The editor exports a document with this general structure:

```json
{
  "meta": {
    "name": "Blaze Field",
    "version": "1.1.0",
    "canvas": {
      "width": 1800,
      "height": 950
    },
    "generatedAt": "2026-04-21T11:35:31.337Z"
  },
  "objects": [
    {
      "id": "perimeter-1",
      "type": "perimeter",
      "hard": true,
      "points": [
        { "x": 10, "y": 10 },
        { "x": 1790, "y": 10 },
        { "x": 1790, "y": 940 },
        { "x": 10, "y": 940 }
      ]
    },
    {
      "id": "rect-2",
      "type": "rect",
      "hard": true,
      "x": 120,
      "y": 360,
      "width": 160,
      "height": 180
    },
    {
      "id": "circle-3",
      "type": "circle",
      "hard": true,
      "x": 600,
      "y": 460,
      "radius": 55
    },
    {
      "id": "polygon-4",
      "type": "polygon",
      "hard": true,
      "points": [
        { "x": 700, "y": 300 },
        { "x": 840, "y": 320 },
        { "x": 810, "y": 420 },
        { "x": 690, "y": 390 }
      ]
    },
    {
      "id": "flag-5",
      "type": "flag",
      "team": "blue",
      "x": 160,
      "y": 450
    },
    {
      "id": "flag-6",
      "type": "flag",
      "team": "red",
      "x": 1240,
      "y": 450
    }
  ]
}
```

Notes:

- IDs are generated automatically with type-based prefixes
- `hard` is emitted as `true` for obstacles and the perimeter
- the current backend ignores advanced `meta.version` semantics and validates the structure itself

## UI and feedback

The current interface shows:

- map name
- canvas width and height
- current operation status
- per-type summary counts
- validation block
- selected-object details
- current zoom
- resolved API base

## Current limitations

- no layers
- no object locking
- no quick duplicate action
- no undo/redo
- no interactive resize for circles after creation
- no advanced overlap validation between obstacles
- no spawn-zone validation because the backend still does not use explicit spawns
- no incremental save; the full map is always sent

## Relationship with the playable client

The active client in `client-pwa/` consumes the same map schema to:

- define world size
- draw the perimeter and obstacles
- place flags
- drive scene presentation

## Practical notes

- if the backend is running from `server/`, the most useful workflow is `GET/PUT /api/map`
- if the backend is published behind a prefix, use `basePath` or `publicPath`
- the editor can work fully offline to create JSON first and sync later

## Author

**David Jorge Aguirre Grazio**  
Developer
