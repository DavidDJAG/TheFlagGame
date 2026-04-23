(function () {
  const TOOL = {
    SELECT: 'select',
    PERIMETER: 'perimeter',
    POLYGON: 'polygon',
    RECT: 'rect',
    CIRCLE: 'circle',
    FLAG: 'flag'
  };

  const GRID_SIZE = 10;
  const ZOOM_MIN = 0.25;
  const ZOOM_MAX = 4;
  const ZOOM_FACTOR = 1.12;
  const HANDLE_RADIUS_SCREEN = 7;
  const HANDLE_HIT_RADIUS_SCREEN = 12;
  const DEFAULT_HTTP_SERVER_BASE = 'http://127.0.0.1:5770';
  const DEFAULT_PUBLIC_PATH = '';

  function normalizeBaseUrl(url) {
    return String(url || '').trim().replace(/\/+$/, '');
  }

  function normalizePublicPath(path) {
    const cleaned = String(path || '').trim();
    if (!cleaned || cleaned === '/') {
      return '';
    }

    const withLeadingSlash = cleaned.startsWith('/') ? cleaned : `/${cleaned}`;
    return withLeadingSlash.replace(/\/+$/, '');
  }

  function inferPublicPathFromLocation(pathname) {
    const marker = '/theflag/';
    if (pathname === '/theflag' || pathname.startsWith(marker)) {
      return '/theflag';
    }
    return '';
  }

  function getRuntimeConfig() {
    const params = new URLSearchParams(window.location.search);
    const explicitBase = params.get('server') || params.get('apiBase');
    const explicitPublicPath = params.get('basePath') || params.get('publicPath');

    if (explicitBase) {
      return {
        serverBase: normalizeBaseUrl(explicitBase),
        publicPath: normalizePublicPath(explicitPublicPath || inferPublicPathFromLocation(window.location.pathname))
      };
    }

    if (window.location.protocol === 'http:' || window.location.protocol === 'https:') {
      return {
        serverBase: normalizeBaseUrl(window.location.origin),
        publicPath: inferPublicPathFromLocation(window.location.pathname)
      };
    }

    return {
      serverBase: DEFAULT_HTTP_SERVER_BASE,
      publicPath: DEFAULT_PUBLIC_PATH
    };
  }

  const runtimeConfig = getRuntimeConfig();
  const HTTP_SERVER_BASE = runtimeConfig.serverBase;
  const PUBLIC_PATH = runtimeConfig.publicPath;
  const API_BASE = `${HTTP_SERVER_BASE}${PUBLIC_PATH}/api`;

  const canvas = document.getElementById('editorCanvas');
  const ctx = canvas.getContext('2d');

  const elements = {
    mapNameInput: document.getElementById('mapNameInput'),
    mapWidthInput: document.getElementById('mapWidthInput'),
    mapHeightInput: document.getElementById('mapHeightInput'),
    applyCanvasSizeBtn: document.getElementById('applyCanvasSizeBtn'),
    loadServerMapBtn: document.getElementById('loadServerMapBtn'),
    saveServerMapBtn: document.getElementById('saveServerMapBtn'),
    toolButtons: Array.from(document.querySelectorAll('[data-tool]')),
    flagTeamSelect: document.getElementById('flagTeamSelect'),
    snapToGridInput: document.getElementById('snapToGridInput'),
    finishShapeBtn: document.getElementById('finishShapeBtn'),
    cancelShapeBtn: document.getElementById('cancelShapeBtn'),
    deleteSelectedBtn: document.getElementById('deleteSelectedBtn'),
    clearAllBtn: document.getElementById('clearAllBtn'),
    saveJsonBtn: document.getElementById('saveJsonBtn'),
    loadJsonInput: document.getElementById('loadJsonInput'),
    statusText: document.getElementById('statusText'),
    validationText: document.getElementById('validationText'),
    selectedObjectDetails: document.getElementById('selectedObjectDetails'),
    summaryBlock: document.getElementById('summaryBlock'),
    canvasWrapper: document.getElementById('canvasWrapper'),
    canvasContent: document.getElementById('canvasContent'),
    zoomOutBtn: document.getElementById('zoomOutBtn'),
    zoomResetBtn: document.getElementById('zoomResetBtn'),
    zoomFitBtn: document.getElementById('zoomFitBtn'),
    zoomInBtn: document.getElementById('zoomInBtn'),
    zoomValue: document.getElementById('zoomValue')
  };

  const state = {
    tool: TOOL.SELECT,
    mapName: 'New map',
    mapVersion: '1.1.0',
    nextId: 1,
    objects: [],
    selectedId: null,
    tempPoints: [],
    draftShape: null,
    dragInfo: null,
    hoverPoint: null,
    mouseCanvas: { x: 0, y: 0 },
    zoom: 1,
    ctrlPressed: false,
    serverRequestPending: false
  };

  function init() {
    bindEvents();
    setStatus('Ready. Use the tools to draw the map.');
    syncCanvasMetaInputs();
    updateCanvasDisplaySize();
    renderAll();
    zoomToFit({ silent: true });
    updateCanvasCursor();
    void loadMapFromServer({ silent: true, auto: true });
  }

  function bindEvents() {
    elements.mapNameInput.addEventListener('input', () => {
      state.mapName = elements.mapNameInput.value.trim() || 'New map';
      renderSummary();
    });

    elements.applyCanvasSizeBtn.addEventListener('click', applyCanvasSize);
    elements.loadServerMapBtn.addEventListener('click', () => {
      void loadMapFromServer();
    });
    elements.saveServerMapBtn.addEventListener('click', () => {
      void saveMapToServer();
    });

    elements.toolButtons.forEach((button) => {
      button.addEventListener('click', () => setTool(button.dataset.tool));
    });

    elements.finishShapeBtn.addEventListener('click', finalizePointShape);
    elements.cancelShapeBtn.addEventListener('click', cancelCurrentDrawing);
    elements.deleteSelectedBtn.addEventListener('click', deleteSelectedObject);
    elements.clearAllBtn.addEventListener('click', clearAllObjects);
    elements.saveJsonBtn.addEventListener('click', saveMapToJson);
    elements.loadJsonInput.addEventListener('change', loadMapFromFile);

    elements.zoomOutBtn.addEventListener('click', () => setZoom(state.zoom / ZOOM_FACTOR));
    elements.zoomInBtn.addEventListener('click', () => setZoom(state.zoom * ZOOM_FACTOR));
    elements.zoomResetBtn.addEventListener('click', () => setZoom(1));
    elements.zoomFitBtn.addEventListener('click', () => zoomToFit());

    canvas.addEventListener('mousedown', onCanvasMouseDown);
    canvas.addEventListener('mousemove', onCanvasMouseMove);
    window.addEventListener('mousemove', onWindowMouseMove);
    window.addEventListener('mouseup', onCanvasMouseUp);
    canvas.addEventListener('mouseleave', onCanvasMouseLeave);
    canvas.addEventListener('contextmenu', (event) => event.preventDefault());
    elements.canvasWrapper.addEventListener('wheel', onCanvasWheel, { passive: false });

    window.addEventListener('keydown', (event) => {
      if (isTypingTarget(event.target)) {
        return;
      }

      if (event.key === 'Delete') {
        event.preventDefault();
        deleteSelectedObject();
      } else if (event.key === 'Escape') {
        event.preventDefault();
        cancelCurrentDrawing();
      } else if (event.key === 'Enter') {
        event.preventDefault();
        finalizePointShape();
      } else if (event.key === '+' || event.key === '=') {
        event.preventDefault();
        setZoom(state.zoom * ZOOM_FACTOR);
      } else if (event.key === '-') {
        event.preventDefault();
        setZoom(state.zoom / ZOOM_FACTOR);
      } else if (event.key === '0') {
        event.preventDefault();
        setZoom(1);
      }

      if (event.key === 'Control') {
        state.ctrlPressed = true;
        updateCanvasCursor();
      }
    });

    window.addEventListener('keyup', (event) => {
      if (event.key === 'Control') {
        state.ctrlPressed = false;
        updateCanvasCursor();
      }
    });
  }

  function applyCanvasSize() {
    const width = clampInt(Number(elements.mapWidthInput.value), 400, 4000, canvas.width);
    const height = clampInt(Number(elements.mapHeightInput.value), 300, 3000, canvas.height);

    if ((width !== canvas.width || height !== canvas.height) && state.objects.length > 0) {
      const confirmed = window.confirm(
        'Changing the canvas size will not rescale existing objects. Do you want to continue?'
      );
      if (!confirmed) {
        return;
      }
    }

    canvas.width = width;
    canvas.height = height;
    updateCanvasDisplaySize();
    syncCanvasMetaInputs();
    setStatus(`Canvas updated to ${width}x${height}.`);
    renderAll();
  }

  function syncCanvasMetaInputs() {
    elements.mapWidthInput.value = canvas.width;
    elements.mapHeightInput.value = canvas.height;
    state.mapName = elements.mapNameInput.value.trim() || 'New map';
  }

  function setTool(tool) {
    state.tool = tool;
    state.draftShape = null;
    state.dragInfo = null;

    if (tool !== TOOL.PERIMETER && tool !== TOOL.POLYGON) {
      state.tempPoints = [];
    }

    elements.toolButtons.forEach((button) => {
      button.classList.toggle('active', button.dataset.tool === tool);
    });

    const messages = {
      [TOOL.SELECT]: 'Selection mode active. Click an object to move it, drag its points to edit it, and use Ctrl + drag to pan the view.',
      [TOOL.PERIMETER]: 'Perimeter mode. Click to add vertices and close the shape with Enter or the first point.',
      [TOOL.POLYGON]: 'Polygon mode. Click to add vertices and close the shape with Enter or the first point.',
      [TOOL.RECT]: 'Rectangle mode. Click and drag to create the object.',
      [TOOL.CIRCLE]: 'Circle mode. Click and drag to create the object.',
      [TOOL.FLAG]: 'Flag mode. Click to place a flag for the selected team.'
    };

    setStatus(messages[tool] || 'Tool updated.');
    updateCanvasCursor();
    renderAll();
  }

  function onCanvasMouseDown(event) {
    const rawPoint = getCanvasPoint(event);
    const snappedPoint = getCanvasPoint(event, { snap: true });
    const point = state.tool === TOOL.SELECT ? rawPoint : snappedPoint;
    state.mouseCanvas = point;
    state.ctrlPressed = event.ctrlKey;

    if (state.tool === TOOL.SELECT) {
      if (event.ctrlKey) {
        event.preventDefault();
        state.dragInfo = {
          mode: 'pan',
          startClientX: event.clientX,
          startClientY: event.clientY,
          startScrollLeft: elements.canvasWrapper.scrollLeft,
          startScrollTop: elements.canvasWrapper.scrollTop
        };
        setStatus('Panning the map view.');
        updateCanvasCursor(rawPoint);
        return;
      }

      const selected = getSelectedObject();
      const selectedHandle = selected ? findEditableHandleAt(rawPoint, selected) : null;

      if (selectedHandle) {
        state.dragInfo = {
          mode: 'handle',
          id: selected.id,
          handle: selectedHandle
        };
        setStatus(`Editing point on ${selected.id}.`);
        updateCanvasCursor();
        renderAll();
        return;
      }

      const hit = findTopmostObjectAt(rawPoint);
      state.selectedId = hit ? hit.id : null;

      if (hit) {
        state.dragInfo = {
          mode: 'object',
          id: hit.id,
          anchor: rawPoint,
          original: cloneObject(hit)
        };
        setStatus(`Selected object: ${hit.id}`);
      } else {
        state.dragInfo = null;
        setStatus('No object selected.');
      }

      updateCanvasCursor(rawPoint);
      renderAll();
      return;
    }

    if (state.tool === TOOL.FLAG) {
      const flagTeam = elements.flagTeamSelect.value;
      const flag = {
        id: generateId('flag'),
        type: 'flag',
        team: flagTeam,
        x: point.x,
        y: point.y
      };
      state.objects.push(flag);
      state.selectedId = flag.id;
      setStatus(`Added ${flagTeam === 'blue' ? 'blue' : 'red'} flag.`);
      renderAll();
      return;
    }

    if (state.tool === TOOL.RECT) {
      state.draftShape = {
        type: 'rect',
        start: point,
        current: point
      };
      renderAll();
      return;
    }

    if (state.tool === TOOL.CIRCLE) {
      state.draftShape = {
        type: 'circle',
        start: point,
        current: point
      };
      renderAll();
      return;
    }

    if (state.tool === TOOL.POLYGON || state.tool === TOOL.PERIMETER) {
      handlePointShapeClick(point);
      renderAll();
    }
  }

  function onCanvasMouseMove(event) {
    const rawPoint = getCanvasPoint(event);
    const snappedPoint = getCanvasPoint(event, { snap: true });
    const point = state.tool === TOOL.SELECT ? rawPoint : snappedPoint;
    state.mouseCanvas = point;
    state.hoverPoint = point;
    state.ctrlPressed = event.ctrlKey;

    if (state.tool === TOOL.SELECT && state.dragInfo) {
      if (state.dragInfo.mode === 'pan') {
        event.preventDefault();
        const deltaX = event.clientX - state.dragInfo.startClientX;
        const deltaY = event.clientY - state.dragInfo.startClientY;
        elements.canvasWrapper.scrollLeft = Math.max(0, state.dragInfo.startScrollLeft - deltaX);
        elements.canvasWrapper.scrollTop = Math.max(0, state.dragInfo.startScrollTop - deltaY);
        updateCanvasCursor(rawPoint);
        return;
      }

      const selected = getSelectedObject();
      if (!selected) {
        state.dragInfo = null;
        updateCanvasCursor(rawPoint);
        return;
      }

      if (state.dragInfo.mode === 'object') {
        const dx = rawPoint.x - state.dragInfo.anchor.x;
        const dy = rawPoint.y - state.dragInfo.anchor.y;
        translateObjectFromOriginal(selected, state.dragInfo.original, dx, dy);
      }

      if (state.dragInfo.mode === 'handle') {
        moveEditableHandle(selected, state.dragInfo.handle, rawPoint);
      }

      renderAll();
      updateCanvasCursor(rawPoint);
      return;
    }

    if (state.draftShape) {
      state.draftShape.current = point;
      renderAll();
      updateCanvasCursor(rawPoint);
      return;
    }

    if (state.tempPoints.length > 0) {
      renderAll();
    }

    updateCanvasCursor(rawPoint);
  }

  function onWindowMouseMove(event) {
    if (!state.dragInfo && !state.draftShape) {
      return;
    }

    onCanvasMouseMove(event);
  }

  function onCanvasMouseUp() {
    if (state.tool === TOOL.SELECT && state.dragInfo) {
      const draggedMode = state.dragInfo.mode;
      state.dragInfo = null;
      updateCanvasCursor();
      if (draggedMode !== 'pan') {
        renderAll();
      }
      return;
    }

    if (!state.draftShape) {
      return;
    }

    const draft = state.draftShape;
    state.draftShape = null;

    if (draft.type === 'rect') {
      const rect = buildRectFromPoints(draft.start, draft.current);
      if (Math.abs(rect.width) < 5 || Math.abs(rect.height) < 5) {
        setStatus('Rectangle discarded because the size is too small.');
        renderAll();
        return;
      }
      rect.id = generateId('rect');
      rect.type = 'rect';
      rect.hard = true;
      state.objects.push(rect);
      state.selectedId = rect.id;
      setStatus(`Rectangle ${rect.id} created.`);
    }

    if (draft.type === 'circle') {
      const circle = buildCircleFromPoints(draft.start, draft.current);
      if (circle.radius < 5) {
        setStatus('Circle discarded because the radius is too small.');
        renderAll();
        return;
      }
      circle.id = generateId('circle');
      circle.type = 'circle';
      circle.hard = true;
      state.objects.push(circle);
      state.selectedId = circle.id;
      setStatus(`Circle ${circle.id} created.`);
    }

    renderAll();
  }

  function onCanvasMouseLeave() {
    state.hoverPoint = null;
    if (state.tool !== TOOL.SELECT || !state.dragInfo) {
      updateCanvasCursor(null, true);
      renderAll();
    }
  }

  function onCanvasWheel(event) {
    const canvasRect = canvas.getBoundingClientRect();
    const insideCanvas =
      event.clientX >= canvasRect.left &&
      event.clientX <= canvasRect.right &&
      event.clientY >= canvasRect.top &&
      event.clientY <= canvasRect.bottom;

    if (!insideCanvas) {
      return;
    }

    event.preventDefault();
    const focusPoint = getCanvasPoint(event);
    const factor = event.deltaY < 0 ? ZOOM_FACTOR : 1 / ZOOM_FACTOR;
    setZoom(state.zoom * factor, {
      clientX: event.clientX,
      clientY: event.clientY,
      canvasX: focusPoint.x,
      canvasY: focusPoint.y
    });
  }

  function handlePointShapeClick(point) {
    if (state.tempPoints.length >= 3 && distance(point, state.tempPoints[0]) <= scaledUnit(12)) {
      finalizePointShape();
      return;
    }

    state.tempPoints.push(point);
    setStatus(`Point added (${round2(point.x)}, ${round2(point.y)}). Total: ${state.tempPoints.length}`);
  }

  function finalizePointShape() {
    if (!(state.tool === TOOL.POLYGON || state.tool === TOOL.PERIMETER)) {
      return;
    }

    if (state.tempPoints.length < 3) {
      setStatus('At least 3 points are required to close the shape.');
      return;
    }

    const type = state.tool === TOOL.PERIMETER ? 'perimeter' : 'polygon';
    const newObject = {
      id: generateId(type),
      type,
      hard: true,
      points: state.tempPoints.map((point) => ({ x: point.x, y: point.y }))
    };

    if (type === 'perimeter') {
      const existingIndex = state.objects.findIndex((obj) => obj.type === 'perimeter');
      if (existingIndex >= 0) {
        const confirmed = window.confirm('A perimeter already exists. It will be replaced by the new one. Continue?');
        if (!confirmed) {
          setStatus('Perimeter replacement canceled.');
          return;
        }
        state.objects.splice(existingIndex, 1);
      }
    }

    state.objects.push(newObject);
    state.selectedId = newObject.id;
    state.tempPoints = [];
    setStatus(`${type === 'perimeter' ? 'Perimeter' : 'Polygon'} ${newObject.id} created.`);
    renderAll();
  }

  function cancelCurrentDrawing() {
    state.tempPoints = [];
    state.draftShape = null;
    state.dragInfo = null;
    setStatus('Current operation canceled.');
    updateCanvasCursor();
    renderAll();
  }

  function deleteSelectedObject() {
    if (!state.selectedId) {
      setStatus('There is no selected object to delete.');
      return;
    }

    const index = state.objects.findIndex((obj) => obj.id === state.selectedId);
    if (index === -1) {
      state.selectedId = null;
      renderAll();
      return;
    }

    const removed = state.objects.splice(index, 1)[0];
    state.selectedId = null;
    setStatus(`Object ${removed.id} deleted.`);
    renderAll();
  }

  function clearAllObjects() {
    if (state.objects.length === 0 && state.tempPoints.length === 0) {
      setStatus('The map is already empty.');
      return;
    }

    const confirmed = window.confirm('All objects in the current map will be deleted. Continue?');
    if (!confirmed) {
      return;
    }

    state.objects = [];
    state.tempPoints = [];
    state.draftShape = null;
    state.dragInfo = null;
    state.selectedId = null;
    setStatus('All objects were deleted.');
    renderAll();
  }

  function saveMapToJson() {
    const mapData = exportMap();
    const validation = validateMap(mapData.objects);

    const json = JSON.stringify(mapData, null, 2);
    const blob = new Blob([json], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `${slugify(mapData.meta.name || 'map')}.json`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);

    if (validation.errors.length > 0) {
      setStatus('Map saved, but it contains validation errors. Review the map summary.');
    } else if (validation.warnings.length > 0) {
      setStatus('Map saved with warnings.');
    } else {
      setStatus('Map saved successfully.');
    }
  }

  function loadMapFromFile(event) {
    const file = event.target.files && event.target.files[0];
    if (!file) {
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      try {
        const parsed = JSON.parse(String(reader.result));
        importMap(parsed);
        setStatus(`Map loaded from ${file.name}.`);
      } catch (error) {
        console.error(error);
        window.alert('Could not load the JSON file. Check the format.');
        setStatus('Error loading map JSON.');
      } finally {
        elements.loadJsonInput.value = '';
      }
    };
    reader.readAsText(file, 'utf-8');
  }

  async function loadMapFromServer(options = {}) {
    if (state.serverRequestPending) {
      return;
    }

    if (!options.auto && (state.objects.length > 0 || state.tempPoints.length > 0 || state.draftShape)) {
      const confirmed = window.confirm('Loading the map from the server will replace the current editor content. Do you want to continue?');
      if (!confirmed) {
        setStatus('Load from server canceled.');
        return;
      }
    }

    setServerRequestPending(true);
    if (!options.silent) {
      setStatus('Loading map from server...');
    }

    try {
      const response = await fetch(`${API_BASE}/map`, { cache: 'no-store' });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const mapData = await response.json();
      importMap(mapData);
      setStatus(`Map loaded from server: ${mapData.meta && mapData.meta.name ? mapData.meta.name : 'unnamed'}.`);
    } catch (error) {
      console.error(error);
      if (!options.silent) {
        const message = error instanceof Error ? error.message : 'Unknown error';
        window.alert(`Could not load the map from the server.

${message}`);
        setStatus('Error loading map from server.');
      }
    } finally {
      setServerRequestPending(false);
    }
  }

  async function saveMapToServer() {
    if (state.serverRequestPending) {
      return;
    }

    const mapData = exportMap();
    const validation = validateMap(mapData.objects);
    if (validation.errors.length > 0) {
      window.alert(`The map cannot be saved to the server because it contains validation errors.

${validation.errors.join('\n')}`);
      setStatus('Fix the validation errors before saving to the server.');
      return;
    }

    if (validation.warnings.length > 0) {
      const confirmed = window.confirm(
        `The map has warnings:

${validation.warnings.join('\n')}

Do you still want to send it to the server?`
      );
      if (!confirmed) {
        setStatus('Save to server canceled.');
        return;
      }
    }

    setServerRequestPending(true);
    setStatus('Sending map to server...');

    try {
      const response = await fetch(`${API_BASE}/map`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(mapData, null, 2)
      });

      const responseText = await response.text();
      const payload = tryParseJson(responseText);
      if (!response.ok) {
        const message = payload && payload.message ? payload.message : `HTTP ${response.status}`;
        throw new Error(message);
      }

      setStatus(payload && payload.message ? payload.message : 'Map saved on the server.');
    } catch (error) {
      console.error(error);
      const message = error instanceof Error ? error.message : 'Unknown error';
      window.alert(`Could not save the map to the server.

${message}`);
      setStatus('Error saving map to the server.');
    } finally {
      setServerRequestPending(false);
    }
  }

  function setServerRequestPending(isPending) {
    state.serverRequestPending = isPending;
    elements.loadServerMapBtn.disabled = isPending;
    elements.saveServerMapBtn.disabled = isPending;
  }

  function tryParseJson(text) {
    try {
      return JSON.parse(text);
    } catch {
      return null;
    }
  }

  function exportMap() {
    const metaName = elements.mapNameInput.value.trim() || 'New map';
    state.mapName = metaName;

    return {
      meta: {
        name: metaName,
        version: state.mapVersion,
        canvas: {
          width: canvas.width,
          height: canvas.height
        },
        generatedAt: new Date().toISOString()
      },
      objects: state.objects.map(cloneObject)
    };
  }

  function importMap(mapData) {
    if (!mapData || typeof mapData !== 'object') {
      throw new Error('Invalid JSON: invalid root.');
    }

    const meta = mapData.meta || {};
    const objects = Array.isArray(mapData.objects) ? mapData.objects : null;
    if (!objects) {
      throw new Error('Invalid JSON: missing objects property.');
    }

    const width = clampInt(Number(meta.canvas && meta.canvas.width), 400, 4000, 1400);
    const height = clampInt(Number(meta.canvas && meta.canvas.height), 300, 3000, 900);
    canvas.width = width;
    canvas.height = height;
    updateCanvasDisplaySize();

    state.mapName = typeof meta.name === 'string' && meta.name.trim() ? meta.name.trim() : 'Imported map';
    state.mapVersion = typeof meta.version === 'string' && meta.version.trim() ? meta.version.trim() : state.mapVersion;
    elements.mapNameInput.value = state.mapName;
    syncCanvasMetaInputs();

    state.objects = objects.map(normalizeImportedObject).filter(Boolean);
    state.tempPoints = [];
    state.draftShape = null;
    state.dragInfo = null;
    state.selectedId = null;
    state.nextId = computeNextId(state.objects);
    renderAll();
    zoomToFit({ silent: true });
  }

  function normalizeImportedObject(raw) {
    if (!raw || typeof raw !== 'object' || typeof raw.type !== 'string') {
      return null;
    }

    const base = {
      id: typeof raw.id === 'string' && raw.id.trim() ? raw.id.trim() : generateId(raw.type),
      type: raw.type,
      hard: raw.type === 'flag' ? false : raw.hard !== false
    };

    if (raw.type === 'rect') {
      return {
        ...base,
        x: Number(raw.x) || 0,
        y: Number(raw.y) || 0,
        width: Number(raw.width) || 0,
        height: Number(raw.height) || 0
      };
    }

    if (raw.type === 'circle') {
      return {
        ...base,
        x: Number(raw.x) || 0,
        y: Number(raw.y) || 0,
        radius: Number(raw.radius) || 0
      };
    }

    if (raw.type === 'flag') {
      return {
        ...base,
        team: raw.team === 'red' ? 'red' : 'blue',
        x: Number(raw.x) || 0,
        y: Number(raw.y) || 0
      };
    }

    if (raw.type === 'polygon' || raw.type === 'perimeter') {
      return {
        ...base,
        points: Array.isArray(raw.points)
          ? raw.points
              .map((point) => ({ x: Number(point.x) || 0, y: Number(point.y) || 0 }))
              .filter((point) => Number.isFinite(point.x) && Number.isFinite(point.y))
          : []
      };
    }

    return null;
  }

  function computeNextId(objects) {
    let max = 0;
    objects.forEach((obj) => {
      const match = String(obj.id || '').match(/(\d+)$/);
      if (match) {
        max = Math.max(max, Number(match[1]));
      }
    });
    return max + 1;
  }

  function cloneObject(obj) {
    return JSON.parse(JSON.stringify(obj));
  }

  function generateId(type) {
    const normalized = String(type || 'obj').toLowerCase();
    const id = `${normalized}-${state.nextId}`;
    state.nextId += 1;
    return id;
  }

  function getSelectedObject() {
    return state.objects.find((obj) => obj.id === state.selectedId) || null;
  }

  function translateObjectFromOriginal(obj, original, dx, dy) {
    if (!obj || !original) {
      return;
    }

    const translation = elements.snapToGridInput.checked
      ? { x: snapValue(dx), y: snapValue(dy) }
      : { x: dx, y: dy };

    const appliedDx = translation.x;
    const appliedDy = translation.y;

    if (obj.type === 'rect' || obj.type === 'circle' || obj.type === 'flag') {
      obj.x = original.x + appliedDx;
      obj.y = original.y + appliedDy;
      return;
    }

    if (obj.type === 'polygon' || obj.type === 'perimeter') {
      obj.points = original.points.map((point) => ({
        x: point.x + appliedDx,
        y: point.y + appliedDy
      }));
    }
  }

  function moveEditableHandle(obj, handle, point) {
    if (!obj || !handle) {
      return;
    }

    const nextPoint = elements.snapToGridInput.checked ? snapPoint(point) : point;

    if (handle.type === 'rect-corner' && obj.type === 'rect') {
      const corners = getRectCorners(obj);
      const oppositeIndex = (handle.index + 2) % 4;
      const opposite = corners[oppositeIndex];
      const rect = buildRectFromPoints(opposite, nextPoint);
      obj.x = rect.x;
      obj.y = rect.y;
      obj.width = rect.width;
      obj.height = rect.height;
      return;
    }

    if (handle.type === 'polygon-point' && (obj.type === 'polygon' || obj.type === 'perimeter')) {
      obj.points[handle.index] = {
        x: nextPoint.x,
        y: nextPoint.y
      };
    }
  }

  function findTopmostObjectAt(point) {
    for (let i = state.objects.length - 1; i >= 0; i -= 1) {
      const obj = state.objects[i];
      if (isPointInsideObject(point, obj)) {
        return obj;
      }
    }
    return null;
  }

  function findEditableHandleAt(point, obj) {
    if (!obj || obj.type === 'circle' || obj.type === 'flag') {
      return null;
    }

    const hitRadius = handleHitRadius();

    if (obj.type === 'rect') {
      const corners = getRectCorners(obj);
      for (let i = 0; i < corners.length; i += 1) {
        if (distance(point, corners[i]) <= hitRadius) {
          return { type: 'rect-corner', index: i };
        }
      }
      return null;
    }

    if (obj.type === 'polygon' || obj.type === 'perimeter') {
      for (let i = 0; i < obj.points.length; i += 1) {
        if (distance(point, obj.points[i]) <= hitRadius) {
          return { type: 'polygon-point', index: i };
        }
      }
    }

    return null;
  }

  function getRectCorners(obj) {
    return [
      { x: obj.x, y: obj.y },
      { x: obj.x + obj.width, y: obj.y },
      { x: obj.x + obj.width, y: obj.y + obj.height },
      { x: obj.x, y: obj.y + obj.height }
    ];
  }

  function isPointInsideObject(point, obj) {
    if (!obj) {
      return false;
    }

    if (obj.type === 'rect') {
      return (
        point.x >= obj.x &&
        point.x <= obj.x + obj.width &&
        point.y >= obj.y &&
        point.y <= obj.y + obj.height
      );
    }

    if (obj.type === 'circle') {
      return distance(point, obj) <= obj.radius;
    }

    if (obj.type === 'flag') {
      return distance(point, obj) <= scaledUnit(16);
    }

    if ((obj.type === 'polygon' || obj.type === 'perimeter') && Array.isArray(obj.points)) {
      return pointInPolygon(point, obj.points) || isPointNearPolygonEdge(point, obj.points, scaledUnit(8));
    }

    return false;
  }

  function buildRectFromPoints(a, b) {
    const x = Math.min(a.x, b.x);
    const y = Math.min(a.y, b.y);
    const width = Math.abs(b.x - a.x);
    const height = Math.abs(b.y - a.y);
    return { x, y, width, height };
  }

  function buildCircleFromPoints(a, b) {
    return {
      x: a.x,
      y: a.y,
      radius: distance(a, b)
    };
  }

  function drawGrid() {
    ctx.save();
    ctx.strokeStyle = 'rgba(255,255,255,0.08)';
    ctx.lineWidth = scaledUnit(1);

    for (let x = 0; x <= canvas.width; x += GRID_SIZE) {
      ctx.beginPath();
      ctx.moveTo(x + 0.5, 0);
      ctx.lineTo(x + 0.5, canvas.height);
      ctx.stroke();
    }

    for (let y = 0; y <= canvas.height; y += GRID_SIZE) {
      ctx.beginPath();
      ctx.moveTo(0, y + 0.5);
      ctx.lineTo(canvas.width, y + 0.5);
      ctx.stroke();
    }

    ctx.restore();
  }

  function drawObjects() {
    state.objects.forEach((obj) => {
      const isSelected = obj.id === state.selectedId;
      drawObject(obj, isSelected);
    });
  }

  function drawObject(obj, isSelected) {
    if (obj.type === 'rect') {
      ctx.save();
      ctx.fillStyle = isSelected ? 'rgba(251, 191, 36, 0.28)' : 'rgba(255, 255, 255, 0.16)';
      ctx.strokeStyle = isSelected ? 'rgba(251, 191, 36, 0.95)' : 'rgba(255, 255, 255, 0.8)';
      ctx.lineWidth = scaledUnit(isSelected ? 3 : 2);
      ctx.fillRect(obj.x, obj.y, obj.width, obj.height);
      ctx.strokeRect(obj.x, obj.y, obj.width, obj.height);
      drawLabel(obj);
      if (isSelected) {
        drawSelectionHandles(obj);
      }
      ctx.restore();
      return;
    }

    if (obj.type === 'circle') {
      ctx.save();
      ctx.beginPath();
      ctx.arc(obj.x, obj.y, obj.radius, 0, Math.PI * 2);
      ctx.fillStyle = isSelected ? 'rgba(251, 191, 36, 0.24)' : 'rgba(255, 255, 255, 0.14)';
      ctx.strokeStyle = isSelected ? 'rgba(251, 191, 36, 0.95)' : 'rgba(255, 255, 255, 0.8)';
      ctx.lineWidth = scaledUnit(isSelected ? 3 : 2);
      ctx.fill();
      ctx.stroke();
      drawLabel(obj);
      ctx.restore();
      return;
    }

    if (obj.type === 'polygon' || obj.type === 'perimeter') {
      const color = obj.type === 'perimeter' ? 'rgba(16, 185, 129, 0.95)' : 'rgba(255,255,255,0.85)';
      const fill = obj.type === 'perimeter'
        ? 'rgba(16, 185, 129, 0.08)'
        : isSelected
          ? 'rgba(251, 191, 36, 0.16)'
          : 'rgba(255, 255, 255, 0.10)';
      ctx.save();
      ctx.beginPath();
      ctx.moveTo(obj.points[0].x, obj.points[0].y);
      for (let i = 1; i < obj.points.length; i += 1) {
        ctx.lineTo(obj.points[i].x, obj.points[i].y);
      }
      ctx.closePath();
      ctx.fillStyle = fill;
      ctx.strokeStyle = isSelected ? 'rgba(251, 191, 36, 0.95)' : color;
      ctx.lineWidth = scaledUnit(obj.type === 'perimeter' ? 4 : isSelected ? 3 : 2);
      ctx.fill();
      ctx.stroke();

      obj.points.forEach((point, index) => {
        ctx.beginPath();
        ctx.arc(point.x, point.y, scaledUnit(index === 0 ? 4 : 3), 0, Math.PI * 2);
        ctx.fillStyle = index === 0 ? 'rgba(96, 165, 250, 0.95)' : 'rgba(255, 255, 255, 0.9)';
        ctx.fill();
      });

      drawLabel(obj);
      if (isSelected) {
        drawSelectionHandles(obj);
      }
      ctx.restore();
      return;
    }

    if (obj.type === 'flag') {
      ctx.save();
      const poleHeight = scaledUnit(24);
      const flagWidth = scaledUnit(18);
      const flagHeight = scaledUnit(12);
      const color = obj.team === 'red' ? '#ef4444' : '#3b82f6';

      ctx.strokeStyle = isSelected ? '#fbbf24' : 'rgba(255,255,255,0.92)';
      ctx.lineWidth = scaledUnit(3);
      ctx.beginPath();
      ctx.moveTo(obj.x, obj.y);
      ctx.lineTo(obj.x, obj.y - poleHeight);
      ctx.stroke();

      ctx.fillStyle = color;
      ctx.fillRect(obj.x, obj.y - poleHeight, flagWidth, flagHeight);
      ctx.strokeStyle = 'rgba(255,255,255,0.85)';
      ctx.lineWidth = scaledUnit(1.5);
      ctx.strokeRect(obj.x, obj.y - poleHeight, flagWidth, flagHeight);

      ctx.beginPath();
      ctx.arc(obj.x, obj.y, scaledUnit(5), 0, Math.PI * 2);
      ctx.fillStyle = isSelected ? '#fbbf24' : color;
      ctx.fill();

      drawLabel(obj);
      ctx.restore();
    }
  }

  function drawSelectionHandles(obj) {
    const radius = handleRadius();

    if (obj.type === 'rect') {
      getRectCorners(obj).forEach((point) => drawHandle(point, radius));
      return;
    }

    if (obj.type === 'polygon' || obj.type === 'perimeter') {
      obj.points.forEach((point) => drawHandle(point, radius));
    }
  }

  function drawHandle(point, radius) {
    ctx.save();
    ctx.beginPath();
    ctx.arc(point.x, point.y, radius, 0, Math.PI * 2);
    ctx.fillStyle = '#ffffff';
    ctx.fill();
    ctx.strokeStyle = '#111827';
    ctx.lineWidth = scaledUnit(2);
    ctx.stroke();
    ctx.restore();
  }

  function drawLabel(obj) {
    const label = `${obj.id} · ${obj.type}` + (obj.type === 'flag' ? ` (${obj.team})` : '');
    const position = getObjectLabelPosition(obj);
    const fontSize = scaledUnit(13);
    const paddingX = scaledUnit(5);
    const boxHeight = scaledUnit(20);
    const margin = scaledUnit(4);

    ctx.save();
    ctx.font = `${fontSize}px Segoe UI, sans-serif`;
    const metrics = ctx.measureText(label);
    const width = metrics.width + paddingX * 2;
    const x = clamp(position.x, margin, canvas.width - width - margin);
    const y = clamp(position.y, boxHeight, canvas.height - margin);
    ctx.fillStyle = 'rgba(0,0,0,0.58)';
    ctx.fillRect(x, y - boxHeight + scaledUnit(2), width, boxHeight);
    ctx.fillStyle = '#ffffff';
    ctx.fillText(label, x + paddingX, y - scaledUnit(4));
    ctx.restore();
  }

  function getObjectLabelPosition(obj) {
    if (obj.type === 'rect') {
      return { x: obj.x, y: obj.y - scaledUnit(6) };
    }

    if (obj.type === 'circle') {
      return { x: obj.x - obj.radius, y: obj.y - obj.radius - scaledUnit(6) };
    }

    if (obj.type === 'flag') {
      return { x: obj.x + scaledUnit(8), y: obj.y - scaledUnit(28) };
    }

    if ((obj.type === 'polygon' || obj.type === 'perimeter') && Array.isArray(obj.points) && obj.points.length > 0) {
      let minX = obj.points[0].x;
      let minY = obj.points[0].y;
      obj.points.forEach((point) => {
        minX = Math.min(minX, point.x);
        minY = Math.min(minY, point.y);
      });
      return { x: minX, y: minY - scaledUnit(6) };
    }

    return { x: scaledUnit(10), y: scaledUnit(20) };
  }

  function drawDrafts() {
    if (state.draftShape && state.draftShape.type === 'rect') {
      const rect = buildRectFromPoints(state.draftShape.start, state.draftShape.current || state.draftShape.start);
      ctx.save();
      ctx.setLineDash([scaledUnit(8), scaledUnit(6)]);
      ctx.strokeStyle = '#93c5fd';
      ctx.lineWidth = scaledUnit(2);
      ctx.strokeRect(rect.x, rect.y, rect.width, rect.height);
      ctx.restore();
    }

    if (state.draftShape && state.draftShape.type === 'circle') {
      const circle = buildCircleFromPoints(state.draftShape.start, state.draftShape.current || state.draftShape.start);
      ctx.save();
      ctx.beginPath();
      ctx.arc(circle.x, circle.y, circle.radius, 0, Math.PI * 2);
      ctx.setLineDash([scaledUnit(8), scaledUnit(6)]);
      ctx.strokeStyle = '#93c5fd';
      ctx.lineWidth = scaledUnit(2);
      ctx.stroke();
      ctx.restore();
    }

    if (state.tempPoints.length > 0) {
      ctx.save();
      ctx.strokeStyle = state.tool === TOOL.PERIMETER ? '#10b981' : '#93c5fd';
      ctx.lineWidth = scaledUnit(2);
      ctx.setLineDash([scaledUnit(8), scaledUnit(4)]);
      ctx.beginPath();
      ctx.moveTo(state.tempPoints[0].x, state.tempPoints[0].y);
      for (let i = 1; i < state.tempPoints.length; i += 1) {
        ctx.lineTo(state.tempPoints[i].x, state.tempPoints[i].y);
      }
      if (state.hoverPoint) {
        ctx.lineTo(state.hoverPoint.x, state.hoverPoint.y);
      }
      ctx.stroke();
      ctx.setLineDash([]);

      state.tempPoints.forEach((point, index) => {
        ctx.beginPath();
        ctx.arc(point.x, point.y, scaledUnit(index === 0 ? 6 : 4), 0, Math.PI * 2);
        ctx.fillStyle = index === 0 ? '#60a5fa' : '#ffffff';
        ctx.fill();
      });
      ctx.restore();
    }
  }

  function renderAll() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    drawBackground();
    drawGrid();
    drawObjects();
    drawDrafts();
    renderSelectedDetails();
    renderSummary();
    renderValidation();
    updateCanvasDisplaySize();
  }

  function drawBackground() {
    const gradient = ctx.createLinearGradient(0, 0, canvas.width, canvas.height);
    gradient.addColorStop(0, '#2b3340');
    gradient.addColorStop(1, '#1b2430');
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, canvas.width, canvas.height);
  }

  function renderSelectedDetails() {
    const obj = getSelectedObject();
    if (!obj) {
      elements.selectedObjectDetails.className = 'selected-details empty-state';
      elements.selectedObjectDetails.textContent = 'No object selected.';
      return;
    }

    elements.selectedObjectDetails.className = 'selected-details';
    const lines = [
      `<div><strong>ID:</strong> ${escapeHtml(obj.id)}</div>`,
      `<div><strong>Type:</strong> ${escapeHtml(obj.type)}</div>`
    ];

    if (obj.type === 'flag') {
      lines.push(`<div><strong>Team:</strong> ${escapeHtml(obj.team)}</div>`);
      lines.push(`<div><strong>Position:</strong> (${round2(obj.x)}, ${round2(obj.y)})</div>`);
      lines.push('<div class="point-chip">You can move the flag by dragging it.</div>');
    }

    if (obj.type === 'rect') {
      lines.push(`<div><strong>Position:</strong> (${round2(obj.x)}, ${round2(obj.y)})</div>`);
      lines.push(`<div><strong>Size:</strong> ${round2(obj.width)} × ${round2(obj.height)}</div>`);
    }

    if (obj.type === 'circle') {
      lines.push(`<div><strong>Center:</strong> (${round2(obj.x)}, ${round2(obj.y)})</div>`);
      lines.push(`<div><strong>Radius:</strong> ${round2(obj.radius)}</div>`);
      lines.push('<div class="point-chip">The circle moves as a whole and does not expose editable handles.</div>');
    }

    if (obj.type === 'polygon' || obj.type === 'perimeter') {
      lines.push(`<div><strong>Vertices:</strong> ${obj.points.length}</div>`);
      lines.push(`<div><strong>Approx. area:</strong> ${round2(Math.abs(polygonArea(obj.points)))}</div>`);
    }

    const editablePoints = getEditablePointsForObject(obj);
    if (editablePoints.length > 0) {
      lines.push('<div class="point-chip">Drag the white object handles to adjust its geometry.</div>');
      lines.push(
        `<div class="selected-points">${editablePoints
          .map(
            (point) =>
              `<div class="point-chip"><strong>${escapeHtml(point.label)}:</strong> (${round2(point.x)}, ${round2(point.y)})</div>`
          )
          .join('')}</div>`
      );
    }

    elements.selectedObjectDetails.innerHTML = lines.join('');
  }

  function getEditablePointsForObject(obj) {
    if (!obj) {
      return [];
    }

    if (obj.type === 'rect') {
      const corners = getRectCorners(obj);
      const names = ['Top left', 'Top right', 'Bottom right', 'Bottom left'];
      return corners.map((point, index) => ({ label: names[index], x: point.x, y: point.y }));
    }

    if (obj.type === 'polygon' || obj.type === 'perimeter') {
      return obj.points.map((point, index) => ({ label: `Point ${index + 1}`, x: point.x, y: point.y }));
    }

    return [];
  }

  function renderSummary() {
    const counts = {
      perimeter: 0,
      polygon: 0,
      rect: 0,
      circle: 0,
      flagBlue: 0,
      flagRed: 0
    };

    state.objects.forEach((obj) => {
      if (obj.type === 'perimeter') counts.perimeter += 1;
      if (obj.type === 'polygon') counts.polygon += 1;
      if (obj.type === 'rect') counts.rect += 1;
      if (obj.type === 'circle') counts.circle += 1;
      if (obj.type === 'flag' && obj.team === 'blue') counts.flagBlue += 1;
      if (obj.type === 'flag' && obj.team === 'red') counts.flagRed += 1;
    });

    elements.summaryBlock.innerHTML = [
      `<div><strong>Map:</strong> ${escapeHtml(state.mapName)}</div>`,
      `<div><strong>Canvas:</strong> ${canvas.width} × ${canvas.height}</div>`,
      `<div><strong>Zoom:</strong> ${Math.round(state.zoom * 100)}%</div>`,
      `<div><strong>API:</strong> ${escapeHtml(API_BASE)}</div>`,
      `<div><strong>Total objects:</strong> ${state.objects.length}</div>`,
      `<div><span class="summary-pill">Perimeter: ${counts.perimeter}</span><span class="summary-pill">Polygons: ${counts.polygon}</span><span class="summary-pill">Rectangles: ${counts.rect}</span><span class="summary-pill">Circles: ${counts.circle}</span><span class="summary-pill">Blue flags: ${counts.flagBlue}</span><span class="summary-pill">Red flags: ${counts.flagRed}</span></div>`
    ].join('');
  }

  function renderValidation() {
    const result = validateMap(state.objects);

    if (result.errors.length === 0 && result.warnings.length === 0) {
      elements.validationText.textContent = 'OK';
      elements.validationText.style.color = '#34d399';
      return;
    }

    const fragments = [];
    result.errors.forEach((text) => {
      fragments.push(`<div class="note-error">${escapeHtml(text)}</div>`);
    });
    result.warnings.forEach((text) => {
      fragments.push(`<div class="note-warning">${escapeHtml(text)}</div>`);
    });

    elements.validationText.textContent = `${result.errors.length} error(s), ${result.warnings.length} warning(s)`;
    elements.validationText.style.color = result.errors.length > 0 ? '#fca5a5' : '#fbbf24';

    if (fragments.length > 0) {
      elements.summaryBlock.insertAdjacentHTML('beforeend', fragments.join(''));
    }
  }

  function validateMap(objects) {
    const errors = [];
    const warnings = [];

    const perimeters = objects.filter((obj) => obj.type === 'perimeter');
    const blueFlags = objects.filter((obj) => obj.type === 'flag' && obj.team === 'blue');
    const redFlags = objects.filter((obj) => obj.type === 'flag' && obj.team === 'red');

    if (perimeters.length === 0) {
      errors.push('The map is missing a hard perimeter.');
    }
    if (perimeters.length > 1) {
      errors.push('Only one hard perimeter is allowed.');
    }
    if (blueFlags.length === 0) {
      errors.push('The blue flag is missing.');
    }
    if (redFlags.length === 0) {
      errors.push('The red flag is missing.');
    }
    if (blueFlags.length > 1) {
      warnings.push('There is more than one blue flag. The game should only use one per team.');
    }
    if (redFlags.length > 1) {
      warnings.push('There is more than one red flag. The game should only use one per team.');
    }

    if (perimeters.length === 1) {
      const perimeter = perimeters[0];
      objects.forEach((obj) => {
        if (obj.type === 'flag') {
          if (!pointInPolygon({ x: obj.x, y: obj.y }, perimeter.points)) {
            warnings.push(`${obj.id} is outside the perimeter.`);
          }
        }
      });
    }

    return { errors, warnings };
  }

  function getCanvasPoint(event, options = {}) {
    return getCanvasPointFromClient(event.clientX, event.clientY, options);
  }

  function getCanvasPointFromClient(clientX, clientY, options = {}) {
    const canvasRect = canvas.getBoundingClientRect();
    const rectWidth = Math.max(canvasRect.width, 1);
    const rectHeight = Math.max(canvasRect.height, 1);

    let x = ((clientX - canvasRect.left) * canvas.width) / rectWidth;
    let y = ((clientY - canvasRect.top) * canvas.height) / rectHeight;

    if (options.snap && elements.snapToGridInput.checked) {
      x = snapValue(x);
      y = snapValue(y);
    }

    return {
      x: clamp(x, 0, canvas.width),
      y: clamp(y, 0, canvas.height)
    };
  }

  function getCanvasOffsetWithinWrapper() {
    const wrapper = elements.canvasWrapper;
    const wrapperRect = wrapper.getBoundingClientRect();
    const canvasRect = canvas.getBoundingClientRect();

    return {
      x: canvasRect.left - wrapperRect.left - wrapper.clientLeft + wrapper.scrollLeft,
      y: canvasRect.top - wrapperRect.top - wrapper.clientTop + wrapper.scrollTop
    };
  }

  function updateCanvasDisplaySize() {
    const displayWidth = canvas.width * state.zoom;
    const displayHeight = canvas.height * state.zoom;
    elements.canvasContent.style.width = `${displayWidth}px`;
    elements.canvasContent.style.height = `${displayHeight}px`;
    canvas.style.width = `${displayWidth}px`;
    canvas.style.height = `${displayHeight}px`;
    elements.zoomValue.textContent = `${Math.round(state.zoom * 100)}%`;
  }

  function setZoom(nextZoom, options = {}) {
    const normalizedZoom = roundZoom(clamp(nextZoom, ZOOM_MIN, ZOOM_MAX));
    if (!Number.isFinite(normalizedZoom) || normalizedZoom === state.zoom) {
      return;
    }

    const wrapper = elements.canvasWrapper;
    const wrapperRect = wrapper.getBoundingClientRect();
    const focusClientX = options.clientX ?? wrapperRect.left + wrapper.clientLeft + wrapper.clientWidth / 2;
    const focusClientY = options.clientY ?? wrapperRect.top + wrapper.clientTop + wrapper.clientHeight / 2;
    const viewportX = clamp(focusClientX - wrapperRect.left - wrapper.clientLeft, 0, wrapper.clientWidth);
    const viewportY = clamp(focusClientY - wrapperRect.top - wrapper.clientTop, 0, wrapper.clientHeight);

    const focusCanvasPoint =
      Number.isFinite(options.canvasX) && Number.isFinite(options.canvasY)
        ? {
            x: clamp(options.canvasX, 0, canvas.width),
            y: clamp(options.canvasY, 0, canvas.height)
          }
        : getCanvasPointFromClient(focusClientX, focusClientY);

    state.zoom = normalizedZoom;
    updateCanvasDisplaySize();
    renderAll();

    const canvasOffset = getCanvasOffsetWithinWrapper();
    wrapper.scrollLeft = Math.max(0, canvasOffset.x + focusCanvasPoint.x * state.zoom - viewportX);
    wrapper.scrollTop = Math.max(0, canvasOffset.y + focusCanvasPoint.y * state.zoom - viewportY);
  }

  function zoomToFit(options = {}) {
    const availableWidth = Math.max(1, elements.canvasWrapper.clientWidth - 24);
    const availableHeight = Math.max(1, elements.canvasWrapper.clientHeight - 24);
    const fitZoom = Math.min(availableWidth / canvas.width, availableHeight / canvas.height);
    setZoom(fitZoom, options);
  }

  function updateCanvasCursor(point = null, forceDefault = false) {
    if (forceDefault) {
      canvas.style.cursor = 'default';
      return;
    }

    if (state.tool !== TOOL.SELECT) {
      canvas.style.cursor = 'crosshair';
      return;
    }

    if (state.dragInfo) {
      canvas.style.cursor = 'grabbing';
      return;
    }

    if (state.ctrlPressed) {
      canvas.style.cursor = 'grab';
      return;
    }

    const testPoint = point || state.mouseCanvas || { x: 0, y: 0 };
    const selected = getSelectedObject();
    if (selected && findEditableHandleAt(testPoint, selected)) {
      canvas.style.cursor = 'crosshair';
      return;
    }

    if (findTopmostObjectAt(testPoint)) {
      canvas.style.cursor = 'move';
      return;
    }

    canvas.style.cursor = 'default';
  }

  function isTypingTarget(target) {
    if (!target) {
      return false;
    }
    const tagName = String(target.tagName || '').toLowerCase();
    return tagName === 'input' || tagName === 'textarea' || tagName === 'select' || target.isContentEditable;
  }

  function snapValue(value) {
    return Math.round(value / GRID_SIZE) * GRID_SIZE;
  }

  function snapPoint(point) {
    return {
      x: snapValue(point.x),
      y: snapValue(point.y)
    };
  }

  function snapDelta(point) {
    return {
      x: Math.round(point.x / GRID_SIZE) * GRID_SIZE,
      y: Math.round(point.y / GRID_SIZE) * GRID_SIZE
    };
  }

  function distance(a, b) {
    return Math.hypot(a.x - b.x, a.y - b.y);
  }

  function pointInPolygon(point, polygon) {
    let inside = false;
    for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
      const xi = polygon[i].x;
      const yi = polygon[i].y;
      const xj = polygon[j].x;
      const yj = polygon[j].y;

      const intersect =
        yi > point.y !== yj > point.y &&
        point.x < ((xj - xi) * (point.y - yi)) / ((yj - yi) || Number.EPSILON) + xi;
      if (intersect) inside = !inside;
    }
    return inside;
  }

  function isPointNearPolygonEdge(point, polygon, threshold) {
    for (let i = 0; i < polygon.length; i += 1) {
      const a = polygon[i];
      const b = polygon[(i + 1) % polygon.length];
      if (distanceToSegment(point, a, b) <= threshold) {
        return true;
      }
    }
    return false;
  }

  function distanceToSegment(p, a, b) {
    const l2 = (b.x - a.x) ** 2 + (b.y - a.y) ** 2;
    if (l2 === 0) {
      return distance(p, a);
    }
    let t = ((p.x - a.x) * (b.x - a.x) + (p.y - a.y) * (b.y - a.y)) / l2;
    t = Math.max(0, Math.min(1, t));
    const projection = {
      x: a.x + t * (b.x - a.x),
      y: a.y + t * (b.y - a.y)
    };
    return distance(p, projection);
  }

  function polygonArea(points) {
    let area = 0;
    for (let i = 0; i < points.length; i += 1) {
      const j = (i + 1) % points.length;
      area += points[i].x * points[j].y;
      area -= points[j].x * points[i].y;
    }
    return area / 2;
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }

  function clampInt(value, min, max, fallback) {
    if (!Number.isFinite(value)) {
      return fallback;
    }
    return Math.round(clamp(value, min, max));
  }

  function round2(value) {
    return Math.round((Number(value) || 0) * 100) / 100;
  }

  function roundZoom(value) {
    return Math.round(value * 100) / 100;
  }

  function scaledUnit(px) {
    return px / state.zoom;
  }

  function handleRadius() {
    return scaledUnit(HANDLE_RADIUS_SCREEN);
  }

  function handleHitRadius() {
    return scaledUnit(HANDLE_HIT_RADIUS_SCREEN);
  }

  function escapeHtml(text) {
    return String(text)
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function slugify(text) {
    return String(text)
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/(^-|-$)/g, '') || 'map';
  }

  function setStatus(message) {
    elements.statusText.textContent = message;
  }

  init();
})();
