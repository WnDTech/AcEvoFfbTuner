(function () {
  'use strict';

  let eventSource = null;
  let currentData = null;
  let rawInterval = null;
  let rawStructInterval = null;

  const WHEEL_KEYS = ['Mz', 'Fx', 'Fy', 'SlipRatio', 'SlipAngle', 'WheelLoad', 'WheelSlip',
    'WheelsPressure', 'WheelAngularSpeed', 'TyreWear', 'TyreDirtyLevel',
    'TyreCoreTemperature', 'CamberRad', 'SuspensionTravel', 'BrakeTemp',
    'TyreTempI', 'TyreTempM', 'TyreTempO', 'TyreTemp', 'BrakeTorque', 'PadLife', 'DiscLife',
    'SuspensionDamage'];

  const DASHBOARD_KEYS = [
    { key: 'SpeedKmh', label: 'Speed', unit: 'km/h', precision: 0, color: 'green' },
    { key: 'Gas', label: 'Throttle', unit: '%', precision: 1, color: 'green', scale: 100 },
    { key: 'Brake', label: 'Brake', unit: '%', precision: 1, color: 'red', scale: 100 },
    { key: 'SteerAngle', label: 'Steering', unit: 'rad', precision: 3, color: 'cyan' },
    { key: 'Rpms', label: 'RPM', precision: 0, color: 'yellow' },
    { key: 'Gear', label: 'Gear', precision: 0, color: 'purple' },
    { key: 'FinalFf', label: 'Final FF', unit: 'Nm', precision: 2, color: 'orange' },
    { key: 'KerbVibration', label: 'Kerb Vib', precision: 3, color: 'cyan' },
    { key: 'SlipVibrations', label: 'Slip Vib', precision: 3, color: 'yellow' },
    { key: 'RoadVibrations', label: 'Road Vib', precision: 3, color: 'green' },
    { key: 'AbsVibrations', label: 'ABS Vib', precision: 3, color: 'red' },
  ];

  const WHEEL_LABELS = ['FL', 'FR', 'RL', 'RR'];
  const WHEEL_FIELDS = [
    { key: 'WheelLoad', unit: 'N', precision: 0 },
    { key: 'Mz', unit: 'Nm', precision: 2 },
    { key: 'Fx', unit: 'N', precision: 1 },
    { key: 'Fy', unit: 'N', precision: 1 },
    { key: 'SlipRatio', precision: 3 },
    { key: 'SlipAngle', unit: 'rad', precision: 3 },
    { key: 'SuspensionTravel', unit: 'm', precision: 4 },
    { key: 'TyreCoreTemperature', unit: '°C', precision: 0 },
    { key: 'BrakeTemp', unit: '°C', precision: 0 },
    { key: 'TyreWear', precision: 1 },
  ];

  const dom = {
    gameSelect: () => document.getElementById('gameSelect'),
    connectBtn: () => document.getElementById('connectBtn'),
    disconnectBtn: () => document.getElementById('disconnectBtn'),
    statusBadge: () => document.getElementById('statusBadge'),
    readStats: () => document.getElementById('readStats'),
    dashboardGauges: () => document.getElementById('dashboardGauges'),
    physicsFields: () => document.getElementById('physicsFields'),
    graphicsFields: () => document.getElementById('graphicsFields'),
    staticFields: () => document.getElementById('staticFields'),
    rawJson: () => document.getElementById('rawJson'),
    readOnceBtn: () => document.getElementById('readOnceBtn'),
    autoRefreshRaw: () => document.getElementById('autoRefreshRaw'),
    rawStructFields: () => document.getElementById('rawStructFields'),
    rawStructInfo: () => document.getElementById('rawStructInfo'),
    autoRefreshRawStruct: () => document.getElementById('autoRefreshRawStruct'),
    coverageSummary: () => document.getElementById('coverageSummary'),
    coverageDetail: () => document.getElementById('coverageDetail'),
    coverageGameLabel: () => document.getElementById('coverageGameLabel'),
    refreshCoverageBtn: () => document.getElementById('refreshCoverageBtn'),
    opponentsGameLabel: () => document.getElementById('opponentsGameLabel'),
    refreshOpponentsBtn: () => document.getElementById('refreshOpponentsBtn'),
    opponentsInfo: () => document.getElementById('opponentsInfo'),
    opponentsTable: () => document.getElementById('opponentsTable'),
    opponentsTableHead: () => document.getElementById('opponentsTableHead'),
    opponentsTableBody: () => document.getElementById('opponentsTableBody'),
    opponentsUnavailable: () => document.getElementById('opponentsUnavailable'),
    logCoverageBtn: () => document.getElementById('logCoverageBtn'),
    logCoverageStatus: () => document.getElementById('logCoverageStatus'),
    logRawBtn: () => document.getElementById('logRawBtn'),
    logRawStatus: () => document.getElementById('logRawStatus'),
    logMappedBtn: () => document.getElementById('logMappedBtn'),
    logMappedStatus: () => document.getElementById('logMappedStatus'),
  };

  const GAME_NAMES = {
    acevo: 'Assetto Corsa EVO',
    assettocorsa: 'Assetto Corsa (AC1)',
    assettocorsac: 'Assetto Corsa Competizione',
    raccoroom: 'RaceRoom Racing Experience',
    lemansultimate: 'Le Mans Ultimate',
    rfactor2: 'rFactor 2',
  };

  let connectedGame = null;

  function valColor(val, key) {
    if (val == null) return '';
    const abs = Math.abs(val);
    if (key === 'Gas' || key === 'Brake' || key === 'Clutch') {
      if (val > 0.01) return 'val-yellow';
      return 'val-green';
    }
    if (key === 'SpeedKmh') {
      if (val > 200) return 'val-red';
      if (val > 100) return 'val-yellow';
      return 'val-green';
    }
    if (key === 'Rpms') {
      if (val > 7000) return 'val-red';
      if (val > 5000) return 'val-yellow';
      return 'val-green';
    }
    if (key === 'SteerAngle') {
      if (abs > 0.3) return 'val-red';
      if (abs > 0.1) return 'val-yellow';
      return 'val-green';
    }
    if (key === 'FinalFf') {
      if (abs > 5) return 'val-red';
      if (abs > 2) return 'val-yellow';
      return 'val-green';
    }
    if (key.includes('Temp') || key.includes('temperature')) {
      if (val > 100) return 'val-red';
      if (val > 70) return 'val-yellow';
      return 'val-green';
    }
    if (key === 'WheelSlip' || key === 'SlipRatio') {
      if (abs > 0.15) return 'val-red';
      if (abs > 0.05) return 'val-yellow';
      return 'val-green';
    }
    if (key === 'WheelLoad' || key === 'tireLoad') {
      if (val > 8000) return 'val-red';
      if (val > 4000) return 'val-yellow';
      return 'val-green';
    }
    return '';
  }

  function fmt(val, precision) {
    if (val == null) return '—';
    if (typeof val === 'number') {
      if (precision === 0) return Math.round(val).toString();
      return val.toFixed(precision);
    }
    return String(val);
  }

  // --- Connection ---
  async function connect() {
    const game = dom.gameSelect().value;
    connectedGame = game;
    dom.connectBtn().disabled = true;
    dom.statusBadge().textContent = 'Connecting...';
    dom.statusBadge().className = 'status-badge';

    try {
      const resp = await fetch(`/api/connect/${game}`, { method: 'POST' });
      const result = await resp.json();
      if (result.success) {
        dom.statusBadge().textContent = 'Connected';
        dom.statusBadge().className = 'status-badge connected';
        dom.disconnectBtn().disabled = false;
        dom.gameSelect().disabled = true;
        startSSE();
        loadCoverage(game);
      } else {
        dom.statusBadge().textContent = result.message || 'Connection failed';
        dom.statusBadge().className = 'status-badge error';
        dom.connectBtn().disabled = false;
      }
    } catch (err) {
      dom.statusBadge().textContent = 'Connection error';
      dom.statusBadge().className = 'status-badge error';
      dom.connectBtn().disabled = false;
    }
  }

  async function disconnect() {
    stopSSE();
    try { await fetch('/api/disconnect', { method: 'POST' }); } catch (_) { }
    dom.statusBadge().textContent = 'Disconnected';
    dom.statusBadge().className = 'status-badge disconnected';
    dom.connectBtn().disabled = false;
    dom.disconnectBtn().disabled = true;
    dom.gameSelect().disabled = false;
    dom.readStats().textContent = '';
    currentData = null;
    dom.rawStructFields().innerHTML = '';
    dom.rawStructInfo().textContent = '';
    dom.coverageSummary().innerHTML = '';
    dom.coverageDetail().innerHTML = '';
    dom.coverageGameLabel().textContent = '';
    rawFieldOffsets = {};
    connectedGame = null;
  }

  function startSSE() {
    if (eventSource) eventSource.close();
    eventSource = new EventSource('/api/stream');

    eventSource.onmessage = function (e) {
      try {
        const data = JSON.parse(e.data);
        currentData = data;
        const ri = data._readIndex || 0;
        dom.readStats().textContent = `#${ri} | ${data._timestamp ? new Date(data._timestamp).toLocaleTimeString() : ''}`;
        updateDashboard(data);
      } catch (_) { }
    };

    eventSource.onerror = function () {
      if (eventSource) { eventSource.close(); eventSource = null; }
      setTimeout(() => {
        if (!dom.disconnectBtn().disabled) startSSE();
      }, 2000);
    };
  }

  function stopSSE() {
    if (eventSource) { eventSource.close(); eventSource = null; }
    if (rawInterval) { clearInterval(rawInterval); rawInterval = null; }
    if (rawStructInterval) { clearInterval(rawStructInterval); rawStructInterval = null; }
  }

  // --- Dashboard ---
  function updateDashboard(data) {
    const grid = dom.dashboardGauges();
    if (!grid.hasChildNodes()) renderDashboardGauges(grid);

    for (const def of DASHBOARD_KEYS) {
      const raw = data[def.key];
      const val = raw != null && def.scale ? raw * def.scale : raw;
      const el = grid.querySelector(`[data-key="${def.key}"]`);
      if (!el) continue;
      const valEl = el.querySelector('.value');
      if (valEl) {
        const display = fmt(val, def.precision);
        if (valEl.textContent !== display) {
          valEl.textContent = display;
          valEl.className = 'value ' + valColor(raw, def.key);
          el.classList.remove('pulse');
          void el.offsetWidth;
          el.classList.add('pulse');
        }
      }
    }

    for (let wi = 0; wi < 4; wi++) {
      const col = grid.querySelector(`.wheel-col[data-wheel="${wi}"]`);
      if (!col) continue;
      for (const wf of WHEEL_FIELDS) {
        const cell = col.querySelector(`[data-field="${wf.key}_${wi}"]`);
        if (!cell) continue;
        const raw = data[`${wf.key}_${wi}`];
        const valEl = cell.querySelector('.wf-value');
        if (valEl) {
          const display = fmt(raw, wf.precision);
          if (valEl.textContent !== display) {
            valEl.textContent = display;
            valEl.className = 'wf-value ' + valColor(raw, wf.key);
          }
        }
      }
    }

    updateFieldGrid(dom.physicsFields(), data, k =>
      !k.startsWith('_') && !k.startsWith('G_') && !k.startsWith('S_'));

    const game = connectedGame || dom.gameSelect().value;
    const hasGraphicsData = Object.keys(data).some(k => k.startsWith('G_'));
    const hasStaticData = Object.keys(data).some(k => k.startsWith('S_'));
    if (!hasGraphicsData) {
      dom.graphicsFields().innerHTML = '<p style="color: var(--text-muted); padding: 20px; text-align: center;">Graphics data not available for this game. View the <strong>Raw Struct</strong> tab for game-native data.</p>';
      dom.graphicsFields()._rendered = true;
    } else {
      updateFieldGrid(dom.graphicsFields(), data, k => k.startsWith('G_'));
    }
    if (!hasStaticData) {
      dom.staticFields().innerHTML = '<p style="color: var(--text-muted); padding: 20px; text-align: center;">Static data not available for this game. View the <strong>Raw Struct</strong> tab for game-native data.</p>';
      dom.staticFields()._rendered = true;
    } else {
      updateFieldGrid(dom.staticFields(), data, k => k.startsWith('S_'));
    }
  }

  function renderDashboardGauges(grid) {
    for (const def of DASHBOARD_KEYS) {
      const card = document.createElement('div');
      card.className = 'gauge-card';
      card.dataset.key = def.key;
      card.innerHTML = `
        <div class="label">${def.label}</div>
        <div class="value">—</div>
        <div class="sub">${def.unit || ''}</div>
      `;
      grid.appendChild(card);
    }

    const wheelSection = document.createElement('div');
    wheelSection.style.cssText = 'grid-column: 1 / -1; margin-top: 8px;';
    const wheelTitle = document.createElement('h3');
    wheelTitle.style.cssText = 'font-size: 0.85rem; color: var(--text-secondary); margin-bottom: 8px; text-transform: uppercase; letter-spacing: 0.05em;';
    wheelTitle.textContent = 'Per-Wheel Forces (Mapped)';
    wheelSection.appendChild(wheelTitle);

    const wheelRow = document.createElement('div');
    wheelRow.className = 'wheel-row';
    for (let wi = 0; wi < 4; wi++) {
      const col = document.createElement('div');
      col.className = 'wheel-col';
      col.dataset.wheel = wi;
      let fieldsHtml = '';
      for (const wf of WHEEL_FIELDS) {
        fieldsHtml += `<div class="wheel-field" data-field="${wf.key}_${wi}">
          <span class="wf-label">${wf.key.replace('Wheel', '').replace('Tyre', 'Tyre ')}</span>
          <span class="wf-value">—</span>
        </div>`;
      }
      col.innerHTML = `<div class="wheel-label">${WHEEL_LABELS[wi]}</div>${fieldsHtml}`;
      wheelRow.appendChild(col);
    }
    wheelSection.appendChild(wheelRow);
    grid.appendChild(wheelSection);
  }

  // --- Field Grids ---
  function updateFieldGrid(container, data, filter) {
    if (!container) return;
    const keys = Object.keys(data).filter(filter).sort((a, b) => {
      const an = a.replace(/^\d+_/, '');
      const bn = b.replace(/^\d+_/, '');
      if (an.includes('_') && !bn.includes('_')) return 1;
      if (!an.includes('_') && bn.includes('_')) return -1;
      return a.localeCompare(b);
    });

    if (!container._rendered) {
      container._rendered = true;
      container.innerHTML = '';
      const frag = document.createDocumentFragment();
      for (const key of keys) {
        const row = document.createElement('div');
        row.className = 'field-row';
        row.dataset.key = key;
        const nameSpan = document.createElement('span');
        nameSpan.className = 'fname';
        nameSpan.textContent = key;
        const valSpan = document.createElement('span');
        valSpan.className = 'fvalue';
        valSpan.textContent = fmtField(data[key]);
        row.appendChild(nameSpan);
        row.appendChild(valSpan);
        frag.appendChild(row);
      }
      container.appendChild(frag);
    } else {
      for (const key of keys) {
        const row = container.querySelector(`[data-key="${CSS.escape(key)}"]`);
        if (row) {
          const valSpan = row.querySelector('.fvalue');
          if (valSpan) {
            const display = fmtField(data[key]);
            if (valSpan.textContent !== display) {
              valSpan.textContent = display;
              valSpan.className = 'fvalue ' + valColorAll(data[key], key);
            }
          }
        }
      }
    }
  }

  function fmtField(val) {
    if (val === null || val === undefined) return '—';
    if (Array.isArray(val)) {
      return '[' + val.map(v => typeof v === 'number' ? v.toFixed(2) : v).join(', ') + ']';
    }
    if (typeof val === 'object') return JSON.stringify(val);
    if (typeof val === 'number') {
      if (Number.isInteger(val)) return val.toString();
      return val.toFixed(4);
    }
    return String(val);
  }

  function valColorAll(val, key) {
    if (typeof val !== 'number') return '';
    return valColor(val, key);
  }

  // === Raw Struct Tab ===
  let rawFieldOffsets = {};

  async function loadRawStruct() {
    const game = dom.gameSelect().value;
    try {
      // Fetch field metadata with offsets (cached after first call per game)
      if (Object.keys(rawFieldOffsets).length === 0) {
        const metaResp = await fetch(`/api/raw/${game}/fields`);
        const meta = await metaResp.json();
        for (const section of Object.values(meta)) {
          for (const f of section) {
            if (f.offset >= 0) {
              // Store both dotted and underscored forms for lookup
              rawFieldOffsets[f.name] = f;
              rawFieldOffsets[f.name.replace(/\./g, '_')] = f;
            }
          }
        }
      }

      const resp = await fetch(`/api/raw/${game}/values`);
      const data = await resp.json();
      if (data.success === false) {
        dom.rawStructInfo().textContent = data.message || 'Cannot read raw data';
        return;
      }
      const count = Object.keys(data).length;
      dom.rawStructInfo().textContent = `${count} native fields`;
      renderRawStructFields(data);
    } catch (err) {
      dom.rawStructInfo().textContent = 'Error: ' + err.message;
    }
  }

  function renderRawStructFields(data) {
    const container = dom.rawStructFields();
    const keys = Object.keys(data).sort((a, b) => a.localeCompare(b));

    if (!container._rendered) {
      container._rendered = true;
      container.innerHTML = '';
      const frag = document.createDocumentFragment();
      for (const key of keys) {
        const off = rawFieldOffsets[key];
        const offsetStr = off ? `@${off.offset} (${off.offsetHex})` : '';
        const row = document.createElement('div');
        row.className = 'field-row';
        row.dataset.key = key;
        row.innerHTML = `
          <span class="fname">${key}</span>
          <span class="fm-offset">${offsetStr}</span>
          <span class="fvalue">${fmtField(data[key])}</span>
        `;
        frag.appendChild(row);
      }
      container.appendChild(frag);
    } else {
      for (const key of keys) {
        const row = container.querySelector(`[data-key="${CSS.escape(key)}"]`);
        if (row) {
          const valSpan = row.querySelector('.fvalue');
          if (valSpan) {
            const display = fmtField(data[key]);
            if (valSpan.textContent !== display) {
              valSpan.textContent = display;
              valSpan.className = 'fvalue ' + valColorAll(data[key], key);
            }
          }
        }
      }
    }
  }

  // === Logging ===
  async function logCoverage(game) {
    const status = dom.logCoverageStatus();
    status.textContent = 'Logging...';
    status.className = 'log-status';
    try {
      const resp = await fetch(`/api/log/coverage/${game}`, { method: 'POST' });
      const result = await resp.json();
      if (result.success) {
        status.textContent = `Logged ${result.rawFieldCount} raw fields, ${result.mappedFieldCount} mapped by reader`;
        status.className = 'log-status success';
      } else {
        status.textContent = result.message || 'Log failed';
        status.className = 'log-status error';
      }
    } catch (err) {
      status.textContent = 'Error: ' + err.message;
      status.className = 'log-status error';
    }
  }

  async function logRawData(game) {
    const status = dom.logRawStatus();
    status.textContent = 'Logging...';
    status.className = 'log-status';
    try {
      const resp = await fetch(`/api/log/raw/${game}`, { method: 'POST' });
      const result = await resp.json();
      if (result.success) {
        status.textContent = `Logged ${result.fieldCount} native fields`;
        status.className = 'log-status success';
      } else {
        status.textContent = result.message || 'Log failed';
        status.className = 'log-status error';
      }
    } catch (err) {
      status.textContent = 'Error: ' + err.message;
      status.className = 'log-status error';
    }
  }

  async function logMappedData() {
    const status = dom.logMappedStatus();
    status.textContent = 'Logging...';
    status.className = 'log-status';
    try {
      const game = connectedGame || dom.gameSelect().value;
      const resp = await fetch(`/api/log/mapped/${game}`, { method: 'POST' });
      const result = await resp.json();
      if (result.success) {
        status.textContent = `Logged ${result.fieldCount} mapped fields`;
        status.className = 'log-status success';
      } else {
        status.textContent = result.message || 'Log failed';
        status.className = 'log-status error';
      }
    } catch (err) {
      status.textContent = 'Error: ' + err.message;
      status.className = 'log-status error';
    }
  }

  // === Coverage Tab ===
  async function loadCoverage(game) {
    dom.coverageGameLabel().textContent = GAME_NAMES[game] || game;
    try {
      const resp = await fetch(`/api/raw/${game}/coverage`);
      const data = await resp.json();
      renderCoverage(data);
    } catch (_) { }
  }

  function renderCoverage(data) {
    const summary = dom.coverageSummary();
    const detail = dom.coverageDetail();

    const analysisLabel = data.analysisSource === 'source-code-analysis'
      ? 'Source code analysis (reader source file)'
      : data.analysisSource === 'source-analysis+hashSetName-supplement'
      ? 'Source code analysis + verified known mappings'
      : data.analysisSource === 'marshal-deserialization'
      ? 'Marshal.PtrToStructure (all struct fields populated)'
      : 'Known mapping fallback';

    summary.innerHTML = `
      <div class="coverage-card">
        <div class="cvalue val-green">${data.totalUniversalStructFields}</div>
        <div class="clabel">Universal Struct Fields</div>
      </div>
      <div class="coverage-card">
        <div class="cvalue val-cyan">${data.mappedInMainApp}</div>
        <div class="clabel">Populated by Reader</div>
      </div>
      <div class="coverage-card">
        <div class="cvalue ${(data.unmappedPhysCount + data.unmappedGfxCount + data.unmappedStCount) > 0 ? 'val-orange' : 'val-green'}">${data.unmappedPhysCount + data.unmappedGfxCount + data.unmappedStCount}</div>
        <div class="clabel">Reader Does Not Set</div>
      </div>
      <div class="coverage-card">
        <div class="cvalue val-purple">${data.coverage}</div>
        <div class="clabel">Reader Coverage</div>
      </div>
      <div style="grid-column: 1 / -1; font-size: 0.75rem; color: var(--text-muted); margin-top: 4px;">
        Analysis: ${analysisLabel} |
        Raw native fields: ${data.totalRaw} |
        Physics: ${data.mappedPhysFields.length}/${data.totalUniversalStructFields - data.unmappedPhysCount - data.unmappedGfxCount - data.unmappedStCount + data.mappedPhysFields.length} fields set
        (${data.unmappedPhysCount} not set)
      </div>
    `;

    // Mapped physics fields
    let html = '';
    if (data.mappedPhysFields && data.mappedPhysFields.length > 0) {
      html += `<h4 style="color: var(--cyan); margin: 14px 0 6px; font-size: 0.85rem;">Physics — Reader Sets These Universal Fields:</h4>`;
      for (const f of data.mappedPhysFields) {
        html += `<div class="field-row"><span class="fname">${f}</span><span class="fvalue val-cyan">✓ mapped</span></div>`;
      }
    }
    if (data.mappedGfxFields && data.mappedGfxFields.length > 0) {
      html += `<h4 style="color: var(--cyan); margin: 14px 0 6px; font-size: 0.85rem;">Graphics — Reader Sets These Universal Fields:</h4>`;
      for (const f of data.mappedGfxFields) {
        html += `<div class="field-row"><span class="fname">${f}</span><span class="fvalue val-cyan">✓ mapped</span></div>`;
      }
    }
    if (data.mappedStFields && data.mappedStFields.length > 0) {
      html += `<h4 style="color: var(--cyan); margin: 14px 0 6px; font-size: 0.85rem;">Static — Reader Sets These Universal Fields:</h4>`;
      for (const f of data.mappedStFields) {
        html += `<div class="field-row"><span class="fname">${f}</span><span class="fvalue val-cyan">✓ mapped</span></div>`;
      }
    }

    // Raw native fields
    html += `<h4 style="color: var(--text-secondary); margin: 18px 0 6px; font-size: 0.85rem;">All Raw Native Fields (${data.totalRaw} total — for reference):</h4>`;
    if (data.rawNativeFields && data.rawNativeFields.length > 0) {
      for (const f of data.rawNativeFields) {
        const offsetStr = f.offset >= 0 ? `@${f.offset} (${f.offsetHex})` : '';
        html += `<div class="field-row">
          <span class="fname">${f.name}</span>
          <span class="fm-offset">${offsetStr}</span>
          <span class="fvalue">${f.type}${f.unit ? ' ' + f.unit : ''}</span>
        </div>`;
      }
    }

    detail.innerHTML = html;
  }

  // --- Raw JSON ---
  async function readOnce() {
    try {
      const resp = await fetch('/api/read-once');
      const data = await resp.json();
      dom.rawJson().textContent = JSON.stringify(data, null, 2);
    } catch (err) {
      dom.rawJson().textContent = 'Error: ' + err.message;
    }
  }

  // === Opponents Tab ===
  async function loadOpponents() {
    const game = connectedGame || dom.gameSelect().value;
    const label = dom.opponentsGameLabel();
    const table = dom.opponentsTable();
    const tHead = dom.opponentsTableHead();
    const tBody = dom.opponentsTableBody();
    const unavailable = dom.opponentsUnavailable();
    const info = dom.opponentsInfo();

    label.textContent = 'Loading...';
    info.textContent = '';
    unavailable.style.display = 'none';

    try {
      const resp = await fetch(`/api/opponents/${game}`);
      const data = await resp.json();
      if (data.success === false) {
        label.textContent = GAME_NAMES[game] || game;
        unavailable.style.display = 'block';
        unavailable.textContent = data.message || 'No opponent data available';
        return;
      }
      label.textContent = data.game + ` — ${data.totalOpponents || data.activeCars || 0} cars`;
      info.textContent = `Loaded`;

      // Render strategy info panel for ACC
      renderAccStrategyInfo(data);

      // Render car positions table
      renderOpponentsTable(data);
    } catch (err) {
      label.textContent = 'Error';
      unavailable.style.display = 'block';
      unavailable.textContent = 'Error: ' + err.message;
    }
  }

  function renderAccStrategyInfo(data) {
    const wrap = document.querySelector('.opponents-table-wrap');
    if (!wrap) return;
    let existing = document.getElementById('accStrategyPanel');
    if (existing) existing.remove();

    // Only show for ACC data with strategy fields
    if (data.tyreCompound === undefined && data.playerCarId === undefined) return;

    const panel = document.createElement('div');
    panel.id = 'accStrategyPanel';
    panel.style.cssText = 'display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px;';

    const info = (label, val, cls) =>
      `<div class="strategy-item" style="background:var(--bg-card);border:1px solid var(--border);border-radius:6px;padding:6px 12px;font-size:0.82rem;white-space:nowrap;">
        <span style="color:var(--text-muted);font-size:0.7rem;text-transform:uppercase;letter-spacing:0.03em;">${label}</span>
        <span class="${cls || ''}" style="margin-left:6px;font-weight:600;font-variant-numeric:tabular-nums;">${val || '—'}</span>
      </div>`;

    let html = '';
    if (data.tyreCompound) html += info('Tyres', data.tyreCompound, 'val-green');
    if (data.fuelEstLaps !== undefined) html += info('Fuel Laps', data.fuelEstLaps.toFixed(1), 'val-cyan');
    if (data.usedFuel !== undefined) html += info('Used', data.usedFuel.toFixed(1) + 'L', 'val-yellow');
    if (data.fuelPerLap !== undefined) html += info('Fuel/Lap', data.fuelPerLap.toFixed(2) + 'L', '');
    if (data.penaltyTime && data.penaltyTime > 0) html += info('Penalty', data.penaltyTime + 's', 'val-red');
    if (data.gapAhead !== undefined && data.gapAhead !== null) html += info('Gap+', data.gapAhead.toFixed(3), '');
    if (data.gapBehind !== undefined && data.gapBehind !== null) html += info('Gap-', data.gapBehind.toFixed(3), '');
    if (data.rainTyres) html += info('Rain', 'YES', 'val-red');
    if (data.windSpeed) html += info('Wind', data.windSpeed.toFixed(1) + 'm/s', '');
    if (data.clock) html += info('Clock', data.clock.toFixed(0) + 's', '');
    if (data.sessionTimeLeft) html += info('Time Left', formatTime(data.sessionTimeLeft), '');
    if (data.trackStatus) html += info('Track', data.trackStatus, '');

    if (html) panel.innerHTML = html;
    wrap.parentNode.insertBefore(panel, wrap);
  }

  function formatTime(seconds) {
    if (!seconds || seconds <= 0) return '—';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  function renderOpponentsTable(data) {
    const entries = data.entries || [];
    const tHead = dom.opponentsTableHead();
    const tBody = dom.opponentsTableBody();

    if (!entries || entries.length === 0) {
      dom.opponentsUnavailable().style.display = 'block';
      dom.opponentsUnavailable().textContent = 'No opponent entries found.';
      tHead.innerHTML = '';
      tBody.innerHTML = '';
      return;
    }

    // Determine columns from first entry
    const COL_DEFS = {
      place:       { label: 'P', align: 'center', width: '32px' },
      name:        { label: 'Driver / Vehicle' },
      driver:      { label: 'Driver' },
      surname:     { label: 'Surname' },
      carModel:    { label: 'Car' },
      team:        { label: 'Team' },
      carNumber:   { label: '#' },
      laps:        { label: 'Laps', align: 'right' },
      bestLapTime: { label: 'Best', align: 'right', fmt: 'time' },
      lastLapTime: { label: 'Last', align: 'right', fmt: 'time' },
      lapTimeCurrent: { label: 'Current', align: 'right', fmt: 'time' },
      gapFront:    { label: 'Gap+', align: 'right', fmt: 'time' },
      gapLeader:   { label: 'Gap L', align: 'right', fmt: 'time' },
      speedMs:     { label: 'Speed', align: 'right', fmt: 'speed' },
      sector:      { label: 'S', align: 'center', width: '28px' },
      sector1:     { label: 'S1', align: 'right', fmt: 'time' },
      sector2:     { label: 'S2', align: 'right', fmt: 'time' },
      inPits:      { label: 'Pits', fmt: 'pit' },
      pitStops:    { label: 'Pit#', align: 'right' },
      tireFront:   { label: 'TireF', width: '60px' },
      tireRear:    { label: 'TireR', width: '60px' },
      drs:         { label: 'DRS' },
      ptp:         { label: 'P2P' },
      fuel:        { label: 'Fuel' },
      virtualEnergy: { label: 'ERS', fmt: 'energy' },
      finished:    { label: 'Status' },
      vehicleClass:{ label: 'Class' },
      control:     { label: 'Ctrl' },
      penalties:   { label: 'Pen#' },
      underYellow: { label: 'Slow' },
      isPlayer:    { label: 'P' },
      driverName:  { label: 'Driver' },
      driverSurname: { label: 'Surname' },
      shortName:   { label: 'Short' },
      carModel:    { label: 'Car' },
      carClass:    { label: 'Class' },
      teamName:    { label: 'Team' },
      raceNumber:  { label: '#' },
      gear:        { label: 'G', align: 'center', width: '28px' },
      rpm:         { label: 'RPM', align: 'right' },
      speedKmh:    { label: 'Speed', align: 'right' },
      posX:        { label: 'Pos X', align: 'right', fmt: 'pos' },
      trackPosition: { label: 'Trk', align: 'center', width: '28px' },
      splinePos:   { label: 'Spline', align: 'right', fmt: 'pos' },
      pitStatus:   { label: 'Pits', width: '60px' },
      speedKmh:    { label: 'Speed', align: 'right' },
      posX:        { label: 'Pos X', align: 'right', fmt: 'pos' },
      posY:        { label: 'Pos Y', align: 'right', fmt: 'pos' },
      posZ:        { label: 'Pos Z', align: 'right', fmt: 'pos' },
    };

    // Build header
    const keys = entries.length > 0 ? Object.keys(entries[0]) : [];
    const cols = keys.filter(k => COL_DEFS[k] && k !== 'index');
    const colsOrder = ['isPlayer', 'position', 'carModel', 'driverName', 'teamName',
      'raceNumber', 'gear', 'speedKmh', 'laps', 'bestLapTime', 'lastLapTime',
      'currentLapTime', 'pitStatus', 'splinePos', 'trackPosition',
      'posX', 'posY', 'posZ'].filter(k => keys.includes(k));

    let headHtml = '';
    for (const col of colsOrder) {
      const def = COL_DEFS[col] || { label: col };
      headHtml += `<th style="${def.align ? 'text-align:' + def.align : ''}${def.width ? ';width:' + def.width : ''}">${def.label}</th>`;
    }
    tHead.innerHTML = '<tr>' + headHtml + '</tr>';

    // Build rows
    let bodyHtml = '';
    for (const entry of entries) {
      const isPlayer = entry.isPlayer == 1 || entry.isPlayer == true;
      let rowClass = isPlayer ? 'is-player' : '';
      bodyHtml += `<tr class="${rowClass}">`;
      for (const col of colsOrder) {
        const val = entry[col];
        const def = COL_DEFS[col] || {};
        let display = formatOppVal(val, def);
        let cls = '';
        if (col === 'place') {
          if (val === 1) cls = 'pos-first';
          else if (val <= 3) cls = 'pos-podium';
        }
        if (col === 'inPits') cls = val == 1 ? 'pit-yes' : 'in-race';
        if (col === 'isPlayer') display = isPlayer ? '★' : '';
        bodyHtml += `<td class="${cls}" style="${def.align ? 'text-align:' + def.align : ''}">${display}</td>`;
      }
      bodyHtml += '</tr>';
    }
    tBody.innerHTML = bodyHtml;
  }

  function formatOppVal(val, def) {
    if (val === null || val === undefined) return '—';
    if (def.fmt === 'time') {
      const num = Number(val);
      if (num <= 0) return '—';
      return num.toFixed(3);
    }
    if (def.fmt === 'speed') {
      const num = Number(val);
      if (num <= 0) return '—';
      return (num * 3.6).toFixed(0) + ' km/h';
    }
    if (def.fmt === 'pit') {
      if (val == 1) return 'PIT';
      return 'out';
    }
    if (def.fmt === 'energy') {
      const num = Number(val);
      return num.toFixed(1);
    }
    if (def.fmt === 'pos') {
      const num = Number(val);
      if (num === 0 || Math.abs(num) < 0.001) return '—';
      if (!isFinite(num)) return '—';
      if (Math.abs(num) > 10000) return '∞';
      return num.toFixed(1);
    }
    if (def.fmt === 'percent') {
      const num = Number(val);
      if (num <= 0) return '—';
      return (num * 100).toFixed(0) + '%';
    }
    if (typeof val === 'number') {
      if (Number.isInteger(val)) return val.toString();
      return val.toFixed(2);
    }
    return String(val);
  }

  // --- Tabs ---
  function switchTab(tabId) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
    document.querySelector(`[data-tab="${tabId}"]`).classList.add('active');
    document.getElementById(`tab-${tabId}`).classList.add('active');

    // Stop all intervals
    if (rawInterval) { clearInterval(rawInterval); rawInterval = null; }
    if (rawStructInterval) { clearInterval(rawStructInterval); rawStructInterval = null; }

    if (tabId === 'rawjson') {
      if (dom.autoRefreshRaw().checked) {
        rawInterval = setInterval(readOnce, 500);
      }
      readOnce();
    } else if (tabId === 'rawstruct') {
      if (dom.autoRefreshRawStruct().checked) {
        rawStructInterval = setInterval(loadRawStruct, 500);
      }
      loadRawStruct();
    } else if (tabId === 'opponents') {
      loadOpponents();
    } else if (tabId === 'coverage' && connectedGame) {
      const game = dom.gameSelect().disabled ? connectedGame : dom.gameSelect().value;
      loadCoverage(game);
    }
  }

  // --- Init ---
  function init() {
    dom.connectBtn().addEventListener('click', connect);
    dom.disconnectBtn().addEventListener('click', disconnect);
    dom.readOnceBtn().addEventListener('click', readOnce);
    dom.refreshCoverageBtn().addEventListener('click', function () {
      if (connectedGame) loadCoverage(connectedGame);
    });
    dom.refreshOpponentsBtn().addEventListener('click', loadOpponents);
    dom.logCoverageBtn().addEventListener('click', function () {
      if (connectedGame) logCoverage(connectedGame);
    });
    dom.logRawBtn().addEventListener('click', function () {
      if (connectedGame) logRawData(connectedGame);
    });
    dom.logMappedBtn().addEventListener('click', logMappedData);

    dom.autoRefreshRaw().addEventListener('change', function () {
      if (document.querySelector('.tab[data-tab="rawjson"]').classList.contains('active')) {
        if (this.checked) {
          rawInterval = setInterval(readOnce, 500);
        } else {
          clearInterval(rawInterval);
          rawInterval = null;
        }
      }
    });

    dom.autoRefreshRawStruct().addEventListener('change', function () {
      if (document.querySelector('.tab[data-tab="rawstruct"]').classList.contains('active')) {
        if (this.checked) {
          rawStructInterval = setInterval(loadRawStruct, 500);
        } else {
          clearInterval(rawStructInterval);
          rawStructInterval = null;
        }
      }
    });

    document.querySelectorAll('.tab').forEach(tab => {
      tab.addEventListener('click', function () { switchTab(this.dataset.tab); });
    });

    document.addEventListener('keydown', function (e) {
      if (e.ctrlKey && e.key === 'Enter') connect();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
