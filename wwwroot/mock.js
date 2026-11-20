'use strict';
// Yalnızca normal tarayıcıda (Photino dışında) devreye girer: arayüzü sahte veriyle geliştirmek için.
if (typeof window.external?.sendMessage !== 'function') {
  const handlers = [];
  const emit = m => handlers.forEach(h => h(m));
  const rnd = (a, b) => a + Math.random() * (b - a);
  const apps = [
    { id: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', name: 'Google Chrome', d: 4e6, u: 2e5,
      conns: [['www.youtube.com', '142.250.187.110', 443, 'US', 'Google LLC'], ['rr3---sn-u0g3uxax3-pnue.googlevideo.com', '173.194.182.8', 443, 'TR', 'Google LLC'], ['github.com', '140.82.121.4', 443, 'DE', 'GitHub, Inc.'], [null, '104.18.32.47', 443, 'US', 'Cloudflare, Inc.']] },
    { id: 'C:\\Program Files (x86)\\Steam\\steam.exe', name: 'Steam', d: 9e6, u: 4e4,
      conns: [['cache8-ist1.steamcontent.com', '185.25.182.77', 80, 'TR', 'Valve Corporation'], ['api.steampowered.com', '23.41.128.10', 443, 'NL', 'Akamai International B.V.']] },
    { id: 'C:\\Users\\kullanici\\AppData\\Local\\Discord\\app-1.0.9200\\Discord.exe', name: 'Discord', d: 6e4, u: 9e4, rule: { up: 512000, down: 0, block: false },
      conns: [['gateway.discord.gg', '162.159.135.234', 443, 'US', 'Cloudflare, Inc.'], [null, '35.214.177.4', 50004, 'NL', 'Google LLC']] },
    { id: 'C:\\Users\\kullanici\\AppData\\Roaming\\Spotify\\Spotify.exe', name: 'Spotify', d: 3e5, u: 6e3,
      conns: [['audio-ak.spotifycdn.com', '2.19.204.80', 443, 'TR', 'Akamai International B.V.']] },
    { id: 'C:\\Windows\\System32\\svchost.exe', name: 'Windows Hizmetleri için Ana İşlem', d: 2e4, u: 5e3,
      conns: [['settings-win.data.microsoft.com', '20.42.65.85', 443, 'US', 'Microsoft Corporation'], [null, '192.168.1.1', 53, '', '']] },
    { id: 'C:\\Program Files\\Torrent\\torrent.exe', name: 'Torrent', d: 0, u: 0, rule: { up: 0, down: 0, block: true }, conns: [] },
  ];
  for (const a of apps) a.conns = a.conns.map(([host, remote, port, cc, org]) => ({ host, remote, port, cc, org, tot: 0, proto: port === 50004 ? 'UDP' : 'TCP' }));
  let t = 0;
  function tick() {
    t++;
    const out = apps.map(a => {
      const wave = 0.6 + 0.4 * Math.sin(t / 7 + a.name.length);
      const conns = a.conns.map((c, i) => {
        const down = Math.round(a.d * wave * rnd(0.5, 1.2) / (i + 1)), up = Math.round(a.u * wave * rnd(0.5, 1.2) / (i + 1));
        c.tot += down + up;
        return { proto: c.proto, local: '192.168.1.20:5' + (1000 + i), remote: c.remote, port: c.port, host: c.host, cc: c.cc, country: c.cc ? '' : 'Yerel ağ', org: c.org, down, up, totDown: c.tot * 0.9, totUp: c.tot * 0.1 };
      });
      a.totDown = (a.totDown || 0) + conns.reduce((s, c) => s + c.down, 0);
      a.totUp = (a.totUp || 0) + conns.reduce((s, c) => s + c.up, 0);
      return { id: a.id, name: a.name, exe: a.id.split('\\').pop(), pids: [1000], down: conns.reduce((s, c) => s + c.down, 0), up: conns.reduce((s, c) => s + c.up, 0), totDown: a.totDown, totUp: a.totUp, rule: a.rule ?? null, conns };
    });
    emit({ type: 'tick', down: out.reduce((s, a) => s + a.down, 0), up: out.reduce((s, a) => s + a.up, 0), caps: { up: true, down: false, block: true }, error: null, icons: {}, apps: out });
  }
  window.akisBridge = {
    on: h => handlers.push(h),
    send(m) {
      if (m.type === 'ready') setInterval(tick, 1000);
      if (m.type === 'rule') {
        const a = apps.find(x => x.id === m.id);
        a.rule = m.up || m.down || m.block ? { up: m.up, down: m.down, block: m.block } : null;
        setTimeout(() => emit({ type: 'toast', text: 'Kural uygulandı', ok: true }), 600);
      }
      if (m.type === 'history') {
        const days = Array.from({ length: m.days }, (_, i) => {
          const d = new Date(Date.now() - (m.days - 1 - i) * 864e5);
          return { date: d.toISOString().slice(0, 10), down: rnd(2e8, 6e9), up: rnd(2e7, 6e8) };
        });
        const sum = days.reduce((s, d) => s + d.down, 0);
        emit({ type: 'history', data: { since: '2026-09-02', allDown: 3.1e11, allUp: 2.4e10, days, apps: apps.map((a, i) => ({ id: a.id, name: a.name, down: sum / (i + 2), up: sum / (i + 2) / 12 })) } });
      }
    },
  };
}
