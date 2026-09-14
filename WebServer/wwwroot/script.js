// ==================== DOM HELPERS ====================
const $ = id => document.getElementById(id);
const toggle = (el, show) => { if (typeof el === 'string') el = $(el); if (el) el.style.display = show ? '' : 'none'; };
const esc = t => String(t || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// ==================== TAB SWITCHING ====================
function switchTab(name) {
    document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.textContent.toLowerCase().includes(name)));
    document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
    const target = $('tab-' + name);
    if (target) target.classList.add('active');
    if (name === 'settings' && !settingsLoaded) loadSettings();
    if (name === 'history') loadHistory('loot');
}

// ==================== WEBSOCKET & STATUS ====================
let ws = null, wsRetryTimer = null, lastDecision = '', lastStatus = '';
let logLines = [], settingsLoaded = false, S = {}, activeSettingsTab = 'build';
let currentBotMode = 'Idle';
let detectedSkillBar = [];

function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    ws = new WebSocket(`${proto}//${location.host}/api/ws`);
    ws.onopen = () => { $('wsStatus').textContent = 'Live'; $('wsStatus').className = 'connection-badge ok'; };
    ws.onmessage = e => { try { updateDashboard(JSON.parse(e.data)); } catch { } };
    ws.onclose = () => {
        $('wsStatus').textContent = 'Reconnecting...'; $('wsStatus').className = 'connection-badge err';
        if (!wsRetryTimer) wsRetryTimer = setTimeout(() => { wsRetryTimer = null; connectWs(); }, 2000);
    };
    ws.onerror = () => ws.close();
}
connectWs();

function updateDashboard(s) {
    const dot = $('statusDot'), txt = $('botStatusText');
    if (!s.inGame) { dot.className = 'status-dot'; txt.textContent = 'Not In Game'; }
    else if (s.running) { dot.className = 'status-dot running'; txt.textContent = `${s.mode}${s.phase ? ' - ' + s.phase : ''}`; }
    else { dot.className = 'status-dot paused'; txt.textContent = 'Paused'; }

    $('areaName').textContent = s.area || '--';
    updateRuntimeWidget(s);

    const ce = $('inCombat');
    ce.textContent = s.inCombat ? 'FIGHTING' : 'Clear';
    ce.className = 'stat-value ' + (s.inCombat ? 'red' : 'green');
    $('monsters').textContent = s.nearbyMonsters;
    $('combatTarget').textContent = s.combatTarget || '--';

    const ne = $('navStatus');
    ne.textContent = s.isNavigating ? `${s.waypointIndex + 1}/${s.waypointTotal}` : 'Idle';
    ne.className = 'stat-value ' + (s.isNavigating ? 'accent' : '');

    $('coverage').textContent = (s.explorationCoverage * 100).toFixed(1) + '%';
    $('regions').textContent = s.explorationRegions;

    const cpd = s.chaosPerDivine || 0;
    $('sessionDiv').textContent = cpd > 0 ? (s.sessionChaos / cpd).toFixed(2) : s.sessionChaos.toFixed(1) + 'c';
    $('divPerHour').textContent = cpd > 0 ? (s.chaosPerHour / cpd).toFixed(2) : s.chaosPerHour.toFixed(1) + 'c';
    $('itemsLooted').textContent = s.itemsLooted;
    $('mapsCompleted').textContent = s.mapsCompleted;
    $('sessionTime').textContent = s.sessionDuration || '--';
    $('lootNearby').textContent = s.lootCandidates;

    // Mode specific cards
    toggle('simCard', s.mode === 'Simulacrum');
    if (s.mode === 'Simulacrum') {
        $('simWave').textContent = s.simWave > 0 ? `${s.simWave}/15` : '-';
        $('simWave').style.color = s.simWaveActive ? '#ff4444' : '';
        $('simDeaths').textContent = s.simDeaths;
        $('simRunTime').textContent = s.simRunTime || '--';
        $('simRuns').textContent = s.simRuns;
        $('simAvgWaves').textContent = s.simAvgWaves > 0 ? s.simAvgWaves.toFixed(1) : '-';
        $('simAvgRunTime').textContent = s.simAvgRunTime || '--';
    }

    toggle('labCard', s.mode === 'Labyrinth');
    if (s.mode === 'Labyrinth') {
        $('labIzaro').textContent = `${s.labIzaroEncounters}/3`;
        $('labDeaths').textContent = s.labDeaths;
        $('labRuns').textContent = s.labRuns;
        $('labGems').textContent = s.labGemsTransformed;
        $('labProfit').textContent = s.labTotalProfit.toFixed(0) + 'c';
        $('labSelectedGem').textContent = s.labSelectedGem || '-';
    }

    toggle('bossCard', s.mode === 'Boss');
    if (s.mode === 'Boss') {
        $('bossRuns').textContent = s.bossRuns;
        $('bossDeaths').textContent = s.bossDeaths;
        $('bossRunTime').textContent = s.bossRunTime || '--';
        $('bossDrops').textContent = s.bossDrops;
        $('bossRunsPerDrop').textContent = s.bossRunsPerDrop > 0 ? s.bossRunsPerDrop.toFixed(1) : '-';
        $('bossAvgRun').textContent = s.bossAvgRunTime > 0 ? Math.round(s.bossAvgRunTime) + 's' : '--';
        $('bossChaosHr').textContent = s.bossChaosPerHour > 0 ? Math.round(s.bossChaosPerHour) + 'c' : '0';
    }

    toggle('heistCard', s.mode === 'Heist');
    if (s.mode === 'Heist') {
        $('heistPhase').textContent = s.phase || s.heistPhase || '-';
        const alertVal = s.heistAlertPercent != null ? s.heistAlertPercent : 0;
        $('heistAlert').textContent = `${alertVal.toFixed(0)}%`;
        $('heistLockdown').textContent = s.heistLockdown ? 'LOCKDOWN' : 'Normal';
        $('heistLockdown').className = 'stat-value ' + (s.heistLockdown ? 'red' : 'green');
        $('heistTarget').textContent = s.heistTarget || s.decision || '--';
    }

    const farmCard = $('farmCard');
    toggle(farmCard, s.mode === 'Wave Farming');
    if (s.mode === 'Wave Farming') {
        $('farmRuns').textContent = s.farmRuns;
        $('farmPhase').textContent = s.farmPhase || '-';
        if (!farmCardInitialized) {
            initFarmCard();
        }
        const farmSel = $('farmStrategySelect');
        if (s.farmStrategy && farmSel.value !== s.farmStrategy) farmSel.value = s.farmStrategy;
        if (s.farmStrategy && farmCard.dataset.loadedStrategy !== s.farmStrategy) {
            farmCard.dataset.loadedStrategy = s.farmStrategy;
            loadFarmStrategySettings(s.farmStrategy);
        }
    }

    toggle('explorationCard', !['Simulacrum', 'Follower', 'Idle', 'Heist'].includes(s.mode));
    toggle('statMapsDone', s.mode === 'Wave Farming');
    toggle('labGemCard', s.mode === 'Labyrinth');

    const phaseEl = $('minimapPhase');
    if (s.phase) { phaseEl.textContent = s.phase; toggle(phaseEl, true); }
    else toggle(phaseEl, false);

    if (s.mode && s.mode !== currentBotMode) {
        currentBotMode = s.mode;
        $('modeTabLabel').textContent = s.mode === 'Idle' ? 'Mode' : currentBotMode;
        if (settingsLoaded && activeSettingsTab === 'mode') {
            const modeTab = $('stab-mode');
            if (modeTab) { modeTab.innerHTML = ''; renderModeTab(modeTab, currentBotMode); }
        }
    }

    const sel = $('modeSelect');
    if (!sel.matches(':focus')) { for (let o of sel.options) if (o.value === s.mode) { sel.value = s.mode; break; } }

    if (s.decision && s.decision !== lastDecision) { addLog('decision', s.decision); lastDecision = s.decision; }
    if (s.status && s.status !== lastStatus) { addLog('status', s.status); lastStatus = s.status; }
}

function fmtHM(seconds) {
    if (seconds < 0) seconds = 0;
    return Math.floor(seconds / 3600) + ':' + Math.floor((seconds % 3600) / 60).toString().padStart(2, '0');
}

function updateRuntimeWidget(s) {
    const active = s.runtimeActiveSeconds || 0, max = s.runtimeMaxMinutes || 0, left = s.runtimeRemainingSeconds || 0;
    $('runtimeElapsed').textContent = fmtHM(active);
    const remEl = $('runtimeRemaining'), barEl = $('runtimeBar');

    if (max <= 0) {
        remEl.textContent = 'no limit'; remEl.style.color = 'var(--text-dim)'; barEl.style.width = '0%';
        return;
    }

    const maxSec = max * 60;
    barEl.style.width = Math.min(100, Math.round((active / maxSec) * 100)) + '%';
    let color = left <= 0 ? '#888' : left < 300 ? '#ff5c5c' : (left / maxSec) < 0.10 ? '#ffaa33' : 'var(--accent)';
    barEl.style.background = color; remEl.style.color = color;
    remEl.textContent = left > 0 ? `${fmtHM(left)} remaining (cap ${Math.floor(max / 60)}:${(max % 60).toString().padStart(2, '0')})` : 'expired — bot stopped';
}

async function resetRuntime() {
    if (!confirm('Reset the active-runtime timer to zero?')) return;
    try { if ((await fetch('/api/runtime/reset', { method: 'POST' })).ok) flashSave(); } catch { }
}

async function resetLootStats() {
    if (confirm('Reset loot tracking stats?')) try { await fetch('/api/loot/reset', { method: 'POST' }); } catch { }
}

function addLog(cls, text) {
    const now = new Date().toLocaleTimeString();
    logLines.push({ cls, text, time: now });
    if (logLines.length > 50) logLines.shift();
    const area = $('logArea');
    area.innerHTML = logLines.map(l => `<div><span class="time">${l.time}</span> <span class="${l.cls}">${esc(l.text)}</span></div>`).join('');
    area.scrollTop = area.scrollHeight;
}

function toggleMapLegend() { const el = $('minimapLegend'); toggle(el, el.style.display === 'none'); }
function toggleActivityLog() {
    const log = $('logArea'), open = log.style.display === 'none';
    toggle(log, open);
    $('logToggle').style.transform = open ? 'rotate(90deg)' : '';
}

// ==================== TAG LIST & AUTOCOMPLETE ====================
function parseTagList(key) { return (S[key]?.value || '').split(',').map(s => s.trim()).filter(s => s.length > 0); }
function saveTagList(key, tags) { updateSetting(key, tags.join(',')); }

function buildTagListWithSearch(key, title, descText, placeholder, searchFn) {
    const wrap = document.createElement('div');
    wrap.innerHTML = `<div class="section-divider">${title}</div>
    <div style="font-size:12px;color:var(--text-dim);margin:0 12px 8px">${descText}</div>
    <div style="padding:0 12px 12px">
      <div class="tag-list"></div>
      <div class="tag-add-row">
        <input type="text" placeholder="${placeholder}">
        <button class="add-btn">Add</button>
        ${searchFn ? '<button class="scan-btn" style="margin-left:4px">Scan Nearby</button>' : ''}
        <div class="autocomplete-dropdown"></div>
      </div>
    </div>`;

    const tagList = wrap.querySelector('.tag-list');
    const input = wrap.querySelector('input');
    const addBtn = wrap.querySelector('.add-btn');
    const scanBtn = wrap.querySelector('.scan-btn');
    const dropdown = wrap.querySelector('.autocomplete-dropdown');

    function render() {
        tagList.innerHTML = '';
        for (const tag of parseTagList(key)) {
            const item = document.createElement('span'); item.className = 'tag-item';
            item.innerHTML = `${esc(tag)}<button class="tag-remove" title="Remove">&times;</button>`;
            item.querySelector('.tag-remove').onclick = () => { saveTagList(key, parseTagList(key).filter(t => t !== tag)); render(); };
            tagList.appendChild(item);
        }
    }

    function addTag(val) {
        val = val.trim(); if (!val) return;
        const tags = parseTagList(key);
        if (!tags.some(t => t.toLowerCase() === val.toLowerCase())) { tags.push(val); saveTagList(key, tags); }
        input.value = ''; toggle(dropdown, false); render();
    }

    addBtn.onclick = () => addTag(input.value);
    input.onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); addTag(input.value); } };

    if (searchFn) {
        scanBtn.onclick = () => searchFn(dropdown, addTag, parseTagList(key));
    } else {
        let timer = null;
        input.oninput = () => {
            clearTimeout(timer);
            const q = input.value.trim();
            if (q.length < 2) { toggle(dropdown, false); return; }
            timer = setTimeout(async () => {
                try {
                    const r = await fetch(`/api/ninja/uniques?q=${encodeURIComponent(q)}`);
                    if (!r.ok) return;
                    const results = await r.json();
                    dropdown.innerHTML = '';
                    const existing = new Set(parseTagList(key).map(t => t.toLowerCase()));
                    for (const item of results) {
                        if (existing.has(item.name.toLowerCase())) continue;
                        const el = document.createElement('div'); el.className = 'autocomplete-item';
                        el.innerHTML = `<span>${esc(item.name)}</span><span><span class="ac-value">${item.chaos.toFixed(0)}c</span><span class="ac-cat">${esc(item.category)}</span></span>`;
                        el.onclick = () => addTag(item.name);
                        dropdown.appendChild(el);
                    }
                    toggle(dropdown, dropdown.children.length > 0);
                } catch { }
            }, 300);
        };
    }

    document.addEventListener('click', e => { if (!wrap.contains(e.target)) toggle(dropdown, false); });
    render();
    return wrap;
}

function buildMustLootUniques() {
    return buildTagListWithSearch('loot.mustLootUniques', 'Must-Loot Uniques', 'Always pick up these uniques regardless of value filtering. Search poe.ninja to add.', 'Search unique items...');
}

function buildEnemyBlacklist() {
    return buildTagListWithSearch('build.blacklistedEnemies', 'Enemy Blacklist', 'Enemies to ignore globally across all combat and seek-and-destroy logic. Click "Scan Nearby" while in-game to discover names.', 'Type enemy name or scan nearby...', async (dropdown, addTag, existingTags) => {
        dropdown.innerHTML = '<div class="autocomplete-item" style="color:var(--text-dim)">Scanning...</div>';
        toggle(dropdown, true);
        try {
            const r = await fetch('/api/nearby-monsters');
            if (!r.ok) { dropdown.innerHTML = '<div class="autocomplete-item" style="color:var(--text-dim)">Not available (bot not in game)</div>'; return; }
            const monsters = await r.json();
            dropdown.innerHTML = '';
            const existing = new Set(existingTags.map(t => t.toLowerCase()));
            for (const m of monsters) {
                if (existing.has(m.name.toLowerCase())) continue;
                const el = document.createElement('div'); el.className = 'autocomplete-item';
                const color = m.rarity === 'Unique' ? '#af6025' : m.rarity === 'Rare' ? '#ff7' : m.rarity === 'Magic' ? '#88f' : '#ccc';
                el.innerHTML = `<span>${esc(m.name)}</span><span><span style="color:${color};margin-right:8px">${esc(m.rarity)}</span><span class="ac-cat">${m.distance.toFixed(0)}g &times;${m.count}</span></span>`;
                el.onclick = () => addTag(m.name);
                dropdown.appendChild(el);
            }
            if (dropdown.children.length === 0) dropdown.innerHTML = '<div class="autocomplete-item" style="color:var(--text-dim)">All nearby enemies already blacklisted</div>';
        } catch (e) { dropdown.innerHTML = `<div class="autocomplete-item" style="color:var(--text-dim)">Error: ${e.message}</div>`; }
    });
}

function buildTagListEditor(key, title, placeholder, showIfKey) {
    const wrap = document.createElement('div');
    if (showIfKey) { const dep = S[showIfKey]; toggle(wrap, dep && dep.value); wrap.dataset.showIf = showIfKey; }
    wrap.innerHTML = `<div class="section-divider">${title}</div>
    <div style="padding:8px 12px">
      <div class="tag-list"></div>
      <div class="tag-add-row"><input type="text" placeholder="${placeholder}"><button>Add</button></div>
    </div>`;

    const tagList = wrap.querySelector('.tag-list'), input = wrap.querySelector('input'), addBtn = wrap.querySelector('button');

    function render() {
        tagList.innerHTML = '';
        for (const tag of parseTagList(key)) {
            const item = document.createElement('span'); item.className = 'tag-item';
            item.innerHTML = `${esc(tag)}<button class="tag-remove" title="Remove">&times;</button>`;
            item.querySelector('.tag-remove').onclick = () => { saveTagList(key, parseTagList(key).filter(t => t !== tag)); render(); };
            tagList.appendChild(item);
        }
    }

    function add() {
        const val = input.value.trim(); if (!val) return;
        const tags = parseTagList(key);
        if (!tags.some(t => t.toLowerCase() === val.toLowerCase())) { tags.push(val); saveTagList(key, tags); }
        input.value = ''; render();
    }

    addBtn.onclick = add;
    input.onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); add(); } };
    render();
    return wrap;
}

// ==================== COMMANDS ====================
async function sendCmd(action, value) {
    try { await fetch('/api/control', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ action, value }) }); } catch { }
}

$('modeSelect').addEventListener('change', function () {
    sendCmd('setMode', this.value);
    if (this.value && this.value !== currentBotMode) {
        currentBotMode = this.value;
        $('modeTabLabel').textContent = this.value === 'Idle' ? 'Mode' : this.value;
        if (settingsLoaded) { const modeTab = $('stab-mode'); if (modeTab) { modeTab.innerHTML = ''; renderModeTab(modeTab, currentBotMode); } }
    }
});

// ==================== BLIGHT TOWER TABLE ====================
function renderTowerTable() {
    const table = document.createElement('table'); table.className = 'tower-table';
    table.innerHTML = '<thead><tr><th>Tower</th><th>Priority</th><th>Stack</th><th>Nearby Req</th><th>Tier 3</th></tr></thead>';
    const tbody = document.createElement('tbody');
    for (const t of TOWER_TYPES) {
        const pKey = `blight.${t}.priority`, sKey = `blight.${t}.canStack`, nKey = `blight.${t}.requiresNearbyTower`, bKey = `blight.${t}.tier3Branch`;
        const p = S[pKey], s = S[sKey], n = S[nKey], b = S[bKey];
        if (!p) continue;
        const name = t.charAt(0).toUpperCase() + t.slice(1);
        const tr = document.createElement('tr');
        const branchOpts = (b?.options || ['None', 'Left', 'Right']).map(o => `<option value="${o}" ${o === b?.value ? 'selected' : ''}>${o}</option>`).join('');
        tr.innerHTML = `<td>${name}</td>
      <td><div class="range-control" style="justify-content:center"><input type="range" class="range-slider" style="width:60px" min="0" max="5" value="${p.value}" oninput="this.nextElementSibling.textContent=this.value;updateSetting('${pKey}',parseInt(this.value))"><span class="range-suffix" style="min-width:12px">${p.value}</span></div></td>
      <td><input type="checkbox" ${s?.value ? 'checked' : ''} onchange="updateSetting('${sKey}',this.checked)" style="accent-color:var(--accent);width:16px;height:16px"></td>
      <td><input type="checkbox" ${n?.value ? 'checked' : ''} onchange="updateSetting('${nKey}',this.checked)" style="accent-color:var(--accent);width:16px;height:16px"></td>
      <td><select onchange="updateSetting('${bKey}',this.value)">${branchOpts}</select></td>`;
        tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    return table;
}

// ==================== SKILL BAR CONFIG ====================
const SKILL_TYPES = {
    disabled: { label: 'Disabled', role: 'Disabled', fields: [] },
    move: { label: 'Move Only', role: 'PrimaryMovement', fields: [] },
    attack: { label: 'Attack', role: 'Enemy', fields: ['priority', 'targetFilter', 'maxTargetRange', 'minCastIntervalMs', 'requireTargetable', 'isChannel'], defaults: { priority: 8 } },
    blink: { label: 'Blink / Dash', role: 'MovementSkill', fields: ['canCrossTerrain', 'minCastIntervalMs'], defaults: { canCrossTerrain: true } },
    curse: { label: 'Curse / Debuff', role: 'Enemy', fields: ['priority', 'minCastIntervalMs', 'requireTargetable', 'buffDebuffName'], defaults: { priority: 3, onlyWhenBuffMissing: true, minCastIntervalMs: 5000 } },
    buff: { label: 'Buff', role: 'Self', fields: ['priority', 'buffDebuffName'], defaults: { priority: 5, onlyWhenBuffMissing: true } },
    guard: { label: 'Guard', role: 'Self', fields: ['priority'], defaults: { priority: 7, onlyOnLowLife: true } },
    vaal: { label: 'Vaal Skill', role: 'Self', fields: ['priority', 'minNearbyEnemies'], defaults: { priority: 6, minNearbyEnemies: 5 } },
    warcry: { label: 'Warcry', role: 'Enemy', fields: ['priority', 'minNearbyEnemies', 'minCastIntervalMs', 'requireTargetable'], defaults: { priority: 4, minNearbyEnemies: 3 } },
    summon: { label: 'Summon / Minion', role: 'Self', fields: ['priority'], defaults: { priority: 5, summonRecast: true } },
    corpse: { label: 'Corpse Skill', role: 'Corpse', fields: ['priority', 'buffDebuffName'], defaults: { priority: 5, onlyWhenBuffMissing: true } },
    totem: { label: 'Totem', role: 'Enemy', fields: ['priority', 'minNearbyEnemies', 'maxTargetRange', 'requireTargetable'], defaults: { priority: 6, summonRecast: true, minNearbyEnemies: 1 } },
    custom: { label: 'Custom', role: null, fields: ['role', 'priority', 'canCrossTerrain', 'targetFilter', 'maxTargetRange', 'minNearbyEnemies', 'minCastIntervalMs', 'onlyWhenBuffMissing', 'buffDebuffName', 'onlyOnLowLife', 'summonRecast', 'requireTargetable', 'isChannel'], defaults: {} },
};

const CURSE_NAMES = ['despair', 'flammability', 'conductivity', 'vulnerability', 'punishment', 'enfeeble', 'temporal_chains', 'elemental_weakness', 'poachers_mark', 'warlords_mark', 'assassins_mark', 'snipers_mark', 'projectile_weakness', 'frostbite'];
const OFFERING_NAMES = ['flesh_offering', 'spirit_offering', 'bone_offering', 'blood_offering'];
const GUARD_NAMES = ['molten_shell', 'steelskin', 'immortal_call', 'bone_armour', 'arcane_cloak', 'frost_shield'];
const BLINK_NAMES = ['frostblink', 'flame_dash', 'dash', 'leap_slam', 'shield_charge', 'whirling_blades', 'lightning_warp', 'blink_arrow', 'flicker_strike', 'smoke_mine', 'bodyswap', 'charged_dash'];

function autoClassifySkill(d) {
    const n = (d.internalName || '').toLowerCase();
    if (d.skillName === 'Move' || n === '') return 'move';
    if (BLINK_NAMES.some(b => n.includes(b))) return 'blink';
    if (d.isVaalSkill) return 'vaal';
    if (d.isCry) return 'warcry';
    if (d.isTotem) return 'totem';
    if (d.isTrap || d.isMine) return 'custom';
    if (CURSE_NAMES.some(c => n.includes(c))) return 'curse';
    if (OFFERING_NAMES.some(o => n.includes(o))) return 'corpse';
    if (GUARD_NAMES.some(g => n.includes(g))) return 'guard';
    if (d.deployedCount > 0 || n.includes('summon') || n.includes('raise') || n.includes('animate')) return 'summon';
    return 'attack';
}

function typeFromConfig(prefix) {
    const role = S[prefix + '.role']?.value || 'Disabled';
    if (role === 'Disabled') return null;
    if (role === 'PrimaryMovement') return 'move';
    if (role === 'MovementSkill') return 'blink';
    if (role === 'Corpse') return 'corpse';
    if (role === 'Enemy') {
        if (S[prefix + '.summonRecast']?.value) return 'totem';
        if (S[prefix + '.onlyWhenBuffMissing']?.value) return 'curse';
        return 'attack';
    }
    if (role === 'Self') {
        if (S[prefix + '.onlyOnLowLife']?.value) return 'guard';
        if (S[prefix + '.summonRecast']?.value) return 'summon';
        if (S[prefix + '.onlyWhenBuffMissing']?.value) return 'buff';
        return 'buff';
    }
    return 'custom';
}

function findConfigSlotForKey(keyName) {
    for (let i = 1; i <= 8; i++) {
        const ke = S[`build.skill${i}.key`];
        if (ke && ke.value === keyName) return i;
    }
    for (let i = 1; i <= 8; i++) {
        const ke = S[`build.skill${i}.key`];
        if (ke && ke.value === 'None') {
            updateSetting(`build.skill${i}.key`, keyName);
            ke.value = keyName;
            return i;
        }
    }
    return null;
}

function renderSkillsSection() {
    const wrap = document.createElement('div');
    wrap.innerHTML = '<div class="section-divider">Skill Bar</div>';

    if (detectedSkillBar.length > 0) {
        for (const detected of detectedSkillBar) {
            const configIdx = findConfigSlotForKey(detected.key);
            const prefix = configIdx ? `build.skill${configIdx}` : null;
            let skillType = prefix ? typeFromConfig(prefix) : null;
            if (!skillType) skillType = autoClassifySkill(detected);
            if (detected.isVaalSkill && skillType === 'attack') skillType = 'vaal';

            const typeDef = SKILL_TYPES[skillType] || SKILL_TYPES.disabled;
            const slot = document.createElement('div'); slot.className = 'subgroup';
            if (configIdx) slot.id = `skill-slot-${configIdx}`;

            const header = document.createElement('h4');
            header.style.cssText = 'display:flex;justify-content:space-between;align-items:center';
            const typeColor = skillType === 'disabled' ? 'var(--text-dim)' : 'var(--accent)';
            header.innerHTML = `<span><span class="hotkey-badge" style="margin-right:8px">${esc(detected.key)}</span><span style="color:var(--text)">${esc(detected.skillName)}</span></span><span style="color:${typeColor};font-size:12px">${esc(typeDef.label)}</span>`;

            const body = document.createElement('div');
            body.style.display = 'none';
            header.onclick = () => { body.style.display = body.style.display === 'none' ? 'block' : 'none'; };

            if (prefix) {
                const typeRow = document.createElement('div'); typeRow.className = 'setting-row';
                const typeOpts = Object.entries(SKILL_TYPES).map(([k, v]) => `<option value="${k}" ${k === skillType ? 'selected' : ''}>${esc(v.label)}</option>`).join('');
                typeRow.innerHTML = `<div class="setting-info"><span class="setting-label">Skill Type</span><span class="setting-desc">How the bot should use this skill</span></div><div class="setting-control"><select onchange="changeSkillType('${prefix}',this.value,${configIdx})">${typeOpts}</select></div>`;
                body.appendChild(typeRow);

                const fieldsContainer = document.createElement('div');
                fieldsContainer.id = `skill-fields-${configIdx}`;
                renderSkillFields(fieldsContainer, prefix, skillType, configIdx);
                body.appendChild(fieldsContainer);
            }
            slot.appendChild(header); slot.appendChild(body); wrap.appendChild(slot);
        }
    } else {
        for (let i = 1; i <= 8; i++) {
            const prefix = `build.skill${i}`;
            const roleEntry = S[`${prefix}.role`]; if (!roleEntry) continue;
            const role = roleEntry.value || 'Disabled';
            const keyEntry = S[`${prefix}.key`];
            const keyName = keyEntry ? keyEntry.value : '?';

            const slot = document.createElement('div'); slot.className = 'subgroup'; slot.id = `skill-slot-${i}`;
            const header = document.createElement('h4');
            header.style.cssText = 'display:flex;justify-content:space-between';
            header.innerHTML = `<span>Slot ${i}</span><span style="font-weight:400;color:var(--text-dim)">${esc(keyName)} — ${esc(role)}</span>`;
            const body = document.createElement('div');
            body.style.display = (role === 'Disabled' && keyName === 'None') ? 'none' : 'block';
            header.onclick = () => { body.style.display = body.style.display === 'none' ? 'block' : 'none'; };

            body.appendChild(buildField(`${prefix}.key`, S[`${prefix}.key`]));
            body.appendChild(buildField(`${prefix}.role`, S[`${prefix}.role`]));
            const allFields = ['priority', 'canCrossTerrain', 'targetFilter', 'maxTargetRange', 'minNearbyEnemies', 'minCastIntervalMs', 'onlyWhenBuffMissing', 'buffDebuffName', 'onlyOnLowLife', 'summonRecast', 'requireTargetable'];
            for (const f of allFields) {
                const entry = S[`${prefix}.${f}`]; if (entry) body.appendChild(buildField(`${prefix}.${f}`, entry));
            }
            slot.appendChild(header); slot.appendChild(body); wrap.appendChild(slot);
        }
    }
    return wrap;
}

function renderSkillFields(container, prefix, skillType, configIdx) {
    container.innerHTML = '';
    const typeDef = SKILL_TYPES[skillType];
    if (!typeDef || typeDef.fields.length === 0) return;
    for (const fieldKey of typeDef.fields) {
        const fullKey = `${prefix}.${fieldKey}`;
        const entry = S[fullKey]; if (entry) container.appendChild(buildField(fullKey, entry));
    }
}

function changeSkillType(prefix, newType, configIdx) {
    const typeDef = SKILL_TYPES[newType];
    if (!typeDef) return;
    if (typeDef.role !== null) updateSetting(`${prefix}.role`, typeDef.role);

    const resets = { priority: 5, canCrossTerrain: false, onlyWhenBuffMissing: false, onlyOnLowLife: false, summonRecast: false, requireTargetable: false, minNearbyEnemies: 0, minCastIntervalMs: 0, maxTargetRange: 0 };
    const merged = { ...resets, ...(typeDef.defaults || {}) };
    for (const [k, v] of Object.entries(merged)) {
        if (S[`${prefix}.${k}`]) updateSetting(`${prefix}.${k}`, v);
    }

    const container = document.getElementById(`skill-fields-${configIdx}`);
    if (container) renderSkillFields(container, prefix, newType, configIdx);

    const slot = document.getElementById(`skill-slot-${configIdx}`);
    if (slot) {
        const typeSpan = slot.querySelector('h4 > span:last-child');
        if (typeSpan) {
            typeSpan.textContent = typeDef.label;
            typeSpan.style.color = newType === 'disabled' ? 'var(--text-dim)' : 'var(--accent)';
        }
    }
}

// ==================== ULTIMATUM, ALTARS, LAB GEM VALUATION ====================
const ULT_TIERS = [
    { value: 0, label: 'Free', color: '#4ade80' },
    { value: 1, label: 'Easy', color: '#a3e635' },
    { value: 3, label: 'Medium', color: '#facc15' },
    { value: 5, label: 'Hard', color: '#fb923c' },
    { value: 10, label: 'Very Hard', color: '#ef4444' },
    { value: 999, label: 'SKIP', color: '#dc2626' }
];

function dangerToTierIndex(danger) {
    if (danger >= 999) return 5;
    if (danger >= 6) return 4;
    if (danger >= 4) return 3;
    if (danger >= 2) return 2;
    if (danger >= 1) return 1;
    return 0;
}

function renderUltimatumModsExpander(parent) {
    const ultModDiv = document.createElement('div');
    ultModDiv.innerHTML = `<div class="section-divider" style="cursor:pointer">&#9654; Modifier Danger Rankings</div>
    <div style="display:none">
      <div style="color:var(--text-dim);font-size:12px;margin-bottom:8px">Rate each modifier. SKIP = never accept this mod.</div>
      <input type="text" placeholder="Filter mods..." style="width:100%;padding:4px 8px;margin-bottom:6px;background:var(--surface2);border:1px solid var(--border);color:var(--text);border-radius:4px">
      <div class="ult-body"></div>
    </div>`;

    const title = ultModDiv.querySelector('.section-divider');
    const content = ultModDiv.querySelector('div:nth-child(2)');
    const filter = ultModDiv.querySelector('input');
    const body = ultModDiv.querySelector('.ult-body');
    parent.appendChild(ultModDiv);

    let loaded = false;
    title.onclick = () => {
        const open = content.style.display !== 'none';
        toggle(content, !open);
        title.textContent = (open ? '\u25b6' : '\u25bc') + ' Modifier Danger Rankings';
        if (!loaded) { loadUltimatumMods(body, filter); loaded = true; }
    };
}

async function loadUltimatumMods(container, filterInput) {
    try {
        const resp = await fetch('/api/ultimatum-mods');
        if (!resp.ok) { container.textContent = 'Failed to load mods'; return; }
        const mods = await resp.json();

        function renderMods(filter) {
            container.innerHTML = '';
            const table = document.createElement('table'); table.className = 'tower-table';
            table.innerHTML = '<thead><tr><th style="text-align:left">Modifier</th><th>Danger</th></tr></thead>';
            const tbody = document.createElement('tbody');

            for (const mod of mods) {
                if (filter && !mod.name.toLowerCase().includes(filter) && !mod.id.toLowerCase().includes(filter)) continue;
                const tr = document.createElement('tr');
                const tierIdx = dangerToTierIndex(mod.currentDanger);
                const tier = ULT_TIERS[tierIdx];
                const nameStyle = mod.isOverridden ? 'font-weight:bold' : '';
                const options = ULT_TIERS.map((t, i) => `<option value="${t.value}" ${i === tierIdx ? 'selected' : ''}>${t.label}</option>`).join('');
                tr.innerHTML = `<td style="${nameStyle}">${esc(mod.name)}${mod.isOverridden ? ' *' : ''}</td>
          <td><select style="background:var(--surface2);border:2px solid ${tier.color};color:${tier.color};padding:2px 6px;border-radius:4px;font-weight:bold"
            onchange="setUltMod('${mod.id}',parseInt(this.value),this)">${options}</select></td>`;
                tbody.appendChild(tr);
            }
            table.appendChild(tbody);
            container.appendChild(table);
        }

        renderMods('');
        filterInput.addEventListener('input', () => renderMods(filterInput.value.toLowerCase()));
    } catch (e) { container.textContent = 'Error: ' + e.message; }
}

async function setUltMod(modId, danger, selectEl) {
    try {
        const resp = await fetch('/api/ultimatum-mods', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: modId, danger }) });
        if (resp.ok) {
            const tier = ULT_TIERS[dangerToTierIndex(danger)];
            selectEl.style.borderColor = tier.color; selectEl.style.color = tier.color;
            flashSave();
        }
    } catch { }
}

function weightToColor(w) {
    if (w >= 50) return '#4ade80';
    if (w >= 20) return '#a3e635';
    if (w > 0) return '#86efac';
    if (w === 0) return '#8b90a5';
    if (w > -50) return '#fbbf24';
    if (w > -100) return '#fb923c';
    return '#ef4444';
}

function renderAltarModsExpander(parent) {
    const altarModDiv = document.createElement('div');
    altarModDiv.innerHTML = `<div class="section-divider" style="cursor:pointer">&#9654; Altar Mod Weights</div>
    <div style="display:none">
      <input type="text" placeholder="Filter mods..." style="width:100%;padding:8px;margin:8px 0;background:var(--surface2);border:1px solid var(--border);border-radius:4px;color:var(--text);font-size:13px">
      <div class="altar-body" style="max-height:400px;overflow-y:auto"></div>
    </div>`;

    const title = altarModDiv.querySelector('.section-divider');
    const content = altarModDiv.querySelector('div:nth-child(2)');
    const filter = altarModDiv.querySelector('input');
    const body = altarModDiv.querySelector('.altar-body');
    parent.appendChild(altarModDiv);

    let loaded = false;
    title.onclick = () => {
        const open = content.style.display !== 'none';
        toggle(content, !open);
        title.textContent = (open ? '\u25b6' : '\u25bc') + ' Altar Mod Weights';
        if (!loaded) { loadAltarMods(body, filter); loaded = true; }
    };
}

async function loadAltarMods(container, filterInput) {
    try {
        const resp = await fetch('/api/altar-mods');
        if (!resp.ok) { container.textContent = 'Failed to load mods'; return; }
        const mods = await resp.json();

        function renderMods(filter) {
            container.innerHTML = '';
            const table = document.createElement('table'); table.className = 'tower-table';
            table.innerHTML = '<thead><tr><th style="text-align:left">Modifier</th><th>Weight</th></tr></thead>';
            const tbody = document.createElement('tbody');

            for (const mod of mods) {
                if (filter && !mod.name.toLowerCase().includes(filter)) continue;
                const tr = document.createElement('tr');
                const nameStyle = mod.isOverridden ? 'font-weight:bold' : '';
                const color = weightToColor(mod.currentWeight);
                tr.innerHTML = `<td style="${nameStyle}">${esc(mod.name)}${mod.isOverridden ? ' *' : ''}</td>
          <td><input type="number" value="${mod.currentWeight}" style="width:70px;background:var(--surface2);border:2px solid ${color};color:${color};padding:2px 6px;border-radius:4px;font-weight:bold;text-align:center"
            data-id="${mod.id}" onchange="setAltarMod(this.dataset.id,parseInt(this.value),this)"></td>`;
                tbody.appendChild(tr);
            }
            table.appendChild(tbody);
            container.appendChild(table);
        }

        renderMods('');
        filterInput.addEventListener('input', () => renderMods(filterInput.value.toLowerCase()));
    } catch (e) { container.textContent = 'Error: ' + e.message; }
}

async function setAltarMod(modId, weight, inputEl) {
    if (isNaN(weight)) return;
    try {
        const resp = await fetch('/api/altar-mods', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: modId, weight }) });
        if (resp.ok) {
            const color = weightToColor(weight);
            inputEl.style.borderColor = color; inputEl.style.color = color;
            flashSave();
        }
    } catch { }
}

async function loadGemReport() {
    const summaryEl = $('labColourSummary'), tableEl = $('labGemTable');
    summaryEl.innerHTML = 'Loading...'; tableEl.innerHTML = '';
    try {
        const resp = await fetch('/api/lab/gems');
        if (!resp.ok) { const err = await resp.json().catch(() => ({})); summaryEl.innerHTML = 'Error: ' + (err.error || resp.status); return; }
        const data = await resp.json();

        let html = '<div style="display:flex;gap:12px;margin-bottom:10px">';
        for (const c of (data.colourSummary || [])) {
            const col = c.colour === 'Red' ? '#e44' : c.colour === 'Green' ? '#4a4' : '#48f';
            html += `<div style="border:1px solid ${col};border-radius:6px;padding:8px;flex:1;text-align:center">
        <div style="color:${col};font-weight:bold">${c.colour}</div>
        <div style="font-size:12px;color:#888">${c.totalVariants} variants</div>
        <div style="font-size:14px;color:#fff">0q avg: ${c.avgValue0Q.toFixed(1)}c</div>
        <div style="font-size:14px;color:#ccc">20q avg: ${c.avgValue20Q > 0 ? c.avgValue20Q.toFixed(1) + 'c' : '-'}</div>
      </div>`;
        }
        html += '</div>';
        summaryEl.innerHTML = html;

        let tbl = `<table style="width:100%;border-collapse:collapse;font-size:12px">
      <tr style="color:#888;text-align:left;border-bottom:1px solid #333">
        <th style="padding:4px">Gem</th><th>Clr</th><th>#</th><th>Buy(0q)</th><th>Buy(20q)</th><th>Out(0q)</th><th>Out(20q)</th><th>Profit(0q)</th><th>Profit(20q)</th></tr>`;
        for (const g of (data.topGems || [])) {
            const col = g.colour === 'Red' ? '#e44' : g.colour === 'Green' ? '#4a4' : g.colour === 'Blue' ? '#48f' : '#888';
            const pc0 = g.expectedProfitLowQ > 0 ? '#4a4' : '#e44', pc20 = g.expectedProfit20Q > 0 ? '#4a4' : '#e44';
            tbl += `<tr style="border-bottom:1px solid #222">
        <td style="padding:3px">${esc(g.baseName)}</td>
        <td style="color:${col}">${g.colour}</td>
        <td>${g.variantCount}</td>
        <td>${g.inputCostLowQ > 0 ? g.inputCostLowQ.toFixed(1) + 'c' : '-'}</td>
        <td>${g.inputCost20Q > 0 ? g.inputCost20Q.toFixed(1) + 'c' : '-'}</td>
        <td>${g.avgOutput0Q.toFixed(1)}c</td>
        <td>${g.avgOutput20Q > 0 ? g.avgOutput20Q.toFixed(1) + 'c' : '-'}</td>
        <td style="color:${pc0}">${g.expectedProfitLowQ > 0 ? '+' : ''}${g.expectedProfitLowQ.toFixed(1)}c</td>
        <td style="color:${pc20}">${g.expectedProfit20Q !== 0 ? (g.expectedProfit20Q > 0 ? '+' : '') + g.expectedProfit20Q.toFixed(1) + 'c' : '-'}</td></tr>`;
        }
        tbl += '</table>';
        tableEl.innerHTML = tbl;
    } catch (e) { summaryEl.innerHTML = 'Failed: ' + e.message; }
}

async function capturePosition(settingKey) {
    const input = document.getElementById('input-' + settingKey.replace(/\./g, '-'));
    const btn = input?.parentNode?.querySelector('button');
    if (btn) btn.textContent = 'Capturing...';
    try {
        const resp = await fetch('/api/capture-position', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ setting: settingKey }) });
        const result = await resp.json();
        if (result.ok && input) {
            input.value = result.position;
            if (btn) btn.textContent = 'Set: ' + result.position;
        } else if (btn) btn.textContent = result.error || 'Failed';
    } catch { if (btn) btn.textContent = 'Error'; }
    setTimeout(() => { if (btn) btn.textContent = 'Set Current Pos'; }, 3000);
}

async function scanPlayerBuffs(settingKey) {
    const input = document.getElementById('input-' + settingKey.replace(/\./g, '-'));
    const btn = input?.parentNode?.querySelector('button');
    if (btn) btn.textContent = 'Scanning...';

    try {
        const buffs = await (await fetch('/api/player-buffs')).json();
        if (!Array.isArray(buffs) || buffs.length === 0) {
            if (btn) btn.textContent = 'No buffs found';
            setTimeout(() => { if (btn) btn.textContent = 'Scan Buffs'; }, 2000);
            return;
        }

        const existing = input?.parentNode?.querySelector('.buff-dropdown');
        if (existing) existing.remove();

        const dropdown = document.createElement('div');
        dropdown.className = 'buff-dropdown';
        dropdown.style.cssText = 'position:absolute;z-index:100;background:var(--surface);border:1px solid var(--border);border-radius:4px;max-height:200px;overflow-y:auto;width:300px;box-shadow:0 4px 12px rgba(0,0,0,0.3)';

        for (const name of buffs) {
            const item = document.createElement('div');
            item.textContent = name;
            item.style.cssText = 'padding:4px 8px;cursor:pointer;font-size:12px';
            item.onmouseenter = () => item.style.background = 'var(--surface2)';
            item.onmouseleave = () => item.style.background = '';
            item.onclick = () => { if (input) input.value = name; updateSetting(settingKey, name); dropdown.remove(); };
            dropdown.appendChild(item);
        }

        input.parentNode.style.position = 'relative';
        input.parentNode.appendChild(dropdown);
        const close = e => { if (!dropdown.contains(e.target) && e.target !== btn) { dropdown.remove(); document.removeEventListener('click', close); } };
        setTimeout(() => document.addEventListener('click', close), 0);
        if (btn) btn.textContent = `${buffs.length} buffs`;
    } catch { if (btn) btn.textContent = 'Error'; }
    setTimeout(() => { if (btn) btn.textContent = 'Scan Buffs'; }, 2500);
}

async function testDiscordWebhook(btn) {
    const original = btn.textContent; btn.textContent = 'Sending...'; btn.disabled = true;
    try {
        const result = await (await fetch('/api/action/discord-test', { method: 'POST' })).json();
        btn.textContent = result.ok ? 'Sent \u2713' : 'Failed';
    } catch { btn.textContent = 'Error'; }
    setTimeout(() => { btn.textContent = original; btn.disabled = false; }, 3000);
}

// ==================== FARMING STRATEGY ====================
const farmStrategySettingsPrefix = { 'Stacked Deck': 'farming.stackedDeck.' };
const farmStrategyDefaults = { 'Stacked Deck': { scarabs: ['Divination Scarab of The Cloister', 'Divination Scarab of The Cloister', 'Divination Scarab of The Cloister', 'Divination Scarab of The Cloister', 'Divination Scarab of The Cloister'], witness: 'None', atlasTree: 0 } };

let farmCardInitialized = false;

function initFarmCard() {
    if (farmCardInitialized || !settingsLoaded) return;
    farmCardInitialized = true;

    const farmSel = $('farmStrategySelect');
    if (farmSel && S['farming.farmStrategy']) {
        const opts = S['farming.farmStrategy'].options || [];
        farmSel.innerHTML = '';
        opts.forEach(o => farmSel.add(new Option(o, o)));
        farmSel.value = S['farming.farmStrategy'].value || (opts[0] || '');
    }

    const mapSel = $('farmMapSelect');
    if (mapSel && S['farming.mapName']) {
        const opts = S['farming.mapName'].options || [];
        mapSel.innerHTML = '';
        opts.forEach(o => mapSel.add(new Option(o ? o : '(any map)', o)));
        mapSel.value = S['farming.mapName'].value || '';
    }

    if (S['farming.witnessType'] && $('farmWitness')) $('farmWitness').value = S['farming.witnessType'].value || 'None';
    if (S['farming.atlasTreePreset'] && $('farmAtlasTree')) $('farmAtlasTree').value = S['farming.atlasTreePreset'].value ?? 0;
    if (S['mapRolling.minMapTier'] && $('farmMinTier')) $('farmMinTier').value = S['mapRolling.minMapTier'].value ?? 0;
    if (S['farming.maxRemainingMonsters'] && $('farmMaxRemaining')) $('farmMaxRemaining').value = S['farming.maxRemainingMonsters'].value ?? 10;

    for (let i = 1; i <= 5; i++) {
        const el = $('farmScarab' + i);
        if (el && S['mapDevice.slot' + i]) el.value = S['mapDevice.slot' + i].value || '';
    }
}

function onFarmStrategyChange(strategyName) {
    updateSetting('farming.farmStrategy', strategyName);
    const defs = farmStrategyDefaults[strategyName];
    if (defs && [1, 2, 3, 4, 5].every(i => !$('farmScarab' + i).value)) applyStrategyDefaults();
    $('farmCard').dataset.loadedStrategy = '';
}

function applyStrategyDefaults() {
    const strategyName = $('farmStrategySelect').value;
    const defs = farmStrategyDefaults[strategyName];
    if (!defs) return;
    if (defs.scarabs) {
        for (let i = 0; i < 5; i++) {
            const val = defs.scarabs[i] || '', el = $('farmScarab' + (i + 1));
            if (el) { el.value = val; updateSetting('mapDevice.slot' + (i + 1), val); }
        }
    }
    if (defs.witness) { $('farmWitness').value = defs.witness; updateSetting('farming.witnessType', defs.witness); }
    if (defs.atlasTree !== undefined) { $('farmAtlasTree').value = defs.atlasTree; updateSetting('farming.atlasTreePreset', defs.atlasTree); }
}

async function loadFarmStrategySettings(strategyName) {
    const container = $('farmSettingsContainer'); container.innerHTML = '';
    const sharedPrefix = 'farming.', strategyPrefix = farmStrategySettingsPrefix[strategyName];
    if (!settingsLoaded) await loadSettings();

    const shownAbove = new Set([
        'farming.farmStrategy', 
        'farming.mapName', 
        'farming.witnessType', 
        'farming.atlasTreePreset', 
        'farming.maxRemainingMonsters',
        'mapRolling.minMapTier', 
        'mapDevice.slot1', 
        'mapDevice.slot2', 
        'mapDevice.slot3', 
        'mapDevice.slot4', 
        'mapDevice.slot5', 
        'run.portalKey'
    ]);
    const entries = [];
    for (const [key, meta] of Object.entries(S)) {
        if (shownAbove.has(key)) continue;
        if (key.startsWith(sharedPrefix) && !key.includes('.stackedDeck.')) entries.push([key, meta]);
        else if (strategyPrefix && key.startsWith(strategyPrefix)) entries.push([key, meta]);
    }

    if (entries.length === 0) { container.innerHTML = '<p style="color:var(--text-dim);font-size:12px">No settings for this strategy</p>'; return; }
    for (const [key, meta] of entries) {
        const row = document.createElement('div');
        row.style.cssText = 'display:flex;align-items:center;gap:10px;margin-bottom:8px;padding:6px 10px;background:var(--surface2);border-radius:6px';
        const label = document.createElement('label');
        label.style.cssText = 'font-size:11px;color:var(--text-dim);min-width:140px;flex-shrink:0';
        label.textContent = meta.label || key.split('.').pop();
        row.appendChild(label);
        const input = createFarmSettingInput(key, meta);
        if (input) row.appendChild(input);
        container.appendChild(row);
    }
}

function createFarmSettingInput(key, meta) {
    const val = meta.value, type = meta.type;
    if (type === 'list' && meta.options) {
        const sel = document.createElement('select'); sel.className = 'mode-select'; sel.style.cssText = 'flex:1;max-width:200px';
        meta.options.forEach(o => sel.add(new Option(o, o)));
        sel.value = val; sel.onchange = () => updateSetting(key, sel.value);
        return sel;
    }
    if (type === 'toggle') {
        const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = val;
        cb.style.cssText = 'width:18px;height:18px;cursor:pointer';
        cb.onchange = () => updateSetting(key, cb.checked);
        return cb;
    }
    if (type === 'range_int' || type === 'range_float') {
        const wrap = document.createElement('div'); wrap.style.cssText = 'display:flex;align-items:center;gap:6px;flex:1';
        const range = document.createElement('input'); range.type = 'range'; range.min = meta.min ?? 0; range.max = meta.max ?? 100;
        const isFloat = type === 'range_float'; range.step = isFloat ? 0.01 : 1; range.value = val; range.style.cssText = 'flex:1';
        const num = document.createElement('span'); num.style.cssText = 'font-size:12px;color:var(--accent);min-width:40px;text-align:right';
        num.textContent = isFloat ? parseFloat(val).toFixed(2) : val;
        range.oninput = () => { num.textContent = isFloat ? parseFloat(range.value).toFixed(2) : range.value; };
        range.onchange = () => updateSetting(key, isFloat ? parseFloat(range.value) : parseInt(range.value));
        wrap.appendChild(range); wrap.appendChild(num);
        return wrap;
    }
    if (type === 'text') {
        const inp = document.createElement('input'); inp.type = 'text'; inp.value = val || '';
        inp.style.cssText = 'flex:1;max-width:200px;background:var(--bg);border:1px solid var(--border);color:var(--text);padding:4px 8px;border-radius:4px;font-size:12px';
        inp.onchange = () => updateSetting(key, inp.value);
        return inp;
    }
    return null;
}

// ==================== SETTINGS & MODE TABS ====================
async function loadSettings() {
    try {
        S = await (await fetch('/api/settings')).json();
        renderAllSettings();
        settingsLoaded = true;
        refreshPresetList();
        farmCardInitialized = false;
        initFarmCard();
    } catch (e) {
        $('settingsContainer').innerHTML = `<div class="empty-state">Failed to load: ${e.message}</div>`;
    }
}

const HEIST_REWARDS = [['heist.rewardCurrency', 'Currency'], ['heist.rewardArmour', 'Armour'], ['heist.rewardWeapons', 'Weapons'], ['heist.rewardGems', 'Gems'], ['heist.rewardDivinationCards', 'Div Cards'], ['heist.rewardUniques', 'Uniques'], ['heist.rewardJewellery', 'Jewellery'], ['heist.rewardEssences', 'Essences'], ['heist.rewardFragments', 'Fragments'], ['heist.rewardMaps', 'Maps'], ['heist.rewardJewels', 'Jewels'], ['heist.rewardCorrupted', 'Corrupted']];
const TOWER_TYPES = ['chilling', 'fireball', 'empowering', 'seismic', 'minion', 'shockNova'];

const MODE_SETTINGS = {
    'Wave Farming': { sections: ['farming'], mechanics: ['interactables', 'ultimatum', 'harvest', 'wishes', 'essence', 'ritual', 'eldritchAltar'] },
    Simulacrum: { sections: ['simulacrum'], mechanics: [] },
    Blight: { sections: ['blight'], mechanics: [] },
    Heist: { sections: ['heist'], mechanics: [] },
    Labyrinth: { sections: ['labyrinth'], mechanics: [] },
    Boss: { sections: ['boss'], mechanics: [] },
    Follower: { sections: ['follower'], mechanics: [] },
};

function switchSettingsTab(name) {
    activeSettingsTab = name;
    document.querySelectorAll('#settingsTabBar .settings-tab').forEach(t => {
        const tabName = t.getAttribute('data-tab') || t.textContent.toLowerCase().replace(/\s+/g, '');
        t.classList.toggle('active', tabName === name);
    });
    document.querySelectorAll('.settings-tab-content').forEach(c => c.classList.remove('active'));
    const el = $('stab-' + name);
    if (el) {
        el.classList.add('active');
        if (name === 'mode' && settingsLoaded) { el.innerHTML = ''; renderModeTab(el, currentBotMode); }
    }
}

function renderModeTab(container, modeName) {
    const cfg = MODE_SETTINGS[modeName];
    if (!cfg) { container.innerHTML = '<div class="empty-state">Select a farming mode to see its settings</div>'; return; }

    for (const section of cfg.sections) {
        if (section === 'farming') {
            addSection(container, 'Farming', [
                ['farming.mapName'], 
                ['farming.farmStrategy'],
                ['farming.maxRemainingMonsters', '0 = disabled (clears full map)'],
                ['farming.minCoverage'],
                ['farming.minPackDensity'],
                ['farming.detourForRares'],
                ['farming.maxDetourDistance']
            ]);
            addSection(container, 'Map Rolling (shared)', [['mapRolling.minMapTier'], ['mapRolling.dangerousMapMods'], ['mapRolling.minMapQuantity']]);
            addSection(container, 'Map Device Slots (shared)', [['mapDevice.slot1'], ['mapDevice.slot2'], ['mapDevice.slot3'], ['mapDevice.slot4'], ['mapDevice.slot5']]);
            addSection(container, 'Run (shared)', [['run.portalKey'], ['run.maxDeaths'], ['run.stashItemThreshold'], ['run.lootSweepTimeoutSeconds']]);
            addSection(container, 'Stash (shared)', [['stash.dumpTabName'], ['stash.mappingSuppliesTabName']]);
        } else if (section === 'simulacrum') {
            addSection(container, 'Simulacrum', [['simulacrum.simulacrumStock'], ['simulacrum.minWaveDelaySeconds'], ['simulacrum.waveTimeoutMinutes']]);
            addSection(container, 'Run (shared)', [['run.maxDeaths'], ['run.stashItemThreshold'], ['run.lootSweepTimeoutSeconds'], ['run.portalKey']]);
            addSection(container, 'Stash (shared)', [['stash.dumpTabName'], ['stash.fragmentTabName']]);
        } else if (section === 'blight') {
            const blightDiv = document.createElement('div');
            blightDiv.innerHTML = '<div class="section-divider">Blight Maps & Stash</div>';
            addFieldsTo(blightDiv, [
                ['blight.runBlightRavaged', 'Run Blight-Ravaged maps instead of standard Blighted maps'],
                ['blight.blightMapTabName', 'Stash tab to pull Blighted maps from when out of maps'],
                ['blight.blightMapStock', 'Number of Blighted maps to maintain in inventory'],
                ['stash.dumpTabName', 'Stash tab for dumping loot']
            ]);
            const encounterDivider = document.createElement('div'); 
            encounterDivider.className = 'section-divider'; 
            encounterDivider.textContent = 'Encounter & Defense'; 
            blightDiv.appendChild(encounterDivider);
            addFieldsTo(blightDiv, [
                ['blight.standAtTower'],
                ['blight.dontBuildTowers'],
                ['blight.ignoreCurrency'],
                ['blight.towerBuildRadius'],
                ['blight.towerBuildCooldownMs'],
                ['blight.towerClickCooldownMs'],
                ['blight.towerApproachDistance'],
                ['blight.sweepDelayAfterTimerSeconds'],
                ['blight.sweepTimeoutSeconds'],
                ['blight.sweepPumpReturnSeconds'],
                ['blight.sweepPumpRadius']
            ]);
            const towerLabel = document.createElement('div'); 
            towerLabel.className = 'section-divider'; 
            towerLabel.textContent = 'Tower Priorities'; 
            blightDiv.appendChild(towerLabel);
            blightDiv.appendChild(renderTowerTable());
            container.appendChild(blightDiv);
        } else if (section === 'heist') {
            const heistDiv = document.createElement('div');
            heistDiv.innerHTML = '<div class="section-divider">Heist</div>';
            addFieldsTo(heistDiv, [['heist.alertThreshold'], ['heist.maxChestDetour'], ['heist.openRewardChests'], ['heist.companionWaitTimeout'], ['heist.companionRetryDelay']]);
            const rewardLabel = document.createElement('div'); rewardLabel.className = 'section-divider'; rewardLabel.textContent = 'Reward Types'; heistDiv.appendChild(rewardLabel);
            const grid = document.createElement('div'); grid.className = 'check-grid';
            for (const [key, label] of HEIST_REWARDS) {
                const entry = S[key]; if (!entry) continue;
                grid.innerHTML += `<div class="check-item"><input type="checkbox" ${entry.value ? 'checked' : ''} onchange="updateSetting('${key}',this.checked)"><label>${esc(label)}</label></div>`;
            }
            heistDiv.appendChild(grid);
            container.appendChild(heistDiv);
        } else if (section === 'labyrinth') {
            addSection(container, 'Labyrinth', [['labyrinth.difficulty'], ['labyrinth.maxRuns'], ['labyrinth.minExpectedProfit'], ['labyrinth.keepThreshold'], ['labyrinth.preferSameType'], ['labyrinth.openRewardChests'], ['labyrinth.zoneTimeoutSeconds'], ['labyrinth.izaroTimeoutSeconds'], ['labyrinth.settleSeconds']]);
            addSection(container, 'Run (shared)', [['run.maxDeaths']]);
        } else if (section === 'boss') {
            addSection(container, 'Boss', [['boss.bossType'], ['boss.fragmentStock'], ['boss.keyDropChaosValue']]);
            addSection(container, 'Run (shared)', [['run.maxDeaths'], ['run.lootSweepTimeoutSeconds'], ['run.stashItemThreshold'], ['run.portalKey']]);
            addSection(container, 'Stash (shared)', [['stash.dumpTabName'], ['stash.fragmentTabName']]);
        } else if (section === 'follower') {
            addSection(container, 'Follower', [['follower.leaderName'], ['follower.followDistance'], ['follower.stopDistance'], ['follower.followThroughTransitions'], ['follower.enableCombat'], ['follower.enableLoot'], ['follower.lootNearLeaderOnly']]);
        }
    }

    for (const mech of cfg.mechanics) renderMechanicSection(container, mech);

    const link = document.createElement('div'); link.style.cssText = 'text-align:center;padding:16px;';
    link.innerHTML = '<a href="#" style="color:var(--accent);font-size:13px" onclick="event.preventDefault();switchSettingsTab(\'all\')">View all settings &rarr;</a>';
    container.appendChild(link);
}

function renderMechanicSection(container, mech) {
    if (mech === 'interactables') {
        addSection(container, 'Interactables', [['mechanics.interactables.shrines'], ['mechanics.interactables.strongboxes'], ['mechanics.interactables.djinnCaches'], ['mechanics.interactables.heistCaches'], ['mechanics.interactables.craftingRecipes'], ['mechanics.interactables.memoryTears']]);
    } else if (mech === 'ultimatum') {
        addSection(container, 'Ultimatum', [['mechanics.ultimatum.mode'], ['mechanics.ultimatum.exitAfter'], ['mechanics.ultimatum.doSurvive'], ['mechanics.ultimatum.doKillEnemies'], ['mechanics.ultimatum.doDefendAltar'], ['mechanics.ultimatum.doStandInCircles'], ['mechanics.ultimatum.maxWaves'], ['mechanics.ultimatum.dangerThreshold'], ['mechanics.ultimatum.minSecureValue'], ['mechanics.ultimatum.orbitRadius']]);
        renderUltimatumModsExpander(container);
    } else if (mech === 'harvest') {
        addSection(container, 'Harvest', [['mechanics.harvest.mode'], ['mechanics.harvest.exitAfter'], ['mechanics.harvest.preferredColour'], ['mechanics.harvest.colourPreferenceBonus'], ['mechanics.harvest.normalWeight'], ['mechanics.harvest.magicWeight'], ['mechanics.harvest.rareWeight'], ['mechanics.harvest.wildMultiplier'], ['mechanics.harvest.vividMultiplier'], ['mechanics.harvest.primalMultiplier'], ['mechanics.harvest.lootSweepSeconds']]);
    } else if (mech === 'wishes') {
        addSection(container, 'Wishes', [['mechanics.wishes.mode'], ['mechanics.wishes.exitAfter'], ['mechanics.wishes.preferredWish'], ['mechanics.wishes.lootSweepSeconds']]);
    } else if (mech === 'essence') {
        addSection(container, 'Essence', [['mechanics.essence.mode'], ['mechanics.essence.exitAfter'], ['mechanics.essence.minEssenceTier'], ['mechanics.essence.corruptEssences'], ['mechanics.essence.lootSweepSeconds']]);
    } else if (mech === 'ritual') {
        addSection(container, 'Ritual', [['mechanics.ritual.mode'], ['mechanics.ritual.exitAfter'], ['mechanics.ritual.lootSweepSeconds']]);
    } else if (mech === 'eldritchAltar') {
        addSection(container, 'Eldritch Altars', [['mechanics.eldritchAltar.enabled'], ['mechanics.eldritchAltar.minScoreThreshold']]);
        renderAltarModsExpander(container);
    }
}

function renderAllSettings() {
    const c = $('settingsContainer'); c.innerHTML = '';
    $('modeTabLabel').textContent = currentBotMode === 'Idle' ? 'Mode' : currentBotMode;

    const modeTab = makeTabContent('mode', activeSettingsTab === 'mode');
    renderModeTab(modeTab, currentBotMode);
    c.appendChild(modeTab);

    const build = makeTabContent('build', activeSettingsTab === 'build');
    addSection(build, 'Movement', [
        ['build.blinkRange'],
        ['build.dashMinDistance', '0 = disable dash-for-speed'],
        ['build.pathMergeThreshold', '0 = disabled'],
        ['build.enablePeriodicReposition'],
        { key: 'build.periodicRepositionIntervalMs', showIf: 'build.enablePeriodicReposition' },
        { key: 'build.periodicRepositionDistance', showIf: 'build.enablePeriodicReposition' }
    ]);
    addSection(build, 'Combat', [['build.alwaysAttack'], ['build.defaultPositioning'], ['build.fightRange'], ['build.combatRange'], ['build.guardHpThreshold'], ['build.guardEsThreshold'], ['build.vaalMinMonsters'], ['build.summonExpectedCount']]);
    build.appendChild(buildEnemyBlacklist());
    build.appendChild(renderSkillsSection());
    addSection(build, 'Flasks', [['build.flasksEnabled'], ['build.lifeFlaskSlot', '0 = disabled'], ['build.lifeFlaskHpThreshold'], ['build.manaFlaskSlot', '0 = disabled'], ['build.manaFlaskManaThreshold'], ['build.utilityFlaskIntervalMs']]);
    c.appendChild(build);

    const loot = makeTabContent('loot', activeSettingsTab === 'loot');
    addSection(loot, 'Unique Filtering', [['loot.skipLowValueUniques'], { key: 'loot.minUniqueChaosValue', showIf: 'loot.skipLowValueUniques' }, { key: 'loot.minChaosPerSlot', note: '0 = disabled' }]);
    loot.appendChild(buildMustLootUniques());
    addSection(loot, 'Cluster Jewels', [['loot.filterClusterJewels'], { key: 'loot.minClusterJewelChaosValue', showIf: 'loot.filterClusterJewels' }]);
    addSection(loot, 'Skill Gems', [['loot.filterSkillGems'], { key: 'loot.minGemChaosValue', showIf: 'loot.filterSkillGems' }, { key: 'loot.alwaysLoot20QualityGems', showIf: 'loot.filterSkillGems' }]);
    addSection(loot, 'Synthesised Items', [['loot.filterSynthesisedItems']]);
    loot.appendChild(buildTagListEditor('loot.synthesisedWhitelist', 'Implicit Whitelist', 'Add implicit mod substring (e.g. "Onslaught")', 'loot.filterSynthesisedItems'));
    addSection(loot, 'Other', [['loot.ignoreQuestItems'], ['loot.labelToggleUnstick'], ['loot.labelToggleCooldownSeconds'], ['loot.stashItemCooldownMs']]);
    c.appendChild(loot);

    const all = makeTabContent('all', activeSettingsTab === 'all');
    all.innerHTML = '<div class="section-divider" style="font-size:14px;font-weight:700">All Modes</div>';
    for (const modeName of Object.keys(MODE_SETTINGS)) {
        renderModeTab(all, modeName);
        all.querySelectorAll('a[onclick*="switchSettingsTab"]').forEach(l => l.closest('div[style*="text-align:center"]')?.remove());
    }
    c.appendChild(all);

    const adv = makeTabContent('advanced', activeSettingsTab === 'advanced');
    addSection(adv, 'General', [['actionCooldownMs'], ['extraLatencyMs'], ['maxClickAttempts'], ['interactRadius'], ['areaSettleSeconds'], ['autoLevelGems'], ['autoApplyIncubators'], ['debugIncubatorOverlay']]);
    addSection(adv, 'Threat Detection', [['threat.enabled'], ['threat.monitorRares'], ['threat.autoDodge'], ['threat.threatRadius'], ['threat.dodgeTriggerDistance'], ['threat.dodgeDistance'], ['threat.dodgeMinProgress'], ['threat.dodgeMaxProgress'], ['threat.dodgeCooldownMs']]);
    addSection(adv, 'Web UI', [['webUiEnabled'], ['webUiPort']]);
    addSection(adv, 'Hotkeys', [['toggleRunning'], ['testMapExplore'], ['dumpGameState'], ['dumpRecording']]);
    c.appendChild(adv);

    switchSettingsTab(activeSettingsTab);
}

function makeTabContent(id, active) {
    const d = document.createElement('div');
    d.id = 'stab-' + id; d.className = 'settings-tab-content' + (active ? ' active' : '');
    return d;
}

function addSection(parent, title, fieldDefs) {
    const div = document.createElement('div');
    div.innerHTML = `<div class="section-divider">${title}</div>`;
    addFieldsTo(div, fieldDefs);
    parent.appendChild(div);
}

function addFieldsTo(parent, fieldDefs) {
    for (const f of fieldDefs) {
        const fd = Array.isArray(f) ? { key: f[0], note: f[1] } : f;
        const entry = S[fd.key]; if (!entry) continue;
        const row = buildField(fd.key, entry, fd.note);
        if (fd.showIf) { toggle(row, S[fd.showIf]?.value); row.dataset.showIf = fd.showIf; }
        parent.appendChild(row);
    }
}

function getUnitSuffix(label) {
    if (!label) return '';
    const match = label.match(/\(([^)]+)\)/);
    if (match) return match[1];
    const l = label.toLowerCase().trim();
    if (l.endsWith(' ms') || l.endsWith('ms')) return 'ms';
    if (l.endsWith(' seconds') || l.endsWith(' second') || l.endsWith(' sec') || l.endsWith('s')) return 's';
    if (l.includes('radius') || l.includes('distance')) return 'grid';
    return '';
}

function buildField(key, entry, note) {
    const row = document.createElement('div'); row.className = 'setting-row'; row.dataset.key = key;
    let input = '';
    const unitStr = getUnitSuffix(entry.label);

    switch (entry.type) {
        case 'toggle':
            input = `<label class="switch"><input type="checkbox" ${entry.value ? 'checked' : ''} onchange="updateSetting('${key}',this.checked)"><span class="slider"></span></label>`;
            break;
        case 'range_int':
        case 'range_float': {
            const v = entry.value, mn = entry.min, mx = entry.max, isFloat = entry.type === 'range_float';
            const step = isFloat ? ((mx - mn) <= 2 ? '0.01' : '0.1') : '1';
            const valFmt = isFloat ? parseFloat(v).toFixed(1) : v;
            input = `<div class="range-control">
        <input type="range" class="range-slider" min="${mn}" max="${mx}" step="${step}" value="${v}" oninput="this.parentNode.querySelector('.range-number').value=${isFloat ? 'parseFloat(this.value).toFixed(1)' : 'this.value'}" onchange="updateSetting('${key}',${isFloat ? 'parseFloat' : 'parseInt'}(this.value))">
        <input type="number" class="range-number" min="${mn}" max="${mx}" step="${step}" value="${valFmt}" oninput="this.parentNode.querySelector('.range-slider').value=this.value" onchange="updateSetting('${key}',${isFloat ? 'parseFloat' : 'parseInt'}(this.value))">
        ${unitStr ? `<span class="range-suffix">${esc(unitStr)}</span>` : ''}
      </div>`;
            break;
        }
        case 'list': {
            const opts = (entry.options || []).map(o => `<option value="${esc(o)}" ${o === entry.value ? 'selected' : ''}>${esc(o)}</option>`).join('');
            input = opts ? `<select onchange="updateSetting('${key}',this.value)">${opts}</select>` : `<input type="text" value="${esc(entry.value || '')}" onchange="updateSetting('${key}',this.value)">`;
            break;
        }
        case 'text':
            if (key.endsWith('.buffDebuffName')) {
                input = `<div style="display:flex;gap:4px;align-items:center;flex:1"><input type="text" value="${esc(entry.value || '')}" onchange="updateSetting('${key}',this.value)" style="flex:1" id="input-${key.replace(/\./g, '-')}"><button onclick="scanPlayerBuffs('${key}')" style="white-space:nowrap">Scan Buffs</button></div>`;
            } else if (entry.description && entry.description.includes('Set Current Pos')) {
                input = `<div style="display:flex;gap:4px;align-items:center;flex:1"><input type="text" value="${esc(entry.value || '')}" onchange="updateSetting('${key}',this.value)" style="flex:1" id="input-${key.replace(/\./g, '-')}"><button onclick="capturePosition('${key}')" style="white-space:nowrap">Set Current Pos</button></div>`;
            } else if (key === 'notifications.discordWebhookUrl') {
                input = `<div style="display:flex;gap:4px;align-items:center;flex:1"><input type="password" value="${esc(entry.value || '')}" onchange="updateSetting('${key}',this.value)" style="flex:1" id="input-${key.replace(/\./g, '-')}"><button onclick="testDiscordWebhook(this)" style="white-space:nowrap">Test</button></div>`;
            } else {
                input = `<input type="text" value="${esc(entry.value || '')}" onchange="updateSetting('${key}',this.value)">`;
            }
            break;
        case 'hotkey': {
            const commonKeys = ['None', 'F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11', 'F12', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 'D0', 'D1', 'D2', 'D3', 'D4', 'D5', 'D6', 'D7', 'D8', 'D9', 'Insert', 'Delete', 'Home', 'End', 'PageUp', 'PageDown', 'Up', 'Down', 'Left', 'Right', 'Space', 'Tab', 'Escape', 'OemTilde', 'OemMinus', 'OemPlus', 'OemOpenBrackets', 'OemCloseBrackets', 'OemPipe', 'OemSemicolon', 'OemQuotes', 'OemComma', 'OemPeriod', 'OemQuestion'];
            const curVal = entry.value || '';
            const opts = commonKeys.map(k => `<option value="${k}" ${k === curVal ? 'selected' : ''}>${k}</option>`).join('');
            input = `<select onchange="updateSetting('${key}',this.value)">${commonKeys.includes(curVal) ? '' : `<option value="${esc(curVal)}" selected>${esc(curVal)}</option>`}${opts}</select>`;
            break;
        }
        default:
            input = `<span class="hotkey-badge">${esc(String(entry.value || ''))}</span>`;
    }
    const desc = entry.description ? `<span class="setting-desc">${esc(entry.description)}</span>` : '';
    const noteHtml = note ? `<span class="setting-desc" style="color:var(--accent)">${esc(note)}</span>` : '';
    row.innerHTML = `<div class="setting-info"><span class="setting-label">${esc(entry.label)}</span>${desc}${noteHtml}</div><div class="setting-control">${input}</div>`;
    return row;
}

async function updateSetting(key, value) {
    if (S[key]) S[key].value = value;
    try {
        const r = await fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ key, value }) });
        const data = await r.json();
        if (data.ok) { flashSave(); toggle(`[data-show-if="${key}"]`, value); }
    } catch { }
}

function flashSave() {
    const el = $('saveFlash'); el.classList.add('show');
    setTimeout(() => el.classList.remove('show'), 1500);
}

// ==================== SEARCH & PROFILES ====================
let searchTimer = null;
function onSettingsSearch(query) {
    clearTimeout(searchTimer);
    toggle('searchClear', !!query);
    if (!query || query.length < 2) { clearSettingsSearch(); return; }
    if (!settingsLoaded) { loadSettings().then(() => onSettingsSearch(query)); return; }

    searchTimer = setTimeout(() => {
        const results = $('settingsSearchResults'), q = query.toLowerCase(), matches = [], groups = {};
        for (const [key, entry] of Object.entries(S)) {
            if ((entry.label || '').toLowerCase().includes(q) || (entry.description || '').toLowerCase().includes(q) || key.toLowerCase().includes(q))
                matches.push({ key, entry });
        }

        results.innerHTML = '';
        if (matches.length === 0) {
            results.innerHTML = '<div class="empty-state">No settings match your search</div>';
        } else {
            results.innerHTML = `<div style="font-size:12px;color:var(--text-dim);margin-bottom:12px">${matches.length} result${matches.length !== 1 ? 's' : ''}</div>`;
            for (const m of matches) {
                const group = m.key.split('.')[0], label = group.charAt(0).toUpperCase() + group.slice(1);
                if (!groups[label]) groups[label] = [];
                groups[label].push(m);
            }
            for (const [groupName, items] of Object.entries(groups)) {
                const section = document.createElement('div');
                section.className = 'settings-group';
                section.innerHTML = `<div class="section-divider">${groupName}</div>`;
                for (const { key, entry } of items) section.appendChild(buildField(key, entry));
                results.appendChild(section);
            }
        }
        toggle('settingsTabBar', false); toggle('settingsContainer', false); toggle(results, true);
    }, 150);
}

function clearSettingsSearch(clearInput) {
    if (clearInput) $('settingsSearch').value = '';
    toggle('searchClear', false); toggle('settingsSearchResults', false);
    toggle('settingsContainer', true); toggle('settingsTabBar', true);
}

let activeProfile = '';
async function refreshPresetList() {
    try {
        const data = await (await fetch('/api/profiles')).json();
        activeProfile = data.active || '';
        const sel = $('profileSelect'); sel.innerHTML = '';
        for (const name of (data.profiles || [])) {
            const opt = document.createElement('option');
            opt.value = name; opt.textContent = name; opt.selected = (name === activeProfile);
            sel.appendChild(opt);
        }
        $('profileActiveBadge').textContent = activeProfile ? 'Active' : '—';
    } catch { }
}

async function switchProfile(name) {
    if (!name || name === activeProfile) return;
    try {
        const r = await fetch('/api/profiles/switch', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
        if (!r.ok) return;
        activeProfile = name; flashSave(); settingsLoaded = false; await loadSettings();
    } catch { }
}

async function createProfile() {
    const name = prompt('New profile name:'); if (!name || !name.trim()) return;
    const switchTo = confirm('Switch to the new profile?');
    try {
        const r = await fetch('/api/profiles/create', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: name.trim(), switchTo }) });
        if (r.ok) { flashSave(); await refreshPresetList(); if (switchTo) { settingsLoaded = false; await loadSettings(); } }
    } catch { }
}

async function renameProfile() {
    const oldName = $('profileSelect').value; if (!oldName) return;
    const newName = prompt(`Rename "${oldName}" to:`, oldName);
    if (!newName || !newName.trim() || newName.trim() === oldName) return;
    try {
        if ((await fetch('/api/profiles/rename', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ from: oldName, to: newName.trim() }) })).ok)
            await refreshPresetList();
    } catch { }
}

async function deleteProfile() {
    const name = $('profileSelect').value; if (!name) return;
    if (name === activeProfile) { alert('Cannot delete the active profile.'); return; }
    if (!confirm(`Delete profile "${name}"?`)) return;
    try {
        if ((await fetch('/api/profiles/delete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) })).ok)
            await refreshPresetList();
    } catch { }
}

async function exportProfile() {
    const name = $('profileSelect').value; if (!name) return;
    try {
        const blob = await (await fetch(`/api/profiles/export?name=${encodeURIComponent(name)}`)).blob();
        const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name + '.json'; a.click();
    } catch { }
}

function importProfile() { $('presetFileInput').click(); }
async function handlePresetFile(event) {
    const file = event.target.files[0]; if (!file) return;
    event.target.value = '';
    const name = prompt('Profile name:', file.name.replace(/\.json$/i, '')); if (!name || !name.trim()) return;
    try {
        const json = await file.text(); JSON.parse(json);
        if ((await fetch('/api/profiles/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: name.trim(), json }) })).ok) {
            flashSave(); await refreshPresetList();
        }
    } catch { }
}

async function switchHistory(type, el) {
    document.querySelectorAll('.history-tab').forEach(t => t.classList.remove('active'));
    if (el) el.classList.add('active');
    await loadHistory(type);
}

async function loadHistory(type) {
    const container = $('historyContent');
    try {
        const data = await (await fetch(`/api/history/${type}?limit=100`)).json();
        if (!data || data.length === 0) { container.innerHTML = '<div class="empty-state">No data yet</div>'; return; }
        if (type === 'loot') renderLootTable(data, container);
        else if (type === 'runs') renderRunsTable(data, container);
        else if (type === 'events') renderEventsTable(data, container);
    } catch (e) { container.innerHTML = `<div class="empty-state">Error: ${e.message}</div>`; }
}

function renderLootTable(data, c) {
    c.innerHTML = '<table><thead><tr><th>Time</th><th>Item</th><th>Value</th><th>Area</th><th>Mode</th></tr></thead><tbody>' +
        data.map(r => `<tr><td>${esc(new Date(r.time).toLocaleString())}</td><td>${esc(r.itemName)}</td><td class="yellow">${r.chaosValue > 0 ? r.chaosValue.toFixed(1) + 'c' : '-'}</td><td>${esc(r.area || '')}</td><td>${esc(r.mode || '')}</td></tr>`).join('') +
        '</tbody></table>';
}

function renderRunsTable(data, c) {
    c.innerHTML = '<table><thead><tr><th>Start</th><th>Mode</th><th>Area</th><th>Duration</th><th>Wave</th><th>Deaths</th><th>Chaos</th><th>Items</th><th>Result</th></tr></thead><tbody>' +
        data.map(r => {
            const dur = r.endTime ? `${Math.floor((new Date(r.endTime) - new Date(r.startTime)) / 60000)}m ${Math.floor(((new Date(r.endTime) - new Date(r.startTime)) % 60000) / 1000)}s` : '-';
            const res = r.completed ? '<span class="green">Done</span>' : (r.endTime ? '<span class="red">Abandoned</span>' : '<span class="yellow">Active</span>');
            return `<tr><td>${esc(new Date(r.startTime).toLocaleString())}</td><td>${esc(r.mode)}</td><td>${esc(r.area || '')}</td><td>${dur}</td><td>${r.highestWave || '-'}</td><td>${r.deaths}</td><td class="yellow">${r.totalChaos.toFixed(1)}c</td><td>${r.itemsLooted}</td><td>${res}</td></tr>`;
        }).join('') + '</tbody></table>';
}

function renderEventsTable(data, c) {
    c.innerHTML = '<table><thead><tr><th>Time</th><th>Type</th><th>Message</th></tr></thead><tbody>' +
        data.map(r => `<tr><td>${esc(new Date(r.time).toLocaleString())}</td><td>${esc(r.type)}</td><td>${esc(r.message)}</td></tr>`).join('') +
        '</tbody></table>';
}

// ==================== MAP ENGINE ====================
const CAM_ANGLE = 38.7 * Math.PI / 180, CAM_COS = Math.cos(CAM_ANGLE), CAM_SIN = Math.sin(CAM_ANGLE), PI_2 = Math.PI * 2;
let mapTerrain = null, mapTerrainCanvas = null, mapLastAreaHash = 0, mapLastStatus = null, minimapScale = 1.2;

const TC = { 0: [10, 10, 15], 1: [25, 28, 35], 2: [30, 33, 42], 3: [35, 38, 48], 4: [40, 44, 55], 5: [45, 50, 62], 6: [15, 30, 50], 8: [10, 10, 15], 9: [50, 55, 70], 10: [60, 65, 80], 11: [70, 76, 92], 12: [82, 88, 105], 13: [95, 102, 120], 14: [30, 58, 95] };

function isoX(dx, dy) { return (dx - dy) * CAM_COS; }
function isoY(dx, dy) { return -(dx + dy) * CAM_SIN; }

async function loadTerrain() {
    try {
        const d = await (await fetch('/api/map/terrain')).json();
        if (d.error) return;
        const raw = atob(d.data), w = d.width, h = d.height;
        mapTerrainCanvas = document.createElement('canvas'); mapTerrainCanvas.width = w; mapTerrainCanvas.height = h;
        const tctx = mapTerrainCanvas.getContext('2d'), imgData = tctx.createImageData(w, h);
        for (let i = 0; i < raw.length; i++) {
            const c = TC[raw.charCodeAt(i)] || TC[0];
            imgData.data[i * 4] = c[0]; imgData.data[i * 4 + 1] = c[1]; imgData.data[i * 4 + 2] = c[2]; imgData.data[i * 4 + 3] = 255;
        }
        tctx.putImageData(imgData, 0, 0);
        mapTerrain = { width: w, height: h, originX: d.originX, originY: d.originY, areaHash: d.areaHash };
        mapLastAreaHash = d.areaHash;
    } catch { }
}

function drawIsoMap(canvas, scale, panX, panY, showBubble, infoEl) {
    const ctx = canvas.getContext('2d'), cw = canvas.clientWidth, ch = canvas.clientHeight;
    if (cw === 0 || ch === 0) return;
    canvas.width = cw; canvas.height = ch;
    ctx.fillStyle = '#0a0a0f'; ctx.fillRect(0, 0, cw, ch);

    if (!mapTerrainCanvas || !mapTerrain || !mapLastStatus) return;
    const s = mapLastStatus, ox = mapTerrain.originX, oy = mapTerrain.originY, ptx = s.playerGridX - ox, pty = s.playerGridY - oy;

    ctx.save();
    ctx.translate(cw / 2 + panX, ch / 2 + panY);
    ctx.scale(scale, scale);

    ctx.save();
    ctx.transform(CAM_COS, -CAM_SIN, -CAM_COS, -CAM_SIN, 0, 0);
    ctx.translate(-ptx, -pty);
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(mapTerrainCanvas, 0, 0);
    ctx.restore();

    function toIso(gx, gy) { return [isoX(gx - s.playerGridX, gy - s.playerGridY), isoY(gx - s.playerGridX, gy - s.playerGridY)]; }

    if (s.navPath && s.navPath.length > 1) {
        ctx.lineWidth = 2 / scale;
        for (let i = 0; i < s.navPath.length - 1; i++) {
            const [x1, y1] = toIso(s.navPath[i][0], s.navPath[i][1]), [x2, y2] = toIso(s.navPath[i + 1][0], s.navPath[i + 1][1]);
            const isBlink = s.navPath[i + 1][2] > 0;
            ctx.strokeStyle = isBlink ? '#e879f9' : '#fb923c';
            ctx.setLineDash(isBlink ? [4 / scale, 4 / scale] : []);
            ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke();
        }
        ctx.setLineDash([]);
    }

    if (s.entities) {
        for (const e of s.entities) {
            const [ex, ey] = toIso(e.x, e.y);
            let color, sz;
            switch (e.t) {
                case 'm': sz = (e.r === 'u' ? 5 : e.r === 'r' ? 4 : e.r === 'm' ? 3 : 2) / scale; color = e.r === 'u' ? '#c084fc' : e.r === 'r' ? '#fbbf24' : e.r === 'm' ? '#60a5fa' : '#f87171'; break;
                case 'c': color = '#fbbf24'; sz = 3 / scale; break;
                case 'a': color = '#22d3ee'; sz = 4 / scale; break;
                case 'o': color = '#60a5fa'; sz = 4 / scale; break;
                case 's': color = '#c084fc'; sz = 4 / scale; break;
                case 'n': color = '#f472b6'; sz = 5 / scale; break;
                case 'p': color = '#86efac'; sz = 4 / scale; break;
                default: continue;
            }
            ctx.fillStyle = color; ctx.beginPath(); ctx.arc(ex, ey, sz, 0, PI_2); ctx.fill();
            if (e.r === 'u' || e.r === 'r') { ctx.strokeStyle = color; ctx.lineWidth = 1 / scale; ctx.beginPath(); ctx.arc(ex, ey, sz + 2 / scale, 0, PI_2); ctx.stroke(); }
        }
    }

    if (showBubble) {
        ctx.strokeStyle = 'rgba(74, 222, 128, 0.15)'; ctx.lineWidth = 1 / scale;
        ctx.beginPath(); ctx.ellipse(0, 0, 180 * CAM_COS, 180 * CAM_SIN, 0, 0, PI_2); ctx.stroke();
    }

    const ps = 5 / scale;
    ctx.fillStyle = '#4ade80'; ctx.beginPath(); ctx.arc(0, 0, ps, 0, PI_2); ctx.fill();
    ctx.strokeStyle = '#fff'; ctx.lineWidth = 2 / scale; ctx.beginPath(); ctx.arc(0, 0, ps, 0, PI_2); ctx.stroke();

    ctx.restore();

    if (infoEl) {
        infoEl.textContent = `Zoom: ${scale.toFixed(1)}x | Monsters: ${(s.entities || []).filter(e => e.t === 'm').length} | (${s.playerGridX.toFixed(0)}, ${s.playerGridY.toFixed(0)})`;
    }
}

function renderMinimap() { drawIsoMap($('minimapCanvas'), minimapScale, 0, 0, true, $('minimapInfo')); }

$('minimapCanvas').addEventListener('wheel', e => {
    e.preventDefault();
    minimapScale = Math.max(0.3, Math.min(6, minimapScale * (e.deltaY < 0 ? 1.15 : 0.87)));
    renderMinimap();
}, { passive: false });

const origUpdateDashboard = updateDashboard;
updateDashboard = function (s) {
    origUpdateDashboard(s);
    mapLastStatus = s;
    if (s.areaHash && s.areaHash !== mapLastAreaHash) loadTerrain();
    renderMinimap();

    if (s.skillBar && s.skillBar.length > 0) {
        const newKeys = s.skillBar.map(d => d.slotIndex + ':' + d.skillName).join(',');
        const oldKeys = detectedSkillBar.map(d => d.slotIndex + ':' + d.skillName).join(',');
        if (newKeys !== oldKeys) {
            detectedSkillBar = s.skillBar;
            if (settingsLoaded && activeSettingsTab === 'build') renderAllSettings();
        }
    }
};