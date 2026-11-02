# Guía para agentes — XTreme

Instrucciones para cualquier agente de IA (Claude, Codex, Copilot, Cursor…) que siga con este proyecto. Acá van las
reglas que valen siempre. Las demás se cargan según dónde trabajes:

- [src/XtremeDaw.App/AGENTS.md](src/XtremeDaw.App/AGENTS.md): diseño de la ventana (tokens, tipografía, clases, atajos, mensajes).
- [src/XtremeDaw.App/Controls/AGENTS.md](src/XtremeDaw.App/Controls/AGENTS.md): controles dibujados a mano y medidores.
- [docs/interfaz.md](docs/interfaz.md): cómo es y dónde vive cada parte de la ventana. Leé solo el apartado de lo que vas a tocar.

Si algo de estas guías deja de ser cierto, actualizalo en el mismo cambio. Contexto general en [README.md](README.md), requisitos, compilación, atajos y `wtlab` en [DETALLES.md](DETALLES.md),
lo que falta en [PENDIENTES.md](PENDIENTES.md); [HECHO.md](HECHO.md) es historial y no hace falta leerlo de entrada.

## 1. Qué es

- **XTreme** (internamente "Xtreme DAW"; hasta 2026-11-01 "WildTrack" y antes "GuitarCam Studio Native") es un **DAW** para grabar, mezclar y exportar que
  también hace **videos de guitarra**: tomas con cámara y audio, y un video con un panel por toma. El exe es
  `XTreme.exe` y los ajustes van en `%APPDATA%\XTreme` (`AppFolder`, que la primera vez copia lo que había en
  `%APPDATA%\WildTrack` y `%APPDATA%\GuitarCam Studio`). Dentro de cada proyecto el archivo sigue llamándose `guitarcam.json` a propósito.
- **El foco manda.** Las funciones de DAW tienen que servir para el video y no complicar el camino de **"grabo,
  mezclo y exporto"**. Si algo nuevo agrega pasos o pantallas a ese camino, replantealo o preguntá.
- **Las decisiones son del usuario.** Lo que describen estas guías lo eligió el usuario: no lo cambies sin preguntar,
  aunque otro DAW lo haga distinto. La app nació como port de una web (`..\GuitarCam`) que **ya no es referencia**:
  las menciones a la web en el código son historia, no reglas.

## 2. Idioma

| Qué | Idioma |
|---|---|
| Textos de la interfaz, mensajes, tooltips | **Español rioplatense, con voseo** ("Abrí", "Elegí", "probalo"), vía `Loc` |
| Comandos, opciones y textos de `wtlab` | Español (`grabar --segundos 15 --vida 0.3`) |
| README, DETALLES, PENDIENTES, HECHO, guías | Español |
| Identificadores, comentarios y `<summary>` | Inglés |
| Mensajes de commit | Inglés, imperativo, una línea descriptiva ("Decode real video in the preview with FFmpeg") |
| Valores guardados en el JSON | Inglés (`"tube"`, `"tape"`, `"side-by-side"`…) |

- La app es multilenguaje (`Assets/Lang/<código>.json`, **es.json es la referencia**): texto nuevo → clave nueva en
  todos los idiomas. Detalles en la guía de la ventana.
- `Audio` y `Projects` no conocen `Loc`: sus mensajes pasan por `Texts.T(clave, español, args...)` (en `XtremeDaw.Audio`,
  prefijos `err.`, `store.`, `camera.`…), que `Loc.Load` conecta; sin esa conexión (wtlab) sale el español.
- Siguen en español a propósito las plantillas que el usuario ya guardó y los nombres que se crean ("Canal 1").
- Vocabulario de músico, no de programador: **canal**, **pista**, **click**, **Vida** (saturación; niveles Apenas /
  Sutil / Con vida / Caliente / Al palo), **Válvula / Cinta**, **Rasgueo / Arpegio / Punteo**, **Ver todo**.
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
  XtremeDaw.Lab        wtlab.exe: consola para probar audio, DSP y proyectos sin la ventana.
  XtremeDaw.VstHost    wtvsthost.exe (C++, SDK de VST3): abre un plugin en su propio proceso. Lo compila el build de Audio.
tools/                 probar.ps1 (compila, comprueba y abre una copia de prueba), make-icon.ps1 (ícono: un vinilo con la X en la etiqueta que dibuja tools/icon/Vinilo.cs píxel a píxel, cada tamaño a su resolución; no necesita nada instalado; hasta 96 px, una versión plana; redondo y con transparencia en todos los tamaños), make-shortcut.ps1 (acceso directo), make-track-icons.py (íconos de pista), get-vst3sdk.ps1 (baja el SDK de VST3), get-basic-pitch.ps1 (baja el modelo de Extraer MIDI), get-demucs.ps1 y make-demucs-onnx.py (arman los modelos de Separar en stems), get-ffmpeg.ps1 (baja los DLL de la preview), make-installer.ps1 (instalador)
.github/workflows/instalador.yml   arma el instalador en GitHub Actions (un tag vX.Y.Z → release en borrador; firma con SignPath)
```

- Las dependencias van **hacia abajo**: App → Projects → Audio. `Audio` y `Projects` no referencian Avalonia; todo lo
  que se pueda probar desde `wtlab` vive ahí.
- Cada función nueva del motor o del proyecto se puede ejercitar desde **`wtlab`** (comando u opción nueva, con su
  ayuda actualizada). `wtlab` usa cultura invariante (punto decimal) a propósito.
- `net10.0-windows`, x64, `Nullable`. Audio por **ASIO** (NAudio), video con **FFmpeg/ffprobe** por proceso y pipe (por nombre: en el PATH, o la carpeta de winget, que `FfmpegSetup.RefreshPath` suma al PATH del proceso al arrancar),
  salvo la preview: la decodifica FFmpeg dentro del proceso (`NativeVideoSource`, FFmpeg.AutoGen con los DLL de FFmpeg 9
  en `lib\ffmpeg`, fuera de git; ver DETALLES.md).
- Sin MVVM ni bindings: code-behind con controles con nombre. No metas un framework sin que el usuario lo pida.
- Sin dependencias nuevas de NuGet sin preguntar. Hoy: Avalonia 11.3, NAudio 2.2, FFmpeg.AutoGen 9.0 y ONNX Runtime
  con DirectML (`Microsoft.ML.OnnxRuntime.DirectML`, decisión del usuario: sus versiones van atrás de las del paquete común; para Extraer MIDI, con el modelo de Basic Pitch de Spotify, Apache 2.0, `lib\basic-pitch`,
  `tools\get-basic-pitch.ps1`: decisión del usuario). Fuera de NuGet, el
  SDK de VST3 de Steinberg (MIT, `lib\vst3sdk`, `tools\get-vst3sdk.ps1`) para `wtvsthost.exe` (decisión del usuario).
- **Separar en stems** (`StemSeparator`): Demucs por ONNX Runtime, en la placa de video (DirectML: NVIDIA, AMD, Intel) o
  en la CPU. Los grafos llevan solo la red; la STFT y su inversa están en C#, iguales a las de Demucs (`wtlab separar
  --sin-desplazar` contra PyTorch: -80 dB, en las dos). DirectML fusiona cada `Sigmoid` con la operación vecina y el audio
  sale mal sin ningún error: por eso sus salidas van expuestas en el grafo (`make-demucs-onnx.py`), y por eso `Choose` no
  confía en una placa hasta que cada modelo coincide con la CPU en un segmento del audio y le gana en tiempo. `lib\demucs` (`tools\get-demucs.ps1`) no va
  en git ni en el instalador (`CopyToPublishDirectory="Never"`): la licencia de los pesos no está clara (ver PENDIENTES).
- **Componentes opcionales** (`XtremeDaw.Audio/Components.cs`): lo que la app puede no tener (FFmpeg, modelos de stems). Uno nuevo se suma a `Components.All` y a sus textos `component.<id>.*`; se instala siempre por `ComponentInstall.Run` (uno a la vez, con progreso y cancelación) y se muestra con `ComponentList`. Lo que depende de él queda deshabilitado con un aviso. Las descargas pasan por `FileDownload` (`.part`, reanudación, SHA-256). Los modelos de stems no se alojan (decisión del usuario): se bajan los `.th` de Meta y `StemConverter` arma el ONNX en `%APPDATA%\XTreme\demucs` desde `lib\demucs-graphs` (grafos sin pesos, `tools\make-demucs-templates.py`). Tienen sus propios términos: nunca digas que son MIT.
- **Decodificar** (`MediaDecoder`): NAudio lo que lee solo, ffmpeg.exe por pipe todo lo demás. Lo que vino de FFmpeg
  entra al proyecto como WAV, para que un proyecto abra sin FFmpeg; abrir un proyecto no llama a FFmpeg.
- **Plugins VST3** (`XtremeDaw.Audio/Vst`): cada instancia corre en su `wtvsthost.exe` (decisión del usuario: un plugin
  que se cuelga no tira abajo la grabación). Hablan por memoria compartida (`shared.h` ↔ `VstBridge`, los offsets tienen
  que coincidir) y líneas de texto por stdin/stdout. En vivo el hilo de audio nunca espera al plugin: lo oye un bloque
  tarde y le manda las notas ese bloque (más su latencia) antes (`InstrumentPlayer`); la exportación espera cada bloque
  (`InstrumentRender`, dentro de `MixRenderer`). En vivo se le anuncia el bloque real del driver (`--block`,
  `VstInsert.LiveBlock`), no el máximo: los plugins dimensionan su trabajo por ese número y pagan por lo que no usan
  (Splice INSTRUMENT, el doble); un bloque mayor igual funciona, el host lo parte en pedazos. Un instrumento que recién abre puede seguir cargando sus samples y
perder las notas: antes de ponerlo en la pista y antes de exportar se prueba una nota hasta que suena (`InstrumentWarmup`). El host avisa dónde está la ventana del plugin (`editor-rect`, `editor-active`) para que el teclado virtual sepa dónde tocar, y la ventana se viste y se llena desde la app (`look`, `title`, `toolbar`, `keys-panel`: el panel del teclado es una ventana de la app hija de la del host). El host es per-monitor DPI aware: le da la escala de la pantalla al plugin (`setContentScaleFactor`), trabaja en píxeles reales y escala solo lo suyo (la barra y el panel, que la app le pide a 100 %); prender el panel no le avisa nada al plugin. Un **efecto** es un slot más (`VstInsert`, tipo `vst`): en vivo manda
  cada bloque y toma el anterior, así que se oye un bloque del driver (`VstInsert.LiveBlock`) más su latencia tarde; en
  la exportación abre su propia copia y espera cada bloque. La ventana de la app maneja procesos y estados
  (`MainWindow.Vst.cs` los instrumentos, `MainWindow.VstFx.cs` los efectos, que el motor encuentra por `LiveVst`).
- **Compensación de latencia (PDC)**: lo que un slot atrasa (`ITrackInsert.LatencySamples`) lo espera el resto de la
  mezcla. `MixLatency.Compute` (un solo cálculo para el vivo y la exportación) dice cuánto espera cada tramo (la salida
  y cada envío de pistas y nodos, `LatencyDelay`) para que todo llegue junto a cada nodo y al master; el master queda
  `PdcFrames` tarde y con eso se corren el click, las notas grabadas y las tomas (`PlaybackEngine.UpdateLatency`, en
  cada tick, solo si algo cambió). La exportación recorta ese atraso del principio, como el del limitador.
  La lista de plugins (`PluginCatalog`, `plugins.json`; carpetas extra y ocultos en `config.json`) se administra en
  Configuración → Plugins; en la ventana pasa siempre por `PluginScan` (una sola lista y una sola búsqueda a la vez).

### Modelo del proyecto (`XtremeDaw.Projects`)

- **Media**: grabaciones e importados. **Nunca se reescriben.**
- **Track**: el canal (volumen, paneo, mute/solo, efectos…), si lleva panel de video, `Color` ("#rrggbb") e `Icon`
  (null = sin color / con su número), `FolderId` = su carpeta.
- **Folder** (`Project.Folders`): agrupa pistas. Sus hijas van juntas en `Project.Tracks` y la carpeta va justo antes
  (`Folder.Index`). Toda edición de orden pasa por `TrackFolders` y `TrackFolders.Normalize` repara lo que quede raro.
  **Solo contiene** (decisión del usuario): sin fader, paneo, M/S ni efectos. Sus pistas suman ahí y van a su `Output`;
  desde esa suma tiene sus `Sends` (nunca PRE: no tiene fader). Un **separador** es una `Folder` con `Separator`: va en
  el mismo orden, nunca toma pistas ni rutea, y se puede ocultar del timeline (`HiddenInTimeline`). Pistas de la raíz,
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
- **Clip**: un pedazo de un media en una pista (`AtSec`, `TrimStartSec`, `TrimEndSec`, `GainDb`, `GainCurve`, encuadre). Todo clip sigue el tempo (decisión del usuario, sin elegir cuáles, video incluido): `TempoFollow` multiplica por viejo/nuevo todos los tiempos del timeline (clips, automatización, marcadores, acordes, loop) y `Stretch` dice cuánto más largo suena su media; sus tiempos ya lo cuentan, lo que lee el media pasa por `MediaSec`. El sonido estirado es un render en `cache/` (`PitchShifter.Render`, `TransposeCache`) y mientras se hace el clip calla (decisión del usuario). "Detectar tempo" no estira: solo mueve la grilla (`SetGrid`). El deshacer guarda `ProjectSettings.TimeScale`, no el tempo. Con `Reversed` suena al revés sin tocar el archivo: sus tiempos cuentan en el sentido en que se oye (`ClipEditing.Reverse`).
- **ChordMarker** (`Project.ChordMarkers`): un acorde detectado (`Sec`, `EndSec`, `Name`), aparte de `Project.Markers`
  (decisión del usuario: no se pisan con los marcadores y se borran juntos). Las ediciones pasan por `ProjectMarkers`.
- **Pista MIDI** (`Track.Kind = "midi"`, `TrackKinds`): en vez de clips tiene `MidiClips` (`MidiClip`: `AtSec` en
  segundos, `LengthBeats` y sus `MidiNote` en tiempos, así siguen al tempo); en una pista de audio `MidiClips` es null.
  Sin video; las tomas no entran en ella. Toca y graba del teclado MIDI: `MidiInput` (null = todas las entradas) y
  `MidiChannel` (0 = todos). Las ediciones pasan por `MidiEditing` (`Midi.cs`). **Patrones** (`MidiPatterns`): clips
  enlazados por `PatternId`, cada uno con su copia de las notas (sin lista propia en el proyecto); una vuelta de
  `PatternBeats` se repite hasta el final del clip. Quien cambie `MidiClip.Notes` llama a `MidiPatterns.Sync`, y quien
  lea lo que suena usa `Heard`/`Slice` y `Passes`. La pestaña Patrones (`MainWindow.Patterns.cs`) los lista desde `All`.
- **Automatización** (`Automation.cs`, `Track.Automation`): cada `AutomationLane` es un parámetro de la pista (`Target`, `AutoTarget`: volumen, paneo, mute, envío, efecto nativo con su ON y las bandas del EQ, VST3 de efecto o de instrumento), hija de la pista en el timeline (decisión del usuario). Su curva vive en bloques (`AutomationBlock`, como los clips MIDI, en segundos: se mueven, recortan, copian y repiten; las copias son independientes), que se leen como una sola curva (`AutomationLane.Flat`, el motor no sabe de bloques; hay que llamar `Touch` después de editarlos): entre bloques mantiene el último valor. Los puntos guardan la posición del control (0..1), no el valor, y `Curve` dobla el tramo hasta el siguiente (-1..1, `AutoCurve.Shape`; 0 = recto, opcional por tramo: decisión del usuario). Nunca cambia el `Channel`: sin puntos o apagada, manda el canal. Un destino que ya no existe conserva su carril, en silencio, para que deshacer lo traiga. Las ediciones pasan por `Automations`.
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
guitarcam.json      el proyecto
guitarcam.json.bak  el guardado anterior
guitarcam.lock      mientras está abierto (una segunda ventana lo abre en solo lectura)
historial/          copias para volver atrás
media/              grabaciones e importados
cache/              lo que se rehace desde media/ (tomas transpuestas y estiradas, TransposeCache): borrarlo solo cuesta tiempo
exports/
```

- Todo guardado es **atómico**: temporal → forzado a disco → `File.Replace`. Nunca escribas `guitarcam.json` directo.
- Toda edición pasa por `MarkDirty()` → `SaveSoon()`: cuándo se escribe lo elige el usuario (`SaveMode`, en
  Configuración → Guardado y exportación): 1,5 s después de cada edición (por defecto), cada X minutos o a mano (serializa en la UI,
  escribe en segundo plano, en orden). Ctrl+S y el diskette fuerzan; las tomas, las exportaciones y el cambiar de proyecto
  (`FlushSave()`) guardan siempre, sea cual sea el modo; cerrar la ventana con cambios pendientes pregunta antes
  (`UnsavedDialog`) y no cierra si no pudo guardar.
- **Perder una toma es el peor error posible.** `RecordingWriter` escribe mientras graba y reescribe el encabezado
  cada ~2 s. Todo lo que toque archivos del usuario piensa en qué pasa si el programa muere a la mitad.

## 4. Audio en tiempo real

**Nivel profesional**, como Logic Pro o Fender Studio Pro: nada de clics ni cortes. Arrancar, parar y hacer scrub
llevan su fundido (`PlaybackEngine.TransportFadeMs`, elegible en Configuración); los saltos, crossfade de 10 ms; el bucle no salta: gira en la muestra exacta y A entra entero, sin fundido (`LoopSplice`, pedido del usuario);
`Stop`/`Pause` cortan en el acto y son solo para grabar, exportar o cambiar de proyecto. El estado de los efectos no
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
sus notas (`NoteSynth`: el tono de prueba, sin estado, desde `ChannelDsp.Notes`) → los **slots de
efectos** del canal en su orden (`Channel.Inserts`; un canal nuevo no trae ninguno y las plantillas de fábrica
saturación → compresor con su EQ de preset antes y después → EQ de pista, que se pueden sacar, repetir y reordenar) → paneo → fader → su salida (su carpeta, un bus o el
master); los envíos salen del fader (post, por defecto) o de antes (PRE: después de los slots y el paneo; mute y solo
igual los callan) hacia los buses. Carpetas y buses son nodos (`BusDspChain`: suma de lo que le llega → slots → paneo →
fader/mute → su salida y sus envíos) que corren en orden, cada uno después de todo lo que lo alimenta
(`ChannelDsp.Nodes`: primero las carpetas, después `ProjectBuses.Order`) → slots del master → Empuje o Destino → soft clip → limitador. El click va por fuera
del limitador, atrasado la misma latencia. `TrackDspChain` (con `TrackSend`), `InsertRack` (los slots, de pista o
bus), `BusDspChain` y `MixGraphBus` (`XtremeDaw.Audio/Dsp/MixRenderer.cs`) los usan igual la reproducción y la
exportación: no los dupliques. La conversión proyecto → motor es una sola, `ChannelDsp` (`Track`, `Bus`, `Nodes`).
`wtlab comprobar-dsp` corre las comprobaciones del DSP.

Automatización en el motor: fader, paneo, mute y envíos se leen en el hilo de audio (`AutoCurve`, sin asignar) y glisan sample a sample hasta el valor del fin del bloque (`TrackDspChain.ProcessAutomated`, `AddSend`), a la hora del sonido que llega ahí (`Frame` menos la latencia de los slots). Los parámetros VST3 viajan con cada bloque por la memoria compartida (`VstParamCurves`, `VstBridge.QueueParam`, `MaxParams`), en vivo y en la exportación. Las perillas de efectos nativos no tienen forma de leerse en el hilo de audio: `InsertAutomator` trabaja sobre su copia del canal y le da los slots al motor cuando un carril se movió, desde el tick de la ventana (`LiveTrack.TickAutomation`) o cada `AutoStep` en la exportación (`MixTrackSpec.InsertsAt`). El decay, tono y ancho de la reverb arman un impulso nuevo: esos se leen cada 0,25 s y el impulso entra en un fundido (`ReverbBus.Convolve`); la exportación lo espera (`InsertSpec.Wait`).

Slots: cada efecto es un `ITrackInsert` (`XtremeDaw.Audio/Dsp/TrackInserts.cs`); un VST3 es un tipo de slot
más. `TrackDspChain.SetInserts` reusa el efecto de cada slot por su id (no pierde el estado al editar) y, si cambia la
lista, el hilo de audio funde 5 ms de salida y 5 ms de entrada. La conversión canal → motor es una sola,
`ChannelDsp.Specs` (en vivo, exportación y wtlab); las ediciones de la lista, `ChannelInserts`. Todo nativo va dentro de
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
   lo que se prueba es lo que está acá. Probá el motor y el proyecto con `wtlab`. Compilar, comprobar y copiar van de a
   una sesión por vez: `dotnet build` (`tools\turno.targets`), `probar.ps1` y los comandos pesados de `wtlab`
   (`HeavyTurn`) esperan su turno solos y dicen a quién esperan, así que correlos con un timeout de 10 minutos, sin
   reintentar ni matar nada mientras esperan (código 4 = el turno no llegó). **Al terminar cada cambio corré
   `tools\probar.ps1 -NoAbrir "qué probar"`** (compila, corre `wtlab comprobar-dsp`, deja una copia en
   `pruebas\`, fuera de AppData a propósito: desde la app de Claude, que es MSIX, lo escrito en AppData queda dentro
   del paquete y el acceso directo no lo ve, y anota el texto, corto y concreto, en la lista "Para probar",
   `pruebas\lista.json`, que el usuario marca desde la copia; varias sesiones pueden anotar a la vez) **y preguntale al usuario**
   (con un cuadro de pregunta, si tu herramienta lo tiene) **si lo abre ahora o lo deja en la lista**: la prueba ya quedó encolada, y con
   varios agentes trabajando él prueba todo junto después (pedido del usuario). Si elige abrir,
   `tools\probar.ps1 -Abrir` abre la última copia con la lista a la vista (`-Proyecto carpeta` la abre en ese
   proyecto); si elige dejarlo, no abras nada ni vuelvas a preguntar por ese cambio. `tools\probar.ps1 -Lista` muestra lo pendiente y lo que el usuario
   marcó como que no anda, con su nota: miralo antes de seguir con algo ya entregado. Si arreglaste un ítem, corré
   `tools\probar.ps1 -NoAbrir -Arregla 12` (con su número): vuelve a por probar en lugar de duplicarse. Si el script dice
   "NO SE PUEDE PROBAR", contale por qué y no abras nada. Nunca abras el exe de `bin\` ni lo mandes a probar desde
   ahí: bloquea los DLL y el próximo build falla. Si hay un XTreme abierto, el script le pregunta al usuario en un
   cuadro si lo cierra (guarda antes); nunca lo cierres ni lo mates por otro lado. Si eligió cancelar (código 2), no
   insistas; si nadie contestó en 100 s (código 3), avisale y que te diga cuándo. La copia muestra en la barra de título "PRUEBA" + el commit (+ cambios), así el usuario sabe qué
   está probando.
3. Para probar de verdad hacen falta una interfaz ASIO y un proyecto con tomas: si no los tenés, decí qué no probaste.
4. En capturas de GDI los píxeles negros tienen alfa 0 y algunos visores los pintan blancos: mirá el RGB.
5. Al terminar un ítem de PENDIENTES, sacalo de ahí y agregalo arriba de todo en HECHO con la fecha (AAAA-MM-DD) y una
   línea de qué se hizo y dónde. Ideas nuevas van en PENDIENTES, en su sección.
6. Si cambiás atajos, comandos de `wtlab` o la estructura, actualizá DETALLES.md y DETALLES_EN.md.
7. Si cambiás cómo se usa algo o el nombre de un control, actualizá su tema de la ayuda en todos los idiomas
   (`src/XtremeDaw.App/Assets/Help`, ver "Ayuda" en docs/interfaz.md); una función nueva que alguien vendría a buscar
   lleva su tema.
8. No subas WAV ni binarios. No hagas commit ni push si el usuario no lo pide.
