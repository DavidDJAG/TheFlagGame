const canvas = document.getElementById('gameCanvas');
const ctx = canvas.getContext('2d');
const canvasWrapEl = document.getElementById('canvasWrap');
const connectToggleBtn = document.getElementById('connectToggleBtn');
const resetGameBtn = document.getElementById('resetGameBtn');
const installAppBtn = document.getElementById('installAppBtn');
const roomIdInput = document.getElementById('roomId');
const playerNameInput = document.getElementById('playerName');
const teamSelectionInput = document.getElementById('teamSelection');
const spectatorModeInput = document.getElementById('spectatorMode');
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
const homeScreenEl = document.getElementById('homeScreen');
const homeMessageEl = document.getElementById('homeMessage');
const homeVideoEl = document.getElementById('homeVideo');

const DEFAULT_HTTP_SERVER_BASE = 'http://127.0.0.1:5770';
const DEFAULT_PUBLIC_PATH = '';
const PRODUCTION_FRONTEND_ORIGINS = new Set([
  'https://www.mcrenox.com.ar'
]);
const PRODUCTION_BACKEND_BASE = 'https://server.mcrenox.com';
const PRODUCTION_BACKEND_PUBLIC_PATH = '/theflag';
const EARTH_COLORS = ['#54493b', '#635644', '#72624e', '#816e57', '#8e7a5e', '#9c8666', '#aa906e', '#b89b75', '#c5a67c', '#d2b183'];
const JOYSTICK_RADIUS = 44;
const JOYSTICK_DEADZONE = 14;
const HIT_EFFECT_DURATION_MS = 620;
const IMPACT_SPARK_DURATION_MS = 180;
const RECENT_PLAYER_IMPACT_TTL_MS = 180;
const PLAYER_IMPACT_PROXIMITY_PX = 14;
const SEEN_EVENT_TTL_MS = 15000;
const STATE_WATCHDOG_INTERVAL_MS = 1000;
const STATE_WATCHDOG_TIMEOUT_MS = 5000;
const RECONNECT_BASE_DELAY_MS = 1000;
const RECONNECT_MAX_DELAY_MS = 5000;
const MAX_CONNECT_ATTEMPTS = 5;
const DEFAULT_ROOM_ID = 'public';
const ROOM_ID_STORAGE_KEY = 'ctf-room-id';
const TEAM_SELECTION_STORAGE_KEY = 'ctf-team-selection';
const SPECTATOR_MODE_STORAGE_KEY = 'ctf-spectator-mode';
const ROOM_ID_PATTERN = /^[a-z0-9_-]{1,32}$/;

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

  const currentOrigin = normalizeBaseUrl(window.location.origin);

  if (PRODUCTION_FRONTEND_ORIGINS.has(currentOrigin)) {
    return {
      serverBase: PRODUCTION_BACKEND_BASE,
      publicPath: PRODUCTION_BACKEND_PUBLIC_PATH
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

function normalizeRoomId(value) {
  const cleaned = String(value || '').trim().toLowerCase();
  return cleaned || DEFAULT_ROOM_ID;
}

function isValidRoomId(roomId) {
  return ROOM_ID_PATTERN.test(roomId);
}

function getInitialRoomId() {
  const params = new URLSearchParams(window.location.search);
  const fromQuery = params.get('room');
  const fromStorage = localStorage.getItem(ROOM_ID_STORAGE_KEY);
  const roomId = normalizeRoomId(fromQuery || fromStorage || DEFAULT_ROOM_ID);
  return isValidRoomId(roomId) ? roomId : DEFAULT_ROOM_ID;
}

function normalizeTeamSelection(value) {
  const normalized = String(value || '').trim().toLowerCase();
  return normalized === 'blue' || normalized === 'red' ? normalized : 'auto';
}

function parseBooleanSetting(value, defaultValue = false) {
  if (value === null || typeof value === 'undefined') {
    return defaultValue;
  }

  const normalized = String(value).trim().toLowerCase();
  if (!normalized) {
    return defaultValue;
  }

  if (normalized === 'true' || normalized === '1' || normalized === 'yes') {
    return true;
  }

  if (normalized === 'false' || normalized === '0' || normalized === 'no') {
    return false;
  }

  return defaultValue;
}

function getInitialTeamSelection() {
  const params = new URLSearchParams(window.location.search);
  const fromQuery = params.get('team');
  const fromStorage = localStorage.getItem(TEAM_SELECTION_STORAGE_KEY);
  return normalizeTeamSelection(fromQuery || fromStorage || 'auto');
}

function getInitialSpectatorMode() {
  const params = new URLSearchParams(window.location.search);
  const fromQuery = params.get('spectator');
  if (fromQuery !== null) {
    return parseBooleanSetting(fromQuery, false);
  }
  return parseBooleanSetting(localStorage.getItem(SPECTATOR_MODE_STORAGE_KEY), false);
}

function getDesiredTeamSelection() {
  const teamSelection = normalizeTeamSelection(teamSelectionInput.value);
  teamSelectionInput.value = teamSelection;
  localStorage.setItem(TEAM_SELECTION_STORAGE_KEY, teamSelection);
  return teamSelection;
}

function getDesiredSpectatorMode() {
  const spectatorMode = Boolean(spectatorModeInput.checked);
  localStorage.setItem(SPECTATOR_MODE_STORAGE_KEY, spectatorMode ? 'true' : 'false');
  return spectatorMode;
}

function syncSpectatorModeUi() {
  const connectionLocked = teamSelectionInput.dataset.connectionLocked === 'true';
  const desiredSpectatorMode = Boolean(spectatorModeInput.checked);
  const activeSpectatorMode = connectionLocked
    ? desiredSpectatorMode || Boolean(state?.spectatorMode)
    : desiredSpectatorMode;

  teamSelectionInput.disabled = connectionLocked || activeSpectatorMode;
  document.body.classList.toggle('spectator-mode', activeSpectatorMode);
}

function getDesiredRoomId({ alertOnInvalid = false } = {}) {
  const roomId = normalizeRoomId(roomIdInput.value);
  roomIdInput.value = roomId;

  if (!isValidRoomId(roomId)) {
    setConnectionStatus('invalid room');
    updatePingLine('room must use a-z, 0-9, _ or - and max 32 chars');
    if (alertOnInvalid) {
      window.alert('Invalid room. Use 1-32 characters: lowercase letters, numbers, hyphen, or underscore.');
    }
    updateConnectButton();
    return null;
  }

  localStorage.setItem(ROOM_ID_STORAGE_KEY, roomId);
  return roomId;
}

function buildWebSocketUrl(roomId, { teamSelection = 'auto', spectatorMode = false } = {}) {
  const url = new URL(WS_URL);
  url.searchParams.set('room', roomId);

  if (spectatorMode) {
    url.searchParams.set('spectator', 'true');
  } else if (teamSelection === 'blue' || teamSelection === 'red') {
    url.searchParams.set('team', teamSelection);
  }

  return url.toString();
}

function getDisplayedRoomId() {
  return state.currentRoomId || normalizeRoomId(roomIdInput.value);
}

function updateMapInfoLine() {
  gameInfoLine1El.textContent = `Map: ${state.mapName} · Room: ${getDisplayedRoomId()}`;
}


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
  screenShake: {
    ageMs: 9999,
    durationMs: 0,
    strengthPx: 0,
    seed: 0,
  },
  lastFrameTime: null,
  input: { up: false, down: false, left: false, right: false },
  mapName: '-',
  currentRoomId: null,
  pingMs: null,
  pingIntervalId: null,
  inputIntervalId: null,
  pingNonce: 1,
  pendingPings: new Map(),
  watchdogIntervalId: null,
  reconnectTimeoutId: null,
  lastStateReceivedAt: null,
  desiredOnline: false,
  spectatorMode: false,
  teamSelection: 'auto',
  connectAttempts: 0,
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

const PLAYER_AVATAR_SIZE = 0.50;
const PLAYER_AVATAR_VISUAL_RADIUS = 42 * PLAYER_AVATAR_SIZE;
const PLAYER_AVATAR_MOTION_TTL_MS = 170;
const PLAYER_AVATAR_MOVE_EPSILON_PX = 0.12;
const PLAYER_AVATAR_WALK_PHASE_SPEED = 28;
const PLAYER_AVATAR_IDLE_PHASE_SPEED = 4;
const PLAYER_POSITION_SMOOTHING_MS = 82;
const PLAYER_POSITION_SELF_PREDICTION_MS = 52;
const PLAYER_POSITION_REMOTE_PREDICTION_MS = 24;
const PLAYER_POSITION_MAX_PREDICTION_PX = 14;
const PLAYER_POSITION_SNAP_DISTANCE_PX = 160;
const PLAYER_POSITION_CHANGE_EPSILON_PX = 0.01;
const playerAvatarStates = new Map();
const playerRenderStates = new Map();

const TOP_DOWN_AVATAR_PALETTES = {
  blue: {
    suit: '#2f80ed',
    suitDark: '#1e4f9c',
    accent: '#8cc6ff',
    cap: '#2563eb',
    flag: '#38bdf8',
  },
  red: {
    suit: '#b94a48',
    suitDark: '#7f1d1d',
    accent: '#d98b84',
    cap: '#a63f3c',
    flag: '#e98787',
  },
  neutral: {
    suit: '#94a3b8',
    suitDark: '#475569',
    accent: '#e2e8f0',
    cap: '#64748b',
    flag: '#f8fafc',
  },
};

class TopDownAvatar {
  constructor({ team = 'neutral', size = PLAYER_AVATAR_SIZE } = {}) {
    this.x = 0;
    this.y = 0;
    this.angle = 0;
    this.walkPhase = 0;
    this.hasFlag = false;
    this.flagTeam = null;
    this.setSize(size);
    this.setTeam(team);
  }

  setTeam(team) {
    const normalizedTeam = normalizeTeamValue(team) || 'neutral';
    this.team = normalizedTeam;
    this.colors = TOP_DOWN_AVATAR_PALETTES[normalizedTeam] || TOP_DOWN_AVATAR_PALETTES.neutral;
  }

  setSize(size = PLAYER_AVATAR_SIZE) {
    const numericSize = Number(size);
    this.size = Number.isFinite(numericSize)
      ? Math.min(2, Math.max(0.45, numericSize))
      : PLAYER_AVATAR_SIZE;
  }

  draw(ctx, {
    x = 0,
    y = 0,
    angle = 0,
    walkPhase = 0,
    team = 'neutral',
    size = PLAYER_AVATAR_SIZE,
    hasFlag = false,
    flagTeam = null,
  } = {}) {
    this.x = x;
    this.y = y;
    this.angle = Number.isFinite(angle) ? angle : 0;
    this.walkPhase = Number.isFinite(walkPhase) ? walkPhase : 0;
    this.hasFlag = Boolean(hasFlag);
    this.flagTeam = normalizeTeamValue(flagTeam);
    this.setSize(size);
    this.setTeam(team);

    ctx.save();
    ctx.translate(this.x, this.y);
    ctx.rotate(this.angle);
    ctx.scale(this.size, this.size);

    this._drawSoftShadow(ctx);

    if (this.hasFlag) {
      this._drawCarriedFlag(ctx);
    }

    this._drawFeet(ctx);
    this._drawShoulders(ctx);
    this._drawTorso(ctx);
    this._drawHeadAndCap(ctx);

    ctx.restore();
  }

  _drawSoftShadow(ctx) {
    const gradient = ctx.createRadialGradient(0, 0, 8, 0, 0, 38);
    gradient.addColorStop(0, 'rgba(0,0,0,0.32)');
    gradient.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.ellipse(0, 2, 42, 28, 0, 0, Math.PI * 2);
    ctx.fill();
  }

  _drawFeet(ctx) {
    const step = Math.sin(this.walkPhase) * 4.5;
    const footY = 11;

    ctx.fillStyle = this.colors.suitDark;
    ctx.strokeStyle = 'rgba(255,255,255,0.16)';
    ctx.lineWidth = 1.5;

    this._roundedEllipse(ctx, -19 + step, -footY, 10, 6, -0.2);
    ctx.fill();
    ctx.stroke();

    this._roundedEllipse(ctx, -19 - step, footY, 10, 6, 0.2);
    ctx.fill();
    ctx.stroke();
  }

  _drawShoulders(ctx) {
    ctx.fillStyle = this.colors.suit;
    ctx.strokeStyle = 'rgba(255,255,255,0.2)';
    ctx.lineWidth = 2;

    this._roundedEllipse(ctx, -2, -19, 14, 8, -0.28);
    ctx.fill();
    ctx.stroke();

    this._roundedEllipse(ctx, -2, 19, 14, 8, 0.28);
    ctx.fill();
    ctx.stroke();
  }

  _drawTorso(ctx) {
    const bodyGradient = ctx.createLinearGradient(-25, 0, 24, 0);
    bodyGradient.addColorStop(0, this.colors.suitDark);
    bodyGradient.addColorStop(0.55, this.colors.suit);
    bodyGradient.addColorStop(1, this.colors.accent);

    ctx.fillStyle = bodyGradient;
    ctx.strokeStyle = 'rgba(255,255,255,0.22)';
    ctx.lineWidth = 2.2;

    ctx.beginPath();
    ctx.ellipse(-2, 0, 26, 20, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();

    ctx.fillStyle = 'rgba(255,255,255,0.14)';
    ctx.beginPath();
    ctx.ellipse(11, -6, 8, 5, -0.4, 0, Math.PI * 2);
    ctx.fill();
  }

  _drawHeadAndCap(ctx) {
    ctx.strokeStyle = 'rgba(255,255,255,0.24)';
    ctx.lineWidth = 2;

    ctx.beginPath();
    ctx.arc(18, 0, 14, 0, Math.PI * 2);
    ctx.fillStyle = this.colors.cap;
    ctx.fill();
    ctx.stroke();

    ctx.fillStyle = 'rgba(255,255,255,0.18)';
    ctx.beginPath();
    ctx.arc(13, -4, 4.2, 0, Math.PI * 2);
    ctx.fill();
  }

  _drawCarriedFlag(ctx) {
    const flagColor = TOP_DOWN_AVATAR_PALETTES[this.flagTeam]?.flag || this.colors.flag;

    ctx.save();
    ctx.translate(3, -19);
    ctx.rotate(-0.08);

    ctx.strokeStyle = 'rgba(226,232,240,0.92)';
    ctx.lineWidth = 2.5;
    ctx.lineCap = 'round';
    ctx.beginPath();
    ctx.moveTo(0, 10);
    ctx.lineTo(0, -28);
    ctx.stroke();

    ctx.fillStyle = 'rgba(15,23,42,0.38)';
    ctx.beginPath();
    ctx.arc(0, 6, 4.2, 0, Math.PI * 2);
    ctx.fill();

    ctx.fillStyle = flagColor;
    ctx.strokeStyle = 'rgba(15,23,42,0.45)';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(0, -28);
    ctx.lineTo(28, -19);
    ctx.lineTo(0, -10);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();

    ctx.restore();
  }

  _roundedEllipse(ctx, x, y, rx, ry, rotation) {
    ctx.beginPath();
    ctx.ellipse(x, y, rx, ry, rotation, 0, Math.PI * 2);
  }
}

const playerAvatarRenderer = new TopDownAvatar({ size: PLAYER_AVATAR_SIZE });

function setCanvasAccent(team) {
  const normalized = team === 'blue' || team === 'red' ? team : null;
  const border = normalized ? toRgba(TEAM_COLORS[normalized], 0.9) : 'rgba(194, 205, 246, 0.22)';
  const glow = normalized ? toRgba(TEAM_COLORS[normalized], 0.24) : 'rgba(194, 205, 246, 0.18)';
  canvasWrapEl.style.setProperty('--canvas-accent', border);
  canvasWrapEl.style.setProperty('--canvas-accent-glow', glow);
}

roomIdInput.value = getInitialRoomId();
teamSelectionInput.value = getInitialTeamSelection();
spectatorModeInput.checked = getInitialSpectatorMode();
playerNameInput.value = localStorage.getItem('ctf-player-name') || '';
syncSpectatorModeUi();
updateMapInfoLine();
setCanvasAccent(null);

async function boot() {
  await loadMap();
  setupEvents();
  updateLayoutMetrics();
  updateTouchLayout();
  updateInstallAvailability();
  updateConnectButton();
  updateHomeScreen();
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
  updateMapInfoLine();
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
  const rawPlayer = state.players.find((item) => item.id === state.myPlayerId);
  const player = rawPlayer ? getPlayerRenderProxy(rawPlayer) : null;
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
  const shake = getScreenShakeOffset();

  ctx.setTransform(
    scaleX,
    0,
    0,
    scaleY,
    -state.camera.x * scaleX + shake.x,
    -state.camera.y * scaleY + shake.y
  );
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
      connect({ resetAttempts: true });
    }
    closeMenu();
  });

  resetGameBtn.addEventListener('click', () => requestMatchReset());
  matchResultResetBtn.addEventListener('click', () => requestMatchReset());

  installAppBtn.addEventListener('click', installPwa);

  roomIdInput.addEventListener('change', () => {
    const roomId = getDesiredRoomId();
    if (roomId) {
      updateMapInfoLine();
    }
  });

  roomIdInput.addEventListener('input', updateMapInfoLine);

  teamSelectionInput.addEventListener('change', () => {
    getDesiredTeamSelection();
    syncSpectatorModeUi();
  });

  spectatorModeInput.addEventListener('change', () => {
    const desiredSpectatorMode = getDesiredSpectatorMode();
    if (!state.connected && !state.desiredOnline) {
      state.spectatorMode = desiredSpectatorMode;
    }
    syncSpectatorModeUi();
    updateHomeScreen();
  });

  playerNameInput.addEventListener('change', () => {
    localStorage.setItem('ctf-player-name', playerNameInput.value.trim());
    if (!state.spectatorMode) {
      send({ type: 'hello', name: playerNameInput.value.trim() || 'Player' });
    }
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
  updateHomeScreen();
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
  updateHomeScreen();

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
  return document.activeElement === playerNameInput || document.activeElement === roomIdInput || document.activeElement === teamSelectionInput;
}

function connect(options = {}) {
  const { resetAttempts = false } = options;
  state.desiredOnline = true;
  clearReconnectTimer();

  if (resetAttempts) {
    state.connectAttempts = 0;
    state.reconnectAttempts = 0;
  }

  if (state.socket && (state.socket.readyState === WebSocket.OPEN || state.socket.readyState === WebSocket.CONNECTING)) {
    return;
  }

  if (state.connectAttempts >= MAX_CONNECT_ATTEMPTS) {
    stopConnectRetries();
    return;
  }

  const roomId = getDesiredRoomId({ alertOnInvalid: true });
  if (!roomId) {
    state.desiredOnline = false;
    return;
  }

  const teamSelection = getDesiredTeamSelection();
  const spectatorMode = getDesiredSpectatorMode();

  state.connectAttempts += 1;
  state.currentRoomId = roomId;
  state.teamSelection = teamSelection;
  state.spectatorMode = spectatorMode;
  updateMapInfoLine();
  const socket = new WebSocket(buildWebSocketUrl(roomId, { teamSelection, spectatorMode }));
  state.socket = socket;
  setConnectionStatus('connecting');
  updateConnectButton();
  resetPing();
  updatePingLine('measuring...');

  socket.addEventListener('open', () => {
    state.connected = true;
    state.connectAttempts = 0;
    state.reconnectAttempts = 0;
    state.lastStateReceivedAt = performance.now();
    setConnectionStatus('connected');
    updateConnectButton();
    closeMenu();
    if (!state.spectatorMode) {
      send({ type: 'hello', name: playerNameInput.value.trim() || 'Player' });
    }
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
      state.spectatorMode = Boolean(message.spectator) || state.spectatorMode;
      state.myPlayerId = state.spectatorMode ? null : message.playerId;
      state.myTeam = state.spectatorMode ? null : message.team;
      const serverRoomId = normalizeRoomId(message.roomId || state.currentRoomId);
      if (isValidRoomId(serverRoomId)) {
        state.currentRoomId = serverRoomId;
        roomIdInput.value = serverRoomId;
        localStorage.setItem(ROOM_ID_STORAGE_KEY, serverRoomId);
        updateMapInfoLine();
      }
      setCanvasAccent(state.spectatorMode ? null : message.team);
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

    if (message.type === 'resetRejected') {
      const retryAfterSeconds = Math.max(1, Math.ceil(Number(message.retryAfterMs || 0) / 1000));
      window.alert(`Reset will be available in ${retryAfterSeconds} second(s).`);
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
    playerRenderStates.clear();
    state.lastFrameTime = null;
    state.match.status = 'running';
    state.match.remainingMs = Math.max(0, (state.match.durationSeconds || 300) * 1000);
    updateMatchHud();
    state.myPlayerId = null;
    state.myTeam = null;
    setConnectionStatus('disconnected');
    if (!shouldReconnect) {
      state.currentRoomId = null;
      state.spectatorMode = Boolean(spectatorModeInput.checked);
    }
    updateMapInfoLine();
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
  state.connectAttempts = 0;
  state.reconnectAttempts = 0;
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

  if (state.connectAttempts >= MAX_CONNECT_ATTEMPTS) {
    stopConnectRetries();
    return;
  }

  const delay = Math.min(
    RECONNECT_MAX_DELAY_MS,
    RECONNECT_BASE_DELAY_MS * Math.max(1, 2 ** state.reconnectAttempts)
  );
  state.reconnectAttempts += 1;
  setConnectionStatus('reconnecting');
  updateConnectButton();
  updatePingLine(`reconnecting... attempt ${state.connectAttempts + 1}/${MAX_CONNECT_ATTEMPTS}`);

  state.reconnectTimeoutId = window.setTimeout(() => {
    state.reconnectTimeoutId = null;
    if (state.desiredOnline) {
      connect({ resetAttempts: false });
    }
  }, delay);
}

function stopConnectRetries() {
  state.desiredOnline = false;
  clearReconnectTimer();
  setConnectionStatus('failed');
  updateConnectButton();
  updatePingLine(`connection failed after ${MAX_CONNECT_ATTEMPTS} attempts`);
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
  if (state.spectatorMode || state.match.status === 'finished') {
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
  if (state.spectatorMode || state.match.status === 'finished') {
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

  const localId = String(state.myPlayerId);
  const me = state.players.find((player) => String(player.id) === localId);
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
  const roomLocked = Boolean(isOnline || (state.desiredOnline && state.reconnectTimeoutId));
  connectToggleBtn.textContent = isOnline ? 'Disconnect' : 'Connect';
  roomIdInput.disabled = roomLocked;
  teamSelectionInput.dataset.connectionLocked = roomLocked ? 'true' : 'false';
  spectatorModeInput.disabled = roomLocked;
  syncSpectatorModeUi();
  resetGameBtn.disabled = !state.connected || state.spectatorMode;
  matchResultResetBtn.disabled = !state.connected || state.spectatorMode;
  updateHomeScreen();
}

function getHomeMessage() {
  const socket = state.socket;
  const socketState = socket ? socket.readyState : 3;
  const statusText = connectionStatusEl.textContent;

  if (!state.connected && (statusText === 'connecting' || (state.desiredOnline && socketState === 0))) {
    return 'Connecting...';
  }

  if (!state.connected && (statusText === 'reconnecting' || (state.desiredOnline && state.reconnectTimeoutId))) {
    return 'Reconnecting...';
  }

  if (!state.connected && statusText === 'failed') {
    return 'Connection failed. Open the menu and press Connect to try again';
  }

  if (!state.connected && statusText === 'error') {
    return 'Connection error. Open the menu and press Connect to try again';
  }

  const verb = spectatorModeInput.checked ? 'watch' : 'join';
  return state.mobile.enabled
    ? `Open the menu and tap Connect to ${verb}`
    : `Open the menu and press Connect to ${verb}`;
}

function updateHomeScreen() {
  if (!homeScreenEl || !homeMessageEl) {
    return;
  }

  const showHome = !state.connected;
  canvasWrapEl.classList.toggle('preconnect-mode', showHome);
  homeScreenEl.setAttribute('aria-hidden', showHome ? 'false' : 'true');
  homeMessageEl.textContent = getHomeMessage();

  if (!homeVideoEl) {
    return;
  }

  if (showHome) {
    const playPromise = homeVideoEl.play();
    if (playPromise && typeof playPromise.catch === 'function') {
      playPromise.catch(() => {});
    }
  } else {
    homeVideoEl.pause();
  }
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
  updatePlayerRenderStates(dtMs, frameTime);
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
  drawEffects('under');
  drawPlayers();
  drawEffects('over');
  ctx.restore();
  drawMinimap();
  drawOverlay();
}

function drawBackgroundGrid() {
  const world = getWorldSize();
  const width = Math.max(1, world.width);
  const height = Math.max(1, world.height);

  drawTacticalGridBase(width, height);
  drawTacticalGridLayer(width, height, 24, 'rgba(148,163,184,0.045)', 1);
  drawTacticalGridLayer(width, height, 48, 'rgba(148,163,184,0.092)', 1);
  drawTacticalGridLayer(width, height, 96, 'rgba(226,232,240,0.135)', 1.25);
  drawTacticalGridLayer(width, height, 192, 'rgba(104,225,253,0.13)', 1.6);
  drawTacticalGridDiagonalTexture(width, height, 96);
  drawTacticalGridIntersectionDots(width, height, 96);
  drawTacticalGridSectorLabels(width, height, 192);
  drawTacticalGridVignette(width, height);
}

function drawTacticalGridBase(width, height) {
  const baseGradient = ctx.createLinearGradient(0, 0, width, height);
  baseGradient.addColorStop(0, '#091322');
  baseGradient.addColorStop(0.48, '#0b1826');
  baseGradient.addColorStop(1, '#082b2e');

  ctx.fillStyle = baseGradient;
  ctx.fillRect(0, 0, width, height);
}

function drawTacticalGridLayer(width, height, spacing, color, lineWidth) {
  ctx.save();
  ctx.strokeStyle = color;
  ctx.lineWidth = lineWidth;

  for (let x = 0; x <= width; x += spacing) {
    ctx.beginPath();
    ctx.moveTo(x + 0.5, 0);
    ctx.lineTo(x + 0.5, height);
    ctx.stroke();
  }

  for (let y = 0; y <= height; y += spacing) {
    ctx.beginPath();
    ctx.moveTo(0, y + 0.5);
    ctx.lineTo(width, y + 0.5);
    ctx.stroke();
  }

  ctx.restore();
}

function drawTacticalGridDiagonalTexture(width, height, spacing) {
  ctx.save();
  ctx.strokeStyle = 'rgba(148,163,184,0.035)';
  ctx.lineWidth = 1;

  for (let x = -height; x < width; x += spacing) {
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x + height, height);
    ctx.stroke();
  }

  ctx.restore();
}

function drawTacticalGridIntersectionDots(width, height, spacing) {
  ctx.save();
  ctx.fillStyle = 'rgba(226,232,240,0.16)';

  for (let x = spacing; x < width; x += spacing) {
    for (let y = spacing; y < height; y += spacing) {
      ctx.beginPath();
      ctx.arc(x, y, 1.6, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  ctx.restore();
}

function drawTacticalGridSectorLabels(width, height, spacing) {
  const columns = Math.ceil(width / spacing);
  const rows = Math.ceil(height / spacing);

  ctx.save();
  ctx.fillStyle = 'rgba(203,213,225,0.22)';
  ctx.font = '700 10px Inter, Segoe UI, system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';

  for (let column = 0; column < columns; column += 1) {
    for (let row = 0; row < rows; row += 1) {
      const label = `${getTacticalGridColumnLabel(column)}${row + 1}`;
      ctx.fillText(label, column * spacing + 18, row * spacing + 18);
    }
  }

  ctx.restore();
}

function getTacticalGridColumnLabel(index) {
  let label = '';
  let value = Math.max(0, Math.floor(index));

  do {
    label = String.fromCharCode(65 + (value % 26)) + label;
    value = Math.floor(value / 26) - 1;
  } while (value >= 0);

  return label;
}

function drawTacticalGridVignette(width, height) {
  const radius = Math.max(width, height);
  const vignette = ctx.createRadialGradient(
    width / 2,
    height / 2,
    radius * 0.18,
    width / 2,
    height / 2,
    radius * 0.68
  );

  vignette.addColorStop(0, 'rgba(0,0,0,0)');
  vignette.addColorStop(1, 'rgba(0,0,0,0.28)');

  ctx.save();
  ctx.fillStyle = vignette;
  ctx.fillRect(0, 0, width, height);
  ctx.restore();
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

    if (runtime && getFlagCarrierPlayer(runtime)) {
      continue;
    }

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


function triggerScreenShake(strengthPx = 5, durationMs = 130) {
  state.screenShake.ageMs = 0;
  state.screenShake.durationMs = durationMs;
  state.screenShake.strengthPx = strengthPx;
  state.screenShake.seed = Math.random() * 1000;
}

function updateScreenShake(dtMs) {
  if (!dtMs || !state.screenShake || state.screenShake.ageMs >= state.screenShake.durationMs) {
    return;
  }

  state.screenShake.ageMs += dtMs;
}

function getScreenShakeOffset() {
  const shake = state.screenShake;

  if (!shake || shake.ageMs >= shake.durationMs) {
    return { x: 0, y: 0 };
  }

  const progress = clamp(shake.ageMs / Math.max(1, shake.durationMs), 0, 1);
  const amplitude = shake.strengthPx * Math.pow(1 - progress, 2);
  const phase = shake.seed + shake.ageMs * 0.09;

  return {
    x: Math.cos(phase * 2.13) * amplitude,
    y: Math.sin(phase * 2.87) * amplitude,
  };
}

function shouldShakeOnHit(gameEvent) {
  const localId = String(state.myPlayerId || '');
  const eventPlayerIds = [
    gameEvent.shooterId,
    gameEvent.victimId,
    gameEvent.playerId,
    gameEvent.targetId,
    gameEvent.hitPlayerId,
    gameEvent.targetPlayerId,
  ]
    .filter((value) => value !== undefined && value !== null)
    .map((value) => String(value));

  if (localId && eventPlayerIds.includes(localId)) {
    return true;
  }

  const me = state.players.find((player) => player.id === state.myPlayerId);

  if (!me) {
    return false;
  }

  const impactX = Number(gameEvent.impactX);
  const impactY = Number(gameEvent.impactY);

  if (!Number.isFinite(impactX) || !Number.isFinite(impactY)) {
    return false;
  }

  return Math.hypot(me.x - impactX, me.y - impactY) <= 220;
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

    if (shouldShakeOnHit(gameEvent)) {
      triggerScreenShake(5, 130);
    }
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

  const particleCount = 34 + Math.floor(Math.random() * 10);
  const particles = [];

  for (let index = 0; index < particleCount; index += 1) {
    const isStreak = index % 3 === 0;
    const angle = (Math.PI * 2 * index) / particleCount + (Math.random() - 0.5) * 0.5;

    particles.push({
      angle,
      speed: isStreak ? 210 + Math.random() * 150 : 80 + Math.random() * 130,
      radius: isStreak ? 1.2 + Math.random() * 1.2 : 1.8 + Math.random() * 2.6,
      length: isStreak ? 14 + Math.random() * 20 : 5 + Math.random() * 8,
      drift: (Math.random() - 0.5) * 28,
      delay: Math.random() * 70,
      kind: isStreak ? 'streak' : 'ember',
    });
  }

  state.effects.push({
    id: `fx-${Math.random().toString(36).slice(2, 10)}`,
    type: 'hitExplosion',
    layer: 'over',
    x: impactX,
    y: impactY,
    team,
    ageMs: 0,
    durationMs: HIT_EFFECT_DURATION_MS,
    rotation: Math.random() * Math.PI * 2,
    particles,
  });

  if (state.effects.length > 48) {
    state.effects.splice(0, state.effects.length - 48);
  }
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
    layer: 'under',
    x: impactX,
    y: impactY,
    team,
    ageMs: 0,
    durationMs: IMPACT_SPARK_DURATION_MS,
    particles,
  });
}

function updateEffects(dtMs) {
  updateScreenShake(dtMs);

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

function easeOutCubic(value) {
  const t = clamp(Number(value) || 0, 0, 1);
  return 1 - Math.pow(1 - t, 3);
}

function drawHitExplosionEffect(effect) {
  const progress = clamp(effect.ageMs / effect.durationMs, 0, 1);
  const eased = easeOutCubic(progress);
  const fade = Math.pow(1 - progress, 1.15);
  const accentColor = TEAM_COLORS[effect.team] || '#ffffff';

  ctx.save();
  ctx.globalCompositeOperation = 'lighter';

  const flashRadius = 16 + eased * 54;
  const flash = ctx.createRadialGradient(
    effect.x,
    effect.y,
    1,
    effect.x,
    effect.y,
    flashRadius
  );

  flash.addColorStop(0, toRgba('#ffffff', 0.95 * fade));
  flash.addColorStop(0.24, toRgba(accentColor, 0.6 * fade));
  flash.addColorStop(1, toRgba(accentColor, 0));

  ctx.fillStyle = flash;
  ctx.beginPath();
  ctx.arc(effect.x, effect.y, flashRadius, 0, Math.PI * 2);
  ctx.fill();

  for (let ringIndex = 0; ringIndex < 3; ringIndex += 1) {
    const ringProgress = clamp(progress * 1.2 - ringIndex * 0.14, 0, 1);

    if (ringProgress <= 0) {
      continue;
    }

    const ringFade = Math.pow(1 - ringProgress, 1.4) * fade;
    const radius = 10 + ringProgress * (32 + ringIndex * 13);

    ctx.lineWidth = 2.8 - ringIndex * 0.45;
    ctx.strokeStyle = toRgba(ringIndex === 0 ? '#ffffff' : accentColor, 0.85 * ringFade);
    ctx.beginPath();
    ctx.arc(effect.x, effect.y, radius, 0, Math.PI * 2);
    ctx.stroke();
  }

  ctx.save();
  ctx.translate(effect.x, effect.y);
  ctx.rotate((effect.rotation || 0) + progress * 0.35);

  const markerSize = 14 + Math.sin(progress * Math.PI) * 14;
  const markerInner = markerSize * 0.42;

  ctx.lineCap = 'round';
  ctx.lineWidth = 2.5 + fade * 1.5;
  ctx.strokeStyle = toRgba('#ffffff', 0.9 * fade);

  ctx.beginPath();
  for (const sx of [-1, 1]) {
    for (const sy of [-1, 1]) {
      ctx.moveTo(sx * markerSize, sy * markerSize);
      ctx.lineTo(sx * markerInner, sy * markerInner);
    }
  }
  ctx.stroke();

  ctx.lineWidth = 1.4;
  ctx.strokeStyle = toRgba(accentColor, 0.9 * fade);
  ctx.stroke();

  ctx.restore();

  for (const particle of effect.particles) {
    const localAge = effect.ageMs - (particle.delay || 0);

    if (localAge <= 0) {
      continue;
    }

    const localProgress = clamp(localAge / Math.max(1, effect.durationMs - (particle.delay || 0)), 0, 1);
    const particleFade = Math.pow(1 - localProgress, 1.35);
    const travel = 7 + particle.speed * (localAge / 1000);
    const drift = particle.drift * localProgress;

    const px =
      effect.x +
      Math.cos(particle.angle) * travel +
      Math.cos(effect.ageMs / 80 + particle.angle) * drift;

    const py =
      effect.y +
      Math.sin(particle.angle) * travel +
      Math.sin(effect.ageMs / 95 + particle.angle) * drift;

    if (particle.kind === 'streak') {
      const tailX = px - Math.cos(particle.angle) * particle.length * (1 - localProgress * 0.35);
      const tailY = py - Math.sin(particle.angle) * particle.length * (1 - localProgress * 0.35);

      ctx.lineCap = 'round';
      ctx.lineWidth = Math.max(0.8, particle.radius * (1 - localProgress * 0.55));
      ctx.strokeStyle = toRgba(accentColor, 0.88 * particleFade);
      ctx.beginPath();
      ctx.moveTo(tailX, tailY);
      ctx.lineTo(px, py);
      ctx.stroke();

      ctx.strokeStyle = toRgba('#ffffff', 0.55 * particleFade);
      ctx.lineWidth = 0.8;
      ctx.stroke();
    } else {
      const radius = Math.max(0.6, particle.radius * (1 - localProgress * 0.75));

      ctx.fillStyle = toRgba(accentColor, 0.86 * particleFade);
      ctx.beginPath();
      ctx.arc(px, py, radius, 0, Math.PI * 2);
      ctx.fill();

      ctx.fillStyle = toRgba('#ffffff', 0.45 * particleFade);
      ctx.beginPath();
      ctx.arc(px, py, radius * 0.45, 0, Math.PI * 2);
      ctx.fill();
    }
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

function drawEffects(layer = 'under') {
  if (state.effects.length === 0) {
    return;
  }

  for (const effect of state.effects) {
    const effectLayer = effect.layer || 'under';

    if (effectLayer !== layer) {
      continue;
    }

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

function normalizeTeamValue(value) {
  const normalized = String(value || '').trim().toLowerCase();
  return normalized === 'blue' || normalized === 'red' ? normalized : null;
}

function getOpposingTeam(team) {
  const normalizedTeam = normalizeTeamValue(team);
  if (normalizedTeam === 'blue') return 'red';
  if (normalizedTeam === 'red') return 'blue';
  return null;
}

function getPlayerAvatarStateKey(player) {
  if (player && player.id !== undefined && player.id !== null) {
    return String(player.id);
  }
  return `${player?.team || 'neutral'}:${player?.name || 'player'}`;
}

function getNumericPlayerPosition(player) {
  return {
    x: Number.isFinite(Number(player?.x)) ? Number(player.x) : 0,
    y: Number.isFinite(Number(player?.y)) ? Number(player.y) : 0,
  };
}

function getMovementInputVector() {
  const x = (state.input.right ? 1 : 0) - (state.input.left ? 1 : 0);
  const y = (state.input.down ? 1 : 0) - (state.input.up ? 1 : 0);
  const length = Math.hypot(x, y);

  if (length <= 0) {
    return { x: 0, y: 0 };
  }

  return { x: x / length, y: y / length };
}

function limitVectorLength(x, y, maxLength) {
  const length = Math.hypot(x, y);

  if (!Number.isFinite(length) || length <= maxLength || maxLength <= 0) {
    return { x, y };
  }

  const scale = maxLength / length;
  return { x: x * scale, y: y * scale };
}

function getPlayerRenderState(player) {
  return playerRenderStates.get(getPlayerAvatarStateKey(player));
}

function getPlayerRenderPosition(player) {
  const renderState = getPlayerRenderState(player);

  if (renderState) {
    return { x: renderState.x, y: renderState.y };
  }

  return getNumericPlayerPosition(player);
}

function getPlayerRenderProxy(player) {
  const position = getPlayerRenderPosition(player);
  return {
    ...player,
    x: position.x,
    y: position.y,
  };
}

function updatePlayerRenderStates(dtMs, now = performance.now()) {
  const activeKeys = new Set();
  const dt = Math.min(64, Math.max(0, Number(dtMs) || 0));
  const blend = 1 - Math.exp(-dt / PLAYER_POSITION_SMOOTHING_MS);
  const selfInputVector = getMovementInputVector();
  const selfInputActive = Math.hypot(selfInputVector.x, selfInputVector.y) > 0;

  for (const player of state.players) {
    const key = getPlayerAvatarStateKey(player);
    const isSelf = player.id === state.myPlayerId;
    const target = getNumericPlayerPosition(player);
    let renderState = playerRenderStates.get(key);

    activeKeys.add(key);

    if (!renderState) {
      renderState = {
        x: target.x,
        y: target.y,
        targetX: target.x,
        targetY: target.y,
        velocityX: 0,
        velocityY: 0,
        lastTargetAt: now,
        lastFrameAt: now,
      };
      playerRenderStates.set(key, renderState);
      continue;
    }

    const targetDelta = Math.hypot(target.x - renderState.targetX, target.y - renderState.targetY);

    if (targetDelta > PLAYER_POSITION_CHANGE_EPSILON_PX) {
      const elapsedSinceTargetSeconds = Math.max(0.016, (now - renderState.lastTargetAt) / 1000);
      renderState.velocityX = (target.x - renderState.targetX) / elapsedSinceTargetSeconds;
      renderState.velocityY = (target.y - renderState.targetY) / elapsedSinceTargetSeconds;
      renderState.targetX = target.x;
      renderState.targetY = target.y;
      renderState.lastTargetAt = now;
    } else if (now - renderState.lastTargetAt > 180) {
      renderState.velocityX *= 0.86;
      renderState.velocityY *= 0.86;
    }

    let desiredX = renderState.targetX;
    let desiredY = renderState.targetY;
    const targetAgeMs = Math.max(0, now - renderState.lastTargetAt);
    const predictionMs = isSelf
      ? Math.min(PLAYER_POSITION_SELF_PREDICTION_MS, targetAgeMs + dt)
      : Math.min(PLAYER_POSITION_REMOTE_PREDICTION_MS, targetAgeMs);

    if (predictionMs > 0) {
      let predictionX = renderState.velocityX * (predictionMs / 1000);
      let predictionY = renderState.velocityY * (predictionMs / 1000);
      const limitedPrediction = limitVectorLength(predictionX, predictionY, PLAYER_POSITION_MAX_PREDICTION_PX);
      predictionX = limitedPrediction.x;
      predictionY = limitedPrediction.y;

      if (isSelf && selfInputActive) {
        const predictedLength = Math.hypot(predictionX, predictionY);
        if (predictedLength > 0) {
          predictionX = selfInputVector.x * predictedLength;
          predictionY = selfInputVector.y * predictedLength;
        }
      }

      desiredX += predictionX;
      desiredY += predictionY;
    }

    const distanceToServer = Math.hypot(renderState.x - target.x, renderState.y - target.y);
    const distanceToDesired = Math.hypot(renderState.x - desiredX, renderState.y - desiredY);

    if (distanceToServer > PLAYER_POSITION_SNAP_DISTANCE_PX || distanceToDesired > PLAYER_POSITION_SNAP_DISTANCE_PX) {
      renderState.x = target.x;
      renderState.y = target.y;
    } else {
      renderState.x += (desiredX - renderState.x) * blend;
      renderState.y += (desiredY - renderState.y) * blend;

      if (Math.hypot(renderState.x - desiredX, renderState.y - desiredY) < 0.02) {
        renderState.x = desiredX;
        renderState.y = desiredY;
      }
    }

    renderState.lastFrameAt = now;
  }

  for (const key of playerRenderStates.keys()) {
    if (!activeKeys.has(key)) {
      playerRenderStates.delete(key);
    }
  }
}

function getPlayerAvatarMotion(player, isSelf, now) {
  const key = getPlayerAvatarStateKey(player);
  const x = Number(player.x) || 0;
  const y = Number(player.y) || 0;
  const existing = playerAvatarStates.get(key) || {
    x,
    y,
    walkPhase: hashString(key) % 360,
    lastUpdateAt: now,
    lastMovementAt: 0,
  };

  const movedDistance = Math.hypot(x - existing.x, y - existing.y);
  const selfInputMoving = Boolean(isSelf && (state.input.up || state.input.down || state.input.left || state.input.right));

  if (movedDistance > PLAYER_AVATAR_MOVE_EPSILON_PX || selfInputMoving) {
    existing.lastMovementAt = now;
  }

  const dt = Math.min(0.064, Math.max(0, (now - existing.lastUpdateAt) / 1000));
  const movingRecently = now - existing.lastMovementAt <= PLAYER_AVATAR_MOTION_TTL_MS;
  existing.walkPhase += dt * (movingRecently ? PLAYER_AVATAR_WALK_PHASE_SPEED : PLAYER_AVATAR_IDLE_PHASE_SPEED);
  existing.x = x;
  existing.y = y;
  existing.lastUpdateAt = now;
  playerAvatarStates.set(key, existing);

  const facing = getFacingVector(player);
  return {
    angle: facing.angle,
    walkPhase: existing.walkPhase,
  };
}

function getFlagCarrierId(flag) {
  const directCandidates = [
    flag?.carrierId,
    flag?.carriedBy,
    flag?.carriedById,
    flag?.holderId,
    flag?.playerId,
  ];

  for (const candidate of directCandidates) {
    if (candidate !== undefined && candidate !== null && String(candidate).trim()) {
      return String(candidate);
    }
  }

  const nestedCandidates = [flag?.carrier?.id, flag?.holder?.id, flag?.player?.id];
  for (const candidate of nestedCandidates) {
    if (candidate !== undefined && candidate !== null && String(candidate).trim()) {
      return String(candidate);
    }
  }

  return null;
}

function isFlagAtBase(flag) {
  const x = Number(flag?.x);
  const y = Number(flag?.y);
  const baseX = Number(flag?.baseX);
  const baseY = Number(flag?.baseY);

  if (![x, y, baseX, baseY].every(Number.isFinite)) {
    return false;
  }

  return Math.hypot(x - baseX, y - baseY) <= 3;
}

function getFlagCarrierPlayer(flag) {
  const flagTeam = normalizeTeamValue(flag?.team);
  const explicitCarrierId = getFlagCarrierId(flag);

  if (explicitCarrierId) {
    return state.players.find((player) => String(player.id) === explicitCarrierId) || null;
  }

  const status = String(flag?.status || flag?.state || '').toLowerCase();
  if (status === 'home' || status === 'base' || status === 'dropped' || isFlagAtBase(flag)) {
    return null;
  }

  const flagX = Number(flag?.x);
  const flagY = Number(flag?.y);
  if (!Number.isFinite(flagX) || !Number.isFinite(flagY)) {
    return null;
  }

  let nearest = null;
  let nearestDistance = Infinity;
  for (const player of state.players) {
    const playerTeam = normalizeTeamValue(player.team);
    if (flagTeam && playerTeam && flagTeam === playerTeam) {
      continue;
    }

    const distance = Math.hypot(flagX - player.x, flagY - player.y);
    const maxDistance = Math.max(Number(player.radius) || 14, PLAYER_AVATAR_VISUAL_RADIUS) + 8;
    if (distance <= maxDistance && distance < nearestDistance) {
      nearest = player;
      nearestDistance = distance;
    }
  }

  return nearest;
}

function getPlayerCarriedFlagTeam(player) {
  const directTeamCandidates = [
    player?.carryingFlagTeam,
    player?.carriedFlagTeam,
    player?.flagTeam,
    player?.capturedFlagTeam,
    player?.hasFlagTeam,
  ];

  for (const candidate of directTeamCandidates) {
    const team = normalizeTeamValue(candidate);
    if (team) {
      return team;
    }
  }

  const nestedTeamCandidates = [
    player?.flag?.team,
    player?.carryingFlag?.team,
    player?.carriedFlag?.team,
  ];

  for (const candidate of nestedTeamCandidates) {
    const team = normalizeTeamValue(candidate);
    if (team) {
      return team;
    }
  }

  if (player?.hasFlag === true || player?.carryingFlag === true || player?.carriesFlag === true) {
    return getOpposingTeam(player.team);
  }

  for (const flag of state.flags) {
    const carrier = getFlagCarrierPlayer(flag);
    if (carrier === player || (carrier && carrier.id !== undefined && player.id !== undefined && String(carrier.id) === String(player.id))) {
      return normalizeTeamValue(flag.team) || getOpposingTeam(player.team);
    }
  }

  return null;
}

function getPlayerAvatarLabelY(player, carriedFlagTeam) {
  const radius = Number(player.radius) || 14;
  const visualRadius = Math.max(radius, PLAYER_AVATAR_VISUAL_RADIUS);
  const flagClearance = carriedFlagTeam ? 8 : 0;
  return player.y - visualRadius - 12 - flagClearance;
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

function drawPlayerAvatar(player, isSelf, now = performance.now(), carriedFlagTeam = getPlayerCarriedFlagTeam(player)) {
  const teamColor = TEAM_COLORS[player.team] || '#ffffff';
  const radius = Number(player.radius) || 14;
  const motion = getPlayerAvatarMotion(player, isSelf, now);

  ctx.save();

  if (isSelf) {
    const pulse = 0.75 + (Math.sin(now / 220) + 1) * 0.12;
    ctx.fillStyle = toRgba(teamColor, 0.18);
    ctx.beginPath();
    ctx.arc(player.x, player.y, Math.max(radius * pulse + 5, PLAYER_AVATAR_VISUAL_RADIUS * 0.92), 0, Math.PI * 2);
    ctx.fill();
  }

  playerAvatarRenderer.draw(ctx, {
    x: player.x,
    y: player.y,
    angle: motion.angle,
    walkPhase: motion.walkPhase,
    team: player.team,
    size: PLAYER_AVATAR_SIZE,
    hasFlag: Boolean(carriedFlagTeam),
    flagTeam: carriedFlagTeam,
  });

  if (isSelf) {
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(player.x, player.y, Math.max(radius + 4, PLAYER_AVATAR_VISUAL_RADIUS * 0.92), 0, Math.PI * 2);
    ctx.stroke();
  }

  ctx.restore();
}

function drawPlayers() {
  const now = performance.now();
  const activeAvatarStateKeys = new Set();

  for (const player of state.players) {
    const isSelf = player.id === state.myPlayerId;
    const avatarStateKey = getPlayerAvatarStateKey(player);
    const carriedFlagTeam = getPlayerCarriedFlagTeam(player);
    const renderPlayer = getPlayerRenderProxy(player);
    activeAvatarStateKeys.add(avatarStateKey);

    drawPlayerAvatar(renderPlayer, isSelf, now, carriedFlagTeam);

    ctx.save();
    ctx.font = '16px Segoe UI';
    ctx.textAlign = 'center';
    ctx.lineWidth = 4;
    ctx.strokeStyle = 'rgba(0,0,0,0.75)';
    const labelY = getPlayerAvatarLabelY(renderPlayer, carriedFlagTeam);
    ctx.strokeText(renderPlayer.name, renderPlayer.x, labelY);
    ctx.fillStyle = '#ffffff';
    ctx.fillText(renderPlayer.name, renderPlayer.x, labelY);
    ctx.restore();
  }

  for (const key of playerAvatarStates.keys()) {
    if (!activeAvatarStateKeys.has(key)) {
      playerAvatarStates.delete(key);
    }
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
