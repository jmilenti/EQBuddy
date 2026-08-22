// EQdps group-DPS relay: a mailbox, not a database.
//
// Each client POSTs its own numbers every few seconds and gets everyone else's
// back in the same response. One Durable Object per group code holds the latest
// snapshot per member, in memory only — nothing is persisted, and a member who
// stops posting vanishes after STALE_MS. GET returns the same roster without
// posting (the web viewer), and /view/CODE serves a small page that polls it.
// No history, no other endpoints.

const STALE_MS = 20_000;   // drop a member this long after their last post
const MAX_MEMBERS = 24;    // a "group" bigger than a raid is abuse, not a group

const NAME_RE = /^[A-Za-z]{2,16}$/;      // EQ character names: letters only
const CODE_RE = /^[A-Z0-9-]{3,16}$/;     // group codes, case-folded upper

export class GroupRoom {
  constructor(state, env) {
    this.members = new Map(); // name (lower) -> {name, dps, fdps, sdps, dmg, csec, sec, seen}
  }

  prune(now) {
    for (const [key, m] of this.members)
      if (now - m.seen > STALE_MS) this.members.delete(key);
  }

  roster(now) {
    return [...this.members.values()]
      .sort((a, b) => b.dps - a.dps)
      .map((m) => ({
        name: m.name, dps: m.dps, fdps: m.fdps, sdps: m.sdps,
        dmg: m.dmg, csec: m.csec, sec: m.sec,
        top: m.top || [], motes: m.motes || null, ageMs: now - m.seen,
      }));
  }

  async fetch(request) {
    const now = Date.now();
    this.prune(now);

    if (request.method === "GET")
      return json({ members: this.roster(now) });

    if (request.method !== "POST")
      return json({ error: "GET or POST only" }, 405);

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: "bad json" }, 400);
    }

    const name = typeof body.name === "string" ? body.name.trim() : "";
    const dps = Number(body.dps);
    // Current-or-last fight rate, so viewers can show a per-fight board; older
    // clients omit it and read as 0.
    const fdps = Number(body.fdps);
    const sdps = Number(body.sdps);
    // Running session totals. A client that resets its own session rebases the board
    // against these, so they must be cumulative, never windowed.
    const dmg = Number(body.dmg);
    const csec = Number(body.csec);
    // Session elapsed seconds — lets a viewer show how LONG a member took to gather
    // what they have, not just the totals.
    const sec = Number(body.sec);
    if (!NAME_RE.test(name) || !Number.isFinite(dps) || dps < 0)
      return json({ error: "bad member" }, 400);

    // Optional damage breakdown: top sources as [{n: "Ignite", t: 48210}, ...].
    // Old clients just omit it.
    const top = [];
    if (Array.isArray(body.top)) {
      for (const e of body.top.slice(0, 8)) {
        const n = typeof e?.n === "string" ? e.n.trim().slice(0, 24) : "";
        const t = Number(e?.t);
        const h = Number(e?.h);
        if (n && Number.isFinite(t) && t >= 0)
          top.push({ n, t: Math.round(t), h: Number.isFinite(h) && h > 0 ? Math.round(h) : 0 });
      }
    }

    // Optional mote haul: {tot, ph, tiers: [{n: "Mote of Greater Potential", c: 3}]}.
    let motes = null;
    if (body.motes && typeof body.motes === "object") {
      const tot = Number(body.motes.tot);
      const ph = Number(body.motes.ph);
      const tiers = [];
      if (Array.isArray(body.motes.tiers)) {
        for (const e of body.motes.tiers.slice(0, 10)) {
          const n = typeof e?.n === "string" ? e.n.trim().slice(0, 32) : "";
          const c = Number(e?.c);
          if (n && Number.isFinite(c) && c >= 0) tiers.push({ n, c: Math.round(c) });
        }
      }
      if (Number.isFinite(tot) && tot >= 0)
        motes = { tot: Math.round(tot), ph: Number.isFinite(ph) && ph >= 0 ? Math.round(ph * 10) / 10 : 0, tiers };
    }

    const key = name.toLowerCase();
    if (!this.members.has(key) && this.members.size >= MAX_MEMBERS)
      return json({ error: "group full" }, 409);

    this.members.set(key, {
      name,
      dps: Math.round(dps * 10) / 10,
      fdps: Number.isFinite(fdps) && fdps >= 0 ? Math.round(fdps * 10) / 10 : 0,
      sdps: Number.isFinite(sdps) && sdps >= 0 ? Math.round(sdps * 10) / 10 : 0,
      dmg: Number.isFinite(dmg) && dmg >= 0 ? Math.round(dmg) : 0,
      csec: Number.isFinite(csec) && csec >= 0 ? Math.round(csec) : 0,
      sec: Number.isFinite(sec) && sec >= 0 ? Math.round(sec) : 0,
      top,
      motes,
      seen: now,
    });

    return json({ members: this.roster(now) });
  }
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;

    const api = path.match(/^\/v1\/group\/([^/]+)$/);
    if (api) {
      const code = decodeURIComponent(api[1]).toUpperCase();
      if (!CODE_RE.test(code)) return json({ error: "bad group code" }, 400);
      return env.ROOMS.get(env.ROOMS.idFromName(code)).fetch(request);
    }

    const view = path.match(/^\/view\/([^/]+)$/);
    if (view && request.method === "GET") {
      const code = decodeURIComponent(view[1]).toUpperCase();
      if (!CODE_RE.test(code)) return json({ error: "bad group code" }, 400);
      // code matches CODE_RE (A-Z, 0-9, -) so it is safe to embed verbatim.
      return new Response(viewPage(code), {
        headers: { "content-type": "text/html; charset=utf-8" },
      });
    }

    return json({ error: "not found" }, 404);
  },
};

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" },
  });
}

// The Lite panel, as a web page: same dark glass, same monospace board. Read-only —
// this page never posts, it just watches the group code it was opened with.
function viewPage(code) {
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>EQdps · ${code}</title>
<style>
  body { margin: 0; min-height: 100vh; display: flex; align-items: center;
         justify-content: center; background: #0a0d10; font-family: 'Segoe UI', system-ui, sans-serif; }
  .panel { background: #101418f0; border: 1px solid #ffffff3c; border-radius: 10px;
           padding: 14px 18px 16px; min-width: 260px; }
  .head { display: flex; align-items: center; gap: 7px; margin-bottom: 8px; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: #666; }
  .dot.live { background: limegreen; }
  .title { color: #dde5ec; font-weight: 600; font-size: 14px; }
  .label { color: #7b8794; font-size: 10px; letter-spacing: .5px; margin: 4px 0; }
  .rows { font-family: Consolas, Menlo, monospace; font-size: 14px; color: #cfe3f5; }
  .mrow { cursor: pointer; padding: 4px 8px; margin: 2px 0; border-radius: 6px;
          background: #ffffff14; border: 1px solid #ffffff1e; white-space: pre; }
  .mrow:hover { background: #ffffff30; border-color: #ffffff55; }
  .mdetail { font-size: 12px; color: #9fb0be; white-space: pre; margin: 2px 0 6px 14px;
             line-height: 1.45; }
  .mdetail .motes { color: #d9c46b; }
  .empty { color: #7b8794; font-style: italic; font-size: 13px; }
  .foot { color: #55616c; font-size: 10px; margin-top: 10px; }
</style>
</head>
<body>
<div class="panel">
  <div class="head"><div class="dot" id="dot"></div><div class="title">EQdps · ${code}</div></div>
  <div class="label">GROUP · live from the relay</div>
  <div class="rows" id="rows"><span class="empty">connecting…</span></div>
  <div class="foot" id="foot"></div>
</div>
<script>
const rows = document.getElementById('rows');
const dot = document.getElementById('dot');
const foot = document.getElementById('foot');
const open = new Set(); // member names whose breakdown is expanded
const pad = (s, w) => s.length >= w ? s.slice(0, w) : s + ' '.repeat(w - s.length);
const fmt = (n) => n >= 1e6 ? (n / 1e6).toFixed(1) + 'M'
  : n >= 1e4 ? Math.round(n / 1e3) + 'k'
  : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
function render(members) {
  rows.innerHTML = '';
  for (const m of members) {
    const row = document.createElement('div');
    row.className = 'mrow';
    row.textContent = pad(m.name, 13) + String(Math.round(m.dps)).padStart(5)
      + ' dps  (session ' + Math.round(m.sdps) + ')';
    row.title = 'Click for breakdown';
    row.onclick = () => { open.has(m.name) ? open.delete(m.name) : open.add(m.name); tick(); };
    rows.appendChild(row);
    if (!open.has(m.name)) continue;
    const det = document.createElement('div');
    det.className = 'mdetail';
    const top = m.top || [];
    const total = top.reduce((a, e) => a + e.t, 0);
    let text = total > 0
      ? top.map(e => pad(e.n, 14) + (e.h > 0 ? String(e.h) + '\\u00d7' : '').padStart(6)
          + fmt(e.t).padStart(7)
          + String(Math.round(e.t * 100 / total)).padStart(4) + '%').join('\\n')
      : '(no breakdown shared)';
    det.textContent = text;
    if (m.motes && m.motes.tot > 0) {
      const mo = document.createElement('div');
      mo.className = 'motes';
      mo.textContent = '\\u2728 ' + m.motes.tot + ' motes (' + m.motes.ph + '/h)  '
        + (m.motes.tiers || []).map(t => t.n.replace(/^Mote of\\s*/i, '').replace(/\\s*Potential$/i, '') .trim().replace(/^$/, 'Base') + '\\u00d7' + t.c).join(', ');
      det.appendChild(document.createElement('br'));
      det.appendChild(mo);
    }
    rows.appendChild(det);
  }
}
async function tick() {
  try {
    const r = await fetch('/v1/group/${code}');
    const data = await r.json();
    const m = data.members || [];
    dot.className = 'dot' + (m.length ? ' live' : '');
    if (!m.length) {
      rows.innerHTML = '<span class="empty">(no one posting — apps send only while running)</span>';
    } else {
      render(m);
    }
    foot.textContent = 'refreshes every 3s · click a name for their breakdown · read-only';
  } catch (e) {
    dot.className = 'dot';
    foot.textContent = 'relay unreachable — retrying…';
  }
}
tick();
setInterval(tick, 3000);
</script>
</body>
</html>`;
}
