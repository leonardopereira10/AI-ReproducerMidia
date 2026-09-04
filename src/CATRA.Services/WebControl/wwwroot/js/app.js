// ==========================================================================
// CATRA — Frontend SPA: Router + Home + Player
// JavaScript puro (ES6+), sem framework, sem build tool.
// ==========================================================================

'use strict';

// ── Utilities ──────────────────────────────────────────────────────────────

/**
 * Format seconds into MM:SS (< 1h) or HH:MM:SS (>= 1h).
 * @param {number} seconds
 * @returns {string}
 */
function formatTime(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) seconds = 0;
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h > 0) {
    return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  }
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

/**
 * Escape HTML to prevent XSS when injecting user data.
 * @param {string} str
 * @returns {string}
 */
function escapeHtml(str) {
  if (!str) return '';
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

// ── Font Awesome icons (self-hosted solid subset — css/fontawesome.css) ─

/** Icon markup used across the panel. */
const FA_ICONS = {
  play: '<i class="fa-solid fa-play"></i>',
  pause: '<i class="fa-solid fa-pause"></i>',
  tv: '<i class="fa-solid fa-tv"></i>',
  film: '<i class="fa-solid fa-film"></i>',
  volumeHigh: '<i class="fa-solid fa-volume-high"></i>',
  volumeLow: '<i class="fa-solid fa-volume-low"></i>',
  volumeMuted: '<i class="fa-solid fa-volume-xmark"></i>',
};

/**
 * Renders a server-provided profile label, replacing the legacy emoji prefix
 * (🖥 / 📄 / 📺 — shared with the desktop UI) with a Font Awesome icon.
 * @param {string} label
 * @returns {string} HTML
 */
function renderProfileLabel(label) {
  if (!label) return '';
  const map = { '\u{1F5A5}': 'fa-desktop', '\u{1F4C4}': 'fa-file', '\u{1F4BA}': 'fa-tv' };
  for (const [emoji, cls] of Object.entries(map)) {
    if (label.startsWith(emoji)) {
      return `<i class="fa-solid ${cls}"></i> ${escapeHtml(label.slice(emoji.length).trimStart())}`;
    }
  }
  return escapeHtml(label);
}

// ── View management ────────────────────────────────────────────────────────

const views = {
  home: document.getElementById('view-home'),
  category: document.getElementById('view-category'),
  detail: document.getElementById('view-detail'),
  player: document.getElementById('view-player'),
};

function showView(viewName) {
  for (const [key, el] of Object.entries(views)) {
    el.style.display = key === viewName ? '' : 'none';
  }
  // Scroll to top on view change
  window.scrollTo(0, 0);
}

// ── Router ─────────────────────────────────────────────────────────────────

class Router {
  constructor(app) {
    this.app = app;
    window.addEventListener('hashchange', () => this.navigate());
    this.navigate();
  }

  navigate() {
    const hash = location.hash || '#/';
    const [path, queryString] = hash.slice(2).split('?');
    const parts = path.split('/').filter(Boolean);
    const params = new URLSearchParams(queryString || '');

    if (parts.length === 0 || parts[0] === 'home') {
      showView('home');
      this.app.loadHome();
    } else if (parts[0] === 'category' && parts[1]) {
      showView('category');
      this.app.loadCategory(parseInt(parts[1], 10));
    } else if (parts[0] === 'item' && parts[1]) {
      showView('detail');
      this.app.loadDetail(parseInt(parts[1], 10));
    } else if (parts[0] === 'play' && parts[1]) {
      showView('player');
      const profile = params.get('profile') || 'original';
      this.app.loadPlayer(parseInt(parts[1], 10), profile);
    } else if (parts[0] === 'transmissao') {
      showView('player');
      this.app.loadCastSession();
    } else if (parts[0] === 'search') {
      showView('home');
      const q = params.get('q') || '';
      this.app.loadSearch(q);
    } else {
      // Fallback to home
      location.hash = '#/';
    }
  }
}

// ── DOM references ─────────────────────────────────────────────────────────

const dom = {
  // Home
  searchInput: document.getElementById('search-input'),
  continueList: document.getElementById('continue-list'),
  categoryList: document.getElementById('category-list'),
  continueWatching: document.getElementById('continue-watching'),
  categories: document.getElementById('categories'),
  searchResults: document.getElementById('search-results'),
  searchList: document.getElementById('search-list'),
  castBanner: document.getElementById('cast-banner'),
  btnFollowCast: document.getElementById('btn-follow-cast'),
  castBannerDetail: document.getElementById('cast-banner-detail'),

  // Category
  btnBackCategory: document.getElementById('btn-back-category'),
  categoryTitle: document.getElementById('category-title'),
  categoryItems: document.getElementById('category-items'),

  // Detail (episodes list)
  btnBackEpisodes: document.getElementById('btn-back-episodes'),
  detailTitle: document.getElementById('detail-title'),
  episodeList: document.getElementById('episode-list'),

  // Player
  btnBackPlayer: document.getElementById('btn-back-detail'),
  playerTitle: document.getElementById('player-title'),
  profileSelect: document.getElementById('profile-select'),
  videoPlayer: document.getElementById('video-player'),
  videoContainer: document.getElementById('video-container'),
  thumbnailContainer: document.getElementById('thumbnail-container'),
  thumbnail: document.getElementById('thumbnail'),
  currentTime: document.getElementById('current-time'),
  seekBar: document.getElementById('seek-bar'),
  totalTime: document.getElementById('total-time'),
  btnPrev: document.getElementById('btn-prev'),
  btnBack: document.getElementById('btn-back'),
  btnPlayPause: document.getElementById('btn-play-pause'),
  btnForward: document.getElementById('btn-forward'),
  btnNext: document.getElementById('btn-next'),
  btnSkipIntro: document.getElementById('btn-skip-intro'),
  volumeIcon: document.getElementById('volume-icon'),
  volumeBar: document.getElementById('volume-bar'),
  volumeValue: document.getElementById('volume-value'),
  deviceName: document.getElementById('device-name'),
  profileLabel: document.getElementById('profile-label'),
  btnCast: document.getElementById('btn-cast'),
  castDeviceList: document.getElementById('cast-device-list'),
  btnStopCast: document.getElementById('btn-stop-cast'),
  queueSection: document.getElementById('queue-section'),
  queueList: document.getElementById('queue-list'),

  // Connection
  connectionStatus: document.getElementById('connection-status'),
};

// ── App (main controller) ──────────────────────────────────────────────────

class App {
  constructor() {
    /** @type {CatraClient|null} */
    this.client = null;
    /** @type {Router|null} */
    this.router = null;
    /** Current category ID being viewed */
    this.currentCategoryId = null;
    /** Current item ID being viewed in detail */
    this.currentItemId = null;
    /** Current episode ID being played */
    this.currentEpisodeId = null;
    /** Search debounce timer */
    this._searchTimeout = null;
    /** Whether HEVC (H.265) is supported by this browser */
    this._hevcSupported = false;
  }

  init() {
    this.client = new CatraClient(this);
    this._bindNavigation();
    this._bindSearch();
    this._checkCodecSupport();
    this._setupVideoHandlers();

    // Home transmission banner → open the "Acompanhar transmissão" screen
    dom.btnFollowCast.addEventListener('click', () => {
      location.hash = '#/transmissao';
    });

    this.router = new Router(this);
  }

  // ── HEVC codec detection ─────────────────────────────────────────────

  _checkCodecSupport() {
    const video = dom.videoPlayer;
    // Check for HEVC Main Profile Level 3.1
    const canPlay = video.canPlayType('video/mp4; codecs="hev1.1.6.L93.B0"');
    this._hevcSupported = canPlay !== '';
  }

  // ── Navigation bindings ──────────────────────────────────────────────

  _bindNavigation() {
    // Category view → back to home
    dom.btnBackCategory.addEventListener('click', () => {
      location.hash = '#/';
    });

    // Detail (episodes) view → back to category or home
    dom.btnBackEpisodes.addEventListener('click', () => {
      if (this.currentCategoryId) {
        location.hash = `#/category/${this.currentCategoryId}`;
      } else {
        location.hash = '#/';
      }
    });

    // Player view → back to detail (episodes) or home
    dom.btnBackPlayer.addEventListener('click', () => {
      if (this.currentItemId) {
        location.hash = `#/item/${this.currentItemId}`;
      } else {
        location.hash = '#/';
      }
    });

    // Profile select change → send switchProfile via WebSocket + update URL
    dom.profileSelect.addEventListener('change', () => {
      const profile = dom.profileSelect.value;
      if (this.client) {
        this.client.send({ type: 'switchProfile', profile });
      }
      // Update URL to reflect profile (replaceState avoids hashchange re-trigger)
      const episodeId = this.currentEpisodeId;
      if (episodeId) {
        history.replaceState(null, '', `#/play/${episodeId}?profile=${profile}`);
      }
    });
  }

  // ── Search with debounce ─────────────────────────────────────────────

  _bindSearch() {
    dom.searchInput.addEventListener('input', (e) => {
      clearTimeout(this._searchTimeout);
      const value = e.target.value.trim();
      this._searchTimeout = setTimeout(() => {
        if (value.length > 0) {
          location.hash = `#/search?q=${encodeURIComponent(value)}`;
        } else {
          // Clear search, show normal home
          dom.searchResults.style.display = 'none';
          dom.continueWatching.style.display = '';
          dom.categories.style.display = '';
        }
      }, 300);
    });
  }

  // ── Home: load categories + continue watching ────────────────────────

  async loadHome() {
    // Reset search
    dom.searchResults.style.display = 'none';
    dom.continueWatching.style.display = '';
    dom.categories.style.display = '';

    try {
      const [categories, continueWatching] = await Promise.all([
        this._fetchJson('/api/library/categories'),
        this._fetchJson('/api/library/continue-watching'),
      ]);

      this._renderCategories(categories);
      this._renderContinueWatching(continueWatching);
    } catch (err) {
      console.error('Failed to load home:', err);
      dom.categoryList.innerHTML = '<div class="empty-state">Erro ao carregar categorias</div>';
      dom.continueList.innerHTML = '';
    }
  }

  _renderCategories(categories) {
    if (!Array.isArray(categories) || categories.length === 0) {
      dom.categoryList.innerHTML = '<div class="empty-state">Nenhuma categoria encontrada</div>';
      return;
    }

    dom.categoryList.innerHTML = categories.map(c => `
      <div class="card" data-nav="#/category/${c.id}">
        <div class="card-title">${escapeHtml(c.name)}</div>
        <div class="card-subtitle">${c.itemCount ?? 0} itens</div>
      </div>
    `).join('');

    this._bindCardNavigation(dom.categoryList);
  }

  _renderContinueWatching(items) {
    if (!Array.isArray(items) || items.length === 0) {
      dom.continueList.innerHTML = '<div class="empty-state">Nenhum vídeo em andamento</div>';
      return;
    }

    dom.continueList.innerHTML = items.map(item => {
      const position = item.lastPositionSec || 0;
      const duration = item.durationSec || 0;
      const progress = typeof item.progressPct === 'number'
        ? Math.min(100, Math.round(item.progressPct))
        : (duration > 0 ? Math.min(100, Math.round((position / duration) * 100)) : 0);
      const thumbUrl = item.thumbnailPath ? `/api/thumbnail/${item.episodeId}` : null;
      const thumbHtml = thumbUrl
        ? `<img class="card-thumb" src="${escapeHtml(thumbUrl)}" alt="" loading="lazy" />`
        : '';
      const seriesTitle = item.itemTitle || '';
      const episodeLabel = item.displayTitle || '';

      return `
        <div class="card" data-nav="#/play/${item.episodeId}">
          ${thumbHtml}
          <div class="card-title">${escapeHtml(seriesTitle || episodeLabel)}</div>
          <div class="card-subtitle">${episodeLabel ? `${escapeHtml(episodeLabel)} · ` : ''}${formatTime(position)} / ${formatTime(duration)}</div>
          <div class="card-progress">
            <div class="card-progress-bar" style="width:${progress}%"></div>
          </div>
        </div>
      `;
    }).join('');

    this._bindCardNavigation(dom.continueList);
  }

  // ── Detail: load episodes for an item ────────────────────────────────

  // ── Category: load items (series/movies) for a category ──────────────

  async loadCategory(categoryId) {
    this.currentCategoryId = categoryId;
    this.currentItemId = null; // reset item context
    dom.categoryTitle.textContent = 'Carregando...';
    dom.categoryItems.innerHTML = '<div class="loading-spinner">Carregando...</div>';

    try {
      // Fetch category name + items in parallel
      const [categories, items] = await Promise.all([
        this._fetchJson('/api/library/categories'),
        this._fetchJson(`/api/library/categories/${categoryId}/items`)
      ]);

      const category = Array.isArray(categories) ? categories.find(c => c.id === categoryId) : null;
      dom.categoryTitle.textContent = category?.name || `Categoria #${categoryId}`;

      if (Array.isArray(items) && items.length > 0) {
        this._renderCategoryItems(items);
      } else {
        dom.categoryItems.innerHTML = '<div class="empty-state">Nenhum item encontrado</div>';
      }
    } catch (err) {
      console.error('Failed to load category:', err);
      dom.categoryTitle.textContent = 'Erro';
      dom.categoryItems.innerHTML = '<div class="empty-state">Erro ao carregar categoria</div>';
    }
  }

  _renderCategoryItems(items) {
    dom.categoryItems.innerHTML = items.map(item => {
      const typeIcon = item.mediaType === 1 ? FA_ICONS.film : FA_ICONS.tv; // MediaType: 0=Series, 1=Movie
      const posterHtml = item.posterUrl
        ? `<img class="card-thumb" src="${escapeHtml(item.posterUrl)}" alt="" loading="lazy" />`
        : '';
      const subtitle = [item.year, item.genre].filter(Boolean).join(' · ') || (item.mediaType === 1 ? 'Filme' : 'Série');

      return `
        <div class="card" data-nav="#/item/${item.id}">
          ${posterHtml}
          <div class="card-type">${typeIcon}</div>
          <div class="card-title">${escapeHtml(item.title)}</div>
          <div class="card-subtitle">${escapeHtml(subtitle)}</div>
        </div>
      `;
    }).join('');

    this._bindCardNavigation(dom.categoryItems);
  }

  // ── Detail: load episodes for an item ──────────────────────────────

  async loadDetail(itemId) {
    this.currentItemId = itemId;
    dom.detailTitle.textContent = 'Carregando...';
    dom.episodeList.innerHTML = '<div class="loading-spinner">Carregando episódios...</div>';

    try {
      const [item, episodes] = await Promise.all([
        this._fetchJson(`/api/library/items/${itemId}`).catch(() => null),
        this._fetchJson(`/api/library/items/${itemId}/episodes`),
      ]);

      dom.detailTitle.textContent = (item && item.title) ? item.title : `Item #${itemId}`;

      if (Array.isArray(episodes) && episodes.length > 0) {
        this._renderEpisodes(episodes);
      } else {
        dom.episodeList.innerHTML = '<div class="empty-state">Nenhum episódio encontrado</div>';
      }
    } catch (err) {
      console.error('Failed to load detail:', err);
      dom.detailTitle.textContent = 'Erro';
      dom.episodeList.innerHTML = '<div class="empty-state">Erro ao carregar episódios</div>';
    }
  }

  _renderEpisodes(episodes) {
    if (!Array.isArray(episodes) || episodes.length === 0) {
      dom.episodeList.innerHTML = '<div class="empty-state">Nenhum episódio encontrado</div>';
      return;
    }

    dom.episodeList.innerHTML = episodes.map(ep => {
      const thumbHtml = ep.thumbnailPath
        ? `<img class="episode-thumb" src="/api/thumbnail/${ep.id}" alt="" loading="lazy" />`
        : '';

      return `
        <div class="episode-card" data-nav="#/play/${ep.id}">
          ${thumbHtml}
          <div class="episode-info">
            <div class="episode-title">${escapeHtml(ep.displayTitle)}</div>
            <div class="episode-duration">${formatTime(ep.durationSec || 0)}</div>
          </div>
        </div>
      `;
    }).join('');

    this._bindCardNavigation(dom.episodeList);
  }

  // ── Player: load player view for an episode ──────────────────────────

  async loadPlayer(episodeId, profile) {
    this.currentEpisodeId = episodeId;
    dom.playerTitle.textContent = 'Carregando...';
    dom.profileSelect.style.display = '';

    // If a transmission to the TV is already active, the backend owns the
    // playback session — render the cast screen instead of requesting a
    // browser stream (a playEpisode here would otherwise interrupt the TV).
    const state = this.client && this.client.state;
    if (state && state.mode === 'dlna' && state.castDeviceName) {
      this.loadCastSession();
      return;
    }

    try {
      // Load profiles for this episode
      const profiles = await this._fetchJson(`/api/library/episodes/${episodeId}/profiles`);
      this._renderProfileOptions(profiles, profile);
      dom.playerTitle.textContent = `Episódio #${episodeId}`;
    } catch (err) {
      console.error('Failed to load player:', err);
      dom.playerTitle.textContent = `Episódio #${episodeId}`;
    }

    // HEVC fallback: if profile is not "original" and HEVC not supported, switch
    if (!this._hevcSupported && profile !== 'original') {
      console.warn('HEVC not supported, falling back to original profile');
      profile = 'original';
      dom.profileSelect.value = profile;
    }

    // Send playEpisode command via WebSocket (server will respond with state containing streamUrl)
    this.client.send({ type: 'playEpisode', episodeId, profile });
  }

  _renderProfileOptions(profiles, currentProfile) {
    const select = dom.profileSelect;
    // Keep the three base options, add processed profiles
    select.innerHTML = `
      <option value="original">Original</option>
      <option value="local">Local</option>
      <option value="dlna">DLNA</option>
    `;

    if (Array.isArray(profiles)) {
      for (const p of profiles) {
        if (p.isProcessed) {
          const opt = document.createElement('option');
          opt.value = p.name;
          opt.textContent = `${p.label || p.name} (${p.width}x${p.height} @ ${p.fps}fps)`;
          select.appendChild(opt);
        }
      }
    }

    select.value = currentProfile;
  }

  // ── Search ───────────────────────────────────────────────────────────

  // ── Cast session: "Acompanhar transmissão" ────────────────────────

  /**
   * Opens the remote-control screen for an active DLNA transmission.
   * The backend owns the stream: nothing is requested here — the UI is
   * rendered from the current WebSocket state and every control is sent
   * as a command that the backend relays to the TV.
   */
  loadCastSession() {
    this.client._currentStreamUrl = null;
    dom.profileSelect.style.display = 'none';

    // Never leave a stale local <video> running — the TV is the player.
    const video = dom.videoPlayer;
    if (video.getAttribute('src')) {
      video.pause();
      video.removeAttribute('src');
      video.load();
    }
    dom.videoContainer.style.display = 'none';

    const state = this.client.state;
    if (state && Object.keys(state).length > 0) {
      this.client._applyFullState(state);
    } else {
      dom.playerTitle.textContent = 'Nenhuma transmissão ativa';
    }
  }

  async loadSearch(query) {
    // Update search input to reflect current query
    dom.searchInput.value = query;

    if (!query || query.trim().length === 0) {
      dom.searchResults.style.display = 'none';
      dom.continueWatching.style.display = '';
      dom.categories.style.display = '';
      return;
    }

    // Hide home sections, show search
    dom.continueWatching.style.display = 'none';
    dom.categories.style.display = 'none';
    dom.searchResults.style.display = '';
    dom.searchList.innerHTML = '<div class="loading-spinner">Buscando...</div>';

    try {
      const results = await this._fetchJson(`/api/library/search?q=${encodeURIComponent(query)}`);
      this._renderSearchResults(results);
    } catch (err) {
      console.error('Search failed:', err);
      dom.searchList.innerHTML = '<div class="empty-state">Erro na busca</div>';
    }
  }

  _renderSearchResults(results) {
    if (!Array.isArray(results) || results.length === 0) {
      dom.searchList.innerHTML = '<div class="empty-state">Nenhum resultado encontrado</div>';
      return;
    }

    dom.searchList.innerHTML = results.map(item => {
      const typeLabel = item.type || '';
      // Route to detail for series/items, play for episodes
      const nav = item.type === 'episode'
        ? `#/play/${item.id}`
        : `#/item/${item.id}`;

      return `
        <div class="card" data-nav="${nav}">
          <div class="card-title">${escapeHtml(item.title)}</div>
          <div class="card-subtitle">${escapeHtml(typeLabel)}</div>
        </div>
      `;
    }).join('');

    this._bindCardNavigation(dom.searchList);
  }

  // ── Card navigation (event delegation) ───────────────────────────────

  _bindCardNavigation(container) {
    // Event delegation — persists across re-renders of inner content
    // Use a data attribute flag to avoid duplicate listeners
    if (!container.dataset.bound) {
      container.dataset.bound = '1';
      container.addEventListener('click', (e) => {
        const card = e.target.closest('[data-nav]');
        if (card) {
          location.hash = card.dataset.nav;
        }
      });
    }
  }

  // ── Video event handlers ─────────────────────────────────────────────

  _setupVideoHandlers() {
    const video = dom.videoPlayer;
    let lastProgressReport = 0;

    // timeupdate → report position to server (~1s throttle)
    video.addEventListener('timeupdate', () => {
      const now = Date.now();
      if (now - lastProgressReport > 1000) {
        lastProgressReport = now;
        if (this.client) {
          this.client.send({ type: 'reportProgress', position: video.currentTime });
        }
      }
    });

    // ended → notify server, auto-play handled by server sending new state
    video.addEventListener('ended', () => {
      if (this.client) {
        this.client.send({ type: 'ended' });
      }
    });

    // loadedmetadata → report duration to server
    video.addEventListener('loadedmetadata', () => {
      if (this.client && Number.isFinite(video.duration)) {
        this.client.send({ type: 'ready', position: video.duration });
      }
    });

    // play/pause → sync UI button
    video.addEventListener('play', () => {
      dom.btnPlayPause.innerHTML = FA_ICONS.pause;
    });
    video.addEventListener('pause', () => {
      dom.btnPlayPause.innerHTML = FA_ICONS.play;
    });

    // error → log for debugging
    video.addEventListener('error', () => {
      const err = video.error;
      if (err) {
        console.error('Video error:', err.code, err.message);
      }
    });
  }

  // ── Fetch helper ─────────────────────────────────────────────────────

  async _fetchJson(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
  }
}

// ── CatraClient (WebSocket) ────────────────────────────────────────────────

class CatraClient {
  /**
   * @param {App} app - Reference to the App controller
   */
  constructor(app) {
    /** @type {App} */
    this.app = app;
    /** @type {WebSocket|null} */
    this.ws = null;
    this.reconnectDelay = 1000;
    this.maxReconnectDelay = 30000;
    this.reconnectTimer = null;

    /** Current player state (from last 'state' message). */
    this.state = {};

    /** Whether the user is currently dragging the seek bar. */
    this.seeking = false;

    /** Last known position (updated by 'position' messages). */
    this.currentPosition = 0;

    /** Last known duration. */
    this.currentDuration = 0;

    /** Current active profile name. */
    this._activeProfile = 'original';

    /** Current stream URL (to detect changes for src reassignment). */
    this._currentStreamUrl = null;

    this._bindControls();
    this.connect();
  }

  // ── WebSocket lifecycle ──────────────────────────────────────────────

  connect() {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${protocol}//${location.host}/ws`;

    try {
      this.ws = new WebSocket(url);
    } catch {
      this._scheduleReconnect();
      return;
    }

    this.ws.onopen = () => {
      this.reconnectDelay = 1000;
      this._setConnectionStatus(true);
    };

    this.ws.onclose = () => {
      this._setConnectionStatus(false);
      this._scheduleReconnect();
    };

    this.ws.onerror = () => {
      // onclose will fire after onerror — reconnect handled there
    };

    this.ws.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data);
        this._handleMessage(msg);
      } catch {
        // Ignore malformed messages
      }
    };
  }

  _scheduleReconnect() {
    if (this.reconnectTimer !== null) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect();
    }, this.reconnectDelay);
    this.reconnectDelay = Math.min(this.reconnectDelay * 2, this.maxReconnectDelay);
  }

  /**
   * Send a command object through the WebSocket.
   * @param {object} command
   */
  send(command) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(command));
    }
  }

  /**
   * Toggles the DLNA device picker: discovers devices via REST and casts
   * the current episode to the selected one (`castTo` command).
   */
  async _toggleCastDeviceList() {
    const list = dom.castDeviceList;
    if (list.style.display !== 'none') {
      list.style.display = 'none';
      return;
    }

    list.innerHTML = '<div class="cast-device-item">Buscando dispositivos...</div>';
    list.style.display = '';

    try {
      const devices = await this.app._fetchJson('/api/cast/devices');
      if (!Array.isArray(devices) || devices.length === 0) {
        list.innerHTML = '<div class="cast-device-item">Nenhum dispositivo encontrado</div>';
        return;
      }

      list.innerHTML = '';
      for (const d of devices) {
        const btn = document.createElement('button');
        btn.className = 'cast-device-item';
        btn.textContent = d.friendlyName || d.udn;
        btn.addEventListener('click', () => {
          this.send({ type: 'castTo', deviceUdn: d.udn });
          list.style.display = 'none';
        });
        list.appendChild(btn);
      }
    } catch (err) {
      console.error('Failed to discover cast devices:', err);
      list.innerHTML = '<div class="cast-device-item">Erro ao buscar dispositivos</div>';
    }
  }

  // ── Message dispatch ─────────────────────────────────────────────────

  _handleMessage(msg) {
    switch (msg.type) {
      case 'state':
        this._applyFullState(msg.data);
        break;
      case 'position':
        this._updatePosition(msg.position, msg.duration);
        break;
      case 'command':
        this._handleRelayCommand(msg.command);
        break;
      case 'queue':
        this._renderQueue(msg.items);
        break;
    }
  }

  // ── Relay command from another client (remote control) ───────────────

  _handleRelayCommand(cmd) {
    if (!cmd) return;
    const video = dom.videoPlayer;

    switch (cmd.type) {
      case 'play':
        video.play().catch(() => {});
        break;
      case 'pause':
        video.pause();
        break;
      case 'seek':
        if (Number.isFinite(cmd.position)) {
          video.currentTime = cmd.position;
        }
        break;
      case 'volume':
        if (Number.isFinite(cmd.level)) {
          video.volume = Math.max(0, Math.min(100, cmd.level)) / 100;
          dom.volumeBar.value = cmd.level;
          dom.volumeValue.textContent = String(cmd.level);
          this._updateVolumeIcon(cmd.level);
        }
        break;
      case 'switchProfile':
        // Handled by subsequent state update from server
        break;
    }
  }

  // ── Full state application ───────────────────────────────────────────

  _applyFullState(data) {
    if (!data) return;
    this.state = data;

    // Title
    dom.playerTitle.textContent = data.title || 'Nenhum vídeo em reprodução';

    // Thumbnail: fallback art for when there is NO active browser stream.
    // Never re-show it while streaming, otherwise the still image stacks with
    // the <video> element (old/episode frame appearing alongside the video).
    const hasBrowserStream = data.mode === 'browser' && !!data.streamUrl;
    if (data.thumbnailUrl && !hasBrowserStream) {
      dom.thumbnail.src = data.thumbnailUrl;
      dom.thumbnail.alt = data.title || '';
      dom.thumbnailContainer.style.display = '';
    } else {
      dom.thumbnailContainer.style.display = 'none';
    }

    // Position & duration
    this.currentPosition = data.position || 0;
    this.currentDuration = data.duration || 0;
    dom.seekBar.max = this.currentDuration || 100;
    if (!this.seeking) {
      dom.seekBar.value = this.currentPosition;
    }
    dom.totalTime.textContent = formatTime(this.currentDuration);
    if (!this.seeking) {
      dom.currentTime.textContent = formatTime(this.currentPosition);
    }

    // Volume
    const vol = data.volume ?? 100;
    dom.volumeBar.value = vol;
    dom.volumeValue.textContent = String(vol);
    this._updateVolumeIcon(vol);

    // Play/Pause button
    dom.btnPlayPause.innerHTML = (data.isPlaying && !data.isPaused) ? FA_ICONS.pause : FA_ICONS.play;

    // Next / Previous
    dom.btnNext.disabled = !data.hasNextEpisode;
    dom.btnPrev.disabled = !data.hasPreviousEpisode;

    // Skip Intro
    if (data.canSkipIntro && data.skipIntroSec > 0) {
      dom.btnSkipIntro.disabled = false;
      dom.btnSkipIntro.textContent = `Pular Abertura (+${formatTime(data.skipIntroSec)})`;
    } else {
      dom.btnSkipIntro.disabled = true;
      dom.btnSkipIntro.textContent = 'Pular Abertura';
    }

    // Device + Profile
    if (data.castDeviceName) {
      dom.deviceName.innerHTML = `${FA_ICONS.tv} ${escapeHtml(data.castDeviceName)}`;
    } else {
      dom.deviceName.textContent = '';
    }
    dom.btnStopCast.style.display = data.castDeviceName ? '' : 'none';
    dom.profileLabel.innerHTML = renderProfileLabel(data.profileLabel);

    // Track active profile
    if (data.activeProfile) {
      this._activeProfile = data.activeProfile;
      dom.profileSelect.value = data.activeProfile;
    }

    // HEVC fallback: if browser can't play HEVC and active profile isn't original
    if (!this.app._hevcSupported && data.activeProfile && data.activeProfile !== 'original') {
      console.warn('HEVC not supported — requesting original profile');
      this.send({ type: 'switchProfile', profile: 'original' });
      return; // State will be re-sent with original profile
    }

    // ── Transmission banner on home ──
    const casting = !!data.castDeviceName;
    dom.castBanner.style.display = casting ? '' : 'none';
    if (casting) {
      dom.castBannerDetail.textContent =
        `${data.title || 'Transmissão ativa'} → ${data.castDeviceName}`;
    }

    // ── HTML5 Video: browser mode streaming ──
    if (data.mode === 'browser' && data.streamUrl) {
      this._applyStreamUrl(data);
    } else if (this._currentStreamUrl) {
      // Transmission is owned by the backend (DLNA): release the local
      // <video> so the panel does not keep playing while the TV streams.
      this._currentStreamUrl = null;
      dom.videoPlayer.pause();
      dom.videoPlayer.removeAttribute('src');
      dom.videoPlayer.load();
      dom.videoContainer.style.display = 'none';
    }

    // Populate available profiles
    if (Array.isArray(data.availableProfiles)) {
      this._renderProfileSelect(data.availableProfiles);
    }

    // Queue (from state)
    if (Array.isArray(data.queue)) {
      this._renderQueue(data.queue);
    }
  }

  // ── Stream URL assignment (browser mode) ─────────────────────────────

  _applyStreamUrl(data) {
    const video = dom.videoPlayer;
    const savedPos = video.currentTime || 0;

    // Only reassign src if streamUrl changed (new episode or profile switch)
    if (data.streamUrl !== this._currentStreamUrl) {
      this._currentStreamUrl = data.streamUrl;
      video.src = data.streamUrl;

      // Restore position on profile switch (keep current playback position)
      if (savedPos > 1 && data.position > 0) {
        video.addEventListener('loadedmetadata', () => {
          video.currentTime = data.position;
        }, { once: true });
      } else if (data.position > 0) {
        // Server-specified resume position
        video.addEventListener('loadedmetadata', () => {
          video.currentTime = data.position;
        }, { once: true });
      }

      // Auto-play (may fail on iOS without user gesture — that's OK)
      video.play().catch(() => {});

      // Hide thumbnail, show video
      dom.thumbnailContainer.style.display = 'none';
      dom.videoContainer.style.display = '';
    }
  }

  // ── Profile select rendering (from server data) ──────────────────────

  _renderProfileSelect(profiles) {
    const select = dom.profileSelect;
    const currentValue = select.value;
    select.innerHTML = '';

    for (const p of profiles) {
      const opt = document.createElement('option');
      opt.value = p.name;
      opt.textContent = p.label || p.name;
      select.appendChild(opt);
    }

    // Restore selection
    select.value = this._activeProfile || currentValue;
  }

  // ── Position update (lightweight, frequent) ──────────────────────────

  _updatePosition(position, duration) {
    if (!Number.isFinite(position)) return;
    this.currentPosition = position;
    if (Number.isFinite(duration) && duration > 0) {
      this.currentDuration = duration;
      dom.seekBar.max = duration;
      dom.totalTime.textContent = formatTime(duration);
    }

    if (!this.seeking) {
      dom.seekBar.value = position;
      dom.currentTime.textContent = formatTime(position);
    }
  }

  // ── Queue rendering (clickable cards → navigate to episode) ──────────

  _renderQueue(items) {
    if (!Array.isArray(items) || items.length === 0) {
      dom.queueList.innerHTML = '';
      dom.queueSection.style.display = 'none';
      return;
    }

    dom.queueSection.style.display = '';
    const fragment = document.createDocumentFragment();

    for (const item of items) {
      const card = document.createElement('div');
      card.className = 'queue-card' + (item.isCurrent ? ' queue-card--current' : '');
      card.dataset.nav = `#/play/${item.id || item.episodeId}`;

      const title = document.createElement('span');
      title.className = 'queue-card__title';
      title.textContent = item.title || '(sem título)';

      const dur = document.createElement('span');
      dur.className = 'queue-card__duration';
      dur.textContent = formatTime(item.durationSec || 0);

      card.appendChild(title);
      card.appendChild(dur);
      fragment.appendChild(card);
    }

    dom.queueList.innerHTML = '';
    dom.queueList.appendChild(fragment);

    // Bind click delegation for queue navigation
    if (!dom.queueList.dataset.bound) {
      dom.queueList.dataset.bound = '1';
      dom.queueList.addEventListener('click', (e) => {
        const card = e.target.closest('[data-nav]');
        if (card) {
          location.hash = card.dataset.nav;
        }
      });
    }
  }

  // ── Connection status ────────────────────────────────────────────────

  _setConnectionStatus(connected) {
    if (connected) {
      dom.connectionStatus.className = 'connected';
      dom.connectionStatus.querySelector('.text').textContent = 'Conectado';
    } else {
      dom.connectionStatus.className = 'disconnected';
      dom.connectionStatus.querySelector('.text').textContent = 'Reconectando...';
    }
  }

  // ── Volume icon helper ───────────────────────────────────────────────

  _updateVolumeIcon(level) {
    if (level === 0) {
      dom.volumeIcon.innerHTML = FA_ICONS.volumeMuted;
    } else if (level < 50) {
      dom.volumeIcon.innerHTML = FA_ICONS.volumeLow;
    } else {
      dom.volumeIcon.innerHTML = FA_ICONS.volumeHigh;
    }
  }

  // ── Control bindings (transport → HTML5 video + WebSocket) ───────────

  _bindControls() {
    const video = dom.videoPlayer;

    // ── Play / Pause toggle (controls both video element and remote) ──
    dom.btnPlayPause.addEventListener('click', () => {
      if (this._hasBrowserStream()) {
        if (video.paused) {
          video.play().catch(() => {});
        } else {
          video.pause();
        }
      }
      // Always send to server for remote clients
      const { isPlaying, isPaused } = this.state;
      if (isPlaying && !isPaused) {
        this.send({ type: 'pause' });
      } else {
        this.send({ type: 'play' });
      }
    });

    // ── Forward (+10s) ──
    dom.btnForward.addEventListener('click', () => {
      if (this._hasBrowserStream()) {
        video.currentTime = Math.min(video.duration || Infinity, video.currentTime + 10);
      }
      this.send({ type: 'seek', position: this.currentPosition + 10 });
    });

    // ── Back (-10s) ──
    dom.btnBack.addEventListener('click', () => {
      if (this._hasBrowserStream()) {
        video.currentTime = Math.max(0, video.currentTime - 10);
      }
      this.send({ type: 'seek', position: Math.max(0, this.currentPosition - 10) });
    });

    // ── Next / Previous ──
    dom.btnNext.addEventListener('click', () => {
      this.send({ type: 'nextEpisode' });
    });

    dom.btnPrev.addEventListener('click', () => {
      this.send({ type: 'previousEpisode' });
    });

    // ── Skip Intro ──
    dom.btnSkipIntro.addEventListener('click', () => {
      this.send({ type: 'skipIntro' });
    });

    // ── Cast (DLNA) ──
    dom.btnCast.addEventListener('click', () => {
      this._toggleCastDeviceList();
    });

    dom.btnStopCast.addEventListener('click', () => {
      this.send({ type: 'stopCasting' });
      dom.castDeviceList.style.display = 'none';
    });

    // ── Seek bar ──
    const onSeekStart = () => {
      this.seeking = true;
    };

    const onSeekEnd = () => {
      this.seeking = false;
      const pos = parseFloat(dom.seekBar.value) || 0;
      dom.currentTime.textContent = formatTime(pos);
      if (this._hasBrowserStream()) {
        video.currentTime = pos;
      }
      this.send({ type: 'seek', position: pos });
    };

    dom.seekBar.addEventListener('mousedown', onSeekStart);
    dom.seekBar.addEventListener('touchstart', onSeekStart, { passive: true });

    dom.seekBar.addEventListener('mouseup', onSeekEnd);
    dom.seekBar.addEventListener('touchend', onSeekEnd);

    dom.seekBar.addEventListener('input', () => {
      // Preview time while dragging
      dom.currentTime.textContent = formatTime(parseFloat(dom.seekBar.value) || 0);
    });

    // ── Volume bar (controls local video + sends to server) ──
    dom.volumeBar.addEventListener('input', () => {
      const val = parseInt(dom.volumeBar.value, 10);
      dom.volumeValue.textContent = String(val);
      this._updateVolumeIcon(val);
      if (this._hasBrowserStream()) {
        video.volume = val / 100;
      }
    });

    dom.volumeBar.addEventListener('change', () => {
      const val = parseInt(dom.volumeBar.value, 10);
      this.send({ type: 'volume', level: val });
    });
  }

  /**
   * Check if the current state has an active browser-mode stream.
   * @returns {boolean}
   */
  _hasBrowserStream() {
    return this.state.mode === 'browser' && !!this._currentStreamUrl;
  }
}

// ── Bootstrap ──────────────────────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', () => {
  window.app = new App();
  window.app.init();
});
