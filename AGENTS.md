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
  XtremeDaw.Bench      xtbench.exe, XT-WorkBench (el Banco de pruebas): enlaza su ventana y estilos de App, sin depender de XTreme.dll.
  XtremeDaw.VstHost    xtvsthost.exe (C++, SDK de VST3): abre un plugin en su propio proceso. Lo compila el build de Audio.
tools/                 probar.ps1 (compila, comprueba y abre una copia de prueba), make-icon.ps1 (ícono: un vinilo con la X en la etiqueta que dibuja tools/icon/Vinilo.cs píxel a píxel, cada tamaño a su resolución; no necesita nada instalado; hasta 96 px, una versión plana; redondo y con transparencia en todos los tamaños), make-shortcut.ps1 (acceso directo), make-track-icons.py (íconos de pista), get-vst3sdk.ps1 (baja el SDK de VST3), get-basic-pitch.ps1 (baja el modelo de Extraer MIDI), get-gm.ps1 (baja el banco General MIDI), get-demucs.ps1 y make-demucs-onnx.py (arman los modelos de Separar en stems), get-ffmpeg.ps1 (baja los DLL de la preview), make-installer.ps1 (instalador), bloqueos.ps1 (hook: un archivo, una sesión a la vez)
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
  `tools\get-basic-pitch.ps1`: decisión del usuario). El banco General MIDI es GeneralUser GS (uso y redistribución libres, `lib\gm`, `tools\get-gm.ps1`, va en el instalador; decisión del usuario, #200: un reproductor de SF2 propio, sin NuGet). Fuera de NuGet, el
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
   reintentar ni matar nada mientras esperan (código 4 = el turno no llegó). **Al terminar cada cambio corré
   `tools\probar.ps1 -NoAbrir "qué probar"`** (un cambio que es solo del Banco, de xtlab o de las herramientas va con
   `-Tema banco`: compila el Banco y xtlab, sin copia de XTreme, y su Probar abre el Banco; pedido del usuario,
   2026-11-05) (compila, corre `xtlab comprobar-dsp`, deja una copia en
   `pruebas\`, fuera de AppData a propósito: desde la app de Claude, que es MSIX, lo escrito en AppData queda dentro
   del paquete y el acceso directo no lo ve, y anota el texto, corto y concreto, en el **Banco de pruebas**,
   `pruebas\lista.json`, que el usuario marca desde la copia; varias sesiones pueden anotar a la vez). **No preguntes si
   abrirlo**: queda en la lista y el usuario prueba todo junto después (pedido del usuario, 2026-11-03: con varios
   agentes trabajando). Abrí la copia solo si el usuario lo pide: `tools\probar.ps1 -Abrir` abre la última con la lista a
   la vista (`-Proyecto carpeta` la abre en ese proyecto). `tools\probar.ps1 -Lista` muestra lo pendiente y lo que el usuario
   marcó como que no anda, con su nota: miralo antes de seguir con algo ya entregado. Si arreglaste un ítem, corré
   `tools\probar.ps1 -NoAbrir -Arregla 12` (con su número): vuelve a Pendiente en lugar de duplicarse. Si el script dice
   "NO SE PUEDE PROBAR", contale por qué y no abras nada. Nunca abras el exe de `bin\` ni lo mandes a probar desde
   ahí: bloquea los DLL y el próximo build falla. Si hay un XTreme abierto, el script le pregunta al usuario en un
   cuadro si lo cierra (guarda antes); nunca lo cierres ni lo mates por otro lado. Si eligió cancelar (código 2), no
   insistas; si nadie contestó en 100 s (código 3), avisale y que te diga cuándo. La copia muestra en la barra de título "PRUEBA" + el commit (+ cambios), así el usuario sabe qué
   está probando.
3. **El trabajo sale de los Pedidos del Banco de pruebas** (pedido del usuario, 2026-11-04): el usuario carga ahí,
   numerados y con su tipo, los cambios, mejoras y correcciones que quiere, y los agentes los toman de ahí.
   `tools\probar.ps1 -Lista` los muestra primero. Antes de empezar uno (un pedido o algo marcado No anda, Anda mal o A
   mejorar), `tools\probar.ps1 -Toma 12`: lo reserva para tu sesión (código 2 = lo tiene otra: elegí otro); si lo dejás
   sin hacer, `-Suelta 12`. Al terminarlo, `tools\probar.ps1 -NoAbrir -Arregla 12` (con `"qué probar"` si hace falta
   decir cómo probarlo): pasa a Para probar para que el usuario lo pruebe. Los tres aceptan varios números (`-Arregla
   12,14`: una sola compilación y copia). Qué hacer según el tipo: **Error**, arreglarlo; **Mejora**, ajustarlo; **Función nueva**, hacerla, y antes
   preguntar lo que sea una decisión de diseño; **Idea**, no se programa de entrada: proponé cómo sería o llevala a
   PENDIENTES, y que el usuario decida. Si un pedido llega por el chat, anotalo primero (`tools\probar.ps1 -Pide "qué"
   -Tipo error|mejora|funcion|idea`), tomalo y seguí igual: así todo queda numerado en un solo lugar.

   **Cómo se mantiene sincronizado el Banco** (pedido del usuario, 2026-11-04: es uno de sus pilares para desarrollar;
   vale para toda sesión y todo subagente):
   - El Banco (tickets) se toca **solo** con `tools\probar.ps1` (o `xtlab lista`). Nunca edites, reescribas ni borres
     `pruebas\lista.json`, `lista.historial.jsonl` ni `lista.lock` a mano, con un script ni con git: varias sesiones y
     el usuario escriben a la vez, y `TestList` es lo único que lo hace con bloqueo y cambio atómico.
   - **Cada ítem es un ticket** (pedido del usuario, 2026-11-04) con su conversación en orden: el pedido o qué probar,
     cada arreglo, cada veredicto con su nota y los comentarios de los dos lados. Nada se pisa. Antes de trabajar uno,
     leelo entero: `tools\probar.ps1 -Ticket 12` (lo último que dijo el usuario manda, no solo el texto del principio).
   - Para decir algo sin mandar un arreglo (una pregunta, "lo llevé a PENDIENTES", "depende del #80"):
     `tools\probar.ps1 -Comenta 12 "texto"`. Le llega al usuario como **Nuevo**; él contesta en el mismo ticket.
     Una pregunta que bloquea el trabajo va también en el chat, si estás en uno.
   - **Cada ticket lleva su tema** (pedido del usuario, 2026-11-04: una lista fija, `TestItem.Topics`; `xtlab lista
     temas` dice qué va en cada uno). Lo que anotás va con el suyo (`-Tema mixer` junto a `-Pide`, `"qué probar"` o
     `-Arregla`), y un ticket que tomás sin tema se lo ponés (`tools\probar.ps1 -Clasifica 12 -Tema mixer`). La lista no
     se agranda sin preguntar: una parte nueva de la app entra en el tema donde la buscaría un músico.
   - **El tipo de un pedido también lo pone el agente** (pedido del usuario, #129: el Banco no lo pregunta): al tomar
     uno sin tipo, `tools\probar.ps1 -Clasifica 12 -Tipo mejora` (error | mejora | funcion | idea).
   - El `"qué probar"` de `-Arregla` es tu mensaje en el ticket ("Arreglo: …"): decí qué cambió y cómo probarlo. El
     texto del principio del ticket no cambia.
   - Cada cambio queda además en el historial con quién lo hizo (tu sesión sale de `CLAUDE_CODE_SESSION_ID`), también
     lo que se sacó de la lista. Si algo no cuadra (volvió, desapareció), mirá `tools\probar.ps1 -Historial 12`
     (`-Historial 0`: lo último de todo) y contale al usuario lo que dice.
   - **Cada ticket lleva un título** (pedido del usuario, 2026-11-04): al tomar uno sin título, ponéselo
     (`tools\probar.ps1 -Titula 12 "Fader del bus no guarda el valor"`): corto, de qué se trata, en el tono de la
     app. Es lo que el usuario ve en la lista; el pedido queda como primera entrada de la línea de tiempo del ticket.
   - `-Toma` antes de empezar, siempre; tu sesión lo puede volver a tomar sin problema. Un mensaje que empieza con
     `/xt_revisar-workbench` y números ya los toma solo al enviarse (hook `pedido` de `tools\bloqueos.ps1`, #173): así el
     Banco los muestra en curso mientras su Claude está abierto, aunque se reinicie. Código 2 dice qué sesión lo
     tiene: no lo trabajes. Lo que tome un subagente lo toma la sesión que lo lanzó (comparten la sesión).
   - `-Arregla` solo números que trabajaste vos en esta sesión y que están en un estado abierto. Si el usuario ya lo
     marcó **Anda**, `-Arregla` no lo reabre (código 2): si hiciste otro cambio, anotalo como ítem nuevo (`-NoAbrir "qué
     probar"`). No uses `-Arregla` para "recordarle" al usuario un ítem que sigue en Para probar.
   - **Al terminar bien, la sesión se cierra** (pedido del usuario, #143): si estás seguro de que todo salió bien (lo
     tuyo fue con `-Arregla` o `-Comenta` sin error y no queda nada que preguntar), lo que ibas a decir en el chat va en
     el ticket, y tu último paso es `tools\cerrar-sesion.ps1`: cierra la consola que abrió el Banco (fuera de ella no
     hace nada). Con una pregunta o algo a medias, no: el usuario te tiene que leer. Un subagente nunca lo corre.
   - Lo que anotás, devolvés o comentás llega al Banco como **Nuevo** y se anuncia: es lo que el usuario lee primero,
     así que va corto, concreto y dice cómo probarlo.
   - El usuario saca ítems y limpia los que andan desde el Banco: que un número ya no esté no es un error; el historial
     dice quién lo sacó. No lo vuelvas a anotar sin preguntar.
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
9. **Un archivo, una sesión a la vez** (regla estricta del usuario, 2026-11-03: se perdieron cambios). Un hook
   (`tools\bloqueos.ps1`, en `.claude\settings.json`) le da cada archivo que tocás a tu sesión hasta que termina el
   turno: otra sesión que quiera editarlo, o correr un comando que lo nombre, hace cola (el primero que llegó lo
   toma primero, pedido del usuario, 2026-11-05). Si en 90 s no le llega, el hook dice **EN COLA** y el puesto ("2° de
   3"): el lugar se guarda 5 min; seguí con otra parte si la hay, volvé a intentar la misma acción y contale al usuario
   en qué puesto estás. Lo de una sesión que se cerró sin terminar su turno (`tools\cerrar-sesion.ps1`, una consola
   cerrada, un cuelgue) queda libre al instante: cada bloqueo guarda su proceso de Claude. Una sesión y sus subagentes no se esperan entre sí (los subagentes hermanos, sí). Lo que cambie
   un comando o un script también queda tomado, salvo lo que otra sesión cambió mientras corría, y si pisó algo de
   otra sesión el hook avisa. Si te dice BLOQUEADO (una cola que vuelve a un archivo tuyo: nadie avanzaría) o ATENCIÓN: no lo
   esquives (ni con shell, ni Write, ni git, ni otro script), no deshagas nada, terminá el turno y avisale al usuario
   qué quedó sin hacer. **El Banco de pruebas y XTreme son dos programas aparte** (pedido del usuario, 2026-11-05): una
   sesión que editó archivos de uno no espera, no toma ni avisa por los del otro que cambien o nombren sus comandos
   (la lista de archivos del Banco es `$BenchFiles` en `tools\bloqueos.ps1`; lo que XTreme le presta, como `Loc.cs` o
   `App.axaml`, es de XTreme). Editar un archivo del otro sigue haciendo cola. Los textos del Banco (`test.*`) van en
   `Assets/Lang/Bench/`, aparte de los de la app. `git restore`, `stash`, `clean`, `reset --hard` y `checkout --`
   están bloqueados: tiran cambios sin commitear que pueden ser de otro; si hacen falta, los corre el usuario.
   `tools\bloqueos.ps1 -Lista` muestra quién tiene qué y quién hace cola; antes de un script que reescribe muchos archivos, miralo.
   El hook deja además el estado de cada sesión (en cola, bloqueada, atención) en `.claude\bloqueos\t-*.json`, y el Banco
   lo muestra como un semáforo en el "en curso" del ticket que tomó (#167).
   **Avisos entre sesiones** (pedido del usuario, #263): el hook te agrega al contexto, en tu próxima edición o comando,
   los "Avisos de otras sesiones" (se liberó lo que esperabas, otra sesión tomó un archivo que cambiaste, un comando
   ajeno cambió uno tuyo, una compilación falla en uno tuyo, un mensaje o un pedido). Tenelos en cuenta y seguí: si un
   archivo tuyo cambió, volvé a leerlo; solo una **ATENCIÓN** te hace parar y avisarle al usuario. Un PEDIDO de un
   archivo tuyo: soltalo apenas termines con él (`tools\bloqueos.ps1 -SueltaArchivo <ruta>`). Si estás en cola y lo
   necesitás, `-PideArchivo <ruta>`; para avisar algo que afecta a otro, `-Avisa <#ticket|sesión|todos> -Texto "…"`
   (corto; `-Atencion` solo si el otro tiene que parar). `-Buzon` lee los sin leer. Cada aviso queda además un día en
   `.claude\bloqueos\avisos.jsonl`. La ventana **Agentes** del Banco (#269, #273: un mapa de quién tiene qué y dónde se traban) no muestra esa conversación (#300), solo la consola de cada sesión; desde ahí el usuario también te
   escribe o te pide un archivo (llega como aviso del usuario).
10. No subas WAV ni binarios. No hagas commit ni push si el usuario no lo pide.
11. Las skills de este proyecto (`.claude/skills`) llevan el prefijo `xt_`, en la carpeta y en su `name`
   (`xt_revisar-workbench`; decisión del usuario, 2026-11-03). El mod `xt-panel` (`.claude/plugins/xt-panel`, del
   catálogo local `xtreme`, activado en `.claude/settings.json`) abre un panel al costado del chat con un botón por
   acción de esas skills (no la barra de arriba del prompt: es una sola y la usa show-tokens). Una skill `xt_` nueva
   aparece sola, en una tarjeta con su `description` y, si tiene `argument-hint`, un campo; las que tienen botones
   propios están en `SECTIONS` y `COVERED`. El panel también pide skills nuevas (campo «Nueva skill»). Cambios al mod
   se recargan con `/reload-plugins`.
