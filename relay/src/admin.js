// The private page of the relay (#321): the sessions open now, closing one, turning new ones off, and the history by
// day (#1). It asks for the admin key once (kept in this browser) and talks to /admin/api with it.
export const adminPage = `<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>XTreme · Sesiones</title>
<style>
  :root { --bg:#f6f6f8; --panel:#fff; --line:#e3e3e8; --text:#1b1b20; --dim:#6b6b76; --accent:#d9480f; --soft:#fde8dd; --danger:#c92a2a; --ok:#2b8a3e; }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#141417; --panel:#1d1d22; --line:#2e2e35; --text:#ececf1; --dim:#9696a3; --accent:#ff7a3d; --soft:#3a2418; --danger:#ff6b6b; --ok:#51cf66; }
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:14px/1.45 system-ui, "Segoe UI", sans-serif; }
  main { max-width:760px; margin:0 auto; padding:28px 16px 48px; }
  h1 { font-size:20px; margin:0 0 4px; }
  .sub { color:var(--dim); margin:0 0 22px; }
  .card { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:18px; margin-bottom:16px; }
  .row { display:flex; align-items:center; gap:12px; flex-wrap:wrap; }
  .grow { flex:1; min-width:0; }
  .label { font-weight:600; }
  .dim { color:var(--dim); }
  .mono { font-family:ui-monospace, Consolas, monospace; }
  button { font:inherit; border-radius:8px; border:1px solid var(--line); background:var(--panel); color:var(--text); padding:7px 14px; cursor:pointer; }
  button:hover { border-color:var(--dim); }
  button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
  button.danger { color:var(--danger); }
  button:disabled { opacity:.5; cursor:default; }
  input { font:inherit; padding:8px 10px; border-radius:8px; border:1px solid var(--line); background:var(--bg); color:var(--text); width:100%; }
  .pill { display:inline-block; padding:2px 9px; border-radius:99px; font-size:12px; font-weight:600; }
  .pill.on { background:color-mix(in srgb, var(--ok) 15%, transparent); color:var(--ok); }
  .pill.off { background:color-mix(in srgb, var(--danger) 15%, transparent); color:var(--danger); }
  .pill.wait { background:var(--soft); color:var(--accent); margin-left:6px; }
  .stats { display:grid; grid-template-columns:repeat(4, 1fr); gap:12px; margin-bottom:14px; }
  .stat .n { font-size:18px; font-weight:600; }
  @media (max-width:560px) { .stats { grid-template-columns:repeat(2, 1fr); } }
  table { width:100%; border-collapse:collapse; }
  th { text-align:left; font-weight:600; color:var(--dim); font-size:12px; padding:0 8px 8px; }
  td { padding:10px 8px; border-top:1px solid var(--line); }
  td.num, th.num { text-align:right; }
  .empty { color:var(--dim); padding:14px 0 2px; }
  .err { color:var(--danger); margin-top:10px; }
  @media (max-width:560px) { .hide-sm { display:none; } }
</style>
</head>
<body>
<main>
  <h1>Sesiones en vivo</h1>
  <p class="sub">Servidor de XTreme por internet. Las salas no muestran el código ni el proyecto: el servidor no los conoce.</p>

  <section class="card" id="login" hidden>
    <div class="label" style="margin-bottom:8px">Clave de administración</div>
    <form class="row" id="loginForm">
      <div class="grow"><input id="key" type="password" autocomplete="current-password" placeholder="Pegá la clave"></div>
      <button class="primary" type="submit">Entrar</button>
    </form>
    <div class="err" id="loginErr" hidden>Clave incorrecta.</div>
  </section>

  <div id="app" hidden>
    <section class="card row">
      <div class="grow">
        <div class="label">Sesiones nuevas <span class="pill" id="pausePill"></span></div>
        <div class="dim" id="pauseText"></div>
      </div>
      <button id="pauseBtn"></button>
    </section>

    <section class="card">
      <div class="row" style="margin-bottom:10px">
        <div class="grow label" id="count"></div>
        <span class="dim" id="updated"></span>
        <button id="logout">Salir</button>
      </div>
      <table>
        <thead><tr><th>Sala</th><th class="hide-sm">Región</th><th class="num">Personas</th><th class="num">Abierta hace</th><th class="num hide-sm">Tráfico</th><th></th></tr></thead>
        <tbody id="rooms"></tbody>
      </table>
      <div class="empty" id="empty" hidden>No hay sesiones abiertas.</div>
      <div class="err" id="err" hidden></div>
    </section>

    <section class="card">
      <div class="label" style="margin-bottom:10px" id="monthTitle"></div>
      <div class="stats">
        <div class="stat"><div class="dim">Sesiones</div><div class="n" id="mSessions"></div></div>
        <div class="stat"><div class="dim">Horas</div><div class="n" id="mHours"></div></div>
        <div class="stat"><div class="dim">Tráfico</div><div class="n" id="mBytes"></div></div>
        <div class="stat"><div class="dim">Pico de invitados</div><div class="n" id="mPeak"></div></div>
      </div>
      <table>
        <thead><tr><th>Día</th><th class="num">Sesiones</th><th class="num hide-sm">Horas</th><th class="num">Tráfico</th><th class="num">Pico</th></tr></thead>
        <tbody id="days"></tbody>
      </table>
      <div class="empty" id="noDays" hidden>Todavía no hay historial.</div>
    </section>
  </div>
</main>
<script>
  const KEY = "xtreme-admin-key";
  let key = "";
  try { key = localStorage.getItem(KEY) || ""; } catch {}
  let state = null, timer = 0;
  const $ = id => document.getElementById(id);

  async function api(path, body) {
    const r = await fetch("/admin/api/" + path, {
      method: body ? "POST" : "GET",
      headers: { authorization: "Bearer " + key, ...(body ? { "content-type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (r.status === 401) { showLogin(true); throw new Error("unauthorized"); }
    if (!r.ok) throw new Error("El servidor respondió " + r.status);
    return r.json();
  }

  function showLogin(wrong) {
    clearInterval(timer);
    $("app").hidden = true;
    $("login").hidden = false;
    $("loginErr").hidden = !wrong;
    $("key").focus();
  }

  function ago(ms) {
    const m = Math.floor(ms / 60000);
    if (m < 1) return "menos de 1 min";
    if (m < 60) return m + " min";
    const h = Math.floor(m / 60);
    return h + " h " + (m % 60) + " min";
  }

  function size(b) {
    if (b < 1 << 20) return Math.round(b / 1024) + " KB";
    if (b < 1 << 30) return (b / (1 << 20)).toFixed(1) + " MB";
    return (b / (1 << 30)).toFixed(2) + " GB";
  }

  const REGIONS = { sam: "Sudamérica", wnam: "Norteamérica oeste", enam: "Norteamérica este", weur: "Europa oeste", eeur: "Europa este", apac: "Asia-Pacífico", oc: "Oceanía", afr: "África", me: "Medio Oriente" };
  const hours = s => (s / 3600).toFixed(1);

  function row(cells) {
    const tr = document.createElement("tr");
    for (const [cls, text, pill] of cells) {
      const td = document.createElement("td");
      td.className = cls;
      td.textContent = text;
      if (pill) {
        const p = document.createElement("span");
        p.className = "pill wait";
        p.textContent = pill;
        td.append(p);
      }
      tr.append(td);
    }
    return tr;
  }

  function render() {
    const s = state;
    $("pausePill").textContent = s.paused ? "Apagadas" : "Permitidas";
    $("pausePill").className = "pill " + (s.paused ? "off" : "on");
    $("pauseText").textContent = s.paused
      ? "Nadie puede crear una sesión por internet. Las abiertas siguen."
      : "Cualquiera con XTreme puede crear una sesión por internet.";
    $("pauseBtn").textContent = s.paused ? "Permitir" : "Apagar";
    $("pauseBtn").className = s.paused ? "primary" : "danger";
    $("count").textContent = s.rooms.length === 1 ? "1 sesión abierta" : s.rooms.length + " sesiones abiertas";
    $("updated").textContent = "Actualizado " + new Date().toLocaleTimeString();
    $("empty").hidden = s.rooms.length > 0;
    const tb = $("rooms");
    tb.replaceChildren(...s.rooms.map(r => {
      const tr = row([
        ["mono", r.room.slice(0, 8), r.away ? "Anfitrión reconectando" : null],
        ["hide-sm dim", REGIONS[r.region] || "—"],
        ["num", String(r.guests + (r.away ? 0 : 1))],
        ["num", ago(s.now - r.started)],
        ["num hide-sm", size(r.bytes)],
      ]);
      const td = document.createElement("td");
      td.className = "num";
      const b = document.createElement("button");
      b.className = "danger";
      b.textContent = "Cerrar";
      b.onclick = async () => {
        if (!confirm("¿Cerrar la sala " + r.room.slice(0, 8) + "? Se corta para todos; cada uno se queda con su copia del proyecto.")) return;
        b.disabled = true;
        try { await api("close", { room: r.room }); } catch (e) { error(e); }
        load();
      };
      td.append(b);
      tr.append(td);
      return tr;
    }));
    const m = s.month || { sessions: 0, seconds: 0, bytes: 0, peak: 0 };
    const [y, mo] = (m.month || "").split("-");
    $("monthTitle").textContent = "Este mes" + (mo ? " (" + mo + "/" + y + ")" : "");
    $("mSessions").textContent = m.sessions;
    $("mHours").textContent = hours(m.seconds);
    $("mBytes").textContent = size(m.bytes);
    $("mPeak").textContent = m.peak;
    const days = s.days || [];
    $("noDays").hidden = days.length > 0;
    $("days").replaceChildren(...days.map(d => row([
      ["", d.day.slice(8) + "/" + d.day.slice(5, 7)],
      ["num", String(d.sessions)],
      ["num hide-sm", hours(d.seconds)],
      ["num", size(d.bytes)],
      ["num", String(d.peak)],
    ])));
  }

  function error(e) {
    if (e.message === "unauthorized") return;
    $("err").hidden = false;
    $("err").textContent = e.message;
  }

  async function load() {
    try {
      state = await api("state");
      $("err").hidden = true;
      $("login").hidden = true;
      $("app").hidden = false;
      render();
    } catch (e) { error(e); }
  }

  $("pauseBtn").onclick = async () => {
    if (!state) return;
    if (!state.paused && !confirm("¿Apagar las sesiones nuevas? Las abiertas siguen hasta que terminen.")) return;
    try { await api("pause", { paused: !state.paused }); } catch (e) { error(e); }
    load();
  };

  $("loginForm").onsubmit = e => {
    e.preventDefault();
    key = $("key").value.trim();
    try { localStorage.setItem(KEY, key); } catch {}
    start();
  };

  $("logout").onclick = () => {
    key = "";
    try { localStorage.removeItem(KEY); } catch {}
    showLogin(false);
  };

  function start() {
    load();
    clearInterval(timer);
    timer = setInterval(load, 5000);
  }

  if (key) start(); else showLogin(false);
</script>
</body>
</html>`;
