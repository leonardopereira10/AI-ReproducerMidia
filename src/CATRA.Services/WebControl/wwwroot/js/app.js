// ==========================================================================
// CATRA — Painel de Controle: WebSocket Client + Controles
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

// ── DOM references (cached once) ───────────────────────────────────────────

const dom = {
  connectionStatus: document.getElementById('connection-status'),
  videoTitle:       document.getElementById('video-title'),
  videoSeries:      document.getElementById('video-series'),
  thumbnailContainer: document.getElementById('thumbnail-container'),
  thumbnail:        document.getElementById('thumbnail'),
  currentTime:      document.getElementById('current-time'),
  seekBar:          document.getElementById('seek-bar'),
  totalTime:        document.getElementById('total-time'),
  btnPrev:          document.getElementById('btn-prev'),
  btnBack:          document.getElementById('btn-back'),
  btnPlayPause:     document.getElementById('btn-play-pause'),
  btnForward:       document.getElementById('btn-forward'),
  btnNext:          document.getElementById('btn-next'),
  btnSkipIntro:     document.getElementById('btn-skip-intro'),
  volumeIcon:       document.getElementById('volume-icon'),
  volumeBar:        document.getElementById('volume-bar'),
  volumeValue:      document.getElementById('volume-value'),
  deviceName:       document.getElementById('device-name'),
  profileLabel:     document.getElementById('profile-label'),
  queueSection:     document.getElementById('queue-section'),
  queueList:        document.getElementById('queue-list'),
};

// ── CatraClient ────────────────────────────────────────────────────────────

class CatraClient {
  constructor() {
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

    this._bindControls();
    this.connect();
  }

  // ── WebSocket lifecycle ────────────────────────────────────────────────

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

  // ── Message dispatch ───────────────────────────────────────────────────

  _handleMessage(msg) {
    switch (msg.type) {
      case 'state':
        this._applyFullState(msg.data);
        break;
      case 'position':
        this._updatePosition(msg.position, msg.duration);
        break;
      case 'queue':
        this._renderQueue(msg.items);
        break;
    }
  }

  // ── Full state application ─────────────────────────────────────────────

  _applyFullState(data) {
    if (!data) return;
    this.state = data;

    // Title & series
    dom.videoTitle.textContent = data.title || 'Nenhum vídeo em reprodução';
    dom.videoSeries.textContent = '';

    // Thumbnail
    if (data.thumbnailUrl) {
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
    dom.btnPlayPause.textContent = (data.isPlaying && !data.isPaused) ? '⏸' : '▶';

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
    dom.deviceName.textContent = data.castDeviceName || '';
    dom.profileLabel.textContent = data.profileLabel || '';

    // Queue (from state)
    if (Array.isArray(data.queue)) {
      this._renderQueue(data.queue);
    }
  }

  // ── Position update (lightweight, frequent) ────────────────────────────

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

  // ── Queue rendering ────────────────────────────────────────────────────

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
  }

  // ── Connection status ──────────────────────────────────────────────────

  _setConnectionStatus(connected) {
    if (connected) {
      dom.connectionStatus.className = 'connected';
      dom.connectionStatus.querySelector('.text').textContent = 'Conectado';
    } else {
      dom.connectionStatus.className = 'disconnected';
      dom.connectionStatus.querySelector('.text').textContent = 'Reconectando...';
    }
  }

  // ── Volume icon helper ─────────────────────────────────────────────────

  _updateVolumeIcon(level) {
    if (level === 0) {
      dom.volumeIcon.textContent = '🔇';
    } else if (level < 50) {
      dom.volumeIcon.textContent = '🔉';
    } else {
      dom.volumeIcon.textContent = '🔊';
    }
  }

  // ── Control bindings ───────────────────────────────────────────────────

  _bindControls() {
    // ── Play / Pause toggle ──
    dom.btnPlayPause.addEventListener('click', () => {
      const { isPlaying, isPaused } = this.state;
      if (isPlaying && !isPaused) {
        this.send({ type: 'pause' });
      } else {
        this.send({ type: 'play' });
      }
    });

    // ── Forward (+10s) ──
    dom.btnForward.addEventListener('click', () => {
      this.send({ type: 'seek', position: this.currentPosition + 10 });
    });

    // ── Back (-10s) ──
    dom.btnBack.addEventListener('click', () => {
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

    // ── Seek bar ──
    const onSeekStart = () => {
      this.seeking = true;
    };

    const onSeekEnd = () => {
      this.seeking = false;
      const pos = parseFloat(dom.seekBar.value) || 0;
      dom.currentTime.textContent = formatTime(pos);
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

    // ── Volume bar ──
    dom.volumeBar.addEventListener('input', () => {
      const val = parseInt(dom.volumeBar.value, 10);
      dom.volumeValue.textContent = String(val);
      this._updateVolumeIcon(val);
    });

    dom.volumeBar.addEventListener('change', () => {
      const val = parseInt(dom.volumeBar.value, 10);
      this.send({ type: 'volume', level: val });
    });
  }
}

// ── Bootstrap ──────────────────────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', () => {
  window.catraClient = new CatraClient();
});
