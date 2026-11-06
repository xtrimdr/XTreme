# Motor de audio (XtremeDaw.Audio) — cómo está hecho

Se suma a la guía de la raíz ([AGENTS.md](../../AGENTS.md)), que tiene las reglas del hilo de audio. Vale también para
el motor que vive en la ventana (`XtremeDaw.App/Audio`: `PlaybackEngine`, `LiveTrack`). El modelo del proyecto que el
motor lee está en [XtremeDaw.Projects/AGENTS.md](../XtremeDaw.Projects/AGENTS.md).

## Cadena por pista

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

## Automatización en el motor

Fader, paneo, mute y envíos se leen en el hilo de audio (`AutoCurve`, sin asignar) y glisan sample a sample hasta el valor del fin del bloque (`TrackDspChain.ProcessAutomated`, `AddSend`), a la hora del sonido que llega ahí (`Frame` menos la latencia de los slots). Los parámetros VST3 viajan con cada bloque por la memoria compartida (`VstParamCurves`, `VstBridge.QueueParam`, `MaxParams`), en vivo y en la exportación. Las perillas de efectos nativos no tienen forma de leerse en el hilo de audio: `InsertAutomator` trabaja sobre su copia del canal y le da los slots al motor cuando un carril se movió, desde el tick de la ventana (`LiveTrack.TickAutomation`) o cada `AutoStep` en la exportación (`MixTrackSpec.InsertsAt`). El decay, tono y ancho de la reverb arman un impulso nuevo: esos se leen cada 0,25 s y el impulso entra en un fundido (`ReverbBus.Convolve`); la exportación lo espera (`InsertSpec.Wait`).

## Slots

Cada efecto es un `ITrackInsert` (`XtremeDaw.Audio/Dsp/TrackInserts.cs`); un VST3 es un tipo de slot
más. `TrackDspChain.SetInserts` reusa el efecto de cada slot por su id (no pierde el estado al editar) y, si cambia la
lista, el hilo de audio funde 5 ms de salida y 5 ms de entrada. La conversión canal → motor es una sola,
`ChannelDsp.Specs` (en vivo, exportación y xtlab); las ediciones de la lista, `ChannelInserts`. Todo nativo va dentro de
un `FxShell` (Dry, Wet, salida, fundido al prender y apagar; con Wet 100 %, Dry 0 % y 0 dB pasa bit a bit), salvo el Limitador,
que tiene latencia propia, la declara y atrasa su Dry lo mismo, y el Balance tonal (`TonalInsert`, `TrackInserts.IsMeter`), que solo
copia lo que pasa a un anillo (`TonalTap`) y nunca cambia el sonido (en el master corre después de Salida, `InsertRack.Analyze`: lee lo que sale); su análisis (`TonalAnalyzer`) corre en el hilo de la ventana. Un efecto nativo nuevo es una clase en `TrackInserts`/`MasterInserts` y su lista en
`FxDefs`, que da el panel en todos los lugares (`FxBody`), y entra en su grupo del menú (`FxGroups`); nunca una interfaz aparte.
La modulación (`ModInserts.cs`) sale sin lo directo: Dry y Wet arman el efecto contra la señal seca, por eso arranca al 50 % (`FxDefs.StartMix`).
Una banda dinámica del EQ (`EqDynamics`) suma `k × filtro` a la señal: nunca calcula coeficientes en el hilo de audio. Las etapas del master son slots
(`TrackInserts.IsStage`, el mismo DSP de siempre) y la cadena del master es `MasterSettings.Slots`: `MasterChain` queda
solo para lo que no pasó por `Normalize` (`ChainInSlots`). Soft clip, Limitador y Destino siguen fijos al final (Salida). La pestaña Canal y la
lista de la tira del mixer muestran la cadena entera (cada slot editado por su id); `Channel.Sat/Comp/Eq` son el
primer slot de cada tipo, el atajo de las perillas del timeline.

## Plugins VST3 y VST2, de 64 y 32 bits

`XtremeDaw.Audio/Vst`, decisión del usuario, 2026-11-02: un plugin de 32 bits solo carga en un proceso de 32 bits, así que el mismo fuente da también `xtvsthost32.exe` y `VstHost.Find(archivo)` elige por los bits del archivo (`PluginFile`, sin cargarlo); un `.dll` es VST2 (`vst2plugin.h`, el mismo protocolo que VST3), el resto VST3. Steinberg ya no licencia el SDK de VST2: `vst2.h` es un encabezado propio con la interfaz binaria, sin nada de ese SDK (decisión del usuario, sabiendo que es zona gris legal); no metas el `aeffect.h` oficial en el repo. Cada instancia corre en su proceso (decisión del usuario: un plugin
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
La lista de plugins (`PluginCatalog`, `plugins.json`; carpetas extra y ocultos en `config.json`) se administra en
Configuración → Plugins; en la ventana pasa siempre por `PluginScan` (una sola lista y una sola búsqueda a la vez).

## Compensación de latencia (PDC)

Lo que un slot atrasa (`ITrackInsert.LatencySamples`) lo espera el resto de la
mezcla. `MixLatency.Compute` (un solo cálculo para el vivo y la exportación) dice cuánto espera cada tramo (la salida
y cada envío de pistas y nodos, `LatencyDelay`) para que todo llegue junto a cada nodo y al master; el master queda
`PdcFrames` tarde y con eso se corren el click, las notas grabadas y las tomas (`PlaybackEngine.UpdateLatency`, en
cada tick, solo si algo cambió). La exportación recorta ese atraso del principio, como el del limitador.
Lo que se ve (imagen, cabezal, reloj) va con lo que se oye: `PlaybackEngine.HeardSec` (`HeardClock`: tras arrancar o saltar espera en el destino, en un bucle termina la vuelta); `PositionSec` sigue siendo dónde se leen las pistas, y es lo que usan la grabación, las ediciones y la automatización nativa. Al parar (`FadeOutAndPause`) la posición vuelve a lo último oído, para que seguir no saltee nada.
**Una toma tiene un solo corrimiento**: mientras graba, la demora no se mueve. `VstInsert.Steady` hace que cada slot arranque con el bloque extra ya dado (`LiveLag`): un bloque tarde entra en su lugar en vez de atrasar la mezcla, y `StartRecording` calcula la PDC antes del primer sample. Si un plugin se va, `MixLatency.Compute(minMaster)` mantiene la demora.

## Separar en stems

`StemSeparator`: Demucs por ONNX Runtime, en la placa de video (DirectML: NVIDIA, AMD, Intel) o
en la CPU. Los grafos llevan solo la red; la STFT y su inversa están en C#, iguales a las de Demucs (`xtlab separar
--sin-desplazar` contra PyTorch: -80 dB, en las dos). DirectML fusiona cada `Sigmoid` con la operación vecina y el audio
sale mal sin ningún error: por eso sus salidas van expuestas en el grafo (`make-demucs-onnx.py`), y por eso `Choose` no
confía en una placa hasta que cada modelo coincide con la CPU en un segmento del audio y le gana en tiempo. `lib\demucs` (`tools\get-demucs.ps1`) no va
en git ni en el instalador (`CopyToPublishDirectory="Never"`): la licencia de los pesos no está clara (ver PENDIENTES).

## Componentes opcionales

`XtremeDaw.Audio/Components.cs`: lo que la app puede no tener (FFmpeg, modelos de stems). Uno nuevo se suma a `Components.All` y a sus textos `component.<id>.*`; se instala siempre por `ComponentInstall.Run` (uno a la vez, con progreso y cancelación) y se muestra con `ComponentList`. Lo que depende de él queda deshabilitado con un aviso. Las descargas pasan por `FileDownload` (`.part`, reanudación, SHA-256). Los modelos de stems no se alojan (decisión del usuario): se bajan los `.th` de Meta y `StemConverter` arma el ONNX en `%APPDATA%\XTreme\demucs` desde `lib\demucs-graphs` (grafos sin pesos, `tools\make-demucs-templates.py`). Tienen sus propios términos: nunca digas que son MIT.

## Decodificar

`MediaDecoder`: NAudio lo que lee solo, ffmpeg.exe por pipe todo lo demás. Lo que vino de FFmpeg
entra al proyecto como WAV, para que un proyecto abra sin FFmpeg; abrir un proyecto no llama a FFmpeg.
