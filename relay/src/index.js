// EQBuddy group-DPS relay: a mailbox, not a database.
//
// Each client POSTs its own numbers every few seconds and gets everyone else's
// back in the same response. One Durable Object per group code holds the latest
// snapshot per member, in memory only — nothing is persisted, and a member who
// stops posting vanishes after STALE_MS. There are no reads without a write, no
// history, and no other endpoints.

const STALE_MS = 20_000;   // drop a member this long after their last post
const MAX_MEMBERS = 24;    // a "group" bigger than a raid is abuse, not a group

const NAME_RE = /^[A-Za-z]{2,16}$/;      // EQ character names: letters only
const CODE_RE = /^[A-Z0-9-]{3,16}$/;     // group codes, case-folded upper

export class GroupRoom {
  constructor(state, env) {
    this.members = new Map(); // name (lower) -> {name, dps, sdps, seen}
  }

  async fetch(request) {
    if (request.method !== "POST")
      return json({ error: "POST only" }, 405);

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

    const now = Date.now();
    for (const [key, m] of this.members)
      if (now - m.seen > STALE_MS) this.members.delete(key);

    const key = name.toLowerCase();
    if (!this.members.has(key) && this.members.size >= MAX_MEMBERS)
      return json({ error: "group full" }, 409);

    this.members.set(key, {
      name,
      dps: Math.round(dps * 10) / 10,
      sdps: Number.isFinite(sdps) && sdps >= 0 ? Math.round(sdps * 10) / 10 : 0,
      seen: now,
    });

    const members = [...this.members.values()]
      .sort((a, b) => b.dps - a.dps)
      .map((m) => ({ name: m.name, dps: m.dps, sdps: m.sdps, ageMs: now - m.seen }));
    return json({ members });
  }
}

export default {
  async fetch(request, env) {
    const match = new URL(request.url).pathname.match(/^\/v1\/group\/([^/]+)$/);
    if (!match) return json({ error: "not found" }, 404);

    const code = decodeURIComponent(match[1]).toUpperCase();
    if (!CODE_RE.test(code)) return json({ error: "bad group code" }, 400);

    return env.ROOMS.get(env.ROOMS.idFromName(code)).fetch(request);
  },
};

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" },
  });
}
