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
  El motor y `wtlab` ya lo soportan; falta una forma de verlo y editarlo que se entienda (un diagrama del ruteo, o
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
  - [ ] La imagen y el cabezal no se atrasan con la PDC: con un plugin de mucha latencia el video se adelanta a lo que
    se oye. Correr `PositionSec` lo que atrasa el master mientras suena.
  - [ ] Clear (Supertone) informa 9406 muestras de latencia pero atrasa 1470 (medido con `wtlab vst efecto`): la PDC
    usa lo que dice el plugin. Ver si otro momento para preguntarla (después de procesar) le da el número bueno.
  - [ ] Un efecto apagado sigue procesando (así prenderlo no corta): ver si conviene pausarlo para ahorrar CPU.
  - [ ] La cola de una reverb VST3 en la exportación: la duración sale de los clips, sin contar cuánto suena el plugin
    después (`--cola` lo mide en el master; probarlo con Supermassive).
  - [ ] Que el loop mande las notas de después de A a tiempo (hoy el primer bloque después del salto puede salir tarde).
  - [ ] Un efecto VST3 que llega siempre un poco tarde pierde todos sus bloques (`VstInsert`, `TrySend`/`TryTake`):
    que tome el bloque atrasado, como ya hace el instrumento en `VstBridge.Exchange`.
  - [ ] Elegir el preset del plugin desde la app y mostrar su carga de CPU. El "DSP %" de la barra de título mide solo
    el callback de ASIO, no el proceso de cada plugin; el comando `stats` del host ya da su tiempo por bloque.
  - [ ] Si vuelven callbacks de 4 ms con el transporte parado (2026-10-31, una vez: 90 en 5 minutos, sin GC, con Splice
    INSTRUMENT y un host anunciando 8192): `audio-cortes.log` ahora dice en qué tramo fue (entrada, pistas, resto). No
    se pudo reproducir en `wtlab` y no se explica cómo el bloque anunciado alargaría el callback de la app.
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
  - [ ] Le falta: que los clips MIDI entren en las herramientas (dividir, borrar, escuchar) y en el rango; que la nota
    que suena al tocar el piano roll pase por la cadena de la pista.
  - [ ] Más cosas en el piano roll (pedido del usuario, 2026-10-28, "bends y eso"): pitch bend y otras curvas por clip
    (modulación, sustain, expresión), dibujadas en carriles como la franja de velocidad. Con el tono de prueba se
    pueden oír los bends; lo demás tiene sentido con el sampler y los VST3, que las reciben como eventos MIDI.
- [ ] **Etapa 2: sampler.** Un WAV por nota (kit de batería) o uno melódico que se transpone. Voces preasignadas en el
  hilo de audio, y el mismo camino en la reproducción y en la exportación.
- [ ] **Etapa 3: instrumentos VST3** con el puente del punto 2.
- [x] **Etapa 4: entradas MIDI** (hecho, ver [HECHO.md](HECHO.md)). Lo que quedó para después:
  - [ ] Probarlo con un teclado de verdad: la latencia al tocar un VST3 (buffer + el bloque del puente) y si las notas
    grabadas caen donde se tocaron (`midiLatencyFrames`); en esta máquina no había ninguna entrada MIDI.
  - [ ] Que una toma MIDI sobreviva si el programa se muere a la mitad (hoy las notas viven en memoria hasta parar;
    las tomas de audio tienen su diario).
  - [ ] Pasarle al VST3 el pedal, el pitch bend y la modulación como eventos (el puente solo lleva notas: el pedal se
    resuelve antes, en `LiveNotes`), junto con los carriles de curvas del piano roll.
  - [ ] Salidas MIDI (synths de hardware, clock hacia afuera) y Windows MIDI Services (varias apps con el mismo
    teclado a la vez).
- [ ] Después: importar y exportar .mid.
- [ ] Importar proyectos de FL Studio (.flp) (pedido del usuario, 2026-11-01): una sola vía, crea un proyecto nuevo y
  avisa lo que no entró. Trae tempo y compás, notas de los patterns (pistas MIDI), colocación de la playlist, clips de
  audio y samples (copiados a `media/`), nombres, colores, volumen y paneo. Los plugins propios de FL no se pueden traer
  (la pista queda con sus notas); el ruteo entre inserts no entra entero porque un bus sale solo al Master; los VST de
  terceros están por ver, y las automatizaciones quedan fuera de la primera etapa. Lector propio en C# en
  `XtremeDaw.Projects`, sin dependencias, con su comando de `wtlab`. El formato no está documentado y cambia con cada
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
- [ ] **Etapa 3: sacar a otra ventana.** _En pausa (el usuario, 2026-10-26: no es prioridad hoy)._ Botón en el encabezado del panel; cerrar la ventana lo devuelve a su lugar. Se
  recuerdan monitor, posición y tamaño; si el monitor ya no está, vuelve a la principal. Preview con pantalla completa
  (monitor para grabar lejos de la compu). Con el mixer afuera se ven timeline y mixer a la vez: la X lo trae al frente.
  Los atajos funcionan desde cualquier ventana. Ojo: dos ventanas dibujando tampoco pueden generar basura por cuadro, y la
  preview de afuera usa el mismo decodificador.
- [ ] **Etapa 4 (si hace falta): arrastrar y soltar** como CapCut, con guías para soltar y diseños propios guardados.

#### 8. Editor de afinación tipo Melodyne (pedido 2026-10-28)
Sobre la toma, no en vivo, y solo monofónico (voz, punteo, bajo; un rasgueo no se separa nota por nota). Es un editor
opcional que se abre desde el clip, como el piano roll: no suma pasos a "grabo, mezclo y exporto". Base que ya hay:
`PitchShifter` (no cambia el largo, los ataques quedan en su lugar), `TransposeCache` (render en segundo plano a
`cache/`, el media no se toca) y el YIN de `PitchTap`. Cada etapa se ejercita desde `wtlab`.
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
- Compresor solo de picos, sin RMS, sidechain, lookahead ni Mix. Delay sin modulación ni saturación en la realimentación.
- Saturación sin oversampling (ADAA de primer orden) y con dos modos.
- No hay transient, de-esser, amp/cab ni pitch en tiempo real. Ningún efecto tiene
  sidechain ni A/B.

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
- [ ] Sidechain: fuente elegible en el ruteo. Es el cambio más grande del motor (`MixLatency`, `ChannelDsp.Nodes`).

**Fase 1: subir lo que hay**
- [ ] EQ: notch y bandpass, pendientes hasta 48 dB/oct, solo de banda, mid/side, detección dinámica externa.
- [ ] Compresor: pico/RMS, lookahead, Mix, gráfico de reducción, sidechain; todo editable en cualquier lugar.
- [ ] Reverb algorítmica nueva (primeras reflexiones, tamaño, difusión, modulación), con cambios sin cortes.
- [ ] Delay: modos cinta y digital, modulación, saturación y filtro en la realimentación.
- [ ] Saturación con más modos y oversampling. Compuerta con rango, hold, release y sidechain.

**Fase 2: lo que le sirve a la guitarra**
- [ ] Amp y gabinete con impulsos propios (reusa el convolver), distorsión de pedal, transient, de-esser, filtro con LFO.
- [ ] Modulación a tempo (velocidad en divisiones del compás) y presets de chorus, flanger, phaser y tremolo.

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

### Separar en stems: antes de publicarlo

- [ ] El instalador no lleva `lib\demucs-graphs` (fuera de git): hay que llevarlos a GitHub Actions, por repo o release, ya que sin ellos stems dice que la conversión no está incluida. Se regeneran con `tools\make-demucs-templates.py`.

- [ ] Probar la separación en una placa NVIDIA (`wtlab separar`): solo se probó en una RX 6800 XT. Si falla, sigue en la
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
- [ ] **Congelar pistas al incorporar VST**: render temporal reversible para reducir DSP, conservando
  originales y estado de plugins.

## Notas

- Lo que quedó del nombre viejo (2026-10-29, ver HECHO): la carpeta `D:\Programacion\GuitarCamNative` (la memoria
  de Claude va por esa ruta: moverla también; no crear nunca otro repo `GuitarCamNative` en GitHub, rompe la
  redirección) y el archivo `guitarcam.json` de cada proyecto (renombrarlo pide migrar al abrir y
  que las versiones viejas no lo vean). Antes de publicar con el nombre nuevo, buscarlo en las oficinas de marcas
  (USPTO, INPI). El botón de descarga del README apunta a `XTreme-Setup.exe`: anda recién con el próximo release.
- Firma del instalador: falta pedirla a SignPath Foundation (gratis para código abierto; el README ya tiene la
  política de firma que piden). Hasta entonces el workflow arma el instalador sin firmar y Windows SmartScreen avisa
  al instalarlo. Con la cuenta: proyecto `XTreme` (el slug por defecto del workflow) con el repo
  `xtrimdr/XTreme`, política `release-signing`, el secreto `SIGNPATH_API_TOKEN` y la variable
  `SIGNPATH_ORGANIZATION_ID` en GitHub; la firma se prende sola.
- `AudioGapLog` es un diagnóstico provisorio: sacarlo (o dejarlo detrás de una opción) cuando el audio esté confirmado.

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
