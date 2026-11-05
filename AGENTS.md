# Guía para agentes — XTreme

Instrucciones para cualquier agente de IA (Claude, Codex, Copilot, Cursor…) que siga con este proyecto. Acá van las
reglas que valen siempre. Las demás se cargan según dónde trabajes:

- [src/XtremeDaw.App/AGENTS.md](src/XtremeDaw.App/AGENTS.md): diseño de la ventana (tokens, tipografía, clases, atajos, mensajes).
- [src/XtremeDaw.App/Controls/AGENTS.md](src/XtremeDaw.App/Controls/AGENTS.md): controles dibujados a mano y medidores.
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
tools/                 probar.ps1 (compila, comprueba y abre una copia de prueba), make-icon.ps1 (ícono: un vinilo con la X en la etiqueta que dibuja tools/icon/Vinilo.cs píxel a píxel, cada tamaño a su resolución; no necesita nada instalado; hasta 96 px, una versión plana; redondo y con transparencia en todos los tamaños), make-shortcut.ps1 (acceso directo), make-track-icons.py (íconos de pista), get-vst3sdk.ps1 (baja el SDK de VST3), get-basic-pitch.ps1 (baja el modelo de Extraer MIDI), get-demucs.ps1 y make-demucs-onnx.py (arman los modelos de Separar en stems), get-ffmpeg.ps1 (baja los DLL de la preview), make-installer.ps1 (instalador), bloqueos.ps1 (hook: un archivo, una sesión a la vez)
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
  `tools\get-basic-pitch.ps1`: decisión del usuario). Fuera de NuGet, el
  SDK de VST3 de Steinberg (MIT, `lib\vst3sdk`, `tools\get-vst3sdk.ps1`) para `xtvsthost.exe` (decisión del usuario).
- **Separar en stems** (`StemSeparator`): Demucs por ONNX Runtime, en la placa de video (DirectML: NVIDIA, AMD, Intel) o
  en la CPU. Los grafos llevan solo la red; la STFT y su inversa están en C#, iguales a las de Demucs (`xtlab separar
  --sin-desplazar` contra PyTorch: -80 dB, en las dos). DirectML fusiona cada `Sigmoid` con la operación vecina y el audio
  sale mal sin ningún error: por eso sus salidas van expuestas en el grafo (`make-demucs-onnx.py`), y por eso `Choose` no
  confía en una placa hasta que cada modelo coincide con la CPU en un segmento del audio y le gana en tiempo. `lib\demucs` (`tools\get-demucs.ps1`) no va
  en git ni en el instalador (`CopyToPublishDirectory="Never"`): la licencia de los pesos no está clara (ver PENDIENTES).
- **Componentes opcionales** (`XtremeDaw.Audio/Components.cs`): lo que la app puede no tener (FFmpeg, modelos de stems). Uno nuevo se suma a `Components.All` y a sus textos `component.<id>.*`; se instala siempre por `ComponentInstall.Run` (uno a la vez, con progreso y cancelación) y se muestra con `ComponentList`. Lo que depende de él queda deshabilitado con un aviso. Las descargas pasan por `FileDownload` (`.part`, reanudación, SHA-256). Los modelos de stems no se alojan (decisión del usuario): se bajan los `.th` de Meta y `StemConverter` arma el ONNX en `%APPDATA%\XTreme\demucs` desde `lib\demucs-graphs` (grafos sin pesos, `tools\make-demucs-templates.py`). Tienen sus propios términos: nunca digas que son MIT.
- **Decodificar** (`MediaDecoder`): NAudio lo que lee solo, ffmpeg.exe por pipe todo lo demás. Lo que vino de FFmpeg
  entra al proyecto como WAV, para que un proyecto abra sin FFmpeg; abrir un proyecto no llama a FFmpeg.
- **Plugins VST3 y VST2, de 64 y 32 bits** (`XtremeDaw.Audio/Vst`, decisión del usuario, 2026-11-02): un plugin de 32 bits solo carga en un proceso de 32 bits, así que el mismo fuente da también `xtvsthost32.exe` y `VstHost.Find(archivo)` elige por los bits del archivo (`PluginFile`, sin cargarlo); un `.dll` es VST2 (`vst2plugin.h`, el mismo protocolo que VST3), el resto VST3. Steinberg ya no licencia el SDK de VST2: `vst2.h` es un encabezado propio con la interfaz binaria, sin nada de ese SDK (decisión del usuario, sabiendo que es zona gris legal); no metas el `aeffect.h` oficial en el repo. Cada instancia corre en su proceso (decisión del usuario: un plugin
  que se cuelga no tira abajo la grabación). Hablan por memoria compartida (`shared.h` ↔ `VstBridge`, los offsets tienen
  que coincidir) y líneas de texto por stdin/stdout. En vivo el hilo de audio nunca espera al plugin: lo oye un bloque
  tarde y le manda las notas ese bloque (más su latencia) antes (`InstrumentPlayer`, que también ve venir el giro del bucle y manda el principio de la vuelta a tiempo); la exportación espera cada bloque
  (`InstrumentRender`, dentro de `MixRenderer`). El puente lleva notas y controladores (`VstNote.Control`: CC y pitch bend de las notas escritas, `NoteExpression`; en VST2 como MIDI, en VST3 por el `IMidiMapping` del plugin, y sin ese mapeo los ignora); el pitch bend y la modulación de un teclado en vivo todavía no. En vivo se le anuncia el bloque real del driver (`--block`,
  `VstInsert.LiveBlock`), no el máximo: los plugins dimensionan su trabajo por ese número y pagan por lo que no usan
  (Splice INSTRUMENT, el doble); un bloque mayor igual funciona, el host lo parte en pedazos. Un instrumento que recién abre puede seguir cargando sus samples y
perder las notas: antes de ponerlo en la pista y antes de exportar se prueba una nota hasta que suena (`InstrumentWarmup`). El host avisa dónde está la ventana del plugin (`editor-rect`, `editor-active`) para que el teclado virtual sepa dónde tocar, y la ventana se viste y se llena desde la app (`look`, `title`, `toolbar`, `keys-panel`: el panel del teclado es una ventana de la app hija de la del host). El host es per-monitor DPI aware: le da la escala de la pantalla al plugin (`setContentScaleFactor`), trabaja en píxeles reales y escala solo lo suyo (la barra y el panel, que la app le pide a 100 %); prender el panel no le avisa nada al plugin. Un **efecto** es un slot más (`VstInsert`, tipo `vst`): en vivo manda
  cada bloque y toma el anterior, así que se oye un bloque del driver (`VstInsert.LiveBlock`) más su latencia tarde (un bloque más hasta el próximo Play si el plugin llegó tarde: `FrameQueue`, el bloque atrasado se oye en su lugar y la compensación de latencia se corre con él); en
  la exportación abre su propia copia y espera cada bloque. La ventana de la app maneja procesos y estados
  (`MainWindow.Vst.cs` los instrumentos, `MainWindow.VstFx.cs` los efectos, que el motor encuentra por `LiveVst`).
- **Compensación de latencia (PDC)**: lo que un slot atrasa (`ITrackInsert.LatencySamples`) lo espera el resto de la
  mezcla. `MixLatency.Compute` (un solo cálculo para el vivo y la exportación) dice cuánto espera cada tramo (la salida
  y cada envío de pistas y nodos, `LatencyDelay`) para que todo llegue junto a cada nodo y al master; el master queda
  `PdcFrames` tarde y con eso se corren el click, las notas grabadas y las tomas (`PlaybackEngine.UpdateLatency`, en
  cada tick, solo si algo cambió). La exportación recorta ese atraso del principio, como el del limitador.
  Lo que se ve (imagen, cabezal, reloj) va con lo que se oye: `PlaybackEngine.HeardSec` (`HeardClock`: tras arrancar o saltar espera en el destino, en un bucle termina la vuelta); `PositionSec` sigue siendo dónde se leen las pistas, y es lo que usan la grabación, las ediciones y la automatización nativa. Al parar (`FadeOutAndPause`) la posición vuelve a lo último oído, para que seguir no saltee nada.
  **Una toma tiene un solo corrimiento**: mientras graba, la demora no se mueve. `VstInsert.Steady` hace que cada slot arranque con el bloque extra ya dado (`LiveLag`): un bloque tarde entra en su lugar en vez de atrasar la mezcla, y `StartRecording` calcula la PDC antes del primer sample. Si un plugin se va, `MixLatency.Compute(minMaster)` mantiene la demora.
  La lista de plugins (`PluginCatalog`, `plugins.json`; carpetas extra y ocultos en `config.json`) se administra en
  Configuración → Plugins; en la ventana pasa siempre por `PluginScan` (una sola lista y una sola búsqueda a la vez).

### Modelo del proyecto (`XtremeDaw.Projects`)

- **Media**: grabaciones e importados. **Nunca se reescriben.** Un importado puede quedar vinculado: fuera de `media/`, con la ruta entera (`Media.Linked`, decisión del usuario; `AudioImport.LinkIn`, `ImportMode`); `LinkedMedia` lo copia adentro o reencuentra un archivo que se movió, sin tocar el de afuera. De una toma, el deshacer solo guarda dónde están sus archivos (`UndoManager.Place`).
- **Track**: el canal (volumen, paneo, mute/solo, efectos…), si lleva panel de video, `Color` ("#rrggbb") e `Icon`
  (null = sin color / con su número), `FolderId` = su carpeta.
- **Folder** (`Project.Folders`): agrupa pistas. Sus hijas van juntas en `Project.Tracks` y la carpeta va justo antes
  (`Folder.Index`). Toda edición de orden pasa por `TrackFolders` y `TrackFolders.Normalize` repara lo que quede raro.
  **Es un canal** (decisión del usuario, 2026-11-04, #134; antes solo contenía): sus pistas suman ahí, pasan por sus
  slots (`Folder.Inserts`), paneo, fader (`Volume`) y mute, y van a su `Output` y sus `Sends` (PRE como los de una
  pista: después de los slots y el paneo). Su `Solo` vale como el solo de cada una de sus pistas (`ChannelDsp.Soloed`,
  el mismo cálculo en vivo, en la exportación y en el timeline); su mute calla su nodo. La automatización de carpetas
  todavía no está. Lo que la versión 2 guardaba con esos nombres vuelve como era. Un **separador** es una `Folder` con `Separator`: va en
  el mismo orden, nunca toma pistas ni rutea ni tiene canal, y se puede ocultar del timeline (`HiddenInTimeline`). Pistas de la raíz,
  carpetas y buses tienen su lado del mixer (`MixerSide`, `MixerSides`: null = el de siempre).
- **Buses y ruteo**: un solo concepto para "bus" y "FX" (`ProjectSettings.Buses`, `Bus`: nombre, fader, paneo, mute,
  slots, salida y envíos; una reverb o un delay son un slot más, `InsertSlot.Reverb` / `Delay`). Un proyecto nuevo vacío
  arranca sin buses (sin envíos); las plantillas traen los suyos o "Reverb" y "Delay" (`ProjectBuses.Defaults`). Pista (`Channel`), carpeta y bus son `IRouted`: `Output` (null = Master,
  o un bus; una pista en carpeta sale por la carpeta) y `Sends` (`Send`: bus, nivel 0..1, ON y PRE). Las ediciones
  pasan por `ProjectBuses`. **Por ahora un bus sale solo al Master** (decisión del usuario, 2026-10-26: las cadenas
  entre buses no se entendían): `CanFeed` rechaza un bus como origen y `Normalize` le borra salida y envíos, además de
  sacar lo que apunta a buses que no existen. El motor sigue sabiendo correr buses que alimentan buses (`Order`), por
  si vuelven. Cada pista (y carpeta) elige a qué buses manda (pedido del usuario): un envío con `Hidden` está sacado
  de su lista (apagado, con su nivel guardado) y un bus sin envío tampoco está: una pista o un bus nuevos no traen
  ninguno, el usuario los suma (pedido del usuario, 2026-10-29; `ProjectBuses.Shown`, `ShowSend`); el timeline, el mixer y el inspector muestran solo esos. La versión 3 trajo esto: los campos de la 2
  (Reverb/Delay fijos, retornos, `reverbSend`…, el fader/M/S/slots de carpeta) se leen como `Old*` y se convierten al
  normalizar.
- **Clip**: un pedazo de un media en una pista (`AtSec`, `TrimStartSec`, `TrimEndSec`, `GainDb`, `GainCurve`, encuadre). Al cambiar el tempo la ventana pregunta si se estiran los clips (`TempoDialog`, pedido del usuario); si sí, van todos (decisión del usuario, sin elegir cuáles, video incluido): `TempoFollow` multiplica por viejo/nuevo todos los tiempos del timeline (clips, automatización, marcadores, acordes, loop) y `Stretch` dice cuánto más largo suena su media; sus tiempos ya lo cuentan, lo que lee el media pasa por `MediaSec`. El sonido estirado es un render en `cache/` (`PitchShifter.Render`, `TransposeCache`) y mientras se hace el clip calla (decisión del usuario). "Detectar tempo" no estira: solo mueve la grilla (`SetGrid`). Un clip también se estira a mano (Ctrl+arrastrar un borde, `ClipEditMode.StretchStart/End`): escala sus tiempos con el mismo `Rescale`, así que un cambio de tempo después se multiplica encima. El deshacer guarda `ProjectSettings.TimeScale`, no el tempo. `TimeMap` detecta los ataques y hace que el render vaya a 1:1 alrededor de cada uno, para que no se corran: el estiramiento va entre ataques (pedido del usuario: loops de ritmo). `Pitch` son semitonos propios del clip, sumados al `Transpose` de la pista (`SemitonesOn`, pedido del usuario): van por el mismo render, sin cambiar el largo. Con `Reversed` suena al revés sin tocar el archivo: sus tiempos cuentan en el sentido en que se oye (`ClipEditing.Reverse`). **Bucle** (`ClipLoops`, pedido del usuario, como GarageBand): las vueltas de una toma son clips enteros con `LoopOf` = el original, así el motor no sabe de bucles; nunca se editan sueltas: `ClipLoops.Keep` (en `EditTimeline` y el inspector) las rehace cuando cambia el original. Un clip MIDI (`LoopBeats`) y un bloque de automatización (`LoopSec`) repiten su primera vuelta como un patrón.
- **ChordMarker** (`Project.ChordMarkers`): un acorde detectado (`Sec`, `EndSec`, `Name`), aparte de `Project.Markers`
  (decisión del usuario: no se pisan con los marcadores y se borran juntos). Las ediciones pasan por `ProjectMarkers`.
- **Sampler** (`Track.Sampler`, `SamplerSettings`, decisión del usuario, 2026-11-02: un sample por ahora): el instrumento nativo de una pista MIDI; nunca junto con `Track.Instrument`. Su sample es un `Media` más (`SamplerMedia`, cuenta como usado en `ProjectHistory.UsedMediaIds`) y lo demás son números en el proyecto, con su deshacer. En el motor (`Dsp/Sampler.cs`, `Dsp/SamplerSound.cs`): `SamplerPatch` y `SamplerSound` son inmutables y se reemplazan enteros, `Sampler` lleva las voces (preasignadas) y lo usan igual `LiveTrack` y `MixRenderer`. Cada voz depende solo del tiempo desde su nota y, por los carriles de sus perillas (`SamplerCurves`, `sampler:<perilla>`, `SamplerKnob`: se leen en los pasos de control de la voz, en el cuadro de la canción, no por `InsertAutomator`), del cuadro de la canción: cualquier tamaño de bloque da los mismos floats. El slide y el portamento son de la voz (`NoteEvent.Bend`, `NoteBend`), no el pitch bend del canal, y `NoteEvent.Pan` se suma al paneo del sampler. El sonido guarda niveles limitados en banda (`Levels`) para que subir el tono no genere aliasing. `SamplerRoot` saca la raíz del nombre o del sonido.
- **Granular** (`Track.Granular`, `GranularSettings`, decisión del usuario, 2026-11-03: un instrumento aparte, no un modo del sampler): otro instrumento nativo de una pista MIDI; nunca junto con `Track.Instrument` ni `Track.Sampler`. Su sample entra por `SamplerMedia.Add` (`GranularMedia`) y se lee con el mismo `SamplerSound` (niveles limitados en banda), sin loop. En el motor (`Dsp/Granular.cs`), cada voz dispara granos (ventana de `Granular.MakeWindow`, lectura Hermite de 4 puntos) alrededor de un cabezal que recorre el sonido (Estirar) o queda en la posición (Recorrer). El azar (Jitter, Spray, Ancho) sale de una semilla por nota (cuadro y tecla en que empezó): en vivo y al exportar, en cualquier bloque, da los mismos floats. Carriles `granular:<perilla>` (`GranularCurves`, `GranularKnob`), leídos en cada grano y paso de control. Lo que el sampler y el granular hacen con las notas (saltos, bucle, teclados, cola) es uno solo: `NoteInstrument` (`Dsp/NoteInstrument.cs`); `LiveTrack.Native` es el que tenga la pista.
- **Pista MIDI** (`Track.Kind = "midi"`, `TrackKinds`): en vez de clips tiene `MidiClips` (`MidiClip`: `AtSec` en
  segundos, `LengthBeats` y sus `MidiNote` en tiempos, así siguen al tempo); en una pista de audio `MidiClips` es null.
  Sin video; las tomas no entran en ella. Toca y graba del teclado MIDI: `MidiInput` (null = todas las entradas) y
  `MidiChannel` (0 = todos). Las ediciones pasan por `MidiEditing` (`Midi.cs`). `MidiEditing.Split` no cambia lo que suena: la nota que cruza el corte sigue en el pedazo derecho como `MidiNote.Tie`, que `NoteExpression.JoinTies` une a la que termina ahí con el mismo tono; sin esa nota es una nota común. Las herramientas del piano roll son datos de `MidiTools` (id, grupo, ajustes y qué hacen): una herramienta nueva va ahí, y el panel y `xtlab` la toman solos; `.mid` por `MidiFiles` (en tiempos, nunca en segundos), partitura por `ScoreSheet`. **Patrones** (`MidiPatterns`): clips
  enlazados por `PatternId`, cada uno con su copia de las notas (sin lista propia en el proyecto); una vuelta de
  `PatternBeats` se repite hasta el final del clip. Un patrón puede tener varios instrumentos (`PatternPart`, una parte con sus notas por pista, sin tocar el motor; nombre y vuelta son del patrón). Quien cambie `MidiClip.Notes` llama a `MidiPatterns.Sync`, y quien
  lea lo que suena usa `Heard`/`Slice` y `Passes`. La pestaña Patrones (`MainWindow.Patterns.cs`) los lista desde `All`.
- **Automatización** (`Automation.cs`, `Track.Automation`): cada `AutomationLane` es un parámetro de la pista (`Target`, `AutoTarget`: volumen, paneo, mute, envío, efecto nativo con su ON y las bandas del EQ, VST3 de efecto o de instrumento, perilla del sampler), hija de la pista en el timeline (decisión del usuario). Su curva vive en bloques (`AutomationBlock`, como los clips MIDI, en segundos: se mueven, recortan, copian y repiten; las copias son independientes), que se leen como una sola curva (`AutomationLane.Flat`, el motor no sabe de bloques; hay que llamar `Touch` después de editarlos): entre bloques mantiene el último valor. Los puntos guardan la posición del control (0..1), no el valor, y `Curve` dobla el tramo hasta el siguiente (-1..1, `AutoCurve.Shape`; 0 = recto, opcional por tramo: decisión del usuario). Nunca cambia el `Channel`: sin puntos o apagada, manda el canal. Un destino que ya no existe conserva su carril, en silencio, para que deshacer lo traiga. Las ediciones pasan por `Automations`.
- **Entradas MIDI** (teclados, controladores): son de la máquina, no del proyecto (`MidiSettings`, `config.json` "midi",
  por nombre de aparato; `MidiSetup` en la ventana). `MidiInputs` (winmm vía NAudio) filtra canal y velocidad en su
  hilo y deja las notas en un anillo sin locks; el hilo de audio las reparte al empezar cada bloque
  (`PlaybackEngine.DispatchMidi`) a las pistas que escuchan (una sola: la MIDI elegida o, sin ninguna, la primera armada; grabando, las armadas), cada una con su
  `LiveNotes` (pedal de sustain, voces del tono de prueba) que suena por su instrumento y su cadena; grabando, cada
  nota va a su `MidiTake` con el cuadro que se oía y al parar se arma el clip (`MidiEditing.AddRecorded`).

Convenciones: tiempos del timeline en **segundos**, correcciones de latencia/sincronía en **ms**. JSON en camelCase
(`ProjectStore.Json`). Cada clase lleva `[JsonExtensionData] Extra`, que conserva lo desconocido: no lo saques.
`Normalize()` rellena lo que falte o venga roto (todo campo nuevo tiene valor por defecto y se normaliza ahí). Cambio
incompatible de formato → subí `Project.CurrentVersion` y migrá al abrir. IDs con prefijo (`Ids.New("prj")`).

### Carpeta del proyecto y guardado

```
Nombre.xtp               el proyecto (sigue al nombre: `ProjectStore.FindFile`, `FilePath`)
Nombre.xtp.bak           el guardado anterior
xtreme_project.lock      mientras está abierto (una segunda ventana lo abre en solo lectura)
historial/               copias para volver atrás
media/                   grabaciones e importados
cache/                   lo que se rehace desde media/ (tomas transpuestas y estiradas, TransposeCache): borrarlo solo cuesta tiempo
exports/
```

- Todo guardado es **atómico**: temporal → forzado a disco → `File.Replace`. Nunca escribas el `.xtp` directo.
- Un proyecto puede vivir en cualquier carpeta: "Abrir…" toma un `.xtp`, "Guardar en…" ubica uno nuevo, y "Guardar como…" / "Guardar una copia…" (`ProjectLibrary.SaveAs`, decisión del usuario: las dos) lo copian a otra carpeta, armado en `.guardando-*` y publicado al final; una carpeta vacía pasa a ser la del proyecto, en otra se crea una con su nombre.
- Toda edición pasa por `MarkDirty()` → `SaveSoon()`: cuándo se escribe lo elige el usuario (`SaveMode`, en
  Configuración → Guardado y exportación): 1,5 s después de cada edición (por defecto), cada X minutos o a mano (serializa en la UI,
  escribe en segundo plano, en orden). Ctrl+S y el diskette fuerzan; las tomas, las exportaciones y el cambiar de proyecto
  (`FlushSave()`) guardan siempre, sea cual sea el modo; cerrar la ventana con cambios pendientes pregunta antes
  (`UnsavedDialog`) y no cierra si no pudo guardar.
- **Perder una toma es el peor error posible.** `RecordingWriter` escribe mientras graba y reescribe el encabezado
  cada ~2 s. Una toma MIDI va igual: `MidiTakeWriter` copia las notas a `media/<clip>.notas` desde su propio hilo, con su diario (`MidiTakeJournal`), y `RecoverMidiTakes` arma el clip al abrir. Todo lo que toque archivos del usuario piensa en qué pasa si el programa muere a la mitad.

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

Cadena por pista: clips (fades, ganancia; el audio ya transpuesto si la pista tiene Transponer) y, en una pista MIDI,
sus notas (`Sampler`, o `NoteSynth`: el tono de prueba, sin estado, desde `ChannelDsp.Notes`) → los **slots de
efectos** del canal en su orden (`Channel.Inserts`; un canal nuevo no trae ninguno y las plantillas de fábrica
saturación → compresor con su EQ de preset antes y después → EQ de pista, que se pueden sacar, repetir y reordenar) → paneo → fader → su salida (su carpeta, un bus o el
master); los envíos salen del fader (post, por defecto) o de antes (PRE: después de los slots y el paneo; mute y solo
igual los callan) hacia los buses. Carpetas y buses son nodos (`BusDspChain`: suma de lo que le llega → slots → paneo →
fader/mute → su salida y sus envíos) que corren en orden, cada uno después de todo lo que lo alimenta
(`ChannelDsp.Nodes`: primero las carpetas, después `ProjectBuses.Order`) → slots del master → Empuje o Destino → soft clip → limitador. El click va por fuera
del limitador, atrasado la misma latencia. `TrackDspChain` (con `TrackSend`), `InsertRack` (los slots, de pista, carpeta o
bus), `BusDspChain` y `MixGraphBus` (`XtremeDaw.Audio/Dsp/MixRenderer.cs`) los usan igual la reproducción y la
exportación: no los dupliques. La conversión proyecto → motor es una sola, `ChannelDsp` (`Track`, `Bus`, `Nodes`).
`xtlab comprobar-dsp` corre las comprobaciones del DSP.

Automatización en el motor: fader, paneo, mute y envíos se leen en el hilo de audio (`AutoCurve`, sin asignar) y glisan sample a sample hasta el valor del fin del bloque (`TrackDspChain.ProcessAutomated`, `AddSend`), a la hora del sonido que llega ahí (`Frame` menos la latencia de los slots). Los parámetros VST3 viajan con cada bloque por la memoria compartida (`VstParamCurves`, `VstBridge.QueueParam`, `MaxParams`), en vivo y en la exportación. Las perillas de efectos nativos no tienen forma de leerse en el hilo de audio: `InsertAutomator` trabaja sobre su copia del canal y le da los slots al motor cuando un carril se movió, desde el tick de la ventana (`LiveTrack.TickAutomation`) o cada `AutoStep` en la exportación (`MixTrackSpec.InsertsAt`). El decay, tono y ancho de la reverb arman un impulso nuevo: esos se leen cada 0,25 s y el impulso entra en un fundido (`ReverbBus.Convolve`); la exportación lo espera (`InsertSpec.Wait`).

Slots: cada efecto es un `ITrackInsert` (`XtremeDaw.Audio/Dsp/TrackInserts.cs`); un VST3 es un tipo de slot
más. `TrackDspChain.SetInserts` reusa el efecto de cada slot por su id (no pierde el estado al editar) y, si cambia la
lista, el hilo de audio funde 5 ms de salida y 5 ms de entrada. La conversión canal → motor es una sola,
`ChannelDsp.Specs` (en vivo, exportación y xtlab); las ediciones de la lista, `ChannelInserts`. Todo nativo va dentro de
un `FxShell` (Dry, Wet, salida, fundido al prender y apagar; con Wet 100 %, Dry 0 % y 0 dB pasa bit a bit), salvo el Limitador,
que tiene latencia propia, la declara y atrasa su Dry lo mismo. Un efecto nativo nuevo es una clase en `TrackInserts`/`MasterInserts` y su lista en
`FxDefs`, que da el panel en todos los lugares (`FxBody`), y entra en su grupo del menú (`FxGroups`); nunca una interfaz aparte.
La modulación (`ModInserts.cs`) sale sin lo directo: Dry y Wet arman el efecto contra la señal seca, por eso arranca al 50 % (`FxDefs.StartMix`).
Una banda dinámica del EQ (`EqDynamics`) suma `k × filtro` a la señal: nunca calcula coeficientes en el hilo de audio. Las etapas del master son slots
(`TrackInserts.IsStage`, el mismo DSP de siempre) y la cadena del master es `MasterSettings.Slots`: `MasterChain` queda
solo para lo que no pasó por `Normalize` (`ChainInSlots`). Soft clip, Limitador y Destino siguen fijos al final (Salida). La pestaña Canal y la
lista de la tira del mixer muestran la cadena entera (cada slot editado por su id); `Channel.Sat/Comp/Eq` son el
primer slot de cada tipo, el atajo de las perillas del timeline.

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
   `tools\probar.ps1 -NoAbrir "qué probar"`** (compila, corre `xtlab comprobar-dsp`, deja una copia en
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
   - `-Toma` antes de empezar, siempre; tu sesión lo puede volver a tomar sin problema. Código 2 dice qué sesión lo
     tiene: no lo trabajes. Lo que tome un subagente lo toma la sesión que lo lanzó (comparten la sesión).
   - `-Arregla` solo números que trabajaste vos en esta sesión y que están en un estado abierto. Si el usuario ya lo
     marcó **Anda**, `-Arregla` no lo reabre (código 2): si hiciste otro cambio, anotalo como ítem nuevo (`-NoAbrir "qué
     probar"`). No uses `-Arregla` para "recordarle" al usuario un ítem que sigue en Para probar.
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
   turno: otra sesión que quiera editarlo, o correr un comando que lo nombre, espera sola a que lo sueltes. Lo que
   cambie un comando o un script también queda tomado, y si pisó algo de otra sesión el hook avisa. Si te dice
   BLOQUEADO o ATENCIÓN: no lo esquives (ni con shell, ni Write, ni git, ni otro script), no deshagas nada, terminá el
   turno y avisale al usuario qué quedó sin hacer. `git restore`, `stash`, `clean`, `reset --hard` y `checkout --`
   están bloqueados: tiran cambios sin commitear que pueden ser de otro; si hacen falta, los corre el usuario.
   `tools\bloqueos.ps1 -Lista` muestra quién tiene qué; antes de un script que reescribe muchos archivos, miralo.
10. No subas WAV ni binarios. No hagas commit ni push si el usuario no lo pide.
11. Las skills de este proyecto (`.claude/skills`) llevan el prefijo `xt_`, en la carpeta y en su `name`
   (`xt_revisar-workbench`; decisión del usuario, 2026-11-03). El mod `xt-panel` (`.claude/plugins/xt-panel`, del
   catálogo local `xtreme`, activado en `.claude/settings.json`) abre un panel al costado del chat con un botón por
   acción de esas skills (no la barra de arriba del prompt: es una sola y la usa show-tokens). Una skill `xt_` nueva
   aparece sola, en una tarjeta con su `description` y, si tiene `argument-hint`, un campo; las que tienen botones
   propios están en `SECTIONS` y `COVERED`. El panel también pide skills nuevas (campo «Nueva skill»). Cambios al mod
   se recargan con `/reload-plugins`.
