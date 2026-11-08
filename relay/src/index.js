// XTreme live relay (#320): carries the shared sessions over the internet. One Durable Object per room; the host
// keeps one WebSocket and every guest one of its own. The relay only moves bytes: everything is encrypted end to end
// by the app (LiveLink, AES-GCM with a key derived from the short code), and the room's name is a hash the relay
// can't turn back into the code.
//
// Host socket, binary messages: [op:1][guest:4, big endian][payload]
//   op 1 = a guest arrived (relay -> host), 2 = bytes (both ways), 3 = a guest left / close it (both ways).
// Guest socket: the bytes as they are.
//
// Administration (#321, asked by the user): /admin is a private page (the ADMIN_KEY secret) that lists the rooms
// open now (people in, since when, traffic), closes one, and turns new sessions off. A single Registry object keeps
// which rooms are open and whether new ones are allowed; each room tells it when its host comes and goes.

import { DurableObject } from "cloudflare:workers";
import { adminPage } from "./admin.js";

const OPEN = 1, DATA = 2, CLOSE = 3;
/** Guests a room takes at most. */
const MAX_GUESTS = 16;
/** The close code a room closed from /admin sends, so the app can say so. */
const CLOSED_BY_ADMIN = 4001;
/** A room saves its traffic count after this many bytes more (a write per megabyte, not per message). */
const SAVE_EVERY = 1 << 20;

const json = (body, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });

/** Compares the admin key in constant time. */
function allowed(request, env) {
  const key = env.ADMIN_KEY;
  const got = (request.headers.get("authorization") || "").replace(/^Bearer /, "");
  if (!key || got.length !== key.length) return false;
  let diff = 0;
  for (let i = 0; i < key.length; i++) diff |= key.charCodeAt(i) ^ got.charCodeAt(i);
  return diff === 0;
}

function registry(env) {
  return env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
}

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
    if (m[2] === "host" && (await registry(env).paused())) return new Response("New sessions are off", { status: 503 });
    const room = env.ROOMS.get(env.ROOMS.idFromName(m[1]));
    const headers = new Headers(request.headers);
    headers.set("x-room", m[1]);
    return room.fetch(new Request(`https://room/${m[2]}`, { headers }));
  },
};

function head(op, id, extra = 0) {
  const b = new Uint8Array(5 + extra);
  b[0] = op;
  new DataView(b.buffer).setUint32(1, id >>> 0);
  return b;
}

/** Which rooms are open now and whether new ones may open: one for the whole relay. */
export class Registry extends DurableObject {
  async paused() {
    return (await this.ctx.storage.get("paused")) === true;
  }

  async pause(on) {
    await this.ctx.storage.put("paused", on);
    return { paused: on };
  }

  async opened(room, started) {
    await this.ctx.storage.put("room:" + room, { started });
  }

  async gone(room) {
    await this.ctx.storage.delete("room:" + room);
  }

  /** Every open room, each asked how it is now; one whose host is gone (a lost close) is dropped. */
  async state() {
    const list = await this.ctx.storage.list({ prefix: "room:" });
    const rooms = [];
    await Promise.all([...list].map(async ([key, v]) => {
      const room = key.slice(5);
      const s = await this.env.ROOMS.get(this.env.ROOMS.idFromName(room)).status().catch(() => null);
      if (!s || !s.host) { await this.ctx.storage.delete(key); return; }
      rooms.push({ room, started: v.started, guests: s.guests, bytes: s.bytes });
    }));
    rooms.sort((a, b) => a.started - b.started);
    return { paused: await this.paused(), rooms, now: Date.now() };
  }
}

export class Room extends DurableObject {
  /** Bytes passed since the count was last saved. */
  unsaved = 0;

  host() {
    return this.ctx.getWebSockets("host")[0];
  }

  guest(id) {
    return this.ctx.getWebSockets("g" + id)[0];
  }

  registry() {
    return registry(this.env);
  }

  async fetch(request) {
    const role = new URL(request.url).pathname.slice(1);
    const host = this.host();
    const [client, server] = Object.values(new WebSocketPair());
    if (role === "host") {
      if (host) return new Response("The room is taken", { status: 409 });
      const room = request.headers.get("x-room");
      const started = Date.now();
      await this.ctx.storage.put({ room, started, bytes: 0 });
      this.unsaved = 0;
      this.ctx.acceptWebSocket(server, ["host"]);
      await this.registry().opened(room, started);
      return new Response(null, { status: 101, webSocket: client });
    }
    if (!host) return new Response("No such room", { status: 404 });
    if (this.ctx.getWebSockets("guest").length >= MAX_GUESTS) return new Response("The room is full", { status: 429 });
    let id;
    do id = crypto.getRandomValues(new Uint32Array(1))[0]; while (id === 0 || this.guest(id));
    this.ctx.acceptWebSocket(server, ["guest", "g" + id]);
    server.serializeAttachment({ id });
    host.send(head(OPEN, id));
    return new Response(null, { status: 101, webSocket: client });
  }

  async count(n) {
    this.unsaved += n;
    if (this.unsaved < SAVE_EVERY) return;
    const bytes = ((await this.ctx.storage.get("bytes")) || 0) + this.unsaved;
    this.unsaved = 0;
    await this.ctx.storage.put("bytes", bytes);
  }

  /** For /admin: whether the host is in, how many guests, and the traffic so far. */
  async status() {
    return {
      host: !!this.host(),
      guests: this.ctx.getWebSockets("guest").length,
      bytes: ((await this.ctx.storage.get("bytes")) || 0) + this.unsaved,
    };
  }

  /** Closed from /admin: everybody out, with the code that says so. */
  async shut() {
    for (const ws of this.ctx.getWebSockets()) this.close(ws, CLOSED_BY_ADMIN, "closed by admin");
  }

  async webSocketMessage(ws, message) {
    if (typeof message === "string") return;
    const data = new Uint8Array(message);
    const host = this.host();
    if (ws === host) {
      if (data.length < 5) return;
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
    if (!host) return this.close(ws, 1001, "host left");
    const { id } = ws.deserializeAttachment();
    const out = head(DATA, id, data.length);
    out.set(data, 5);
    host.send(out);
    await this.count(data.length);
  }

  async webSocketClose(ws, code, reason) {
    await this.gone(ws);
  }

  async webSocketError(ws, error) {
    await this.gone(ws);
  }

  /** A socket closed: the host takes everybody with it (and the room leaves the list); a guest is told to the host. */
  async gone(ws) {
    this.close(ws, 1000, "bye");
    const tags = this.ctx.getTags(ws);
    if (tags.includes("host")) {
      for (const g of this.ctx.getWebSockets("guest")) this.close(g, 1001, "host left");
      const room = await this.ctx.storage.get("room");
      if (room) await this.registry().gone(room);
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
