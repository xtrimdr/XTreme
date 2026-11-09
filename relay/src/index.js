// XTreme live relay (#320): carries the shared sessions over the internet. One Durable Object per room; the host
// keeps one WebSocket and every guest one of its own. The relay only moves bytes: everything is encrypted end to end
// by the app (LiveLink, AES-GCM with a key derived from the short code), and the room's name is a hash the relay
// can't turn back into the code.
//
// URL: /r/<room>/host?v=<protocol>[&resume=<token>] and /r/<room>/join?v=<protocol>. A client without v is protocol 1.
//
// Host socket, binary messages: [op:1][guest:4, big endian][payload]
//   op 1 = a guest arrived (relay -> host), 2 = bytes (both ways), 3 = a guest left / close it (both ways),
//   4 = the room's resume token, in ASCII (relay -> host, right after it opens; protocol 2),
//   5 = the host is back (relay -> host, after a resume): the payload lists the guests still in, 4 bytes each.
// Guest socket: the bytes as they are.
//
// Reconnecting (#1, protocol 2): when the host's socket drops (any close but a normal one), the room waits
// HOST_GRACE_MS for it with the guests in; what they send meanwhile is dropped. The host comes back with the token
// and the app resends what the other side hadn't confirmed (its own numbering, inside the payload, end to end).
//
// Limits (#1): open rooms in all and per address, rooms opened per address per hour, a session's length and its
// traffic. Each ends with its own status or close code, which the app explains.
//
// Administration (#321, asked by the user): /admin is a private page (the ADMIN_KEY secret) that lists the rooms
// open now (people in, since when, traffic, region), closes one, turns new sessions off, and shows the history by day.
// A single Registry object keeps which rooms are open, whether new ones are allowed and the history; each room tells
// it when it opens and ends. A room's storage is wiped when it ends.

import { DurableObject } from "cloudflare:workers";
import { adminPage } from "./admin.js";

const OPEN = 1, DATA = 2, CLOSE = 3, TOKEN = 4, RESUMED = 5;
/** The protocol this relay speaks; a client older than MIN_PROTOCOL is told to update. */
const PROTOCOL = 2, MIN_PROTOCOL = 1;
/** Guests a room takes at most. */
const MAX_GUESTS = 16;
/** Close codes the app explains: closed from /admin, the session got too long, too much traffic. */
const CLOSED_BY_ADMIN = 4001, TOO_LONG = 4002, TOO_MUCH = 4003;
/** How long a room waits for its host to come back after its socket dropped. */
const HOST_GRACE_MS = 30_000;
/** A session lasts this long at most. */
const MAX_SESSION_MS = 12 * 3600_000;
/** The traffic a session may pass in all (both ways). */
const MAX_SESSION_BYTES = 20 * 2 ** 30;
/** Rooms open at once, in all and from one address; rooms one address may open in an hour. */
const MAX_ROOMS = 200, MAX_ROOMS_PER_ADDRESS = 3, MAX_OPENS_PER_HOUR = 20;
/** A room's traffic count goes to storage this long after it changed, so a room going to sleep doesn't lose it. */
const FLUSH_MS = 5_000;
/** Days of history kept. */
const HISTORY_DAYS = 90;

const json = (body, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });

/** Compares two strings in constant time. */
function same(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function allowed(request, env) {
  const key = env.ADMIN_KEY;
  return !!key && same((request.headers.get("authorization") || "").replace(/^Bearer /, ""), key);
}

function registry(env) {
  return env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
}

/** A short hash of the address a request came from: enough to count rooms per address, without keeping it. */
async function addressOf(request) {
  const ip = request.headers.get("cf-connecting-ip") || "";
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("xtreme relay " + ip));
  return [...new Uint8Array(d).slice(0, 8)].map(b => b.toString(16).padStart(2, "0")).join("");
}

const day = t => new Date(t).toISOString().slice(0, 10);

async function admin(request, env, path) {
  if (path === "/admin") return new Response(adminPage, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
  if (!allowed(request, env)) return json({ error: "unauthorized" }, 401);
  const reg = registry(env);
  if (path === "/admin/api/state" && request.method === "GET") return json(await reg.state());
  if (request.method !== "POST") return json({ error: "method" }, 405);
  const body = await request.json().catch(() => ({}));
  if (path === "/admin/api/close" && typeof body.room === "string" && /^[0-9a-f]{32}$/.test(body.room)) {
    await env.ROOMS.get(env.ROOMS.idFromName(body.room)).shut();
    await reg.gone(body.room);
    return json({ ok: true });
  }
  if (path === "/admin/api/pause") return json(await reg.pause(!!body.paused));
  return json({ error: "not found" }, 404);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/admin" || url.pathname.startsWith("/admin/api/")) return admin(request, env, url.pathname);
    const m = url.pathname.match(/^\/r\/([0-9a-f]{32})\/(host|join)$/);
    if (!m) return new Response("XTreme live relay", { status: url.pathname === "/" ? 200 : 404 });
    if (request.headers.get("Upgrade") !== "websocket") return new Response("Expected a WebSocket", { status: 426 });
    const v = Number(url.searchParams.get("v") || 1);
    if (!(v >= MIN_PROTOCOL)) return new Response("Update XTreme", { status: 426 });
    const resume = url.searchParams.get("resume") || "";
    const headers = new Headers(request.headers);
    headers.set("x-room", m[1]);
    headers.set("x-v", String(Math.min(v, PROTOCOL)));
    if (m[2] === "host" && !resume) {
      const address = await addressOf(request);
      const no = await registry(env).admit(address);
      if (no) return new Response(no.text, { status: no.status });
      headers.set("x-address", address);
    }
    // a room is created where its first request asks; without a hint Cloudflare may put it far from both (from
    // Argentina it went abroad: 635 ms round trip, #320), so the host's request asks for its own region
    const hint = m[2] === "host" ? regionOf(request.cf) : undefined;
    if (hint) headers.set("x-region", hint);
    const room = env.ROOMS.get(env.ROOMS.idFromName(m[1]), hint ? { locationHint: hint } : undefined);
    const to = new URL(`https://room/${m[2]}`);
    if (resume) to.searchParams.set("resume", resume);
    return room.fetch(new Request(to, { headers }));
  },
};

/** The Durable Object region closest to where a request came from (Cloudflare's location hints), or undefined. */
function regionOf(cf) {
  if (!cf) return undefined;
  const lon = Number(cf.longitude);
  switch (cf.continent) {
    case "SA": return "sam";
    case "NA": return lon < -100 ? "wnam" : "enam";
    case "EU": return lon > 20 ? "eeur" : "weur";
    case "AS": return ["AE", "SA", "QA", "BH", "KW", "OM", "IL", "JO", "IR", "IQ"].includes(cf.country) ? "me" : "apac";
    case "OC": return "oc";
    case "AF": return "afr";
    default: return undefined;
  }
}

function head(op, id, extra = 0) {
  const b = new Uint8Array(5 + extra);
  b[0] = op;
  new DataView(b.buffer).setUint32(1, id >>> 0);
  return b;
}

/** Which rooms are open now, whether new ones may open, and the history by day: one for the whole relay. */
export class Registry extends DurableObject {
  async paused() {
    return (await this.ctx.storage.get("paused")) === true;
  }

  async pause(on) {
    await this.ctx.storage.put("paused", on);
    return { paused: on };
  }

  /** Whether a new room may open from this address; null = yes, or the status and text that say no. */
  async admit(address) {
    if (await this.paused()) return { status: 503, text: "New sessions are off" };
    const open = [...(await this.ctx.storage.list({ prefix: "room:" })).values()];
    if (open.length >= MAX_ROOMS) return { status: 503, text: "The relay is full" };
    if (open.filter(r => r.address === address).length >= MAX_ROOMS_PER_ADDRESS) return { status: 429, text: "Too many rooms" };
    const now = Date.now();
    const opens = ((await this.ctx.storage.get("opens:" + address)) || []).filter(t => now - t < 3600_000);
    if (opens.length >= MAX_OPENS_PER_HOUR) return { status: 429, text: "Too many rooms" };
    opens.push(now);
    await this.ctx.storage.put("opens:" + address, opens);
    return null;
  }

  async opened(room, started, address, region) {
    await this.ctx.storage.put("room:" + room, { started, address, region });
    await this.add(day(started), { sessions: 1 });
  }

  async gone(room) {
    await this.ctx.storage.delete("room:" + room);
  }

  /** A room ended: it leaves the list and its numbers go to the history. */
  async ended(room, s) {
    await this.gone(room);
    await this.add(day(Date.now()), { bytes: s.bytes || 0, seconds: Math.round((Date.now() - s.started) / 1000), peak: s.peak || 0 });
  }

  async add(d, n) {
    const key = "day:" + d;
    const v = (await this.ctx.storage.get(key)) || { sessions: 0, bytes: 0, seconds: 0, peak: 0 };
    v.sessions += n.sessions || 0;
    v.bytes += n.bytes || 0;
    v.seconds += n.seconds || 0;
    v.peak = Math.max(v.peak, n.peak || 0);
    await this.ctx.storage.put(key, v);
    // what's older than the history goes, and the hourly counts of addresses that stopped opening
    const old = day(Date.now() - HISTORY_DAYS * 86400_000);
    const keys = [...(await this.ctx.storage.list({ prefix: "day:", end: "day:" + old })).keys()];
    if (keys.length) await this.ctx.storage.delete(keys);
    const now = Date.now(), stale = [];
    for (const [k, t] of await this.ctx.storage.list({ prefix: "opens:" })) if (!t.some(x => now - x < 3600_000)) stale.push(k);
    if (stale.length) await this.ctx.storage.delete(stale);
  }

  /** Every open room, each asked how it is now (one whose host is gone for good is dropped), and the history. */
  async state() {
    const list = await this.ctx.storage.list({ prefix: "room:" });
    const rooms = [];
    await Promise.all([...list].map(async ([key, v]) => {
      const room = key.slice(5);
      const s = await this.env.ROOMS.get(this.env.ROOMS.idFromName(room)).status().catch(() => null);
      if (!s || !s.open) { await this.ctx.storage.delete(key); return; }
      rooms.push({ room, started: v.started, region: v.region || null, guests: s.guests, bytes: s.bytes, away: s.away });
    }));
    rooms.sort((a, b) => a.started - b.started);
    const days = [...(await this.ctx.storage.list({ prefix: "day:", reverse: true, limit: 31 }))]
      .map(([k, v]) => ({ day: k.slice(4), ...v }));
    const month = day(Date.now()).slice(0, 7), total = { sessions: 0, bytes: 0, seconds: 0, peak: 0 };
    for (const [k, v] of await this.ctx.storage.list({ prefix: "day:" + month })) {
      total.sessions += v.sessions; total.bytes += v.bytes; total.seconds += v.seconds; total.peak = Math.max(total.peak, v.peak);
    }
    return { paused: await this.paused(), rooms, days, month: { month, ...total }, now: Date.now() };
  }
}

/**
 * One room. Its storage while open: room, started, v (protocol), token, gen (which host socket is the current one),
 * bytes, peak (guests at most), away (when the host's socket dropped, or absent), address, region.
 */
export class Room extends DurableObject {
  /** Bytes passed since the count was last saved. */
  unsaved = 0;
  meta = null;

  constructor(ctx, env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => { this.meta = (await ctx.storage.get("meta")) || null; });
  }

  async save() {
    await this.ctx.storage.put("meta", this.meta);
  }

  /** The host's current socket: an older one replaced by a resume no longer counts. */
  host() {
    if (!this.meta) return undefined;
    return this.ctx.getWebSockets("h" + this.meta.gen)[0];
  }

  guest(id) {
    return this.ctx.getWebSockets("g" + id)[0];
  }

  guests() {
    return this.ctx.getWebSockets("guest");
  }

  registry() {
    return registry(this.env);
  }

  async fetch(request) {
    const url = new URL(request.url);
    const role = url.pathname.slice(1);
    const v = Number(request.headers.get("x-v")) || 1;
    const host = this.host();
    if (role === "host") {
      const resume = url.searchParams.get("resume");
      if (resume) {
        if (!this.meta || !same(resume, this.meta.token)) return new Response("No such room", { status: 404 });
        const old = host;
        this.meta.gen++;
        delete this.meta.away;
        await this.save();
        await this.schedule();
        if (old) this.close(old, 1000, "replaced");
        const [client, server] = Object.values(new WebSocketPair());
        this.ctx.acceptWebSocket(server, ["host", "h" + this.meta.gen]);
        const ids = this.guests().map(g => g.deserializeAttachment().id);
        const back = head(RESUMED, 0, ids.length * 4);
        ids.forEach((id, i) => new DataView(back.buffer).setUint32(5 + i * 4, id >>> 0));
        server.send(back);
        return new Response(null, { status: 101, webSocket: client });
      }
      if (this.meta) return new Response("The room is taken", { status: 409 });
      const room = request.headers.get("x-room");
      const token = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, "0")).join("");
      this.meta = {
        room, started: Date.now(), v, token, gen: 1, bytes: 0, peak: 0,
        address: request.headers.get("x-address") || "", region: request.headers.get("x-region") || null,
      };
      this.unsaved = 0;
      await this.save();
      await this.schedule();
      const [client, server] = Object.values(new WebSocketPair());
      this.ctx.acceptWebSocket(server, ["host", "h1"]);
      if (v >= 2) {
        const t = head(TOKEN, 0, token.length);
        t.set(new TextEncoder().encode(token), 5);
        server.send(t);
      }
      await this.registry().opened(room, this.meta.started, this.meta.address, this.meta.region);
      return new Response(null, { status: 101, webSocket: client });
    }
    if (!this.meta || !host) return new Response("No such room", { status: 404 });
    if (v !== this.meta.v) return new Response("Another version", { status: 426 });
    if (this.guests().length >= MAX_GUESTS) return new Response("The room is full", { status: 429 });
    const [client, server] = Object.values(new WebSocketPair());
    let id;
    do id = crypto.getRandomValues(new Uint32Array(1))[0]; while (id === 0 || this.guest(id));
    this.ctx.acceptWebSocket(server, ["guest", "g" + id]);
    server.serializeAttachment({ id });
    const n = this.guests().length;
    if (n > this.meta.peak) {
      this.meta.peak = n;
      await this.save();
    }
    host.send(head(OPEN, id));
    return new Response(null, { status: 101, webSocket: client });
  }

  /** The next alarm: the host's deadline to come back, the session's end, or saving the traffic count. */
  async schedule() {
    if (!this.meta) return this.ctx.storage.deleteAlarm();
    let at = this.meta.started + MAX_SESSION_MS;
    if (this.meta.away) at = Math.min(at, this.meta.away + HOST_GRACE_MS);
    if (this.unsaved > 0) at = Math.min(at, Date.now() + FLUSH_MS);
    const now = await this.ctx.storage.getAlarm();
    if (now === null || now !== at) await this.ctx.storage.setAlarm(at);
  }

  async alarm() {
    if (!this.meta) return;
    const now = Date.now();
    if (now >= this.meta.started + MAX_SESSION_MS) return this.end(TOO_LONG, "session too long");
    if (this.meta.away && now >= this.meta.away + HOST_GRACE_MS) return this.end(1001, "host left");
    await this.flush();
    await this.schedule();
  }

  async flush() {
    if (!this.meta || this.unsaved === 0) return;
    this.meta.bytes += this.unsaved;
    this.unsaved = 0;
    await this.save();
  }

  async count(n) {
    const first = this.unsaved === 0;
    this.unsaved += n;
    if (this.meta && this.meta.bytes + this.unsaved > MAX_SESSION_BYTES) return this.end(TOO_MUCH, "too much traffic");
    if (first) await this.schedule();
  }

  /** For /admin: whether the room is open, how many guests, the traffic so far and whether the host is away. */
  async status() {
    return {
      open: !!this.meta && (!!this.host() || !!this.meta.away),
      guests: this.guests().length,
      bytes: (this.meta?.bytes || 0) + this.unsaved,
      away: !!this.meta?.away,
    };
  }

  /** Closed from /admin: everybody out, with the code that says so. */
  async shut() {
    await this.end(CLOSED_BY_ADMIN, "closed by admin");
  }

  /** The room ends: everybody out with this code, its numbers to the history, and its storage wiped. */
  async end(code, reason) {
    for (const ws of this.ctx.getWebSockets()) this.close(ws, code, reason);
    const meta = this.meta;
    this.meta = null;
    if (meta) {
      meta.bytes += this.unsaved;
      await this.registry().ended(meta.room, meta).catch(() => { });
    }
    this.unsaved = 0;
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
  }

  async webSocketMessage(ws, message) {
    if (typeof message === "string" || !this.meta) return;
    const data = new Uint8Array(message);
    const host = this.host();
    const tags = this.ctx.getTags(ws);
    if (tags.includes("host")) {
      if (ws !== host || data.length < 5) return;
      const op = data[0];
      const id = new DataView(data.buffer, data.byteOffset).getUint32(1);
      const g = this.guest(id);
      if (!g) {
        if (op === DATA) host.send(head(CLOSE, id));
        return;
      }
      if (op === DATA) g.send(data.subarray(5));
      else if (op === CLOSE) this.close(g, 1000, "closed");
      await this.count(data.length - 5);
      return;
    }
    // while the host is away, what a guest sends is dropped: the app sends it again when the host is back
    if (!host) return;
    const { id } = ws.deserializeAttachment();
    const out = head(DATA, id, data.length);
    out.set(data, 5);
    host.send(out);
    await this.count(data.length);
  }

  async webSocketClose(ws, code, reason) {
    await this.gone(ws, code);
  }

  async webSocketError(ws, error) {
    await this.gone(ws, 1006);
  }

  /**
   * A socket closed. The host's, closed on purpose, ends the room; dropped, the room waits for it (protocol 2). An
   * older host socket replaced by a resume doesn't count. A guest's is told to the host.
   */
  async gone(ws, code) {
    this.close(ws, 1000, "bye");
    const tags = this.ctx.getTags(ws);
    if (tags.includes("host")) {
      if (!this.meta || !tags.includes("h" + this.meta.gen)) return;
      if (code === 1000 || this.meta.v < 2) return this.end(1001, "host left");
      this.meta.away = Date.now();
      await this.save();
      await this.schedule();
      return;
    }
    const host = this.host();
    const a = ws.deserializeAttachment();
    if (host && a) host.send(head(CLOSE, a.id));
  }

  close(ws, code, reason) {
    try { ws.close(code, reason); } catch { }
  }
}
