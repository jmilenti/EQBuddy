// EQBuddy group-DPS relay: a mailbox, not a database.
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
    this.members = new Map(); // name (lower) -> {name, dps, sdps, seen}
  }

  prune(now) {
    for (const [key, m] of this.members)
      if (now - m.seen > STALE_MS) this.members.delete(key);
  }

  roster(now) {
    return [...this.members.values()]
      .sort((a, b) => b.dps - a.dps)
      .map((m) => ({ name: m.name, dps: m.dps, sdps: m.sdps, ageMs: now - m.seen }));
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
    const sdps = Number(body.sdps);
    if (!NAME_RE.test(name) || !Number.isFinite(dps) || dps < 0)
      return json({ error: "bad member" }, 400);

    const key = name.toLowerCase();
    if (!this.members.has(key) && this.members.size >= MAX_MEMBERS)
      return json({ error: "group full" }, 409);

    this.members.set(key, {
      name,
      dps: Math.round(dps * 10) / 10,
      sdps: Number.isFinite(sdps) && sdps >= 0 ? Math.round(sdps * 10) / 10 : 0,
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
<title>EQBuddy · ${code}</title>
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
  .rows { font-family: Consolas, Menlo, monospace; font-size: 14px; color: #cfe3f5;
          white-space: pre; line-height: 1.5; }
  .empty { color: #7b8794; font-style: italic; font-size: 13px; }
  .foot { color: #55616c; font-size: 10px; margin-top: 10px; }
</style>
</head>
<body>
<div class="panel">
  <div class="head"><div class="dot" id="dot"></div><div class="title">EQBuddy · ${code}</div></div>
  <div class="label">GROUP · live from the relay</div>
  <div class="rows" id="rows"><span class="empty">connecting…</span></div>
  <div class="foot" id="foot"></div>
</div>
<script>
const rows = document.getElementById('rows');
const dot = document.getElementById('dot');
const foot = document.getElementById('foot');
const pad = (s, w) => s.length >= w ? s.slice(0, w) : s + ' '.repeat(w - s.length);
async function tick() {
  try {
    const r = await fetch('/v1/group/${code}');
    const data = await r.json();
    const m = data.members || [];
    dot.className = 'dot' + (m.length ? ' live' : '');
    rows.innerHTML = '';
    if (!m.length) {
      rows.innerHTML = '<span class="empty">(no one posting — apps send only while running)</span>';
    } else {
      rows.textContent = m.map(x =>
        pad(x.name, 13) + String(Math.round(x.dps)).padStart(5) + ' dps  (session ' + Math.round(x.sdps) + ')'
      ).join('\\n');
    }
    foot.textContent = 'refreshes every 3s · read-only';
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
