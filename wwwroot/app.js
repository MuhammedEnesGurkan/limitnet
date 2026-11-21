'use strict';

const $ = s => document.querySelector(s);
const nf = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 1 });
const regions = new Intl.DisplayNames(['tr'], { type: 'region' });
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);
const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];
function split(b) {
  let i = 0;
  while (b >= 1024 && i < UNITS.length - 1) { b /= 1024; i++; }
  return [nf.format(b), UNITS[i]];
}
const size = b => split(b).join(' ');
const rate = b => b > 0 ? size(b) + '/s' : '—';
const countryName = c => {
  if (!c.cc) return c.country || '—';
  try { return regions.of(c.cc); } catch { return c.cc; }
};

// Photino köprüsü; tarayıcıda geliştirirken mock.js kendi köprüsünü koyar
const bridge = window.akisBridge ?? {
  send: m => window.external.sendMessage(JSON.stringify(m)),
  on: fn => window.external.receiveMessage(s => fn(JSON.parse(s))),
};

const icons = {};
const expanded = new Set();
const rows = new Map();
let last = { apps: [], caps: {} };
let sort = { key: 'rate', dir: -1 };
let query = '';

/* ---------- akış nehri ---------- */
const N = 120;
const river = Array.from({ length: N }, () => ({ d: 0, u: 0 }));
let tickAt = 0;
const canvas = $('#river');
const ctx = canvas.getContext('2d');

function niceMax(v) {
  const m = Math.max(v, 64 * 1024);
  const p = 2 ** Math.ceil(Math.log2(m));
  return p * 0.75 >= m ? p * 0.75 : p;
}

function drawRiver(now) {
  const dpr = devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const css = getComputedStyle(document.documentElement);
  const cUp = css.getPropertyValue('--up').trim(), cDown = css.getPropertyValue('--down').trim();
  const cLine = css.getPropertyValue('--line').trim(), cMuted = css.getPropertyValue('--muted').trim();

  const mid = Math.round(h * 0.4) + 0.5;
  const maxU = niceMax(Math.max(...river.map(p => p.u)));
  const maxD = niceMax(Math.max(...river.map(p => p.d)));
  const step = w / (N - 2);
  // yeni örnek sağdan girip bir saniyede yerine kayar
  const slide = reduceMotion ? 0 : Math.max(0, 1 - (now - tickAt) / 1000);
  const x = i => w - (N - 1 - i) * step + slide * step;

  function band(val, max, dir, color) {
    const y = i => mid + dir * (val(river[i]) / max) * (dir < 0 ? mid - 8 : h - mid - 20);
    ctx.beginPath();
    ctx.moveTo(x(0), mid);
    ctx.lineTo(x(0), y(0));
    for (let i = 1; i < N; i++) {
      const xm = (x(i - 1) + x(i)) / 2;
      ctx.bezierCurveTo(xm, y(i - 1), xm, y(i), x(i), y(i));
    }
    ctx.lineTo(x(N - 1), mid);
    ctx.closePath();
    const g = ctx.createLinearGradient(0, mid, 0, dir < 0 ? 0 : h);
    g.addColorStop(0, color + '55');
    g.addColorStop(1, color + '0a');
    ctx.fillStyle = g;
    ctx.fill();
    ctx.beginPath();
    ctx.moveTo(x(0), y(0));
    for (let i = 1; i < N; i++) {
      const xm = (x(i - 1) + x(i)) / 2;
      ctx.bezierCurveTo(xm, y(i - 1), xm, y(i), x(i), y(i));
    }
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.5;
    ctx.stroke();
  }
  band(p => p.u, maxU, -1, cUp);
  band(p => p.d, maxD, 1, cDown);

  ctx.strokeStyle = cLine;
  ctx.lineWidth = 1;
  ctx.beginPath(); ctx.moveTo(0, mid); ctx.lineTo(w, mid); ctx.stroke();

  ctx.font = '11px ' + css.getPropertyValue('--mono');
  ctx.fillStyle = cMuted;
  ctx.textAlign = 'right';
  ctx.fillText(size(maxU) + '/s', w - 4, 12);
  ctx.fillText(size(maxD) + '/s', w - 4, h - 6);
  ctx.textAlign = 'left';
  ctx.fillText('2 dk önce', 4, h - 6);

  requestAnimationFrame(drawRiver);
}
requestAnimationFrame(drawRiver);

/* ---------- canlı uygulama listesi ---------- */
function meter(el, bytes) {
  const [n, u] = split(bytes);
  el.innerHTML = `${esc(n)}<small>${esc(u)}/s</small>`;
}

function avatar(app) {
  if (icons[app.id]) return `<img src="${icons[app.id]}" alt="">`;
  let hue = 0;
  for (const c of app.id) hue = (hue * 31 + c.charCodeAt(0)) % 360;
  return `<span style="color:#fff;background:hsl(${hue} 45% 42%);width:100%;height:100%;display:grid;place-items:center">${esc((app.name[0] || '?').toUpperCase())}</span>`;
}

const sortValue = {
  rate: a => a.down + a.up || (a.totDown + a.totUp) / 1e12,
  name: a => a.name.toLocaleLowerCase('tr'),
  down: a => a.down,
  up: a => a.up,
  total: a => a.totDown + a.totUp,
  conns: a => a.conns.length,
};

function matches(app) {
  if (!query) return true;
  const q = query.toLocaleLowerCase('tr');
  return [app.name, app.id, ...app.conns.flatMap(c => [c.host, c.remote, c.org, countryName(c)])]
    .some(s => s && s.toLocaleLowerCase('tr').includes(q));
}

function createRow(app) {
  const root = document.createElement('article');
  root.className = 'app';
  root.innerHTML = `
    <button class="app-main" aria-expanded="false">
      <span class="icon"></span>
      <span class="who"><span class="name"></span><span class="exe"></span></span>
      <span class="num rd"></span><span class="num ru"></span><span class="num tot"></span><span class="num nc"></span>
      <span class="chev" aria-hidden="true">›</span>
    </button>
    <div class="share"><i class="d"></i><i class="u"></i></div>
    <section class="detail" hidden></section>`;
  const r = {
    root, id: app.id,
    main: root.querySelector('.app-main'),
    icon: root.querySelector('.icon'),
    name: root.querySelector('.name'), exe: root.querySelector('.exe'),
    rd: root.querySelector('.rd'), ru: root.querySelector('.ru'),
    tot: root.querySelector('.tot'), nc: root.querySelector('.nc'),
    sd: root.querySelector('.share .d'), su: root.querySelector('.share .u'),
    detail: root.querySelector('.detail'),
    hasIcon: false, form: null, tbody: null,
  };
  r.main.addEventListener('click', () => toggle(r));
  rows.set(app.id, r);
  return r;
}

function toggle(r) {
  const open = !expanded.has(r.id);
  open ? expanded.add(r.id) : expanded.delete(r.id);
  r.root.classList.toggle('open', open);
  r.main.setAttribute('aria-expanded', open);
  r.detail.hidden = !open;
  if (open) {
    const app = last.apps.find(a => a.id === r.id);
    if (app) buildDetail(r, app);
  }
}

function buildDetail(r, app) {
  const caps = last.caps || {};
  const rule = app.rule || { up: 0, down: 0, block: false };
  const kb = v => v ? Math.round(v / 1024) : '';
  const none = !caps.up && !caps.down && !caps.block;
  const note = none
    ? 'Bu sistemde hız limiti ve engelleme henüz yok; şimdilik yalnızca izleme yapılıyor.'
    : !caps.down ? 'İndirme limiti bu sürümde yok: çekirdek sürücüsü gerektiriyor. Yükleme limiti ve engelleme çalışır.' : '';
  r.detail.innerHTML = `
    <form class="rule">
      <label class="field">Yükleme limiti<div><input name="up" type="number" min="1" step="1" placeholder="Sınırsız" value="${kb(rule.up)}" ${caps.up ? '' : 'disabled'}> KB/s</div></label>
      <label class="field">İndirme limiti<div><input name="down" type="number" min="1" step="1" placeholder="Sınırsız" value="${kb(rule.down)}" ${caps.down ? '' : 'disabled'}> KB/s</div></label>
      <label class="check"><input name="block" type="checkbox" ${rule.block ? 'checked' : ''} ${caps.block ? '' : 'disabled'}> İnternet erişimini engelle</label>
      <button class="btn primary" type="submit" ${none ? 'disabled' : ''}>Kuralı uygula</button>
      <button class="btn" type="button" data-clear ${app.rule ? '' : 'hidden'}>Kuralı kaldır</button>
      ${note ? `<p class="note">${esc(note)}</p>` : ''}
    </form>
    <table class="conns">
      <thead><tr><th>Sunucu</th><th>Adres</th><th>Ülke</th><th>Sağlayıcı</th><th class="num">İndirme</th><th class="num">Yükleme</th><th class="num">Toplam</th></tr></thead>
      <tbody></tbody>
    </table>`;
  r.form = r.detail.querySelector('form');
  r.tbody = r.detail.querySelector('tbody');
  r.form.addEventListener('submit', e => {
    e.preventDefault();
    const f = new FormData(r.form);
    const bytes = k => Math.max(0, Math.round(Number(f.get(k) || 0) * 1024));
    sendRule(r.id, { up: bytes('up'), down: bytes('down'), block: f.get('block') === 'on' });
  });
  r.detail.querySelector('[data-clear]').addEventListener('click', () => {
    r.form.reset();
    r.form.querySelectorAll('input[type=number]').forEach(i => i.value = '');
    r.form.querySelector('[name=block]').checked = false;
    sendRule(r.id, { up: 0, down: 0, block: false });
  });
  fillConns(r, app);
}

function sendRule(id, rule) {
  bridge.send({ type: 'rule', id, ...rule });
  toast('Kural uygulanıyor…', true);
}

function fillConns(r, app) {
  const conns = [...app.conns].sort((a, b) => (b.down + b.up) - (a.down + a.up) || (b.totDown + b.totUp) - (a.totDown + a.totUp));
  r.tbody.innerHTML = conns.length ? conns.slice(0, 300).map(c => `
    <tr>
      <td class="host" title="${esc(c.host || '')}">${c.host ? esc(c.host) : '<span class="idle">ad yok</span>'}</td>
      <td class="ip"><span class="proto">${esc(c.proto)}</span> ${esc(c.remote)}:${c.port}</td>
      <td>${c.cc ? `<span class="cc">${esc(c.cc)}</span>` : ''}${esc(countryName(c))}</td>
      <td class="org" title="${esc(c.org)}">${esc(c.org || '—')}</td>
      <td class="num ${c.down ? 'rate-d' : 'idle'}">${rate(c.down)}</td>
      <td class="num ${c.up ? 'rate-u' : 'idle'}">${rate(c.up)}</td>
      <td class="num">${size(c.totDown + c.totUp)}</td>
    </tr>`).join('') : '<tr><td colspan="7" class="idle">Şu an açık bağlantı yok.</td></tr>';
}

function updateRow(r, app, total) {
  const kind = icons[app.id] ? 'img' : 'letter';
  if (r.iconKind !== kind) { r.icon.innerHTML = avatar(app); r.iconKind = kind; }
  r.name.textContent = app.name;
  const badges = [];
  if (app.rule?.block) badges.push('<span class="badge block">Engelli</span>');
  if (app.rule?.up) badges.push(`<span class="badge limit">↑ en çok ${esc(size(app.rule.up))}/s</span>`);
  if (app.rule?.down) badges.push(`<span class="badge limit">↓ en çok ${esc(size(app.rule.down))}/s</span>`);
  if (badges.length) r.name.insertAdjacentHTML('beforeend', `<span class="badges">${badges.join('')}</span>`);
  r.exe.textContent = app.exe && app.exe !== app.name ? app.exe : app.id;
  r.exe.title = app.id;
  r.rd.textContent = rate(app.down); r.rd.className = 'num ' + (app.down ? 'rate-d' : 'idle');
  r.ru.textContent = rate(app.up); r.ru.className = 'num ' + (app.up ? 'rate-u' : 'idle');
  r.tot.textContent = size(app.totDown + app.totUp);
  r.nc.textContent = app.conns.length;
  r.sd.style.width = total ? (app.down / total * 100) + '%' : '0';
  r.su.style.width = total ? (app.up / total * 100) + '%' : '0';
  if (expanded.has(app.id) && r.tbody) {
    fillConns(r, app);
    r.form.querySelector('[data-clear]').hidden = !app.rule;
  }
}

function renderLive() {
  const list = last.apps.filter(matches);
  const val = sortValue[sort.key];
  list.sort((a, b) => {
    const x = val(a), y = val(b);
    return (x < y ? -1 : x > y ? 1 : 0) * sort.dir || a.name.localeCompare(b.name, 'tr');
  });
  const total = last.apps.reduce((s, a) => s + a.down + a.up, 0);
  const box = $('#apps');
  const seen = new Set();
  list.forEach(app => {
    seen.add(app.id);
    updateRow(rows.get(app.id) ?? createRow(app), app, total);
  });
  for (const [id, r] of rows) if (!seen.has(id)) { r.root.remove(); if (!last.apps.some(a => a.id === id)) rows.delete(id); }
  // yazı yazılan kural alanı varken satırları yerinden oynatma
  if (!box.contains(document.activeElement) || document.activeElement === document.body) {
    list.forEach((app, i) => { const el = rows.get(app.id).root; if (box.children[i] !== el) box.insertBefore(el, box.children[i] ?? null); });
  } else {
    list.forEach(app => { const el = rows.get(app.id).root; if (!el.isConnected) box.append(el); });
  }
  $('#empty').hidden = list.length > 0;
  $('#empty').textContent = query ? `"${query}" ile eşleşen uygulama yok.` : 'Henüz trafik yok. Bir uygulama internete bağlandığında burada görünür.';
}

function onTick(t) {
  last = t;
  Object.assign(icons, t.icons || {});
  river.shift();
  river.push({ d: t.down, u: t.up });
  tickAt = performance.now();
  meter($('#down'), t.down);
  meter($('#up'), t.up);
  const sd = t.apps.reduce((s, a) => s + a.totDown, 0), su = t.apps.reduce((s, a) => s + a.totUp, 0);
  $('#session').textContent = `Bu oturumda ${size(sd)} indirildi, ${size(su)} yüklendi`;
  const err = $('#error');
  err.hidden = !t.error;
  if (t.error) err.textContent = 'Trafik izlenemiyor: ' + t.error;
  renderLive();
}

/* ---------- geçmiş ---------- */
let days = 7;
const dayFmt = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'short' });

function amount(el, bytes) {
  const [n, u] = split(bytes);
  el.innerHTML = `${esc(n)}<small>${esc(u)}</small>`;
}

function renderHistory(h) {
  const down = h.days.reduce((s, d) => s + d.down, 0), up = h.days.reduce((s, d) => s + d.up, 0);
  $('#sum-label').textContent = days === 1 ? 'Bugün toplam' : `Son ${days} günde toplam`;
  amount($('#sum-total'), down + up);
  amount($('#sum-down'), down);
  amount($('#sum-up'), up);
  amount($('#sum-all'), h.allDown + h.allUp);
  $('#sum-since').textContent = h.since
    ? ` · ${new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'short', year: 'numeric' }).format(new Date(h.since + 'T00:00'))} – bugün`
    : '';
  const max = Math.max(1, ...h.days.map(d => Math.max(d.down, d.up)));
  const label = days <= 31 || days === 1;
  $('#days').innerHTML = h.days.map((d, i) => {
    const date = new Date(d.date + 'T00:00');
    const show = label ? (days <= 7 || i % 5 === 0) : i % 15 === 0;
    return `<div class="day" title="${esc(dayFmt.format(date))}: ↓ ${esc(size(d.down))}, ↑ ${esc(size(d.up))}">
      <div class="u"><i style="height:${d.up / max * 100}%"></i></div>
      <div class="d"><i style="height:${d.down / max * 100}%"></i></div>
      <span>${show ? esc(dayFmt.format(date)) : '&nbsp;'}</span>
    </div>`;
  }).join('');
  const total = Math.max(1, h.apps.reduce((s, a) => s + a.down + a.up, 0));
  $('#happs').innerHTML = h.apps.map(a => `
    <div class="hrow">
      <span class="icon">${avatar(a)}</span>
      <span class="who"><span class="name">${esc(a.name)}</span><span class="exe" title="${esc(a.id)}">${esc(a.id)}</span></span>
      <span class="num rate-d">${size(a.down)}</span>
      <span class="num rate-u">${size(a.up)}</span>
      <span class="num">${size(a.down + a.up)}</span>
      <div class="share"><i class="d" style="width:${a.down / total * 100}%"></i><i class="u" style="width:${a.up / total * 100}%"></i></div>
    </div>`).join('');
  $('#hempty').hidden = h.apps.length > 0;
}

/* ---------- kontroller ---------- */
let toastTimer;
function toast(text, ok) {
  const t = $('#toast');
  t.textContent = text;
  t.classList.toggle('bad', !ok);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.hidden = true, ok ? 2500 : 6000);
}

document.querySelectorAll('.views button').forEach(b => b.addEventListener('click', () => {
  document.querySelectorAll('.views button').forEach(x => x.setAttribute('aria-pressed', x === b));
  const v = b.dataset.view;
  $('#live').hidden = v !== 'live';
  $('#history').hidden = v !== 'history';
  if (v === 'history') bridge.send({ type: 'history', days });
}));

document.querySelectorAll('.range button').forEach(b => b.addEventListener('click', () => {
  document.querySelectorAll('.range button').forEach(x => x.setAttribute('aria-pressed', x === b));
  days = Number(b.dataset.days);
  bridge.send({ type: 'history', days });
}));

document.querySelectorAll('.thead [data-sort]').forEach(b => b.addEventListener('click', () => {
  const key = b.dataset.sort;
  sort = sort.key === key ? { key, dir: -sort.dir } : { key, dir: key === 'name' ? 1 : -1 };
  document.querySelectorAll('.thead [data-sort]').forEach(x => x === b
    ? x.setAttribute('aria-sort', sort.dir < 0 ? 'descending' : 'ascending')
    : x.removeAttribute('aria-sort'));
  renderLive();
}));

$('#q').addEventListener('input', e => { query = e.target.value.trim(); renderLive(); });

function onService(m) {
  const box = $('#service'), btn = $('#install');
  box.hidden = m.state === 'connected';
  if (box.hidden) return;
  const canInstall = m.state === 'missing' && m.canInstall;
  btn.hidden = !canInstall;
  btn.textContent = m.installed ? 'Servisi başlat' : 'Servisi kur';
  $('#service-text').textContent = {
    missing: !m.canInstall ? 'Arka plan servisi çalışmıyor. Başlatmak için terminalde: sudo akis --service'
      : m.installed ? 'Arka plan servisi durmuş. Trafik şu an sayılmıyor.'
      : 'Arka plan servisi kurulu değil. Trafiğin pencere kapalıyken de sayılması için bir kez kurulması gerekiyor; kurulum yönetici izni ister.',
    starting: 'Servise bağlanılıyor…',
    lost: 'Servis bağlantısı koptu, yeniden bağlanılıyor…',
    installing: 'Servis kuruluyor, Windows izin isteyecek…',
  }[m.state] ?? m.state;
}
$('#install').addEventListener('click', () => bridge.send({ type: 'install' }));

bridge.on(m => {
  if (m.type === 'tick') onTick(m);
  else if (m.type === 'icons') Object.assign(icons, m.icons);
  else if (m.type === 'service') onService(m);
  else if (m.type === 'history') renderHistory(m.data);
  else if (m.type === 'toast') toast(m.text, m.ok);
});
bridge.send({ type: 'ready' });
