const canvas = document.getElementById('gameCanvas');
const ctx = canvas.getContext('2d');
const canvasWrapEl = document.getElementById('canvasWrap');
const connectToggleBtn = document.getElementById('connectToggleBtn');
const resetGameBtn = document.getElementById('resetGameBtn');
const installAppBtn = document.getElementById('installAppBtn');
const playerNameInput = document.getElementById('playerName');
const connectionStatusEl = document.getElementById('connectionStatus');
const gameInfoLine1El = document.getElementById('gameInfoLine1');
const pingValueEl = document.getElementById('gameInfoLine4');
const blueScoreEl = document.getElementById('blueScore');
const redScoreEl = document.getElementById('redScore');
const matchTimerEl = document.getElementById('matchTimer');
const matchResultEl = document.getElementById('matchResult');
const matchResultTitleEl = document.getElementById('matchResultTitle');
const matchResultScoresEl = document.getElementById('matchResultScores');
const matchResultDetailsEl = document.getElementById('matchResultDetails');
const matchResultResetBtn = document.getElementById('matchResultResetBtn');
const viewportShellEl = document.getElementById('viewportShell');
const appShellEl = document.getElementById('appShell');
const sideDrawerEl = document.getElementById('sideDrawer');
const menuToggleBtn = document.getElementById('menuToggleBtn');
const closeMenuBtn = document.getElementById('closeMenuBtn');
const drawerBackdropEl = document.getElementById('drawerBackdrop');
const rotateHintEl = document.getElementById('rotateHint');
const mobileControlsEl = document.getElementById('mobileControls');
const moveZoneEl = document.getElementById('moveZone');
const joystickBaseEl = document.getElementById('joystickBase');
const joystickStickEl = document.getElementById('joystickStick');
const shootZoneEl = document.getElementById('shootZone');

const DEFAULT_HTTP_SERVER_BASE = 'http://127.0.0.1:5770';
const DEFAULT_PUBLIC_PATH = '';
const EARTH_COLORS = ['#54493b', '#635644', '#72624e', '#816e57', '#8e7a5e', '#9c8666', '#aa906e', '#b89b75', '#c5a67c', '#d2b183'];
const JOYSTICK_RADIUS = 44;
const JOYSTICK_DEADZONE = 14;
const HIT_EFFECT_DURATION_MS = 320;
const IMPACT_SPARK_DURATION_MS = 180;
const RECENT_PLAYER_IMPACT_TTL_MS = 180;
const PLAYER_IMPACT_PROXIMITY_PX = 14;
const SEEN_EVENT_TTL_MS = 15000;
const STATE_WATCHDOG_INTERVAL_MS = 1000;
const STATE_WATCHDOG_TIMEOUT_MS = 5000;
const RECONNECT_BASE_DELAY_MS = 1000;
const RECONNECT_MAX_DELAY_MS = 5000;

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

function toWebSocketBase(httpBase) {
  if (httpBase.startsWith('https://')) {
    return `wss://${httpBase.slice('https://'.length)}`;
  }
  if (httpBase.startsWith('http://')) {
    return `ws://${httpBase.slice('http://'.length)}`;
  }
  if (httpBase.startsWith('wss://') || httpBase.startsWith('ws://')) {
    return httpBase;
  }
  return `ws://${httpBase}`;
}

const runtimeConfig = getRuntimeConfig();
const HTTP_SERVER_BASE = runtimeConfig.serverBase;
const PUBLIC_PATH = runtimeConfig.publicPath;
const API_BASE = `${HTTP_SERVER_BASE}${PUBLIC_PATH}/api`;
const WS_URL = `${toWebSocketBase(HTTP_SERVER_BASE)}${PUBLIC_PATH}/ws`;

const state = {
  socket: null,
  connected: false,
  map: null,
  mapById: new Map(),
  obstacleColors: new Map(),
  myPlayerId: null,
  myTeam: null,
  scores: { blue: 0, red: 0 },
  match: {
    status: 'running',
    durationSeconds: 300,
    remainingMs: 300000,
    winnerTeam: null,
    loserTeam: null,
    isTie: false,
  },
  players: [],
  flags: [],
  shots: [],
  shotSignatures: new Set(),
  effects: [],
  seenEventIds: new Map(),
  recentPlayerImpacts: [],
  lastFrameTime: null,
  input: { up: false, down: false, left: false, right: false },
  mapName: '-',
  pingMs: null,
  pingIntervalId: null,
  inputIntervalId: null,
  pingNonce: 1,
  pendingPings: new Map(),
  watchdogIntervalId: null,
  reconnectTimeoutId: null,
  lastStateReceivedAt: null,
  desiredOnline: false,
  reconnectAttempts: 0,
  mobile: {
    enabled: false,
    movePointerId: null,
    startX: 0,
    startY: 0,
    dx: 0,
    dy: 0,
    shootPointerIds: new Set(),
  },
  pwa: {
    deferredPrompt: null,
  },
  ui: {
    menuOpen: false,
  },
  viewport: {
    cssWidth: 1400,
    cssHeight: 900,
    portraitPlayable: false,
  },
  camera: {
    x: 0,
    y: 0,
    viewWidth: 1400,
    viewHeight: 900,
  },
};

const TEAM_COLORS = {
  blue: '#4f92ff',
  red: '#ff6565',
};

function setCanvasAccent(team) {
  const normalized = team === 'blue' || team === 'red' ? team : null;
  const border = normalized ? toRgba(TEAM_COLORS[normalized], 0.9) : 'rgba(194, 205, 246, 0.22)';
  const glow = normalized ? toRgba(TEAM_COLORS[normalized], 0.24) : 'rgba(194, 205, 246, 0.18)';
  canvasWrapEl.style.setProperty('--canvas-accent', border);
  canvasWrapEl.style.setProperty('--canvas-accent-glow', glow);
}

playerNameInput.value = localStorage.getItem('ctf-player-name') || '';
setCanvasAccent(null);

async function boot() {
  await loadMap();
  setupEvents();
  updateLayoutMetrics();
  updateTouchLayout();
  updateInstallAvailability();
  updateConnectButton();
  updatePingLine();
  updateMatchHud();
  syncMenuState();
  requestAnimationFrame(renderLoop);
  registerServiceWorker();
}

async function loadMap() {
  const response = await fetch(`${API_BASE}/map`, { cache: 'no-store' });
  if (!response.ok) {
    throw new Error('Could not load the map');
  }

  state.map = await response.json();
  state.mapName = state.map?.meta?.name || '-';
  gameInfoLine1El.textContent = `Map: ${state.mapName}`;
  updateLayoutMetrics();
  state.mapById.clear();
  state.obstacleColors.clear();

  for (const obj of state.map.objects) {
    state.mapById.set(obj.id, obj);
    if (obj.type === 'rect' || obj.type === 'circle' || obj.type === 'polygon') {
      state.obstacleColors.set(obj.id, pickEarthColor());
    }
  }
}

function pickEarthColor() {
  const index = Math.floor(Math.random() * EARTH_COLORS.length);
  return EARTH_COLORS[index];
}

function getWorldSize() {
  return {
    width: state.map?.meta?.canvas?.width || 1400,
    height: state.map?.meta?.canvas?.height || 900,
  };
}

function clamp(value, min, max) {
  return Math.min(Math.max(value, min), max);
}

function updateCanvasBackingStore(cssWidth, cssHeight) {
  const safeWidth = Math.max(1, Math.floor(cssWidth));
  const safeHeight = Math.max(1, Math.floor(cssHeight));
  const deviceScale = Math.min(window.devicePixelRatio || 1, 2);
  const pixelWidth = Math.max(1, Math.round(safeWidth * deviceScale));
  const pixelHeight = Math.max(1, Math.round(safeHeight * deviceScale));

  if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
    canvas.width = pixelWidth;
    canvas.height = pixelHeight;
  }
}

function getViewportAspectRatio() {
  return canvas.height > 0 ? canvas.width / canvas.height : 1400 / 900;
}

function computeTargetCamera() {
  const world = getWorldSize();

  if (!state.viewport.portraitPlayable) {
    return {
      x: 0,
      y: 0,
      viewWidth: world.width,
      viewHeight: world.height,
    };
  }

  const aspectRatio = Math.max(0.24, Math.min(1, getViewportAspectRatio()));
  const viewHeight = world.height;
  const viewWidth = Math.min(world.width, Math.max(320, viewHeight * aspectRatio));
  const player = state.players.find((item) => item.id === state.myPlayerId);
  const facingX = player && Number.isFinite(player.facingX) ? player.facingX : 0;
  const lookAhead = viewWidth * 0.14;
  const anchorX = player ? player.x + facingX * lookAhead : world.width / 2;
  const x = clamp(anchorX - viewWidth / 2, 0, Math.max(0, world.width - viewWidth));

  return {
    x,
    y: 0,
    viewWidth,
    viewHeight,
  };
}

function updateCamera(dtMs = 16) {
  const target = computeTargetCamera();

  if (!state.viewport.portraitPlayable) {
    state.camera = target;
    return;
  }

  const blend = Math.min(1, Math.max(0.12, dtMs / 180));
  state.camera.x += (target.x - state.camera.x) * blend;
  state.camera.y += (target.y - state.camera.y) * blend;
  state.camera.viewWidth += (target.viewWidth - state.camera.viewWidth) * blend;
  state.camera.viewHeight += (target.viewHeight - state.camera.viewHeight) * blend;
}

function applyWorldTransform() {
  const world = getWorldSize();
  const viewWidth = Math.max(1, state.camera.viewWidth || world.width);
  const viewHeight = Math.max(1, state.camera.viewHeight || world.height);
  const scaleX = canvas.width / viewWidth;
  const scaleY = canvas.height / viewHeight;
  ctx.setTransform(scaleX, 0, 0, scaleY, -state.camera.x * scaleX, -state.camera.y * scaleY);
}

function resetScreenTransform() {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
}

function setupEvents() {
  menuToggleBtn.addEventListener('click', toggleMenu);
  closeMenuBtn.addEventListener('click', closeMenu);
  drawerBackdropEl.addEventListener('click', closeMenu);

  connectToggleBtn.addEventListener('click', () => {
    const socket = state.socket;
    const isOnline = socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING);
    if (isOnline) {
      disconnect();
    } else {
      connect();
    }
    closeMenu();
  });

  resetGameBtn.addEventListener('click', () => requestMatchReset());
  matchResultResetBtn.addEventListener('click', () => requestMatchReset());

  installAppBtn.addEventListener('click', installPwa);

  playerNameInput.addEventListener('change', () => {
    localStorage.setItem('ctf-player-name', playerNameInput.value.trim());
    send({ type: 'hello', name: playerNameInput.value.trim() || 'Player' });
  });

  const mapKey = (code, pressed) => {
    let updated = false;

    switch (code) {
      case 'KeyW':
      case 'ArrowUp':
        updated = updateDirectionalKey('up', pressed);
        break;
      case 'KeyS':
      case 'ArrowDown':
        updated = updateDirectionalKey('down', pressed);
        break;
      case 'KeyA':
      case 'ArrowLeft':
        updated = updateDirectionalKey('left', pressed);
        break;
      case 'KeyD':
      case 'ArrowRight':
        updated = updateDirectionalKey('right', pressed);
        break;
      default:
        return false;
    }

    if (updated) {
      sendCurrentInput();
    }

    return true;
  };

  window.addEventListener('keydown', (event) => {
    if (event.code === 'Escape' && state.ui.menuOpen) {
      event.preventDefault();
      closeMenu();
      return;
    }

    if (isTextInputFocused()) {
      return;
    }

    if (event.code === 'Space') {
      event.preventDefault();
      if (!event.repeat) {
        shoot();
      }
      return;
    }

    if (mapKey(event.code, true)) {
      event.preventDefault();
    }
  });

  window.addEventListener('keyup', (event) => {
    if (mapKey(event.code, false)) {
      event.preventDefault();
    }
  });

  canvas.addEventListener('mousedown', (event) => {
    if (event.button !== 0) {
      return;
    }

    shoot();
  });

  canvas.addEventListener('dragstart', (event) => event.preventDefault());

  setupMobileControls();

  window.addEventListener('beforeinstallprompt', (event) => {
    event.preventDefault();
    state.pwa.deferredPrompt = event;
    updateInstallAvailability();
  });

  window.addEventListener('appinstalled', () => {
    state.pwa.deferredPrompt = null;
    updateInstallAvailability();
  });

  window.addEventListener('resize', handleViewportChange);
  window.addEventListener('orientationchange', handleViewportChange);
  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', handleViewportChange);
  }
  window.addEventListener('blur', resetTransientInputs);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      resetTransientInputs();
    }
  });

  state.inputIntervalId = window.setInterval(() => {
    if (!state.connected) return;
    sendCurrentInput();
  }, 50);
}

function setupMobileControls() {
  moveZoneEl.addEventListener('pointerdown', (event) => {
    if (!shouldHandleMobilePointer(event) || state.mobile.movePointerId !== null) {
      return;
    }

    event.preventDefault();
    state.mobile.movePointerId = event.pointerId;
    state.mobile.startX = event.clientX;
    state.mobile.startY = event.clientY;
    state.mobile.dx = 0;
    state.mobile.dy = 0;
    showJoystickAt(event.clientX, event.clientY);
    if (typeof moveZoneEl.setPointerCapture === 'function') {
      moveZoneEl.setPointerCapture(event.pointerId);
    }
    applyJoystickVector(0, 0);
  });

  moveZoneEl.addEventListener('pointermove', (event) => {
    if (event.pointerId !== state.mobile.movePointerId) {
      return;
    }

    event.preventDefault();
    const dx = event.clientX - state.mobile.startX;
    const dy = event.clientY - state.mobile.startY;
    state.mobile.dx = dx;
    state.mobile.dy = dy;
    applyJoystickVector(dx, dy);
  });

  const endMoveGesture = (event) => {
    if (event.pointerId !== state.mobile.movePointerId) {
      return;
    }

    event.preventDefault();
    state.mobile.movePointerId = null;
    state.mobile.dx = 0;
    state.mobile.dy = 0;
    hideJoystick();
    applyNeutralMovement();
  };

  moveZoneEl.addEventListener('pointerup', endMoveGesture);
  moveZoneEl.addEventListener('pointercancel', endMoveGesture);
  moveZoneEl.addEventListener('lostpointercapture', (event) => {
    if (event.pointerId === state.mobile.movePointerId) {
      state.mobile.movePointerId = null;
      hideJoystick();
      applyNeutralMovement();
    }
  });

  shootZoneEl.addEventListener('pointerdown', (event) => {
    if (!shouldHandleMobilePointer(event)) {
      return;
    }

    event.preventDefault();
    state.mobile.shootPointerIds.add(event.pointerId);
    if (typeof shootZoneEl.setPointerCapture === 'function') {
      shootZoneEl.setPointerCapture(event.pointerId);
    }
    shoot();
  });

  const releaseShootPointer = (event) => {
    if (!state.mobile.shootPointerIds.has(event.pointerId)) {
      return;
    }

    event.preventDefault();
    state.mobile.shootPointerIds.delete(event.pointerId);
    if (typeof shootZoneEl.releasePointerCapture === 'function' && shootZoneEl.hasPointerCapture?.(event.pointerId)) {
      shootZoneEl.releasePointerCapture(event.pointerId);
    }
  };

  shootZoneEl.addEventListener('pointerup', releaseShootPointer);
  shootZoneEl.addEventListener('pointercancel', releaseShootPointer);
  shootZoneEl.addEventListener('lostpointercapture', (event) => {
    state.mobile.shootPointerIds.delete(event.pointerId);
  });
}

function shouldHandleMobilePointer(event) {
  return state.mobile.enabled && event.pointerType !== 'mouse';
}

function handleViewportChange() {
  updateLayoutMetrics();
  updateTouchLayout();
}

function updateLayoutMetrics() {
  const world = getWorldSize();
  const viewportRect = viewportShellEl.getBoundingClientRect();
  const viewportStyles = window.getComputedStyle(viewportShellEl);
  const paddingLeft = parseFloat(viewportStyles.paddingLeft || '0');
  const paddingRight = parseFloat(viewportStyles.paddingRight || '0');
  const paddingTop = parseFloat(viewportStyles.paddingTop || '0');
  const paddingBottom = parseFloat(viewportStyles.paddingBottom || '0');
  const availableWidth = Math.max(0, viewportRect.width - paddingLeft - paddingRight);
  const availableHeight = Math.max(0, viewportRect.height - paddingTop - paddingBottom);
  const portraitPlayable = state.mobile.enabled && availableHeight > availableWidth;

  let cssWidth = availableWidth;
  let cssHeight = availableHeight;

  if (!portraitPlayable) {
    const scale = Math.min(
      availableWidth / Math.max(1, world.width),
      availableHeight / Math.max(1, world.height)
    );
    const safeScale = Number.isFinite(scale) && scale > 0 ? scale : 1;
    cssWidth = Math.max(1, Math.floor(world.width * safeScale));
    cssHeight = Math.max(1, Math.floor(world.height * safeScale));
  } else {
    cssWidth = Math.max(1, Math.floor(availableWidth));
    cssHeight = Math.max(1, Math.floor(availableHeight));
  }

  state.viewport.cssWidth = cssWidth;
  state.viewport.cssHeight = cssHeight;
  state.viewport.portraitPlayable = portraitPlayable;

  canvasWrapEl.style.width = `${cssWidth}px`;
  canvasWrapEl.style.height = `${cssHeight}px`;
  updateCanvasBackingStore(cssWidth, cssHeight);
  document.body.classList.toggle('portrait-playable', portraitPlayable);
}

function updateTouchLayout() {
  const coarsePointer = window.matchMedia('(hover: none) and (pointer: coarse)').matches;
  const hasTouchPoints = navigator.maxTouchPoints > 0;
  const compactTouchViewport = window.innerWidth <= 1180 && hasTouchPoints;
  const portraitTouch = (coarsePointer || hasTouchPoints) && window.innerHeight > window.innerWidth;

  state.mobile.enabled = coarsePointer || compactTouchViewport;
  document.body.classList.toggle('has-touch-controls', state.mobile.enabled);
  document.body.classList.toggle('touch-portrait', portraitTouch);
  mobileControlsEl.setAttribute('aria-hidden', state.mobile.enabled ? 'false' : 'true');
  rotateHintEl.setAttribute('aria-hidden', 'true');

  updateLayoutMetrics();

  if (!state.mobile.enabled) {
    resetTransientInputs();
  }
}

function syncMenuState() {
  document.body.classList.toggle('menu-open', state.ui.menuOpen);
  menuToggleBtn.setAttribute('aria-expanded', state.ui.menuOpen ? 'true' : 'false');
  sideDrawerEl.setAttribute('aria-hidden', state.ui.menuOpen ? 'false' : 'true');
  drawerBackdropEl.setAttribute('aria-hidden', state.ui.menuOpen ? 'false' : 'true');
}

function openMenu() {
  if (state.ui.menuOpen) {
    return;
  }

  state.ui.menuOpen = true;
  syncMenuState();
}

function closeMenu() {
  if (!state.ui.menuOpen) {
    return;
  }

  state.ui.menuOpen = false;
  syncMenuState();
}

function toggleMenu() {
  if (state.ui.menuOpen) {
    closeMenu();
  } else {
    openMenu();
  }
}

function showJoystickAt(x, y) {
  joystickBaseEl.classList.remove('hidden');
  const bounds = moveZoneEl.getBoundingClientRect();
  joystickBaseEl.style.left = `${x - bounds.left}px`;
  joystickBaseEl.style.top = `${y - bounds.top}px`;
  joystickStickEl.style.transform = 'translate(0px, 0px)';
}

function hideJoystick() {
  joystickBaseEl.classList.add('hidden');
  joystickStickEl.style.transform = 'translate(0px, 0px)';
}

function applyJoystickVector(dx, dy) {
  const distance = Math.hypot(dx, dy);
  const limitedDistance = Math.min(distance, JOYSTICK_RADIUS);
  const angle = distance > 0 ? Math.atan2(dy, dx) : 0;
  const stickX = Math.cos(angle) * limitedDistance;
  const stickY = Math.sin(angle) * limitedDistance;
  joystickStickEl.style.transform = `translate(${stickX}px, ${stickY}px)`;

  if (distance < JOYSTICK_DEADZONE) {
    applyNeutralMovement();
    return;
  }

  const snapped = snapToEightDirections(angle);
  const nextInput = {
    up: snapped.up,
    down: snapped.down,
    left: snapped.left,
    right: snapped.right,
  };
  applyInputState(nextInput);
}

function snapToEightDirections(angle) {
  const step = Math.PI / 4;
  const index = ((Math.round(angle / step) % 8) + 8) % 8;
  const directions = [
    { up: false, down: false, left: false, right: true },
    { up: false, down: true, left: false, right: true },
    { up: false, down: true, left: false, right: false },
    { up: false, down: true, left: true, right: false },
    { up: false, down: false, left: true, right: false },
    { up: true, down: false, left: true, right: false },
    { up: true, down: false, left: false, right: false },
    { up: true, down: false, left: false, right: true },
  ];
  return directions[index];
}

function applyNeutralMovement() {
  applyInputState({ up: false, down: false, left: false, right: false });
}

function applyInputState(nextInput) {
  const changed = [
    'up',
    'down',
    'left',
    'right'
  ].some((key) => state.input[key] !== nextInput[key]);

  if (!changed) {
    return;
  }

  state.input.up = nextInput.up;
  state.input.down = nextInput.down;
  state.input.left = nextInput.left;
  state.input.right = nextInput.right;
  sendCurrentInput();
}

function updateDirectionalKey(key, pressed) {
  if (state.input[key] === pressed) {
    return false;
  }

  state.input[key] = pressed;
  return true;
}

function resetTransientInputs() {
  state.mobile.movePointerId = null;
  state.mobile.shootPointerIds.clear();
  hideJoystick();
  applyNeutralMovement();
}

function isTextInputFocused() {
  return document.activeElement === playerNameInput;
}

function connect() {
  state.desiredOnline = true;
  clearReconnectTimer();

  if (state.socket && (state.socket.readyState === WebSocket.OPEN || state.socket.readyState === WebSocket.CONNECTING)) {
    return;
  }

  const socket = new WebSocket(WS_URL);
  state.socket = socket;
  setConnectionStatus('connecting');
  updateConnectButton();
  resetPing();
  updatePingLine('measuring...');

  socket.addEventListener('open', () => {
    state.connected = true;
    state.reconnectAttempts = 0;
    state.lastStateReceivedAt = performance.now();
    setConnectionStatus('connected');
    updateConnectButton();
    closeMenu();
    send({ type: 'hello', name: playerNameInput.value.trim() || 'Player' });
    schedulePingLoop();
    scheduleStateWatchdog();
    sendPing();
    sendCurrentInput();
  });

  socket.addEventListener('message', (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch (error) {
      console.warn('Invalid WebSocket message received', error);
      return;
    }
    if (message.type === 'welcome') {
      state.myPlayerId = message.playerId;
      state.myTeam = message.team;
      setCanvasAccent(message.team);
      return;
    }

    if (message.type === 'pong') {
      handlePong(message);
      return;
    }

    if (message.type === 'state') {
      state.lastStateReceivedAt = performance.now();
      processServerEvents(message.events || []);
      state.players = message.players || [];
      updateMyTeamFromState();
      state.flags = message.flags || [];
      processShotImpacts(message.shots || []);
      state.shots = message.shots || [];
      state.scores = message.scores || { blue: 0, red: 0 };
      state.match = normalizeMatchState(message.match);
      blueScoreEl.textContent = state.scores.blue;
      redScoreEl.textContent = state.scores.red;
      updateMatchHud();
    }
  });

  socket.addEventListener('close', () => {
    const shouldReconnect = state.desiredOnline;
    if (state.socket === socket) {
      state.socket = null;
    }
    state.connected = false;
    state.shots = [];
    state.shotSignatures.clear();
    state.effects = [];
    state.seenEventIds.clear();
    state.recentPlayerImpacts = [];
    state.lastFrameTime = null;
    state.match.status = 'running';
    state.match.remainingMs = Math.max(0, (state.match.durationSeconds || 300) * 1000);
    updateMatchHud();
    state.myPlayerId = null;
    state.myTeam = null;
    setConnectionStatus('disconnected');
    updateConnectButton();
    stopPingLoop();
    stopStateWatchdog();
    resetPing();
    state.lastStateReceivedAt = null;
    setCanvasAccent(null);
    updatePingLine();

    if (shouldReconnect) {
      scheduleReconnect();
    }
  });

  socket.addEventListener('error', () => {
    setConnectionStatus('error');
    updateConnectButton();
    updatePingLine('no response');
  });
}

function requestMatchReset() {
  if (!state.connected) {
    return;
  }

  const accepted = window.confirm('Reset the current match? Scores, flags, timer, teams, and positions will be reset.');
  if (!accepted) {
    return;
  }

  send({ type: 'resetGame' });
}

function disconnect() {
  state.desiredOnline = false;
  clearReconnectTimer();
  stopPingLoop();
  stopStateWatchdog();
  if (state.socket) {
    state.socket.close();
  }
}

function clearReconnectTimer() {
  if (state.reconnectTimeoutId) {
    clearTimeout(state.reconnectTimeoutId);
    state.reconnectTimeoutId = null;
  }
}

function scheduleReconnect() {
  if (!state.desiredOnline || state.reconnectTimeoutId) {
    return;
  }

  const delay = Math.min(
    RECONNECT_MAX_DELAY_MS,
    RECONNECT_BASE_DELAY_MS * Math.max(1, 2 ** state.reconnectAttempts)
  );
  state.reconnectAttempts += 1;
  setConnectionStatus('reconnecting');
  updateConnectButton();
  updatePingLine('reconnecting...');

  state.reconnectTimeoutId = window.setTimeout(() => {
    state.reconnectTimeoutId = null;
    if (state.desiredOnline) {
      connect();
    }
  }, delay);
}

function scheduleStateWatchdog() {
  stopStateWatchdog();
  state.watchdogIntervalId = window.setInterval(checkStateWatchdog, STATE_WATCHDOG_INTERVAL_MS);
}

function stopStateWatchdog() {
  if (state.watchdogIntervalId) {
    clearInterval(state.watchdogIntervalId);
    state.watchdogIntervalId = null;
  }
}

function checkStateWatchdog() {
  const socket = state.socket;
  if (!state.connected || !socket || socket.readyState !== WebSocket.OPEN) {
    return;
  }

  const lastStateAt = state.lastStateReceivedAt;
  if (typeof lastStateAt !== 'number') {
    return;
  }

  const elapsed = performance.now() - lastStateAt;
  if (elapsed <= STATE_WATCHDOG_TIMEOUT_MS) {
    return;
  }

  console.warn(`No state message received for ${Math.round(elapsed)} ms. Reconnecting WebSocket.`);
  setConnectionStatus('state timeout');
  updatePingLine('state timeout');
  stopStateWatchdog();

  try {
    socket.close(4000, 'state watchdog timeout');
  } catch (error) {
    console.warn('Could not close timed-out WebSocket cleanly', error);
    socket.close();
  }
}

function shoot() {
  if (state.match.status === 'finished') {
    return;
  }

  send({ type: 'shoot' });
}

function send(payload) {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
    return;
  }
  state.socket.send(JSON.stringify(payload));
}

function sendCurrentInput() {
  if (state.match.status === 'finished') {
    return;
  }

  send({ type: 'input', ...state.input });
}

function normalizeMatchState(match) {
  const fallbackDurationSeconds = state.match.durationSeconds || 300;
  if (!match || typeof match !== 'object') {
    return {
      status: 'running',
      durationSeconds: fallbackDurationSeconds,
      remainingMs: fallbackDurationSeconds * 1000,
      winnerTeam: null,
      loserTeam: null,
      isTie: false,
    };
  }

  const durationSeconds = Number.isFinite(match.durationSeconds) ? match.durationSeconds : fallbackDurationSeconds;
  const remainingMs = Number.isFinite(match.remainingMs)
    ? Math.max(0, match.remainingMs)
    : Math.max(0, durationSeconds * 1000);
  const status = match.status === 'finished' ? 'finished' : 'running';

  return {
    status,
    durationSeconds,
    remainingMs,
    winnerTeam: match.winnerTeam || null,
    loserTeam: match.loserTeam || null,
    isTie: Boolean(match.isTie) || match.winnerTeam === 'draw',
  };
}

function updateMyTeamFromState() {
  if (!state.myPlayerId) {
    return;
  }

  const me = state.players.find((player) => player.id === state.myPlayerId);
  if (!me || (me.team !== 'blue' && me.team !== 'red')) {
    return;
  }

  if (state.myTeam !== me.team) {
    state.myTeam = me.team;
    setCanvasAccent(me.team);
  }
}

function formatMatchTime(ms) {
  const totalSeconds = Math.max(0, Math.ceil(ms / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0');
}

function formatTeamName(team) {
  if (team === 'blue') {
    return 'BLUE';
  }
  if (team === 'red') {
    return 'RED';
  }
  return 'DRAW';
}

function updateMatchHud() {
  const match = state.match || {};
  const isFinished = match.status === 'finished';
  const blueScore = Number.isFinite(state.scores.blue) ? state.scores.blue : 0;
  const redScore = Number.isFinite(state.scores.red) ? state.scores.red : 0;

  matchTimerEl.textContent = formatMatchTime(match.remainingMs || 0);
  matchTimerEl.classList.toggle('finished', isFinished);

  matchResultEl.classList.toggle('hidden', !isFinished);
  matchResultEl.setAttribute('aria-hidden', isFinished ? 'false' : 'true');

  if (!isFinished) {
    return;
  }

  matchResultScoresEl.textContent = 'Blue ' + blueScore + ' - Red ' + redScore;

  if (match.isTie || match.winnerTeam === 'draw') {
    matchResultTitleEl.textContent = 'Draw';
    matchResultDetailsEl.textContent = 'No winner. Final score: Blue ' + blueScore + ' - Red ' + redScore + '. Press Reset match to start again.';
    return;
  }

  const winner = formatTeamName(match.winnerTeam);
  const loser = formatTeamName(match.loserTeam);
  matchResultTitleEl.textContent = winner + ' wins';
  matchResultDetailsEl.textContent = 'Winner: ' + winner + '. Loser: ' + loser + '. Press Reset match to start again.';
}

function setConnectionStatus(value) {
  connectionStatusEl.textContent = value;
}

function updateConnectButton() {
  const socket = state.socket;
  const isOnline = socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING);
  connectToggleBtn.textContent = isOnline ? 'Disconnect' : 'Connect';
  resetGameBtn.disabled = !state.connected;
  matchResultResetBtn.disabled = !state.connected;
}

function updateInstallAvailability() {
  const isStandalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
  const canInstall = Boolean(state.pwa.deferredPrompt) && !isStandalone;
  installAppBtn.hidden = !canInstall;
}

async function installPwa() {
  const promptEvent = state.pwa.deferredPrompt;
  if (!promptEvent) {
    return;
  }

  promptEvent.prompt();
  try {
    await promptEvent.userChoice;
  } finally {
    state.pwa.deferredPrompt = null;
    updateInstallAvailability();
  }
}

async function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) {
    return;
  }

  if (!window.isSecureContext && window.location.hostname !== 'localhost' && window.location.hostname !== '127.0.0.1') {
    return;
  }

  try {
    await navigator.serviceWorker.register('./sw.js', { scope: './' });
  } catch (error) {
    console.warn('Could not register the service worker', error);
  }
}

function schedulePingLoop() {
  stopPingLoop();
  state.pingIntervalId = window.setInterval(sendPing, 2000);
}

function stopPingLoop() {
  if (state.pingIntervalId) {
    clearInterval(state.pingIntervalId);
    state.pingIntervalId = null;
  }
}

function resetPing() {
  state.pingMs = null;
  state.pendingPings.clear();
}

function sendPing() {
  if (!state.connected) {
    return;
  }

  const nonce = state.pingNonce++;
  state.pendingPings.set(nonce, performance.now());
  send({ type: 'ping', nonce });
}

function handlePong(message) {
  const nonce = Number(message.nonce);
  if (!Number.isFinite(nonce)) {
    return;
  }

  const sentAt = state.pendingPings.get(nonce);
  if (typeof sentAt !== 'number') {
    return;
  }

  state.pendingPings.delete(nonce);
  state.pingMs = Math.max(0, Math.round(performance.now() - sentAt));
  updatePingLine();
}

function updatePingLine(forcedText) {
  if (typeof forcedText === 'string') {
    pingValueEl.textContent = forcedText;
    return;
  }

  if (typeof state.pingMs === 'number') {
    pingValueEl.textContent = `${state.pingMs} ms`;
    return;
  }

  pingValueEl.textContent = '-';
}

function renderLoop(frameTime) {
  if (!Number.isFinite(frameTime)) {
    frameTime = performance.now();
  }

  if (state.lastFrameTime === null) {
    state.lastFrameTime = frameTime;
  }

  const dtMs = Math.min(64, Math.max(0, frameTime - state.lastFrameTime));
  state.lastFrameTime = frameTime;
  updateEffects(dtMs);
  updateCamera(dtMs);
  draw();
  requestAnimationFrame(renderLoop);
}

function draw() {
  if (!state.map) {
    resetScreenTransform();
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    return;
  }

  resetScreenTransform();
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.save();
  applyWorldTransform();
  drawBackgroundGrid();
  drawMap();
  drawFlags();
  drawShots();
  drawEffects();
  drawPlayers();
  ctx.restore();
  drawMinimap();
  drawOverlay();
}

function drawBackgroundGrid() {
  const world = getWorldSize();
  ctx.fillStyle = '#0b1123';
  ctx.fillRect(0, 0, world.width, world.height);

  ctx.strokeStyle = 'rgba(120,140,255,0.05)';
  ctx.lineWidth = 1;
  const step = 40;
  for (let x = 0; x <= world.width; x += step) {
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, world.height);
    ctx.stroke();
  }
  for (let y = 0; y <= world.height; y += step) {
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(world.width, y);
    ctx.stroke();
  }
}

function drawMap() {
  for (const obj of state.map.objects) {
    if (obj.type === 'perimeter') {
      ctx.save();
      ctx.strokeStyle = 'rgba(255,255,255,0.7)';
      ctx.lineWidth = 4;
      ctx.beginPath();
      obj.points.forEach((point, index) => {
        if (index === 0) ctx.moveTo(point.x, point.y);
        else ctx.lineTo(point.x, point.y);
      });
      ctx.closePath();
      ctx.stroke();
      ctx.restore();
      continue;
    }

    if (obj.type === 'flag') {
      continue;
    }

    ctx.save();
    const obstacleColor = state.obstacleColors.get(obj.id) || EARTH_COLORS[0];
    ctx.fillStyle = obj.hard ? obstacleColor : `${obstacleColor}99`;
    ctx.strokeStyle = 'rgba(0,0,0,0.35)';
    ctx.lineWidth = 2;

    if (obj.type === 'rect') {
      ctx.fillRect(obj.x, obj.y, obj.width, obj.height);
      ctx.strokeRect(obj.x, obj.y, obj.width, obj.height);
    } else if (obj.type === 'circle') {
      ctx.beginPath();
      ctx.arc(obj.x, obj.y, obj.radius, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    } else if (obj.type === 'polygon') {
      ctx.beginPath();
      obj.points.forEach((point, index) => {
        if (index === 0) ctx.moveTo(point.x, point.y);
        else ctx.lineTo(point.x, point.y);
      });
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
    }
    ctx.restore();
  }
}

function drawFlags() {
  const runtimeFlags = new Map();
  for (const flag of state.flags) {
    runtimeFlags.set(flag.team, flag);
  }

  for (const obj of state.map.objects.filter((item) => item.type === 'flag')) {
    const runtime = runtimeFlags.get(obj.team);
    const x = runtime ? runtime.x : obj.x;
    const y = runtime ? runtime.y : obj.y;
    const baseX = runtime ? runtime.baseX : obj.x;
    const baseY = runtime ? runtime.baseY : obj.y;
    const color = TEAM_COLORS[obj.team] || '#ffffff';

    ctx.save();
    ctx.strokeStyle = 'rgba(255,255,255,0.25)';
    ctx.setLineDash([6, 6]);
    ctx.beginPath();
    ctx.arc(baseX, baseY, 32, 0, Math.PI * 2);
    ctx.stroke();
    ctx.setLineDash([]);

    ctx.fillStyle = color;
    ctx.strokeStyle = '#0f1116';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(x, y, 10, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();

    ctx.strokeStyle = '#0f1116';
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(x, y + 8);
    ctx.lineTo(x, y - 26);
    ctx.stroke();

    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.moveTo(x, y - 26);
    ctx.lineTo(x + 18, y - 18);
    ctx.lineTo(x, y - 10);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }
}

function processServerEvents(events) {
  const now = performance.now();
  pruneSeenEventIds(now);

  for (const gameEvent of events) {
    if (!gameEvent || gameEvent.type !== 'playerHit') {
      continue;
    }

    const eventId = String(gameEvent.id || '');
    if (!eventId || state.seenEventIds.has(eventId)) {
      continue;
    }

    state.seenEventIds.set(eventId, now);
    rememberPlayerImpact(gameEvent.impactX, gameEvent.impactY, now);
    spawnHitExplosion(gameEvent.impactX, gameEvent.impactY, gameEvent.shooterTeam || gameEvent.victimTeam);
  }
}

function rememberPlayerImpact(x, y, now = performance.now()) {
  const impactX = Number(x);
  const impactY = Number(y);
  if (!Number.isFinite(impactX) || !Number.isFinite(impactY)) {
    return;
  }

  pruneRecentPlayerImpacts(now);
  state.recentPlayerImpacts.push({ x: impactX, y: impactY, time: now });
}

function pruneRecentPlayerImpacts(now = performance.now()) {
  state.recentPlayerImpacts = state.recentPlayerImpacts.filter((impact) => now - impact.time <= RECENT_PLAYER_IMPACT_TTL_MS);
}

function isNearRecentPlayerImpact(x, y, now = performance.now()) {
  pruneRecentPlayerImpacts(now);
  const impactX = Number(x);
  const impactY = Number(y);
  const maxDistance = PLAYER_IMPACT_PROXIMITY_PX;

  return state.recentPlayerImpacts.some((impact) => Math.hypot(impact.x - impactX, impact.y - impactY) <= maxDistance);
}

function getShotSignature(shot) {
  if (!shot || typeof shot !== 'object') {
    return '';
  }

  if (shot.id != null && shot.id !== '') {
    return `id:${shot.id}`;
  }

  const sx = Math.round(Number(shot.startX) || 0);
  const sy = Math.round(Number(shot.startY) || 0);
  const ex = Math.round(Number(shot.endX) || 0);
  const ey = Math.round(Number(shot.endY) || 0);
  const team = String(shot.team || 'neutral');
  return `${team}:${sx}:${sy}:${ex}:${ey}`;
}

function processShotImpacts(shots) {
  const nextSignatures = new Set();
  const now = performance.now();

  for (const shot of shots) {
    const signature = getShotSignature(shot);
    if (!signature) {
      continue;
    }

    nextSignatures.add(signature);
    if (state.shotSignatures.has(signature)) {
      continue;
    }

    const impactX = Number(shot.endX);
    const impactY = Number(shot.endY);
    if (!Number.isFinite(impactX) || !Number.isFinite(impactY)) {
      continue;
    }

    if (isNearRecentPlayerImpact(impactX, impactY, now)) {
      continue;
    }

    spawnImpactSpark(impactX, impactY, shot.team);
  }

  state.shotSignatures = nextSignatures;
}

function pruneSeenEventIds(now) {
  for (const [eventId, seenAt] of state.seenEventIds.entries()) {
    if (now - seenAt > SEEN_EVENT_TTL_MS) {
      state.seenEventIds.delete(eventId);
    }
  }
}

function spawnHitExplosion(x, y, team) {
  const impactX = Number(x);
  const impactY = Number(y);
  if (!Number.isFinite(impactX) || !Number.isFinite(impactY)) {
    return;
  }

  const particleCount = 12 + Math.floor(Math.random() * 6);
  const particles = [];

  for (let index = 0; index < particleCount; index += 1) {
    const angle = (Math.PI * 2 * index) / particleCount + (Math.random() - 0.5) * 0.24;
    particles.push({
      angle,
      speed: 70 + Math.random() * 115,
      radius: 2 + Math.random() * 2.8,
      drift: (Math.random() - 0.5) * 16,
    });
  }

  state.effects.push({
    id: `fx-${Math.random().toString(36).slice(2, 10)}`,
    type: 'hitExplosion',
    x: impactX,
    y: impactY,
    team,
    ageMs: 0,
    durationMs: HIT_EFFECT_DURATION_MS,
    particles,
  });
}

function spawnImpactSpark(x, y, team) {
  const impactX = Number(x);
  const impactY = Number(y);
  if (!Number.isFinite(impactX) || !Number.isFinite(impactY)) {
    return;
  }

  const particleCount = 5 + Math.floor(Math.random() * 3);
  const particles = [];

  for (let index = 0; index < particleCount; index += 1) {
    const angle = (Math.PI * 2 * index) / particleCount + (Math.random() - 0.5) * 0.7;
    particles.push({
      angle,
      speed: 26 + Math.random() * 48,
      radius: 1 + Math.random() * 1.6,
      drift: (Math.random() - 0.5) * 6,
    });
  }

  state.effects.push({
    id: `fx-${Math.random().toString(36).slice(2, 10)}`,
    type: 'impactSpark',
    x: impactX,
    y: impactY,
    team,
    ageMs: 0,
    durationMs: IMPACT_SPARK_DURATION_MS,
    particles,
  });
}

function updateEffects(dtMs) {
  if (!dtMs || state.effects.length === 0) {
    return;
  }

  for (let index = state.effects.length - 1; index >= 0; index -= 1) {
    const effect = state.effects[index];
    effect.ageMs += dtMs;
    if (effect.ageMs >= effect.durationMs) {
      state.effects.splice(index, 1);
    }
  }
}

function drawHitExplosionEffect(effect) {
  const progress = Math.min(1, effect.ageMs / effect.durationMs);
  const fade = 1 - progress;
  const accentColor = TEAM_COLORS[effect.team] || '#ffffff';

  ctx.save();
  ctx.globalCompositeOperation = 'lighter';

  const shockwaveRadius = 7 + progress * 26;
  ctx.lineWidth = 2 + fade * 2;
  ctx.strokeStyle = toRgba(accentColor, 0.85 * fade);
  ctx.beginPath();
  ctx.arc(effect.x, effect.y, shockwaveRadius, 0, Math.PI * 2);
  ctx.stroke();

  ctx.fillStyle = toRgba('#ffffff', 0.55 * fade);
  ctx.beginPath();
  ctx.arc(effect.x, effect.y, 5 + progress * 6, 0, Math.PI * 2);
  ctx.fill();

  for (const particle of effect.particles) {
    const travel = 8 + particle.speed * (effect.ageMs / 1000);
    const px = effect.x + Math.cos(particle.angle) * travel + Math.cos(effect.ageMs / 90 + particle.angle) * particle.drift * progress;
    const py = effect.y + Math.sin(particle.angle) * travel + Math.sin(effect.ageMs / 110 + particle.angle) * particle.drift * progress;
    const radius = Math.max(0.8, particle.radius * (1 - progress * 0.75));

    ctx.fillStyle = toRgba(accentColor, 0.95 * fade);
    ctx.beginPath();
    ctx.arc(px, py, radius, 0, Math.PI * 2);
    ctx.fill();
  }

  ctx.restore();
}

function drawImpactSparkEffect(effect) {
  const progress = Math.min(1, effect.ageMs / effect.durationMs);
  const fade = 1 - progress;
  const accentColor = TEAM_COLORS[effect.team] || '#ffffff';

  ctx.save();
  ctx.globalCompositeOperation = 'lighter';

  ctx.lineWidth = 1.2 + fade * 1.2;
  ctx.strokeStyle = toRgba(accentColor, 0.5 * fade);
  ctx.beginPath();
  ctx.arc(effect.x, effect.y, 2 + progress * 8, 0, Math.PI * 2);
  ctx.stroke();

  ctx.fillStyle = toRgba('#ffffff', 0.65 * fade);
  ctx.beginPath();
  ctx.arc(effect.x, effect.y, Math.max(0.8, 2.8 - progress * 1.2), 0, Math.PI * 2);
  ctx.fill();

  for (const particle of effect.particles) {
    const travel = 3 + particle.speed * (effect.ageMs / 1000);
    const px = effect.x + Math.cos(particle.angle) * travel + Math.cos(effect.ageMs / 70 + particle.angle) * particle.drift * progress;
    const py = effect.y + Math.sin(particle.angle) * travel + Math.sin(effect.ageMs / 80 + particle.angle) * particle.drift * progress;
    const radius = Math.max(0.5, particle.radius * (1 - progress * 0.82));

    ctx.fillStyle = toRgba(accentColor, 0.8 * fade);
    ctx.beginPath();
    ctx.arc(px, py, radius, 0, Math.PI * 2);
    ctx.fill();
  }

  ctx.restore();
}

function drawEffects() {
  if (state.effects.length === 0) {
    return;
  }

  for (const effect of state.effects) {
    if (effect.type === 'hitExplosion') {
      drawHitExplosionEffect(effect);
      continue;
    }

    if (effect.type === 'impactSpark') {
      drawImpactSparkEffect(effect);
    }
  }
}

function toRgba(hexColor, alpha) {
  const safeAlpha = Math.max(0, Math.min(1, Number(alpha) || 0));
  const normalized = String(hexColor || '').trim();
  const shortMatch = /^#([\da-f]{3})$/i.exec(normalized);
  const longMatch = /^#([\da-f]{6})$/i.exec(normalized);

  if (shortMatch) {
    const [r, g, b] = shortMatch[1].split('').map((value) => Number.parseInt(value + value, 16));
    return `rgba(${r}, ${g}, ${b}, ${safeAlpha})`;
  }

  if (longMatch) {
    const value = longMatch[1];
    const r = Number.parseInt(value.slice(0, 2), 16);
    const g = Number.parseInt(value.slice(2, 4), 16);
    const b = Number.parseInt(value.slice(4, 6), 16);
    return `rgba(${r}, ${g}, ${b}, ${safeAlpha})`;
  }

  return `rgba(255, 255, 255, ${safeAlpha})`;
}

function drawShots() {
  for (const shot of state.shots) {
    const startX = Number(shot.startX);
    const startY = Number(shot.startY);
    const endX = Number(shot.endX);
    const endY = Number(shot.endY);

    if (![startX, startY, endX, endY].every(Number.isFinite)) {
      continue;
    }

    const color = TEAM_COLORS[shot.team] || '#ffffff';
    const glowGradient = ctx.createLinearGradient(startX, startY, endX, endY);
    glowGradient.addColorStop(0, toRgba(color, 0.05));
    glowGradient.addColorStop(0.45, toRgba(color, 0.18));
    glowGradient.addColorStop(1, toRgba(color, 0.42));

    const coreGradient = ctx.createLinearGradient(startX, startY, endX, endY);
    coreGradient.addColorStop(0, toRgba(color, 0.14));
    coreGradient.addColorStop(0.35, toRgba(color, 0.34));
    coreGradient.addColorStop(1, toRgba(color, 0.92));

    ctx.save();
    ctx.lineCap = 'round';

    ctx.strokeStyle = glowGradient;
    ctx.lineWidth = 6;
    ctx.beginPath();
    ctx.moveTo(startX, startY);
    ctx.lineTo(endX, endY);
    ctx.stroke();

    ctx.strokeStyle = coreGradient;
    ctx.lineWidth = 2.6;
    ctx.beginPath();
    ctx.moveTo(startX, startY);
    ctx.lineTo(endX, endY);
    ctx.stroke();

    ctx.fillStyle = toRgba(color, 0.8);
    ctx.beginPath();
    ctx.arc(endX, endY, 3.4, 0, Math.PI * 2);
    ctx.fill();

    ctx.fillStyle = '#ffffff';
    ctx.beginPath();
    ctx.arc(endX, endY, 1.5, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
  }
}

function parseHexColor(hexColor) {
  const normalized = String(hexColor || '').trim();
  const shortMatch = /^#([\da-f]{3})$/i.exec(normalized);
  const longMatch = /^#([\da-f]{6})$/i.exec(normalized);

  if (shortMatch) {
    const [r, g, b] = shortMatch[1].split('').map((value) => Number.parseInt(value + value, 16));
    return { r, g, b };
  }

  if (longMatch) {
    const value = longMatch[1];
    return {
      r: Number.parseInt(value.slice(0, 2), 16),
      g: Number.parseInt(value.slice(2, 4), 16),
      b: Number.parseInt(value.slice(4, 6), 16),
    };
  }

  return { r: 255, g: 255, b: 255 };
}

function mixHexColors(baseColor, targetColor, ratio) {
  const amount = clamp(Number(ratio) || 0, 0, 1);
  const base = parseHexColor(baseColor);
  const target = parseHexColor(targetColor);
  const r = Math.round(base.r + (target.r - base.r) * amount);
  const g = Math.round(base.g + (target.g - base.g) * amount);
  const b = Math.round(base.b + (target.b - base.b) * amount);
  return `rgb(${r}, ${g}, ${b})`;
}

function hashString(value) {
  let hash = 0;
  const input = String(value || '');

  for (let index = 0; index < input.length; index += 1) {
    hash = ((hash << 5) - hash + input.charCodeAt(index)) | 0;
  }

  return Math.abs(hash);
}

function getFacingVector(player) {
  const rawX = Number.isFinite(player.facingX) ? player.facingX : 1;
  const rawY = Number.isFinite(player.facingY) ? player.facingY : 0;
  const length = Math.hypot(rawX, rawY) || 1;
  return {
    x: rawX / length,
    y: rawY / length,
    angle: Math.atan2(rawY / length, rawX / length),
  };
}

function drawPlayerMark(player, radius, accentColor, detailColor) {
  const variant = hashString(`${player.id || ''}:${player.name || ''}:${player.team || ''}`) % 5;
  const facing = getFacingVector(player);

  ctx.save();
  ctx.translate(player.x, player.y);
  ctx.rotate(facing.angle);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';

  switch (variant) {
    case 0:
      ctx.rotate(-Math.PI / 7);
      ctx.fillStyle = accentColor;
      ctx.fillRect(-radius * 0.56, -radius * 0.14, radius * 0.88, radius * 0.28);
      ctx.beginPath();
      ctx.moveTo(radius * 0.24, -radius * 0.28);
      ctx.lineTo(radius * 0.62, 0);
      ctx.lineTo(radius * 0.24, radius * 0.28);
      ctx.closePath();
      ctx.fill();
      break;
    case 1:
      ctx.fillStyle = accentColor;
      ctx.beginPath();
      ctx.arc(-radius * 0.16, 0, radius * 0.15, 0, Math.PI * 2);
      ctx.arc(radius * 0.22, 0, radius * 0.23, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = detailColor;
      ctx.beginPath();
      ctx.arc(radius * 0.34, 0, radius * 0.08, 0, Math.PI * 2);
      ctx.fill();
      break;
    case 2:
      ctx.strokeStyle = accentColor;
      ctx.lineWidth = Math.max(2, radius * 0.2);
      ctx.beginPath();
      ctx.moveTo(-radius * 0.4, -radius * 0.24);
      ctx.lineTo(radius * 0.08, 0);
      ctx.lineTo(-radius * 0.4, radius * 0.24);
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(-radius * 0.02, -radius * 0.24);
      ctx.lineTo(radius * 0.46, 0);
      ctx.lineTo(-radius * 0.02, radius * 0.24);
      ctx.stroke();
      break;
    case 3:
      ctx.fillStyle = accentColor;
      ctx.fillRect(-radius * 0.5, -radius * 0.14, radius * 0.72, radius * 0.28);
      ctx.beginPath();
      ctx.moveTo(radius * 0.16, -radius * 0.32);
      ctx.lineTo(radius * 0.54, 0);
      ctx.lineTo(radius * 0.16, radius * 0.32);
      ctx.closePath();
      ctx.fill();
      ctx.strokeStyle = detailColor;
      ctx.lineWidth = Math.max(1.5, radius * 0.1);
      ctx.beginPath();
      ctx.moveTo(-radius * 0.3, -radius * 0.34);
      ctx.lineTo(-radius * 0.3, radius * 0.34);
      ctx.stroke();
      break;
    default:
      ctx.strokeStyle = accentColor;
      ctx.lineWidth = Math.max(2, radius * 0.15);
      ctx.beginPath();
      ctx.arc(-radius * 0.08, 0, radius * 0.34, 0, Math.PI * 2);
      ctx.stroke();
      ctx.fillStyle = detailColor;
      ctx.beginPath();
      ctx.arc(radius * 0.24, 0, radius * 0.11, 0, Math.PI * 2);
      ctx.fill();
      break;
  }

  ctx.restore();
}

function drawFlagCarrierBadgeAt(x, y, flagTeam, scale = 1) {
  const flagColor = TEAM_COLORS[flagTeam] || '#ffffff';
  const poleX = x - 3 * scale;
  const poleTopY = y - 7 * scale;
  const poleBottomY = y + 6 * scale;

  ctx.save();
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.strokeStyle = 'rgba(16, 19, 26, 0.9)';
  ctx.lineWidth = Math.max(1.4, 1.8 * scale);
  ctx.beginPath();
  ctx.moveTo(poleX, poleBottomY);
  ctx.lineTo(poleX, poleTopY);
  ctx.stroke();

  ctx.fillStyle = flagColor;
  ctx.beginPath();
  ctx.moveTo(poleX + 1 * scale, poleTopY);
  ctx.lineTo(x + 8 * scale, y - 3 * scale);
  ctx.lineTo(poleX + 1 * scale, y + 1 * scale);
  ctx.closePath();
  ctx.fill();

  ctx.strokeStyle = 'rgba(255, 255, 255, 0.82)';
  ctx.lineWidth = Math.max(1, 1.1 * scale);
  ctx.stroke();
  ctx.restore();
}

function drawPlayerAvatar(player, isSelf) {
  const teamColor = TEAM_COLORS[player.team] || '#ffffff';
  const radius = Number(player.radius) || 14;
  const facing = getFacingVector(player);
  const darkFill = mixHexColors(teamColor, '#10131a', 0.35);
  const coreFill = mixHexColors(teamColor, '#10131a', 0.2);
  const accentFill = mixHexColors(teamColor, '#ffffff', 0.34);
  const markColor = toRgba('#ffffff', 0.8);
  const markDetailColor = toRgba('#10131a', 0.45);

  ctx.save();

  if (isSelf) {
    const pulse = 0.75 + (Math.sin(performance.now() / 220) + 1) * 0.12;
    ctx.fillStyle = toRgba(teamColor, 0.18);
    ctx.beginPath();
    ctx.arc(player.x, player.y, radius * pulse + 5, 0, Math.PI * 2);
    ctx.fill();
  }

  ctx.fillStyle = 'rgba(0, 0, 0, 0.22)';
  ctx.beginPath();
  ctx.ellipse(player.x, player.y + radius * 0.36, radius * 0.88, radius * 0.48, 0, 0, Math.PI * 2);
  ctx.fill();

  ctx.fillStyle = teamColor;
  ctx.beginPath();
  ctx.arc(player.x, player.y, radius, 0, Math.PI * 2);
  ctx.fill();

  ctx.fillStyle = coreFill;
  ctx.beginPath();
  ctx.arc(player.x, player.y, radius * 0.74, 0, Math.PI * 2);
  ctx.fill();

  ctx.fillStyle = toRgba('#ffffff', 0.18);
  ctx.beginPath();
  ctx.arc(player.x - radius * 0.28, player.y - radius * 0.3, radius * 0.24, 0, Math.PI * 2);
  ctx.fill();

  drawPlayerMark(player, radius * 0.74, markColor, markDetailColor);

  ctx.save();
  ctx.translate(player.x, player.y);
  ctx.rotate(facing.angle);
  ctx.fillStyle = accentFill;
  roundRect(ctx, radius * 0.15, -radius * 0.26, radius * 0.74, radius * 0.52, radius * 0.24);
  ctx.fill();
  ctx.strokeStyle = toRgba('#10131a', 0.45);
  ctx.lineWidth = Math.max(1.5, radius * 0.1);
  ctx.stroke();
  ctx.restore();

  ctx.strokeStyle = isSelf ? '#ffffff' : darkFill;
  ctx.lineWidth = isSelf ? 3 : 2;
  ctx.beginPath();
  ctx.arc(player.x, player.y, radius, 0, Math.PI * 2);
  ctx.stroke();

  ctx.restore();
}

function drawPlayers() {
  for (const player of state.players) {
    const isSelf = player.id === state.myPlayerId;
    const radius = Number(player.radius) || 14;

    drawPlayerAvatar(player, isSelf);

    ctx.save();
    ctx.font = '16px Segoe UI';
    ctx.textAlign = 'center';
    ctx.lineWidth = 4;
    ctx.strokeStyle = 'rgba(0,0,0,0.75)';
    const labelY = player.y - radius - 10;
    ctx.strokeText(player.name, player.x, labelY);
    ctx.fillStyle = '#ffffff';
    ctx.fillText(player.name, player.x, labelY);
    ctx.restore();

  }
}


function drawMinimap() {
  if (!state.viewport.portraitPlayable || !state.map) {
    return;
  }

  const world = getWorldSize();
  const pad = 12 * Math.min(window.devicePixelRatio || 1, 2);
  const size = Math.max(84, Math.min(canvas.width, canvas.height) * 0.2);
  const x = canvas.width - size - pad;
  const y = pad;
  const scale = Math.min((size - 18) / world.width, (size - 18) / world.height);
  const mapWidth = world.width * scale;
  const mapHeight = world.height * scale;
  const offsetX = x + (size - mapWidth) / 2;
  const offsetY = y + (size - mapHeight) / 2;

  ctx.save();
  resetScreenTransform();
  ctx.globalAlpha = 0.95;
  ctx.fillStyle = 'rgba(6, 11, 28, 0.78)';
  ctx.strokeStyle = 'rgba(162, 184, 255, 0.26)';
  ctx.lineWidth = 1;
  roundRect(ctx, x, y, size, size, 16);
  ctx.fill();
  ctx.stroke();

  ctx.fillStyle = 'rgba(120, 140, 255, 0.08)';
  ctx.fillRect(offsetX, offsetY, mapWidth, mapHeight);

  const perimeter = state.map.objects.find((item) => item.type === 'perimeter');
  if (perimeter?.points?.length) {
    ctx.strokeStyle = 'rgba(255,255,255,0.45)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    perimeter.points.forEach((point, index) => {
      const px = offsetX + point.x * scale;
      const py = offsetY + point.y * scale;
      if (index === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    });
    ctx.closePath();
    ctx.stroke();
  }

  for (const flag of state.flags) {
    const color = TEAM_COLORS[flag.team] || '#ffffff';
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(offsetX + flag.x * scale, offsetY + flag.y * scale, 3.2, 0, Math.PI * 2);
    ctx.fill();
  }

  for (const player of state.players) {
    const color = TEAM_COLORS[player.team] || '#ffffff';
    const radius = player.id === state.myPlayerId ? 3.2 : 2.4;
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(offsetX + player.x * scale, offsetY + player.y * scale, radius, 0, Math.PI * 2);
    ctx.fill();

    if (player.id === state.myPlayerId) {
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 1.25;
      ctx.stroke();
    }
  }

  const viewX = offsetX + state.camera.x * scale;
  const viewY = offsetY + state.camera.y * scale;
  const viewW = state.camera.viewWidth * scale;
  const viewH = state.camera.viewHeight * scale;
  ctx.strokeStyle = 'rgba(255,255,255,0.75)';
  ctx.lineWidth = 1.25;
  ctx.strokeRect(viewX, viewY, viewW, viewH);

  ctx.fillStyle = 'rgba(255,255,255,0.88)';
  ctx.font = `${Math.max(11, Math.round(size * 0.11))}px Segoe UI`;
  ctx.textAlign = 'left';
  ctx.fillText('Map', x + 12, y + 18);
  ctx.restore();
}

function roundRect(context, x, y, width, height, radius) {
  const r = Math.min(radius, width / 2, height / 2);
  context.beginPath();
  context.moveTo(x + r, y);
  context.arcTo(x + width, y, x + width, y + height, r);
  context.arcTo(x + width, y + height, x, y + height, r);
  context.arcTo(x, y + height, x, y, r);
  context.arcTo(x, y, x + width, y, r);
  context.closePath();
}

function drawOverlay() {
  if (!state.connected) {
    ctx.save();
    resetScreenTransform();
    ctx.fillStyle = 'rgba(10, 10, 15, 0.58)';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#ffffff';
    ctx.textAlign = 'center';
    ctx.font = `400 ${Math.max(22, Math.round(canvas.width * 0.05))}px Segoe UI`;
    const message = state.mobile.enabled
      ? 'Open the menu and tap Connect to join'
      : 'Open the menu and press Connect to join';
    ctx.fillText(message, canvas.width / 2, canvas.height / 2 - 10);
    ctx.restore();
  }

  if (state.myPlayerId) {
    const player = state.players.find((item) => item.id === state.myPlayerId);
    if (player && player.team !== state.myTeam) {
      state.myTeam = player.team;
      setCanvasAccent(player.team);
    }
  }
}

boot().catch((error) => {
  console.error(error);
  gameInfoLine1El.textContent = 'Error loading map';
  pingValueEl.textContent = '-';
  updateConnectButton();
});
