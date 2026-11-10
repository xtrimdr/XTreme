# Hecho — WildTrack

Lo que ya se terminó de [PENDIENTES.md](PENDIENTES.md), el más nuevo arriba. Cada ítem: fecha (AAAA-MM-DD),
qué se hizo y dónde. Es historial: no hace falta leerlo para trabajar, salvo para buscar cómo se resolvió algo.
Hasta 2026-10-29 la app se llamó GuitarCam Studio Native: los ítems de antes usan los nombres de entonces
(`GuitarCam.*`, `gclab`, `gcvsthost`).

- 2026-11-10: **Revisión de punta a punta, segunda tanda** (pedido del usuario: corregir lo que quedaba). "Guardar" de un proyecto sin título trae lo que entró a la carpeta vieja mientras copiaba (una toma, un importado, un congelado) y espera a que termine una toma antes de borrarla (`ProjectLibrary.BringMissing`, `SaveUntitled`). Dos aperturas de proyecto a la vez van de a una (`OpenProject`, `opening`). Una edición que falla a la mitad vuelve atrás entera, sin paso en el deshacer (`UndoManager.Revert`, `EditTimeline`). Pegar no entra en una pista bloqueada o congelada (`ClipClipboard`). Un tempo que no es un número se rechaza y fuera de 20–400 se ajusta (`TempoFollow.Valid`). Diarios de toma, copias del historial y `versiones.json` se fuerzan a disco antes de tomar su nombre (`ProjectStore.WriteFlushed`); Duplicar un proyecto no deja su carpeta a medio hacer. Motor: un fallo del callback de audio se avisa una vez (`PlaybackEngine.CallbackError`, `engine.callbackFail`); el click no se lee mientras la UI lo cambia; reiniciar una reverb ya no borra megabytes en el callback (`FftStage.Reset`); un plugin colgado corta la exportación a los 120 s (`VstBridge.Render`, `err.vst.hung`) y el host recibe su "quit". Sesiones en vivo: los archivos compartidos llevan el id de su media en el nombre, así dos "loop.wav" distintos no se confunden (`LiveState.Shared`; fila nueva en `colaborar comprobar`). Interfaz: el loop del transporte se llama Loop también en el menú (un clip repetido sigue siendo "en bucle") y la canción es canción, no tema, tooltips sin punto final, Master con mayúscula, tamaños de letra a la escala (12,5 en filas, 19 en el código de sala), tokens `Composition`, `Composition80` y `Rec40` en vez de colores escritos, el desplegable de componentes es un `SelectButton`, Enter cierra Novedades.
- 2026-11-10: **Revisión de punta a punta** (pedido del usuario): errores encontrados leyendo todo el código y corregidos. Grabación: la toma se escribe antes de la mezcla del bloque, así un fallo de una pista o un plugin no le saca audio (`PlaybackEngine.Process`); grabar armado sobre un clip en bucle ya no falla ni deja la toma afuera (`TakeRecording.AddTake`; igual `DeleteRange` y "Quitar del timeline"). Motor: un NaN o infinito de un plugin no pasa del puente (`VstBridge`), el chorus no lee adelante de la escritura con mucha profundidad (`ChorusInsert`), una pista recién creada sirve de sidechain sin reabrir (`LiveKeys.Update`), un driver ASIO que no abre se suelta (`AsioDuplex`). Proyecto: los puntos de automatización y el largo de su vuelta siguen bien un cambio de tempo (`TempoFollow`), quitar una carpeta con una pista bloqueada no saca las demás, duplicar sin contenido no copia el congelado. Sesiones en vivo: nadie puede cambiar dónde están los archivos de otro (`LiveState.Apply`), ni mandar ediciones sin ser aceptado, ni dejar un diario de grabación en `media/`; un mensaje roto termina la sesión, no la app. Ventana: Espacio no arranca la reproducción detrás de Novedades ni de Separar en stems; stems y congelar esperan si el gestor o una edición están en curso en vez de perder el resultado (`WaitEditable`), y congelar no se aplica si la pista cambió mientras se renderizaba; copiar al portapapeles ocupado no cierra la app; el valor compacto del inspector suelta el arrastre al perder el mouse; la preview no asigna por cuadro; el fondo del gestor sigue el tema. `xtlab`: `verificar-clips` se colgaba y `verificar-tomas` fallaba (pruebas viejas), con casos nuevos del bucle; el build ya no da el warning de `turno.targets`.
- 2026-11-09: **Fuera el Banco de pruebas viejo** (#37, pedido del usuario: lo reemplaza ClaudeWorkBench): se sacaron XT-WorkBench (`src/XtremeDaw.Bench`, `TestListWindow`, `Bench.cs`, `Agent*.cs`, `LockLight.cs`, `Terminal/`, `Lang/Bench`), el botón del Banco y el aviso de Probar en las copias de prueba, `TestList`/`AgentSessions` (Projects), `xtlab lista`, la parte de tickets de `probar.ps1` (queda compilar, comprobar, copiar y abrir; `-Probar` lo usa el botón Probar de ClaudeWorkBench), `bloqueos.ps1` y sus hooks, `cerrar-sesion.ps1`, el acceso directo "XT-WorkBench", las skills `xt_revisar-workbench` y `xt_lista-workbench` y el mod `xt-panel`. El código quedó en `archivo\banco-viejo.zip` y los datos (lista, historial, adjuntos, versiones de xtbench) en `pruebas\archivo-banco\`, fuera de git.
- 2026-11-09: **Relay de las sesiones por internet, más firme** (#1): protocolo 2 (`?v=`; otra versión → 426, "actualizá XTreme"). Si el socket del anfitrión se corta, la sala lo espera 30 s y vuelve con un token (`RelayHost.Reconnect`); cada conexión numera sus mensajes y reenvía lo no confirmado (`RelayStream`), así LiveLink no ve el corte. Límites: 3 salas abiertas y 30 por hora por dirección, 200 en total, 12 h y 20 GB por sesión (códigos 4002/4003, con su aviso). Cada sala borra su almacenamiento al terminar y guarda el tráfico con una alarma antes de dormirse. `/admin` muestra región, "anfitrión reconectando" e historial por día y del mes (en el `Registry`, 90 días). La ventana dice cuánto falta bajar (`LiveSession.DownloadLeft`). `xtlab colaborar comprobar --internet` prueba el corte y los límites.
- 2026-11-08: **Administrar las sesiones por internet** (#321, pedido del usuario): página privada `/admin` del relay (`relay/src/admin.js`, clave `ADMIN_KEY`, en `relay/.admin-key`) con las salas abiertas (personas, desde cuándo, tráfico), Cerrar una (código 4001: la app dice que la cerró el servidor) y Apagar las nuevas (503: la app dice que están desactivadas). Un Durable Object `Registry` lleva las salas abiertas y el interruptor. `xtlab colaborar comprobar --internet` con `XTREME_RELAY_ADMIN`.
- 2026-11-08: **Sesión en vivo por internet** (#320, pedido del usuario: un servidor en Cloudflare y un código corto como el de una reunión): Crear sesión pregunta **Internet** o **Red local** (`LiveDialog`, `MainWindow.Live.cs`). Por internet el código es "abc-defg-hij" (`LiveInvite.NewOnline`): de él salen, con PBKDF2, la clave y el nombre de la sala, así el servidor no conoce ninguno. El relay (`relay/`, Worker + Durable Object por sala) solo pasa bytes: el anfitrión tiene un WebSocket por el que van todos los invitados (`RelayHost`) y `LiveLink` corre igual sobre ese `Stream` que sobre TCP, cifrado de punta a punta. `xtlab colaborar comprobar --internet`.
- 2026-11-08: **Unir clips MIDI y Shift+clic para marcar un tramo** (#311, decisiones del usuario): Ctrl+J o clic derecho → Unir clips deja un clip por pista con los MIDI marcados, del primer inicio al último final, sonando igual (bucles y patrones escritos como notas, huecos en silencio, notas atadas por un corte vuelven a ser una; `MidiEditing.Join`, `MainWindow.MidiSplit.cs`); Shift+clic sin mover en un clip marca desde el elegido hasta ese (`TimelineView.BoxRange`). Filas "Unir:" en `midi comprobar`.
- 2026-11-08: **Sesión en vivo: quién hace qué y plugins que faltan** (#291, decisiones del usuario): cada uno con su color (`Palette.PersonColor`); su nombre sobre lo que elige, edita, graba o toca, en el encabezado, el clip y la tira del mixer, y su cabezal en el timeline (`LivePresence`, `MainWindow.Presence.cs`, `TimelineView.Presence.cs`). Historial con el autor de cada paso (`UndoManager.Remote`), Biblioteca con quién trajo cada toma (`Media.AddedBy`). Plugins: solo se usan los que tienen todos (decisión del usuario, en lugar del render que se probó primero): cada copia dice qué tiene, uno que a alguien le falta no se puede elegir y uno del proyecto queda apagado para todos hasta que todos lo tengan (`LivePlugins`, `LiveSession.LackingIt`, `MainWindow.LivePlugins.cs`). Aviso y sonido al entrar o salir (`CueSounds`, `PlaybackEngine.PlayCue`), latencia por ping y lista de personas con rol y estado (`LiveDialog`). `xtlab colaborar comprobar`.
- 2026-11-07: **Sesión en vivo, primera etapa** (#291, decisiones del usuario: P2P sin servidor, cada uno escucha por su cuenta, todos editan todo, código o link, misma versión): Archivo → Sesión en vivo (`LiveDialog`, `MainWindow.Live.cs`). El anfitrión ordena los cambios (`LiveSession`), que viajan como diferencias del estado del deshacer más los media (`LiveDiff`, `LiveState`; por id en listas, así dos pistas distintas no se pisan), cifrados con la clave del código (`LiveLink`, AES-GCM + HKDF; `LiveInvite`). El invitado recibe una copia nueva con su audio; una toma nueva viaja al terminar, por el anfitrión. El deshacer de cada uno saca solo sus pasos (`UndoManager.Rebase`). `xtlab colaborar comprobar`.
- 2026-11-07: **Envíos entre buses** (#264): un bus manda a otros buses como una pista (sección Envíos en su tira del mixer y del inspector, `RouterOf(bus)`); `ProjectBuses.CanFeed` rechaza el envío a sí mismo o uno que cierre un bucle, la salida de un bus sigue yendo solo al Master (`CanOutput`) y `Normalize` saca de un archivo el envío que cierre un bucle. `xtlab comprobar-dsp`: "Buses: modelo, envíos entre buses, v2".
- 2026-11-06: **Congelar pistas** (#240, como el Track Freeze de Studio Pro): "Congelar" en el menú de la pista (timeline y mixer) renderiza instrumento y slots con su automatización por la cadena de la exportación (`Freezing.Render`, la pista sola, sin fader, paneo ni envíos) a `freeze/`; en vivo la pista toca el render sin slots y cierra sus plugins, con fader, paneo, mute, solo y envíos activos (`LiveTrack.SetFreeze`, `MainWindow.Freeze.cs`). Congelada no se editan clips, notas, slots, instrumento ni Transponer (`Track.EditLocked`); copo en el encabezado y en la tira, clips apagados bajo la onda del render. La exportación usa el render (`ChannelDsp.Track(frozen:)`). `xtlab comprobar-dsp`: congelada suena como en vivo.
- 2026-11-06: **Asociar los .xtp y abrirlos en la ventana abierta** (#254, pedido del usuario): Configuración → Asociación de archivos (pestaña propia) dice qué abre un doble clic y "Abrir con este XTreme" registra el exe que corre para el usuario (HKCU, mismas claves que el instalador; `FileAssociation`). Con XTreme abierto, el .xtp va a esa ventana por un pipe (por usuario y por exe) y se abre en lugar del actual, que se guarda (`ShellOpen`, `MainWindow.ShellOpen.cs`).
- 2026-11-06: **Pista de tempo y compás** (#214, pedido del usuario, como Studio Pro): `TempoMap` (puntos en tiempos con escalón o rampa lineal, compases n/d al empezar un compás) en `ProjectSettings.TempoMap`; `TempoFollow.SetMap` deja lo musical (MIDI, pistas con `Track.FollowTempo`, sus carriles, marcadores, acordes, loop) en su tiempo y el resto en segundos, y se deshace. El click sigue el mapa y acentúa cada compás (`PlaybackEngine.Tempo`, `SeedClick`/`NextClick`); la regla, la grilla, el imán, el reloj, el piano roll y los .mid también. Franja plegable bajo la regla (`TimelineView.Tempo.cs`, `MainWindow.Tempo.cs`), Seguir tempo en el menú de la pista de audio. `xtlab proyecto verificar-tempo`, `tempo-mapa`, `sigue-tempo`.
- 2026-11-06: **Bancos General MIDI** (#212, pedido del usuario): componente "gm" (`GmBanksComponent`) que baja FluidR3 GM y MuseScore General (MIT, tamaño y SHA-256 fijos), `GmSettings.Bank` por pista (o un .sf2 propio, "Cargar .sf2…"), selector Banco en `GmWindow`, `GmBank` con varios bancos y vuelta al de la app si falta; moduladores de velocidad y tecla del SF2 (`SfMod`, en `GmSynth.Start`), sin ellos los pianos de MuseScore sonaban cerrados. `xtlab gm --banco`, `gm zonas` y filas en comprobar-dsp.
- 2026-11-06: **Balance tonal** (#210, pedido del usuario): slot de análisis `tonal` (`TonalInsert`, sin `FxShell`, pasa bit a bit) con `TonalAnalyzer` en el hilo de la ventana (1/6 de octava suavizado a 1/3, nivelado en 200 Hz–5 kHz, Estéreo/Mid/Side, punch bajo 120 Hz), seis objetivos de fábrica propios (`TonalTargets`) y objetivo desde referencias guardado en el slot (`TonalReference`, `InsertSlot.Tonal`); `TonalView` (cuatro bandas y punch; espectro en full), grupo Análisis en "+ Efecto", `xtlab balance` y sus filas en comprobar-dsp.
- 2026-11-05: **Compacto y full en todos los efectos nativos** (#186, pedido del usuario): cada uno tiene su `FxLayout` en `FxDefs.Layout` (saturación, compuerta, reverb, delay, EQ, los del master, modulación y los cinco modelos de compresor, con gráfico y sidechain como el Compresor); el EQ en full lleva el gráfico grande (`EqEditor(big)`). El Bombeo sigue con su panel.

- 2026-11-04: **Más estilos en Configuración → Apariencia** (#68, pedido del usuario): faders (`FaderLooks`, ocho looks, Consola por defecto), medidores (`MeterLooks`: Continuo, LED, Degradé, Fino), ondas del timeline (`WaveLooks`: Rellena, Contorno, Rectificada, Barras), fuente de la interfaz y tamaño general (`UiFonts`: el recurso `Ui` dinámico, `Palette.Ui` settable, y una escala con `LayoutTransformControl` en la plantilla de cada ventana y popup). Todo en vivo, guardado en `config.json`.
- 2026-11-04: **Pedidos en el Banco de pruebas** (pedido del usuario: de ahí toman el trabajo los agentes): pestaña Pedidos con un cuadro para cargar cambios numerados, con tipo Error / Mejora / Función nueva / Idea (`TestItem.Request`, `Kind`, `Taken`; `TestList.AddRequest`, `Take`, `EditRequest`), `xtlab lista pedir | tomar | soltar`, `probar.ps1 -Toma / -Suelta / -Pide`, y la regla en AGENTS.md (sección 6) y la skill `xt_revisar-pruebas`.
- 2026-11-04: **Colas de los efectos de pista al parar** (pedido del usuario: se cortaban en seco, en los buses no): el fundido del transporte (`TransportRamp`) iba sobre la suma del master y la entrada de los buses, después de la cadena de cada pista; ahora va sobre lo que toca cada pista (`LiveTrack.MixInto`, `fade`), y parada su cadena sigue con silencio hasta 3 s callada o 15 s (`LiveTrack.Ring`, `Ringing`). Play solo reinicia las cadenas que ya no suenan (`pendingQuietReset`).
- 2026-11-04: **Reverb sin picos y carga por pista** (pedido del usuario: el DSP marcaba 25 % con dos samplers): el tramo largo de la convolución (`ReverbBus.FftStage`) hacía todos los productos de las particiones cada 1024 cuadros y se comía un callback entero; ahora suma las viejas de a 16 pedazos mientras entra el bloque (en lugares fijos: cualquier bloque da los mismos floats) y al final solo la nueva y las FFT. Cada etapa usa una FFT real (la mitad de bins y de productos; DC y Nyquist juntos en el bin 0): con decay de 5 s, de 4,2 a 2,1 % de un bloque de 192. `XtremeDaw.Audio` se compila optimizado también en Debug, para que la copia de prueba mida como el instalador. El tooltip del DSP dice qué pista pesa más y cuánto su instrumento (`LiveTrack.TakeLoad`).
- 2026-11-03: **Teclas de los plugins a elección** (pedido del usuario: el paso de los atajos fallaba a veces): la barra de la ventana de un plugin (instrumento o efecto) tiene **Teclas al plugin** y **Teclas a XTreme** (`pluginKeyRoute`, `pluginKeys` en `config.json`; `key-route` y `app-key` en `xtvsthost`): todo al plugin, o todo a `OnKey` salvo modificadores y Alt+Tab/Alt+F4/Windows. El hook (`ForeignKeys`) ahora también toma las teclas de una ventana del plugin en otro hilo de su proceso.
- 2026-11-03: **Banco de pruebas** (pedido del usuario): la lista "Para probar" pasa a llamarse así (`test.list.title`), y los agentes ya no preguntan si abrir la copia: cada cambio queda en la lista (AGENTS.md, CLAUDE.md, `xt_revisar-pruebas`).
- 2026-11-03: **Tags de la Biblioteca** (pedido del usuario: costaba leerla de un vistazo): cada tag va en su cajita, chico, debajo del nombre y junto a la hora, el largo y el peso; se prenden en acento mientras la toma está en el timeline (Sin conexión nunca) (`TakeRow` en `MainWindow.History.cs`). Se borró `TrimRow`, que quedó sin uso.
- 2026-11-03: **Proyectos sin carpeta de proyectos** (pedido del usuario): el pie del gestor ya no elige dónde guardar; la lista muestra todos los proyectos creados o abiertos, estén donde estén (`RecentProjects` recuerda hasta 500), con su ruta debajo de cada uno (`ProjectsDialog.PathLine`).
- 2026-11-03: **Bombeo como KickStart** (pedido del usuario): panel nuevo (`MainWindow.PumpPanel.cs`): Tempo / Sidechain y la división arriba, Profundidad grande al lado de la curva sobre el sonido (`PumpView`, `PumpInsert.WaveIn/WaveOut`), formas de un clic (`PumpShapeIcon`). Con sidechain la otra pista le da forma (su envolvente sobre el umbral dice cuánto baja, con la Curva; pedido del usuario: que siga lo que entra) y las perillas son de compresor: Umbral (`PumpParams.ThresholdDb`, Auto = 24 dB debajo del pico), Ataque, Hold (`HoldMs`) y Release. `xtlab comprobar-dsp`, filas "Sidechain: bombeo, umbral", "sigue el nivel" y "hold".
- 2026-11-03: **Estilos de perilla** (pedido del usuario): diez looks para todas las perillas (Neumórfico, por defecto, Consola, Línea, Aluminio, Cristal, Anillo, Puntos, Bakelita, Halo, Cerámica) en `Controls/KnobLooks.cs`, elegidos en Configuración → Apariencia (antes Vista; "knobLook" en `config.json`) con una muestra de cada uno.
- 2026-11-03: **Plugins por marca, categoría o lista** (pedido del usuario): Configuración → Plugins elige cómo agrupan los menús de instrumentos y efectos (`PluginCatalog.Grouping`, `AddPluginItems` en `MainWindow.Vst.cs`), la lista de la página se agrupa igual y cada plugin tiene un chip de categoría (automática por su sub-categoría de VST3 o su nombre, `AutoCategory`, o propia, "vst.categories"). `xtlab vst agrupar`, `vst categoria`.
- 2026-11-03: **Vuelve el engranaje** (pedido del usuario): Configuración desde la barra de título (`SettingsButton`), además de Archivo.
- 2026-11-03: **Pista MIDI con instrumento** (pedido del usuario): submenú de "+ Nueva pista" con los instrumentos nativos y VST (`NewInstrumentTrackMenu` en `MainWindow.Vst.cs`, `NewInstrumentTrack` en `MainWindow.Midi.cs`); la pista nace con el instrumento y su nombre (`MidiEditing.AddTrack(p, nombre)`), en un paso de deshacer.
- 2026-11-03: **Granular, fase 1** (pedido del usuario: un sampler granular como instrumento aparte): `Track.Granular` (`Projects/Granular.cs`), motor `Dsp/Granular.cs` (granos con Densidad o Sync, Tamaño, Forma, Sesgo, Jitter, Spray, Ancho; cabezal Estirar a una velocidad, con Sync al tempo, o Recorrer en una posición; voces con envolvente), ventana `Dialogs/GranularWindow.cs` y `MainWindow.Granular.cs`, en NATIVOS de Instrumento y "Abrir en el granular" de la Biblioteca. Lo que el sampler hacía con las notas pasó a `Dsp/NoteInstrument.cs`, compartido. Carriles `granular:<perilla>`. `xtlab granular`, `xtlab midi granular`; `xtlab comprobar-dsp`, filas "Granular", "Estirar", "Recorrer".
- 2026-11-03: **Mastering rediseñado** (pedido del usuario: muy ancho, recargado, la medición repartida): rack con editor (`MainWindow.Master.cs`). Arriba Master con un solo ON para toda la cadena, Salida incluida (`MasterSettings.ToParams` apaga también el limitador; un proyecto nuevo arranca con el master prendido, `ProjectLibrary.Create`) y el Destino; debajo la cadena y Salida como fichas (manija, ON, rayita de reducción) y el panel de la elegida en un `PagedPanel` del alto del más alto; a la derecha el medidor con cuánto falta para el Destino y los LUFS de 30 s (`Controls/LoudnessHistory.cs`). Se sacaron Looks y Sugerime de la app (`MainWindow.Suggest.cs`); `MasterLooks`/`MasterSuggest` quedan para `xtlab`.
- 2026-11-03: **Formas de automatización** (pedido del usuario, como FL Studio): cada tramo de un carril tiene forma y tensión (`AutoShape`, `AutoShapes` en `Dsp/AutoCurve.cs`; `AutomationPoint.Shape`/`Tension`, guardados como `"curve"`, `"double"`, `"hold"`, `"stairs"`, `"smooth-stairs"`, `"pulse"`, `"wave"`). La curva pasó de potencia a exponencial (más pareja, y un pedazo de ella es la misma curva: dividir y recortar quedan exactos); el `curve` viejo se lee y se convierte a la curva que pasa por el mismo medio. Clic derecho sobre un punto o un tramo abre solo el menú de su forma, con su dibujo (`ShowShapeMenu`, `TimelineView.AutomationShapeRequested`). `xtlab comprobar-dsp`, filas "forma …" y "una curva vieja…".
- 2026-11-03: **Gráfico de compresión** (pedido del usuario: ver cómo comprime cada compresor): en el panel del Compresor y de los cinco clásicos (`Controls/CompGraph.cs`, desde `FxBody`), la curva con un punto donde está la señal y unos 4 s de entrada, salida y reducción. El motor deja un punto cada 256 muestras en un anillo sin locks (`CompScope`) y cada compresor da su curva (`ICompGraph.CurveOutDb`); sin el motor la curva sale de una copia fuera de él (`TrackInserts.CurveOf`). `xtlab comprobar-dsp`: la curva coincide con lo que suena, el gráfico recibe la señal, medir no asigna.
- 2026-11-03: **Compresores clásicos** (pedido del usuario: los modelos más famosos, nativos): FET, Óptico, VCA de bus, VCA clásico y Vari-Mu, en su grupo de "+ Efecto" (`Dsp/CompModels.cs`, tipos `fet`, `opto`, `buscomp`, `vca`, `varimu`, valores como los de modulación en `FxDefs`). Cada uno con su detector (pico o RMS), sus tiempos (release del óptico en dos etapas con memoria, Auto del de bus, 120 dB/s del VCA, las seis constantes del Vari-Mu, ratio que crece con el nivel) y su color de válvula o transformador. Nombres genéricos, sin marcas. Perillas por pasos (`FxParam.Steps`, como las llaves de los originales). `xtlab comprobar-dsp`, filas de los cinco.
- 2026-11-03: **Gráfico del Bombeo** (pedido del usuario): `PumpView` arriba de sus perillas, con la curva y un punto que la recorre en vivo (`PumpInsert.ShownPhase/ShownGain/ShownKey`); con sidechain, la barra del nivel de la key con sus rayas de disparo y rearme; con sidechain se ocultan Cada, Largo (%) y Desplazar y aparece Largo en ms (`PumpParams.KeyMs`, `FxDefs.BeatOnly` / `KeyedOnly`, `MainWindow.FxPanel.cs`).
- 2026-11-03: **Sidechain** (pedido del usuario, segunda parte de "como KickStart"): el compresor, los compresores clásicos, la puerta y el Bombeo pueden escuchar otra pista (`InsertSlot.Sidechain`, desplegable "Sidechain" en su panel, `KeyChoice`). La fuente deja su señal después de sus slots y antes del fader (una pista muteada igual manda) en una `SidechainKey` (`Sidechain.cs`, `TrackDspChain.KeyOut`), que se borra en cada bloque; quien escucha la lee por su `KeyTap` (`IKeyed`). Las fuentes corren primero (`Sidechains.Levels`: en vivo `PlaybackEngine.trackOrder`, al exportar por niveles en paralelo, también en los stems, con una key por juego de buffers); en vivo las keys son de `LiveKeys`. El Bombeo con fuente baja en cada golpe de esa pista. `xtlab proyecto insertos … sidechain n pista|propia`; `xtlab comprobar-dsp`, filas "Sidechain".
- 2026-11-03: **Bombeo** (pedido del usuario: un sidechain como KickStart, primera parte; el sidechain desde otra pista sigue en PENDIENTES): efecto nativo en Dinámica (`PumpInsert`, tipo `pump`, valores como los de modulación en `FxDefs`) que baja el volumen en cada 1/1 a 1/16 con Profundidad, Largo, Curva, Ataque y Desplazar, atado al tempo y a la posición de la canción (`InsertRack.Frame`; buses y master ahora reciben la suya menos su PDC, `MixLatency.Plan.NodeArrive`); con la canción quieta corre solo. Su reducción va al medidor de la tira. `xtlab comprobar-dsp`, filas "Bombeo".
- 2026-11-03: **Samples de la Biblioteca a los plugins** (lo marcaron unos colaboradores): el arrastre de un sample lleva también el archivo como lo lleva el Explorador (`DragSample`, `MainWindow.Samples.cs`), así lo toma la ventana de un sampler VST; el timeline y el sampler nativo siguen leyendo `xtreme-sample`. `xtvsthost` inicia OLE (`OleInitialize`) en el hilo del editor, sin lo cual algunos plugins no pueden recibir archivos.
- 2026-11-03: **Paneles en su propia ventana** (pedido del usuario, etapa 3 de "Paneles reubicables": todos los paneles): inspector, preview con "En vivo", timeline, mixer, piano roll, Mastering (cada uno a la suya) y la barra del proyecto salen a una ventana aparte, independiente, con su botón en la barra de tareas y la barra de título de la app, y vuelven sin perder estado ni tamaño, como el teclado virtual (`MainWindow.PanelWindows.cs`, botón en cada panel y Ver → "Paneles en ventana"; `panelsOut`, `panelWindow.<id>` en `config.json`). Lo que queda ocupa el lugar libre: preview afuera se apila como solo audio, timeline afuera cierra la fila de abajo si no hay mixer, mixer afuera deja el timeline fijo (`StageHere`, `BottomEmpty`, `MixerHere`, `KeepBottomRow` / `ApplyBottomRow`); Enter en una ventana de panel la maximiza.
- 2026-11-03: **Revisión de la interfaz, segunda tanda** (pedido del usuario): textos de controles y teclas por `Loc` y `Shortcuts.First` (fader, medidores, secciones, monitor en vivo, menús, ayuda del piano roll), claves `common.cancel/close/ok/quoted`, comillas «» y tooltips sin punto, el "…" dentro de cada clave; `MenuCheck` para En el video, Bloquear, envíos y Activa del carril, el menú del clip MIDI en el orden del de audio, Bloquear y plantillas en pistas MIDI, Entrada… en las de audio, Envíos en carpetas, y Quitar pista pregunta siempre (`AskRemoveTrack`); diálogos con Cancelar a la derecha, un solo aviso (`Border.notice`), `Border.dialog`, `dialog-title`, `Scrim`, `Button.link` y `act.close` en `App.axaml`; `TransportPanel` y `ToolbarPanel` en el transporte, el mixer, la barra de formato y la de velocidad, el nombre del proyecto se recorta antes de que las herramientas pisen Exportar; `Mute` e `InTune` cambian con acentos cálidos o verdes (`UiTheme.StateColors`) y escala compacta de letra en mixer y transporte; los vinculados se desvinculan sin preguntar (`MainWindow.History.cs`), la costura del loop del sampler siempre se funde (`SamplerSound.MinSeam`, `LoopBack`, ganancias según parecido), inversiones centradas (`Chords.Voice`, `RootOf`) con Alt+rueda (`TurnChord`) y la barra del piano roll en grupos (`PianoGroup`); el inspector de una pista MIDI muestra Mezcla y efectos.
- 2026-11-03: **Imán y grilla con los mismos valores** (pedido del usuario: los nombres de la industria en todos lados): el imán, la grilla de la regla y los del piano roll usan `Subdivisions` (Compás, 1/2 a 1/64, tresillos 1/2T a 1/32T, puntillos 1/2D a 1/16D, de mayor a menor), con su nombre musical en el tooltip (`SnapChoices`). Los puntillos arrancan en cada compás (`SnapGrid.Bar`, `ClipEditing.Snap`, también al cuantizar y al dibujar la grilla). Línea pasa a Auto y Celda se fue; lo guardado con los nombres viejos se convierte al abrir (`Subdivisions.Migrate`). `xtlab midi comprobar`, filas "Imán".
- 2026-11-03: **Franjas de rango, marcadores y acordes** (pedido del usuario): el rango A–B y los marcadores tienen cada uno su franja y su botón para mostrarla u ocultarla (`RangeStripShown`, `timeline.rangeStrip`); doble clic en la del rango lo saca. La de acordes se muestra sin acordes, y se escriben y renombran a mano con doble clic, y se mueven y estiran arrastrando (`ProjectMarkers.AddChord` / `RenameChord` / `SetChordTimes`, `ChordSlotAt`, `DragChords`, `MainWindow.Rename.cs`). `xtlab comprobar-dsp`, filas "Escala: acorde a mano" y "mover y estirar acordes".
- 2026-11-03: **Abren los proyectos de WildTrack** (pedido del usuario): su `guitarcam.json` (formato `guitarcam-project`, mismos campos) se reconoce en la lista, en "Abrir…" y con doble clic, y al abrirlo pasa a `Nombre.xtp` como proyecto de XTreme (`ProjectStore.WildTrackFileName`, `Project.OldFormatId`). Los ajustes viejos siguen sin migrarse. `xtlab proyecto verificar-guardar-como`.
- 2026-11-03: **Guardar como y archivo .xtp** (pedido del usuario: guardar y abrir desde cualquier carpeta, como cualquier programa): el proyecto es `<Nombre>.xtp` (JSON por dentro) y sigue al nombre al renombrar (`ProjectStore.FindFile`, `MatchFileToName`); un `xtreme_project.json` pasa a `.xtp` al abrirlo. En Proyectos, "Abrir…" toma un `.xtp` de cualquier lado, el proyecto nuevo tiene "Guardar en…", y el botón de guardar de cada fila (o Ctrl+Shift+S) da "Guardar como…" (sigue en la copia) y "Guardar una copia…" (sigue en el original), `ProjectLibrary.SaveAs`, `MainWindow.SaveAs.cs`. El paquete ZIP lleva `Nombre.xtp` y sigue abriendo los de antes. El instalador asocia `.xtp` (doble clic abre). `xtlab proyecto verificar-guardar-como`.
- 2026-11-02: **Clips en bucle** (pedido del usuario, como GarageBand; `ClipLoops`, `Clip.LoopOf`, `MidiClip.LoopBeats`, `AutomationBlock.LoopSec`, `Controls/TimelineView.Loops.cs`): la esquina de arriba a la derecha de una toma, un clip MIDI o un bloque de automatización lo repite hasta donde soltás, con el puntero y el ícono del bucle en cada vuelta. En una toma las vueltas son clips enteros (el motor, el video y la exportación no cambian) que se rehacen desde el original en cada edición; en MIDI y en automatización son vueltas del mismo clip, como las de un patrón. Historial lo nombra "Bucle de…". `xtlab proyecto verificar-bucles`.
- 2026-11-02: **Lápiz y Pincel en el timeline** (pedido de un colaborador, como los de FL Studio; `TimelineTool.Draw` / `Paint`, `Controls/TimelineView.Pencil.cs`, `MainWindow.Pencil.cs`, `ClipStamp`): clic en un clip lo toma, clic en un lugar vacío pone una copia; el Lápiz pone una (arrastrar la ubica), el Pincel repite a lo largo del tramo arrastrado, sin pisar clips, en un paso de deshacer. Un patrón de Patrones o un sample de la Biblioteca también se eligen con un clic; el sample se importa en su primera copia. Teclas 8 y 9 (P el Lápiz en FL Studio). `xtlab proyecto verificar-clips`, últimas filas.
- 2026-11-02: **El mixer no aplasta los faders** (`MixerMinH`, `ApplyMixerMinHeight`, `MainWindow.Mixer.cs`): el alto mínimo deja 120 px de fader debajo de la cabecera, las listas y el paneo, y crece con las listas; antes era 250 fijo y el fader quedaba en nada.
- 2026-11-02: **La imagen y el cabezal van con lo que se oye** (`HeardClock`, `PlaybackEngine.HeardSec`, `MainWindow.Tick`): con la PDC, el video, el cabezal y el reloj se atrasan lo que atrasa el master mientras suena. Tras arrancar o saltar esperan en el destino y en un bucle terminan la vuelta. `PositionSec` no cambió (grabación, ediciones, automatización nativa); al parar, la posición queda en lo último oído. `xtlab comprobar-dsp`, filas "PDC: el cabezal…".
- 2026-11-02: **Un plugin tarde ya no corre una toma** (`LiveLag`, `VstInsert.Steady`, `StartRecording`): durante una toma cada efecto VST arranca con el bloque extra ya dado, así que un bloque tarde entra en su lugar sin mover la mezcla, y la PDC de la toma se calcula antes del primer sample. Si un plugin se va en medio, `MixLatency.Compute(minMaster)` mantiene la demora. `xtlab comprobar-dsp` (filas "VST: en una toma…") y `vst efecto --toma`.
- 2026-11-02: **Los clips MIDI entran en las herramientas y el rango; el tono de prueba pasa por la pista** (`MidiEditing.Split`, `DeleteRange`, `MidiNote.Tie`, `NoteExpression.JoinTies`, `NotePreview`, `MainWindow.MidiSplit.cs`): Dividir (herramienta, Alt+X y menú), Borrar, Escuchar y el rango (borrar y cerrar el hueco) actúan sobre clips MIDI, con un paso de deshacer. Una división no cambia lo que suena: la nota que cruza el corte queda en los dos pedazos y el de la derecha va atado (`Tie`), que `NoteExpression` une a la de la izquierda antes de armar nada, así no hay segundo ataque ni cambia un glide. En un patrón, un corte en la raya de una vuelta deja dos clips del patrón; en otro lado, la derecha queda clip propio con sus repeticiones escritas, sonando igual (un clip de patrón no puede empezar en medio de una vuelta). Las notas que suenan al tocar el piano roll sin instrumento ya no van directo a la salida: entran antes de la cadena de la pista y sin asignar en el hilo de audio. `midi comprobar` (`MidiSplitChecks`).
- 2026-11-02: **El loop manda a tiempo las notas de después de A** (`InstrumentPlayer.Play`, `Turn`, `LiveTrack.MixInto`): el salto del bucle ya no es un salto para un instrumento: la mirada adelante llega a B antes que el playhead y manda el fin de la vuelta y el principio de la siguiente en el mismo bloque, cada nota en su muestra; en el giro terminan solo las notas que cruzan B, entran las que empezaron antes de A y lo tocado en el teclado queda sostenido. Medido con `xtlab vst bucle` en Surge XT a 256: de 350 a 530 muestras corrido a lo que la nota se mueve sola (20 a 80). Bucles más cortos que la mirada adelante y un bloque siguen saltando.
- 2026-11-02: **Un efecto VST que llega tarde ya no se pierde** (`VstInsert`, `FrameQueue`): lo que vuelve del plugin va a su lugar en la cola por muestra. El primer bloque tarde deja un hueco de un bloque y el slot queda un bloque más atrás hasta el próximo Play, con la compensación de latencia corrida con él: el bloque atrasado se oye, el plugin que llega siempre tarde suena entero y uno con picos pasa sin más cortes. Más atrás no va: lo que llega más tarde se pierde sin correr el resto. Medido con `xtlab vst efecto --tarde` en Surge XT Effects, MJUCjr y ValhallaSupermassive: de 480 a 562 bloques perdidos y todo en silencio a 14 a 18 (los primeros 100 ms, que no cuentan) y el mismo sonido a -141 dB.
- 2026-11-02: **Copiar al proyecto y volver a encontrar un archivo** (`LinkedMedia`, `MainWindow.Linked.cs`): en la Biblioteca una toma vinculada se copia a `media/` y una con el archivo faltante (marca Sin conexión; también en el menú del clip gris) se reencuentra eligiendo el archivo o buscando en una carpeta por nombre y tamaño (`Media.AudioBytes`/`VideoBytes`; sin tamaño va por nombre y duración y no adivina entre dos). Nunca mueve ni borra el archivo externo. El deshacer guarda dónde están los archivos de cada toma (`UndoManager.Place`). Lo que dejó una copia cortada se limpia al abrir (`SweepPartialCopies`). `xtlab proyecto verificar-vinculados` mata de verdad un proceso a mitad de copia y al terminarla (`LinkedChecks`).
- 2026-11-02: **Una toma MIDI sobrevive si el programa se muere** (`MidiTakeWriter`, `ProjectStore.MidiTakeJournal`): el hilo de audio sigue llenando `MidiTake` sin tocar disco y otro hilo copia lo nuevo a `media/<clip>.notas` cada 250 ms, con el cabezal; al abrir, `RecoverMidiTakes` arma el clip igual que al parar (compases, cuantizado). `xtlab midi comprobar` mata de verdad un proceso a mitad de toma (`MidiTakeChecks`).
- 2026-11-02: **Perillas del sampler automatizables** (`AutoTarget.Sampler`, `SamplerKnob`, `SamplerCurves`): corte, resonancia, drive, envolvente del filtro, volumen, paneo y lo que el LFO mueve (tono, filtro, volumen), con clic derecho en la perilla o el menú de la pista. Cada voz lee las curvas en sus pasos de control, en el cuadro de la canción: no pasa por `InsertAutomator`, así que da lo mismo en cualquier bloque y en la exportación. La velocidad del LFO y las envolventes quedan afuera: su efecto depende de la historia de la voz.
- 2026-11-02: **El sampler oye el slide, el portamento y el paneo por nota**: cada voz es su propia nota, así que no usa el pitch bend de `NoteSequence.Controls`: `NoteExpression` le da a la nota sostenida su recorrido (`NoteBend`, función del tiempo desde la nota, con la integral cerrada para caer bien en un salto) y `NoteEvent.Pan` se suma al paneo del sampler.
- 2026-11-02: **Fuera los nombres viejos** (pedido del usuario): `wtlab` → `xtlab`, `wtvsthost` y `wtvsthost32` → `xtvsthost` y `xtvsthost32`, identificadores `Wt*` → `Xt*`, `guitarcam.json`/`.lock` → `xtreme_project.json`/`.lock`, formato `xtreme-project`. Se borró lo que migraba desde GuitarCam y WildTrack (`AppFolder`, `ProjectLibrary`, `installer\XTreme.iss`; decisión del usuario): los proyectos y ajustes anteriores ya no se abren ni se copian.
- 2026-11-02: **Herramientas del piano roll** (pedido del usuario: todas las de FL Studio 2026): arpegiar, rasgueo, legato, cortar, unir, flam, claw, limitar, espejar, azar, Riff Machine, progresiones, cuantizador, groove, humanizar, niveles, LFO, rotar, espacio y más, en una tabla (`MidiTools`) con panel y vista previa que suena (`MainWindow.PianoTools.cs`). Pintar, cortar, lazo, selección inteligente, notas silenciadas, grupos y colores, fantasmas, onda de fondo, nombres de teclas, carril por valor, sellos, detección de acordes. Expresión por nota hasta los plugins (`NoteExpression`, protocolo 3 del host), .mid (`MidiFiles`), partitura PDF (`ScoreSheet`), arpegiador en vivo y teclado a la escala.
- 2026-11-02: **Varios instrumentos en un patrón** (pedido del usuario): un clip por pista (decisión del usuario), cada instrumento una parte con sus notas (`MidiClip.PatternPart`, `MidiPatterns.Parts/AddPart/RemovePart/Together`), sin tocar el motor. En el piano roll, un chip abre la lista de instrumentos, con notas fantasma por instrumento (`MainWindow.PatternParts.cs`); un uso del patrón se mueve, se estira y se duplica junto (`TimelineView.Midi.cs`). `wtlab midi comprobar`, `midi patron-instrumento`. Sirve de base para importar los patterns de FL Studio.
- 2026-11-02: **Sampler nativo** (pedido del usuario): un instrumento más de la pista MIDI, con un sample. Arrastrar un archivo a su ventana o a la pista lo carga (Biblioteca: "Abrir en el sampler"); onda con inicio, fin y loop (adelante, ida y vuelta, con crossfade), Nota o One shot, raíz detectada por el nombre o el sonido, detector de afinación del tramo elegido con botón Afinar (pedido del usuario), voces, mono, legato y glide, ADSR de volumen y de filtro dibujadas y arrastrables, filtros LP/HP 12 y 24 dB, banda y notch con drive, LFO a tono, filtro y volumen (libre o al tempo) y envolvente de tono. `Sampler`, `SamplerSound`, `SamplerSettings`, `SamplerWindow`, `MainWindow.Sampler.cs`; el motor es el mismo en vivo y en la exportación, sin asignar memoria y con la nota en su cuadro exacto (`SamplerChecks` en `comprobar-dsp`, `wtlab sampler`, `wtlab midi sampler`).
- 2026-11-02: **Teclas de otros DAW** (pedido del usuario): Configuración → Atajos de teclado → Distribución de teclas cambia todo con un clic a Cubase, Logic Pro, FL Studio, Reaper o Studio Pro (`KeyLayouts`, `Shortcuts.SetLayout`, `config.json` "shortcutLayout"); cada distribución guarda sus propios cambios ("shortcuts.cubase"…) y lo que el DAW no tiene sigue con la tecla de XTreme. `wtlab atajos comprobar`.
- 2026-11-02: **Copiar o vincular al importar** (pedido del usuario): `ImportAudio` pregunta (`ImportDialog`, con "No mostrar más") o sigue Configuración → Guardado y exportación (`ImportMode`, `config.json` "import.mode"). Vincular deja el archivo donde está (`AudioImport.LinkIn`, `Media.Linked`, marca en la Biblioteca); lo que solo lee FFmpeg entra igual como WAV. `wtlab proyecto importar-audio --vincular`.
- 2026-11-02: **Miniaturas de plugins** (pedido del usuario): cada plugin toma la suya al abrir su ventana por primera vez (`snapshot` en `wtvsthost`, `PluginThumbs`); el botón Miniatura la rehace; se ven en Configuración → Plugins y en un tooltip en los menús (`PluginThumbView`). `wtlab vst miniatura`.
- 2026-11-02: **El transporte anda también con plugins hechos con una página web** (pedido del usuario): en Splice INSTRUMENT el foco queda en `msedgewebview2.exe`, otro proceso, y sus teclas no pasaban por el bucle del host. `ForeignKeys` (`wtvsthost`) las toma con un hook de teclado en su propio hilo, solo con la ventana abierta y al frente y el foco en otro proceso que no sea la app; un segundo hilo mira con UI Automation si hay un campo de texto en uso. Sin probar el campo de texto: Splice pide iniciar sesión.
- 2026-11-02: **Enviar comentarios** (pedido del usuario): error o sugerencia desde la barra de título y "Acerca de" (`FeedbackDialog`), que sigue en el issue nuevo de GitHub ya lleno (`IssueReport`, `wtlab reporte`), sin token en la app; el usuario lo envía con su cuenta, y lo que no entra en el enlace va por el portapapeles. Tema nuevo en la Ayuda y en Novedades.
- 2026-11-02: **El transporte anda con un plugin en foco** (pedido del usuario): la ventana del plugin es de otro proceso y sus teclas no llegaban a la app. `wtvsthost` saca del plugin los atajos del grupo Transporte (`TakeKey`, `transport-keys`, `transport-key`) salvo que haya un cursor de texto, y la app los pasa por `OnKey` (`MainWindow.PluginKeys.cs`).
- 2026-11-02: **Novedades y Ayuda más amable** (pedido del usuario): ventana con los cambios de cada versión, que se muestra sola la primera vez y se abre con el megáfono de la barra de título, del gestor y del inicio de la Ayuda (`NewsDialog`, `NewsGuide`, `Assets/News`, `config.json` "newsSeen"; cargadas 0.9.11 y 0.9.12). La Ayuda suma un ícono por tema, "Lo más buscado", pasos unidos por una línea, tema anterior y siguiente, y `{icon:…}` para mostrar el botón en el texto; se puso al día con lo agregado después de escribirla (temas nuevos: Invertir, EQ dinámico y Novedades) y las migas del camino recuperan su estilo (`Button.link`).
- 2026-11-02: **Lista "Para probar"** (pedido del usuario: con varios cambios se perdía qué había probado y qué andaba): cada cambio se anota en `pruebas\lista.json` (`TestList`, `wtlab lista`, `probar.ps1 -NoAbrir "texto"`, `-Lista`, `-Arregla n`), seguro con varias sesiones a la vez; una copia de prueba la muestra en una ventana aparte (`TestListWindow`, `MainWindow.TestList.cs`) donde cada ítem se marca Anda o No anda, con nota. Reemplaza al toast de `--probar`. Los agentes preguntan si se abre ahora o queda en la lista.
- 2026-11-02: **Escalas y acordes** (pedido del usuario): la escala de la canción (`MusicScale`, en el proyecto) se elige desde el piano roll o el teclado virtual y los dos la siguen: filas resaltadas, notas que caen en la escala y se mueven por grados, y en el teclado las filas de Z, A y Q como tres octavas de la escala, con sus letras en las teclas. Acordes de un clic con vista previa, y un selector de inversiones sobre cualquier acorde elegido (`Chords`, `MainWindow.Scales.cs`).
- 2026-11-02: **Imán como el de FL Studio** (pedido del usuario): timeline y piano roll comparten la lista (`SnapChoices`): Línea, Celda (`SnapGrid`), Sin imán, fracciones de paso y de tiempo, Tiempo, 2 tiempos y Compás; el piano roll suma "Igual que el timeline" y su imán pasó a un menú. Cuantizar pide una división fija.
- 2026-11-02: **Plugins VST2 y de 32 bits** (pedido del usuario): `wtvsthost` también abre VST2 (`vst2.h`, encabezado propio porque Steinberg ya no licencia ese SDK; `vst2plugin.h`, con shells tipo Waves) y el mismo fuente da `wtvsthost32.exe`; la app elige el host por los bits del archivo (`PluginFile`, `VstHost.Find`). La búsqueda suma las carpetas de VST2 y las de 32 bits, y los menús y Configuración → Plugins marcan los que son VST2 o de 32 bits.
- 2026-11-02: **Automatizar desde la ventana del plugin** (pedido del usuario): un botón en la barra del host espera a que se mueva un parámetro de ese plugin y le crea su carril al soltarlo (`MainWindow.AutomationListen.cs`). Los efectos de pista ahora también tienen barra, y `wtvsthost` avisa cada gesto, no solo cuando cambia de parámetro.
- 2026-11-02: **Varias sesiones ya no se pisan al probar** (pedido del usuario: se colgaba todo): compilar, comprobar y copiar van de a una, sin que nadie tenga que acordarse (`HeavyTurn`, `tools\turno.targets`, también en `dotnet build`), y `wtlab` y `probar.ps1` con prioridad baja; `comprobar-dsp` usa su propia carpeta temporal y ya no falla por una máquina ocupada (Extraer MIDI, escaneo de samples).
- 2026-11-02: **Nueva pista sin barra** (pedido del usuario): la barra fija que tapaba las pistas pasó a ser un botón + en la barra de zoom (`NewTrackButton`), y el clic derecho en toda el área vacía bajo las pistas abre su menú; el scroll deja 64 px libres debajo de la última (`TailH`).
- 2026-11-02: **Presets del compresor por categoría** (pedido del usuario): 96 presets en 12 categorías (`CompPresetTable`), en submenús (`SelectButton` con grupos). Buses y Usos generales mantienen el volumen (`CompPresets.SameLevel`) y un compresor nuevo en un bus o en el master arranca con uno de esos; elegir preset en el master ahora se mide (antes no hacía nada). Los presets se aplican con valores fijos, sin leer la toma (decisión del usuario); **Analizar toma**, al lado del preset, la mide solo si se lo pide y la envolvente se calcula recién ahí, no al cargar. `wtlab comprobar-dsp` prueba todos (`CompPresetChecks`).
- 2026-11-02: **FFmpeg instalado con winget se encuentra aunque falte en el PATH** (pedido del usuario): winget lo daba por instalado, pero su carpeta ya no estaba en el PATH y reinstalar no la repone; `FfmpegSetup.RefreshPath` busca en las carpetas de paquetes de winget y corre al arrancar la app y `wtlab`, antes que la cámara.
- 2026-11-02: **Ícono más profesional y sin Blender** (pedido del usuario): `tools/icon/Vinilo.cs` lo dibuja píxel a píxel, cada tamaño a su resolución y sin nada instalado; más vinilo a la vista, etiqueta de metal naranja y una X de letra con corte recto y bisel en vez de la de puntas redondas (`tools/make-icon.ps1`).
- 2026-11-02: **Aspecto de pistas, faders, perillas y mixer** (pedido del usuario): la pista seleccionada lleva un borde de acento en vez de la barrita (`DrawSelectedFrame`); los faders ganan contraste y el del encabezado usa la tapa del mixer (`Fader`, `DrawSideCap`, `DrawVolume`); todas las perillas son de consola (`Knob.Draw`); en el mixer todo va a 6 px y el master se separa con una línea de 2 px (`PinnedColumn`).
- 2026-11-02: **El inspector edita varias pistas a la vez** (pedido del usuario): con varias pistas elegidas muestra un aviso con cuántas son y solo lo común, de audio y MIDI (Mezcla, Salida, envíos compartidos, Transponer; "En el video" sin MIDI); cada control va a todas en un paso de deshacer, lo que no comparten dice "Varios" y las bloqueadas quedan afuera (`MainWindow.InspectorGroup.cs`, `EditPicked`, `GroupRouter`).
- 2026-11-02: **Clic derecho con varias pistas elegidas** (pedido del usuario): el menú actúa sobre todas (video, bloquear, Carpeta, quitar) y quitar también va por Supr y el tacho, en un paso de deshacer (`TrackGroup`, `TrackGroupItems`, `RemoveTracks`).
- 2026-11-02: **Mover un clip con imán lo pone en la línea, Shift+arrastrar lo copia y atajos de FL Studio** (pedido del usuario): el comienzo cae en la grilla en vez de conservar el desfasaje, también en clips MIDI (`ClipEditing.Drag`, `UpdateMidiDrag`); Shift+arrastrar el cuerpo de un clip deja una copia donde estaba (`TimelineView.cloning`, `MidiEditing.LeaveCopies`); Ctrl+B duplica, Insert divide, Shift+Q alinea a la grilla (`AlignClips`), Ctrl+A marca todo y Ctrl+D saca la selección (`Shortcuts`). `comprobar-dsp` (`ClipChecks`).
- 2026-11-02: **El bucle gira sin fundido** (pedido del usuario): saltaba con el crossfade de un seek, así que A entraba fundiéndose, y al final del bloque, salteando hasta un bloque del comienzo. Ahora gira en la muestra exacta: el empalme de 5 ms va antes de B y A entra entero (`LoopSplice`, `PlaybackEngine`); el click no pierde el tiempo de A. Además el aviso "Ajustando al tempo" sale solo en el clip que espera su sonido y, con Ctrl, las manijas y el cursor muestran que se va a estirar (`ClipRenderProgress`, `StretchTick`, `ToolCursors.Stretch`). `comprobar-dsp` (`LoopChecks`).
- 2026-11-02: **El time-stretch conserva los ataques** (pedido del usuario: loops de ritmo): `TimeMap` los detecta y el render va a 1:1 alrededor de cada uno, con el estiramiento entre ataques. En `wtlab` pasaron de 22 ms a ×2 de largo y 60 ms a ×4 a menos de 1 ms, sin pre-eco medible; un loop con un golpe cada 100 ms queda a menos de 3,5 ms. `PitchShifter.Version` 2: los renders en `cache/` se rehacen. `comprobar-dsp`.
- 2026-11-02: **Tono por clip** (pedido del usuario): clic derecho → Tono, de −12 a +12 semitonos sumados al Transponer de la pista, también en varios clips (`Clip.Pitch`, `Clip.SemitonesOn`, `ClipEditing.SetPitch`, `MainWindow.PitchItem`); usa el render y la caché de Transponer, el clip muestra "+2" (`DrawPitchTab`). `wtlab proyecto editar carpeta tono clip semitonos`; `comprobar-dsp`.
- 2026-11-02: **Estirar un clip a mano y preguntar al cambiar el tempo** (pedido del usuario): Ctrl+arrastrar la manija de recorte estira el clip con el otro borde fijo, sin tope, con imán a la grilla (`ClipEditMode.StretchStart/End`, `ClipEditing.StretchEdge`, `TimelineView.GrabMode`); dos pedazos de una toma con distinto estiramiento ya no se unen como audio continuo (`Clip.TakeKey`, `LiveTrack.UpdateTransitions`). Con clips en el timeline, cambiar el tempo pregunta una vez, cuando el control queda quieto: Estirar, Solo la grilla o Cancelar (`TempoDialog`, `MainWindow.TempoEdited`). `wtlab proyecto editar carpeta estirar-inicio|estirar-final`; `comprobar-dsp`.
- 2026-11-01: **Versión 0.9.11** (instalador `XTreme-Setup.exe` en el release `v0.9.11`, armado en local para que lleve `lib\demucs-graphs`).
- 2026-11-01: **Los clips MIDI siguen su compás al cambiar el tempo** (el tempo sigue fuera del deshacer): `TempoFollow.SetBpm` y `SetGrid` los mueven; cada paso de `UndoManager` guarda también su BPM y `TempoFollow.Regrid` acomoda lo que movió un cambio de grilla, que la escala de tiempo no ve. Comprobado en `wtlab midi comprobar`, y patrones seguidos no se pisan ni dejan huecos.
- 2026-11-01: **Ayuda dentro de la app** (pedido del usuario: didáctica, simple y por categorías): F1 o el ? de la barra de título y del gestor abren `Dialogs/HelpDialog`, con buscador y 71 temas en 12 categorías, nombrados por lo que la gente quiere hacer y contados en pasos numerados, más una categoría para cuando algo no funciona. El contenido va aparte de los textos, en `Assets/Help/<idioma>.json` (`HelpGuide`), en español e inglés; los atajos se leen de `Shortcuts`, así que muestra los que el usuario configuró.
- 2026-11-01: **Todo sigue el tempo con time-stretch** (pedido del usuario; decidió: todos los clips sin elegir, video incluido, motor propio, silencio hasta que esté): cambiar el BPM cambia la velocidad de la canción sin cambiar el tono y deja clips, automatización, marcadores, acordes, loop y cabezal en su compás (`TempoFollow`, `Clip.Stretch`, `ProjectSettings.TimeScale` para el deshacer). El mismo PVDR de Transponer hace tono y largo en una pasada (`PitchShifter.Render`), en `cache/` (`TransposeCache`), de a uno y en segundo plano; el clip calla con fundido de 10 ms hasta que está (`ClipSound.Silent`, `LiveTrack.AddClips`, `MainWindow.Transpose.cs`). La imagen sigue al sonido en la preview y en el MP4 (`ExportTakeSpec.Stretch`, `setpts`). "Detectar tempo" solo mueve la grilla (`TempoFollow.SetGrid`). `wtlab estirar`, `proyecto editar carpeta tempo`; `comprobar-dsp`.
- 2026-11-01: **Invertir un clip** (pedido del usuario: reverse): clic derecho en un clip de audio, también con varios seleccionados; suena de atrás para adelante en el mismo lugar, igual en vivo y al exportar, y el archivo no cambia (`Clip.Reversed`, `ClipEditing.Reverse`, `ClipSound.Flipped`, `ProjectStore.TrackAudio`). La onda se dibuja invertida y el clip lleva una flechita. No aplica a clips con video. `wtlab proyecto editar carpeta invertir clip`; `comprobar-dsp`.
- 2026-11-01: **Transiciones solo entre videos** (pedido del usuario): el chip de la unión y las entradas del clic derecho aparecen solo si los dos clips tienen imagen en una pista con video de un proyecto con video (`LiveTrack.OffersTransition`). Una transición ya guardada entre clips de audio sigue sonando y conserva su chip para cambiarla o sacarla.
- 2026-11-01: **Editar un patrón es ese patrón** (pedido del usuario): un clip de patrón abre en el piano roll solo una vuelta desde el compás 1, sin los otros clips ni repeticiones, con el largo en un chip de la cabecera (1 a 16 compases u "Otro…"); el cabezal recorre el patrón mientras suena cualquiera de sus clips (`PianoRollView.PlayBeat`, `MainWindow.Midi.cs`).
- 2026-11-01: **Pestaña Patrones** en la barra del proyecto (pedido del usuario): lista de patrones con vista de sus notas (`MainWindow.Patterns.cs`, `Controls/PatternPreview`); arrastrar a una pista MIDI suma un clip enlazado, y desde la fila se renombra, se abre el piano roll y se elimina eligiendo cada vez entre borrar los clips o dejarlos únicos (`MidiPatterns.Remove`, `Dissolve`), con deshacer y su paso con nombre. Las pestañas se juntan o pasan a íconos si no entran (`FitProjectTabs`). `wtlab midi patron-disolver`, `patron-borrar`; `midi comprobar`.
- 2026-11-01: **Automatización** (pedido del usuario: cualquier parámetro de un canal, de audio o MIDI): clic derecho en el control o menú de la pista → carril hijo de la pista en el timeline (`Automation.cs`, `TimelineView.Automation.cs`, `MainWindow.Automation.cs`). Bloques como los clips MIDI (`AutomationBlock`), con copiar, duplicar y repetir N veces, y curva opcional por tramo (`AutomationPoint.Curve`). Volumen, paneo, mute, envíos, perillas y ON de efectos nativos, bandas del EQ y parámetros VST3 de efectos e instrumentos, iguales en vivo y al exportar (`AutoCurve`, `InsertAutomator`, `VstParamCurves`; protocolo 2 del host, con `params` y `touched`). Los controles siguen al carril. La reverb cruza impulsos al cambiar. `wtlab comprobar-dsp`.
- 2026-11-01: **Patrones por pista** (pedido del usuario, como en FL Studio): clips MIDI enlazados (`MidiPatterns`, `MidiClip.PatternId`): editar uno cambia todos, duplicar o pegar sigue enlazado, estirar el clip repite la vuelta. Crear, renombrar, largo y "Hacer único" (suena igual) en el clic derecho del clip; "Insertar patrón en el cabezal" en el de la pista MIDI. El piano roll edita la primera vuelta y muestra tenues las repeticiones. Cada clip guarda su copia de las notas, así que una versión vieja abre el proyecto. `wtlab midi patron`, `patron-insertar`, `patron-unico`; `midi comprobar`.
- 2026-11-01: **Mastering modular** (pedido del usuario): la cadena del master es su lista de efectos (`MasterSettings.Slots`), la misma en la tira y en Mastering, con un módulo por efecto que se arrastra (`MasterModules`, `SlotDrag` de costado). Look y Sugerime escriben en esos módulos (`FxDefs.SyncStages`); un proyecto viejo migra al abrir y suena igual (`MoveChainToSlots`, `FxChecks`). Soft clip, Limitador y Destino quedan fijos en Salida. La tira del master sigue el ancho del mixer.
- 2026-11-01: **Modulación, menú por categorías y EQ de 7 bandas dinámico** (pedido del usuario): Chorus, Flanger, Phaser y Tremolo con paneo automático (`Dsp/ModInserts.cs`, listas en `FxDefs`). "+ Efecto" agrupa los nativos en Dinámica, EQ, Saturación, Modulación y Espacio y estéreo (`FxGroups`). El EQ suma una campana a 8 kHz antes del corte de agudos, y un proyecto de 6 bandas se lee igual (`EqSettings.Normalize`). Cada campana o estante puede ser dinámico (`EqDynamics`: umbral, rango, ataque y release, sin latencia), con su recorrido en el gráfico. Botón de vista grande: una ventana con el mismo editor, que sigue al slot (`ShowEqBig`). `wtlab comprobar-dsp` (`ModChecks`).
- 2026-11-01: **Efectos nativos: panel único y efectos del master en cualquier canal** (pedido del usuario, base del punto 9 de PENDIENTES): lista de parámetros de cada efecto (`FxDefs`) y un solo panel (`MainWindow.FxPanel.cs`, `FxBody`) para inspector, ventanita del mixer y timeline, que escala con su ancho; reemplaza a `*Section` y `*Panel`. El inspector de un bus muestra sus efectos; el compresor a mano se edita en cualquier canal. Limpieza, Multibanda (con Púa), Color, Cinta, Ancho, Soft clip y Limitador son slots (`MasterInserts.cs`, `MasterMultiband`), en pistas, buses y master; reverb y delay también van en pistas. Todo efecto nativo tiene Dry, Wet, Salida y fundido al prender y apagar (`FxShell`). El master tiene su lista de efectos (`MasterSettings.Slots`, antes de la cadena de Mastering, solo nativos) y "Cadena completa" agrega las etapas que falten (`ChannelInserts.AddMasterChain`). Desde el timeline, el menú de la pista → Efectos. Sale el Look "Domar la púa" (la perilla Púa queda). `wtlab proyecto insertos --master`, `comprobar-dsp`.
- 2026-11-01: Biblioteca: arrastrar una toma de la vista Proyecto al timeline, igual que un sample (`ClipEditing.PlaceMedia`, `wtlab historial usar --en`), e íconos en todo el panel (`IconText`).
- 2026-11-01: Biblioteca → Samples: carpetas del sistema en árbol, búsqueda por palabras, Audio / Video / Favoritos, onda y largo por fila, escucha por ASIO con su volumen y loop, arrastrar al timeline o doble clic (`SampleLibrary`, `SampleList`, `MainWindow.Samples.cs`, `SampleVoice`, `TimelineView.Drop.cs`). Importar acepta casi cualquier audio y video (`MediaDecoder`): lo que solo lee FFmpeg entra como WAV en media/, y un video trae su imagen si se pide. `wtlab samples`.
- 2026-11-01: Componentes opcionales: una sola lista (`Components`, `ComponentInstall`, `ComponentList`) para FFmpeg y los modelos de stems: aviso al abrir, Omitir, Configuración → Componentes, Acerca de y la función deshabilitada; descarga con reanudación y hash (`FileDownload`, `wtlab componentes`); los modelos de stems se bajan de Meta y se convierten en la PC (`StemConverter`, `tools\make-demucs-templates.py`). Reemplaza a `FfmpegDialog`.
- 2026-11-01: **La ventana de un plugin respeta la escala de pantalla** (surgió del issue #2, que sigue abierto: lo que se agranda es toda la app y esto no lo resuelve de forma comprobada; `sesiones.log` registra los cambios de escala): `wtvsthost` pasa a ser per-monitor DPI aware (sin esto Windows lo estiraba y el panel quedaba escalado dos veces), le da la escala al plugin (`setContentScaleFactor`, también al cambiar de monitor con `WM_DPICHANGED`), escala la barra y el panel (`keys-panel` va a 100 %) y solo le avisa al plugin si su tamaño cambió de verdad (`main.cpp`).
- 2026-11-01: **Importar y abrir audio mucho más rápido, con barra de progreso** (pedido del usuario): 5 minutos en MP3 a 44,1 kHz pasan de 14 s a 2 s. Lo que tardaba era el remuestreo a 48 kHz (senos y cosenos por muestra y por coeficiente): `AudioLoader.Resample` calcula el filtro una vez por fracción de muestra, da lo mismo que antes (`proyecto verificar-importar`) y reparte el trabajo entre los núcleos; también se nota al abrir proyectos y exportar. La importación decodifica una sola vez (`AudioImport.Read`, `importedAudio`) y muestra un toast con barra (`ToastHost.ShowProgress`) si tarda más de 0,3 s. `wtlab tiempos-carga` mide cada paso.
- 2026-11-01: **Detectar escala y acordes** (pedido del usuario): clic derecho en un clip de audio. `ScaleDetector` saca la escala (perfiles de Krumhansl–Kessler, con la afinación de la toma y los parciales devueltos a su nota) y los acordes por tramo (mayores, menores y quintas, con un camino de Viterbi); los acordes deciden entre una escala y su relativa. `Dialogs/ScaleDialog` da el resumen y un check agrega los acordes al timeline, en un paso de deshacer. `wtlab escala`, y sus filas en `comprobar-dsp`.
- 2026-11-01: **Acordes mejores y cadena de métodos** (pedido del usuario): los acordes miran el bajo y alisan golpes y notas de paso, ninguno dura menos que un tiempo y solo el silencio queda sin acorde (`ScaleDetector.Chords`). La ventanita muestra la seguridad de la escala y de los acordes; si la menor no llega a 85 %, repite sin voz ni batería (Demucs) o, sin esos modelos, por notas. Son estimaciones: se midió la raíz contra el stem de bajo en un solo tema (`wtlab escala --bajo`), de 51 % a 76 %. Lee hasta 10 minutos y la barra de progreso llega al final.
- 2026-11-01: **Franja propia para los acordes y franjas que se ocultan** (pedido del usuario): los acordes detectados van en `Project.ChordMarkers`, como bloques debajo de los marcadores (`TimelineView.Chords.cs`); se marcan de a varios y se borran con Supr o el menú, marcados o todos. Dos botones junto a las herramientas muestran u ocultan la franja de acordes y la de marcadores, y la regla sube con ellas.
- 2026-11-01: **Menús del clip y de la pista por grupos** (pedido del usuario): edición, lugar, recorte y análisis en el clip; pegar, pista, sonido, orden y plantillas en la pista (`MainWindow.Timeline.cs`). Una sola lista, sin submenús nuevos.
- 2026-11-01: **Separar en stems usa la placa de video** (pedido del usuario): NVIDIA, AMD o Intel por DirectML, y 4 stems en Alta pasa de 4:48 a 45 s en una RX 6800 XT. La causa del audio equivocado era que DirectML fusiona los `Sigmoid`: `make-demucs-onnx.py` expone sus salidas. `StemSeparator.Choose` elige la placa y comprueba cada modelo contra la CPU con el audio real; si falla, o si la placa falla a mitad de camino, sigue en la CPU. Pasa a `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4. `wtlab separar --cpu`.
- 2026-11-01: **Separar en stems se cancela a la vista** (pedido del usuario): toast fijo con "Cancelar" mientras corre, que corta la red en el acto (`Net`, `RunOptions.Terminate`). `wtlab separar --cancelar`.
- 2026-11-01: **Separar en stems** (pedido del usuario): clic derecho en un clip de audio → 2, 4 o 6 stems y calidad Rápida, Alta o Máxima, con Demucs en la CPU. Cada stem queda en un WAV en media/ y en su pista, en una carpeta bajo la del clip, y la original se silencia; "Resto" es la mezcla menos los demás, así que siempre suman la mezcla. `StemSeparator` (la STFT en C#, -80 dB contra PyTorch), `tools/get-demucs.ps1`, `Dialogs/StemsDialog`, `MainWindow.Stems.cs`, `AudioImport.WriteIn` y `AddStems`, `wtlab separar`. Los modelos no van en el instalador: la licencia de los pesos no está clara.
- 2026-11-01: **README más corto** (pedido del usuario): requisitos, compilación, atajos, estructura y `wtlab` pasan a `DETALLES.md` y `DETALLES_EN.md`; quedan la presentación, qué hace, hacia dónde va, licencia y firma.
- 2026-11-01: **Ícono redondo con transparencia en todos los tamaños** (pedido del usuario), sin el cuadrado de fondo: también en el splash, Acerca de y los README. Hasta 96 px (barra de tareas, accesos directos, barra de título) es un disco plano: etiqueta naranja viva, X y aro de vinilo negros, dibujado en `tools/make-icon.ps1` (`New-MarkPng`). El `.ico` suma cuadros de 72 y 96 px para que un acceso directo a 150 y 200 % de escala no reduzca el render. Desde 128 px, el render de Blender solo; `vinilo.py` pierde `--plano`.
- 2026-11-01: **La columna de encabezados del timeline se achica mucho más** (pedido del usuario): va de 94 a 640 (antes 324), el encabezado completo llega hasta 276 y debajo pasa a compacto (`TimelineView.DrawCompactHeader`, `CompactBtn`): ícono, ● M S y medidores verticales más anchos, en fila, en tira o apilados según el lugar, con el nombre solo si entra. Las barras de arriba y del zoom sacan lo que ya no entra (`MainWindow.FitTimelineBars`).
- 2026-11-01: **Título del splash y de Acerca de con más carácter** (pedido del usuario): `Controls/BrandTitle` reemplaza el nombre y la frase de los dos: "XTREME" en Bahnschrift Bold con la X en el acento, una tira de barras estática como una onda que se apaga hacia la derecha y "GRABÁ. MEZCLÁ. EXPORTÁ." en mayúsculas espaciadas.
- 2026-11-01: **Ícono nuevo de XTreme: un vinilo con la X en una etiqueta gigante de metal naranja torneado** (elegido entre varios bocetos; reemplaza al diafragma; la X es de metal oscuro pulido y la etiqueta lleva líneas finas concéntricas y reflejo anisótropo). `tools/icon/vinilo.py` lo renderiza en Blender (vista cenital, surcos procedurales con reflejo anisótropo, cuatro luces en las diagonales) y `tools/make-icon.ps1` lo pone sobre el cuadrado oscuro: con surcos desde 128 px y liso en los tamaños chicos. Ya no hacen falta Inkscape ni Python; se borraron `escena.py`, `relieves.py` y los dibujos de `tools/icon/instrumentos`. El script también escribe `docs/xtreme-icon.png` (el de los README).
- 2026-11-01: La app pasa a llamarse **XTreme** (internamente "Xtreme DAW"): proyectos, carpetas y namespaces `XtremeDaw.*`, `XtremeDaw.slnx`, `XTreme.exe`, ensamblado y recursos `XTreme`, instalador `XTreme-Setup.exe` (`installer/XTreme.iss`), `docs/xtreme-icon.png`, textos de es/en, README y guías. Los ajustes pasan a `%APPDATA%\XTreme` (`AppFolder` copia lo que falte de `%APPDATA%\WildTrack` y de `%APPDATA%\GuitarCam Studio`) y la carpeta de proyectos por defecto sigue la que exista (`Documentos\WildTrack Proyectos`, `GuitarCam Proyectos`) o crea `XTreme Proyectos`. `UpdateCheck.Repo` apunta a `xtrimdr/XTreme`. El ícono no lleva texto, queda igual.
- 2026-11-01: **Versión 0.9.10 publicada** (instalador `WildTrack-Setup.exe` en el release `v0.9.10`).
- 2026-10-31: **Los plugins en vivo saben el bloque real del driver**: se les anunciaba 8192 y Splice INSTRUMENT
  gastaba el doble (445 → 230 µs por bloque a 192 muestras, peor caso 1,2 → 0,7 ms); el DSP dejó de dispararse con sus
  cuerdas (lo probó el usuario). `wtvsthost --block`, `VstBridge.Start(maxBlock)`; el host parte en pedazos un bloque
  mayor (el warmup, un cambio de buffer). `audio-cortes.log` desglosa el peor callback, el host tiene `stats`, y wtlab
  suma `colas` y `vst vivo --tope --prioridad --mmcss`.
- 2026-10-31: **El teclado toca una sola pista**: la elegida o, sin ninguna, la primera armada; antes sonaba también en
  todas las armadas y las notas llegaban a dos instrumentos. Grabando, en las armadas (`Hears`, `KeyTrack`).
- 2026-10-31: **Teclado MIDI virtual** (Ctrl+K o el botón del transporte): A S D F G H J K L son las blancas, W E T Y U
  O P las negras, Z/X la octava y C/V la velocidad; los demás atajos siguen andando. Las notas entran como las de un
  teclado real (`VirtualKeyboard`, `MidiInputs.VirtualPort`): suenan en la pista elegida y las armadas, y se graban. Una
  tira bajo el transporte muestra las teclas y se toca con clic (`VirtualKeyboardView`, `MainWindow.VirtualKeys.cs`).
  Las teclas se leen por Raw Input en su propio hilo, sin esperar a la UI (`RawKeys`; `sesiones.log` dice cuánto ahorra),
  Shift es el sustain, la fila sigue después de la L (Ñ, { y + en un teclado latino), la tira muestra de 1 a 7 octavas (la ruedita hace zoom) y
  se puede transponer. Con el modo prendido, el panel sigue a la ventana de un instrumento (el host avisa dónde está:
  `editor-rect`, y el panel va integrado adentro de ella: `EmbedPanel`) y con esa ventana al frente las teclas tocan ahí y en la pista de ese plugin. La tira muestra dónde
  caen las notas transpuestas, se desplaza con clic derecho, y Transponer, Velocidad y Rango son barras (`ValueBar`).
  Con una ventana de instrumento abierta las teclas tocan siempre en su pista, y esa ventana tiene el aspecto de la
  app, su ícono y una barra con Teclado, M, S y Detener notas (`main.cpp`, `ShowPluginBars`). El Sustain es un botón On/Off con la tecla Shift a la vista: Shift mientras se aprieta, clic para dejarlo prendido.
  `probar.ps1` ya no se
  traba: pregunta en una ventana siempre visible, se resuelve solo y confirma que la copia arrancó.
- 2026-10-31: **Instrumentos VST3 que quedaban mudos**: cuando el cabezal cae en medio de una nota larga, la nota se
  retoma (`InstrumentPlayer.CatchUp`); antes un acorde largo no sonaba hasta la nota siguiente. Un plugin que recién
  abrió se prueba con una nota de la pista hasta que suena, antes de ponerlo en la pista y antes de exportar
  (`InstrumentWarmup`): Splice INSTRUMENT perdía las notas mientras cargaba y exportaba silencio. Un bloque que llega
  tarde se oye con el siguiente (`VstBridge.Exchange`). `wtlab vst vivo` toca un instrumento como la reproducción.
  Con la canción parada ahora se oye lo que el plugin toca solo, como las teclas de su ventana (`InstrumentPlayer.Idle`,
  después de que se apaga la cola de la canción); antes se descartaba.
- 2026-10-31: **Un solo ícono para los buses sin ícono** (`FxIcon.Bus`; antes dependía de su primera reverb o delay),
  también en los envíos. La tira seleccionada del mixer se marca con el borde en acento y conserva el color del canal
  en el filo de arriba (`StripCardLook`).
- 2026-10-31: **Menú de canal en el mixer**: el clic derecho en una tira de pista abre sus acciones, las mismas del
  encabezado del timeline más Color e ícono (`TrackMenuItems`). Doble clic en todo el título de pista, bus o carpeta
  para renombrar, también con la tira sin seleccionar (`RenameOnDoubleClick`).
- 2026-10-31: **Escala de los medidores legible con cualquier tema**: números en `Dim` (el 0 en `Fg`) y fondo de las
  barras en `Hover`, que además no se pierde sobre una tira seleccionada (`MixerMeter`).
- 2026-10-29: **Master y medidor fijos a la derecha**: en el mixer la tira del master queda fuera de lo que se
  desplaza (`masterPin`, `AlignPin`); en Mastering, el Medidor de la salida en vivo (`LiveMeter`: medidor L/R, LUFS,
  pico real, COR y MONO), con las lecturas que antes estaban en Volumen. Etapas con ícono, filo y borde de acento
  cuando están prendidas, títulos de caja con presencia, reducción desde la derecha y sin los textos que explicaban
  la cadena (`MainWindow.Master.cs`).
- 2026-10-29: **Versión 0.9.9 publicada**, la primera como WildTrack (instalador `WildTrack-Setup.exe` en el release
  `v0.9.9`).
- 2026-10-29: **La ventana recuerda dónde quedó**: posición, tamaño y si estaba maximizada, en `config.json` "window"
  (`MainWindow.WindowPlace.cs`); si el monitor ya no está, abre centrada.
- 2026-10-29: **Las carpetas siguen el zoom vertical**: su alto (abiertas o plegadas, con o sin alto propio) escala
  con el alto de pista como las pistas (al alejar, el de siempre no baja: si no, se ocultan los knobs de envío); `Folder.Height` pasa a ser px al alto por defecto (`Track.DefaultHeight`)
  (`TimelineView.FolderZoomed`, `SetRowHeight`).
- 2026-10-29: **Envíos a mano y más compactos**: una pista o un bus nuevos ya no traen envíos a todos los buses, se
  suman con "+ Envío" (`ProjectBuses.Shown`, `ShowSend`, comprobado en `wtlab`); en el inspector cada envío es una fila
  fija (`InspectorSendRow`) y tiene menú de clic derecho, también en el mixer (`ShowSendMenu`).
- 2026-10-29: **Menú de cada efecto con clic derecho**, en el mixer y en el inspector (`ShowSlotMenu`, el mismo del ⋯):
  abrir, prender o apagar, apagar los demás, subir, bajar, duplicar y eliminar.
- 2026-10-29: **VST3 más directos**: un clic en el renglón de un efecto VST3 cargado en el mixer abre su ventana, y
  donde un plugin ya está puesto (lista del mixer, inspector, instrumento) se muestra sin su fabricante.
- 2026-10-29: **Zoom del mixer y una sola tira**: en vez de Compacto / Amplio / Auto, un ancho de canal continuo
  (42 a 150 px) que se cambia con el gesto de zoom del timeline (Shift+rueda, alrededor del mouse) o la barra de la
  cabecera; la rueda sola y Ctrl+rueda desplazan el mixer a los costados (`ZoomMixer`, `OnMixerWheel`, `mixerStripW`).
  Después de probar tres estilos, el usuario eligió la lista: se sacaron las tiras amplias con sus paneles al costado
  (y su plegado, la fila de salida, los paneles de envío y el de Transponer). La tira se acomoda al angostarse
  (`StripHead` con el ícono del canal siempre a la vista, `NarrowParts`, `Fader.Slim`, `MixerMeter.Scale`, filas con
  ícono que suman ⏻, nombre y ajuste, `SlotBrief`, según su ancho). **FX** en la cabecera pone en bypass todos los
  efectos, solo en la escucha (`PlaybackEngine.FxBypass`, `ChannelDsp.Specs(..., bypass)`, comprobado en
  `wtlab comprobar-dsp`). Las carpetas siguen el zoom como las demás tiras y dejan 6 px de aire con el canal o separador de al lado (`FolderGap`).
- 2026-10-29: **Compacto / amplio igual para todos los canales del mixer**: los buses se arman con las mismas piezas
  que las pistas (`StripCardLook`, `CompactChain`, `WideControls`, `PanRow` genéricos; `BusStrip` en
  `MainWindow.Buses.cs`, fuera `BusRack`) y el mismo ancho en los dos modos; se seleccionan como una pista
  (`SelectBus`, panel del bus en el inspector) y tienen color e ícono (`Bus.Color`, `Bus.Icon`, `ProjectBuses.SetLook`,
  el selector compartido `ShowLook`, `TrackBadge.SetBus`; `wtlab proyecto buses … color|icono`). Doble clic en el nombre
  de una pista o un bus en el mixer lo renombra.
- 2026-10-29: **Pantalla completa con F11** (atajo `fullScreen`, configurable; `MainWindow.FullScreen.cs`): F11 o
  doble clic en la barra vuelven al estado de antes; la barra de título suelta el lugar de los botones del sistema.
- 2026-10-29: **Textos profesionales**: `es.json` y `en.json` revisados enteros (estados, avisos, tooltips y ayudas
  cortos, sin primera persona, humor ni explicaciones internas; "No se pudo…" en vez de "No pude…"), los respaldos de
  `Texts.T` en Audio y Projects igualados al json, y fuera las ayudas que no sumaban (`insp.transpose.hint`,
  `insp.comp.what`, `insp.clip.hint`, `vst.insp.hint`, `timeline.empty.b`, con su llamada en el código).
- 2026-10-29: **Efectos VST3** en los slots de pistas y buses, mezclados con los propios ("+ Efecto" → VST3; panel con
  "Abrir", estado guardado en el slot, el sonido pasa sin él mientras abre o si se cae): `VstInsert` (en vivo un bloque
  tarde sin esperar al plugin, en la exportación su propia copia), `MainWindow.VstFx.cs`, `InsertSlot.VstKind`. Con
  **compensación de latencia** en toda la mezcla, el click y las tomas (`LatencyDelay`, `MixLatency`,
  `PlaybackEngine.UpdateLatency`, recorte en `MixRenderer`); el host reinicia el plugin al arrancar y avisa si cambia su
  latencia. `wtlab vst efecto` compara el vivo con la exportación y `proyecto insertos … agregar vst --plugin`.
- 2026-10-29: Las carpetas muestran sus envíos a buses como perillas en el timeline y, como las de las pistas,
  arrancan a la izquierda debajo de la insignia en vez de repartirse el ancho; su
  alto de siempre es el mismo abierta o plegada; se siguen redimensionando (`TimelineView.cs`, `MainWindow.Timeline.cs`).
- 2026-10-29: Dos botones en la barra de título ocultan del todo el panel del canal y el del proyecto, y se recuerdan
  (`MainWindow.Sidebars.cs`).
- 2026-10-29: Carpetas más livianas en el timeline: la fila abierta baja de 64 a 34 px, su carril resume dónde hay clips
  de sus pistas (`DrawFolderSummary`), sin color van en gris en vez del acento y el recuadro va al 45 %, un píxel adentro
  para que el borde de la tarjeta no lo tape; la pista seleccionada lleva una píldora de acento visible y un tinte suave
  del acento (`DrawSelectedPill`, `SelectedTint`) (`TimelineView.cs`).
- 2026-10-29: Historial sirve para volver atrás: **Pasos** (los de deshacer de la sesión con nombre, clic para ir a
  cualquiera; `EditSteps`, `UndoManager.Steps`/`JumpTo`, `MainWindow.Steps.cs`) y **Versiones** (las de `historial/`
  con "Guardar versión" con nombre, una por exportación y Volver; `ProjectStore.SaveVersion`/`RenameVersion`,
  `historial/versiones.json`). Volver a una versión ya no se lleva las tomas nuevas ni las exportaciones. Se fue la
  sección Actividad. `wtlab proyecto verificar-pasos`, `guardar-version`, `nombrar-version`.
- 2026-10-29: Un canal nuevo arranca sin efectos (`Channel.Inserts`; las plantillas de fábrica siguen con Vida →
  Compresor → EQ). El inspector muestra el canal de una pista vacía (lo del clip va aparte, `clipSections`); las
  tarjetas de efectos y envíos arrancan plegadas, más bajas y con un resumen al lado del título
  (`SectionHeader.SetSummary`), y la ayuda del envío pasó al tooltip.
- 2026-10-29: Un proyecto nuevo vacío arranca sin buses, así las pistas no muestran envíos; las plantillas siguen
  trayendo los suyos (`ProjectLibrary.Create`).
- 2026-10-29: En el inspector, efectos y envíos tienen cada uno su área: una bandeja más oscura con título, cuántos
  hay y su "+", y sus secciones como tarjetas (`Controls/InspectorTray`); las bandejas se pliegan y la columna
  plegada separa los grupos.
- 2026-10-29: La insignia de una pista sin ícono muestra el de su tipo (onda, teclas MIDI o cámara si sale en el
  video) con el número chiquito abajo a la derecha (`TrackIcons.DrawBadge`, `TrackBadge`).
- 2026-10-29: La app pasa a llamarse **WildTrack** (ya es más un DAW que un estudio de cámara): proyectos y namespaces
  `WildTrack.*`, `WildTrack.exe`, `wtlab`, `wtvsthost`, `WildTrack.slnx`, instalador `WildTrack-Setup.exe` (mismo
  `AppId`: actualiza la instalación vieja y borra sus archivos y accesos). Los ajustes pasan a `%APPDATA%\WildTrack`
  (`WildTrack.Audio/AppFolder.cs` copia la primera vez lo que falte de `%APPDATA%\GuitarCam Studio`) y la carpeta de
  proyectos por defecto a `Documentos\WildTrack Proyectos` si no existía la vieja. El repo de GitHub pasa a
  `xtrimdr/WildTrack` (`UpdateCheck.Repo`, links de los README; GitHub redirige el nombre viejo, así la 0.9.8
  instalada sigue encontrando las versiones nuevas). Siguen igual `guitarcam.json` y las menciones a la web original. Ícono nuevo, renderizado en Blender
  (`tools/make-icon.ps1` + `tools/icon/`): un diafragma de seis hojas de acero cepillado alrededor del sensor, con
  un aro pulido y, desde 128 px, en cada hoja grabada apenas la parte más reconocible de un instrumento, en primer
  plano (pastillas de guitarra, teclas, rejilla de micrófono, campana de saxo, efes de violín, pistones de trompeta).
  Los dibujos son de Openclipart, dominio público (`tools/icon/instrumentos/ORIGEN.txt`).
- 2026-10-29: Sin FFmpeg, la app avisa al abrir con un cuadro (`Dialogs/FfmpegDialog`, también desde el chip
  "FFmpeg no encontrado") que lo instala con winget (`Gyan.FFmpeg`, `GuitarCam.Audio/FfmpegSetup.cs`), abre la página
  de descarga o lo busca de nuevo; relee el PATH del registro para no tener que reiniciar. `gclab sistema --instalar-ffmpeg`.
- 2026-10-29: El instalador se arma en GitHub Actions (`.github/workflows/instalador.yml`): baja lo que no va en git
  (`tools\get-ffmpeg.ps1`, nuevo; SDK de VST3 y Basic Pitch), corre `comprobar-dsp` y, con un tag `vX.Y.Z`, deja un
  release en borrador. Listo para firmar con SignPath Foundation en cuanto aprueben el proyecto; política de firma y de
  privacidad en los README. `get-vst3sdk.ps1` arreglado en carpetas limpias (rutas largas).
- 2026-10-28: **Extraer MIDI** de un clip de audio, con el clic derecho (Acordes / Melodía) o arrastrándolo a una
  pista MIDI: Basic Pitch de Spotify por ONNX Runtime en la CPU (`AudioToMidi`, igual nota por nota al código de
  Spotify) más una limpieza propia (un ataque marcado dos veces es uno, fuera los fantasmas bajo una cuerda grave,
  notas desde 70 ms para semicorcheas y chugs, y el modo Melodía de a una nota); las notas van a un clip MIDI del mismo
  tramo en una pista nueva debajo (`MidiEditing.AddExtracted`, `MainWindow.ToMidi.cs`). Modelo en `lib\basic-pitch`
  (`tools\get-basic-pitch.ps1`). En gclab: `extraer-midi archivo` (con `--midi` y `--verdad`) y sus filas en
  `comprobar-dsp` (`ToMidiChecks`, cuerdas pulsadas sintéticas: riff, trémolo y acordes F ≥ 0,93, chugs 0,94).
- 2026-10-28: **Detectar tempo** en el clic derecho de cualquier clip de audio: flujo espectral + autocorrelación con
  preferencia por 120 y un peine que afina a la décima y halla el primer golpe (`TempoDetector`); un menú ofrece poner el
  proyecto a ese tempo, al doble o a la mitad, y el toast trae "Volver a N" (`MainWindow.TempoDetect.cs`). En gclab:
  `tempo archivo` y sus filas en `comprobar-dsp` (`TempoChecks`).
- 2026-10-28: **Comparar mezclas A | B** en la barra del mixer: dos mezclas de las mismas tomas (canales, carpetas y
  buses, sin el master), ida y vuelta mientras suena sin perder el estado de los efectos, con deshacer, "Copiar A en B"
  y "Olvidar" (`MixCompare`, `MainWindow.MixCompare.cs`). En gclab: `proyecto mezcla … ab|copiar|olvidar` y
  `proyecto verificar-ab`.
- 2026-10-28: **Bloquear clips y pistas** (clic derecho): lo bloqueado no se mueve, no se recorta, no se divide ni se
  borra, y grabar no lo pisa (la toma va a una pista nueva); volumen, fades y encuadre siguen. Una sola regla,
  `ClipEditing.IsLocked`, para la ventana (`MainWindow.Locks.cs`, candado en el timeline) y gclab (`editar … bloquear`,
  `proyecto verificar-bloqueo`).
- 2026-10-28: **Exportar solo el loop A–B** ("Tramo" en el diálogo de exportar, MP4 y audio, stems incluidos): la
  mezcla corta las tomas en B y deja sonar la cola, arranca en A con un fundido corto, y el video lleva solo las tomas
  del tramo (`ExportRange`, `MixMaster.Render`, `MixRenderer` con `stopSec`). En gclab: `exportar … --rango 12-45|loop`
  y las filas "Rango:" de `comprobar-dsp`.
- 2026-10-28: **Escalar y humanizar velocidades**: "Ajustar" en la cabecera del piano roll (Escalar alrededor del
  promedio, Humanizar ± al azar; `MidiEditing.ScaleVelocity` / `HumanizeVelocity`). En gclab: `midi velocidad` y
  `midi humanizar`, y su comprobación en `midi comprobar`.
- 2026-10-28: **Piano roll: herramientas de velocidad y flechas**: la franja de velocidad con escala, botón para
  mostrarla, paso vertical (caja y menú), Lápiz / Línea y clic derecho a 100; Shift+flechas mueven las notas (←→ un
  paso del imán), Ctrl+↑↓ una octava (`PianoRollView`, `BuildPianoVelocityTools`).
- 2026-10-28: **Piano roll: copiar, pegar, duplicar y scrub**: clic derecho en vacío deselecciona, Shift+arrastrar
  copia las notas elegidas, Ctrl+C/X/V y D con las notas (siempre dentro del clip, con el imán) y la regla hace scrub
  en vivo como la del timeline (`PianoRollView`).
- 2026-10-28: **Copiar capítulos para YouTube** (clic derecho en la franja de marcadores): los marcadores como
  "0:00 Intro…" para la descripción del video, con aviso si YouTube no los tomaría (menos de 3, alguno de menos de
  10 s; `ProjectMarkers.Chapters`, `CopyChapters`). En gclab: `proyecto marcadores … capitulos`.
- 2026-10-28: **Usar plantilla en una pista que ya existe** (clic derecho en la pista): le pone el sonido de la
  plantilla (efectos, volumen, paneo y envíos a los buses que haya) sin tocar sus tomas, nombre, look ni ruteo
  (`TrackPresets.ApplyTo`, `ApplyPresetSubmenu`). En gclab: `proyecto editar … aplicar-plantilla pista nombre`.
- 2026-10-28: **Paquete ZIP del proyecto** en el gestor: "Para compartir" (el proyecto y las tomas que suenan) o
  "Respaldo completo" (también tomas sin usar, versiones y exportaciones); las tomas importadas sin copiar entran en
  `media/de-afuera/` y el JSON del paquete las apunta ahí. "Abrir paquete…" lo trae como proyecto nuevo, sin pisar
  ninguno y rechazando entradas que salgan de la carpeta (`ProjectPackage.cs`). En gclab: `proyectos empaquetar` /
  `desempaquetar` y `proyecto verificar-paquete`.
- 2026-10-28: **Puerta de ruido** como slot de efectos (pistas, carpetas y buses): calla el zumbido entre frases con
  Suave / Normal / Firme y una perilla de Umbral; abre en ~1 ms, cierra 6 dB por debajo después de 60 ms, así una nota
  que se apaga no aletea (`Dsp/NoiseGate.cs`, `GateInsert`, `GateSettings`, `GatePanel` / `GateSection`). Y botón
  **MONO** en la tira del master para escuchar la mezcla en mono, solo el monitoreo y con fundido de 10 ms
  (`PlaybackEngine.MonoCheck`), y **COR** bajo los LUFS: la correlación de la salida, en rojo bajo cero
  (`Dsp/CorrelationTap.cs`, `ShowMasterCorrelation`). En gclab: `insertos … agregar puerta`, `--cadena …,puerta` y filas en `comprobar-dsp`.
- 2026-10-28: Aviso de versión nueva: al abrir, la app compara su versión con la última release de GitHub
  (`UpdateCheck` en `GuitarCam.Projects`, `MainWindow.Updates.cs`); toast con "Descargar" la primera vez, campanita
  después, estado y casilla para apagarlo en "Acerca de". En gclab: `version`.
- 2026-10-28: Entradas MIDI, etapa 4 del editor MIDI (ideada con el usuario): Configuración → MIDI con cada teclado o
  controlador (lucecita de actividad, nombre propio, prendido, canal, curva de velocidad, Notas / Perillas / Transporte;
  `SettingsDialog.Midi.cs`, `MidiSetup`, `config.json` "midi"), el teclado suena en la pista MIDI elegida y en las
  armadas con su instrumento y su cadena, con pedal de sustain (`GuitarCam.Audio/Midi`, `PlaybackEngine.DispatchMidi`,
  `LiveNotes`), las pistas MIDI se arman y graban las notas en un clip nuevo (`MidiEditing.AddRecorded`), cada pista
  elige su entrada y canal (`Track.MidiInput`), y cuantizar con Q, el menú del clip o al grabar, al imán del piano roll
  (`MidiEditing.Quantize`). En gclab: `midi entradas`, `midi escuchar`, `midi cuantizar` y sus comprobaciones.
- 2026-10-28: Acento "Fuego" (#ff5809) en la paleta y los acentos ordenados por tono, de cálidos a fríos (pedido del
  usuario, `UiTheme.Accents`).
- 2026-10-28: "Acerca de" con el diseño del splash (pedido del usuario): logo grande, brillo y línea del acento del
  tema, versión en la esquina (`Dialogs/AboutDialog`, `BrandArt.cs`). El splash dura al menos 3 s.
- 2026-10-28: Splash al abrir (pedido del usuario): ícono, nombre, versión con commit y franja de carga animada, con
  los colores del tema, mientras se arma la ventana principal (`SplashWindow`, `App.axaml.cs`).
- 2026-10-28: Inspector de la pista MIDI (pedido del usuario): el instrumento se elige en un desplegable en vez de una
  lista a la vista, con los grupos Nativos y VST3 (submenú por marca desde 12) también en el timeline y el piano roll,
  y debajo la salida y los envíos a buses (`MainWindow.Vst.cs`, `PlaceInspectorSends`). La búsqueda de VST3 detecta
  sola si cada uno es instrumento o efecto por sus entradas de audio y de notas, no solo por su categoría
  (`gcvsthost --scan`, `VstPluginInfo.IsInstrument`; los ya encontrados se reabren una vez, `PluginCatalog.CurrentFormat`).

- 2026-10-28: El piano roll muestra la pista MIDI entera y sigue al timeline (pedido del usuario): clic en otra pista
  MIDI la abre entera en el clip bajo el cabezal, clic en un clip lo muestra, los clips de la pista se marcan en la
  regla y en la grilla (los otros, tenues, se eligen con un clic) y grilla e imán cuentan desde el timeline. Notas
  redondeadas con una rayita de velocidad (`PianoRollView`, `MainWindow.Midi.cs`: `FollowPiano`, `PianoClipFor`).

- 2026-10-28: Configuración → Plugins, para buscar y administrar los VST3 (pedido del usuario): carpetas propias además
  de las de Windows, buscar nuevos o revisar todos con avance y Cancelar, lista con filtro y búsqueda donde se ocultan
  los que no se usan, y los que no abrieron con "Probar de nuevo" (`SettingsDialog.Plugins.cs`, `PluginScan`,
  `PluginCatalog`). En gclab: `vst carpetas`, `vst ocultar|mostrar`, `vst escanear --archivo`.

- 2026-10-28: Instrumentos VST3 en las pistas MIDI (etapa 3 del editor MIDI, primero instrumentos por pedido del
  usuario): `gcvsthost.exe` (`src/GuitarCam.VstHost`, C++ con el SDK de Steinberg, MIT, en `lib\vst3sdk`) abre cada
  plugin en su propio proceso y habla con la app por memoria compartida (`VstBridge`); en vivo el audio nunca lo espera
  (un bloque de demora, compensado mandando las notas antes: `InstrumentPlayer`), la exportación lo espera bloque a
  bloque (`InstrumentRender` en `MixRenderer`, igual dos veces seguidas con MT-PowerDrumKit). Escaneo con caché
  (`VstScanner`, `PluginCatalog`), elección desde el clic derecho de la pista y el piano roll, su ventana, su estado
  guardado en el proyecto (`Track.Instrument`) y avisos si no abre o se cae (`MainWindow.Vst.cs`). `gclab vst …` y
  `midi instrumento`. Probados Surge XT y MT-PowerDrumKit; EZdrummer 3 todavía no suena.
- 2026-10-28: Piano roll: la franja de velocidad se agranda desde su barrita (`VelocityHeight`, `pianoVelHeight`) y
  arrastrar la barra de una nota elegida cambia la de todas las elegidas que empiezan ahí (`velGroup`), pedidos del usuario.
- 2026-10-28: Ctrl+C / Ctrl+X / Ctrl+V también con clips MIDI, solos o mezclados con tomas (pedido del usuario): el
  portapapeles lleva sus notas (`ClipClipboard.MidiItems`), los pega solo en pistas MIDI y, sin tomas, también en otro
  proyecto; copiar y cortar en su clic derecho. `gclab midi comprobar` lo verifica.
- 2026-10-28: Lo que el usuario anotó al probar la etapa 1 del editor MIDI: varios clips MIDI a la vez con Ctrl+clic
  (también en las tomas, al soltar sin mover) y con el rectángulo, mezclados con tomas, que se mueven, duplican y borran
  juntos en un paso (`TimelineView.Midi.cs`: `ToggleBoxed`, `StartMidiCarry`; `MidiEditing.DuplicateMany`, `RemoveAny`);
  "Piano roll" justo después de "Timeline" en el selector de vistas; y la grilla del piano roll, igual que el imán, Auto
  o fija, desde su cabecera o con clic derecho en su regla (`PianoRollView.GridMode`, `pianoGrid`).
- 2026-10-28: Editor MIDI, etapa 1: pistas MIDI ("+ Nueva pista" → Pista MIDI; `Track.Kind`, `MidiClips`,
  `MidiEditing` en `Midi.cs`) con clips que se crean con doble clic, se mueven, se alargan y pasan a otra pista MIDI en
  el timeline (`TimelineView.Midi.cs`), y el **piano roll** como cuarta vista en el lugar del mixer
  (`PianoRollView`, `MainWindow.Midi.cs`): escribir, mover, alargar y borrar notas al estilo FL, imán propio,
  velocidad abajo, teclado que suena, deshacer. Suenan con un tono de prueba sin estado (`NoteSynth`), igual en vivo
  (`LiveTrack`), en la exportación (`MixRenderer`, `MixTrackSpec.Notes`) y en gclab (`ChannelDsp.Notes`); las notas
  siguen al tempo y al Transponer. `gclab midi …` las arma y `midi comprobar` (también en `comprobar-dsp`) verifica el
  sonido. Arreglado de paso: deshacer no veía los cambios de notas.
- 2026-10-26: Marcas de duración por red en el timeline: el clic derecho en la regla prende una o varias (historias de
  1:00 que se repiten en cada corte, X 2:20, Reels · Shorts 3:00, TikTok 10:00); la línea punteada se pone ámbar con
  "no entra" si el tema o la toma que se graba la pasa; cada marca lleva el nombre de sus redes. En ese menú el clic
  derecho elige sin cerrarlo (`MenuChoice` / `MenuCheck`), igual que en los menús de entrada de la pista, plegar
  carpeta y ocultar separador. Los largos viven en `SocialLengths` (`GuitarCam.Audio`), se
  dibujan en `TimelineView.Social.cs`, se guardan en `config.json` ("socialMarks") y `gclab proyecto duracion` dice
  si entra en cada una.
- 2026-10-26: Arreglado el cierre al abrir por falta de memoria: con "Ver todo", un timeline apenas más ancho que los
  encabezados (mientras se acomoda la ventana) daba zoom 0 o negativo y la grilla crecía sin fin (`TimelineView.Render` /
  `BuildGrid`).
- 2026-10-26: Resplandor (glow) en Color → Efectos (`VideoFx.Glow`) y los looks **Soñado** y **HDR**. El resplandor
  toma lo que pasa de un umbral en cada canal, lo desenfoca ancho y lo suma en modo pantalla: en la preview
  (`VideoDetail.Processor`, ~1,6 ms por cuadro de 640×360, ~5 ms vertical, sin asignar memoria) y en la exportación
  (`lutrgb` + `boxblur` + `blend`) da lo mismo. Los dos looks son un LUT más y suman su parte espacial según la
  Intensidad (`VideoDetail.For`: Soñado, resplandor; HDR, claridad y algo de nitidez). `gclab nitidez --resplandor` /
  `--look` los compara.
- 2026-10-26: Nitidez y Claridad en Color → Efectos (`VideoFx.Sharpness` / `Clarity`, 0 = apagado). Se calculan sobre
  la luma, con el radio relativo al alto que muestra el panel, así la preview (`VideoDetail.Processor`, en
  `LiveClip.UpdateVideo`, sin asignar memoria y repartida en `BandPool`: menos de 1 ms por cuadro de 640×360, ~3 ms
  vertical) y la exportación (`boxblur` + `lut2` y `unsharp` sobre el panel recortado, `VideoDetail.ExportFilters`) dan lo
  mismo. `gclab nitidez` las compara y mide; `comprobar-dsp` revisa lo básico.
- 2026-10-26: Release 0.9.6 en GitHub. La versión vive en `Directory.Build.props`: la leen el Acerca de (antes decía
  1.0.0), el exe y `tools\make-installer.ps1`.
- 2026-10-26: Arreglado el mixer que quedaba sin responder después de arrastrar una tira hasta un clic en el timeline:
  se refrescaba durante la edición (deshabilitado) y nadie lo volvía a habilitar (`EditTimeline` lo refresca al terminar).
- 2026-10-26: El timeline muestra solo las perillas de los envíos de cada pista, sin fijas (`TimelineView.SendKnobs`);
  en el mixer, efectos y envíos son secciones de alto parejo en todas las tiras, estirables con una manija y con su
  botón en la cabecera (`ListBox`, `ApplyMixerHeights`, `SectionSwitch`).
- 2026-10-26: Mixer al estilo consola (pedido del usuario): cada pista elige a qué buses manda (`Send.Hidden`,
  `ProjectBuses.Shown`/`ShowSend`, "+ Envío", clic derecho en la pista, `gclab proyecto envios … --quitar|--sumar`) y
  el timeline muestra una perilla por envío en lugar de Reverb/Delay fijas; la tira compacta lleva la lista de efectos,
  la de envíos y el paneo en barras (`CompactChain`, `HBar`); "Envíos" en la cabecera del mixer los muestra u oculta,
  con un título nuevo; el fader tiene tapa de consola (`Fader`).
- 2026-10-26: Por ahora los buses salen solo al Master, sin salida ni envíos a otros buses (decisión del usuario:
  el circuito no se entendía; `ProjectBuses.CanFeed`, `Normalize`; el motor sigue pudiendo encadenarlos).
- 2026-10-26: Buses como rack (pedido del usuario): la cadena de efectos a la izquierda y fader y medidor fijos a la
  derecha (`BusRack`); cada slot se pliega a una fila con su resumen y se abre solo en una ventanita
  (`FxPanel`, `ShowSlotWindow`, `InsertSlot.Collapsed`, `gclab proyecto insertos … plegar n`).
- 2026-10-26: Mixer más prolijo (pedido del usuario): las tiras se arrastran deslizándose como los slots y las pistas
  del timeline, las demás haciéndole lugar (`MixerDraggable`, `MakeMixerRoom`); el clic derecho y un clic que
  deselecciona andan en el fondo vacío (`InitMixerMenu`); los separadores nacen ocultos en el timeline, el ojo de su
  tira volvió a verse (íconos perdidos en el mixer y en menús de carpeta/bus/separador) y su selector de color se
  cierra al elegir (`ShowFolderLook`).
- 2026-10-26: El inspector plegado muestra la cadena del canal seleccionado: un ícono por sección (Mezcla, slots,
  Reverb, Delay, envíos, Transponer), prendido en acento o apagado tenue, y un clic lleva a esa sección
  (`MainWindow.InspectorRail.cs`).
- 2026-10-26: Mixer ordenable (pedido del usuario): cada tira va a la izquierda o a la derecha (`MixerSide`) y se
  arrastra a otro lugar, carpeta o lado; el orden de pistas y carpetas es el del timeline, en los dos sentidos.
  Separadores con nombre (`Folder.Separator`), en el mixer y, si se quiere, en el timeline (`MainWindow.MixerOrder.cs`,
  `TrackFolders.AddSeparator`, `gclab proyecto editar … nuevo-separador | lado`).
- 2026-10-26: Buses unificados (pedido del usuario): bus y FX son lo mismo (`Bus`, `ProjectSettings.Buses`): volumen,
  paneo, mute, slots, salida y envíos; un proyecto nuevo trae "Reverb" y "Delay". Pistas, carpetas y buses eligen su
  salida y mandan a buses (`IRouted`, `ProjectBuses`, sin bucles); las carpetas solo agrupan (sin fader, M/S ni
  efectos). Motor: un solo nodo `BusDspChain` en orden para vivo y exportación (`ChannelDsp.Nodes`), sin Reverb/Delay
  fijos. Formato versión 3: los proyectos y plantillas viejos se convierten solos. Mixer (`MainWindow.Buses.cs`),
  inspector, timeline y `gclab proyecto buses|envios` al día; filas nuevas en `comprobar-dsp`.
- 2026-10-26: Medidores del mixer en estéreo: dos barras L/R con su RMS, su pico y su luz de clip en pistas, carpetas,
  retornos y master; una pista mono (tomas con L y R iguales, `MediaAudio.Mono`, o sin tomas con entrada mono) muestra
  una sola barra del mismo ancho (`MixerMeter.Set` / `Mono`, `RmsTap.MeanSqL` / `MeanSqR`, `TrackMono` en
  `MainWindow.Mixer.cs`; fila nueva en `gclab comprobar-dsp`).
- 2026-10-25: Mixer más completo. **Carpetas como bus**: con efectos, la carpeta suma sus pistas y las pasa por su
  cadena de slots (`Folder.Inserts`, `InsertRack`, `BusDspChain`, `MixFolderSpec`); sin efectos suena idéntica y el VCA
  sigue igual. **Medidores**: RMS con el pico, pico máximo en dBFS, luz de clip que queda prendida y LUFS de corto plazo
  en el master (`MixerMeter`, `RmsTap`). **Retornos propios** ("+ Retorno": otra reverb u otro delay con su cadena,
  `ProjectSettings.Returns`, `ReverbInsert`/`DelayInsert`) y **envíos pre/post fader** por pista (`Channel.ReverbPre`,
  `DelayPre`, `Sends`; `MainWindow.Returns.cs`). gclab: `proyecto insertos --carpeta|--retorno`, `proyecto retornos`,
  `proyecto envios` y las comprobaciones Carpetas / Medidor / Retornos de `comprobar-dsp`.
- 2026-10-25: Efectos en slots por canal (etapa 1 de los plugins). Cada efecto es un `ITrackInsert`
  (`GuitarCam.Audio/Dsp/TrackInserts.cs`) y `TrackDspChain` corre una lista que la UI reemplaza entera: reusa el efecto
  de cada slot por id y, si cambia la lista, funde 5 ms y 5 ms sin clic. El canal guarda `inserts` (proyecto versión 2);
  los `sat`/`comp`/`eq` de la versión 1 pasan a tres slots en ese orden y suenan idéntico (`Channel`, `InsertSlot` en
  `Model.cs`; `ChannelDsp`, `ChannelInserts`). En la tira amplia del mixer, un panel por slot con ⋯ (subir, bajar,
  duplicar, sacar), "+ Efecto", arrastre para reordenar (`Controls/SlotDrag.cs`) y las bandas de cada EQ en un
  flyout (`MainWindow.Mixer.cs`). La pestaña Canal también es la cadena: una sección por slot, con ⋯, manija para
  arrastrar y "+ Efecto" (`RebuildInspectorChain` en `MainWindow.Inspector.cs`). gclab: `proyecto insertos`,
  `mezclar --cadena` y las comprobaciones "Slots" de `comprobar-dsp` (`InsertCommands.cs`).
- 2026-10-25: Release 0.9.5 en GitHub con su instalador. `tools\make-installer.ps1` ahora le pone la versión al exe y
  publica en una carpeta limpia; el instalador lleva los DLL de FFmpeg de la preview (`ffmpeg\`).
- 2026-10-25: Transiciones entre clips de una pista (Corte / Fundido cruzado / Pasar por negro, 0,1 a 3 s, 0,5 por
  defecto) que funden la imagen y el sonido. Un chip blanco (`|`, `X`, `V`), centrado en cada unión del timeline, abre el
  menú (`MainWindow.Transitions.cs`, `TimelineView.DrawJoins`); también desde el clic derecho del clip. Se guarda en el
  clip que entra (`Clip.Transition`, `TransitionSec`); la cuenta es una sola para reproducción, preview y exportación
  (`GuitarCam.Audio/ClipTransitions.cs`; `LiveTrack.UpdateTransitions`, `PreviewView.DrawPicture`; el audio en
  `LiveTrack.AddClips` y `TrackDspChain.SampleClips`; en `Mp4Exporter`, `fade` con alfa o a color y `tpad` para
  congelar el borde sin video escondido). `gclab proyecto editar … transicion` y `gclab proyecto transiciones`.
- 2026-10-25: Preview sin cortes al pasar de un clip a otro: el video se decodifica con FFmpeg dentro del proceso
  (`GuitarCam.Audio/NativeVideoSource.cs`, FFmpeg.AutoGen 9 + DLL en `lib\ffmpeg`; un salto en una toma de 720p pasa
  de ~130 ms a ~20 ms, sin asignar memoria por cuadro, colores iguales a ffmpeg.exe), con ffmpeg.exe por pipe de
  respaldo (`Video/PipeVideoSource.cs`). Cada clip arranca su decodificador 1 s antes de empezar y muestra el primer
  cuadro en el mismo tick (`LiveClip.PreRollSec`, `VideoDecoder.Want`); un panel no muestra cuadros viejos
  (`LiveClip.Fresh`); los decodificadores sin usar se cierran a los 3 s (`VideoDecoder.Idle`). `gclab leer-video` lo mide.

- 2026-10-25: Clips: **Ctrl+C / Ctrl+X / Ctrl+V** (`ClipClipboard`, `MainWindow.Timeline.cs`, también en los menús de
  clic derecho) pegan en el cabezal desde la pista elegida, con la distancia entre pistas; **D** duplica varios clips
  marcados, en varias pistas, como un bloque (`ClipEditing.DuplicateMany`). `gclab proyecto editar … duplicar a,b` y
  `pegar`, con sus comprobaciones en `verificar-clips`.
- 2026-10-25: Masterización, Etapa 3: panel **Look** al principio de la cadena con seis looks (Natural, Pegadito,
  Brillante, Cálido, Al frente, Domar la púa, como los describió el usuario; `Dsp/MasterLooks.cs`) y su perilla
  Cuánto (de Natural al look entero, reescribe las perillas; tocar una a mano lo deja en "A mano"). Cuarta banda
  **Púa** (`MasterPickTamer`): un corte dinámico en 2–5 kHz (en − k·banda: en 0 pasa exacto) contra el nivel sostenido
  de la banda, que baja los ataques y deja el tono; con su perilla y su barra en el multibanda. **Sugerime**
  (`Dsp/MasterSuggest.cs`, `MainWindow.Suggest.cs`): escucha la mezcla antes del master en segundo plano (octavas
  contra el balance de una mezcla terminada y lo picuda que es cada banda) y muestra una tarjeta con lo que propone y
  Aplicar / Cancelar, como eligió el usuario. `gclab masterizar --look --cuanto-look --pua --sugerime`; tres
  comprobaciones nuevas en `comprobar-dsp`.
- 2026-10-25: Masterización, Etapa 1 completa: gráfico chico del multibanda en lugar de las dos perillas de cruce, con
  la curva de cada banda (Linkwitz-Riley) que baja con lo que comprime y los cruces que se arrastran (Shift fino, doble
  clic vuelve) (`Controls/MasterBandsGraph.cs`, sin asignar por cuadro).
- 2026-10-25: Masterización, Etapa 2: el **Destino** de LUFS pasa al master (Como está, YouTube, Instagram, TikTok,
  Spotify −14, Apple Music −16; `MasterLoudness`, `MasterSettings.Target`). Después de cada edición mide la mezcla en
  segundo plano con el mismo `MixMaster.Render` de la exportación y guarda la ganancia (`TargetGainDb`), que la
  reproducción aplica en lugar del Empuje (`MainWindow.Loudness.cs`); la exportación arranca de ella y cae en la misma,
  así que se escucha igual que sale. El diálogo de exportar ya no elige volumen (los presets solo cambian el cuadro; se
  fue la casilla de −14 de "Exportar audio"). Soft clip con **Auto**: de lo que el pico más fuerte pasa del Techo más
  allá de 2 dB, se lleva la mitad (hasta 4 dB) y el resto queda para el limitador; el ADAA va solo sobre lo que recorta,
  así que debajo del codo pasa exacto (antes filtraba los agudos) y su medidor ya no marca de más. `gclab masterizar
  --volumen`, `gclab exportar --volumen`; tres comprobaciones nuevas en `comprobar-dsp`.
- 2026-10-25: Scrollbars de toda la app fijos: de 8 px y sin flechas cuando hacen falta, ocultos si no, y al costado
  del contenido en vez de encima (`App.axaml`). Mastering llega bien al final de la cadena (margen en el contenido, no
  `Padding` del `ScrollViewer`), sin scroll vertical, y Salida va en su propia caja igual a la del master, con los
  paneles de las dos del mismo alto (`MainWindow.Master.cs`).
- 2026-10-25: Diseño de la ventana (etapas 1 y 2 de "Paneles reubicables"): botón en la barra de título con Clásico y
  Vertical (video de arriba a abajo a la izquierda o a la derecha, timeline al lado y mixer debajo, columna de ancho
  arrastrable), guardado en `config.json`, y un toast que lo ofrece al pasar a 9:16 (`MainWindow.Layout.cs`;
  `MoveBottomView` ahora sirve para solo audio y para Vertical).
- 2026-10-25: "Volver a una versión guardada" en la ventana: el chip del proyecto abre un menú con "Proyectos…" y las
  últimas 20 versiones de `historial/` (`MainWindow.Versions.cs`, `ProjectStore.Restore`); antes solo con gclab.
- 2026-10-25: Avisos que salen como toast, elegidos por el usuario: errores al guardar (proyecto y tomas), toma
  guardada, exportación terminada con "Mostrar" (`ToastHost` ahora acepta un botón), cámara que se corta
  (`TickCamera`) e interfaz de audio que deja de responder (`PlaybackEngine.Stalled`, `TickAudioDriver`).
- 2026-10-25: "Borrar y cerrar el hueco" sobre todas las pistas corre también los marcadores: los de después se
  corren el largo del rango y los de adentro quedan al inicio del rango, sin perderse (`ProjectMarkers.CloseGap`,
  desde `ClipEditing.DeleteRangeClosing`; vuelve con deshacer). En algunas pistas solas no se mueven, y el rango A–B
  tampoco. Comprobado en `gclab proyecto verificar-clips`.
- 2026-10-25: "Avanzado" en el diálogo de exportar MP4, plegado: resolución 720p / 1080p / 1440p / 4K y bitrate a
  mano (1–100 Mbps, o Automático = el de la calidad), recordados por proyecto (`ExportSettings.Resolution`,
  `BitrateMbps`; `Mp4Exporter.ExportSize`, `VideoEncoders.Args`). `gclab exportar --resolucion --bitrate`.
- 2026-10-25: Exportación más rápida (un MP4 de 95 s con 2 tomas: de 73 s a ~46 s; el audio solo, de 6,9 s a 4,3 s).
  El video se codifica con la placa de video si FFmpeg encuentra NVENC, AMF o Quick Sync (`VideoEncoders`, se elige
  en Configuración → Guardado y exportación, `gclab exportar --codificador`); cada toma se filtra en su propio grafo
  de FFmpeg en paralelo (`Mp4Exporter.BuildFfmpegCommand`, mismos cuadros que antes); la mezcla corre en tres etapas
  en paralelo (`MixRenderer.RenderTo`, bit a bit igual). Si la placa falla, repite sola con el procesador.
- 2026-10-25: Con barras de cine, el fundido de video de la preview dejaba ver el video a través de las barras
  (Avalonia aplica la opacidad a cada dibujo por separado): ahora las barras van fuera del fundido
  (`PreviewVideoFx.DrawLetterbox`, `PreviewView`). Barras de cine **Libre** con slider "Alto de las barras" (1–40 % del
  panel por barra, `VideoFx.LetterboxHeight`), igual en la preview y en el MP4 (`VideoColorProcessor.LetterboxBar`).
- 2026-10-25: Pasar a solo audio mientras se reproducía dejaba el timeline (y los medidores) muerto en los dos modos:
  los decodificadores se cerraban en otro hilo mientras el tick dibujaba esos clips, el tick tiraba un error y su
  timer no volvía a arrancar. Ahora se sueltan en la UI (`LiveClip.DetachVideo`, `MainWindow.Mode.cs`) y un error en el
  tick queda en `sesiones.log` sin frenar el timer (`SafeTick`).
- 2026-10-25: Biblioteca: cada canal sin usar tiene su papelera (confirmación en la fila) y al escucharlo aparece un
  mini reproductor bajo la fila con la onda (clic o arrastre para saltar), ±5 s, pausa y tiempo (`MainWindow.History.cs`,
  `Controls/TakeScrubber.cs`, `TakePreview`). Las dos barras laterales cambian de ancho arrastrando el borde y lo
  recuerdan (`MainWindow.Sidebars.cs`).
- 2026-10-25: El Ancho del mastering ya no sube el volumen al abrir (ni lo baja al cerrar): compensa la energía que
  cambia el ancho según cuánto centro y costados tiene la mezcla, medido en vivo (`StereoWidth`), así el ON/OFF compara
  solo el ancho. `gclab comprobar-dsp` lo mide (antes: hasta 3,9 dB de diferencia; ahora 0,1).
- 2026-10-25: En modo video la onda sobre las miniaturas toma el color de la pista (antes siempre blanca), y cada clip
  lleva arriba a la derecha un badge de solo audio / audio + video con tooltip (`TimelineView.MediaBadge`).
- 2026-10-25: "Abrir carpeta" del gestor entiende qué elegiste: el proyecto, una subcarpeta suya (media\, exports\…),
  la carpeta que tiene un solo proyecto adentro, o uno sin `guitarcam.json` pero con `.bak` o historial (lo rescata).
  Un proyecto de la web o una carpeta con varios lo dicen claro en vez de "no está vacía".
  `ProjectLibrary.Inspect`, `ProjectStore.IsRecoverable`, `gclab proyectos buscar`.

- 2026-10-25: **Herramientas del timeline**: Seleccionar, Rango, Dividir, Borrar, Escuchar, Zoom y Mano, en una caja
  centrada en la barra del proyecto (teclas 1 a 7; los zooms fijos que tenían 1 a 5 quedan sin tecla), cada una con
  su puntero. `MainWindow.Tools.cs`, `Controls/TimelineView.Tools.cs`, `Controls/ToolCursors.cs`, `LiveTrack.ListenSolo`.
- 2026-10-25: **Rangos de varias pistas y "Borrar y cerrar el hueco"** (Shift+Supr): lo de después se corre en esas
  pistas. `ClipEditing.DeleteRangeClosing`, `gclab proyecto editar borrar-rango pista,pista desde --hasta fin --cerrar`.
- 2026-10-25: **Traer al frente**: donde dos clips de una pista se pisan, el clic derecho elige cuál se ve (timeline,
  vista previa y exportación). `ClipEditing.BringToFront`, `gclab proyecto editar al-frente clip`.
- 2026-10-25: **Transponer más rápido y con feedback**: el `PitchShifter` analiza y sintetiza otros bloques mientras
  integra las fases (resultado idéntico, 3 min de 7,0 s a 4,2 s), los clips muestran "Transponiendo… %" con una barra,
  el knob Tono queda gris con el porcentaje y un toast avisa cuando está lista; los valores probados quedan en caché
  durante la sesión. `PitchShifter.Shift`, `TimelineView.DrawTransposing`, `MainWindow.Transpose.cs`.
- 2026-10-25: **knob de Transponer en el encabezado de pista y elegir qué knobs se ven**: Tono se suma a Vida · Comp ·
  Reverb · Delay (bipolar, −12…+12) y Configuración → Vista los prende y apaga (todos por defecto). `TrackKnobs`,
  `TimelineView.SetKnobs`, `SettingsDialog.BuildView`.
- 2026-10-25: **Transponer se ve en el canal y es un efecto más**: tarjeta propia en el inspector (después de Delay,
  atenuada en Original), panel − / + en la tira amplia del mixer, "+2" en la compacta y una etiqueta "♪ +2" junto al
  nombre en el encabezado de pista (gris mientras se calcula). `MainWindow.Transpose.cs`, `TimelineView.DrawHeader`.
- 2026-10-25: **avisa al cerrar con cambios sin guardar**: en los modos cada X minutos o a mano (o si un guardado
  falló) cerrar la ventana pregunta Guardar y cerrar / Cerrar sin guardar / Cancelar, y si no puede guardar no cierra.
  `Dialogs/UnsavedDialog`, `Closing` en `MainWindow.axaml.cs`.
- 2026-10-25: **la grilla manda, el borde corrige**: con imán y bordes a la vez, la cercanía al borde se mide desde
  donde la grilla pone el clip y no desde el mouse (con pasos de compás saltaba ~26 px fuera de la grilla, visto en
  un video del usuario). `ClipEditing.Drag`, caso en `gclab proyecto verificar-clips`.
- 2026-10-25: **imanes sin saltos y selector de imán a clips**: con los dos imanes gana el más cercano (el paso de
  la grilla o un borde a menos de 8 px); antes el borde ganaba siempre y al entrar o salir de su alcance el clip
  saltaba medio paso. Se pega a bordes visibles, y el botón pasó a ser un selector No / Inicio / Final / Ambos.
  `ClipEditing.Drag` (`SnapEdges`), `MainWindow.Timeline.cs`, `gclab proyecto editar --a-clips`.
- 2026-10-25: **guía del imán a otros clips**: mientras un clip está pegado al borde de otro, una línea de acento
  cruza las pistas en ese borde. `ClipEditing.Drag` (`stuckTo`), `TimelineView`.
- 2026-10-25: **la onda sigue al clip con el zoom al máximo**: la clave del caché de ondas era una sola suma y a
  96 000 px/s no veía los movimientos chicos (la onda quedaba vieja hasta hacer zoom); ahora cada valor va aparte
  (`TimelineView.WaveKey`).
- 2026-10-25: **grilla elegible e imán hasta 1/64**: el clic derecho en la regla elige la grilla (Auto, Igual que el
  imán, o una subdivisión fija que no cambia con el zoom) y el imán suma 1/32 y 1/64. `TimelineView.GridMode`,
  `MainWindow.Timeline.cs` (`ShowGridMenu`), `ClipEditing.GridSeconds`.
- 2026-10-25: **imán a la grilla e imán a otros clips, por separado**: "Sin imán" ya no pega al cabezal, al cero ni a
  los bordes (el imán de 8 px heredado de la web); los bordes de otros clips son un toggle propio al lado del imán,
  guardado en config.json. `ClipEditing.Drag` (`edges`), `MainWindow.Timeline.cs`, `gclab proyecto editar --a-clips`.
- 2026-10-25: **mover fino y alinear a la grilla**: los clips se mueven, recortan y fundan de a muestra (antes de a
  ms, que al zoom máximo eran 96 px, más grueso que la grilla aun con Alt) y el clic derecho suma "Alinear a la
  grilla" con la subdivisión del imán, para uno o todos los marcados ("Quitar" también los saca a todos).
  `ClipEditing.ToSample` / `AlignToGrid`, `MainWindow.Timeline.cs`, casos en `gclab proyecto verificar-clips`.
- 2026-10-25: **mover con grilla conserva el desfasaje**: un clip (o varios) que no está justo en la grilla se
  mueve de a pasos enteros sin saltar a la línea, así cae igual de corrido en otro compás; Alt sigue siendo libre.
  `ClipEditing.Drag`, caso nuevo en `gclab proyecto verificar-clips`.
- 2026-10-25: **rectángulo de selección de clips**: arrastrar en vacío sobre las pistas marca los clips que toca
  (Ctrl suma); moverlos los lleva juntos en el tiempo, cada uno en su pista, y Supr los borra a todos, en un solo paso
  para deshacer. `Controls/TimelineView.cs` (`boxed`, `StartCarry`), `MainWindow.Timeline.cs` (`ClipsMoved`).
- 2026-10-25: **el limitador, fuera de la caja del master**: la vista Mastering separa en una caja lo que prende el ON
  del master (Limpieza a Soft clip, que se atenúa entera con el master apagado) y deja afuera "Salida" (Limitador →
  Volumen), que trabaja siempre; el estado apagado dice "la mezcla va directo a la salida". `MainWindow.Master.cs`.
- 2026-10-25: **limitador y medidor en pico real**: el Techo pasa a dBTP con −1 por defecto (lo que piden las
  redes; el AAC del MP4 clipeaba con −0,3 por muestra). `TruePeak` sobremuestrea ×4 con el filtro de BS.1770 y el
  `MasterLimiter` lo usa como detector además de cada muestra; el panel Volumen muestra "Pico real" en dBTP (rojo si
  pasa del Techo), la exportación sin limitador frena en el pico real y `gclab masterizar` lo informa. Se guarda con
  otra clave (`limCeilingDbtp`): los proyectos de antes arrancan en −1. Comprobaciones en `comprobar-dsp`.

- 2026-10-25: **Transponer por pista**: slider en Mezcla del inspector (−12 a +12 semitonos, `Track.Transpose`) que
  cambia el tono sin cambiar el largo, así sigue en sincronía con el video. `PitchShifter` corre el espectro de cada
  cuadro (85 ms, FFT ×4) sin estirar ni remuestrear y rearma las fases desde los bins más fuertes (PVDR + phase
  locking), con estéreo coherente; se hace en segundo plano y queda en `cache/` (`TransposeCache`), mientras tanto
  suena el original y al llegar entra con un crossfade de 10 ms (`LiveClip.Sound`). Exportar y `gclab mezclar` /
  `exportar` usan el mismo caché; `gclab transponer` y `proyecto editar … transponer`, y comprobaciones en `comprobar-dsp`.
- 2026-10-25: **Cinta, Ancho, Soft clip y Volumen en el mastering**: tres etapas nuevas antes del limitador
  (`Saturator` en modo cinta, `StereoWidth` con graves mono, `SoftClipper` con ADAA, igual en vivo y al exportar) y un
  panel de LUFS / pico / "para −14" al final; la vista tiene el alto de la cadena y no se estira.
- 2026-10-25: **vista Mastering y multibanda sin medir**: tercer botón Timeline · Mixer · Mastering con la cadena
  en el lugar del mixer (en vez de la tarjeta flotante); cada banda fija su umbral siguiendo su propio nivel
  (`MasterBandComp`), así no hay "Midiendo la mezcla" (se sacó `MasterAnalysis`); el limitador tiene ON
  (`LimOn`, `MasterLimiter.Bypass`) y sin él la exportación con LUFS frena en el Techo.
- 2026-10-25: **tarjeta del master en cadena horizontal y limitador ajustable**: un panel por etapa (Limpieza →
  Multibanda → Color → Limitador) con perillas más grandes, ON propio para la EQ de color (`ColorOn`), y el limitador
  con Empuje, Techo y Release (`MasterSettings.Lim*`, `MasterLimiter.Set`, `MixGraphBus.SetMaster`), igual en vivo y
  al exportar (`MixMaster`). La medición ya no se cancela con cada edición. Sin Comparar.
- 2026-10-25: **master, etapa 1**: el botón "Master" del mixer abre una tarjeta con una EQ (Corte · Barro antes del
  multibanda; Graves · Presencia · Aire · Inclinación después), un compresor de 3 bandas Linkwitz-Riley con una
  perilla "Cuánto" y un medidor por banda y cruces ajustables (tuvo un Comparar a igual volumen; el usuario lo sacó
  porque no se entendía). Los umbrales salen de medir la mezcla en segundo plano y ajustar cada banda como el compresor de pista, con
  makeup que conserva el volumen; se guardan en el proyecto, así la reproducción y la exportación suenan igual.
  `Dsp/MasterChain.cs`, `Dsp/MasterAnalysis.cs`, `MasterSettings`, `MainWindow.Master.cs`; `gclab masterizar` y
  comprobaciones en `comprobar-dsp`.

- 2026-10-25: **rango A–B de reproducción**: franja arriba de la regla donde se arrastra el rango (bordes y cuerpo
  móviles), I / O lo ponen en el cabezal, L prende el loop, y un rango de pista (Shift+arrastrar) se puede repetir
  desde su menú. El loop vuelve a A con el crossfade de los saltos y sin reiniciar los efectos (antes volvía a 0 con
  un corte y reiniciaba la cadena). `ProjectSettings.LoopStartSec/LoopEndSec`, `PlaybackEngine.SetLoopRange`,
  `TimelineView.Markers.cs`, `MainWindow.Markers.cs`; `gclab proyecto repetir`.
- 2026-10-25: **marcadores con nombre**: banderitas en la misma franja que se arrastran, renombran en el lugar y
  saltan con un clic; K agrega uno en el cabezal, `,` / `.` van al anterior / siguiente, y el clic derecho ofrece
  nombres (Intro, Estrofa, Estribillo, Puente, Solo, Final) y "Repetir hasta el siguiente". Se deshacen como las
  pistas (`Project.Markers`, `ProjectMarkers`, `UndoManager`); `gclab proyecto marcadores`, `verificar-marcadores`.
- 2026-10-25: **exportar con presets para redes**: fila "Destino" en el diálogo de exportar (Proyecto por defecto,
  Reels · TikTok 9:16, YouTube 16:9, IG 1:1, IG 4:5 = 1080×1350), con la mezcla llevada a −14 LUFS; en "Exportar
  audio", una casilla de −14 LUFS (`ExportTargets`, `ExportDialog`, `ExportSettings.Target` / `AudioLufs`;
  `gclab exportar --destino --lufs`).
- 2026-10-25: **LUFS de la mezcla al terminar de exportar**: el resultado se mide con `LoudnessMeter.Measure` (el
  cálculo del medidor del master) y se ve en la pantalla final y en Historial (`ExportResult.Lufs`, `ExportRecord.Lufs`).
- 2026-10-25: **colas de efectos al exportar**: la mezcla sigue hasta 8 s después del último clip y corta cuando la
  reverb y el delay se apagan (−66 dBFS); el video congela el último cuadro y lo funde al fondo durante la cola
  (`Dsp/MixMaster.cs`, `Mp4Exporter.BuildFfmpegCommand`, `gclab exportar --cola`; comprobado en `comprobar-dsp`).
- 2026-10-25: **color de carpeta a sus pistas**: al elegirlo, el selector (y el inspector) pregunta si pinta también
  sus pistas con ese color (`MainWindow.TrackLook.FolderTracksAsk` / `PaintFolderTracks`, un paso de deshacer).
- 2026-10-25: **24 colores de pista en dos filas**, en orden de arco iris (`TrackIcons.Colors`, `ColorsPerRow`;
  `MainWindow.TrackLook.ColorChips`): se suman los tonos intermedios y se conservan los 12 de antes con el mismo hex.
- 2026-10-25: **knobs de efecto en los encabezados de pista**: Vida · Comp · Reverb · Delay pasan de cuatro filas
  de slider a una sola fila de knobs que crecen con el alto de la pista y el ancho de la columna (20 a 64 px) y se
  achican con ella (`TimelineView.FxKnobD`, `DrawFxKnob`); arrastre vertical, ruedita y doble clic para restablecer.
- 2026-10-25: **íconos de pista nuevos y más grandes**: rellenos, Fluent Emoji High Contrast de base y lo que le
  falta de Material Design Icons, game-icons y Qlementine (`tools/make-track-icons.py` los pasa a una grilla de 32,
  licencias en `Assets/Icons/LICENSES.txt`; los ids viejos de IconOven van a su par en `TrackIcons.Legacy`). En el
  timeline la insignia crece con la columna de encabezados de 28 a 52 (`TimelineView.BadgeSize`) y la columna llega
  a 640 (antes 460); en el mixer y el inspector pasó a 28 / 22.
- 2026-10-25: **Looks Fresco, Limpio, Moody y Crema** (los populares que faltaban, ver docs/filtros-capcut.md) y
  ajustes **Blancos, Negros y Sensación de luz** en Color → Ajustes, horneados en el mismo LUT que la vista previa y la
  exportación (`VideoColorProcessor.BuildLut`, `ColorGrade.Whites/Blacks/Light`, `gclab looks --blancos --negros --luz`).
- 2026-10-25: **5 looks nuevos y Brujo más fiel**: Solo rojo, Plata, Noche azul, Vivo y Hielo, que dan el mismo
  resultado que los filtros de CapCut que usa el usuario con un modelo propio (curva de tono, saturación y giro de
  tono por color, virado y curvas por canal) ajustado por mínimos cuadrados sobre cuadros reales (`FittedLooks.cs`).
  Brujo pasó a ese modelo. `gclab looks` los lista, comprueba el rango y escribe un look como `.cube`.
  Investigación en [docs/filtros-capcut.md](docs/filtros-capcut.md).
- 2026-10-25: **"+ Nueva pista" fija al pie** de la columna de encabezados, encima de los sliders de zoom
  (`TimelineView.AddRect`, `DrawAddButton`), en vez de ir debajo de la última pista.
- 2026-10-25: **varias pistas juntas**: Ctrl+clic / Shift+clic en los encabezados las marca, la manija las arrastra
  en bloque y el menú "Carpeta" las mueve todas (`TimelineView.Pick`, `TrackFolders.Move` con varias). **Quitar una
  carpeta** con pistas pregunta si van también (`MainWindow.RemoveFolder`, `gclab … quitar-carpeta --con-pistas`).
  Arreglos: el resize de una pista ya no hace saltar al resto al soltar, y el lugar donde cae una pista arrastrada se
  elige por el hueco más cercano con 8 px de "pegue" (`FindDrop`), así se puede sacar la última pista de una carpeta.
- 2026-10-25: **Barra superior en dos filas** para que entre con la ventana sin maximizar: arriba la barra de título
  (app, medidores, avisos, tema, ⚙); abajo modo, proyecto, guardar/deshacer y Exportar (`MainWindow.axaml`, `TitleBar`). Exportar, más prolijo: 32 de alto,
  ícono de descarga, hover más claro (`AccentHover`) y apagado sin volverse gris (`Button.accent` en `App.axaml`).
- 2026-10-25: **Ctrl+rueda al revés y "Invertir" por gesto**: Ctrl+rueda hacia arriba va a la derecha
  (`TimelineView.OnPointerWheelChanged`); la ruedita sola, zoom, costados y altura tienen cada uno su "Invertir" en
  Configuración → Atajos (`Shortcuts.WheelSign`, guardado en `config.json` "wheelReverse").
- 2026-10-24: **un solo camino para probar**: `tools\probar.ps1` compila, corre `gclab comprobar-dsp`, copia la app a
  `%LOCALAPPDATA%\GuitarCam Pruebas` y la abre desde ahí (el `bin\` nunca queda bloqueado); la copia lleva
  `prueba.txt` y la barra de título muestra "PRUEBA <commit>" (`TestBuild.cs`); el acceso directo del escritorio
  (`make-shortcut.ps1`) abre la última copia; si hay un GuitarCam abierto, un cuadro pregunta si lo cierra (guarda
  antes, `CloseMainWindow`) para que la copia nueva no abra el proyecto en solo lectura. Se trabaja en `master`, sin ramas ni worktrees (AGENTS.md §6).
- 2026-10-24: **hover de botones sin gris y con glow**: los botones encendidos que se pintan desde código (M/S, armar,
  ON, C/LIM, muestras de color, sonido del click) llevan la clase `lit` y conservan su color al pasar el mouse; los
  `:pressed` que faltaban (acento, primario, peligro, play encendido…) y los grises de Fluent van al token `Hover`;
  todo botón brilla suave con el acento (`HoverGlow`, un `DropShadowEffect` del botón que recolorea `UiTheme`) o en rojo
  los de peligro, y los botones de color (Exportar, primario, peligro) ya no cambian de color en hover (`App.axaml`).
- 2026-10-24: **modo de guardado elegible**: Configuración → Guardado (`SaveMode.cs`, `SettingsDialog.BuildSave`)
  elige entre guardar al editar, cada X minutos (1–30) o a mano; diskette en la barra de título (`SaveButton`) y estado
  "Sin guardar" (`TickTopBar`). Tomas, exportaciones y cerrar o cambiar de proyecto guardan siempre.
- 2026-10-24: **ruedita configurable**: Shift+rueda hace zoom y Ctrl+rueda va a los costados (antes al revés); los
  modificadores de zoom, costados y altura se eligen en Configuración → Atajos, girando la ruedita con ellos apretados
  (`Shortcuts.Wheel`, `config.json` "wheel"; tooltips del pie del timeline leídos de ahí).
- 2026-10-24: **carpeta plegada en el mixer compacto y barra espaciadora**: el nombre de la carpeta ya no ensancha su
  grupo (`Controls/NoWidth.cs`; nombre y cantidad se recortan juntos en `MainWindow.FolderGroup`); soltar una tecla de
  transporte no llega al botón enfocado (`OnTransportKeyUp`) y los M / S / ● del mixer no toman el foco.
- 2026-10-24: **alto propio por pista y carpeta, y zooms fijos**: `Track.HeightScale` (proporción del alto general: el slider las sigue escalando) / `Folder.Height` (arrastrando el borde de
  abajo del encabezado; doble clic en el borde o el botón ↺ del pie, `ResetHeightsButton`, lo igualan); el timeline mide
  cada fila con `LaneH` / `RowH` / `FolderRowH`; teclas 1 a 5 = un tiempo, 1, 4 y 16 compases y toda la canción
  (`TimelineView.ZoomPreset`, atajos `zoom1`…`zoom5`).
- 2026-10-24: **timeline sin tooltips y ruedita nueva**: se sacaron los tooltips del timeline (trababan el zoom y el
  scroll y no sumaban); queda el cursor (`TimelineView.UpdateCursor`, cursores armados una vez en `CursorOf`) y se
  borraron sus textos. Rueda sola: arriba y abajo; Ctrl: zoom; Shift: a los costados; Alt: altura.
- 2026-10-24: **carpetas más firmes y renombrar en el lugar**: la carpeta se selecciona (timeline y mixer) y el inspector
  la muestra con nombre, color, volumen y M / S (`InitFolderInspector`); su ícono abre el selector de color (`ShowFolderLook`,
  `ColorChips` compartido con el de pista); M, S y Supr actúan sobre ella; las pistas de una carpeta van con sangría
  (`FolderIndent`, 24 px) y la carpeta entera dentro de un recuadro fino de su color (`DrawFolderFrames`, sin riel); doble clic en el nombre de una pista o carpeta lo edita ahí mismo (`MainWindow.Rename.cs`).
- 2026-10-24: **más zoom en el timeline y barra de zoom**: zoom horizontal hasta 2 px por muestra (la onda dibuja las
  muestras, `TimelineView.SampleWave`; regla hasta 1 ms y tiempos partidos) y pistas de hasta 320 px; los sliders de zoom
  horizontal y de altura van al pie de la columna de encabezados (`TimelineFooter`); el volumen del encabezado tiene más
  presencia (barra gruesa, marca en 100, tapa de fader).
- 2026-10-24: **carpetas de pistas, plantillas de pista y menú de nueva pista**: `Folder` / `Track.FolderId` y
  `TrackFolders` (Projects: orden como secuencia, mover adentro / afuera, quitar, VCA con `Mix`); el timeline arma filas
  en píxeles (`TimelineView.Relayout`, `FindDrop`) con fila de carpeta, riel de color, plegado y el lugar donde cae la pista
  marcado; el mixer las muestra como grupos con un canal de carpeta (`MainWindow.Folders.cs`); el fader, M y S de la carpeta
  llegan al motor (`LiveTrack.FolderGain`…) y a la exportación. `TrackPresets` (`pistas.json`): clic derecho en una pista →
  Guardar como plantilla, y Nueva pista → Plantilla. Las ayudas del menú de nueva pista pasaron a tooltips con 1 s de demora.
  `gclab proyecto verificar-carpetas`.
- 2026-10-24: **color e ícono por pista**: `Track.Color` / `Track.Icon` (también en las plantillas) y una insignia
  (`TrackIcons.DrawBadge`, `Controls/TrackBadge`) en el encabezado del timeline, el inspector y el mixer; tocarla abre el
  selector (`MainWindow.TrackLook.cs`) con 12 colores y 53 íconos de instrumentos en 5 categorías: IconOven (MIT),
  generados con `tools/make-track-icons.py` en `Controls/TrackIcons.Data.cs`. Con color, los clips se tiñen.
- 2026-10-24: **toasts y `--probar`**: `Controls/ToastHost` muestra avisos abajo a la derecha, encima de todo (también del
  gestor y los diálogos); se van solos a los 6 s salvo con el puntero encima, y los fijos esperan su ×. `MainWindow.Toast`
  los manda también a la campanita. `GuitarCam.exe --probar "texto"` abre con un toast fijo "Para probar" (`App.ParseArgs`).
- 2026-10-24: **manijas en el timeline**: la columna de las pistas se ensancha solo desde su manija (una franja de 8 px adentro
  del borde, de todo el alto; antes compartía píxeles con el recorte de un clip en 0 s), y cada clip tiene manijas chicas en
  los bordes: mitad de arriba = fade, mitad de abajo = recorte, con el cursor que corresponde (`TimelineView.DrawEdgeHandles`,
  `HitHandle`, `OnGrip`).
- 2026-10-24: **parar con fundido**: Espacio y ■ paran con el mismo fundido de 10 ms (smoothstep) que play, los saltos y el
  scrub (`PlaybackEngine.FadeOutAndPause`, `StopTransport`); las pistas se apagan antes de los buses, así la reverb y el delay
  siguen sonando. `Stopping` distingue el fundido en curso (play en ese momento vuelve a subir donde está). `Stop`/`Pause`
  siguen cortando en el acto para grabar, exportar y cambiar de proyecto. El largo del fundido de play / stop / scrub se elige
  en Configuración → Entradas y salidas, "Fundido al arrancar y parar" (2–50 ms, 10 por defecto; `config.json`
  `audio.transportFadeMs`, `PlaybackEngine.TransportFadeMs`); el crossfade de los saltos queda fijo en 10 ms.
- 2026-10-24: **corte en el primer play de la sesión**: lo que solo corre sonando (mezcla de clips, saturación, compresor,
  EQ, LUFS, grabación) se compilaba (JIT) la primera vez adentro del callback y el bloque se pasaba del buffer (5,8 ms en uno
  de 4). `AudioWarmUp` precompila en segundo plano al arrancar todo `GuitarCam.Audio`, `GuitarCam.App.Audio` y
  `GuitarCam.App.Model`, y al terminar de abrir un proyecto hace una recolección completa con todo parado. Sin medir con la placa.
- 2026-10-24: **saltos de audio al mover el cabezal mientras suena**: `PlaybackEngine.Seek` escribía la posición desde la UI
  y el callback la pisaba al terminar el bloque (a veces se perdía el salto, pero la cadena se reiniciaba igual), y el corte era
  seco. Ahora, sonando, es un pedido que toma el hilo de audio: un bloque se apaga en la posición vieja y el siguiente se
  enciende en la nueva (`pendingSeek`, `SeekRamp`), sin reiniciar los efectos (el compresor reiniciado arrancaba sin
  reducción y el salto sonaba más fuerte); las colas de reverb y delay siguen. `AudioGapLog` (diagnóstico, provisorio) anota
  los huecos del callback en `%APPDATA%\GuitarCam Studioudio-cortes.log`: con saltos sonando no hubo ninguno.
  Arrastrar el cabezal sonando (regla y mini timeline del mixer) ya no hace "grrr": como en Studio Pro o Logic, el audio se
  pausa con fundido mientras arrastrás y sigue con fundido desde donde soltás (`FadeOutAndPause`, `ScrubChanged`); un clic
  suelto sigue siendo un salto. Play también arranca con fundido.
  Los fundidos duran 10 ms fijos con curva suave (`TransportFadeSec`, `TransportRamp`), no un bloque del buffer (4 ms con 192
  muestras hacía clic con material saturado). El salto sonando es un crossfade de igual potencia entre la posición vieja y la
  nueva, sumado en los clips antes de la cadena (`LiveTrack.AddClips`, `CrossfadeWeights`): bajar y después subir dejaba un
  "hipo" que se veía hasta en el cabezal, que ahora muestra el destino desde el clic (`PositionSec` lee el salto pendiente). Un clic en la regla
  pedía dos saltos (al apretar y al soltar) y el segundo tiraba el cabezal para atrás: ahora es uno solo (`scrubSought`).
- 2026-10-24: **la onda del clip con su ganancia** (ítem 1): `TimelineView.Waveform` multiplica cada columna por la
  ganancia del clip y su curva (lo mismo que suena), y el caché se rehace al cambiar la curva o la ganancia. Sin mirarla en la ventana.
- 2026-10-24: **afinador pulido**: notas en castellano desde `Loc` (`note.*`, en inglés C D E) en el panel y en la celda;
  triángulos a los costados que dicen para dónde girar la clavija y parpadean más rápido cuanto más desafinado
  (`TunerView`); "OK!" en verde dentro de ±3 cents (token `InTune` = verde de los medidores del mixer); slider
  "Sensibilidad" = compuerta de nivel de `PitchTap.GateDb` (por defecto −50 dB, igual que antes), en `config.json`
  (`tuner.gateDb`) y en `gclab afinar --umbral`; el panel se cierra con un clic afuera. `comprobar-dsp` suma un tono de
  −60 dB que no pasa con −50 y sí con −70. Sin probar con una guitarra ni mirando el panel en la ventana.
- 2026-10-24: **afinador y medidor de LUFS** (sugerencias): celda "Afinador" (A) en la barra de transporte con un panel
  de aguja (`TunerView`); YIN sobre la primera pista armada o la entrada 1-2 (`PitchTap`). Celda "LUFS" con el integrado
  del master de cada pasada, corto y momentáneo en el tooltip (`LoudnessMeter`, BS.1770). `gclab lufs`, `gclab afinar` y
  comprobaciones en `comprobar-dsp` (notas de B1 a A4 dentro de 0,3 cents; seno de −23 dBFS = −23,0 LUFS). Sin probar
  con una guitarra de verdad ni la celda LUFS mientras suena.
- 2026-10-24: **stems** (ítem 1): casilla "Además, cada pista en su WAV (stems)" en el diálogo de exportar (MP4 y audio,
  se recuerda en `settings.export.stems`): deja "<nombre> - stems" con un WAV float de 32 bits por pista que suena, cada
  una sola con sus efectos y envíos, sin el limitador del master y desde 0 (`AudioExporter.ExportStemsAsync`,
  `MixRenderer.Render(only:)`). `gclab exportar … --stems`. Probado con "Un beso y una flor": los 4 stems suman la mezcla
  (diferencia −39 dB, lo que hace el limitador). Sin ver el paso "Separando las pistas" del diálogo.
- 2026-10-24: **ganancia por clip y curva de volumen** (ítem 1): slider "Ganancia del clip" en el inspector
  (−24 a +12 dB) y `Clip.GainCurve` (`ClipGainCurve`): en el clip seleccionado, clic en su línea agrega un punto, se
  arrastra y doble clic lo saca; "Restaurar clip" saca las dos cosas. Reproducción y exportación la aplican igual (cada 64
  muestras con rampa). `gclab proyecto editar carpeta ganancia|curva` y 6 comprobaciones en `verificar-clips` (también se
  arreglaron dos que ya fallaban por el imán con grilla). Probado en la ventana: agregar y sacar puntos con el mouse;
  sin probar el arrastre de un punto.
- 2026-10-24: **grabar desde el cabezal** (ítem 1): la cuenta lleva hasta donde está el cabezal, la toma cae ahí
  (`RecordingJournal.StartSec`; el motor cuenta desde `recStartFrame` en `PlaybackEngine`) y al parar el cabezal vuelve
  a ese punto. En una pista con clips, desde el cabezal la toma reemplaza solo el tramo que cubre
  (`ClipEditing.DeleteRange` en `TakeRecording.AddTake`); desde 0, toda la pista, como antes. La barra roja del timeline
  crece desde el inicio de la toma. 2 comprobaciones nuevas en `verificar-tomas`. Sin probar con una interfaz ASIO:
  ni la cuenta, ni el click después de ella, ni el video de la toma desde el cabezal.
- 2026-10-24: **cabezal y medidores del timeline en una capa aparte** (`TimelineView.Layer`): mientras suena solo se
  redibuja esa capa, no cada clip, onda y etiqueta. El timeline entero se redibuja solo si se desplaza, si se graba
  (la barra roja crece con el cabezal) o si se arrastra un encabezado (los medidores vuelven al fondo para que la
  tarjeta levantada los tape). Con "Un beso y una flor" sonando: de ~4,2 a ~2 MB/s de basura y de 22 a 11 GC cada
  40 s; los gen1 de la reproducción ya no dejan huecos (queda uno, en el primer gen1 después de abrir y dar play).
- 2026-10-24: **cortes de audio por el recolector de memoria**: cambiar Compacto / Amplio en el mixer lo reconstruía
  entero y los GC frenaban el callback de ASIO 10–40 ms; ahora arma las dos vistas juntas y alterna (`MixerLayout`).
  Además la UI dejó de generar basura por cuadro: el timeline y el medidor de entrada no se redibujan si nada se movió,
  y `Palette` cachea textos (`TextLayout`), pinceles y plumas. Medido con "Un beso y una flor": de ~20 MB/s y ~48 GC
  cada 20 s a ~4 MB/s y ~10 (quieta, 1,7 MB/s); huecos de audio de 1,5 bloques o más, de 13 cada 20 s a 1 cada 40 s.
- 2026-10-24: **borrar un pedazo (rango)** (ítem 1): Shift+arrastrar sobre una pista marca un rango (con el imán, Alt
  = libre; se ve como una banda de acento con su largo) y Supr, o "Borrar el rango" del clic derecho, saca ese tramo
  de todos los clips de la pista: los que quedan adentro se van, los que sobresalen por un lado se recortan y los que
  sobresalen por los dos se parten en dos, con el fade in a la izquierda y el fade out a la derecha. Queda el hueco y
  nada se corre; es un paso de deshacer y no toca los archivos (`ClipEditing.DeleteRange`, `TimelineView.Range`,
  `MainWindow.DeleteRangeSelection`). Un clic, Esc o cualquier edición sacan la selección. `gclab proyecto editar
  carpeta borrar-rango pista desde --hasta fin` y 9 comprobaciones nuevas en `verificar-clips`. Probado con `gclab`
  (borrar y mezclar) y la ventana abre; sin ver el arrastre ni la banda con el mouse.

- 2026-10-24: **mover un clip a otra pista y a otro tiempo a la vez** (ítem 1, pedido del usuario): al arrastrar un
  clip sobre otra pista, el clip original queda atenuado donde estaba y un "fantasmita" (contorno punteado de acento
  con el nombre de la pista y el segundo de inicio) muestra dónde va a caer, con el imán de siempre (Alt = libre).
  Ctrl mantiene lo de la web: intercambiar con el clip de abajo, sin cambiar el tiempo (`TimelineView.DrawGhost`).
  Probado con `gclab` (mover y pasar de pista en el mismo proyecto; `verificar-clips` y `verificar-deshacer`
  siguen bien). Sin ver en la ventana: el dibujo del fantasma y el arrastre con el mouse.

- 2026-10-24: **importar audio externo** (ítem 1): "Importar audio…" en el menú del "+" de las pistas o soltar
  archivos WAV / MP3 / FLAC / OGG / M4A / AIFF en la ventana (`MainWindow.Import.cs`). Cada archivo se copia a
  `media/` en segundo plano (`GuitarCam.Projects/AudioImport.cs`, con nombre único y sin dejar medio archivo si
  falla) y queda en una pista nueva de solo audio, sin compresor ni saturación, en el cabezal (la pista vacía
  seleccionada se usa para el primero); es un paso de deshacer. `gclab proyecto importar-audio` y
  `proyecto verificar-importar`. De paso se arregló `AudioLoader`: con FLAC (y otros formatos de Media Foundation)
  el audio se cortaba porque el largo que informa el lector es solo una pista; ahora se lee hasta el final y la
  duración del medio se mide decodificando (`AudioLoader.Measure`). Probado con `gclab`: MP3, FLAC y M4A importados y
  mezclados (largos revisados con ffprobe), y la ventana abre el proyecto. Sin probar a mano: el diálogo de archivos
  y soltar archivos sobre la ventana.

- 2026-10-23: **R arma la pista seleccionada** (pedido del usuario): R ahora arma / desarma la pista seleccionada
  (acción "arm" en `Shortcuts.cs`) y grabar pasó a **Shift+R** o **Num \***; los tooltips del ● lo dicen.

- 2026-10-23: **atajos personalizables** (pedido del usuario): pestaña "Atajos de teclado" en Configuración, con una
  fila por acción agrupada en Transporte / Pistas / Edición / Vista; clic en la tecla y se toca la nueva (Esc cancela),
  si era de otra acción esa queda sin atajo y se avisa, ↺ por fila y "Restablecer todo". Se guardan en `config.json`
  ("shortcuts", solo lo que cambió). `OnKey` y las teclas de grabación buscan la acción en la tabla (`Shortcuts.cs`)
  y todos los tooltips leen la tecla de ahí. Atajos nuevos: **B** muestra / oculta el panel de la toma (izquierda) y
  **P** el del proyecto (derecha). Compila y arranca; la pestaña no la pude probar con el mouse en esta sesión.

- 2026-10-23: barras de estado más prolijas (pedido del usuario): botón **Monitor** al lado del medidor de entrada
  (prende el monitoreo por software, el mismo de Configuración); el formato ("48 kHz · 192 muestras") chiquito debajo
  del medidor; la carga DSP arriba con barrita, junto a CPU / GPU / RAM, que ahora se leen "CPU 19% ▬" (antes el
  número quedaba pegado a la etiqueta siguiente) con líneas finas entre los grupos (`MainWindow.axaml`,
  `MainWindow.TopBar.cs`, `Controls/SystemMeter.cs`). El medidor de entrada tiene el ancho de ese texto y una escala
  en dB debajo (`StereoMeter.ShowScale`). Visto en la ventana; el botón Monitor no lo escuché.

- 2026-10-23: **Quitar todos los mute / solo** (pedido del usuario, como Cubase): una M y una S al final de la barra
  de arriba del timeline (la columna de encabezados pasa a medir 324 como mínimo para que entren) y en la barra del
  mixer; se prenden si alguna pista tiene mute o solo y un clic los saca todos en un solo paso de deshacer
  (`MainWindow.ClearAllToggles`). Las plantillas de paneo del mixer pasaron a un desplegable "Paneo" con la apertura
  adentro (30 % por defecto). Visto en la ventana con la copia de prueba de "Camino a San Francisco".

- 2026-10-23: **Video | Solo audio** (ítem 1, pedido del usuario): interruptor en la barra de título y en "Nuevo
  proyecto" (`ProjectSettings.Mode`, `MainWindow.Mode.cs`, `gclab proyecto modo`). En solo audio el timeline ocupa el
  lugar de la composición, la cámara queda libre, se ocultan Color, Encuadre y lo de video del inspector, y
  "Exportar audio" saca la mezcla en WAV de 24 bits o MP3 de 320 kbps (`AudioExporter`, `gclab exportar --audio`).
  Se cambia en cualquier momento, es un paso de deshacer y no borra nada. Además, "En el video" por pista
  (inspector y clic derecho) para una pista que suena sin panel. Probado en la ventana con una copia de PruebasNEW:
  pasar a solo audio y volver, exportar un MP3 y abrirlo desde Historial, Ctrl+Z del cambio; y con `gclab` (WAV y
  MP3 revisados con ffprobe, `verificar-deshacer`). Sin probar: grabar una toma en solo audio y el formulario de
  "Nuevo proyecto" en la ventana. No quedó en el mixer el interruptor "En el video" (no entra en las tiras
  angostas): está en el inspector y en el clic derecho del timeline.

- 2026-10-23: dividir y duplicar clips (ítem 1, primer tramo): Alt+X divide el clip en el cabezal y D lo duplica
  justo después, también desde el clic derecho (`ClipEditing.Split` / `Duplicate`, `gclab proyecto editar …
  dividir|duplicar`, comprobados en `verificar-clips`). La exportación pone todos los clips de una pista en su
  panel, cada uno en su tramo (`ExportTakeSpec.Panel` en `Mp4Exporter`); probado con `gclab exportar` sobre una
  copia de PruebasNEW con clips divididos, movidos y duplicados (cuadros revisados a mano).
- 2026-10-23: **grabar varias pistas a la vez** (pedido del usuario): se arman varias pistas y R graba una toma
  por pista desde el mismo sample, cada una con su entrada (chip "1-2 ▾" en el encabezado armado y en la tira del
  mixer; `Track.Input`, `InputRoute`/`InputTap` en `GuitarCam.Audio`, el motor abre todas las entradas). Un journal
  por toma, así un corte las recupera todas; el video va a la primera armada que sale en el video. El ● ahora se
  puede desarmar también cuando lo eligió la app, y las pistas vacías tienen volumen, paneo, M, S y medidor.
  Probado: `gclab grabar --simular --entradas 1,2,3-4,1-2,4` (cada WAV comparado muestra a muestra),
  `proyecto verificar-tomas`, y la ventana con un proyecto de tres pistas (chip de entrada visible). Sin probar:
  grabar de verdad con la placa, el menú de entradas y armar varias en la ventana, y el monitoreo de varias entradas.
- 2026-10-23: armar pistas para grabar (pedido del usuario): ● en cada encabezado del timeline y en cada tira
  del mixer; la pista donde cae la toma se tiñe de rojo; armada con una toma, la nueva la reemplaza (la vieja
  queda en Historial) y la pista no suena mientras grabás. El botón Grabar del transporte quedó como un ● chico.
  Probado en la ventana: armar, ver el tinte en timeline y mixer, y grabar unos segundos sobre una pista con
  dos clips. Sin probar: grabar tocando de verdad (la toma salió muda).

- 2026-10-23: editar el timeline ya no recarga el proyecto: mover, recortar, fades, cambiar de pista, quitar,
  reordenar, deshacer y sumar una toma grabada se aplican en el lugar (`ApplyTracks` en `MainWindow.Timeline.cs`):
  pistas y clips se emparejan por id y siguen con su audio, su video, el estado de los efectos y el cabezal,
  sin parar la reproducción. Solo se lee del disco la toma nueva. Antes cada gesto paraba el audio, reabría
  todos los videos y el clip soltado volvía un instante a su lugar viejo.
- 2026-10-23: las tomas grababan con cortes en el lado derecho: en estéreo, el "mono automático" (la entrada 1
  a los dos lados si la 2 está muda) se decidía cada bloque y el soplido de la entrada 2 de la Focusrite
  (~0,0001) caía justo en el umbral de la web (0,00005), así que el lado derecho se prendía y apagaba ~60
  veces por segundo. Ahora la decisión queda trabada hasta que la entrada 2 trae señal de verdad y la 2
  cuenta como muda hasta -66 dBFS (`InputProcessor`, `RecordingSession.cs`). La web tiene el mismo problema.
  Se prueba con `gclab grabar --simular --canal estereo --ruido-2 0.00005`.
- 2026-10-23: la app se llama **GuitarCam Studio Native**: título de la ventana, badge "NATIVE" en la barra
  de título (`MainWindow.axaml`), nombre y descripción del exe (`GuitarCam.App.csproj`), acceso directo del
  escritorio (`tools/make-shortcut.ps1`, borra el viejo "(nativa)") y docs. El logo de la barra de título
  es el ícono de la app (`Assets/GuitarCam-64.png`, de `tools/make-icon.ps1`) y abre "Acerca de"
  (`Dialogs/AboutDialog`: versión con el commit, en qué está hecha y la carpeta de ajustes).
- 2026-10-23: lo último que faltaba de MIGRACION (§1.4 y §11): la cuenta grande 1-2-3-4 sobre el escenario
  (`PreviewView.CountdownBeat`, como el overlay de `SplitScreenCanvas`), "Mostrar en el Explorador" al frente
  (`Explorer.cs`, port de `openInExplorer` + `ForegroundWindow.cs`, en el gestor, el Historial y la
  exportación) y `tools/make-shortcut.ps1` para el acceso directo. El selector de carpetas de Avalonia ya era el
  moderno de Windows. Probado: la cuenta en la ventana (se ve y se va al cancelar, sin dejar archivos) y el
  Explorador (el usuario: abría, pero tardaba). Ahora le pide la carpeta directo al shell (ShellExecute /
  `SHOpenFolderAndSelectItems`, ~0,3 s contra 0,8–1,5 s de arrancar explorer.exe) en segundo plano. Sin probar
  en la ventana esa última versión (la app estaba abierta; compilada aparte).

- 2026-10-23: miniaturas del video en los carriles (§3 y §11 de MIGRACION): `Video/VideoThumbnails.cs`, port de
  `thumbnails.ts` (16 cuadros de 160 px por toma, en memoria, FFmpeg en segundo plano de a una toma) y la tira de
  `Filmstrip` en `TimelineView` (celdas 16:9 a la altura del carril, cover, espejo del clip, la onda blanca abajo
  sobre el degradé negro), desde 52 px de alto. Comparado con la web (TESTINGS). Revisado lo que habían dejado los
  agentes: A5 (el tempo con el mouse: arrastre del entero y del decimal, rueda y doble clic para escribir, probado
  en la ventana) y B6 (el diálogo de exportación lado a lado con la web: igual salvo la calidad, que ahora usa el
  `Segmented` gris de la web en vez de una pastilla de acento). Falta escuchar el menú del click y la calibración
  con cable o tocando (necesitan el cable o la guitarra).

- 2026-10-23: Configuración → Entradas y salidas (§7 de MIGRACION): "Buffer" con el tamaño actual y el botón
  "Panel del driver" (`PlaybackEngine.ShowDriverPanel`); cuando el driver pide reiniciarse (nuevo buffer) el
  audio se reabre solo, esperando si está grabando o calibrando (`AsioDuplex.ResetRequested`). Papelera (§11): lo
  que se borraba ya iba a la Papelera; ahora también la toma de "Parar sin guardar" (WAV y video), con un aviso en
  la barra de estado. F2 en el gestor ya estaba. Probado: abrir y cerrar el panel de la Scarlett y descartar una
  toma en la copia de prueba. Sin probar: cambiar el buffer desde el panel (no toqué la configuración de la interfaz).

- 2026-10-23: timeline (§3 y §1.7 de MIGRACION): la esquina de la regla quedó como la de la web (modo de regla
  en un recuadro con el activo resaltado, imán con su ícono, "Ver todo" deshabilitado si ya está ajustado,
  altura de pistas de a 2 px con el slider chico de la web) y la altura de la timeline se recuerda
  (`timelineHeight`; mínimo 110, doble clic en el separador vuelve a la de por defecto). La regla en minutos,
  el reloj que la alterna, el botón del medio y deseleccionar con un clic en blanco ya estaban. De paso:
  abrir un proyecto ya no deja un paso de deshacer vacío "track:…:name" (`InitInspector`: Avalonia avisa el
  `TextChanged` después, fuera de `syncing`). Probado en la ventana: arrastrar y doble clic en el separador,
  cerrar y abrir.

- 2026-10-23: pestaña Historial (§2.3 de MIGRACION): `MainWindow.History.cs`, port de `HistoryPanel.tsx`, con la
  lógica en `GuitarCam.Projects/ProjectHistory.cs` (`gclab proyecto historial` y `verificar-historial`). Tomas con
  filtro, buscador y días, escuchar el WAV crudo (`GuitarCam.Audio/TakePreview.cs`, salida de Windows), quitar /
  usar, "Borrar no usadas" a la Papelera (limpia el deshacer, como la web); Exportaciones (ver, mostrar, Papelera
  con confirmación en la fila; ahora guardan sus tomas); Actividad en frases por día con "Ver más". Las tomas que
  salen o vuelven al timeline se registran al guardar, como el `saveState` de la web. Es la pestaña por defecto.
  Probado en la ventana con una copia de "Camino a San Francisco": quitar, usar, escuchar, borrar no usadas,
  exportar, mandar el MP4 a la Papelera y el buscador; comparado con la web (TESTINGS). Sin probar: escuchar con
  los parlantes (solo vi el cambio de ícono al empezar y al terminar).

- 2026-10-23: más colores en el selector de tema (`UiTheme.cs`): 8 acentos nuevos (Naranja, Oro,
  Esmeralda, Menta, Celeste, Índigo, Lavanda y Fucsia) y 3 fondos oscuros (Océano, Café y Humo).
  El catálogo queda en 16 acentos y 9 fondos, con el mismo selector y tema predeterminado.

- 2026-10-22: barra superior (§1.1 de MIGRACION): `MainWindow.TopBar.cs`. Indicador "Guardando / Guardado /
  Error al guardar" en el chip del proyecto; medidor CPU / GPU / RAM (`GuitarCam.Audio/SystemStats.cs`, contadores
  de Windows por PDH; `gclab sistema`); resolución de la cámara, "48 kHz" y FFmpeg con su punto verde o "FFmpeg no
  encontrado"; selector de tema (`UiTheme.cs`, `Controls/ThemePicker.cs`) con los acentos, "Color a elección" y
  los seis fondos de la web, en vivo y recordado. Los tokens de color ahora son pinceles que cambian con el tema
  (`Palette` y App.axaml). Clic en el medidor de entrada abre Configuración → Entradas. De paso: los diálogos
  (Proyectos, Exportar, Configuración) no tapaban la barra derecha. Probado en la ventana con la Focusrite: el
  indicador, FFmpeg, abrir el tema, Madera + Violeta en vivo y recordado. Sin probar: "Color a elección"
  (diálogo de Windows) y el medidor del sistema en la ventana (necesita 1600 px; con `gclab sistema` sí).
  Lo que se arma una sola vez (el número de toma del inspector) conserva el acento anterior hasta reabrir.

- 2026-10-22: lo que faltaba del inspector (§1.2 de MIGRACION): el panel "Toma" se pliega a una columna
  de íconos ("Ocultar panel" / "Mostrar la toma") y se recuerda (`sidebarCollapsed`); estados vacíos
  como la web ("Seleccioná una toma" + "Todavía no hay tomas grabadas." / "Hacé click en una pista o en su
  panel del video.", y "Pista vacía" con el número gris, el texto de grabar y "Quitar pista" con ícono);
  el ícono de "Mostrar / Colapsar todos los efectos" cambia según el estado; y el **espectro en vivo** del
  EQ (`GuitarCam.Audio/Dsp/SpectrumTap.cs`: port del AnalyserNode de la web, FFT de 8192 con Blackman y
  suavizado 0,75; el hilo de audio solo copia a un anillo preasignado y la FFT corre en el `Tick`; el
  gráfico dibuja igual que `EqGraph` de la web). `gclab espectro` lo comprueba contra la fórmula del
  AnalyserNode (bien a 0,05 dB). Probado en la ventana: plegar y desplegar, proyecto sin tomas, pista
  vacía y el espectro de una toma sonando. Las plantillas de paneo y el canal de la pista vacía no van en
  el inspector: en la web están en el mixer, que ya los tiene.
- 2026-10-22: preview (§1.4 de MIGRACION): elegir el formato desde la ventana, arriba del escenario como en
  la web ("Lado a lado" / "Apilado" y 16:9 · 9:16 · 1:1, `MainWindow.Preview.cs`); 9:16 pasa a apilado y
  16:9 a lado a lado, cada cambio es un paso de deshacer y se guarda solo (`ProjectSettings.SetAspect` /
  `SetLayout`, también `gclab proyecto formato`). En `PreviewView`: borde de acento en el panel de la pista
  elegida, la separación entre paneles de la exportación (4 px de salida), los textos de estado de la web
  ("La composición aparece…", "Mientras grabás…", "Cargando video…") y los fades de video solo mientras
  suena o graba (como la web). La preview ya no le da panel a una pista cuyo primer clip no tiene video,
  igual que la exportación. Arrastre, rueda y clic ya estaban. Probado en la ventana: los dos selectores,
  9:16 → apilado, 1:1 y deshacer; comparado con un MP4 de `gclab exportar` (misma grilla y encuadre). Sin
  probar a mano: el arrastre y la rueda en un panel y ver el fade mientras reproduce.

- 2026-10-22: mixer (§4 y §1.7 de MIGRACION): `MainWindow.Mixer.cs`, port de `Mixer.tsx` y
  `MiniTimeline.tsx`. Se alterna con el timeline (X o el botón de abajo) y tiene su propia altura
  (recordada). Tiras amplias (paneo y un panel por efecto: Sat, Comp con LIM y "Comprime", EQ con el
  gráfico chico y su plantilla, envíos de reverb y delay) o compactas (Pan / Comp / Rev / Delay, M S C),
  "Auto / Compacto / Amplio" recordado; pistas vacías con "vacía". Retornos de reverb y delay con sus
  presets, nota o "Libre (ms)", estéreo / ping-pong, mute y fader; master con la luz LIM. Plantillas de
  paneo con "Apertura". Controles nuevos: `Knob`, `Fader`, `MixerMeter`, `MiniTimeline`, `SelectButton`;
  el limitador del master ahora informa su reducción. Todo con deshacer (mismas claves que el inspector) y
  en vivo. Probado en la ventana: cambiar de vista, los tres anchos, paneo con la rueda, "Alternado L/R"
  y deshacer, reproducir con la mini timeline. Sin probar: los medidores con una toma con sonido (el
  proyecto de prueba tiene tomas en silencio), los presets de los buses de oído, y la captura lado a lado
  con la web.

- 2026-10-22: barra derecha y pestaña Color (§1.3 y §6 de MIGRACION; A6): `MainWindow.Color.cs`, port de
  `ColorPanel.tsx` con Plantillas (guardar actual, aplicar, borrar; mismo nombre = actualizar), Filtro con
  las muestras de cada look e Intensidad, Ajustes con Restablecer, Efectos (viñeta y tamaño, grano, barras
  de cine) y los ON/OFF "Ver sin color" / "Ver sin efectos". Cada cambio es un paso de deshacer (misma
  clave que la web), se guarda solo y se ve en vivo en la preview. La barra se pliega a una columna de
  íconos y recuerda pestaña y secciones. Plantillas en `VideoTemplates.cs` (`plantillas-video.json`),
  `gclab proyectos plantillas-video | guardar-plantilla-video | aplicar-plantilla-video |
  borrar-plantilla-video`. `ColorGrade` y `VideoFx` ahora se normalizan al abrir (mismos rangos que la
  web). Probado en la ventana: elegir un look, deshacer, guardar y borrar una plantilla, plegar y
  desplegar. La pestaña Historial está pero vacía (§2.3). Falta la captura lado a lado con la web.

- 2026-10-22: cámara (§11 de MIGRACION) y monitor "En vivo" (§1.5): `CameraCapture.cs` (DirectShow por
  FFmpeg, la elección de cámara de la web, resolución con sus alternativas, DroidCam por IP) y
  `VideoTakeWriter.cs` (H.264 8 Mbps, keyframe cada 1 s, 30 fps contados desde la primera muestra de
  audio, MP4 fragmentado mientras graba y con el índice adelante al terminar). La toma guarda
  `media/{id}.mp4` con la latencia de cámara y el espejo; el diario lo recupera. Pestaña Cámara y
  "Latencia de cámara" de Configuración prendidas (`Video/LiveCamera.cs`), monitor en
  `Controls/LiveMonitor.cs`. `gclab camara listar|grabar|verificar-corte`, `proyecto toma|cortar --camara`.
  Probado con la Smart Connect Camera (1280×720) sin el celular conectado (imagen de espera): toma de
  3,4 s con video en la ventana, cuenta cancelada con Esc sin archivos, y los cortes. Sin probar:
  DroidCam por IP (no hay celular en la red) y una cámara que ofrezca 1080p.

- 2026-10-22: Configuración (§7 de MIGRACION): `Dialogs/SettingsDialog.axaml(.cs)` desde el ⚙, con
  Entradas y salidas (driver ASIO, medidor, canales, ganancia, par de salida, monitoreo), Sincronización
  (latencia calibrada/estimada con sus avisos, calibrar con cable o tocando, "Usar la estimación",
  ajuste fino que usa la grabación) y Cámara con la forma de la web pero apagada. El driver y la salida se
  recuerdan. El motor ahora aplica la calibración guardada aunque esté vieja (avisa) y vuelve a la
  estimación en un par sin medir. Probado abrirlo con la Focusrite; la calibración desde el diálogo
  todavía no se probó con cable ni tocando.

- 2026-10-22: grabar desde la ventana (§5.8 de MIGRACION): R / botón Grabar, cuenta con "Cancelar n/4"
  (Esc), Detener (Espacio / R) y Descartar, reloj y cabezal rojos, "Grabando…" en la pista destino o en
  una fila nueva. La toma va a la pista vacía como en la web (`GuitarCam.Projects/TakeRecording.cs`), es
  un paso de deshacer, recalcula el compresor con su nivel y se guarda antes de borrar el diario; cerrar
  la ventana grabando la guarda (`MainWindow.Recording.cs`). `gclab proyecto toma` y
  `proyecto verificar-tomas`. Probado con la Focusrite: dos tomas guardadas y la recuperación de un corte.
  Solo audio hasta que esté la cámara.

- 2026-10-22: color y efectos de video en la preview (B7): LUT 3D trilineal, grano, viñeta y barras de
  cine, los mismos de la exportación (`VideoColorProcessor`, `Video/PreviewVideoFx.cs`). El color de cada
  cuadro corre en paralelo (1,2 ms por cuadro de 640 px en vez de 5,3). Falta la pestaña Color para
  editarlo (A6); hoy se ve el que trae el proyecto.

- 2026-10-22: escucha del click desde su menú y al mover el volumen (`PlaybackEngine.PreviewClick`).

- 2026-10-22: inspector completo (§1.2; tarea A4, ChatGPT): mezcla, saturación, compresor con estilos,
  cantidad, ajuste fino y EQ del preset, EQ de 6 bandas con editor gráfico y plantillas, envíos de reverb
  y delay, sincronización por clip, encuadre y clip (recortes y fades de audio y video), secciones
  plegables recordadas (`MainWindow.Inspector.cs`, `Controls/EqEditor.cs`, `Controls/Inspector*.cs`).
  Todo con deshacer. (El espectro en vivo del EQ se sumó después, ver arriba.)

- 2026-10-22: render offline de la mezcla (parte de audio de §8; tarea B2, Antigravity):
  `Dsp/MixRenderer.cs` (la cadena por pista y los buses, los mismos que usa la reproducción, sin la
  demora del limitador) y `gclab mezclar`, verificado contra la web. `AudioLoader.cs` carga los medios y
  los remuestrea a 48 kHz para escuchar y exportar (no es el remuestreador de Chromium: misma duración y
  tono, no las mismas muestras).

- 2026-10-22: calibración de latencia (§5.9, motor; tarea B5, Antigravity): `LatencyCalibration.cs`,
  port de `latencyCalibration.ts` (con cable o tocando), verificada con `gclab verificar`; se guarda por
  driver y canales en `%APPDATA%\GuitarCam Studio\latencia.json`. `gclab latencia --modo cable|tocando`,
  `--ver`, `--borrar`, `--simular`. Falta la pantalla (Configuración → Sincronización).

- 2026-10-22: tooltip "Rehacer (Ctrl+Y)", igual que la web (R5, ChatGPT).

- 2026-10-22: ports del DSP que faltaba (§5.3 a §5.6 de MIGRACION; tarea B1, Antigravity), verificados
  muestra a muestra con `gclab verificar` (también con bloques de 100 y 441): presets y EQ del
  compresor, EQ de pista, reverb, delay con ducker, plantillas de paneo y fades de clip, en
  `src/GuitarCam.Audio/Dsp/`. Todavía no suenan en la ventana (tarea B3).

- 2026-10-22: deshacer / rehacer (§9 de MIGRACION; tarea A2, ChatGPT): `GuitarCam.Projects/UndoManager.cs`,
  port de las fotos de `App.tsx` (pistas, clips, canales, formato, aspecto, reverb, delay, color, efectos;
  200 pasos; misma clave en menos de 800 ms = un paso). Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z y los botones de la
  barra; se vacía al cambiar de proyecto. `gclab proyecto verificar-deshacer`. Además, `gclab proyecto
  versiones` / `volver` para las versiones guardadas.

- 2026-10-22: gestor "Proyectos" y plantillas, igual que `SessionPicker` de la web (§2.1 y §2.2 de
  MIGRACION; tarea A1, hecha por Claude y ChatGPT):
  - `GuitarCam.Projects/ProjectLibrary.cs`: lista de la carpeta de proyectos + recientes, nombres
    limpios para Windows y "(2)", renombrar (con la carpeta), duplicar (con hard links, sin exports,
    en una carpeta temporal que se publica al terminar), Papelera, carpeta de proyectos cambiable
  - `ProjectTemplates.cs`: las 5 de fábrica + las del usuario en `%APPDATA%`, "Guardar como plantilla"
  - `Controls/ProjectsDialog.axaml(.cs)`: la app arranca ahí y el chip lo abre; los atajos de la
    ventana se apagan mientras está abierto
  - `gclab proyectos …` para todo lo anterior

- 2026-10-22: formato de proyecto propio (`src/GuitarCam.Projects`), sin compatibilidad con la web:
  - `guitarcam.json` con medios / pistas / clips (varios clips por pista, pistas solo audio)
  - escritura atómica (tmp + `File.Replace` con `.bak`), autoguardado a los 1,5 s, Ctrl+S
  - historial en `historial/` (cada 10 min, 30 recientes + una por día por 90 días) y "Volver a una versión guardada"
  - si el archivo está dañado abre la copia válida más nueva; bloqueo para no abrirlo en dos ventanas
  - versión del formato con migraciones; una versión más nueva abre solo para ver
  - grabaciones recuperables: `RecordingWriter` + diario `*.grabando.json`; archivos sueltos en `media/` vuelven como medios
  - importador de la web (`WebImporter`, menú del proyecto o `gclab proyecto importar`): carpeta nueva
    "Nombre (nativa)" con enlaces duros a los medios, el original no se toca

- 2026-10-22: video decodificado de verdad en la preview (`Video/VideoDecoder.cs`): FFmpeg por pipe,
  BGRA a 30 fps y hasta 640 px de ancho, un hilo por toma que lee un poco por delante del playhead

- 2026-10-22: primera ventana (`src/GuitarCam.App`, Avalonia 11.3): abre el proyecto más reciente de
  la web, timeline con ondas, preview con la grilla de paneles portada, inspector de toma (volumen,
  paneo, saturación, compresor), reproducción por ASIO con click, medidores de entrada y de pista
