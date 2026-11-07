# Pendientes — XTreme

Ideas y cosas para implementar en la versión nativa. Cada ítem: qué es, por qué, y notas si hay.
Al terminar uno, pasarlo a [HECHO.md](HECHO.md) con la fecha (y sacarlo de acá).

## Ideas nuevas (solo nativa)

_Ideas y mejoras para la nativa._

> **Foco:** la app sigue siendo para hacer videos de guitarra. Lo de DAW tiene que servir para eso
> y no puede complicar el camino de "grabo, mezclo y exporto el video".


### Pedidas

#### 1. Usarla también como DAW, solo audio
- [x] Proyectos o pistas sin video (hecho, ver [HECHO.md](HECHO.md))
- [x] Edición de clips: dividir, duplicar, mover a otra pista y a otro tiempo, borrar un pedazo (rango), también de
  varias pistas y cerrando el hueco (corre los marcadores si es en todas), y barra de herramientas (hechos, ver
  [HECHO.md](HECHO.md))
- [x] Ganancia por clip con su curva de volumen, y la onda dibujada con la curva aplicada (hecho, ver [HECHO.md](HECHO.md))
- [x] Varios clips por pista: se reproducen, se dibujan, se exportan (comparten el panel), se graba desde el cabezal y
  "Traer al frente" elige cuál se ve donde se pisan (hecho, ver [HECHO.md](HECHO.md))
- [x] Importar audio externo (hecho, ver [HECHO.md](HECHO.md))
- [ ] Time-stretch: una toma tocada sin click se estira parejo: no se ajusta a la grilla.
- [ ] Transponer: probar si conviene un modo "fino" en cents. Límite conocido: dos armónicos a menos de ~7 Hz (la 3ª
  de un acorde grave) mantienen su batido original
- [x] Efectos en slots por canal, con los internos y los VST3, su latencia compensada y el estado del plugin guardado
  (hecho, ver [HECHO.md](HECHO.md))
- [x] Mejorar el mixer: medidores completos, envíos pre/post y buses unificados (bus = FX: salida y envíos de pistas,
  carpetas y buses; las carpetas solo agrupan) (hecho, ver [HECHO.md](HECHO.md))
- [ ] **Perillas configurables** (pedido del usuario 2026-10-26, cambio grande: decidirlo antes de hacerlo). Los envíos
  ya son una perilla por bus de la lista de cada pista (hecho, ver HECHO). Falta lo demás: que cada perilla sea un
  atajo que elige el usuario: un parámetro de un efecto de la pista (Vida, umbral o cantidad del compresor…) y, con VST3, uno o
  varios parámetros de un plugin (ej. Mix y Decay de una Valhalla). Abierto: ¿un juego para todas las pistas, por
  proyecto o por pista? ¿se elige con clic derecho en la perilla, en Configuración o en los dos? ¿qué hace sin destino
  (apagada con aviso, o agrega el efecto)? Lo mismo serviría para MIDI learn.
- [ ] **Buses que mandan a otros buses** (sacado el 2026-10-26 a pedido del usuario: el circuito no quedaba claro).
  El motor y `xtlab` ya lo soportan; falta una forma de verlo y editarlo que se entienda (un diagrama del ruteo, o
  solo una salida por bus sin envíos) antes de volver a abrirlo.
- …y más: ir sumando acá

#### 2. Soporte de plugins VST
- [x] Instrumentos VST3 en las pistas MIDI y efectos VST3 en los slots de pistas y buses, con compensación de latencia
  (hecho, ver [HECHO.md](HECHO.md)). Falta:
  - [ ] Probar los efectos con una interfaz ASIO y un proyecto de verdad: que la PDC no se oiga al agregar o sacar un
    efecto mientras suena, la carga con varios plugins, y un amp sim (Neural DSP, TONE3000) en una toma de guitarra.
  - [ ] Las 2 a 4 **perillas** por efecto VST3 que elige el usuario de sus parámetros (charlado el 2026-10-26): hace
    falta que el puente liste y mueva parámetros (`IEditController`), y sirve para las perillas configurables.
  - [ ] **Monitorear a través de la cadena**: hoy el monitoreo por software es la entrada cruda; para grabar una guitarra
    directa escuchando un amp sim, la pista armada tendría que pasar su entrada por sus slots (con la latencia de un
    bloque más la del plugin, sin PDC para ella).
  - [ ] Con la PDC, lo que se hace "en el cabezal" mientras suena (marcador, dividir, insertar) cae en `PositionSec`, adelante de lo que se ve y se oye por lo que atrasa el master. Se dejó así a propósito (solo se atrasa lo que se ve): decidir si esas acciones pasan a `HeardSec`.
  - [ ] Al llegar al final de la canción el motor para y se pierde lo que atrasa la PDC: los últimos ms no se oyen. Seguir hasta `endFrame + PdcFrames`.
  - [ ] Un plugin que recién termina de cargar en medio de una toma (o uno agregado mientras se graba) atrasa la mezcla más que lo que la toma tomó en cuenta: `Steady` cubre los bloques tarde y `minMaster` que un plugin se vaya, pero subir la demora no se puede frenar sin dejar ese plugin fuera de tiempo. Ver si conviene apagar esos slots hasta que pare la toma.
  - [ ] Clear (Supertone) informa 9406 muestras de latencia pero atrasa 1470 (medido con `xtlab vst efecto`): la PDC
    usa lo que dice el plugin. Ver si otro momento para preguntarla (después de procesar) le da el número bueno.
  - [ ] VST2: la ventana del plugin no se escala con la pantalla (a 150 % sale chica) y no se puede estirar; `xtvsthost` no le pasa una escala porque el formato no la tiene. Ver si conviene un modo DPI mixto para esa ventana.
  - [ ] VST2: probar con plugins de 32 bits reales y un shell de Waves (hasta ahora, un VST2 propio de prueba en 32 y 64 bits y Padness 2 Jr).
  - [ ] Un efecto apagado sigue procesando (así prenderlo no corta): ver si conviene pausarlo para ahorrar CPU.
  - [ ] La cola de una reverb VST3 en la exportación: la duración sale de los clips, sin contar cuánto suena el plugin
    después (`--cola` lo mide en el master; probarlo con Supermassive).
  - [ ] Elegir el preset del plugin desde la app y mostrar su carga de CPU. El "DSP %" de la barra de título mide solo
    el callback de ASIO, no el proceso de cada plugin; el comando `stats` del host ya da su tiempo por bloque.
  - [ ] Si vuelven callbacks de 4 ms con el transporte parado (2026-10-31, una vez: 90 en 5 minutos, sin GC, con Splice
    INSTRUMENT y un host anunciando 8192): `audio-cortes.log` ahora dice en qué tramo fue (entrada, pistas, resto). No
    se pudo reproducir en `xtlab` y no se explica cómo el bloque anunciado alargaría el callback de la app.
  - [ ] El hilo de audio del host es TIME_CRITICAL sin MMCSS, y a veces queda esperando un hilo del plugin (Windows sube
    ese hilo a prioridad 15): ver si pasarlo a MMCSS "Pro Audio" reduce los bloques perdidos.
  - [ ] Un GC de la UI frena el callback 6 a 8 ms cada unos 8 s con el motor parado (`audio-cortes.log`): ver qué asigna.
- Notas: decidido con el usuario (2026-10-28): **puente en C++ con el SDK oficial de Steinberg, en otro proceso**,
  comunicado por memoria compartida, así si un plugin se cuelga no tira abajo la grabación. El SDK de VST3 es MIT
  desde la 3.8 (octubre de 2025): ya no hay tema de GPL. Hace falta el compilador de C++ de Visual Studio y es un
  proyecto nuevo en `src/`.
  La exportación tiene que pasar por los mismos plugins, igual que hoy el mix offline usa el mismo grafo (ojo: hoy
  el render corre por tramos en paralelo, con plugins con estado eso se complica).
  Compensar la latencia de los plugins (PDC) en todas las pistas y en el click, como ya se hace con el limitador.
  La ventana propia de cada plugin es una ventana Win32 nativa, por fuera de Avalonia.
  Idea charlada con el usuario (2026-10-26): un VST es una tarjeta de slot más (la misma que pliega, abre en
  ventanita y se arrastra), con "Abrir" para su ventana y 2 a 4 perillas que elige el usuario de sus parámetros.
  Para eso, que todo efecto (nativo o VST) exponga una misma lista de parámetros (id, nombre, valor 0..1, texto):
  sirve también para las perillas configurables y el MIDI learn. Un plugin sin parámetros útiles queda con
  encendido y "Abrir".

#### 3. MIDI
**Prioridad del usuario (2026-10-28): un editor para armar bases con instrumentos virtuales.** Decidido:
- **Clips MIDI por pista**, como Logic o Studio One: cada clip tiene sus notas y se edita en un piano roll. Los
  patrones son clips enlazados (hecho, ver [HECHO.md](HECHO.md)); sin step sequencer (se puede sumar después).
- El piano roll va **abajo, en lugar del mixer** (se alternan), sin esperar a los paneles desacoplables.
- Primero un **sampler propio** (tus WAV), después VST3 (punto 2).

Etapas (cada una sirve sola):
- [x] **Etapa 1: pistas y clips MIDI con piano roll** (hecho, ver [HECHO.md](HECHO.md)). Lo que anotó el usuario al
  probarla (2026-10-28):
  - [x] Varios clips MIDI a la vez, el piano roll después de Timeline y su grilla (hechos, ver [HECHO.md](HECHO.md)).
  - [ ] Más cosas en el piano roll (pedido del usuario, 2026-10-28, "bends y eso"): pitch bend y otras curvas por clip
    (modulación, sustain, expresión), dibujadas en carriles como la franja de velocidad. Con el tono de prueba se
    pueden oír los bends; lo demás tiene sentido con el sampler y los VST3, que las reciben como eventos MIDI.
- [x] **Etapa 2: sampler** (hecho, ver [HECHO.md](HECHO.md)), con un sample por pista (decisión del usuario, 2026-11-02: "más adelante vemos"). Lo que quedó:
  - [ ] Varios samples: uno por nota (kit de batería) o por zonas de teclado y velocidad.
- [ ] **Granular** (pedido del usuario, 2026-11-03, a la manera de un granular de estudio; la fase 1 está hecha, ver [HECHO.md](HECHO.md)). Lo que queda, por fases:
  - [ ] Tono: Retune a una escala (Suave / Duro), Detune por grano, voces extra (+3, +7, +12) y acorde o arpegio.
  - [ ] Proceso de grano: filtro, imagen estéreo y delay por grano.
  - [ ] Modulación: dos macros, LFOs, curvas dibujables y seguidor de envolvente, asignables a cualquier perilla.
  - [ ] Animador: un pad XY con recorrido automático (órbita, deriva, al compás) que mueve las macros.
  - [ ] Detectar el BPM del sample para Sync (hoy se escribe a mano).
- [ ] **Etapa 3: instrumentos VST3** con el puente del punto 2.
- [x] **Etapa 4: entradas MIDI** (hecho, ver [HECHO.md](HECHO.md)). Lo que quedó para después:
  - [ ] Probarlo con un teclado de verdad: la latencia al tocar un VST3 (buffer + el bloque del puente) y si las notas
    grabadas caen donde se tocaron (`midiLatencyFrames`); en esta máquina no había ninguna entrada MIDI.
  - [ ] Pasarle al plugin el pedal, el pitch bend y la modulación del teclado en vivo (el puente ya lleva pitch bend y
    CC, los de las notas escritas: slide, porta, paneo, Mod X/Y; del teclado llegan solo notas y el pedal se resuelve
    antes, en `LiveNotes`), junto con los carriles de curvas del piano roll.
  - [ ] Salidas MIDI (synths de hardware, clock hacia afuera) y Windows MIDI Services (varias apps con el mismo
    teclado a la vez).
- [ ] Carriles de curvas por clip, con el LFO de las herramientas sobre ellos (hoy el LFO escribe los valores por nota).
- [ ] Importar proyectos de FL Studio (.flp) (pedido del usuario, 2026-11-01): una sola vía, crea un proyecto nuevo y
  avisa lo que no entró. Trae tempo y compás, notas de los patterns (pistas MIDI), colocación de la playlist, clips de
  audio y samples (copiados a `media/`), nombres, colores, volumen y paneo. Los plugins propios de FL no se pueden traer
  (la pista queda con sus notas); el ruteo entre inserts no entra entero porque la salida de un bus va solo al Master (los envíos entre buses sí, #264); los VST de
  terceros están por ver, y las automatizaciones quedan fuera de la primera etapa. Lector propio en C# en
  `XtremeDaw.Projects`, sin dependencias, con su comando de `xtlab`. El formato no está documentado y cambia con cada
  versión de FL: PyFLP (GPL v3) sirve como referencia, sin copiar su código. Hacen falta `.flp` reales del usuario para
  probarlo.

Otras de MIDI (se apoyan en las entradas de la etapa 4):
- [ ] Mapear perillas y faders de un controlador al mixer (MIDI learn)
- [ ] MIDI clock / tempo sincronizado con otros equipos
- [ ] (Baja prioridad: al usuario no le interesa mucho por ahora) Pedalera o controlador MIDI: grabar/parar, play,
  marcar tomas con los pies

#### 4. Edición de video más completa
- [ ] Color por toma, además del color global
- [ ] Más filtros y efectos (nitidez, desenfoque, glow, estabilización, chroma key/pantalla verde)
  Investigación de CapCut y orden propuesto en [docs/filtros-capcut.md](docs/filtros-capcut.md):
  - [ ] Importar LUT .cube
  - [ ] HSL de 8 colores; después Curvas y ruedas lift/gamma/gain
  - [ ] Look brumoso/pastel (como "Sueño de Mistwood"): en CapCut es un shader de 4 colores con ruido, no un LUT
  - [ ] (Para después, lo pidió el usuario 2026-10-25) Efectos de movimiento: sacudida, zoom y destello al pulso del
    click, glitch
  - [ ] Light leak y partículas de luz (bokeh, luciérnagas, chispas) con semilla fija, igual en vista previa y exportación
  - [ ] VHS, glitch / RGB split, película vieja (polvo, rayas), desenfoque de entrada y salida
- [ ] Textos y títulos: nombre del tema, créditos, carteles
- [ ] Transiciones entre layouts (las de video entre clips ya están: ver HECHO 2026-10-25)
- [ ] Recorte y encuadre animados (zoom/paneo que cambian con el tiempo)

#### 7. Paneles reubicables y desacoplables (pedido 2026-10-25)
Poder acomodar la ventana a cada video (vertical: la preview grande a un costado) y sacar paneles a otros monitores.
Decidido con el usuario:
- El diseño es **de la app** (`config.json`), no del proyecto. Al pasar el proyecto a 9:16 se **sugiere** el diseño
  Vertical con un toast; nunca se cambia solo.
- Pueden salir a otra ventana: **Mixer** (con Mastering), **Preview**, **En vivo**, **Inspector** y **Proyecto**. El
  **transporte** y la barra de título quedan fijos en la ventana principal; el timeline también.
- Hecho a mano (sin `Dock.Avalonia`, salvo que el usuario cambie de idea): sin dependencias nuevas y sin MVVM.

Etapas (cada una sirve sola):
- [x] **Etapas 1 y 2: paneles que se mueven sin reconstruirse y diseños Clásico / Vertical** (hecho, ver
  [HECHO.md](HECHO.md)).
- [x] **Etapa 3: sacar a otra ventana** (hecho, todos los paneles, ver [HECHO.md](HECHO.md)). Queda: la preview a
  pantalla completa en su ventana (monitor para grabar lejos de la compu).
- [ ] **Etapa 4 (si hace falta): arrastrar y soltar** como CapCut, con guías para soltar y diseños propios guardados.

#### 8. Editor de afinación tipo Melodyne (pedido 2026-10-28)
Sobre la toma, no en vivo, y solo monofónico (voz, punteo, bajo; un rasgueo no se separa nota por nota). Es un editor
opcional que se abre desde el clip, como el piano roll: no suma pasos a "grabo, mezclo y exporto". Base que ya hay:
`PitchShifter` (no cambia el largo, los ataques quedan en su lugar), `TransposeCache` (render en segundo plano a
`cache/`, el media no se toca) y el YIN de `PitchTap`. Cada etapa se ejercita desde `xtlab`.
- [ ] **Etapa 1: detectar notas.** YIN sobre toda la toma → curva de pitch → "gotas" (una por nota) dibujadas sobre el
  audio, tipo piano roll. Sin corregir todavía: sirve para ver si la detección anda con tomas reales.
- [ ] **Etapa 2: corregir.** `PitchShifter` con una relación de tono por cuadro (hoy una sola para toda la toma).
  Arrastrar una gota a otra nota y "afinar al centro" con un porcentaje. La versión afinada se cachea igual que una transpuesta.
- [ ] **Etapa 3: expresión.** Modulación/vibrato (escalar lo que la curva se aleja del centro: 0 % plano, >100 %
  exagerado), transición entre notas (0 = efecto "T-Pain") y deriva (aplanar desafinaciones lentas sin tocar el vibrato).
  Por nota y para una selección de varias a la vez.
- [ ] **Etapa 4: curvas a mano** (pedido del usuario, 2026-10-28: "completo completo"). La curva de pitch se edita
  directamente: lápiz para dibujarla, línea recta entre dos puntos, puntos de control con curvas suaves entre ellos y
  borrar lo dibujado para volver a la detectada. Snap opcional a la escala. Vibrato agregado a mano (profundidad,
  velocidad y entrada gradual) donde no lo había.
- [ ] **Etapa 5: formante.** Separar la envolvente espectral (cepstro) y volver a aplicarla después de mover el tono, así
  la voz no suena a ardilla al subirla más de 2 o 3 semitonos. Moverla sola (voz más grave o más finita), por nota y
  con su propia curva dibujable.
- [ ] **Etapa 6: el resto de la parafernalia.** Curva de volumen por nota (y su lápiz), cortar y unir notas (tijera,
  pegamento), corregir la detección a mano (nota mal leída, octava equivocada), sibilantes y respiraciones que no se
  afinan, comparar con el original (A/B) y todo con deshacer. Mover notas en el tiempo y estirarlas: decidirlo antes,
  porque en una toma con cámara desfasa lo que se canta de lo que se ve.
- [ ] **Etapa 7: autotune automático.** Elegir escala y tonalidad y que corrija sola, con la velocidad de la etapa 3.
  El resultado son curvas comunes, así se retoca a mano después.
- [ ] **Medir los aciertos de la detección de escala** (idea, 2026-11-01): correrla contra canciones de escala conocida. La seguridad en % es una estimación; en la batería sola da alta sin sentido.
- [ ] **Usar la escala detectada** (idea, 2026-11-01): resaltar sus notas en el piano roll y proponerla en la etapa 7; hoy `ScaleDetector` solo la muestra.
- [ ] (Para después, si hace falta) Autotune en vivo mientras se graba: otro algoritmo, de baja latencia y peor sonido.

#### 9. Efectos nativos a nivel profesional (pedido 2026-11-01)

Pedido: mejorar los efectos propios, poder usar los del master, una sola interfaz escalable y completa en inspector,
timeline, mixer y master, y una colección que compita con la stock de FL Studio. Nada de esto está hecho ni decidido.

**Relevado en el código (no medido con audio):**
- Reverb: impulso de ruido sintético. Al cambiar Decay, Tone o Width el convolver se reemplaza sin crossfade y pierde
  la cola; el predelay salta sin deslizarse. Va contra "nada de clics": probarlo con `AudioGapLog`.
- Compresor solo de picos, sin RMS, lookahead ni Mix. Delay sin modulación ni saturación en la realimentación.
- Saturación sin oversampling (ADAA de primer orden) y con dos modos.
- No hay transient, de-esser, amp/cab ni pitch en tiempo real. Ningún efecto tiene
  A/B.

**FL Studio como referencia** (manual y comparación de Image-Line, 2026-11-01): 53 / 59 / 65 / 71 efectos según la
edición, contando utilidades, controladores, VFX y versiones repetidas. El objetivo es cubrir sus funciones, no su
número. Lo que hace fuerte a su colección: Fruity Parametric EQ 2 (7 bandas, hasta 48 dB/oct, fase lineal, espectro),
Fruity Limiter (comp + limitador + gate con sidechain y A/B), Maximus (multibanda con lookahead), Fruity Delay 3,
Reeverb 2 y LuxeVerb, Convolver (impulsos propios), Hardcore (amp, gabinete y pedales), Gross Beat, Pitcher.
XTreme ya tiene algo que el Fruity Limiter no trae por defecto: techo en pico real, y además LUFS y Destino.

**Base, antes de cualquier efecto nuevo:**
- [ ] Para todos: A/B y presets del usuario (Dry, Wet, salida y fundido ya están, ver HECHO).
- [ ] Una fila o botón de efectos dibujado en el encabezado del timeline (hoy se abren desde el menú de la pista).
- [ ] VST3 en el master: hoy su lista lleva solo nativos, hace falta que la ventana abra su proceso (`LiveVst`).
- [ ] Oversampling común (2× / 4×) y latencia declarada: la PDC ya existe, hay que sacar el "cero" de los nativos.
- [ ] Sidechain, lo que falta: una carpeta o un bus como fuente (hoy solo pistas) y alinear la key con la PDC cuando la
  fuente y quien escucha tienen latencias distintas (hoy la key llega con la latencia de la fuente).

**Fase 1: subir lo que hay**
- [ ] EQ: notch y bandpass, pendientes hasta 48 dB/oct, solo de banda, mid/side, detección dinámica externa.
- [ ] Compresor: pico/RMS, lookahead, Mix, gráfico de reducción; todo editable en cualquier lugar.
- [ ] Reverb algorítmica nueva (primeras reflexiones, tamaño, difusión, modulación), con cambios sin cortes.
- [ ] Delay: modos cinta y digital, modulación, saturación y filtro en la realimentación.
- [ ] Saturación con más modos y oversampling. Compuerta con rango, hold y release.

**Fase 2: lo que le sirve a la guitarra**
- [ ] Amp y gabinete con impulsos propios (reusa el convolver), distorsión de pedal, transient, de-esser, filtro con LFO.
- [ ] Modulación a tempo (velocidad en divisiones del compás) y presets de chorus, flanger, phaser y tremolo.
- [ ] Pump y Delay siguen solo el primer tempo del proyecto: con cambios en la pista de tempo se corren después del
  primero. Necesitan leer el mapa de tempo en el hilo de audio (`TempoMap` vive en Projects; Audio no lo ve). (#256)

**Fase 3: creativos y de mezcla fina**
- [ ] Reverb por convolución, stutter y gate por pasos, pitch en tiempo real, armonizador, vocoder, shifter de
  frecuencia, delay multibanda, analizador de espectro y estéreo.

Notas: DSP propio, sin paquetes nuevos. Nombres genéricos en la app: no usar marcas ni copiar interfaces ajenas. Los
proyectos viejos tienen que sonar igual: los modos nuevos entran como opción y la reverb actual queda disponible.

#### 10. Biblioteca de samples: lo que sigue (pedido 2026-11-01)

La etapa 1 está hecha (ver [HECHO.md](HECHO.md)). Falta:

- [ ] Escuchar en contexto: sincronizado con el proyecto, entrando en el compás.
- [ ] BPM y tonalidad en segundo plano, leídos del nombre o detectados, con filtros por rango y por tono.
- [ ] Que un loop tome el tempo de su nombre o detectado al insertarlo: hoy entra a su velocidad y recién sigue los
  cambios de tempo del proyecto desde ahí (`Clip.Stretch`). Lo mismo para un importado.
- [ ] Colecciones propias, etiquetas (automáticas por el nombre y manuales) y últimos usados.
- [ ] Marcar un pedazo de la onda y arrastrar solo eso. Selección múltiple en la lista.
- [ ] Más adelante: buscar parecidos, clasificar por contenido, archivos .mid, duplicados, procesar antes de insertar.

#### 11. Más opciones en Configuración → Apariencia (pedido 2026-11-03)

Ya están las perillas, los faders, los medidores, las ondas, la fuente y el tamaño. Falta, con el mismo criterio
(elegir y verlo en vivo):

- [ ] Estilos de ventana (bordes, radios, densidad).
- [ ] Ver qué más conviene hacer elegible (grilla del timeline, clips, botones…).
- [ ] Ondas: un look espejo con el RMS adentro (hace falta guardar el RMS en el caché de picos).

#### 12. Del Banco de pruebas, ya decididos (2026-11-04)

- [ ] **Cortar lo que suena** (#94): con el transporte parado, Stop otra vez corta las colas de los efectos y las voces
  (fundido corto, sin clic; `NoteInstrument.Panic`, `InstrumentPlayer.Panic`, `Chain.Reset` tras el fundido), más un
  atajo; lo explica el tooltip de Stop. Stop no queda deshabilitado mientras algo suena (`LiveTrack.Ringing`).
- [ ] **Ver L/R en todas** (#92): un botón en la cabecera del timeline, junto a restablecer alturas: si alguna pista
  estéreo no lo tiene lo prende en todas, si no lo apaga en todas (`Track.SplitWave`).
- [ ] **Notación de las notas** (#86): Do Re Mi o C D E, aparte del idioma, para toda la app (`NoteNames`, con sus
  cachés rehechos); en un menú del clic derecho del teclado del piano roll (con "Nombrar tecla…", que hoy abre directo)
  y en Configuración. Cambia la decisión del 2026-11-03 ("notas en el sistema del idioma").
- [ ] **Mastering con más diseño** (#73): fichas más bajas (ícono, nombre, ⏻); la elegida en `Raised` con una lengüeta
  que la une al editor de abajo, que va en una tarjeta con borde de acento; las apagadas en gris.
- [ ] **Inspector: canal y secciones** (#77): una franja de 3 px con el color de la pista al costado del nombre (y su
  fondo muy tenue); los títulos Efectos / Envíos / Salida en una banda `Raised` con ícono, 12,5 SemiBold y una rayita
  de acento.

#### 13. Accesibilidad con lector de pantalla (NVDA) (idea 2026-11-04)

Indy, que prueba la app, tiene un amigo productor ciego que produce con NVDA. Hoy NVDA lee los controles comunes
(botones, menús, listas, diálogos) por UI Automation de Avalonia, pero los dibujados a mano (timeline, piano roll,
perillas, faders, mixer, EQ) son un rectángulo vacío. Antes de diseñar, preguntarle qué DAW usa hoy (si es REAPER con
OSARA, copiar sus costumbres y atajos), qué hace más (audio, MIDI, mezcla) y si puede probar versiones parciales.
Por dónde arrancar: los pasos 1 y 2.

- [ ] **Todo con teclado**: moverse entre pistas, clips y slots con flechas; editar con atajos (cortar, mover a la
  grilla, armar, mute/solo, abrir un efecto). Sirve para todos.
- [ ] **Controles propios legibles**: `AutomationPeer` con nombre, rol y valor para `Knob`, `Fader`, la fila de pista,
  el clip y el slot ("Fader Canal 3, -4,5 dB"), manejables con flechas y avisando cuando cambian.
- [ ] **Anuncios hablados** de lo que pasa ("Grabando", "Clip cortado en 1.3.2", "Pista 4 armada"): por
  `nvdaControllerClient` (dependencia nueva, a decidir) o por los anuncios de UIA, que no dependen de NVDA.
- [ ] **Timeline y piano roll navegables**: posición en compases, clip bajo el cursor, nota/velocidad/largo en el piano
  roll. Lo más difícil y lo que más diseño lleva.
- [ ] **Parámetros de plugins en una lista** genérica (el host ya los conoce), porque las ventanas de los plugins no se
  pueden hacer accesibles desde acá. Subirlo junto a los pasos 1 y 2: abre Serum y cualquier otro plugin de una vez.
  Hoy `SayParams` (`main.cpp`) manda solo nombre y valor 0..1: falta pedir el texto de cada valor
  (`getParamStringByValue` en VST3, `effGetParamDisplay` en VST2) para que NVDA diga "1,2 kHz" y no "0,43".
- [ ] **Serum** (consulta del 2026-11-04; descompilarlo no: lo prohíbe su licencia y no sirve, es C++ compilado y su
  ventana es un dibujo). Con la lista de parámetros se ajustan osciladores, filtro, envolventes, LFOs, macros y efectos.
  Queda afuera lo que no es parámetro: navegador de presets, matriz de modulación, editor de wavetables. Para eso hay
  parches de la comunidad que recorren la ventana con clics, imágenes y OCR:
  - Accessibility Bundle (Sonorous Arts, gratis, menú virtual con Ctrl+Alt+V): soporta Serum (no dice si Serum 2), no
    nombra DAW y pide el plugin en un proceso aparte, como ya corre en XTreme. **Probarlo con el amigo de Indy.**
  - ReaHotkey (AutoHotkey, NVDA): soporta Serum 2 (presets, menú) solo en REAPER y Ableton Live 12, con coordenadas
    calibradas para ellos; en XTreme no anda sin que lo adapten sus autores.
  - Investigar recorrer y cargar los presets de Serum desde XTreme (leer sus archivos y pasárselos al plugin).
  - Pedirle accesibilidad a Xfer Records con el caso concreto.

### Separar en stems: antes de publicarlo

- [ ] El instalador no lleva `lib\demucs-graphs` (fuera de git): hay que llevarlos a GitHub Actions, por repo o release, ya que sin ellos stems dice que la conversión no está incluida. Se regeneran con `tools\make-demucs-templates.py`.

- [ ] Probar la separación en una placa NVIDIA (`xtlab separar`): solo se probó en una RX 6800 XT. Si falla, sigue en la
  CPU sola, sin dar audio equivocado.

### Sugerencias (para decidir)

_Ideas que surgieron charlando. Todas apuntan al video._

- [ ] **Layout que cambia en el tiempo ("director")**: en el solo, el panel del punteo crece o pasa a
  pantalla completa, y después vuelve al split. Se marca en el timeline como automatización.
- [ ] **Paneles automáticos**: los paneles se reacomodan solos según quién toca. Si entra un arreglo de
  4 s, los que están se corren con una animación, aparece el nuevo y cuando termina vuelven a ocupar todo.
  Notas: que la disposición sea una función del tiempo del timeline (`Layout(t)`: tramos con el mismo
  conjunto de pistas activas, la grilla de `PanelLayout` para cada uno, ~0,4 s de interpolación con easing),
  así la preview, el scrub y la exportación dan lo mismo. Que la transición termine justo cuando entra el
  clip, que los huecos cortos (< ~2 s) no desarmen nada, que se mantenga el orden y que sea opcional (apagada
  por defecto). La preview es fácil; lo difícil es la exportación, que hoy usa rectángulos fijos en el
  `filter_complex`: o se usan expresiones de FFmpeg con `t` (`overlay`, `scale` con `eval=frame`, y el
  encuadre acompañando) o se componen los cuadros en .NET y se le mandan a FFmpeg por pipe.
- [ ] **Automatización, lo que sigue**: escribirla moviendo los controles (Touch / Latch), bloques enlazados como los patrones MIDI, seleccionar y mover varios puntos, copiarla con los clips, MIDI CC y pitch bend, y carriles en buses y master. La base está hecha (ver [HECHO.md](HECHO.md)).
- [ ] **Varias cámaras por toma**: plano general + primer plano de la mano, y elegir cuál se ve en cada momento
- [ ] **Importar video del celular** y sincronizarlo solo por el audio de la cámara (correlación,
  como la calibración de latencia)
- [ ] **Amp sim + cargador de IRs de gabinete**: grabar la guitarra directa (DI) y que suene bien sin VSTs
- [ ] **Grabación en loop / tomas múltiples (comping)**: grabar varias pasadas del solo y elegir
  los mejores pedazos
- [ ] **Visualizadores de audio** como capa del video (onda o espectro animado)
- [ ] **Acordes / tablatura / letra en pantalla** mientras grabás (tipo teleprompter) y opcionalmente en el video

### Funciones grandes de Fender Studio Pro que faltan (relevado 2026-11-05)

Cruce del manual (`referencia/fender-studio-pro/manual-completo.md`) con el código, la ayuda y HECHO. Solo lo que no
estaba anotado en otra sección (comping, afinación tipo Melodyne, congelar, MIDI learn, MIDI clock, Touch/Latch, amp
sim, vocoder y partitura completa ya están arriba). Nada decidido: cada una se charla con el usuario antes de hacerla.
Orden sugerido por impacto: pista de arreglo, bounce. Cada una tiene su pedido en el Banco (#214 a #248).

Línea de tiempo y arreglo:
- [ ] **Pista de arreglo**: secciones con nombre (Intro, Estribillo…) que se mueven, duplican o borran con todo lo que
  tienen en todas las pistas. Los marcadores hoy solo marcan.
- [ ] **Versiones del arreglo** (Scratch Pad): arreglos alternativos dentro del mismo proyecto.
- [ ] **Pista de letra**: letra sincronizada y una vista para leerla al grabar (se junta con la sugerencia del
  teleprompter).
- [ ] **Pista de acordes que edita**: que el audio y el MIDI sigan los acordes escritos (hoy `ChordMarker` solo detecta).

Grabación:
- [ ] **Grabación retrospectiva MIDI**: recuperar lo tocado en el teclado sin haber apretado Grabar.
- [ ] **Step record y Note Repeat** para MIDI.
- [ ] **Mezclas de auriculares** (cue mixes): una mezcla de monitoreo por músico, a salidas de la interfaz.

Edición de audio:
- [ ] **Cuantizar audio** por sus ataques (`TimeMap` ya los detecta): llevarlos a la grilla.
- [ ] **Bounce / render en el lugar**: un clip o una pista con sus efectos pasa a ser audio nuevo.

Mixer:
- [ ] **Canales VCA y grupos de mezcla**, aparte de las carpetas.
- [ ] **Bus de escucha** (Listen Bus): salida de monitoreo aparte del master (auriculares, corrección de sala).

MIDI e instrumentos:
- [ ] **Efectos de nota en vivo**: arpegiador, acordes y repetidor sobre lo que se toca (hoy el arpegio es una
  herramienta del piano roll).
- [ ] **Instrumentos nativos**: batería por pads, sintetizador, multi-instrumento (varios en una pista).
- [ ] **Articulaciones** (Sound Variations): cambiar la articulación de un instrumento desde las notas.
- [ ] **Instrumentos y efectos de hardware** por envío y retorno de la interfaz, con su latencia compensada.

Páginas enteras:
- [ ] **Mastering de un álbum**: varios temas en orden con sus pausas, y DDP/CD (hoy se masteriza un tema).
- [ ] **Modo en vivo** (Show): setlist con reproductores y patches por tema.
- [ ] **Lanzador de clips** (Launcher): clips en una grilla, lanzados a tempo.
- [ ] **Audio espacial / Dolby Atmos**.

Archivos y sincronía:
- [ ] **AAF**: importar y exportar proyectos entre DAWs.
- [ ] **Importar pistas de otro proyecto .xtp** (con sus clips, efectos y ruteo).
- [ ] **Conversor de audio por lotes**.
- [ ] **Ableton Link**.

### Opcional — ideas de Fender Studio Pro (2026-10-24)

Se relevaron los 224 temas del manual y se cruzaron con la nativa. El detalle, las fuentes y el orden
sugerido están en [docs/FENDER_STUDIO_PRO_ANALISIS.md](docs/FENDER_STUDIO_PRO_ANALISIS.md).
Estas ideas son opcionales, sin compromiso de implementación. Comping, MIDI, automatización, amp sim, importación
de video y normalización ya están anotados arriba; el análisis amplía su alcance sin duplicarlos acá.

- [ ] **Regrabar un rango automáticamente**: escuchar compases previos del tema, grabar solamente entre
  entrada y salida y escuchar el después. Amplía la grabación actual desde el cabezal; requiere silenciar
  la toma anterior solo dentro del tramo y conservar la sincronía de cámara.
- [ ] **Fundidos cruzados de audio**: unir clips o correcciones con un fundido breve ajustable, igual en
  reproducción y exportación; distinguirlo de las transiciones visuales.
- [ ] **Rescatar audio recién tocado**: investigar un búfer opcional y preasignado para recuperar una
  idea sin haber apretado Grabar. Adaptación propia: la grabación retrospectiva del manual es MIDI.

## Notas

- Del nombre viejo: no crear nunca otro repo `GuitarCamNative` en GitHub, rompe la redirección. Antes de publicar con
  el nombre nuevo, buscarlo en las oficinas de marcas (USPTO, INPI). El botón de descarga del README apunta a
  `XTreme-Setup.exe`: anda recién con el próximo release.
- Firma del instalador: falta pedirla a SignPath Foundation (gratis para código abierto; el README ya tiene la
  política de firma que piden). Hasta entonces el workflow arma el instalador sin firmar y Windows SmartScreen avisa
  al instalarlo. Con la cuenta: proyecto `XTreme` (el slug por defecto del workflow) con el repo
  `xtrimdr/XTreme`, política `release-signing`, el secreto `SIGNPATH_API_TOKEN` y la variable
  `SIGNPATH_ORGANIZATION_ID` en GitHub; la firma se prende sola.
- `AudioGapLog` es un diagnóstico provisorio: sacarlo (o dejarlo detrás de una opción) cuando el audio esté confirmado.
- Dudas de la revisión del audio en vivo (2026-11-02, leyendo el código, sin ASIO):
  - [ ] Un plugin que termina de cargar en medio de una toma sube el PDC por encima del de la toma (`recPdcFrames`, que solo sirve de piso): la mezcla se atrasa y la toma queda corrida eso. ¿Se deja cargarlo en silencio hasta el final, o se avisa?
  - [ ] Una toma recuperada después de un cierre inesperado entra sin el PDC: el diario guarda `SyncMs` antes de arrancar (`GetLatencyInfo` no lo cuenta y todavía no se sabe). Parar normal sí lo suma. El diario se podría reescribir una vez arrancada.
  - [ ] Al llegar al final del tema (sin bucle) con PDC, los últimos `PdcFrames` quedan en las líneas de retardo y no se oyen, y el cabezal salta esos cuadros. Son milisegundos.
  - [ ] El primer Play después de una toma, o de un plugin atrasado, baja la latencia del slot enseguida pero el resto de la mezcla recién en el próximo tick (`UpdateLatency`): hasta 16 ms desalineados al arrancar.
  - [ ] `UpdateLatency` asigna un cierre chico en cada tick aunque no cambie nada (`Index` lo usan lambdas, y C# lo crea al entrar). Moverlo a un método aparte lo evita. `RefreshClearButtons` y el `videoFade` de `Tick` también asignan por tick.

- Puntitos en la preview (resuelto): con la ventana permitiendo transparencia, algunos píxeles oscuros
  del video dejaban ver la ventana de atrás. `TransparencyLevelHint="None"` lo corta, y con el video
  decodificado quedan solo unos pocos píxeles negros sobre casi negro, invisibles. Ojo al revisar
  capturas: las de GDI (CopyFromScreen/PrintWindow) dan alfa 0 en los píxeles negros y los visores los
  pintan blancos, aunque en pantalla se vean bien. Mirar el RGB, no el alfa.
- Lo que falta probar a mano (saltos del video mientras reproduce, fps con muchas tomas, calibración,
  medidores con sonido…) está todo en [docs/PRUEBAS.md](docs/PRUEBAS.md).
- Cámara: mientras la app está abierta, la cámara queda tomada, así que otro programa no
  la puede usar. Si se desconecta, el monitor dice "Sin señal de cámara" y no se reconecta sola: hay que
  tocar "Refrescar dispositivos".
