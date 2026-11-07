# XTreme: detalles

Requisitos, cómo compilar, atajos de teclado, cómo está hecho y `xtlab`. Lo general está en el [README](README.md).

**Español** · [English](DETALLES_EN.md)

## Requisitos

- Windows 10/11 de 64 bits.
- Una interfaz de audio con driver **ASIO**.
- **FFmpeg** y **ffprobe** en el `PATH` o instalados con winget (cámara, video y exportación).
- Para la preview, los DLL de **FFmpeg 9** (versión *shared*) en `lib\ffmpeg\` (`avcodec-63`, `avformat-63`,
  `avutil-61`, `swscale-10`, `swresample-7` y su `LICENSE`). No van en git: los baja `tools\get-ffmpeg.ps1` de
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) (`ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip`,
  carpeta `bin`). El build los copia a `ffmpeg\` junto al exe. Sin ellos la preview usa ffmpeg.exe, que tarda más
  en cada salto.
- [.NET 10 SDK](https://dotnet.microsoft.com/) para compilar.
- Para los plugins VST3 y VST2 (opcional): Visual Studio con **Desarrollo para el escritorio con C++** y el **Windows 11 SDK**,
  y el SDK de VST3 de Steinberg (licencia MIT) en `lib\vst3sdk`, que baja `tools\get-vst3sdk.ps1` (unos 21 MB, fuera de
  git). Con eso, `dotnet build` también compila `xtvsthost.exe` y `xtvsthost32.exe` (`src\XtremeDaw.VstHost`, C++) y los copia junto al exe;
  sin eso la app compila igual y las pistas MIDI suenan con el tono de prueba.
- Para **Extraer MIDI**: el modelo de Basic Pitch (Spotify, Apache 2.0, ~230 KB) en `lib\basic-pitch`, que baja
  `tools\get-basic-pitch.ps1` (fuera de git). El build lo copia a `basic-pitch\` junto al exe; sin él, "Extraer MIDI"
  queda deshabilitado y lo dice. Corre con ONNX Runtime (NuGet).
- Para el instrumento **General MIDI**: el banco GeneralUser GS (S. Christian Collins, uso y redistribución libres,
  ~31 MB) en `lib\gm`, que baja `tools\get-gm.ps1` (fuera de git). El build lo copia a `gm\` junto al exe y va en el
  instalador; sin él, una pista General MIDI avisa que falta y calla.
- Para **Separar en stems**: los modelos de Demucs (Meta, ~425 MB) en `lib\demucs`, que arma `tools\get-demucs.ps1`
  (fuera de git, necesita [uv](https://docs.astral.sh/uv/)). El build los copia a `demucs\` junto al exe, pero no van en
  el instalador: la licencia de los pesos no está clara. Sin ellos, la opción queda deshabilitada y lo dice.

## Compilar y ejecutar

```
tools\probar.ps1 "qué probar"
```

Compila, corre `xtlab comprobar-dsp`, copia la app a `pruebas\<fecha>-<commit>` (fuera de git; quedan las
últimas 5) y la abre desde ahí, con la marca "PRUEBA <commit>" en la barra de título. Como nunca corre desde `bin\`,
la app abierta no traba la próxima compilación. `-Abrir` abre la última copia sin compilar (con `-Probar 12`, con el aviso de qué probar en el #12), `-NoAbrir` solo compila y
copia, `-SinComprobar` salta `xtlab`, `-Proyecto carpeta` abre ese proyecto. Si ya hay un XTreme abierto, antes de abrir
pregunta en un cuadro si lo cierra (guarda el proyecto antes), así la copia nueva no abre el mismo proyecto en solo
lectura. Si algo falla, dice "NO SE PUEDE PROBAR" y no abre nada (desde el acceso directo, que no muestra la consola, lo avisa en un
cuadro; queda anotado en `%APPDATA%\XTreme\sesiones.log`, junto con cada inicio y cierre de la app y sus errores). `-SoloCompilar` solo compila, y `dotnet build` también sirve.

El texto queda en el **Banco de pruebas** (`pruebas\lista.json`, sobrevive a las copias). Lo muestra el Banco, un programa aparte (`xtbench.exe [--lista archivo]`, en `pruebas/banco/`, uno solo a la vez; compilar XTreme no lo cierra y uno nuevo entra la próxima vez que se abre), que una copia de prueba abre sola si hay algo pendiente y desde el botón junto a "PRUEBA", y que también abre `-Banco` o el acceso directo "XT-WorkBench": cada ítem se marca Anda o No anda, con una nota de qué falló. Un ítem de una copia más nueva que la abierta avisa que falta en esta. Sus botones **Revisar** abren Claude en `/xt_revisar-workbench` (un ítem, un estado o la pestaña), y el menú **Claude** de la barra de título las demás skills del proyecto; ahí mismo se elige, una vez, si abren en la app de escritorio (el mensaje queda escrito y se confirma la carpeta) o en una consola (Windows Terminal, donde arranca solo; sin la app, siempre la consola). `-Lista` lo muestra con las pestañas del Banco (Pedidos, A arreglar, Para probar y Resuelto); `-Arregla 12` (con `-NoAbrir` o no) devuelve ese ítem a Para probar en la copia nueva y cuenta un intento más, salvo que el usuario ya lo haya marcado Anda (código 2: no se reabre). Cada ítem es un **ticket** con su conversación (el pedido o qué probar, cada arreglo, cada veredicto con su nota y los comentarios): `-Ticket 12` lo muestra entero y `-Comenta 12 "texto"` dice algo en él sin cambiar su estado; `-Titula 12 "título"` le pone el título corto que el agente le da al tomarlo (el Banco lo muestra arriba y el ticket como una línea de tiempo, cada entrada de Usuario o de Agente); el `"qué probar"` de `-Arregla` queda como el mensaje del arreglo. Cada cambio queda además en `pruebas\lista.historial.jsonl`, con quién lo hizo (el usuario o la sesión de Claude): `-Historial 12` muestra el de un ítem y `-Historial 0` lo último de todos. Lo que anota o devuelve un agente llega al Banco como **Nuevo** hasta que el usuario lo ve.

En la pestaña **Pedidos** del panel el usuario carga lo que quiere que se haga, numerado como el resto y con su tipo (**Error**, **Mejora**, **Función nueva** o **Idea**); de ahí sacan el trabajo los agentes. `-Toma 12` reserva un pedido, o algo marcado No anda, Anda mal o A mejorar, para una sesión (código 2 si ya lo tiene otra, y dice cuál; la misma sesión lo puede volver a tomar; a las 6 horas vence, y al cerrarse la sesión que lo tomó queda libre), `-Suelta 12` lo devuelve y `-Arregla 12` lo vuelve a Para probar cuando está hecho. Los tres aceptan varios números (`-Arregla 12,14`: una sola compilación y copia). `-Pide "qué" -Tipo error|mejora|funcion|idea` anota uno desde la consola.

Cada ticket tiene un **tema**, de una lista fija (la parte de la app: Timeline, Mixer, Inspector, Piano roll y MIDI, Instrumentos, Efectos, Mastering, Automatización, Plugins, Audio y transporte, Video, Exportación, Análisis, Proyecto y archivos, Interfaz, Banco y herramientas; `xtlab lista temas` dice qué va en cada uno). El Banco lo muestra como un badge, filtra por tema arriba de la lista y se elige al cargar un pedido o desde el ticket. `-Tema mixer` le pone el tema a lo que se anota (`-Pide`, `"qué probar"`, `-Arregla`) y `-Clasifica 12,14 -Tema mixer` a tickets que ya están (`-Tema ninguno` lo saca).

Si hay varias sesiones trabajando en esta carpeta, compilar, comprobar y copiar van de a una: `dotnet build` en Debug (`tools\turno.targets`), `probar.ps1` y los comandos pesados de `xtlab` (`comprobar-dsp`, `exportar`, `separar`…) esperan su turno (`turno.lock`, junto a `xtlab.exe`). `probar.ps1` y `xtlab` corren con prioridad baja para que la máquina siga respondiendo. Si en 9 minutos no les toca, salen con error diciendo quién lo tiene (`probar.ps1` y `xtlab`, código 4). `XtremeDaw.Audio` se compila optimizado también en Debug: así la copia de prueba y `xtlab` miden el DSP como el instalador.

Los archivos también van de a una sesión: un hook de Claude Code (`tools\bloqueos.ps1`, registrado en `.claude\settings.json`) le da cada archivo editado, o cambiado por un comando, a la sesión que lo tocó hasta que termina su turno; otra sesión que quiera cambiarlo espera (hasta 9 minutos) y, si un comando pisó un archivo ajeno, se le avisa. Bloquea `git restore`, `stash`, `clean`, `reset --hard` y `checkout --`. `tools\bloqueos.ps1 -Lista` muestra quién tiene qué y `-Limpiar` suelta todo (por si una sesión murió; igual vencen solos a los 30 minutos sin uso). Las sesiones se mandan avisos (#263): el hook avisa solo cuando se libera un archivo que esperabas, cuando otra sesión toma uno que cambiaste, cuando un comando ajeno cambió uno tuyo y cuando una compilación falla en un archivo tuyo; a mano, `-Avisa <#ticket|sesión|todos> -Texto "…"` (`-Atencion` para que pare), `-PideArchivo <ruta>` (se lo pide a quien lo tiene), `-SueltaArchivo <ruta>` (lo suelta antes de terminar el turno) y `-Buzon` (los avisos sin leer). Le llegan al agente en su próxima edición o comando, antes de terminar su turno o con el próximo mensaje, y el Banco los muestra al llegar.

La app arranca en el gestor de **Proyectos** (lista, buscador, plantillas, renombrar, duplicar, Papelera).
Con una carpeta de proyecto como argumento, la abre directo. `tools\make-shortcut.ps1` crea en el escritorio el acceso
directo **XTreme Prueba**, que abre la última copia de prueba terminada (`probar.ps1 -Abrir -Acceso`) sin compilar y sin depender de ninguna sesión abierta; `probar.ps1` lo rehace después de cada copia. Los ajustes se guardan en `%APPDATA%\XTreme`.

## Atajos principales

Todos se pueden cambiar en ⚙ Configuración → Atajos de teclado (menos Esc). Ahí mismo, **Distribución de teclas** cambia
todos de una vez a los de Cubase, Logic Pro, FL Studio, Reaper o Studio Pro (`KeyLayouts`); los cambios propios quedan
guardados en cada distribución. Esta tabla es la de XTreme. Con la ventana de un plugin en primer plano
todas las teclas van a XTreme, salvo con **Teclas al plugin** prendido en su barra.

| Tecla | Acción |
|---|---|
| Espacio | reproducir / parar |
| Shift+R o Num * | grabar (grabando: detener y guardar) |
| R | armar / desarmar la pista seleccionada |
| Inicio | volver al principio |
| Ctrl+. | cortar audio: detiene y calla las colas de los efectos y las notas que siguen sonando |
| L | repetir en loop (el rango A–B, o todo el tema) |
| I / O | inicio (A) / final (B) del rango en el cabezal |
| K / , / . | marcador en el cabezal / ir al anterior / al siguiente |
| C / Shift+C / T | click / cuenta previa / tap tempo |
| A | afinador |
| M / S | mute / solo de la pista |
| Ctrl+K | teclado virtual: con él prendido, la fila de A son las blancas, la de arriba las negras, Z / X la octava, C / V la velocidad y Shift el sustain. Con escala, las filas de Z, A y Q son tres octavas de la escala, O / P la octava y L / Ñ la velocidad |
| H | acordes en el piano roll: un clic escribe un acorde; 1 a 4 eligen la inversión del acorde elegido |
| F | ver todo |
| X | timeline / mixer |
| Alt+X o Insert / D o Ctrl+B | dividir el clip en el cabezal / duplicarlo (o los marcados, en bloque) |
| Shift+arrastrar un clip | copiarlo: el original queda en su lugar |
| Shift+Q | alinear los clips elegidos a la grilla |
| Ctrl+A / Ctrl+D | marcar todos los clips / sacar la selección |
| Q | cuantizar al imán del piano roll: las notas elegidas (o todas) en el piano roll, o los clips MIDI elegidos |
| Alt+1 / 2 / 3 | piano roll: escribir, pintar, cortar |
| Alt+A, Alt+S, Alt+U, Alt+F, Alt+W, Alt+J, Alt+Y, Alt+R, Alt+E, Alt+P, Alt+Q, Alt+V, Alt+O, Alt+L | piano roll: arpegiar, rasgueo, cortar con ritmo, flam, claw, limitar, espejar, azar, riff, progresión, cuantizar, niveles, LFO, articular |
| Ctrl+L, Ctrl+U, Ctrl+G | piano roll: legato, cortar al imán, unir |
| Shift+M, Shift+G, Alt+G, Shift+I | piano roll: silenciar notas, agrupar, desagrupar, invertir la selección |
| Enter / Esc | con una herramienta abierta: aplicar / cancelar |
| Ctrl+C / Ctrl+X / Ctrl+V | copiar / cortar los clips elegidos (tomas y clips MIDI) / pegarlos en el cabezal, desde la pista elegida |
| Supr | quitar el clip (o los marcados, o la pista vacía); con un rango, borrarlo |
| Shift+Supr | borrar el rango y cerrar el hueco (lo de después se corre) |
| 1 a 9 | herramientas: Seleccionar / Lápiz / Pincel / Rango / Dividir / Borrar / Escuchar / Zoom / Mano |
| Ctrl+Z / Ctrl+Y | deshacer / rehacer |
| Ctrl+Shift+N | nueva pista |
| Ctrl+Shift+D | duplicar la pista (o las elegidas), con su contenido |
| F2 | renombrar según dónde hiciste el último clic: en el inspector, su nombre; en los encabezados, la pista o carpeta seleccionada; en el timeline, el clip seleccionado (sin clip, la pista o carpeta) |
| Ctrl+G | agrupar en una carpeta lo elegido en el timeline (pistas, o las de los clips seleccionados) |
| Clic derecho en la regla | cómo se dibuja la grilla: Auto, igual que el imán o una subdivisión fija |
| Arrastrar en vacío sobre las pistas (Ctrl: sumar) | marcar varios clips con un rectángulo para moverlos o borrarlos juntos |
| Ctrl+clic / Shift+clic en un encabezado | marcar varias pistas (una por una / un rango) para moverlas juntas |
| Ctrl+S | guardar ya |
| Ctrl+Shift+S | guardar como… |
| B / P | plegar o abrir el panel del canal / del proyecto (si está oculto con su botón de la barra de título, lo vuelve a mostrar) |
| Enter | maximizar el panel que tocaste último: video, timeline, piano roll, mixer o mastering (otra vez Enter vuelve a como estaba) |
| F11 | pantalla completa (otra vez F11 vuelve a como estaba) |
| Ctrl + / Ctrl − / Ctrl 0 | agranda o achica la interfaz 1 px, o la vuelve al 100 % |
| F1 | ayuda (otra vez F1 la cierra) |
| Rueda, Shift+rueda, Ctrl+rueda, Alt+rueda | subir y bajar, zoom, desplazar a los costados, alto de pista (los modificadores y el sentido de cada uno, con "Invertir", se cambian en Configuración → Atajos de teclado; zoom y alto también con los sliders al pie de los encabezados). En el mixer, la rueda sola y Ctrl+rueda lo desplazan a los costados y Shift+rueda (o la barra de su cabecera) agranda o achica el ancho de los canales |

En el clip seleccionado, un clic sobre su línea de volumen agrega un punto; los puntos se arrastran y un
doble clic los saca.

En la franja de arriba de la regla, arrastrar marca el rango A–B (y prende el loop); sus bordes y el rango entero se
arrastran. Doble clic agrega un marcador; los marcadores se arrastran, un clic salta ahí y doble clic los renombra.
Clic derecho en la franja: agregar marcador, empezar o terminar el rango ahí, sacarlo, y en un marcador ponerle un
nombre de la lista o repetir hasta el siguiente. Un rango marcado con Shift+arrastrar desde fuera de un clip (o con la herramienta Rango),
en una pista o en varias, también se puede repetir desde su menú.

Las herramientas del timeline están en el centro de la barra del proyecto: **Seleccionar** (lo de siempre), **Lápiz** (clic en un clip lo toma; clic en un
lugar vacío pone una copia, y arrastrar la ubica), **Pincel** (igual, pero arrastrar repite el clip a lo largo del tramo,
una copia al lado de la otra; un clic en un patrón de Patrones o en un sample de la Biblioteca también los carga), **Rango**
(marca un tramo de una o varias pistas; clic derecho: borrarlo, borrarlo y cerrar el hueco, repetirlo), **Dividir**
(un clic corta el clip ahí, en la grilla), **Borrar** (saca los clips que toca), **Escuchar** (mantené apretado sobre
un clip para oír esa pista sola desde ahí), **Zoom** (clic acerca, Alt+clic aleja, arrastrar muestra ese tramo) y
**Mano** (arrastrar mueve la vista). Donde dos clips de una pista se pisan, el clic derecho → **Traer al frente**
elige cuál se ve. **Invertir** (clic derecho en un clip de audio) lo hace sonar de atrás para adelante, sin tocar el
archivo; no aplica a clips con video. **Bloquear clip** o **Bloquear pista** (clic derecho) deja una toma que ya quedó bien fija: no se
mueve, no se recorta, no se borra y grabar no la pisa; el volumen, los fades y el encuadre se siguen tocando.

Cada pista o carpeta puede tener su propio alto: arrastrá el borde de abajo de su encabezado (doble clic en el borde
la vuelve al alto de siempre). El botón ↺ al pie de los encabezados iguala todas. Las carpetas llevan una perilla por
envío a bus debajo del nombre, como las pistas.

## Cómo está hecho

```
src/
  XtremeDaw.Audio      motor de audio: DSP, ASIO dúplex, click, grabación a disco, exportación
  XtremeDaw.Projects   modelo del proyecto, guardado atómico, historial, gestor de proyectos
  XtremeDaw.App        la ventana (Avalonia 11.3)
  XtremeDaw.Lab        xtlab.exe: consola para probar audio, DSP y proyectos sin la ventana
  XtremeDaw.Bench      xtbench.exe: XT-WorkBench, el Banco de pruebas (un programa aparte: compilar XTreme no lo cierra)
  XtremeDaw.VstHost    xtvsthost.exe y xtvsthost32.exe (C++): abren un plugin VST3 o VST2 en su propio proceso
tools/                 ícono, acceso directo y utilidades de verificación
```

Las dependencias van hacia abajo (App → Projects → Audio): el motor y el modelo del proyecto no conocen la
interfaz, y todo lo que hacen se puede ejercitar desde `xtlab`. El proyecto se guarda en una carpeta:

```
Nombre.xtp               el proyecto (JSON); doble clic lo abre
Nombre.xtp.bak           el guardado anterior
historial/               copias para volver atrás
media/                   grabaciones e importados (nunca se reescriben); mientras se graba, los diarios de las tomas y las notas de las MIDI (.notas)
cache/                   tomas transpuestas y estiradas por el tempo (se rehacen solas si se borran)
freeze/                  renders de las pistas congeladas
exports/                 videos y audios exportados
```

## xtlab

`xtlab.exe` (`src\XtremeDaw.Lab\bin\Debug\net10.0-windows\xtlab.exe`) prueba el motor y los proyectos desde
la consola, sin abrir la ventana.

| Comando | Qué hace |
|---|---|
| `dispositivos` / `panel` | drivers ASIO y dispositivos WASAPI / panel del driver (tamaño de buffer) |
| `latencia [--modo cable\|tocando] [--ver] [--borrar]` | mide la latencia ida y vuelta, o muestra / borra la guardada |
| `grabar --monitor --sat valvula` | graba sobre el click; con `--monitor` te escuchás con el efecto (también `--ganancia`, `--descartar`, `--entradas 1,2,3-4`) |
| `mezclar proyecto\|toma.wav [--salida mezcla.wav] [--cadena vida,comp,eq,puerta,reverb,limitador…]` | renderiza la mezcla offline, con el mismo grafo que la reproducción; con un WAV, `--cadena` arma los slots en ese orden (se pueden repetir) |
| `procesar toma.wav --sat cinta --comp-estilo punteo --eq ritmica --reverb cuarto --delay punteada` | aplica efectos a un WAV (`--comp-estilo` también toma el id de cualquier preset, como `vocal-pop`; `--comp-analizar` lo mide contra la toma, sin eso van sus valores fijos) |
| `click` | escribe todos los sonidos de click a un WAV |
| `sistema [--segundos 5] [--instalar-ffmpeg]` | CPU, GPU y RAM, si FFmpeg está en el PATH y con qué placa de video exporta; `--instalar-ffmpeg` lo instala antes con winget, como el aviso de la ventana |
| `version` | esta versión contra la última release de GitHub (lo que avisa la ventana y muestra "Acerca de") |
| `reporte enlace "título" [--texto t] [--idea]` / `reporte comprobar` | el enlace al issue nuevo que abre "Enviar comentarios", sin abrir nada; `comprobar` prueba textos, codificación y largo |
| `lista [ver \| agregar "qué probar" \| arreglado número [--texto t]] --archivo x [--copia nombre]` / `lista comprobar` | el Banco de pruebas de las copias de prueba (la usa `tools\probar.ps1`); `comprobar` la prueba sobre un archivo temporal |
| `lista pedir "qué" --tipo error\|mejora\|funcion\|idea \| tomar número \| soltar número --archivo x` | los pedidos del usuario: anotar uno, reservarlo para una sesión o devolverlo |
| `lista tema número tema\|ninguno \| temas --archivo x` (y `--tema t` en `agregar` y `pedir`) | el tema de un ticket, de la lista fija; `temas` la muestra con qué va en cada uno |
| `lista ticket número \| comentar número "qué" \| titulo número "título" --archivo x` | un ticket entero, con su conversación; decir algo en él sin cambiar su estado; ponerle título (vacío lo saca) |
| `lista historial [número] --archivo x` | quién hizo qué y cuándo con un ítem; sin número, lo último de toda la lista |
| `atajos comprobar` | que ninguna distribución de teclas de otro DAW (`KeyLayouts`) repita una tecla |
| `espectro [toma.wav]` | el espectro en vivo del EQ |
| `comprobar-dsp` | comprobaciones propias del DSP: remuestreo, tomas, reinicio de la cadena, afinador, LUFS y mastering (bandas que suman plano, reducción por banda sin importar el nivel, Cinta, Ancho (graves mono y mismo volumen), soft clip, limitador prendido y apagado) y slots de efectos (un canal viejo migrado suena idéntico a la cadena fija, repetir, reordenar, cambio de lista sin saltos, el bypass de FX los apaga sin tocar el canal), puerta de ruido (baja el zumbido, deja pasar la nota, no aletea cuando una nota se apaga), automatización de pistas, carpetas y buses (fader, paneo, mute, envíos, perillas de efectos, guardar, tempo, deshacer); además Nitidez, Claridad y Resplandor (un cuadro plano queda igual, un borde se marca, una luz en lo oscuro deja su halo de su color, los looks HDR y Soñado suman lo suyo solo con el filtro prendido) |
| `looks [--look id] [--salida look.cube] [--blancos n] [--negros n] [--luz n]` | los looks de color con colores de prueba, comprobación de rango y, con `--look`, el LUT `.cube` de ese look con Blancos, Negros y Sensación de luz |
| `masterizar archivo.wav [--cuanto 0.35] [--banda-graves\|--banda-medios\|--banda-agudos 0..1] [--corte] [--barro] [--graves] [--presencia] [--aire] [--inclinacion] [--cinta] [--ancho] [--graves-mono] [--recorte] [--empuje] [--techo] [--release] [--sin-limitador] [--volumen youtube\|instagram\|tiktok\|spotify\|apple] [--pua 0..1] [--look natural\|glue\|bright\|warm\|front\|pick [--cuanto-look 1]] [--sugerime]` | el mastering de la ventana sobre un WAV: cuánto comprime cada banda (y la Púa), cuánto recortan el soft clip (Auto incluido) y el limitador y los LUFS de entrada y salida; `--volumen` es el Destino del master (mide el archivo y usa esa ganancia en vez del Empuje), `--look` pone un look, `--sugerime` muestra qué oye y qué propone, y masteriza con eso |
| `midi ver\|pista\|clip\|nota\|acordes\|quitar carpeta` | pistas MIDI, sus clips y sus notas (`clip --pista n --en 0 --compases 4`, `nota --clip id --altura C4 --pulso 0 --largo 1 --vel 100`, `acordes --clip id` escribe Do, Sol, Lam, Fa); `mezclar carpeta` las renderiza |
| `midi patron carpeta --clip id [--nombre x] [--compases 4] [--repetir 2]` (o `--clips a,b [--separados]`: varios clips, un patrón del largo de todos juntos o uno por clip), `patron-insertar --clip id --pista n [--en 0]`, `patron-instrumento --clip id --pista n` (o `--quitar`), `patron-unico --clip id`, `patron-disolver --clip id`, `patron-borrar --clip id` | hace de un clip un patrón (clips enlazados, una vuelta que se repite), suma otro clip del patrón, suma o quita un instrumento, saca uno sonando igual, termina el patrón dejando todos sus clips únicos o lo borra con sus clips |
| `midi acorde carpeta --clip id [--raiz C4] [--tipo scale] [--inversion 0] [--pulso 0] [--largo 1] [--vel 100]` | un acorde en el clip; `scale` y `scale7` lo sacan de la escala del proyecto |
| `midi exportar-mid carpeta --clip id --a x.mid`, `importar-mid carpeta --de x.mid [--pista n] [--en 0]` | el clip como `.mid` (tipo 1, al tempo del proyecto); un `.mid` adentro: con `--pista`, todo en un clip de esa pista MIDI, sin ella una pista nueva por cada pista del archivo (en un tipo 0, una por canal) |
| `midi herramienta carpeta --clip id --tool arpeggiate [--rate 5 …]` | corre cualquier herramienta del piano roll (`MidiTools.All`) con sus ajustes por id |
| `midi partitura carpeta --clip id --a x.pdf` | el clip como partitura en PDF: doble pentagrama, 4/4, cuantizado a 1/16, con la armadura de la escala del proyecto si es mayor o menor natural |
| `midi escala carpeta [--raiz C4] [--escala minor]` | la escala del proyecto; sin `--escala`, ninguna |
| `midi ajustar-escala carpeta --clip id` | lleva a la escala del proyecto las notas que quedaron afuera |
| `midi cuantizar carpeta --clip id [--grilla 1/16]` | mueve el comienzo de las notas a la grilla de la canción (bar, 1/2 a 1/64, tresillos 1/2T a 1/32T, puntillos 1/2D a 1/16D, que empiezan en cada compás) |
| `midi velocidad carpeta --clip id [--rango 100]` | escala las velocidades alrededor de su promedio (50 las acerca, 0 las empareja, 150 da más dinámica) |
| `midi humanizar carpeta --clip id [--cantidad 10] [--semilla n]` | mueve cada velocidad al azar hasta ± cantidad |
| `midi entradas` | los teclados y controladores MIDI que ve Windows, con sus ajustes de Configuración → MIDI |
| `midi escuchar [--entrada nombre] [--segundos 10]` | abre las entradas como la ventana e imprime lo que llega y lo que toma el motor (canal y curva de velocidad aplicados) |
| `midi teclas [--segundos 10]` | las teclas físicas de la computadora como las lee el teclado virtual, por Raw Input |
| `midi instrumento carpeta [--pista n] --plugin "nombre" \| --quitar` | el instrumento VST3 que toca la pista; `mezclar carpeta` lo renderiza con el plugin, como la exportación |
| `midi sampler carpeta [--pista n] --sample archivo \| --quitar` | la pista toca ese archivo con el sampler nativo |
| `midi granular carpeta [--pista n] --sample archivo \| --quitar` | la pista toca ese archivo con el granular nativo |
| `midi gm carpeta [--pista n] [--programa 0] [--bateria] \| --quitar` | la pista toca con el instrumento General MIDI nativo (un programa 0..127 o un kit de batería) |
| `gm presets` / `gm zonas` / `gm [--banco musescore\|fluidr3\|archivo.sf2] [--programa 0] [--bateria] [--nota 60] [--velocidad 100] [--segundos 2] [--salida x.wav]` | lista los presets del banco GM (el de la app, o el de `--banco`), las zonas que suenan con esa nota y velocidad, o toca una nota y dice el pico |
| `gm comprobar` | las filas General MIDI de `comprobar-dsp`: un SF2 armado ahí (afinación, loop, release, bloques, clase exclusiva), un .mid con programas y batería, y el banco de la app |
| `granular archivo [--nota 60] [--segundos 4] [--modo stretch\|scrub] [--posicion 0] [--velocidad 1] [--densidad 20] [--tamano 0.08] [--spray 0] [--jitter 0] [--salida x.wav]` | toca un archivo con el granular en una nota: dice la raíz que encontró y el pico |
| `sampler archivo [--nota 60] [--raiz n] [--segundos 2] [--filtro lp24] [--corte 2000] [--salida x.wav]` | toca un archivo con el sampler en una nota: dice la raíz que encontró y el pico |
| `vst estado "nombre"` | carga el estado del plugin en una copia nueva y comprueba que vuelve igual, como al reabrir un proyecto |
| `vst miniatura "nombre" [--salida x.png] [--ya]` | abre la ventana del plugin y guarda su miniatura como la app (espera a que se quede quieta; con `--ya`, lo que muestra al abrir) |
| `vst escanear [--todo] [--archivo X]` / `vst lista` | busca los plugins VST3 y VST2, de 64 y 32 bits (cada archivo en su propio proceso; los recuerda en `plugins.json`; `--archivo` reintenta uno) / los lista |
| `vst carpetas [--agregar X] [--sacar X]` / `vst ocultar\|mostrar "nombre"` | dónde se buscan los plugins y cuáles salen en los menús (lo mismo que Configuración → Plugins) |
| `vst agrupar [marca\|categoria\|lista]` / `vst categoria "nombre" [--poner X \| --auto]` | cómo agrupan los menús (y la lista así agrupada) / la categoría de un plugin: ponerle una propia o volver a la automática |
| `vst probar "nombre" [--segundos 4] [--ventana] [--espera s] [--tiempo-real]` | toca un acorde (o un ritmo, si es una batería) con el plugin a un WAV, por el mismo puente que la exportación; `--ventana` abre antes su ventana y `--espera` limita cuánto espera a que suene (10 s, como la exportación; 0 = no espera). Con la variable `GCVST_DEBUG=1`, el puente cuenta sus salidas y cada bloque |
| `colas [--proyecto carpeta] [--bloque 192] [--segundos 3] [--silencio 120] [--paso 5] [--nivel 0.1] [--caida dB/s]` | lo que cuestan por bloque los buses y el master mientras se apagan sus colas; `--caida` apaga la entrada de a poco, como la cola de un instrumento |
| `vst vivo "nombre" [--segundos 4] [--bloque 256] [--tope 256] [--espera 0] [--desde 0] [--sin-carga] [--apuro 1] [--estado archivo] [--prioridad] [--mmcss] [--ventana] [--salida x.wav]` | toca el instrumento como la reproducción, en bloques del driver a tiempo real, y cuenta los bloques perdidos y los que salieron con sonido, lo que tarda cada bloque en el hilo de audio y en el del plugin, y la CPU de su proceso. `--tope` es el bloque máximo que se le anuncia (por defecto, el del driver), `--prioridad` y `--mmcss` suben este hilo a la prioridad de uno de driver. `--desde` arranca en ese segundo (en medio de una nota), `--espera` lo deja parado antes, `--sin-carga` no espera a que suene y `--apuro 20` adelanta los bloques para que el plugin llegue siempre tarde |
| `vst bucle "nombre" [--bloque 256] [--vueltas 4]` | un bucle en un instrumento, en bloques del driver a tiempo real: mide cuánto se corre el primer tiempo de cada vuelta (sabiendo el bucle de antes y como un salto) y que una nota que cruza B no quede prendida |
| `vst efecto "nombre" [--entrada x.wav] [--segundos 4] [--bloque 256] [--tarde] [--toma] [--apagado]` | pasa audio (o acordes de prueba) por un efecto como la exportación y en vivo, en bloques del driver a tiempo real; escribe los dos WAV y comprueba que en vivo llega justo un bloque después (lo que cuenta la compensación de latencia). `--tarde` llama cada bloque apenas terminó el anterior, así el plugin nunca llega a tiempo: tiene que salir entero, un bloque más tarde |
| `vst parametros "nombre" [--mover "parámetro"] [--segundos 2]` | lista lo que el plugin deja automatizar; con `--mover`, pasa un carril de 0 a 1 por ese parámetro como la exportación y comprueba que el sonido cambia |
| `midi comprobar` | el sonido de las notas: igual en cualquier tamaño de bloque, termina con el release, la exportación suena como la reproducción y el clip pasa a notas con el tempo; y el teclado: una tecla suena como la nota escrita, el pedal, las tomas, cuantizar y el clip grabado; la toma cortada: mata un proceso a mitad de toma (`midi cortar carpeta`) y comprueba que al abrir vuelvan las notas; dividir y borrar rango con y sin patrón, notas que cruzan el corte y deshacer; y el tono de prueba por la cadena de la pista, sin asignar (también corre en `comprobar-dsp`) |
| `transponer archivo.wav [--semitonos 2] [--salida x.wav]` | cambia el tono sin cambiar el largo, como el Transponer de la pista (de −12 a +12, admite cents) |
| `estirar archivo.wav [--razon 1.25 \| --de 120 --a 100] [--semitonos 0] [--salida x.wav] [--comprobar]` | cambia el largo sin cambiar el tono, como un clip que sigue el tempo; `--comprobar` corre solo sus filas de `comprobar-dsp` |
| `proyecto editar carpeta tempo bpm` | cambia el tempo con todo en su compás y deja los clips estirados en `cache/` |
| `proyecto editar carpeta tempo-mapa [tiempo:bpm,…] [--compas compás:n/d,…]` | la pista de tempo: cada punto en tiempos (negras desde 0), con `~` al final es una rampa hasta el siguiente (`0:120,16:90~,32:140`); los compases cambian al empezar uno (`0:4/4,8:7/8`). Lo MIDI, las pistas que siguen el tempo, marcadores, acordes y el loop quedan en su compás; sin nada, muestra el mapa |
| `proyecto editar carpeta sigue-tempo pista si\|no` | una pista de audio que sigue el tempo: sus clips se mueven y se estiran con el mapa (por defecto quedan en segundos) |
| `proyecto editar carpeta transponer pista semitonos` | el Transponer de esa pista; deja sus tomas transpuestas en `cache/` |
| `tiempos-carga archivo` | cuánto tarda cada paso de cargar un audio: decodificar, remuestrear y analizar |
| `samples listar carpeta [--buscar "kick 808"]`, `samples leer archivo` | los samples de una carpeta, como los busca la Biblioteca; decodifica un archivo y dice si fue nativo o por FFmpeg |
| `lufs archivo` / `afinar archivo [--en segundos] [--umbral -50]` | volumen integrado de un archivo / la nota que oye el afinador en ese momento, con la sensibilidad de la ventana (dB) |
| `tempo archivo [--desde s] [--hasta s]` | los BPM de un archivo (o de ese tramo), su primer golpe y qué tan seguro está, como "Detectar tempo" en el clic derecho de un clip |
| `escala archivo [--desde s] [--hasta s] [--callado] [--espectro] [--bajo stem.wav]` | la escala de un archivo (o de ese tramo), sus notas, la afinación y los acordes con su tiempo, como "Detectar escala y acordes" en un clip; `--espectro` no prueba otros métodos; `--bajo` compara los acordes con ese stem de bajo; `--comprobar` corre solo sus comprobaciones |
| `balance archivo [--objetivo pop\|rock\|edm\|hiphop\|acoustic\|orchestral\|dubstep\|riddim\|deepdubstep\|…] [--referencia a.wav,b.mp3] [--modo estereo\|mid\|side] [--curva] [--comprobar]` | el Balance tonal del archivo contra un objetivo: las cuatro bandas (20–120 Hz, 120 Hz–1 kHz, 1–5 kHz, 5–20 kHz) y el punch de los graves; `--referencia` arma el objetivo con esos archivos, `--curva` lista los 61 puntos con su rango, `--comprobar` corre solo sus comprobaciones |
| `extraer-midi archivo [--melodia] [--desde s] [--hasta s] [--inicio 0.5] [--nota 0.3] [--min-ms 70] [--midi x.mid] [--bpm 120] [--verdad notas.json] [--callado]` | las notas de un archivo, como "Extraer MIDI" en un clip; `--midi` las guarda en un .mid y `--verdad` las puntúa contra notas conocidas (`[[inicio s, largo s, nota], …]`); `--comprobar` corre solo sus comprobaciones |
| `separar archivo [--stems 2\|4\|6] [--calidad rapida\|alta\|maxima] [--modelo general\|porstem\|6] [--solo voz,bateria,bajo,guitarra,piano] [--pasadas n] [--sin-desplazar] [--desde s] [--hasta s] [--salida carpeta] [--cancelar s] [--cpu]` | separa el archivo y deja un WAV por stem, como "Separar en stems" en un clip; `--sin-desplazar` hace una pasada sin correr el audio, para comparar versiones; `--modelo`, `--solo` y `--pasadas` son el modo Avanzado del diálogo (el modelo manda sobre `--stems` y `--calidad`); `--comprobar` corre solo sus comprobaciones |
| `nitidez [video\|imagen] [--en 1] [--nitidez 60] [--claridad 60] [--resplandor 0] [--look hdr\|dreamy] [--intensidad 100] [--alto 1080] [--ancho-preview 640] [--carpeta dir]` | compara Nitidez, Claridad y Resplandor (y lo que suman los looks HDR y Soñado) de la exportación (FFmpeg) con los de la preview sobre el mismo cuadro, a tamaño de exportación y al ancho que muestra la preview; mide cuánto cuestan por cuadro y deja PNG para mirarlas |
| `leer-video archivo [--cpu] [--saltos 20] [--cuadro 12.5 --salida x.bmp] [--comparar]` | mide el decodificador de la preview (FFmpeg dentro de la app): abrir, leer seguido, saltar y memoria por cuadro; `--comparar` lo mide contra ffmpeg.exe (tiempo por salto y diferencia de imagen) |
| `camara [listar]` | las cámaras que ve FFmpeg y sus formatos |
| `camara grabar [--dispositivo X \| --ip 192.168.1.50:4747] [--segundos 5]` | graba video como una toma y lo revisa con ffprobe |
| `camara verificar-corte` | corta una grabación a la mitad y comprueba que el video quede |
| `exportar [proyecto] [--calidad alta\|estandar\|borrador] [--salida nombre]` | exporta el MP4 |
| `exportar proyecto --audio wav\|mp3\|flac\|aiff\|ogg` | exporta solo la mezcla |
| `exportar proyecto --audio … --bits 16\|24\|32 --frecuencia 44100\|48000\|96000 [--sin-dither] [--kbps 128..320\|vbr] [--cola-fija 4] [--normalizar -1] [--mono]` | el "Avanzado" de Exportar audio (por defecto, lo último que eligió el proyecto) |
| `exportar proyecto … --stems [--stems-buses Reverb,Delay]` | además, un WAV por pista en `<nombre> - stems` (y uno por esas carpetas o buses) |
| `exportar proyecto … --rango 12-45\|1:05-1:30\|loop` | solo ese tramo (`loop` = el A–B del proyecto), con la cola de efectos después de B |
| `exportar proyecto … --codificador auto\|cpu\|nvenc\|amf\|qsv` | quién codifica el video (auto = la placa de video si hay) |
| `exportar proyecto … --resolucion 720\|1080\|1440\|2160 --bitrate 20` | el "Avanzado" del diálogo: lado corto del cuadro y bitrate del video en Mbps |
| `exportar proyecto … --destino reels\|youtube\|ig-square\|ig-portrait [--volumen como-esta\|youtube\|…] [--lufs -14] [--cola 8]` | con el cuadro de esa red; el volumen es el Destino del master del proyecto (`--volumen` lo cambia para esta exportación, `--lufs` lleva la mezcla a ese volumen integrado), `--cola` deja sonar reverb y delay hasta N s después del último clip (0 = corta). Muestra los LUFS del resultado |
| `proyecto abrir\|clips\|versiones\|volver carpeta` | abre un proyecto, lista pistas y clips, y recorre el historial |
| `proyecto guardar-version carpeta [nombre] \| nombrar-version carpeta n [nombre]` | guarda una versión ahora / le pone nombre a la n de la lista (con nombre no se borra sola; sin nombre se lo saca) |
| `proyecto toma\|cortar carpeta [--camara]` | graba un tono como la ventana / lo corta sin cerrar, para probar la recuperación |
| `proyecto editar carpeta mover\|recortar-inicio\|recortar-final\|estirar-inicio\|estirar-final\|fade-in\|fade-out clip delta` | edita un clip como en el timeline (más: `a-pista`, `dividir`, `duplicar` (varios ids con comas; `--iman 1/4` corre las copias por pasos del imán), `pegar`, `ganancia`, `curva`, `invertir`, `tono`, `quitar-clip`, `nueva-pista`…) |
| `proyecto mezcla carpeta [ab\|copiar\|olvidar]` | el A \| B del mixer: pasa a la otra mezcla, copia esta sobre la otra u olvida la otra |
| `proyecto verificar-ab` | comprueba el A/B de la mezcla |
| `proyecto editar carpeta bloquear\|desbloquear clip\|pista` | fija un clip (o toda la pista): no se mueve, no se recorta, no se borra y grabar no lo pisa |
| `proyecto verificar-bloqueo` | comprueba la regla del bloqueo |
| `proyecto verificar-bucles` | comprueba los bucles de tomas, clips MIDI y bloques de automatización: repeticiones, que sigan al original, dividir y guardar |
| `proyecto editar carpeta transicion clip corte\|fundido\|negro [--segundos 0.5]`, `proyecto transiciones carpeta` | la transición que entra a ese clip (imagen y sonido) y la lista de uniones de cada pista |
| `proyecto editar carpeta nueva-carpeta\|a-carpeta\|sacar-de-carpeta\|mover-carpeta\|quitar-carpeta\|carpeta …` | carpetas de pistas: agrupar, mover adentro y afuera, renombrar y plegar |
| `proyecto editar carpeta nuevo-separador [nombre] [--en n] [--visible] \| lado id izquierda\|derecha` | separadores con nombre (nacen ocultos en el timeline salvo `--visible`; `carpeta id --ocultar` lo alterna) y de qué lado del mixer va cada tira |
| `proyecto editar carpeta guardar-plantilla pista [nombre] \| usar-plantilla nombre \| aplicar-plantilla pista nombre`, `proyecto plantillas-de-pista x` | plantillas de pista (`pistas.json`) |
| `proyecto encuadre\|sincronia\|formato\|modo carpeta …` | encuadre del clip, ajuste de sincronía, formato del video y modo Video / Solo audio |
| `proyecto importar-audio carpeta archivo` | copia un audio de afuera a `media/` (con `--vincular`, lo deja donde está) y lo pone en una pista |
| `proyecto marcadores carpeta [agregar\|mover\|renombrar\|quitar … \| capitulos]`, `proyecto repetir carpeta [desde hasta \| --quitar]` | marcadores del tema y rango A–B del loop (`capitulos`: los marcadores como capítulos de YouTube) |
| `proyecto duracion carpeta` | cuánto dura el tema y si entra en cada red (historias, X, Reels · Shorts, TikTok) |
| `proyecto historial carpeta` | tomas en uso y no usadas, exportaciones y actividad |
| `proyecto insertos carpeta pista [agregar vida\|comp\|eq\|puerta\|reverb\|delay\|limpieza\|multibanda\|color\|cinta\|ancho\|softclip\|limitador\|chorus\|flanger\|phaser\|tremolo\|bombeo\|fet\|optico\|vca-bus\|vca\|varimu [--en n] \| agregar vst --plugin "nombre" \| quitar n \| mover n m \| duplicar n \| prender n \| apagar n \| plegar n \| sidechain n pista\|propia]` | los slots de efectos de la pista en su orden; sin operación, los lista (`plegar` lo pliega en el mixer; `sidechain` elige la pista que escucha un compresor, una puerta o un bombeo) |
| `proyecto insertos carpeta --bus id\|n [agregar … \| quitar n \| …]`, `proyecto insertos carpeta [agregar … \| cadena \| quitar n \| …] --master` | lo mismo para un bus o el master; `cadena` (solo `--master`) es "Cadena completa": agrega las etapas que falten |
| `proyecto insertos carpeta --carpeta id\|n\|nombre [agregar … \| quitar n \| …]` | los slots de una carpeta (van sobre la suma de sus pistas) |
| `proyecto buses carpeta --carpeta id\|n\|nombre [volumen x\|paneo x\|mute\|solo]` | el canal de una carpeta; sin operación lo muestra |
| `proyecto buses carpeta [agregar [nombre] [--tipo reverb\|delay\|vacio] \| quitar n \| renombrar n nombre \| volumen n x \| paneo n x \| mute n \| color n #rrggbb\|ninguno \| icono n id\|ninguno]` | los buses, en el orden en que suenan, con sus slots, su salida y sus envíos; quitar uno se lleva los envíos que le llegaban |
| `proyecto envios carpeta quien [bus [nivel] [--pre\|--post] [--apagar\|--prender] [--quitar\|--sumar]] [--salida bus\|master]` | la salida y los envíos de una pista (número o id) o `carpeta:n`; un bus (`bus:n`) manda a otros buses (no a sí mismo ni cerrando un bucle) y su salida va solo al master; `--quitar`/`--sumar` saca o vuelve a poner un bus en su lista |
| `proyecto vinculados carpeta`, `proyecto copiar-vinculado carpeta medio`, `proyecto reencontrar carpeta medio --archivo X`, `proyecto reencontrar carpeta [medio] --buscar-en Y` | dónde están los archivos de cada toma, copia a media/ lo vinculado y vuelve a encontrar lo que falta |
| `proyecto cortar-copia carpeta medio [--al-final]` | copia y mata el proceso a la mitad (o al terminar, sin guardar) |
| `proyecto verificar-deshacer\|verificar-pasos\|verificar-clips\|verificar-tomas\|verificar-importar\|verificar-vinculados\|verificar-historial\|verificar-carpetas\|verificar-marcadores\|verificar-tempo\|verificar-paquete\|verificar-guardar-como\|verificar-sin-guardar` | pruebas automáticas sobre una carpeta temporal |
| `proyectos lista\|plantillas\|nuevo\|renombrar\|duplicar\|borrar\|olvidar\|carpeta\|buscar` | lo que hace el gestor de proyectos (`buscar`: qué abre una carpeta, un .xtp o un guitarcam.json de WildTrack elegido) |
| `proyectos empaquetar carpeta [--completo] [--salida x.zip] \| desempaquetar x.zip` | el proyecto en un ZIP (para compartir: lo que suena; `--completo`: también las tomas sin usar, las versiones y las exportaciones) y de vuelta a un proyecto nuevo |
| `proyectos guardar-plantilla\|borrar-plantilla` | plantillas de proyecto del usuario |
| `proyectos plantillas-video\|guardar-plantilla-video\|aplicar-plantilla-video\|borrar-plantilla-video` | plantillas de video (filtro, ajustes y efectos) |

En `proyecto` y `proyectos`, `--datos X` usa otra carpeta para la configuración, los recientes y las
plantillas (para probar sin tocar los tuyos), y `--raiz X` otra carpeta de proyectos.

`xtlab componentes [estado | instalar id [--paquete p] [--url u] | comprobar]`: estado de los componentes opcionales, instalación y prueba de la descarga contra un servidor local.

## Modelos de Separar en stems

Los modelos son de Meta (Demucs, https://github.com/facebookresearch/demucs). XTreme no los incluye ni los aloja: se bajan de su fuente original a tu PC cuando los instalás. Tienen sus propios términos y no están cubiertos por la licencia de XTreme. La app funciona sin ellos.
