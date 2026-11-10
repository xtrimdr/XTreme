# Guía para agentes — XTreme

Instrucciones para cualquier agente de IA (Claude, Codex, Copilot, Cursor…) que siga con este proyecto. Acá van las
reglas que valen siempre. Las demás se cargan según dónde trabajes:

- [src/XtremeDaw.App/AGENTS.md](src/XtremeDaw.App/AGENTS.md): diseño de la ventana (tokens, tipografía, clases, atajos, mensajes).
- [src/XtremeDaw.App/Controls/AGENTS.md](src/XtremeDaw.App/Controls/AGENTS.md): controles dibujados a mano y medidores.
- [src/XtremeDaw.Audio/AGENTS.md](src/XtremeDaw.Audio/AGENTS.md): el motor por dentro (cadena por pista, slots, automatización en el motor, VST, compensación de latencia, stems, componentes, decodificar).
- [src/XtremeDaw.Projects/AGENTS.md](src/XtremeDaw.Projects/AGENTS.md): el modelo del proyecto (media, pistas, carpetas, buses y ruteo, clips, sampler, granular, MIDI, automatización) y su carpeta.
- [docs/interfaz.md](docs/interfaz.md): cómo es y dónde vive cada parte de la ventana. Leé solo el apartado de lo que vas a tocar.

Si algo de estas guías deja de ser cierto, actualizalo en el mismo cambio. Contexto general en [README.md](README.md), requisitos, compilación, atajos y `xtlab` en [DETALLES.md](DETALLES.md),
lo que falta en [PENDIENTES.md](PENDIENTES.md); [HECHO.md](HECHO.md) es historial y no hace falta leerlo de entrada.

## 1. Qué es

- **XTreme** (internamente "Xtreme DAW") es un **DAW completo** (decisión del usuario, 2026-11-03: "un DAW puro y
  duro"): audio y MIDI, instrumentos y efectos, buses y mastering, para cualquier instrumento y estilo. El video es un
  extra: si se graba con cámara, cada canal guarda su imagen y sale un video con un panel por canal. El exe es
  `XTreme.exe` y los ajustes van en `%APPDATA%\XTreme` (`AppFolder`). El archivo del proyecto es `<Nombre>.xtp` (JSON por dentro, decisión del usuario, 2026-11-03), dentro de su carpeta; un `xtreme_project.json` se renombra solo al abrir (`ProjectStore.MatchFileToName`).
  Los ajustes de los nombres anteriores de la app no se leen ni se migran (decisión del usuario, 2026-11-02); los
  proyectos de WildTrack sí abren (`guitarcam.json`, formato `guitarcam-project`: pasan a `.xtp` al abrirlos; pedido del usuario, 2026-11-03).
- **Nivel de DAW profesional.** Una función nueva se mide contra lo que ofrece un DAW serio, no contra el video. El
  video no condiciona al resto: sin cámara la app tiene que ser un DAW completo.
- **Las decisiones son del usuario.** Lo que describen estas guías lo eligió el usuario: no lo cambies sin preguntar,
  aunque otro DAW lo haga distinto. La app nació como port de una web que **ya no es referencia**:
  las menciones a la web en el código son historia, no reglas.

## 2. Idioma

| Qué | Idioma |
|---|---|
| Textos de la interfaz, mensajes, tooltips | **Español rioplatense, con voseo** ("Abrí", "Elegí", "probalo"), vía `Loc` |
| Comandos, opciones y textos de `xtlab` | Español (`grabar --segundos 15 --vida 0.3`) |
| README, DETALLES, PENDIENTES, HECHO, guías | Español |
| Identificadores, comentarios y `<summary>` | Inglés |
| Mensajes de commit | Inglés, imperativo, una línea descriptiva ("Decode real video in the preview with FFmpeg") |
| Valores guardados en el JSON | Inglés (`"tube"`, `"tape"`, `"side-by-side"`…) |

- La app es multilenguaje (`Assets/Lang/<código>.json`, **es.json es la referencia**): texto nuevo → clave nueva en
  todos los idiomas. Detalles en la guía de la ventana.
- `Audio` y `Projects` no conocen `Loc`: sus mensajes pasan por `Texts.T(clave, español, args...)` (en `XtremeDaw.Audio`,
  prefijos `err.`, `store.`, `camera.`…), que `Loc.Load` conecta; sin esa conexión (xtlab) sale el español.
- Siguen en español a propósito las plantillas que el usuario ya guardó y los nombres que se crean ("Canal 1").
- Vocabulario de músico, no de programador: **canal** (lo grabado también: "toma" no se usa más), **pista**, **click**,
  **Saturación** (no "Vida", decisión del usuario, 2026-11-03; su perilla es **Drive**, niveles Apenas / Sutil / Media /
  Caliente / Al palo), **Válvula / Cinta**, **Rasgueo / Arpegio / Punteo**, **Ver todo**. Las opciones de `xtlab`
  conservan `vida` como nombre.
- **Tono profesional** (pedido del usuario, 2026-10-29: es una app profesional): corto y al grano en todo, botones,
  tooltips, estados, avisos y ayudas. Sin humor, sin charla ni frases de relleno, sin explicar lo obvio ni cómo
  funciona por dentro; si un texto no le suma nada al usuario, no va. Un estado entra en una línea ("Activo · latencia
  5 ms, compensada", "Cargando…").

## 3. Arquitectura

```
src/
  XtremeDaw.Audio      motor de audio: DSP, ASIO dúplex, click, grabación a disco. Sin UI.
  XtremeDaw.Projects   modelo del proyecto, guardado atómico, historial. Sin UI.
  XtremeDaw.App        la ventana (Avalonia 11.3). Depende de Audio y Projects.
  XtremeDaw.Lab        xtlab.exe: consola para probar audio, DSP y proyectos sin la ventana.
  XtremeDaw.VstHost    xtvsthost.exe (C++, SDK de VST3): abre un plugin en su propio proceso. Lo compila el build de Audio.
tools/                 probar.ps1 (compila, comprueba y abre una copia de prueba), make-icon.ps1 (ícono: un vinilo con la X en la etiqueta que dibuja tools/icon/Vinilo.cs píxel a píxel, cada tamaño a su resolución; no necesita nada instalado; hasta 96 px, una versión plana; redondo y con transparencia en todos los tamaños), make-shortcut.ps1 (acceso directo), make-track-icons.py (íconos de pista), get-vst3sdk.ps1 (baja el SDK de VST3), get-basic-pitch.ps1 (baja el modelo de Extraer MIDI), get-gm.ps1 (baja el banco General MIDI), get-demucs.ps1 y make-demucs-onnx.py (arman los modelos de Separar en stems), get-ffmpeg.ps1 (baja los DLL de la preview), make-installer.ps1 (instalador)
relay/                 el servidor de las sesiones en vivo por internet (#320): un Worker de Cloudflare con un Durable Object por sala (`npx wrangler deploy` desde ahí; `npx wrangler dev` para probarlo local con `XTREME_RELAY`), publicado en `https://xtreme-live.martinperez86.workers.dev`, con su página privada `/admin` (#321: ver las sesiones abiertas, cerrar una, apagar las nuevas; la clave está en `relay/.admin-key`, fuera de git)
.github/workflows/instalador.yml   arma el instalador en GitHub Actions (un tag vX.Y.Z → release en borrador; firma con SignPath)
```

- Las dependencias van **hacia abajo**: App → Projects → Audio. `Audio` y `Projects` no referencian Avalonia; todo lo
  que se pueda probar desde `xtlab` vive ahí.
- Cada función nueva del motor o del proyecto se puede ejercitar desde **`xtlab`** (comando u opción nueva, con su
  ayuda actualizada). `xtlab` usa cultura invariante (punto decimal) a propósito.
- `net10.0-windows`, x64, `Nullable`. Audio por **ASIO** (NAudio), video con **FFmpeg/ffprobe** por proceso y pipe (por nombre: en el PATH, o la carpeta de winget, que `FfmpegSetup.RefreshPath` suma al PATH del proceso al arrancar),
  salvo la preview: la decodifica FFmpeg dentro del proceso (`NativeVideoSource`, FFmpeg.AutoGen con los DLL de FFmpeg 9
  en `lib\ffmpeg`, fuera de git; ver DETALLES.md).
- Sin MVVM ni bindings: code-behind con controles con nombre. No metas un framework sin que el usuario lo pida.
- Sin dependencias nuevas de NuGet sin preguntar. Hoy: Avalonia 11.3, NAudio 2.2, FFmpeg.AutoGen 9.0 y ONNX Runtime
  con DirectML (`Microsoft.ML.OnnxRuntime.DirectML`, decisión del usuario: sus versiones van atrás de las del paquete común; para Extraer MIDI, con el modelo de Basic Pitch de Spotify, Apache 2.0, `lib\basic-pitch`,
  `tools\get-basic-pitch.ps1`: decisión del usuario). Los bancos General MIDI (GeneralUser GS, FluidR3 GM, MuseScore General) son descargas opcionales, ninguno va en el instalador (pedido del usuario, #304; un reproductor de SF2 propio, sin NuGet, #200). Fuera de NuGet, el
  SDK de VST3 de Steinberg (MIT, `lib\vst3sdk`, `tools\get-vst3sdk.ps1`) para `xtvsthost.exe` (decisión del usuario).
- **Los detalles de cada parte están en su guía** (lista de arriba): se cargan solas al leer un archivo de esa carpeta.
  La ventana usa el modelo y el motor todo el tiempo: si tu cambio los toca desde `App`, leé antes el apartado que
  corresponda de [Audio](src/XtremeDaw.Audio/AGENTS.md) o [Projects](src/XtremeDaw.Projects/AGENTS.md).

Convenciones: tiempos del timeline en **segundos**, correcciones de latencia/sincronía en **ms**. JSON en camelCase
(`ProjectStore.Json`). Cada clase lleva `[JsonExtensionData] Extra`, que conserva lo desconocido: no lo saques.
`Normalize()` rellena lo que falte o venga roto (todo campo nuevo tiene valor por defecto y se normaliza ahí). Cambio
incompatible de formato → subí `Project.CurrentVersion` y migrá al abrir. IDs con prefijo (`Ids.New("prj")`).

### Guardado

- Todo guardado es **atómico**: temporal → forzado a disco → `File.Replace`. Nunca escribas el `.xtp` directo.
- **Perder una toma es el peor error posible.** Todo lo que toque archivos del usuario piensa en qué pasa si el
  programa muere a la mitad. La carpeta del proyecto, cuándo se guarda y cómo se escribe una toma: en la guía de Projects.

## 4. Audio en tiempo real

**Nivel profesional**, como Logic Pro o Fender Studio Pro: nada de clics ni cortes. Arrancar, parar y hacer scrub
llevan su fundido (`PlaybackEngine.TransportFadeMs`, elegible en Configuración); los saltos, crossfade de 10 ms; el bucle no salta: gira en la muestra exacta y A entra entero, sin fundido (`LoopSplice`, pedido del usuario);
el fundido del transporte va sobre lo que toca cada pista, antes de sus slots, y parado la cadena sigue corriendo mientras suena su cola (`LiveTrack.Ringing`: una reverb o un delay de pista no se cortan al parar); `Stop`/`Pause` cortan en el acto y son solo para grabar, exportar o cambiar de proyecto. El estado de los efectos no
se reinicia en medio de la música. Lo que se dude se mide (`AudioGapLog`) en vez de suponerlo.

El callback de ASIO (`AsioDuplex.ProcessBlock`, `PlaybackEngine.Process`) corre en el hilo del driver:

- **No asignes memoria, no bloquees, no uses locks, no toques disco ni la UI.** Tampoco LINQ que asigne, `async`,
  logs, excepciones ni métodos genéricos (no se precompilan).
- Buffers preasignados (8192 muestras por canal en el motor y en cada `LiveTrack`). Datos nuevos al motor =
  **reemplazar la referencia entera** (`volatile LiveTrack[] tracks`); la posición va con `Interlocked`. Lo que va a
  disco sale por otro hilo (`RecordingWriter`).
- **La basura de la UI también corta el audio**: un GC gen1 deja huecos de 10–15 ms. En cada `Tick` o `Render` no se
  asigna (ni se reconstruyen controles, ni pinceles o textos por cuadro).
- `AudioWarmUp` precompila al arrancar `XtremeDaw.Audio`, `XtremeDaw.App.Audio` y `XtremeDaw.App.Model`: el código de
  tiempo real nuevo va en esos namespaces (o se suma a la lista).
- Motor a 48 kHz (`PlaybackEngine.SampleRate`); los medios se remuestrean con `AudioLoader`. Si un cambio sube mucho la
  carga "DSP x %" de la barra de título, decilo.

La cadena por pista, la automatización en el motor y los slots están en la [guía del motor](src/XtremeDaw.Audio/AGENTS.md).
`xtlab comprobar-dsp` corre las comprobaciones del DSP.

## 5. Estilo de código

- Namespaces por archivo, clases `sealed`, sin `private` explícito, `readonly` donde se pueda, expresiones de
  colección (`[]`), `var`, nombres cortos en código numérico (`n`, `ml`, `pos`).
- **Cada clase lleva un `<summary>`** con qué es y por qué está hecha así. Comentarios internos pocos y para el *por qué*.
- Constantes con significado, con nombre y comentario (`SaveDelay`, `Lookahead`, `PeakBin`).
- El proyecto pertenece al hilo de la UI; lo de otros hilos vuelve con `Dispatcher.UIThread.Post`.

## 6. Cómo trabajar

1. `dotnet build` de `XtremeDaw.slnx` compila sin warnings nuevos.
2. **Se trabaja en esta carpeta, en `master`, sin ramas ni worktrees** (lo pidió el usuario): así hay un solo build y
   lo que se prueba es lo que está acá. Probá el motor y el proyecto con `xtlab`. Compilar, comprobar y copiar van de a
   una sesión por vez: `dotnet build` (`tools\turno.targets`), `probar.ps1` y los comandos pesados de `xtlab`
   (`HeavyTurn`) esperan su turno solos y dicen a quién esperan, así que correlos con un timeout de 10 minutos, sin
   reintentar ni matar nada mientras esperan (código 4 = el turno no llegó). `tools\probar.ps1 -NoAbrir` compila, corre
   `xtlab comprobar-dsp` y deja una copia en `pruebas\` (fuera de AppData a propósito: desde la app de Claude, que es
   MSIX, lo escrito en AppData queda dentro del paquete y el acceso directo no lo ve). **No preguntes si abrirla**: el
   usuario prueba desde el ticket (pedido del usuario, 2026-11-03: con varios agentes trabajando), con el botón Probar
   de ClaudeWorkBench (`tools\probar.ps1 -Abrir -Probar 12`). Abrí la copia solo si el usuario lo pide:
   `tools\probar.ps1 -Abrir` abre la última (`-Proyecto carpeta` la abre en ese proyecto). Si el script dice "NO SE
   PUEDE PROBAR", contale por qué y no abras nada. Nunca abras el exe de `bin\` ni lo mandes a probar desde ahí:
   bloquea los DLL y el próximo build falla. Si hay un XTreme abierto, el script le pregunta al usuario en un cuadro si
   lo cierra (guarda antes); nunca lo cierres ni lo mates por otro lado. Si eligió cancelar (código 2), no insistas; si
   nadie contestó en 100 s (código 3), avisale y que te diga cuándo. La copia muestra en la barra de título "PRUEBA" +
   el commit (+ cambios), así el usuario sabe qué está probando.
3. **El trabajo sale de los tickets de ClaudeWorkBench** (`wb`, su base en `.workbench\`; reemplazó al Banco de
   pruebas viejo, #37): el usuario carga ahí los pedidos y marca lo que probó, y los agentes los trabajan con
   `/workbench:revisar` (cómo se toman, titulan, clasifican, comentan y mandan a probar está en esa skill). Los tickets
   se tocan **solo** con `wb`: nunca edites `.workbench\` a mano. Qué hacer según el tipo: **Error**, arreglarlo;
   **Mejora**, ajustarlo; **Función nueva**, hacerla, y antes preguntar lo que sea una decisión de diseño; **Idea**, no
   se programa de entrada: proponé cómo sería o llevala a PENDIENTES, y que el usuario decida. El código y los datos del
   Banco viejo (XT-WorkBench) quedaron en `archivo\banco-viejo.zip` y `pruebas\archivo-banco\`, fuera de git.
4. Para probar de verdad hacen falta una interfaz ASIO y un proyecto con tomas: si no los tenés, decí qué no probaste.
5. En capturas de GDI los píxeles negros tienen alfa 0 y algunos visores los pintan blancos: mirá el RGB.
6. Al terminar un ítem de PENDIENTES, sacalo de ahí y agregalo arriba de todo en HECHO con la fecha (AAAA-MM-DD) y una
   línea de qué se hizo y dónde. Ideas nuevas van en PENDIENTES, en su sección.
7. Si cambiás atajos, comandos de `xtlab` o la estructura, actualizá DETALLES.md y DETALLES_EN.md.
8. Si cambiás cómo se usa algo o el nombre de un control, actualizá su tema de la ayuda en todos los idiomas
   (`src/XtremeDaw.App/Assets/Help`, ver "Ayuda" en docs/interfaz.md); una función nueva que alguien vendría a buscar
   lleva su tema. Lo que el usuario va a notar suma una línea en la versión más nueva de `Assets/News` (todos los idiomas,
   ver "Novedades" en docs/interfaz.md), que puede ser la próxima, aún sin publicar. Al publicar, esa versión tiene que ser
   la de `Directory.Build.props`.
9. **Un archivo, una sesión a la vez** (regla estricta del usuario, 2026-11-03: se perdieron cambios). Los hooks de
   ClaudeWorkBench le dan cada archivo que editás a tu sesión: si otra lo tiene, la edición espera o el hook dice
   **EN COLA** (seguí con otra parte y volvé a intentar) o **BLOQUEADO** (no lo esquives, ni con shell, ni Write, ni
   git, ni otro script; terminá el turno y avisale al usuario qué quedó sin hacer). Los comandos de git que descartan
   cambios sin commitear (restore, stash, clean, reset --hard) están bloqueados: pueden ser de otro; si hacen falta,
   los corre el usuario.
10. No subas WAV ni binarios. No hagas commit ni push si el usuario no lo pide.
11. Las skills de este proyecto (`.claude/skills`) llevan el prefijo `xt_`, en la carpeta y en su `name`
   (`xt_launch`; decisión del usuario, 2026-11-03).
