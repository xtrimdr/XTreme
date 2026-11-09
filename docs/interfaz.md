# Interfaz — cómo es cada parte de la ventana

Referencia para agentes: qué hay en cada parte de la ventana, cómo se comporta y en qué archivo vive. No hace falta
leerla entera: buscá el apartado de lo que vas a tocar. Las reglas generales de diseño (tokens, tipografía, clases,
atajos) están en [src/XtremeDaw.App/AGENTS.md](../src/XtremeDaw.App/AGENTS.md). Si cambiás cómo funciona algo de
acá, actualizá su apartado en el mismo cambio.

La captura de referencia es [captura.png](captura.png).

Todo lo que se describe acá lo decidió el usuario: no lo cambies sin preguntar, aunque otro DAW (o la web
de donde nació la app) lo haga distinto.

## Splash (`SplashWindow`)

Al abrir la app, mientras se arma la ventana principal: ícono grande, el bloque de título (`Controls/BrandTitle`, compartido con "Acerca de": "XTREME" en Bahnschrift Bold con la X en el acento, una tira estática de barras como una onda y la frase en mayúsculas espaciadas; Bahnschrift viene con Windows 10 en adelante, sin ella cae a Segoe UI), versión con el commit
("PRUEBA" en una copia de prueba) y una franja del acento que corre por el borde de abajo (animación de composición:
sigue moviéndose aunque el hilo de la UI esté ocupado). Toma los colores del tema, dura al menos 3 s (pedido del usuario), queda
encima de la ventana principal mientras esta carga detrás y se va con un fundido. Excepción a "sin gradientes": el brillo del acento detrás del ícono y la franja
(`BrandArt`, compartido con "Acerca de").

## Estructura

```
┌ barra de título propia (44) ─ logo · nombre · (PRUEBA) ······················ CPU │ GPU │ RAM │ DSP │ FFmpeg · 🔔 · ◧ ◨ · diseño · tema · ? · ⚙ · [_ □ ×] ┐
├ fila de herramientas (~40) ─ Video|Solo audio · chip del proyecto (guardado) ▾ │ guardar · deshacer/rehacer ···· [↖ ⌶│✂ ⌫│🎧 🔍 ✋] ···· [Exportar MP4] ┤
├ inspector (290↔) ┬─ preview (un panel por pista con video) ────────┬─ "En vivo" (256) ─┬─ proyecto (290↔, plegable a 44) ┤
│ TOMA + secciones │                                                 │ cámara + entrada  │ pestañas Color / Historial     │
├──────────────────┴─ separador arrastrable ─────────────────────────┴───────────────────┤                                │
│ timeline (280): regla (32) · encabezados fijos (94–640, 324) · un carril por pista (64, 40–320) │                                │
├ transporte, siempre abajo (~52) ────────────────────────────────────────────────────────────────────────────────────────────┤
│ ·························· medidor de entrada (kHz · buffer debajo) · Monitor  [ reloj │ ⏮ ■ ▶ ● loop │ Compás Tempo TAP Cuenta Click ▾ ] ··· Timeline/Piano roll/Mixer/Mastering · Ver todo │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Ese es el diseño **Clásico**. Para el **Vertical**, ver el apartado que sigue.

## Diseño de la ventana (`MainWindow.Layout.cs`)

- Botón de la barra de título (ícono de mosaico, antes del tema) con un menú: **Clásico**, **Vertical, video a la
  izquierda** y **Vertical, video a la derecha**, cada uno con un dibujito y el elegido en acento. Es de la app, no del
  proyecto (`config.json`: `layout`), pedido del usuario.
- Vertical: la columna del video (formato, preview y "En vivo" debajo de la preview) va de arriba a abajo entre el
  inspector y la barra del proyecto, a la izquierda o a la derecha de la timeline; la timeline ocupa el resto y el
  mixer (o Mastering) se abre debajo, igual que en solo audio (`MoveBottomView`, `stackedViews`), sin mini timeline.
  El ancho de la columna se arrastra desde su borde interior (400–1000, 460 por defecto, doble clic vuelve;
  `stageWidth`) y se achica sola si la ventana no da, para que la timeline conserve 360.
- En solo audio no hay video: el diseño no cambia nada y se ve como siempre, y el botón queda desactivado con un
  tooltip que dice por qué (pedido del usuario).
- Al pasar el proyecto a 9:16 en Clásico, un toast ofrece el Vertical (una vez por sesión); nunca cambia solo.
- **Maximizar un panel** (Enter, atajo `maximize`, o el botón junto a las vistas del transporte, `MainWindow.MaxPanel.cs`;
  pedido del usuario, como en FL Studio): le da toda la zona de trabajo al panel que se tocó último (`pressedPanel`): la
  preview, sin "En vivo", o la vista (timeline, piano roll, mixer o mastering; con la mini timeline en estas). Las barras
  de título y de transporte quedan: maximizado, los botones de vista cambian lo que llena el lugar (con la preview
  maximizada, pasan a esa vista). Usa la disposición apilada de solo audio (`MoveBottomView`, `ApplyViewHosts`), así las
  alturas recordadas no se tocan. B y P traen las barras laterales sin guardarlo; otro Enter vuelve todo como estaba. No se
  recuerda entre sesiones.
- Los paneles (InspectorBar, StageArea, AudioArea con TimelineHost y MixerHost, ProjectBar) se mueven sin
  reconstruirse; la barra de título y el transporte no se mueven. `ApplyLayout` decide dónde va cada uno: todo
  diseño nuevo pasa por ahí.
- **Paneles en su propia ventana** (`MainWindow.PanelWindows.cs`, pedido del usuario: "Todos los paneles. Todos."):
  inspector, preview con "En vivo", timeline, el lugar del mixer, el piano roll, Mastering y la barra del proyecto
  salen a una ventana aparte (el piano roll y Mastering cada uno a la suya, pedido del usuario: los tres a la vista;
  dejan el lugar del mixer, `mixerViews` / `mixerPins`, y al volver se muestran ahí) con el mismo criterio que el teclado virtual: un botón (`FloatButton`, ícono E8A7 / E944)
  siempre arriba a la derecha del panel (pedido del usuario): el último del encabezado del Canal y del Proyecto, de la
  barra de formato de la preview, del mixer y del piano roll; sobre el final de la regla en el timeline; en el título
  del medidor de Mastering (su columna de la derecha); y Ver → "Paneles en ventana" (con "Volver todos"). La ventana
  es independiente de la principal (pedido del usuario): tiene su botón en la barra de tareas y se minimiza, va atrás
  o adelante sola; se cierra con la principal. Lleva la barra de título de la app (oscura, el nombre a la
  izquierda, como el Banco de pruebas; doble clic la maximiza), pasa los atajos a `OnKey` y su X devuelve el panel. Se mueve el panel
  entero (de las barras laterales, su contenido; la barra queda oculta), así no pierde estado. Se recuerdan qué paneles
  están afuera (`panelsOut`) y lugar, tamaño y si estaba maximizada cada ventana (`panelWindow.<id>`); al arrancar
  vuelven a salir. Lo que queda ocupa el lugar libre (decisión del usuario): con la preview afuera la ventana se apila
  como en solo audio (`StageHere`), con el timeline afuera la fila de abajo muestra el lugar del mixer o se cierra
  (`BottomEmpty`). Mixer, piano roll y Mastering salen cada uno solo a su ventana (pedido del usuario, #139): el mixer
  se lleva su encabezado, las tiras y el master (`mixerPane`, con su propio mini timeline), y el lugar de abajo queda
  para los que siguen adentro; su botón de vista trae su ventana. Piano roll o Mastering afuera se ven siempre
  (`PianoShown`, `MasteringShown`): su botón de vista trae su ventana, y si estaban en el lugar, el lugar se cierra. Una barra lateral afuera se ve siempre abierta y sus
  botones de ocultar (y B, P) traen su ventana al frente. En una ventana de panel, Enter (`maximize`) la maximiza
  (decisión del usuario); mover un panel restaura antes el panel maximizado. En solo audio la ventana de la preview se
  oculta; la preview en una ventana alta pone "En vivo" debajo (`ArrangeStage`).

## Barra de título

- Son dos filas en un mismo `TitleBar` (para que todo entre con la ventana sin maximizar, pedido del usuario): la
  primera, de 44, es la barra de título (`ExtendClientAreaTitleBarHeightHint="44"`) con la app, la barra de menús,
  los medidores, los paneles, el diseño, el tema y el engranaje de Configuración (`SettingsButton`, pedido del usuario,
  2026-11-03: más a mano que Archivo → Configuración, que sigue. Configuración tiene arriba de las pestañas un **buscador**,
  #302, `Dialogs/SettingsDialog.Search.cs`: busca sin tildes ni mayúsculas en lo que muestra cada página, leído de sus
  controles, y en los nombres de todos los atajos; los resultados tapan la página, por pestaña, y las pestañas sin nada
  quedan tenues; un resultado abre su página, despliega su grupo y prende un momento su tarjeta; Ctrl+F, Enter abre el
  primero, Esc borra. Una tarjeta nueva se encuentra sola si va en `Card`); la segunda es la del proyecto: modo, chip, guardar/deshacer y Exportar a la derecha (ahí no
  hay botones del sistema, así que va sin el margen de 150). Las dos arrastran la ventana.
- En el medio de la segunda fila, las **herramientas del timeline** (`MainWindow.Tools.cs`, pedido del usuario): una
  caja como la de Video | Solo audio (`Border.mode` con `ToggleButton.mode` de 32 px, solo ícono) con Seleccionar ·
  Rango │ Dividir · Borrar │ Escuchar · Zoom · Mano. Queda **centrada en la ventana**, como el transporte; solo se
  corre (`PlaceTools`) si no entra entre deshacer y Exportar, y si ni así entra, el nombre del proyecto se recorta. Con el mixer en lugar del timeline se apaga.
- Es la de la app (`ExtendClientAreaToDecorationsHint`): arrastrable, doble clic maximiza, los botones del sistema
  quedan a la derecha (margen 150). `TransparencyLevelHint="None"` no se toca: con transparencia, píxeles oscuros
  del video dejan ver lo que hay detrás.
- La ventana abre donde quedó (`MainWindow.WindowPlace.cs`, `config.json` "window"): posición y tamaño normales y si
  estaba maximizada. Si ese lugar ya no cae en ningún monitor, abre centrada en 1440×900.
- **Pantalla completa** (F11, `MainWindow.FullScreen.cs`): tapa la barra de tareas y los botones del sistema; la barra
  de título queda (sin el margen de 150) y no arrastra; doble clic o F11 otra vez vuelven a como estaba (maximizada o
  no). No se recuerda entre sesiones: abre como estaba antes de la pantalla completa. La `TitleBar` de Avalonia va transparente y sin mouse
  (`TitleBar:fullscreen`; su `IsVisible` lo pone ella por código): si no, asoma arriba como una franja del acento.
- **Barra de menús** (`MainWindow.MenuBar.cs`, pedido del usuario, 2026-11-03: sin ella parecía una app de juguete):
  Archivo, Edición, Pista, Ver, Transporte, Herramientas y Ayuda, a la derecha del nombre. Reemplazó el menú del chip
  del proyecto y los íconos de Ayuda, Novedades y Enviar comentarios (el engranaje volvió). Cada menú se arma al abrirse (lo
  que no se puede hacer en ese momento sale deshabilitado) y muestra la tecla de cada acción, leída de `Shortcuts`. Las
  entradas llaman al mismo código que su atajo o su botón (`RunEdit` para la edición, los botones de vista y de
  exportar); Herramientas analiza el clip de audio elegido con las mismas entradas que su menú. Los títulos son botones
  (`Button.menubar`) que abren el mismo menú que el clic derecho del timeline (`Popup`, uno a la vez); con uno abierto,
  pasar a otro título abre ese. El `Menu` de Avalonia no tomaba los clics en la barra de título. Sin acceso por Alt.
- El logo es el ícono de la app y abre "Acerca de" (`Dialogs/AboutDialog`), con el mismo encabezado que el splash
  (`BrandArt`: logo grande, brillo y línea del acento del tema, versión en la esquina).
  Ahí dice si hay una versión nueva: 5 s después de abrir, `MainWindow.Updates.cs` le pregunta a GitHub por la última
  release (`UpdateCheck`, en `XtremeDaw.Projects`; sin cuenta ni datos del usuario, y si falla no avisa nada). Una
  versión nueva sale en un toast con "Descargar" solo la primera vez (`config.json` "updateNotified"); después, solo
  en "Acerca de". La casilla "Avisarme cuando salga una versión nueva" lo apaga (`config.json` "checkUpdates": "off").
- "Enviar comentarios" (`Dialogs/FeedbackDialog`, Ayuda → Enviar comentarios y el botón de "Acerca de";
  pedido del usuario): Error o Sugerencia, título, descripción y los datos que se agregan (versión, Windows, driver ASIO),
  a la vista y con casilla. "Continuar en GitHub" abre el issue nuevo ya lleno (`IssueReport`): la app no tiene token y el
  usuario lo envía con su cuenta. Lo que no entra en el enlace va por el portapapeles, con un toast. No abre grabando ni
  sobre Configuración o Exportar. Lo escrito queda hasta que el navegador abrió.
- FFmpeg solo aparece en la barra si falta ("FFmpeg no encontrado", `MainWindow.TopBar.cs`). Al abrir sin FFmpeg, y
  al hacer clic en ese chip, sale "Falta FFmpeg" (`Dialogs/FfmpegDialog`, pedido del usuario). Ofrece Instalar ahora
  (winget, `FfmpegSetup`, si está; se puede cerrar mientras instala y avisa con un toast), Abrir la página (gyan.dev),
  Ya lo instalé (relee el PATH y lo busca de nuevo) y el comando de winget para copiar.
- El chip del proyecto muestra el nombre y el estado del guardado; un clic abre el gestor. "Volver a una versión
  guardada" está en Archivo (`MainWindow.Versions.cs`), con las últimas 20 de `historial/` (`ProjectStore.Snapshots`,
  con su nombre si tienen; las mismas que Historial → Versiones). Volver guarda antes lo actual como otra versión,
  recarga el proyecto y borra los pasos de deshacer.
- **Al arrancar** (`StartupMode`, pedido del usuario, 2026-11-03): el gestor (por defecto) o un proyecto vacío sin
  lugar (`UntitledProjects`, `MainWindow.Untitled.cs`). Se elige con "Mostrar al arrancar" al pie del gestor o en
  Configuración → Guardado y exportación ("startup" en `config.json`). El proyecto sin lugar vive en
  `%APPDATA%\XTreme\sin-guardar\`, se guarda solo como cualquiera (lo grabado está en disco desde el principio) y no
  entra en recientes; arranca en Solo audio, como todo proyecto nuevo (pedido del usuario: el video se elige). Guardar (atajo, diskette o Archivo) pregunta dónde va
  (Guardar como) y sigue en esa copia; el chip dice siempre "Sin guardar", nunca Guardando ni Guardado (#306: ese
  guardado solo cuida lo grabado), y el diskette late mientras tenga algo. Cerrar la ventana con algo
  adentro pregunta (`UnsavedDialog`: Guardar…, o a la Papelera); vacío se borra sin preguntar. Si se abre otro
  proyecto, el sin lugar con algo queda en recientes. Si la app se cae, el próximo arranque en modo vacío lo abre de
  nuevo (`UntitledProjects.Leftover`, con un toast).
- El gestor siempre se puede cerrar (×, Esc), aunque no haya un proyecto abierto: entonces la ventana empieza en un
  proyecto vacío sin lugar (pedido del usuario: no tiene que obligar a crear uno).
- Doble clic en un `.xtp` (#254): el instalador asocia los .xtp (tarea "Asociar archivos .xtp a XTreme", marcada); Configuración → Asociación de archivos dice qué exe los abre y "Asociar archivos .xtp a XTreme" registra el que corre (`FileAssociation`, HKCU). Con XTreme abierto, el archivo va a esa ventana por un pipe por usuario y por exe (`ShellOpen`, `MainWindow.ShellOpen.cs`) y se abre en lugar del actual, como desde el gestor; no mientras graba o exporta.
- El gestor (`Controls/ProjectsDialog.axaml.cs`) lleva el logo de la app en el encabezado. "Abrir…" elige un `.xtp` (o un
  `xtreme_project.json` viejo) de cualquier carpeta; el formulario de proyecto nuevo tiene "Guardar en…" (una carpeta vacía
  es la del proyecto, en otra se crea una adentro). El botón de guardar de cada fila abre "Guardar como…" (sigue en la copia)
  y "Guardar una copia…" (sigue en este), también con Ctrl+Shift+S (`MainWindow.SaveAs.cs`: elige el archivo, guarda lo
  pendiente y copia en segundo plano con un toast de progreso). Empaqueta: el botón de paquete de cada fila abre "Para compartir" /
  "Respaldo completo" y pide dónde guardar el ZIP (`ProjectLibrary.Pack`; nunca dentro del mismo proyecto; el actual
  se guarda antes, `FlushCurrent`) y "Abrir paquete…" de la barra lo trae como proyecto nuevo, resaltado en la lista
  (`ProjectLibrary.Unpack`).
- Al lado del nombre, el estado del guardado (`TickTopBar`): Guardando / Guardado / Sin guardar / Error al guardar /
  Solo lectura. "Sin guardar" sale cuando Configuración → Guardado y exportación (`SaveMode`) guarda cada X minutos o a mano; el
  diskette antes de deshacer (`SaveButton`, el atajo "save") guarda ya y está apagado si no hay nada pendiente.
- Cerrar la ventana con cambios sin guardar (modo cada X minutos o a mano, o un guardado que falló) pregunta
  "¿Guardo los cambios?" (`Dialogs/UnsavedDialog`): Guardar y cerrar (Enter), Cerrar sin guardar, Cancelar (Esc).
  Si el guardado falla, no cierra. Después de cada edición no pregunta: lo pendiente se escribe y cierra.
- Una copia hecha por `tools\probar.ps1` lleva un `prueba.txt` al lado del exe (commit, con o sin cambios, hora):
  `TestBuild` lo lee y la barra muestra, después del nombre, el badge "PRUEBA a1b2c3d + cambios" (`TestBuildChip`,
  fondo `Hover`, la hora en el tooltip) y el título de la ventana lo repite. Sin ese archivo no aparece nada.
  Al lado, el botón del **Banco de pruebas** (`TestListButton`, con lo pendiente; `MainWindow.TestList.cs`) abre
  `Dialogs/TestListWindow`, que es un programa aparte (pedido del usuario, 2026-11-04: se abre desde XTreme y desde afuera, y
  cerrar XTreme no lo cierra), llamado **XT-WorkBench** (decisión del usuario): `xtbench.exe`, su propio proyecto
  (`src\XtremeDaw.Bench`, #142: una compilación de XTreme lo cerraba), que toma la ventana, `App.axaml`, `Loc`, `Palette`,
  `UiTheme` y `UiFonts` de `XtremeDaw.App` como archivos enlazados, nunca `XTreme.dll` (`Bench.cs` es compartido;
  `BenchRun.cs` y `BenchApp.cs` son del Banco). `probar.ps1` lo deja en `pruebas\banco\<fecha>\` solo si su build cambió
  (`firma.txt`). Uno solo a la vez: el botón trae al frente el abierto, y **nada lo cierra**: ni una compilación ni un
  XTreme nuevo; uno más nuevo entra la próxima vez que se abre, o con **Probar** en un ticket del Banco (tema `bench`,
  pedido del usuario, 2026-11-05): un cambio del Banco se prueba en el Banco, no en XTreme. Ese Probar
  (`probar.ps1 -Banco`) cierra el Banco abierto si es más viejo que el ticket y abre el más nuevo; si el abierto ya lo
  tiene, no aparece. `probar.ps1 -Tema banco` (o `-Arregla` de tickets del Banco) compila solo el Banco y xtlab y ata
  el ticket a ese build. `XTreme.exe --banco` (accesos viejos) abre `xtbench.exe`.
  Se abre solo, sin tomar el foco, si hay algo pendiente; desde afuera,
  `tools\probar.ps1 -Banco` o el acceso directo "XT-WorkBench". Lee la lista él mismo y toma como copia en prueba la más
  nueva. **Simple a propósito** (pedido del usuario, 2026-11-04): la barra de título tiene, a la derecha, **+** (nuevo
  pedido, también Ctrl+N: el cuadro se cierra al agregarlo), **Abrir XTreme** (la última copia
  sin pasar por Claude, `probar.ps1 -Abrir -Acceso`: si hay un XTreme abierto, pregunta si lo cierra) y el menú
  **Claude** (las skills del proyecto: Compilar y abrir, Última copia, Lista, Revisar todo, Publicar versión y una
  entrada por cada otra `xt_` de `.claude\skills`, que aparece sola; "Nueva skill…", que usa el mismo cuadro; y si
  abren en la app o en una consola, elegido una vez y guardado en `config.json`, "benchClaude"). **Revisar**
  (`AgentLauncher`, decisión del usuario: una sesión que se ve, no en segundo plano) abre Claude en el repositorio con
  `/xt_revisar-workbench`: en la app de escritorio (`claude://code/new?folder=…&q=…`: el mensaje queda escrito y la app
  pide confirmar la carpeta) o en una consola con Claude Code, que arranca solo (sin la app, siempre la consola). La
  consola va **dentro del Banco** (pedido del usuario, #278): vive en el Banco (`TestListWindow.Consoles.cs`) y se ve
  en **Agentes**, en la pestaña de su sesión, con una terminal propia (`Terminal/`: ConPTY de Windows en `PseudoConsole`, el emulador VT en
  `TerminalScreen`, dibujada por `TerminalView` en Cascadia Mono; la ruedita y Mayús+RePág recorren lo que salió de la
  pantalla, arrastrar selecciona, clic derecho copia o pega, Ctrl+C copia si hay selección y si no interrumpe,
  Mayús+Enter es una línea nueva). Cerrar Agentes no las corta (siguen trabajando); el botón de terminar de su pestaña
  pide un segundo clic, y cerrar el Banco con sesiones abiertas, cerrarlo de nuevo (las termina). Windows Terminal queda solo si la pseudo consola no arranca.
  En cada ítem (todos menos Resuelto; con el número la skill sigue sin proponer antes) son dos botones, **Desktop** y
  **Consola** (pedido del usuario, #121), con el mismo comando; la consola le saca al proceso las variables de la
  sesión de Claude que abrió la copia (`DropHostSession`), que si no la hacen fallar. El menú elige solo para el enlace de cada
  estado de A arreglar y en "Revisar todo" arriba de A arreglar y de Pedidos. Sin esas skills al lado de `pruebas\`,
  nada de Claude aparece. **Tickets** (pedido del usuario, 2026-11-04): cada ítem es un ticket con su conversación
  (`TestItem.Thread`, `TestMessage`): el pedido o qué probar, cada arreglo de un agente, cada veredicto con su nota y
  los comentarios de los dos lados, en orden; nada se pisa, y un arreglo que vuelve arranca con la respuesta vacía.
  El ticket abierto la muestra como una **línea de tiempo** (pedido del usuario, 2026-11-04): un punto por entrada
  unido por una línea, y cada una dice **Usuario** o **Agente** (la sesión, en el tooltip). Arriba va el **título** que
  le pone el agente al tomarlo (`TestItem.Title`, `-Titula`), que es también lo que muestra la fila; con título, el
  pedido pasa a ser la primera entrada; sin título, el pedido hace de título.
  Sin pestañas (pedido del usuario, 2026-11-04, #125: todo en vertical): una sola lista **por temas** y, dentro de cada
  tema, sus tickets en una sola lista, sin encabezados de sección (pedido del usuario, #197: era mucho ruido): cada
  fila dice su estado en un chip de su color (No anda, Anda mal, A mejorar, Pedido o Para probar;
  `StateChip`), y van primero los Nuevo, después lo que hay que arreglar, los pedidos y lo que espera prueba (`Ranked`);
  **Resuelto** queda abajo, plegado en una línea con su cuenta (`DoneHead`). Bajo la búsqueda, un **filtro** (pedido del usuario, #249: con muchos tickets a la vez se mezclaban los estados; `FillViews`, `InView`, guardado en `config.json`, `benchView`): **Pendiente** (pedidos y lo que hay que arreglar, sin agente), **En curso** (lo que tiene un agente o está arrancando, sea cual sea su estado), **Para probar**, **Terminado** (lo que anda, sin pliegue), **Todo**, cada uno con su cuenta. Arriba,
  **Buscar** (pedido del usuario, 2026-11-04; Ctrl+F) filtra la lista por número, texto, conversación, tema y tipo, sin
  mayúsculas ni tildes; Enter abre el primero y Esc borra. El tema de un ticket solo se lee
  en la ventana: un pedido entra "A categorizar" y se lo pone el agente que lo toma (`-Clasifica`; pedido del usuario,
  2026-11-04: era mucho para cargar); los temas son plegables (pedido del usuario, 2026-11-04, #123: sin líneas de árbol; `Tree`, `TopicHead`): un encabezado por tema con tickets (orden de `TestItem.Topics`, "A categorizar" al final), con flecha, un punto de color y una cuenta por estado (se lee plegado; #197), Nuevo, cuántos están en curso (con el spinner) y su Revisar con los pedidos y lo que hay para arreglar del tema. Lo plegado queda guardado en `config.json` (`benchFolded`; #124) y buscar abre todo. Arriba de la lista, **Revisar todo** manda todos los pedidos y lo que hay para arreglar. Cada ticket va en dos renglones (#204): arriba el número, el tipo y el título, solos; abajo el chip del estado (el de un pedido dice "Pedido"),
  Nuevo o Tomado, el intento o la hora, y Probar o la consola; solo uno Nuevo suma un tercero con lo último que se dijo, que en los demás va en el
  tooltip. La flecha de cada línea la despliega en un resumen (`Unfolded`, #197): el pedido, el último mensaje de un
  agente y lo último del usuario después, dos líneas cada uno, y cuántos mensajes más hay en el ticket. El aviso de arriba dice número y título, no lo que se escribió (#197). Un clic abre el ticket (`FillTicket`): arriba "← su
  sección" (también Esc) y **⋯**, fijos fuera del scroll (`TicketBar`, #175) (Historial de cambios y Sacar de la lista, que pide un
  segundo clic); después el estado, el texto, la conversación (un pedido se edita en el lugar con el lápiz al lado de su texto, #177: `EditableText`), y abajo los veredictos, la respuesta y, al lado de Comentar, Probar, Ver consola y Revisar (#166). Anda (verde) se
  marca en el acto y vuelve a la lista; los otros tres se eligen y esperan a **Guardar · No anda** (Ctrl+Enter), con
  la respuesta como nota; sin veredicto elegido, el botón es **Comentar**: queda en la conversación sin cambiar el
  estado. Ctrl+Shift+Enter guarda igual y lanza un agente en una consola sobre el ticket (como su Consola; no tras
  Anda; con la respuesta vacía lo lanza solo, #178). La respuesta llega al disco como borrador (`TestItem.Reply`, `Draft`), por si la copia que se prueba se cae.
  **Imágenes** (pedido del usuario, #176; `TestListWindow.Images.cs`): una captura pegada con Ctrl+V o archivos de imagen
  arrastrados a la ventana esperan bajo el cuadro del pedido nuevo o de la respuesta (cada una con su ×) y salen con el
  mensaje (`TestMessage.Images`, archivos en `pruebas\adjuntos\<ticket>\`, que se borran al sacar el ticket); en la
  conversación son miniaturas y un clic muestra una grande sobre el Banco (clic o Esc cierra). `-Ticket` lista su ruta
  para que el agente la mire. Hasta 25 MB cada una (`TestList.MaxImageBytes`).
  Los **Pedidos** (pedido del usuario, 2026-11-04: de ahí toman el trabajo los agentes) se cargan con **+** o Ctrl+N, en
  un cuadro arriba de la lista que se cierra al agregar (`Composer`, fuera de la lista, para que no se lo saque un refresco; Ctrl+Enter agrega; con Ctrl+Shift+N, al agregarlo lo empieza un agente en una consola con `/xt_revisar-workbench <número>`, #178), sin tipo: como el tema, se
  lo pone el agente que lo toma (Error, Mejora, Función nueva, Idea: `TestItem.Kind`, `-Clasifica 12 -Tipo mejora`; pedido
  del usuario, #129); la fila tiene borde de acento y "en curso" si
  un agente lo tomó (`TestItem.Taken`) y su sesión sigue abierta (`TestList.Held`: un Claude cerrado, según
  `AgentSessions`, que lee los `p-*.json` del hook, lo deja libre al instante, en el Banco y para `-Toma`; #173). Hecho, pasa a Para probar con su tipo. **Lo nuevo** (pedido del usuario,
  2026-11-04): lo que anota, devuelve o comenta un agente llega con `TestItem.Unseen` y lleva la etiqueta **Nuevo**
  (también en su tema y su sección) y borde de acento hasta que se abre el ticket (`TestList.See`); al abrir el Banco, y cada vez
  que llega algo con el Banco abierto, un toast por ticket abajo a la derecha (el `ToastHost` de XTreme, enlazado en
  xtbench; más de 3 juntos van en uno) lo anuncia con **Ver**, que abre el ticket, y se cierra solo si el ticket se ve
  antes; la campanita de la barra de título cuenta los Nuevo y su menú los lista, con **Marcar todo como visto** (#260); si
  la ventana no está al frente parpadea en la barra de tareas; lo del ticket que está abierto a la vista no se anuncia
  (#143). Cada ticket de Para probar que la última copia tiene lleva **Probar** (en su fila y en el ticket; #143): abre
  esa copia con `probar.ps1 -Abrir -Probar 12` (`XTreme.exe --probar 12`), que muestra un aviso fijo con qué probar (el
  texto del último arreglo, o el del ticket) y **Ir al Banco**. Uno tomado por un agente cuyo Claude sigue abierto lleva
  **Ver consola** en lugar de Probar y de Revisar (en su fila y en el ticket; #164, #172: mientras un agente lo arranca
  o lo trabaja no hay Probar ni un segundo Revisar; arrancando dice **Iniciando…**, también en su tema): trae al frente su ventana (`AgentLauncher.SessionProcess` busca la
  sesión en `.claude\bloqueos\p-*.json`, que el hook escribe en cada comando; `ShowSession` toma la consola, o la app
  que la corre; una sesión que corre en el Banco, su pestaña en Agentes). Su "en curso" lleva el **semáforo de bloqueos** de esa sesión (#167; `LockLight`, que lee los
  `.claude\bloqueos\t-*.json` que escribe el hook desde la sesión del agente): verde, nada la frena; amarillo, "en cola
  2° de 3" esperando un archivo de otra sesión; rojo, "bloqueado" (espera en círculo) o "atención" (un comando cambió
  archivos de otra sesión), que queda hasta su próxima acción. El tooltip dice qué archivo y quién lo tiene; el
  encabezado del tema muestra el peor. Los avisos entre agentes (#263; `AgentAlert`, que lee los
  `.claude\bloqueos\m-*.json` del hook) llegan como toast ("Aviso para el #60", de quién y qué dice; Ver abre ese
  ticket; uno de atención queda hasta cerrarlo), y los últimos tres van en el tooltip del "en curso". Al lado del "en
  curso" y en el nodo de cada sesión del mapa de Agentes va su modelo y nivel ("Opus 5.5 · medio", #315): el hook los lee
  de la línea de comando de su `claude.exe` y los guarda en su `p-*.json` (`AgentLauncher.Models`); sin `--model` ni
  `--effort` no se muestra nada. Las consolas del Banco arrancan siempre con Opus 5.5 en nivel medio (`AgentLauncher.ModelArgs`). La ventana
  **Agentes** (pedido del usuario, #269: para destrabar un atasco; #273: un mapa, en una ventana aparte, sin vista en el
  filtro; el botón de personas arriba, con cuántas sesiones hay abiertas; `TestListWindow.Agents.cs`, `AgentsWindow` y
  `AgentMap` en `TestListWindow.AgentMap.cs`) muestra arriba un mapa: cada sesión de Claude abierta (`AgentAlert.Live`)
  a la izquierda, con su ticket, su semáforo y solo cuántos archivos está editando (#305), y a la derecha solo los
  archivos con conflicto (una cola o un choque, #305), cada uno a la altura de la sesión que lo tiene
  (`LockLight.Held`). Una línea llena en el acento es un archivo que tiene; una amarilla punteada, su lugar en la cola de
  otro archivo, con el puesto y desde hace cuánto; una roja, una cola en círculo o un comando que cambió archivos ajenos
  (`LockLight.Snags`). El mapa se achica solo para entrar entero en su lugar (#286: `AgentMap.FitHeight`, el alto de su
  scroll), llenando siempre el ancho; por debajo de la mitad deja de achicarse y se desplaza. Pasar el mouse por una caja deja encendidas solo sus líneas; un clic en una sesión le apunta la
  caja de mensajes, en un archivo tomado ofrece pedirle que lo suelte (como `-PideArchivo`). Una sesión que termina sale
  del mapa con sus archivos en el próximo refresco. Abajo, la conversación de las últimas 24 h como un chat
  (`AgentAlert.History`: el registro `avisos.jsonl` que el hook suma con cada aviso) y una caja para escribirle a una
  sesión o a todas (Ctrl+Enter envía); la caja no se rearma al refrescar. Lo que manda el Banco lo escribe
  `AgentAlert.Send` como el usuario, igual que el hook. Arriba del chat, pestañas (#278): una por cada sesión abierta, siempre a la vista (con un punto en el acento si espera al usuario); el chat
  de las últimas 24 h no tiene pestaña (#294: el usuario no la usaba) y se ve solo sin ninguna sesión abierta. El botón de
  terminar (al lado de los niveles, con un segundo clic de confirmación) termina la sesión elegida (#294): una consola
  del Banco se cierra; cualquier otra, matando su proceso de Claude (`EndSession`), lo que libera sus archivos al
  instante. Una sesión que termina, bien o no, se cierra al instante (#287): su pestaña y su consola se van y
  se pasa a otra abierta (la que espera primero) o al chat. La pestaña o el clic en la sesión del mapa la eligen (su caja queda
  marcada y la caja de mensajes le escribe) y muestran su conversación con Claude en vivo cada 2 s
  (`AgentTranscript`, que lee solo lo nuevo del transcript de la sesión en `~\.claude\projects`), con tres niveles al
  costado de las pestañas (#278): **Resumen** (el de entrada: solo los mensajes, los de Claude cortados a su primer párrafo),
  **Normal** (además una línea por herramienta) y **Todo** (la terminal real, si la sesión corre en el Banco). Una sesión
  del Banco que pide un permiso o hace una pregunta muestra su terminal en cualquier nivel, para contestarle ahí; con
  otro nivel, la caja de mensajes le escribe en su consola (pegado y Enter), no como aviso. Lo que se le manda como aviso
  aparece en su conversación al instante (#283: el aviso no llega al transcript), en orden con el resto y con su estado:
  "Enviado · sin leer" hasta que el hook se lo da al agente (marca `read` en su `m-*.json`), y entonces "Leído". Una que recién arranca tiene
  su pestaña con el pedido hasta que el hook dice su sesión (`AgentLauncher.LiveProcesses`). En el mapa, una sesión que
  trabaja dice qué hace (su última herramienta, "Pensando…" o "Escribiendo…"). En el pie del Banco, al lado del tamaño del texto, el
  botón **Agentes** (pedido del usuario, #278; ahí abajo y chico, #318) abre la ventana (en la sesión que te espera, o la primera) y dice cuántas sesiones hay abiertas, cuántas te esperan y cuántas en cola. Al lado, un slider pone el **máximo de agentes a la vez** (#318, `TestListWindow.Queue.cs`, "benchMaxAgents", 4 por defecto, de 1 a 10): cuentan las sesiones abiertas del proyecto y las consolas del Banco que arrancan; un Revisar que se pasa queda **En cola** (en sus tickets, en lugar de "Iniciando…") y arranca, de a uno, cuando termina otro. La cola vive mientras el Banco está abierto. Una sesión que espera
  al usuario se ve en el acento con su motivo: terminó su turno o la cortaron ("en espera de tu respuesta", del
  transcript), una pregunta abierta ("te hizo una pregunta") o un permiso ("espera tu permiso": el hook `espera`, en
  Notification, deja `u-<sesión>.json`, que cualquier línea nueva del transcript anula). Abajo, al lado de Historial, un slider cambia el tamaño
  del texto del Banco (una escala sobre la lista, la búsqueda y el cuadro, aparte de la de XTreme: `benchZoom` en
  `config.json`; doble clic, 100 %). El Banco tiene su propio idioma y tema (#151): el botón de paleta, primero arriba
  (`ThemePicker` con su fila de Idioma, `TestListWindow.AddLookPicker`), los guarda en `benchLanguage` y `benchTheme`,
  aparte de los de XTreme (hasta elegir, el idioma del sistema y el tema por defecto); el tema cambia en vivo y el
  idioma rearma la ventana al instante, en el mismo lugar y con la búsqueda y el pedido a medio escribir
  (`Bench.Open`, `TakeOver`). La consola que abre Revisar (su pestaña en Agentes) se cierra sola cuando el agente terminó bien (y la pestaña, cuando termina de cualquier forma): corre
  `tools\cerrar-sesion.ps1`, que suelta sus bloqueos, deja el archivo de `XT_BENCH_CLOSE` y termina su Claude. Lee y escribe `pruebas\lista.json` (`TestList`): lo ve al instante (`FileSystemWatcher`, y cada 2 s por
  las dudas). Cada clic lleva la versión de la tarjeta (`TestItem.Rev`): si un agente cambió el ítem mientras tanto,
  no se escribe nada, avisa y la lista se refresca. **Historial** (el botón de abajo, o el ⋯ de una tarjeta) muestra
  `pruebas\lista.historial.jsonl`: quién hizo qué y cuándo, también lo que se sacó de la lista.
  Va con color (pedido del usuario, 2026-11-04: le faltaba carácter): encabezado con ícono, línea de acento y la copia que se prueba;
  cada sección con su punto (Pendiente acento, Con nota azul, A arreglar rojo, Resuelto verde, Pedidos violeta); cada tarjeta con
  una franja a la izquierda y un tinte del color de su veredicto o de su tipo (Error rojo, Mejora azul, Función nueva violeta,
  Idea ámbar, los mismos en los badges y en el selector de tipo), y abajo el total de cada veredicto en su color.
- CPU / GPU / RAM / DSP con la misma forma ("CPU 19% ▬"), con líneas `Border.tsep` entre los grupos. El tooltip del DSP dice qué pista se lleva más del hilo de audio, y cuánto su instrumento (`LiveTrack.TakeLoad`, medido cada 0,5 s).
- A la derecha de los medidores, dos botones **ocultan del todo** el panel del canal y el del proyecto (`LeftPanelButton`,
  `RightPanelButton`, `MainWindow.Sidebars.cs`, pedido del usuario): una ventanita dibujada con el panel relleno
  mientras se ve. Oculto no deja ni la columna de íconos; al volver, vuelve como estaba (abierto o plegado). Se
  recuerda en `config.json` ("sidebarHidden", "projectBarHidden"). B / P, con el panel oculto, lo muestran.
- Maximizada, Windows corre la ventana 8 px afuera de cada borde (por la barra propia). La grilla raíz lleva
  `Margin="{Binding $parent[Window].OffScreenMargin}"` para compensarlo; no lo saques o se cortan los costados y
  la barra de transporte.
- Los paneles (inspector, barra de proyecto, timeline, mixer) son tarjetas (pedido del usuario, para ver dónde empieza
  y termina cada uno): radio 10, borde `Line` completo y 4 px de margen, así entre dos quedan 8 px de fondo `App`
  (clases `card`, y `card-clip` + `card-edge` en el timeline y el mixer, que pintan hasta el borde). Las barras de
  título y de transporte siguen de lado a lado. Los separadores arrastrables van en una fila de 0 y ocupan ese hueco
  con `Margin="0,-4"`.

## Ayuda (`Dialogs/HelpDialog`, `HelpGuide`)

- Se abre con F1 (atajo `help`), con Ayuda → Ayuda de la barra de menús y con el ? del gestor de proyectos,
  que es lo primero que ve alguien nuevo (pedido del usuario: didáctica, simple y por categorías). No abre grabando ni
  sobre Configuración, Exportar o Acerca de. Reabre donde quedó.
- A la izquierda, el buscador y las categorías (una abierta a la vez, con sus temas); a la derecha, la página: Inicio con
  "Empezá por acá" y Novedades, lo más buscado (`HelpDialog.Popular`) y una tarjeta por categoría; la categoría con sus temas, o el tema, que termina con
  el anterior y el siguiente de su categoría. Cada tema lleva su ícono (`icon`, en la lista, las páginas y los chips; sin ícono usa el de su categoría). El foco arranca en el buscador; Enter abre el primer
  resultado y Esc limpia la búsqueda y después cierra. La búsqueda ignora mayúsculas y acentos, y mira título, `keywords`
  y texto (`HelpGuide.Search`).
- Un tema se llama como lo que la gente quiere hacer ("Grabar una toma", "No se oye nada"), no como el control. Lleva
  `intro`, `steps` (números), `notes` (tarjetas con lamparita), `settings` (botón a esa página de Configuración, oculto
  sobre el gestor, donde Configuración no abre) y `see` (ids de otros temas).
- El contenido va en `Assets/Help/<código>.json`, no en `Assets/Lang`: son páginas, no etiquetas. **es.json es la
  referencia** y lo que falta en otro idioma cae al español. En el texto, `**negrita**` es lo que se ve en pantalla (tal
  cual dice la interfaz), `{key:id}` / `{wheel:id}` el atajo que el usuario tiene configurado y `{icon:E74E}` el glifo de
  un botón (el mismo que en pantalla).

## Novedades (`Dialogs/NewsDialog`, `NewsGuide`)

- Qué cambió en cada versión, la más nueva primero y las anteriores en chips arriba; cada novedad lleva ícono y, si tiene,
  "Ver en la ayuda" (`topic`). Se abre con el megáfono de la barra de título y del gestor de proyectos y con la tarjeta del
  inicio de la Ayuda. No abre grabando ni sobre Configuración, Exportar o Acerca de.
- Se muestra sola al arrancar si la primera versión del archivo no es la de `config.json` "newsSeen": también en una
  instalación nueva (pedido del usuario: la primera vez que se entra). Va por encima del aviso de componentes.
- El contenido va en `Assets/News/<código>.json`, con las marcas de la Ayuda; **es.json es la referencia**. Se cuenta para
  el usuario, sin nombres internos ni arreglos que no va a notar.

## Sesión en vivo (`MainWindow.Live.cs`, `Dialogs/LiveDialog`, #291)

El botón **Sesión en vivo** de la barra del proyecto, a la izquierda de Exportar (`LiveButton`, clase `live`: contorno
del acento; en una sesión se llena y dice "En vivo · 3"; pedido del usuario), y Archivo → **Sesión en
vivo…** abren `LiveDialog` (ancho de formulario, sobre `Scrim`): sin sesión, el nombre (se recuerda, "liveName") y dos
tarjetas (`Button.livepick`): **Crear sesión** (pregunta dónde, en otras dos tarjetas, #320: **Internet**, que abre la sala
en el relay con un código corto, o **Red local**; la abre en el acto y muestra qué pasar) o **Unirme** (el campo del código
o link, cualquiera de los dos, y **Unirse**; Volver o Esc regresa a las tarjetas); en una sesión, lo que se pasa (solo el
anfitrión): por internet el código corto grande (`RoomText`) con Copiar código / Copiar link; en la red local el código
largo con Copiar código / Copiar link / Copiar IP y la IP, quién está y **Finalizar sesión** (anfitrión) o **Salir de la sesión** (invitado).
El menú dice cuántos hay ("Sesión en vivo (3)…"). Las herramientas del timeline se centran sin pisar este botón (`PlaceTools`). Cada edición (`MarkDirty`) sale 120 ms después (`LiveSoon`); lo que llega entra por
`RestoreEdit`, como un deshacer: el deshacer de cada uno saca solo sus pasos (`UndoManager.Rebase`), y Historial →
Pasos lista los de los otros entre los propios, con su nombre (`UndoManager.Remote`; no se puede ir a ellos).
Grabando no sale ni entra nada: espera a que termine la toma. El invitado recibe una copia nueva en la biblioteca;
el audio llega después y sus clips pasan de offline a sonar (`ReloadArrived`). Abrir otro proyecto o cerrar sale de la sesión.

**La animación del botón** (`Controls/LivePulse.cs`, pedido del usuario, #319): en una sesión, en lugar del ícono, un
orbe por persona (hasta 8) en su color, unidos por una línea fina con un brillo que la recorre. Cada orbe respira; al
entrar alguien su orbe aparece con un rebote y una onda, al irse se achica y un anillo se cierra sobre él. Mientras
alguien toca o edita salen ondas de su orbe (lentas y tenues si solo corre su transporte, rojas y rápidas si graba, con
un punto rojo adentro), y cada cambio suyo que llega (o que sale de esta copia) o cada vez que elige otra cosa destella
un anillo. Solo lo que esta copia de verdad mandó destella su propio orbe (`LiveSession.Publish` dice si salió algo): lo
que llega de otro no. Lo mueve el timer de la presencia (`PresenceStep`, 33 ms); todo sale del reloj al dibujar, sin asignar.

**Los cursores de los demás** (`Controls/LivePointers.cs`, `MainWindow.LivePointers.cs`, pedido del usuario, #319: "mouse y lo
que tocan"): una flecha de su color con su nombre, sobre los paneles y bajo los diálogos, sin tomar clics. Cada ventana
tiene su tamaño, zoom y scroll, así que no viajan píxeles (`LivePointer`, en la presencia): sobre el timeline, la fila y el
tiempo (o el lugar sobre los encabezados; `TimelineView.PointerSpot` / `SpotPoint`); con un control agarrado en una tira
del mixer o el inspector (botón, fader, perilla, campo, gráfico), de quién es y cuál de ese tipo es ahí
(`mix:Fader:0`), con un anillo que late en la punta, hasta 0,7 s después de soltarlo. Cada copia lo busca en su
pantalla: la misma tira, o el inspector si muestra lo mismo; si no lo muestra, la flecha va al encabezado de esa fila
en el timeline. Viaja con la presencia (15 por segundo como mucho) y entre dos llegadas la flecha se desliza; que solo
se mueva el puntero no rehace las etiquetas (`PumpResult.PointersMoved`).

**Quién está dónde** (`MainWindow.Presence.cs`, `TimelineView.Presence.cs`; pedido del usuario): el anfitrión le da a
cada uno un color fijo (`LiveSession.ColorOf`, `Palette.PersonColor`, 0 = el anfitrión). El nombre de cada uno, en una
etiqueta de su color, va en la pista y el clip que tiene elegidos, en lo que está editando (leído de sus cambios,
`LiveSession.EditingBy`: 1,5 s después del último), en la pista donde graba (con el punto rojo) o toca con el teclado; en
el encabezado de pista va después del nombre y sus etiquetas, en el clip pide lugar a su franja (`ClipBand`), en el
mixer va arriba de la tira, sin tomar clics. Con varios en lo mismo, "Ana +1". Su cabezal es una línea fina de su color con
su nombre en la regla, mientras su transporte corre (en la capa que se mueve; sigue solo desde que llegó). Cada copia
dice dónde está (`LivePresence`, un mensaje aparte) desde un timer de 33 ms, solo si algo cambió y como mucho ~15 por
segundo; el cabezal, solo si se corrió de donde los otros lo dibujan. No se guarda ni se deshace, y se borra al salir. En
Historial cada paso lleva el nombre de quién lo hizo y en Biblioteca cada toma el de quién la trajo (`Media.AddedBy`,
`PersonBadge`): en su color si está en la sesión; fuera de una sesión, los pasos van sin nombre.

**Quién está** (`LiveDialog.Rows`, pedido del usuario): en el diálogo, una fila por persona (caja `Raised`, radio 6) con
su punto de color, el nombre ("(vos)" la propia), su rol (Anfitrión en acento, Invitado en gris), la latencia del link
en mono (ida y vuelta, `LiveSession.LatencyOf`: el anfitrión ve la de cada invitado; el invitado, solo la suya; "—" si
no llegó respuesta en 5 s) y abajo qué hace (Grabando, Tocando, Editando, Reproduciendo o Conectado, y "Le faltan
plugins"). Se rearma si cambia quién está y cada segundo reescribe latencias y estados. Cada link se mide con un ping
por segundo, en el hilo de la red. Cuando alguien entra o sale: un toast ("Ana entró a la sesión") y un sonido corto
(`CueSounds`, dos notas suaves que suben o bajan, hechas en código; suenan por el camino del click,
`PlaybackEngine.PlayCue`: nunca en una toma ni en la exportación, y no suenan mientras se graba ni sin driver abierto).

**Plugins: solo los que tienen todos** (`MainWindow.LivePlugins.cs`, `LivePlugins`, decisión del usuario, #291): cada
copia le dice al anfitrión qué plugins tiene (los que encontró al buscar plugins y los del proyecto que puede cargar, por
su archivo; al entrar sin ninguno encontrado, busca), y el anfitrión les pasa a todos las listas de todos
(`LiveSession.LackingIt`). Un plugin que a alguien le falta sale apagado en los menús de instrumento y de "+ Efecto",
con un tooltip que dice quién no lo tiene. Uno que ya está en el proyecto (instrumento o slot VST3 de pista, carpeta o
bus) queda apagado en todas las copias mientras falte en alguna: su proceso no corre, el efecto deja pasar el sonido y el
instrumento calla (`LiveTrack.InstrumentOff`, sin el tono de prueba); el chip del instrumento, el inspector y el estado del
slot dicen "Falta en Ana · apagado". Al instalarlo y buscar plugins, o al irse el que no lo tiene, se prende solo; fuera de
la sesión todo suena. La exportación usa los plugins de esta PC, como siempre.

## Avisos y toasts

- Sin campanita (pedido del usuario, 2026-11-03: nadie la abría). `Status(texto)` solo lo anota en el registro de la
  sesión (`SessionLog`): es para lo que una acción hizo y ya se ve, o una nota de progreso. Nada se escribe en el
  centro de la barra de título ni en diálogos.
- **Toasts** (`Controls/ToastHost`): `Toast(texto)` es para lo que el usuario tiene que ver: un error, una acción que
  no se pudo hacer, un resultado que no se ve en otro lado. Sale abajo a la
  derecha, encima de todo, se va sola a los 6 s (el puntero encima la frena); el mismo texto otra vez reemplaza su
  tarjeta en vez de apilar una copia. Una
  fija (`sticky`) se va solo con su ×: la usa `--probar "texto"` ("Para probar"). Puede llevar un botón (`action`).
  Su letra es más grande que la del resto de la ventana (texto 14, título 12, 420 px de ancho; pedido del usuario,
  para leerla de un vistazo).
  Entre otros salen como toast: los errores al guardar el proyecto o
  una toma (el reintento del guardado, solo la primera vez seguida), la toma guardada (o su problema), la exportación
  terminada (con "Mostrar" en el Explorador), la cámara que deja de mandar imagen (`TickCamera`, no en solo audio) y
  la interfaz de audio que deja de llamar más de 3 s (`PlaybackEngine.Stalled`, `TickAudioDriver`).
  Toast con barra (`Toasts.ShowProgress`): texto, porcentaje y barra de acento. Aparece si el trabajo pasa de 0,3 s
  (lo que termina enseguida no parpadea), se va solo y no tiene ×. Lo usa Importar audio, que también avisa con toast
  si un archivo no se pudo importar.

## Transporte (abajo)

- Celdas planas con el valor arriba y una etiqueta chica abajo, al estilo Fender Studio Pro.
- En el medio, **un solo recuadro oscuro** (`Border.metro`) con reloj │ transporte (`Button.tbtn`; Grabar es un ●
  rojo sin texto) │ metrónomo, separados por `Border.vsep`. Entre Detener y Play, **Cortar audio** (parlante tachado,
  Ctrl+., pedido del usuario): detiene y calla las colas de todos los slots (pistas, carpetas, buses, master) y las notas
  que siguen sonando, con un fundido de 10 ms (`PlaybackEngine.CutAudio`, `LiveTrack.Silence`). Después del loop, **Volver al parar** (#271, `ReturnToggle`,
  `PlaybackEngine.ReturnOnStop`, en `config.json` "transport.returnOnStop", apagado de fábrica): prendido, parar con
  Espacio o ▶ vuelve el cabezal a donde arrancó la reproducción (o al último salto hecho tocando), que mientras suena marca una línea punteada del acento (`TimelineView.SetReturnMark`); ■ ya lo hacía
  siempre. Queda **centrado en la ventana** mientras los costados entran al lado (`TransportPanel`): si no, se corre. **Nunca pasa a dos filas** (#36): si los tres grupos no caben, se esconden solos los controles con `TransportPanel.Fold`, el número más bajo primero (sonido del click, Compás, Ver todo, teclado virtual, Maximizar, Cortar audio, LUFS, Volver al parar, Monitor, la entrada, el afinador). Clic derecho en la barra elige cuáles mostrar (`MainWindow.TransportShow.cs`, `TransportPanel.Off`, en `config.json` "transport.hidden"); los apagados no vuelven con ningún ancho. Con el afinador escondido, su panel abre sobre la barra.
- A la izquierda, pegados al recuadro: el medidor de entrada (del ancho del texto de abajo y con su escala en dB,
  `StereoMeter.ShowScale`) con "Entrada 1-2 · 48 kHz · 192 muestras (4,0 ms)" chiquito debajo (el driver va en el
  tooltip) y el botón **Monitor** (`ToggleButton.cell`, el monitoreo por software de Configuración). A la derecha, la vista:
  tocar de nuevo Piano roll, Mixer o Mastering (el botón encendido) vuelve al timeline (pedido del usuario), igual que el atajo del mixer.
  Apilado (solo audio, Vertical) el timeline está siempre a la vista y el botón Timeline no se muestra (pedido del usuario);
  vuelve con un panel maximizado, la preview maximizada o el timeline en su ventana (`ApplyViewHosts`).
- El reloj en compases dice compás.tiempo.semicorchea sobre el mapa de tempo (`BarsKey`: el tiempo es la figura del
  compás, corcheas en 6/8). Con cambios de tempo, la caja de Tempo muestra el **tempo bajo el cabezal** (`ShownBpm`);
  escribirlo sigue cambiando la velocidad de toda la canción: el mapa entero se escala para que ese punto quede en el
  valor escrito (`InitialFor`).
- Metrónomo: una caja con celdas Compás · Tempo · TAP · Cuenta · Click · sonido ▾ (ícono `MetronomeIcon`, dibujado
  porque Segoe no tiene metrónomo).
- **Cambiar el tempo pregunta si se estiran los clips** (pedido del usuario): con clips en el timeline, el valor nuevo
  solo se muestra hasta que el control queda quieto (`TempoAskDelay`, y `TempoControl.Busy` mientras se arrastra o se
  marca TAP) y entonces `TempoDialog` ofrece **Estirar**, **Solo la grilla** (`SetTempo(valor, follow: false)`) o
  Cancelar (el tempo queda como estaba). Sin clips o grabando no pregunta.
- **Estirar cambia la velocidad de toda la canción** (`SetTempo`, `TempoFollow`, decisión del usuario, sin
  elegir qué clips): clips, video, automatización, marcadores, acordes, loop y cabezal quedan en su compás, con el mismo
  tono. Cada clip que cambió de largo **calla** hasta que su sonido está listo (decisión del usuario): se ve "Ajustando
  al tempo… N%" sobre el clip y un toast con barra para todos, de a un render por vez, después de 300 ms sin tocar el
  tempo; entra con un crossfade de 10 ms y queda en `cache/` (`MainWindow.Transpose.cs`). Volver al tempo original es la
  toma tal cual. Grabando, solo se mueve la grilla. La imagen cambia de velocidad con el sonido, en la preview y al exportar.
  Los clips MIDI siguen en su compás y tiempo siempre, también cuando solo se mueve la grilla: los patrones seguidos no
  se pisan ni dejan huecos. Cada cambio de tempo es un paso del deshacer (#262; los de la ruedita seguidos, uno solo): deshacerlo vuelve el tempo y lo que movió; el bucle A–B lo sigue.

## Afinador y LUFS (`MainWindow.Meters.cs`)

- En el transporte, después de Monitor, la celda "Afinador" (A) muestra la nota y abre un panel (`Controls/TunerView`)
  con la aguja; escucha la primera pista armada o la entrada 1-2 (`PlaybackEngine.Pitch`, YIN en el `Tick` solo
  mientras está prendido).
- Las notas salen de `Loc` (`note.0`…`note.11`: Do Re Mi en castellano, C D E en inglés; `TunerView.NoteName`).
- A cada lado de la nota, un triángulo dice para dónde girar la clavija (▶ a la izquierda = subí, ◀ a la derecha =
  bajá) y parpadea más rápido cuanto más lejos está (1,5 a 8 por segundo, llevado por el `Tick`, sin asignar). Dentro
  de ±3 cents (`TunerView.InTuneCents`) la nota, la aguja y un "OK!" se ponen en verde (`InTune`), también en la celda.
- El slider "Sensibilidad" es la compuerta de nivel de `PitchTap` (−70 a −30 dB, por defecto −50 =
  `PitchTap.DefaultGateDb`; el umbral de YIN no se toca). Se guarda en `config.json` (`tuner.gateDb`) al cerrar el
  panel; en xtlab, `afinar --umbral`.
- El panel se cierra con un clic afuera (`IsLightDismissEnabled`), con A o con la celda; con el foco en el slider,
  los atajos pasan igual.
- A la derecha, la celda "LUFS" con el integrado del master de la pasada actual (`LoudnessMeter`, BS.1770; empieza
  de nuevo con cada play o un clic).

## Inspector (izquierda)

- **Armado como el de Fender Studio Pro** (`MainWindow.InspectorStrip.cs`, pedido del usuario, 2026-11-03: se perdía
  en la lista larga; con lo visual de XTreme). De arriba a abajo:
  - **El nombre, fijo** (`inspectorTopBox`): badge + nombre editable del canal (`TextBox.title`), su cuadrito de color (`ColorPickButton`, #145: despliega la grilla de colores y la cierra al elegir; también en el bus y en la carpeta, que al lado lleva el botón que pinta sus pistas, `FolderPaintButton`) y el botón que pliega
    las secciones, o el del bus (`busHead`), o el aviso de varias pistas. El color de lo elegido
    (pista, carpeta, bus, o varias pistas si comparten color) se ve en la línea de 3 px bajo el encabezado del inspector
    y en un tinte suave del recuadro del nombre (`inspectorTopRule`, #145); sin color, la línea común. Cómo se pinta
    se elige en Configuración → Apariencia → Color de pista en el inspector (#162, `TrackColorLooks.Head`: Línea y tinte,
    Solo línea, Lleno, Sin color; y aparte franja al costado, secciones teñidas, íconos de sección en su color).
    Con el inspector y el timeline en la ventana, una línea de 1,5 px del color de lo elegido (el acento sin color) va del
    borde izquierdo del encabezado de la pista o carpeta elegida, por el hueco entre los paneles (o por el borde izquierdo
    de la ventana si el inspector está arriba), hasta una punta junto a la fila del nombre en el inspector; mientras está
    prendida, el inspector deja 6 px más de hueco a su derecha para que se vea (`SpaceForPointer`) (#180,
    `InspectorPointer`, `MainWindow.InspectorPointer.cs`: se ubica en cada tick, sin asignar). No se ve con el inspector
    plegado o en su ventana, el timeline afuera o tapado por el mixer, un bus elegido o la pista fuera de la vista. Se
    apaga en la misma tarjeta de Apariencia ("Flecha de la pista elegida al inspector", `TrackColorLooks.Pointer`,
    "trackColor.pointer", prendida por defecto).
  - **La pista** (`takeSections`, siempre): en una pista MIDI su instrumento (`midiPanel`) antes; "En el video" como
    fila; en una pista de audio con clips, "Canales" (Mono / Estéreo / Mixto, `LiveTrack.Layout`) y, si hay estéreo, "Ver izquierdo y derecho"; una pista vacía suma cómo llenarla. Transponer (`trackTranspose`) va acá en una pista MIDI o con varias
    pistas elegidas; en una pista de audio sola va con el clip, como el Transpose por evento de Studio Pro. En un bus: ícono y "Quitar bus" (`busPanel`); su color, en el cuadrito del nombre.
  - **El clip** (`clipArea`, solo una pista de audio sola): el nombre de su archivo con el ícono de clip, sus datos
    como badges, el Transponer de la pista y Sincronización, Clip, Encuadre y "Quitar del timeline" (`clipSections`, `clipRefresh`); sin clip,
    "Ningún clip elegido". Pista y clip son lo único que se desplaza.
  - **La tira, fija abajo** (`inspectorStrip`, fondo `App`): a la izquierda paneo (barra bipolar), M y S (el bus,
    solo M), el fader con su medidor (`TickInspectorStrip`, sin asignar) y el nivel en dB; a la derecha, en el orden en
    que suena, **EFECTOS** (una fila por slot, `StripSlotRow`, la misma `ListRow` que el mixer: ⏻, ícono, nombre y lo
    suyo en mono; el ⏻ ocupa todo el borde izquierdo de la fila, con la flecha común; el resto, con la mano, es lo
    que se agarra; clic = su ventana, como en el mixer, `OpenStripSlot`: un VST3 cargado la suya, los demás la
    ventanita del panel, `ShowSlotWindow`; arrastrar = lo mueve, `SlotDrag`; clic derecho = su menú; "+" como
    última fila de la lista, `ListAddRow`, #150), **ENVÍOS** (las barras de `CompactSends`: ⏻, nivel, PRE; clic derecho, `ShowSendMenu`; el "+" al final tilda
    buses) y **SALIDA** (`OutputSelect`, "Por la carpeta …" o, en un bus, Master). Cada parte lleva su título
    (`StripListHead`, pedido del usuario: que se sepa de un vistazo qué es): ícono y nombre en el acento, 11 SemiBold en
    mayúsculas, sobre una línea fina. La tira edita la pista, las elegidas (`EditPicked`) o el bus (`StripEdit`).
  - **Paneles** (como Studio Pro): dos botones en el encabezado del inspector (`InspectorPaneButtons`, en acento si se
    ve) muestran u ocultan el clip y la tira (`inspector.clip`, `inspector.strip`). Con la tira a la vista, el divisor
    (`stripSplitter`, propio: un `GridSplitter` limitaba el arrastre por lo que medía el contenido y en MIDI no se movía) cambia su alto en todo canal (audio con o sin clip, MIDI, bus, varias pistas; mínimos `StripMinH` y
    `EditorMinH`; se recuerda como `inspector.stripH`) y las secciones de arriba toman el resto y se desplazan si no
    entran. El fader crece con la tira.
- **Filas compactas** (pedido del usuario, como Studio Pro): todo valor del inspector es una fila, etiqueta a la
  izquierda y el valor a la derecha en una cajita (`InspectorSlider` con `compact`), como los campos de Studio Pro: se
  arrastra para arriba o para abajo (Shift: fino), la ruedita lo mueve, doble clic deja escribir el número (Enter lo
  pone, Esc no) y Alt+clic lo vuelve a su valor. Transponer (`OnRelease`) muestra el número mientras se mueve y lo
  pone al soltar (o cuando la ruedita para): cada valor es un render. Cada sección plana (`Border.section`, encabezado de
  36) lleva ícono + etiqueta en mayúscula y, si se puede apagar, un ON/OFF; apagada, sus filas al 40 %.
- **Con varias pistas elegidas** (`MainWindow.InspectorGroup.cs`, pedido del usuario; Ctrl o Shift+clic en los
  encabezados): en lugar del nombre, un aviso ("3 pistas · los cambios se aplican a todas") y solo lo común, de audio y
  MIDI mezcladas: la tira sin efectos (fader, paneo, M/S, los envíos a los buses que tienen todas, la salida si ninguna
  está en una carpeta) y Transponer; "En el video" solo sin pistas MIDI. Efectos, clips, el instrumento y la
  automatización son de cada pista y no se muestran. Cada control pone el mismo valor en todas (`EditPicked`,
  `GroupRouter`), en un solo paso de deshacer por gesto (`EditTracks`). Un valor que no comparten dice "Varios" (el
  fader atenuado, el paneo, Transponer, la salida), "En el video" queda indeterminado y arriba dice "Mute en 2 de 3".
  Mute (y M y S del teclado) prende todas salvo que ya estén todas. Las pistas bloqueadas quedan afuera, como en toda
  edición de varios clips, y el aviso dice cuántas; si lo están todas, los controles quedan deshabilitados.
- **Un solo panel por efecto** (`MainWindow.FxPanel.cs`, pedido del usuario): `FxBody` arma los controles de cualquier
  efecto nativo desde su lista (`FxDefs`) más sus opciones (presets, modos), igual en el inspector, la ventanita del
  mixer y la del timeline; la celda de cada perilla (`FxCell`) es la misma que en Mastering. Las perillas se reparten el
  ancho y crecen con él (`FxGrid`). Lo fino del compresor va detrás de "Ajuste fino" y se abre solo en modo manual.
  **Siempre full** (pedido del usuario, #140, #186; sin vista compacta desde #312, menos el Bombeo, que tiene su panel; el EQ lleva el gráfico grande, `EqEditor(big)`): un efecto con
  `FxDefs.Layout` abre su ventana full (`SlotFullW`, todo en paneles internos, `FxLayout.Rows`, cada uno tan ancho como su `Weight`; `@graph` y `@key` son el
  gráfico y el sidechain). `FxLayout.Compact` e `InsertSlot.Full` quedan en el modelo sin uso en la ventana. **Sin huecos** (#312): cada fila es un `FxRow`
  (todos sus paneles tan altos como el más alto) y cada panel un `FxFill`: el gráfico (`FxScope`, `CompGraph`) se estira con el alto que sobra y, sin gráfico,
  las perillas crecen (`KnobGrid.FillMax`, hasta `FxKnobFillW`). Mastering lo sigue mostrando como antes (`full: null`).
  **Identidad de cada efecto** (pedido del usuario, #187): cada nativo lleva el color de su familia (`FxDefs.Family`,
  `Palette.FamilyColor`: violeta dinámica, verde tono, naranja saturación, azul espacio, rosa modulación) en el ícono, la barra
  del título (`FxPanel`), el arco de sus perillas (`Knob.Tint`; el estilo de perilla sigue siendo el de Apariencia) y los
  paneles internos; sus perillas protagonistas (`FxLayout.Hero`) van grandes (`FxHeroW`) en las dos vistas, el resto chicas al
  lado; y su gráfico (`FxScope`, `@scope` en `FxLayout`: la cola de la reverb, los ecos del delay, la onda de la modulación
  que corre mientras suena, la curva de la saturación, cinta, clipper y limitador con una senoidal, la puerta, el campo
  estéreo y la respuesta de Limpieza, Color y Bandas) en su panel. La reverb, más alta (`FxScopeReverbH`, #312): golpe seco con brillo, corchete del pre-delay, reflexiones tempranas que rebotan de lado a lado, cola con brillo que se apaga y chispas de difusión, L arriba y R abajo según el ancho, más blanca cuanto más brillante el tono. Dibuja lo que dicen las
  perillas, no la señal. Los compresores siguen con su `CompGraph`, el EQ con su editor (en la ventana del efecto, la banda elegida y su dinámica van en perillas, en dos paneles; en la vista completa, lado a lado bajo un gráfico de 220 px: `EqEditor` con `knobs`/`wide`) y el Bombeo con su panel.
  **Las opciones propias de cada efecto van en su panel** (pedido del usuario, #261): en la full, la nota del delay junto a
  sus ecos (`@division`), Estéreo / Ping-pong en Estéreo, Válvula / Cinta en Saturación, el modo del gate en Compuerta
  (`@mode`) y LIM del compresor en Salida (`@lim`); en Mastering, en una fila bajo los presets
  (`FxChoiceRow`; la nota del delay en una línea propia). **Cada parámetro con el control que le va** (pedido del usuario, #261): lo continuo, perilla; lo que tiene pocos valores fijos (hasta 8, `FxChoiceMaxSteps`: ratio, ataque y release del compresor de bus, ratio del FET y del VCA, knee, modo del Óptico, Time del Vari-Mu, etapas del phaser, nota del delay con "Libre"), botones a la vista sobre las perillas de su panel (`FxStepChoice`), que siguen su carril como una perilla. Lo de cada panel va en el orden de su lista y llena el alto que le da su fila; el panel con
  gráfico ocupa el ancho entero cuando a su lado quedaba un hueco (delay, reverb, gate, flanger, phaser).
  **Presets** (pedido del usuario, #141): todo efecto nativo lleva arriba su barra de presets (`PresetBar`), una banda
  propia igual en todos (#261): PRESET en el color de la familia, ◀ ▶ para recorrerlos (saltean "Personalizado"), el
  menú grande y los botones. **Default**
  primero (cómo arranca: `InsertSlot.New` más `StartSlot`, lo mismo que "+ Efecto"; en el compresor, el preset
  `CompPresets.DefaultId`), los propios del efecto (compresor por categoría, EQ, reverb, delay) y los del usuario en
  "Mis presets" (`FxPresets`, `%APPDATA%\XTreme\efectos.json`, para todos los proyectos), y "Personalizado" (Manual en el
  compresor) si no está en ninguno. Al lado van los botones propios del efecto (analizar del compresor, agrandar
  del EQ), guardar y borrar. Un preset cambia los ajustes y deja el ON, el sidechain y la ventanita (`FxPresets.Apply`).
  Nada arranca para guitarra: compresor nuevo, pista nueva y grabación van en Default. Todo compresor (`TrackInserts.HasCompGraph`)
  lleva su gráfico (`CompGraph`): la curva con un punto donde está la señal, y a la derecha unos 4 s de entrada y salida
  (barras desde abajo) y reducción (desde arriba, en el acento), leídos del `CompScope` del efecto que suena. Todo
  efecto nuevo agrega su lista en `FxDefs`, no una interfaz propia. Dry, Wet y Salida son comunes y separados (`FxDefs.Common`, pedido del usuario; Reverb y Delay nuevos en una pista, una carpeta o el Master arrancan con Wet 25 % y Dry 100 %, en un bus con Wet 100 % y Dry 0 %; el doble clic en Dry y Wet vuelve a esos valores, #187), y prender y apagar se funde (`FxShell`).
- **EQ** (`Controls/EqEditor.cs`): 7 bandas. Un chip por banda, y en una campana o estante, **Dinámico** (`EqDynamics`): la
  banda se mueve sola cuando esa zona pasa del Umbral, hasta su Rango (negativo corta, positivo realza), con su Ataque y
  Release; en el gráfico, una barra muestra el recorrido y un punto dónde está ahora (`EqEditor.Live`, 30 veces por
  segundo). El botón de expandir abre la **vista grande** (`ShowEqBig`): el mismo editor en una ventana propia, con un
  gráfico que sigue su alto, que edita el mismo slot y lo sigue (`EqEditor.Follow`); se cierra si el slot se va. El
  espectro en vivo llega ahí solo desde el primer EQ de la pista seleccionada.
- **Los efectos son la cadena de slots del canal** (`RebuildInspectorChain`, de una pista, una carpeta o un bus): una fila por slot
  en la tira, en el orden en que suena (un tipo repetido se numera, "EQ 2"; un VST3 lleva el nombre del plugin). Se
  rearma solo cuando cambia el canal o la lista (sus refrescos van en `chainRefresh`). Su panel no vive en el
  inspector: se abre en su ventana. El espectro en vivo llega a la vista grande del EQ de la pista elegida.
- **Envíos y salida** (`RebuildInspectorSends`, en `MainWindow.Buses.cs`): se rearman solo cuando cambia la pista, su
  carpeta o los buses (nombres, efecto principal, ruteo; sus refrescos van en `sendRefresh`). El PRE: el envío sale
  antes del fader, después de los efectos y el paneo; el mute igual lo calla. Su id estable es `bus:<id del bus>` y el
  nombre que muestra es el del bus (`SendTitles`); las filas también son íconos de la columna plegada (`sendRows`).
- Con su encabezado "Toma" se pliega a una columna de íconos de 44, como la barra del proyecto, y se recuerda.
  Plegada, debajo de los dos botones muestra el canal seleccionado (`MainWindow.InspectorRail.cs`, pedido del
  usuario): un ícono por sección en el orden en que suena (Mezcla, los slots, los envíos a buses, Transponer), con una rayita entre esos grupos, en acento si está prendida y tenue si no; un clic despliega el inspector y, en un efecto, abre su ventana. Se rearma solo cuando cambia la lista de secciones (`Section` guarda su ícono y su
  ON en `sectionLooks`).
- **Transponer** (`MainWindow.Transpose.cs`, pedido del usuario): su propia tarjeta, después de Delay, con el ícono
  `FxIcon.Transpose`. −24 a +24 semitonos en `Track.Transpose`, mismo largo. Se toca en una escala dibujada
  (`TransposeScale`, #316): el nombre y el intervalo ("5ª justa arriba", `IntervalText`) a la izquierda, los semitonos
  grandes a la derecha y una regla con una marca por semitono, las octavas marcadas y la segunda de cada lado
  sombreada, encendida de 0 al valor. Clic o arrastre, ruedita (Shift: octava) o flechas (RePág/AvPág: octava) solo
  muestran adónde va (el valor anterior queda como un anillo) y lo fijan al soltar o al quedarse quietas; doble clic o
  Alt+clic: 0; no tiene ON/OFF: en "Original" (0) la
  tarjeta queda atenuada como un efecto apagado. Se calcula en segundo plano (`PitchShifter`, unos 4 s por 3 min en
  8 núcleos) y queda en `cache/`; los valores probados en la sesión quedan ahí (volver a uno es inmediato) y lo que no
  se usa se borra una vez al abrir el proyecto. Mientras tanto la pista suena como estaba y **se ve que está
  procesando** (pedido del usuario): sobre sus clips, la onda atenuada, una barra de progreso abajo y "♪ Transponiendo…
  40%" (`TimelineView.DrawTransposing`); el knob Tono en gris con el porcentaje en lugar del nombre. Al llegar, el clip pasa con un crossfade de 10 ms y, si tardó más de 1 s, un toast avisa que ya suena
  transpuesta. Mover el slider cancela lo que ya no se quiere. Todo cambio pasa por `SetTranspose(pista, semis)`
  (inspector, mixer y knob).
- **El canal muestra que está transpuesto**: en el encabezado de pista del timeline, una etiqueta "♪ +2" después del
  nombre (acento; gris mientras se calcula, con `TimelineView.TransposeProgress`); en la tira del mixer, "+2"
  debajo del nombre; en el encabezado alto, el knob Tono (ver la fila de knobs). Mientras se calcula, `transposeClock` refresca esas marcas (`MixerLayout.Transpose`) cada 250 ms.
- El espectro en vivo del EQ sale de un `SpectrumTap` colgado solo de la pista seleccionada mientras el EQ está a la
  vista: el hilo de audio copia a un anillo preasignado y la FFT corre en el `Tick` de la UI.

## Encabezados plegables

Todo encabezado plegable (inspector, Color, Historial) es un `Controls/SectionHeader`: la franja entera reacciona al
hover y se pliega con un clic en cualquier parte fuera de sus botones.

## Barra del proyecto (derecha, `MainWindow.Color.cs`)

- Lo que es del proyecto (lo de la pista va al inspector). Ocupa la altura de la preview y el timeline, se pliega a
  una columna de íconos y recuerda pestaña y secciones plegadas. Las ediciones de Color pasan por `ColorEdit(...)`.
- Las dos barras laterales (esta y el inspector) cambian de ancho arrastrando su borde interior (240–600, el escenario
  conserva 360; doble clic vuelve a 290) y lo recuerdan en `config.json` (`sidebarWidth`, `projectBarWidth`):
  `MainWindow.Sidebars.cs`.
- Efectos de Color: Nitidez, Claridad y Resplandor (`Audio/VideoDetail.cs`; los looks HDR y Soñado suman ahí su parte, `VideoDetail.For`: en la preview, sobre el cuadro decodificado en
  `LiveClip.UpdateVideo`, después del color y el grano, con su radio según lo que muestra el panel, `LiveClip.VisibleHeight`;
  en la exportación, `boxblur` + `lut2`, `unsharp` y `lutrgb` + `boxblur` + `blend` sobre el panel ya recortado), viñeta, grano y barras de cine (No / 1.85 / 2.00 / 2.39 / Libre, con "Alto de las barras" en % del
  panel solo en Libre). En la preview las barras se dibujan fuera del fundido del clip (`PreviewVideoFx.DrawLetterbox`):
  con la opacidad encima dejarían ver el video.
- Pestañas Color / Patrones / Biblioteca / Historial (`FitProjectTabs`: se juntan si la barra se angosta y, si los
  nombres no entran, muestran sus íconos). **Patrones** (`MainWindow.Patterns.cs`, pedido del usuario, 2026-11-01): la
  lista de patrones MIDI, cada uno con su vista chica (`Controls/PatternPreview`, una vuelta en el color de su primera
  pista). Arrastrar una fila a una pista MIDI suma un clip enlazado donde cae (marca igual que un sample; fuera de una
  pista MIDI no cae). Cada fila renombra, abre el piano roll (también con doble clic, en el clip elegido si es del
  patrón, si no en el primero) y elimina: la confirmación en la fila ofrece **Borrar clips** o **Dejar únicos** (decisión
  del usuario: elegir cada vez). Se arma solo a la vista, desde `RefreshHistory`.
- Biblioteca (las tomas, usadas o no, importadas o grabadas, con sus tags en cajitas debajo del nombre, en acento mientras están en el timeline, siempre enteras: la que no entra baja a otra línea, y la hora, duración y peso se recortan antes y, con menos de 64 px, van en su propia línea (`Controls/FitRow`: nunca pasan por debajo de los botones); la papelera de
  cada fila sin usar, con confirmación en la fila; "Desvincular no usados", que saca del proyecto los vinculados sin usar sin preguntar, y "Borrar no usados", que manda a la Papelera los archivos propios tras confirmar (dos botones: pedido del usuario, 2026-11-04); y al escuchar una toma, un mini reproductor bajo
  su fila: onda para saltar con clic o arrastre (`Controls/TakeScrubber`), ±5 s, tiempo y parar, por la salida de
  Windows con `TakePreview`, que hace los saltos con un fundido corto) e Historial (la pestaña por defecto) viven en `MainWindow.History.cs` y se arman
  solo mientras alguna está a la vista (`RefreshHistory`); lo que hacen con el proyecto y los archivos vive en
  `ProjectHistory` (Projects).
- La Biblioteca tiene arriba Proyecto / Samples (`config.json`: `libraryView`). **Samples** (`MainWindow.Samples.cs`) son
  carpetas de la PC, de la máquina y no del proyecto (`SampleLibrary`: `config.json` "sampleFolders", "sampleFavorites";
  `samples.idx` guarda archivos, largos y ondas). Se abre recién la primera vez que se muestra: arranca del índice y lee
  el disco atrás, y cada carpeta se vigila con un `FileSystemWatcher`. Una carpeta que no está sale "Sin conexión" y
  conserva sus archivos. La lista (`Controls/SampleList`) se dibuja a mano y solo lo visible: con foco, flechas, Inicio,
  Fin, Re Pág y Av Pág se mueven, izquierda y derecha pliegan, Espacio escucha, Enter inserta y F marca favorito
  (`HandleKey`, antes que los atajos del proyecto). Se marcan varios archivos como en cualquier lista (pedido del usuario):
  Ctrl+clic suma o saca uno, Shift+clic y Shift+flechas marcan el tramo; arrastrar, insertar o el menú de uno marcado
  llevan todos. Con texto buscado o un filtro (Todo / Audio / Video / MIDI / Favoritos) la lista es plana. Los `.mid` también se listan
  (pedido del usuario, #200; `SampleFile.IsMidi`): su largo sale a su tempo y su onda es cuántas notas suenan en cada tramo; no se
  escuchan, y el reproductor muestra sus notas como un piano roll chico (`TakeScrubber.SetNotes`, #206; un archivo que no suena muestra la onda de su fila), y al insertarlos o soltarlos van por `ImportMidi` a la pista MIDI elegida (o la de abajo del mouse) o a pistas nuevas. Largo y onda se
  piden solo para las filas a la vista, en un hilo aparte; más de `MaxWaveSec` (120 s) da largo sin onda. El pie es el
  reproductor: onda para saltar (sacarla de la onda hacia arriba o abajo, o arrastrar el nombre, lleva el sample al timeline), Loop, Auto (escucha al seleccionar), Con imagen (solo con un video) y volumen. Escucha
  hasta `MaxListenSec` (10 min) por `SampleVoice`, que suma a la salida después del limitador, igual que el click: no
  entra en tomas ni exportaciones, y sin driver abierto sale por Windows. Grabar la corta.
  Doble clic o Enter insertan en el cursor; arrastrar marca dónde cae (`TimelineView.Drop.cs`, con la grilla, Alt la
  libera). Soltar sobre una pista la usa si ese tramo está libre; si no, o sobre un lugar vacío, va a una pista nueva. Varios caen uno abajo del otro, desde la pista de abajo del mouse (o la elegida, al insertar): cada uno en la siguiente pista visible que lo toma y los que no, en pistas nuevas al final (`AudioImport.Stack`, la misma regla para dibujar y para soltar). Mientras se arrastran, un fantasma por archivo con su largo (`TimelineView.DropGhost`), los de pistas nuevas en filas debajo de la última. Todo pasa por `ImportAudio`, que copia a
  media/ o vincula el archivo donde está (`ImportMode`, `config.json` "import.mode": pregunta con `ImportDialog`, cuyo "No mostrar más" fija la respuesta, o lo que diga Configuración → Guardado y exportación). Un vínculo guarda la ruta entera (`Media.Linked`, marca Vinculado en la Biblioteca): si el archivo se va, el clip queda offline; borrar la toma no lo toca y el paquete lleva su copia. La fila de una toma vinculada copia a media/ ("Copiar al proyecto") y la de una que perdió su archivo (marca Sin conexión, también en el menú del clip gris) lo reencuentra: "Elegir el archivo…" (misma duración, cualquier nombre) o "Buscar en una carpeta…" (todo lo que falta, por nombre y tamaño); cada una es un paso de deshacer (`MainWindow.Linked.cs`, `LinkedMedia`). En la vista Proyecto una toma se arrastra igual desde su fila (`DraggableTake`,
  `ClipEditing.PlaceMedia`): no copia nada, se puede usar varias veces y una que estaba fuera del timeline vuelve con su corte y su canal. Lo que NAudio no lee lo decodifica FFmpeg (`MediaDecoder`) y entra
  como WAV aunque se elija vincular, así el proyecto abre sin FFmpeg; un video con Con imagen entra además con el archivo tal cual y pista con panel.
- Historial es para volver atrás (pedido del usuario, 2026-10-29; antes mostraba la actividad, que se sigue anotando
  en el proyecto y lee `xtlab proyecto historial`). Tres secciones, en `MainWindow.Steps.cs` salvo Exportaciones:
  - **Pasos**: los pasos de deshacer de esta sesión con nombre ("Moviste «Toma 1»", "Volumen de «Bajo»: -3,0 dB"), el
    más nuevo arriba, y abajo de todo cómo estaba al empezar (al abrir, al volver a una versión o al borrar canales sin
    usar). Clic en un paso = ir ahí de una (`UndoManager.JumpTo`, por el mismo `RestoreEdit` que Ctrl+Z); el actual va
    con el acento y los deshechos en gris hasta que una edición nueva los saca. Los nombres salen de comparar el antes
    y el después (`EditSteps.Describe`, en Projects); una grabación trae el suyo (`EditTimeline(step:)`). La lista se
    redibuja 400 ms después de la última edición (`ScheduleSteps`), nunca por cuadro mientras se arrastra un fader.
  - **Versiones**: las de `historial/` (se leen fuera del hilo de la UI, `ReloadVersions`): las que se guardan solas,
    las de "Guardar versión" (con nombre) y una por cada exportación, que guarda la mezcla con la que salió. Las que
    tienen nombre o exportación no se borran solas (`historial/versiones.json`, `ProjectStore.SaveVersion`,
    `RenameVersion`). Volver guarda antes lo actual como otra versión ("Antes de volver a…") y trae solo el tema: las
    tomas grabadas después, las exportaciones y el nombre se quedan (`ProjectStore.Restore`).
  - **Exportaciones**: cada una con su "Volver a la mezcla de esta exportación" si tiene versión (las de antes de
    esto no); mandarla a la Papelera suelta su versión (`UnlinkExport`).
- "Salió / volvió al timeline" se registra al guardar (`ProjectStore.Serialize` → `LogTimelineChanges`).

## Preview y monitor "En vivo"

- **Preview**: los paneles se calculan con `PanelLayout.Compute` sobre un lienzo de 1080 px de alto, **la misma
  cuenta que usa la exportación**: lo que se ve tiene que ser lo que se exporta. Encuadre: cover-fit + zoom/paneo/espejo
  del clip. Etiqueta con el nombre de la pista abajo a la izquierda.
- **Video de la preview**: cada clip tiene su `Video/VideoDecoder` (un hilo que va unos cuadros adelante). Lee con
  FFmpeg dentro del proceso (`NativeVideoSource`, en `XtremeDaw.Audio`: los DLL de `lib\ffmpeg`, en el procesador hasta
  1080p y en la placa de video arriba de eso); si no están los DLL o el archivo no abre, con ffmpeg.exe por pipe
  (`PipeVideoSource`, un proceso por salto). Un clip arranca su decodificador 1 s antes de empezar (`LiveClip.PreRollSec`),
  así los cortes no congelan la imagen, y un panel no muestra un cuadro hasta tener uno de ese momento (`LiveClip.Fresh`).
  Un decodificador que no se usa (`VideoDecoder.Idle`) cierra el archivo a los 3 s.
- **Transiciones** (ver Timeline): `LiveTrack.UpdateTransitions` corre en cada tick antes de pedir los cuadros, con la
  misma cuenta que la exportación (`ClipTransitions`); también le pasa al motor el fundido de audio de cada clip
  (`LiveClip.AudioJoins`, cambiado entero solo cuando cambia, como `MixRenderer`): en un fundido cruzado los dos clips muestran video pasando su
  borde (`LiveClip.Lead` / `Tail`, así su decodificador arranca antes) y `PreviewView` dibuja el que sale y encima el
  que entra con su peso. Se ven también en pausa (los fundidos propios del clip, solo reproduciendo, como siempre).
- **"En vivo"** (`Controls/LiveMonitor.cs`), a la derecha de la preview: la cámara cruda, nunca la composición. La
  abre `Video/LiveCamera.cs` (un solo FFmpeg, `CameraCapture`), que también alimenta el video de la toma (`VideoTakeWriter`).

## Timeline

- **Clips**: fondo `Hover` al 40 % (acento al 16 % si está seleccionado), borde `Line` (acento al 60 % si está seleccionado), onda
  acento al 80 % (entero si está seleccionado, `Faint` si la pista no suena), sin el nombre adentro; sin archivo, gris con "Sin conexión…". Con
  video y carriles de 52 px o más, la tira de miniaturas (`Video/VideoThumbnails.cs`) con la onda abajo sobre
  un degradé (del color de la pista si tiene uno; si no, blanca). Arriba a la derecha, un badge dice si el medio es solo
  audio (nota) o audio + video (cámara), sin tooltip (`MediaBadge`). Pista seleccionada (y carpeta) con un borde de acento alrededor del encabezado, como una tira seleccionada del mixer y por dentro del borde de la tarjeta y del recuadro de carpeta (`DrawSelectedFrame`), y un tinte de acento del 6 % en encabezado y carril (`SelectedTint`), pedido del usuario: sin barra al costado; playhead de acento con triángulo.
- **Bordes de clip**: en cada lado dos manijas chicas como la de la columna (pestañita oscura con dos rayitas), una por
  mitad del clip (`DrawEdgeHandles`): la de arriba, donde termina el fade, mueve el fade; la de abajo, en el borde,
  recorta. El cursor lo dice (diagonal como la línea del fade, ↔ para recortar) y se ilumina la manija que vas a agarrar.
  **Ctrl+arrastrar la de abajo estira** (pedido del usuario; Shift ya marca rangos): el otro borde queda fijo, con imán a
  la grilla (Alt lo saca), sin tope (pedido del usuario: solo frenan el largo mínimo del clip y el cero; `StretchEdge`), y una
  etiqueta al lado del puntero dice la velocidad (la mitad de largo es ×2, pedido del usuario). Con Ctrl apretado, aun
  sin mover el mouse (`SetModifiers`), las manijas de abajo se ven en acento con dos flechas (`StretchTick`) y el cursor
  es la flecha doble con un cronómetro (`ToolCursors.Stretch`), pedido del usuario. "Ajustando al tempo… N%" sale solo
  en el clip que espera su sonido (`ClipRenderProgress`). Al soltar se hace el sonido, como con el tempo; un clip bloqueado no se estira.
- **Bucle** (pedido del usuario, como GarageBand; `Controls/TimelineView.Loops.cs`, `ClipLoops`): la esquina de arriba a la
  derecha de un clip (`LoopGrab` × `LoopBand`, sobre la manija del fade) muestra el ícono del bucle (`ToolCursors.LoopGlyph`) y el
  puntero de las dos flechas (`ToolCursors.Loop`). Arrastrarla repite el clip hasta donde soltás, la última vuelta cortada
  ahí; mientras se arrastra se ven las vueltas que van a quedar (`DrawLoopGhost`). En una toma cada vuelta es un clip
  (`Clip.LoopOf`) con el ícono del bucle en su pestaña: se rehacen cuando cambia el original (`ClipLoops.Take`/`Keep` en
  `EditTimeline` y en el inspector), una editada sola sale del bucle y borrar el original se las lleva. Agarrada en una
  vuelta, actúa sobre el original. Terminar dentro de la primera vuelta saca el bucle. En un clip MIDI
  (`MidiClip.LoopBeats`, `MidiEditing.Loop`) y en un bloque de automatización (`AutomationBlock.LoopSec`,
  `Automations.Loop`, la punta derecha de su tira) las vueltas son del mismo clip: cada una lleva el ícono y lo que se
  edita en cualquiera cambia todas. El final de la primera vuelta (la primera costura) tiene su propia manija de borde
  (#171: `MidiEditing.Pass`, `AutomationBlock.SetPass`): cambia el largo del original y todas las vueltas lo siguen; el
  clip conserva cuántas vueltas tenía. Un clip bloqueado no tiene bucle.
- **Varios clips por pista**: Alt+X divide en el cabezal y D duplica (`ClipEditing.Split` / `DuplicateMany`; con
  varios clips marcados, aunque estén en varias pistas, los copia en bloque después del final del bloque; con el imán prendido, el bloque se corre el próximo número entero de pasos del imán, `ClipEditing.DuplicateShift`, pedido del usuario).
- **Copiar y pegar** (`ClipClipboard`, `MainWindow.Timeline.cs`): Ctrl+C / Ctrl+X toman el clip elegido o los
  marcados; Ctrl+V los pega con el comienzo en el cabezal y la pista de arriba en la elegida (o en la suya), con la
  misma distancia entre pistas; si no entran abajo, el bloque sube. Quedan marcados y, parado, el cabezal salta al final
  del bloque, así otro Ctrl+V pone la copia siguiente. Solo en memoria y, si lleva tomas, solo dentro del mismo
  proyecto. Lleva también clips MIDI (con sus notas), que solo caen en pistas MIDI, como las tomas solo en pistas de
  audio; un bloque solo de clips MIDI se pega en otro proyecto (sin pista elegida, en su primera pista MIDI). Los clips
  de una pista comparten su panel y se ve el de arriba (el último), igual en la preview (`LiveTrack.ShownClip`) y en
  la exportación (`ExportTakeSpec.Panel`).
- **Arrastrar a otra pista** cambia la pista **y el tiempo** a la vez: el original queda atenuado y un fantasma
  punteado marca dónde cae (`DrawGhost`). Con Ctrl se intercambia con el clip de abajo. Soltado debajo de la última
  fila, un clip de audio o MIDI va a un canal nuevo de su tipo, al final (pedido del usuario, #309: un carril
  «Canal nuevo» lo anuncia, `DrawNewTrackLane`; `TrackCopies.MoveToNew`: el MIDI lleva el mismo instrumento, nada más
  del canal de origen).
- **Grilla e imán** (pedido del usuario, 2026-11-03: los mismos nombres en todos lados, los de la industria): el imán, la
  grilla de la regla y los del piano roll usan los valores de `Subdivisions` (`SnapChoices`), de mayor a menor y con un
  separador entre grupos: Compás, 1/2 a 1/64; tresillos 1/2T a 1/32T; puntillos 1/2D a 1/16D. Un puntillo no entra
  justo en el compás: sus líneas **arrancan de nuevo en cada compás** (`SnapGrid.Bar`, también al cuantizar). El imán
  suma **Auto** (la línea dibujada más cercana, cambia con el zoom) y Sin imán; los nombres viejos guardados en
  config.json ("1/4beat", "cell"…) pasan al valor nuevo al abrir (`Subdivisions.Migrate`). El clic derecho en los números de la
  regla elige cómo se dibuja la grilla (`TimelineView.GridMode`, `timelineGrid` en config.json): **Auto** (por
  defecto) se afina con el zoom, **Igual que el imán** dibuja sus pasos y un valor fijo mantiene ese espaciado
  con cualquier zoom; las líneas a menos de 6 px no se dibujan (`MinGridPx`, `FixedGridBeats`, `FineBeats`). Todo en
  el mapa de tempo (ver Pista de tempo).
- **Mover con grilla** (uno o varios; pedido del usuario, 2026-11-02, antes conservaba el desfasaje): el comienzo del
  clip **cae en una línea** de la grilla, así que moverlo lo vuelve a poner en tiempo (`ClipEditing.Drag`; con varios,
  cae el agarrado y los demás lo siguen). Para no perder el tiempo de una toma tocada, Alt. Lo mismo en clips MIDI.
- **Shift+arrastrar el cuerpo de un clip lo copia** (pedido del usuario, como FL Studio; uno o varios, también a otra
  pista): el gesto es el de mover y al soltar queda una copia donde estaba (`cloning`, `DrawLeftBehind`, el contorno
  punteado; `MidiEditing.LeaveCopies`). Un clip bloqueado no se copia así. Fuera del cuerpo, Shift sigue marcando rangos.
  **Shift+clic** en un clip, sin moverlo (#311), marca desde el clip elegido hasta ese: todos los que empiezan entre los
  dos, en las pistas que van de una a otra (`BoxRange`, `shiftAnchor`).
- **Imán a otros clips**: el selector al lado del imán (`SnapClipsBox`: No / Inicio / Final /
  Ambos, `snapToClips` en config.json, No de entrada) elige qué borde visible del clip que movés se pega a los bordes
  visibles de los otros, a menos de 8 px (`EdgePullPx`, `SnapEdges`). Con los dos imanes la grilla ubica el clip y el borde
  solo lo corrige si el clip ya ubicado queda a menos de 8 px: nunca salta más que eso. Mientras está pegado, una línea de acento
  cruza las pistas en ese borde (`stuckTo`, la guía). Anda también con la grilla en "Sin imán". Nada más tira: ni el cabezal ni el cero. Con los dos apagados, o con Alt, se mueve libre, de a muestra
  (`ClipEditing.ToSample`: el milisegundo eran 96 px al zoom máximo). Para volver a ponerlo en sincro, el clic derecho
  tiene **"Alinear a la grilla"** (Shift+Q, `AlignClips`): lleva el comienzo de la toma a la línea más cercana con la subdivisión del imán
  (`ClipEditing.AlignToGrid`; deshabilitada con el imán apagado). Sobre un clip marcado, alinea o quita todos los marcados.
- **Detectar tempo** (clic derecho en un clip de audio, pedido del usuario): escucha lo que suena del clip (lo recortado
  no cuenta) en segundo plano con `TempoDetector` (el clip muestra la barra y "Escuchando el tempo… N%", como al
  transponer; de a un clip por vez; si se está ajustando al tempo, pide esperar) y abre un menú en el mismo lugar con los BPM, qué tan seguro está y
  "Poner el proyecto a N BPM", al doble o a la mitad (es el mismo pulso: no distingue 87 de 174) o dejarlo. Eso mueve la
  grilla y no estira nada: el audio ya está a ese tempo (`SetTempo(valor, follow: false)`). El tempo
  no tiene deshacer, así que el toast que lo confirma trae "Volver a N" (`MainWindow.TempoDetect.cs`). Menos de 3 s,
  silencio o sin pulso claro: un toast lo dice.
- **Detectar escala y acordes** (clic derecho en un clip de audio, pedido del usuario): escucha lo que suena del clip
  (lo recortado no cuenta; transpuesto si la pista lo está) con `ScaleDetector` en segundo plano, de a un clip por vez
  (la barra y "Detectando la escala… N%" sobre el clip). Al terminar, `Dialogs/ScaleDialog` muestra la escala, qué tan
  segura es, la relativa, sus notas y los acordes, del que más suena al que menos, con un check "Agregar los acordes al
  timeline" (`scaleMarkers` en `config.json`, apagado de entrada): al aceptar los pone en la franja de acordes, en un
  paso de deshacer, y reemplaza los detectados antes en ese tramo. Lee hasta
  10 minutos. Siempre da acordes, nunca sigue la melodía (para eso está Extraer MIDI): solo queda sin acorde lo que está
  en silencio. La ventanita da dos seguridades en %, de la escala y de los acordes, que son estimaciones y no tasas
  medidas de aciertos. Si la menor de las dos no llega a 85 % prueba otro método y se queda con la lectura más segura
  (`ScaleDetector.DetectBest`): sin voz ni batería, con Demucs (`scale.skip` en el menú del clip la saltea, y si se
  graba una toma espera); sin esos modelos, por notas con `AudioToMidi`. Una escala dudosa igual se muestra, avisando "Poco clara"; menos de 3 s, silencio o sin notas (ruido): un toast lo dice (`MainWindow.ScaleDetect.cs`).
- **Extraer MIDI** (clic derecho en un clip de audio → Acordes / Melodía, pedido del usuario, como Studio Pro): saca las
  notas de lo que suena del clip (lo recortado no cuenta; transpuesto si la pista lo está) con `AudioToMidi` (Basic
  Pitch) en segundo plano, de a un clip por vez (la barra y "Sacando las notas… N%" sobre el clip), y las pone en un
  clip MIDI del mismo tramo en una pista MIDI nueva justo debajo, en su carpeta ("Nombre (MIDI)"). Arrastrar el clip a
  una pista MIDI hace lo mismo en esa pista, con el modo elegido la última vez (`toMidiMode` en `config.json`): el clip
  de audio no se mueve y el fantasma dice "Extraer MIDI a …" donde caerán las notas. Es un paso de deshacer y el toast
  trae "Ver notas" (abre el piano roll). Sin notas claras o sin el modelo, un toast lo dice (`MainWindow.ToMidi.cs`,
  `TimelineView.ExtractMidiRequested`).
- **Separar en stems** (clic derecho en un clip de audio, pedido del usuario): `Dialogs/StemsDialog` elige cuántos (2:
  voz y resto; 4: voz, batería, bajo y resto; 6: suma guitarra y piano) y la calidad (Rápida, Alta, Máxima), con el
  tiempo estimado; abre en lo último elegido (`stemsLayout`, `stemsQuality` en `config.json`; de entrada 4 y Alta). Usa
  lo que suena del clip, como Extraer MIDI, con `StemSeparator` (Demucs, en la placa de video si coincide con la CPU; hasta que se prueba, el tiempo dice "hasta") en segundo plano, de a un clip por vez ("Separando… N%"
  sobre el clip); mientras corre queda un toast fijo con "Cancelar" (también en el menú del clip), que corta en el acto, y si se graba una toma espera. Cada
  stem queda en un WAV nuevo en media/ y en su pista, en una carpeta nueva bajo la del clip, con la ganancia del clip y
  en su lugar. La pista original queda silenciada y sin tocar. Guitarra o piano vacíos no crean pista: van a "Resto", que
  es la mezcla menos los demás, así los stems siempre suman la mezcla. Es un paso de deshacer (`MainWindow.Stems.cs`,
  `AudioImport.WriteIn`, `AddStems`). Arriba a la derecha, **Simple / Avanzado** (pedido del usuario, #146): Simple es
  lo de arriba; Avanzado cambia cantidad y calidad por el modelo (General, Por stem, 6 stems: `StemSeparator.Model`),
  qué stems (casillas; lo demás va a Resto, `Settings.Only`: Por stem corre solo los modelos de los elegidos), las
  pasadas (1, 2, 4, 8), el rango (clip entero o la selección del timeline sobre el clip, si la pista está en ella) y
  el original (`StemOriginal`: silenciar la pista, dejarla o quitar el clip; con selección no se quita, y un clip
  bloqueado se silencia). Cada modo recuerda lo suyo (`stemsAdvanced`, `stemsModel`, `stemsOnly`, `stemsPasses`,
  `stemsRange`, `stemsOriginal` en `config.json`).
- **Selección de varios clips** (pedido del usuario): arrastrar en la parte vacía de las pistas (o debajo de la
  última, o en un carril de automatización fuera de sus bloques) dibuja un rectángulo que marca cada clip que toca, tomas, clips MIDI y bloques de automatización (#281, `BoxAuto`); con Ctrl suma a lo marcado (`boxed`,
  `UpdateBox`). Un bloque marcado se agarra por su franja de arriba para mover todo lo marcado (`autoCarrying`, `DragAutoCarry`; `ClipsMoved` lleva también los bloques), Ctrl+clic en su cuerpo (o en la franja, sin mover) lo suma o lo saca (`ToggleBlock`), y Supr y Seleccionar todo los incluyen. Shift+arrastrar deja una copia de cada bloque (`CloneIds`, `LeaveBlockCopies`), D los duplica con el resto (`MidiEditing.DuplicateMany`), Ctrl+C/X/V los llevan (`ClipClipboard.BlockItems`: cada bloque vuelve a su carril, corrido el mismo tiempo) y un borde o el bucle de uno marcado, clip o bloque, edita todos (`autoGroup`, `autoGrouped`, `GroupBlock`; `ClipsEdited` lleva los bloques). **Ctrl+clic** en un clip lo suma o lo saca (en una toma, al soltar sin mover: Ctrl+arrastrar sigue
  intercambiando con el clip de otra pista; `ToggleBoxed`). Agarrar
  uno marcado por el cuerpo mueve todos el mismo tiempo, cada uno en su pista (`StartCarry`, `DragCarried`, evento `ClipsMoved`: un solo paso para deshacer); por un borde, un fade, el estiramiento (Ctrl) o el bucle, edita todos igual (pedido del usuario, `TimelineView.Group.cs`, evento `ClipsEdited`, un solo paso): el agarrado va con la grilla y los demás corren ese borde el mismo tiempo, cada uno con sus límites; un clip MIDI toma el borde derecho como el final de una toma y el izquierdo como su comienzo (uno de patrón no tiene), y los fades son solo de tomas. Supr los saca a
  todos. Un clic sin mover, o cualquier selección común, deja uno solo.
- **Rango**: la herramienta Rango, o Shift+arrastrar con Seleccionar desde fuera de un clip, marca un tramo; arrastrando hacia arriba o abajo
  suma las pistas visibles que cruza (`rangeTracks`). Supr o "Borrar el rango" del clic derecho lo saca de sus clips
  (`ClipEditing.DeleteRange`) dejando el hueco; Shift+Supr o "Borrar y cerrar el hueco" además corre lo de después en
  esas pistas (`ClipEditing.DeleteRangeClosing`; las otras pistas y el A–B no se mueven; los marcadores, solo si el rango abarca todas las pistas). "Repetir este
  rango" lo pasa al rango A–B. La selección se va con un clic, Esc o una edición. Corta también los clips MIDI (`MidiEditing.DeleteRange`): una nota que sobresale del rango
  conserva lo que queda de ella, y a la derecha vuelve a atacar donde termina el rango.
- **Herramientas** (`Controls/TimelineView.Tools.cs`, `TimelineView.Tool`): solo cambian qué hace apretar sobre los
  carriles; encabezados, regla, marcadores y menús andan igual con cualquiera. **Dividir** corta el clip en el clic
  (imantado; Alt, libre) y mientras pasás muestra la línea del corte. **Borrar** saca lo que toca un arrastre, en un solo
  paso de deshacer (los clips se atenúan hasta soltar). **Escuchar**, mientras mantenés apretado sobre un clip, toca
  esa pista sola desde ahí (`LiveTrack.ListenSolo`: un solo del motor que no se guarda ni prende la S); al soltar se
  apaga con el fundido y el cabezal vuelve a donde estaba. **Zoom**: clic ×2, Alt+clic ÷2, arrastrar muestra ese tramo.
  **Mano** arrastra la vista. Ctrl+clic en un clip lo suma a la selección o lo saca con cualquier herramienta salvo
  Rango, Zoom y Mano (#311: con el Lápiz lo tomaba de pincel). Cada una tiene su puntero (`Controls/ToolCursors.cs`: el mismo ícono que su botón,
  blanco con borde negro; la mano sale del trazo de lucide porque Segoe no tiene). Teclas 1 a 9 en el orden de los botones (pedido del usuario, #135).
  **En los carriles de automatización** (#281, `Controls/TimelineView.AutoTools.cs`: un bloque responde a todo como un
  clip) Dividir corta el bloque (`Automations.Split`, con su línea), Borrar lo saca, Escuchar toca la pista del carril,
  Rango marca el tramo de su pista, y Zoom y Mano andan igual. Lápiz y Pincel tienen su pincel propio (`autoBrush`): clic
  en un bloque lo toma, clic en un lugar vacío pone una copia (el Pincel, una tras otra arrastrando; donde ya hay un
  bloque no pone); sin un bloque tomado, el clic crea uno. Borrar un rango corta también los carriles de sus pistas
  (`Automations.ClearRange`, desde `ClipEditing.DeleteRange`). Alt+X divide el bloque elegido (o los marcados) en el
  cabezal, y Ctrl+X y Alinear a la grilla (Shift+Q, también en el menú del bloque) valen para bloques.
- **Lápiz y Pincel** (pedido de un colaborador, como los de FL Studio; `Controls/TimelineView.Pencil.cs`, `MainWindow.Pencil.cs`,
  `ClipStamp`): van después de Seleccionar y comparten lo que dibujan (el pincel, `PencilBrush`). Clic en un clip lo
  toma; clic en un lugar vacío de una pista que lo acepta (audio para una toma o un sample, MIDI para un clip MIDI o un
  patrón) pone una copia en el imán (Alt, libre). El **Lápiz** pone una por clic y arrastrar la ubica antes de soltar;
  el **Pincel** cubre un tramo (pedido del usuario: el lápiz es uno a uno): arrastrar a la derecha sigue poniendo copias
  pegadas, en un solo paso de deshacer. Las copias que pisan un clip no se ponen.
  Mientras pasás se ve el fantasma de la copia. Un clic en un patrón de la pestaña Patrones o en un sample de la
  Biblioteca también lo hace el pincel (con cualquier herramienta): un patrón pone usos del patrón (todas sus partes),
  y un sample se importa en su primera copia y desde ahí pinta como esa toma. Elegir el lápiz sin pincel toma el clip
  seleccionado. El tooltip del botón dice qué pinta.
- **Clips que se pisan**: se ve el último de la pista. El clic derecho → "Traer al frente" (`ClipEditing.BringToFront`,
  prendido solo si otro lo tapa) lo pasa al final: arriba en el timeline, en la vista previa y en la exportación. El
  sonido no cambia: los dos suenan.
- **Tono de un clip** (pedido del usuario): clic derecho → Tono, un submenú de −12 a +12 semitonos (`PitchItem`), con los
  que estén en la selección. Se suma al Transponer de la pista hasta ±24 (`Clip.Pitch`, `Clip.SemitonesOn`), no cambia el
  largo y no toca el archivo; usa el mismo render y caché (`RefreshTranspose`). El clip lleva "+2" arriba a la derecha
  (`DrawPitchTab`). Un clip bloqueado no cambia de tono.
- **Invertir** (pedido del usuario: reverse): clic derecho en un clip de audio, con los que estén en la selección; clic
  derecho de nuevo lo deja como estaba. No toca el archivo: `Clip.Reversed` y `ClipEditing.Reverse`, que deja el clip en
  el mismo lugar, pasa lo recortado a la otra punta y da vuelta la curva de volumen; los fades quedan en sus bordes. Suena
  por `ClipSound.Flipped`, un sonido más, así que entra con el fundido de siempre y el hilo de audio no sabe nada; la
  exportación lo toma de `ProjectStore.TrackAudio`. La onda se lee de la otra punta y el clip lleva una flechita arriba a
  la derecha. Deshabilitado en clips con video (`ClipEditing.CanReverse`) y en los bloqueados.
- **Nombre de un clip** (#137, decisión del usuario: se ve solo si tiene uno propio): "Renombrar…" en el menú de un clip de
  audio o MIDI abre la caja de texto sobre su borde de arriba (`MainWindow.Rename.cs`, `ClipNameRectOf`; fuera de pantalla,
  `AskName`). En blanco le saca el nombre. `Clip.Name` / `MidiClip.Name` (`ClipEditing.Rename`, `MidiEditing.Rename`): una
  vuelta del bucle renombra el original, un clip de patrón renombra el patrón. Se dibuja arriba a la izquierda, en una
  pestaña oscura como las de la derecha (`DrawClipName`), y es el nombre del clip en el historial y en Stems, Extraer MIDI…
- **Bloquear** (`MainWindow.Locks.cs`): "Bloquear clip" en el menú del clip y "Bloquear pista" en el de la pista (también
  en las MIDI, cuyos clips todavía no se bloquean sueltos), con tilde (`MenuCheck`). La regla es una sola, `ClipEditing.IsLocked` (`Clip.Locked` o `Track.Locked`), y la usa
  xtlab: lo bloqueado no cambia de lugar, de largo ni de pista, no se divide ni se borra (`Replace`, `Split`,
  `RemoveClip`… lo rechazan); borrar un tramo lo saltea y "cerrar el hueco" deja quieta su pista. El volumen, los
  fades, la curva y el encuadre sí se tocan. En el timeline un clip bloqueado lleva un candadito arriba a la derecha
  (a la izquierda del ícono de audio/video) y no muestra las pestañas de recorte; agarrarlo lo elige pero no lo mueve.
  Una pista bloqueada lleva el candado después del nombre. No se puede armar una pista con tomas bloqueadas, y una toma
  que igual caiga sobre ella va a una pista nueva (`TakeRecording.AddTake`). Cada negativa avisa con un toast que trae
  "Desbloquear" (uno cada 2 s como mucho).
- **Congelar** (#240, `MainWindow.Freeze.cs`): "Congelar" / "Descongelar" en el menú de la pista, junto a Efectos (el
  mismo en el timeline y en la tira del mixer). Renderiza en segundo plano, con un toast con barra y Cancelar, y congela
  en un paso de deshacer. Congelada, la pista bloquea como una bloqueada y además sus slots, su instrumento y su
  Transponer (`Track.EditLocked`; el toast trae "Descongelar"); en el encabezado del timeline, un botón con el copo a la
  izquierda del ● (`FreezeRect`, `FreezeRequested`; el copo se dibuja a mano, `Snowflake`: el glifo de Segoe parecía un termómetro) congela o descongela con un clic y queda en el acento mientras está
  congelada (no en el encabezado compacto); en la tira del mixer lleva un copo, y sus clips se dibujan apagados (`FrozenDim`), cada uno con un copo en su esquina (`FrozenBadge`), bajo la onda del render (`TimelineView.DrawFrozen`, un
  `LiveClip` sintético con su caché de ondas). Sus plugins se cierran (`FreezeHolds`) mientras el render toca o carga;
  si falta, la pista suena en vivo y un toast lo dice.
- **Transiciones** (pedido del usuario: simples de usar; después, que también fundan el audio, porque el corte pelado
  era brusco; `MainWindow.Transitions.cs`, `TimelineView.DrawJoins`): donde un clip termina y otro empieza en una pista
  (a menos de 2 cuadros, `ClipTransitions.JoinSec`), si los dos son videos en una pista con video de un proyecto con
  video (pedido del usuario, `LiveTrack.OffersTransition`: entre clips de audio no se ofrece; la que ya estaba guardada
  sigue sonando y mostrando su chip, para poder cambiarla o sacarla), un chip cuadrado de 20 px va centrado en la unión a media
  altura de los clips: blanco con una `|` si es un corte, de acento con una `X` (fundido cruzado: un fundido que baja
  cruzado con uno que sube, como las líneas de fundido de los clips) o una `V` (pasar por negro); el moñito de CapCut no
  (pedido del usuario), y de los costados del chip salen dos flechitas de acento (con borde oscuro, a su altura) hasta los bordes
  del tramo que cubre (`DrawJoinSpan`). Queda encima de las
  manijas de los bordes y se lleva sus clics. Clic → menú "Transición": Corte / Fundido cruzado / Pasar por negro (este
  solo si los dos clips tienen imagen), duración con 0,25 · 0,5 · 1 · 2 s y un slider de 0,1 a 3 s (0,5 por defecto), y
  qué pasa con el sonido. También desde el clic derecho del clip ("Transición al entrar / al salir"). Se guarda en el
  clip que entra (`Clip.Transition`, `TransitionSec`) y va centrada en la unión; nunca cambia los fundidos propios del
  clip. **Imagen**: el fundido cruzado usa el video que esconden los recortes; si de un lado no hay, ese lado se queda en
  su último o primer cuadro (el menú lo avisa); pasar por negro no necesita nada escondido. **Sonido** (los dos tipos):
  fundido cruzado en el mismo tramo, con el audio que esconden los recortes (`ClipTransitions.Audio`); dos pedazos de la
  misma toma se funden en línea recta (suman exactamente la toma), tomas distintas a potencia constante; sin audio
  escondido de ningún lado, cruza con un fundido de 10 ms justo en la unión (el menú lo avisa). Dividir deja la
  transición en la pieza de la izquierda.
- **Franjas sobre la regla** (pedido del usuario): de arriba abajo, rango, marcadores y acordes, 18 px cada una
  (`RangeH`, `MarkerH`, `ChordH`), con su nombre en la columna de encabezados (`DrawStripLabels`). La regla sube con
  ellas (`RulerH` depende de `StripsH`).
- **Mostrar u ocultar las franjas**: cuatro botones al final de las herramientas (`StripButton` en
  `MainWindow.Tools.cs`; `timeline.rangeStrip`, `timeline.markerStrip`, `timeline.chordStrip` y `timeline.tempoStrip` en `config.json`, de la
  app y no del proyecto; la de tempo arranca plegada). Oculta la del rango, el rango A–B sigue andando con I, O y L, y con el loop prendido sus bordes
  siguen sobre las pistas. La de acordes se muestra aunque no haya acordes y se prende sola al detectarlos.
- **Franja del rango** (`Controls/TimelineView.Markers.cs`, `MainWindow.Markers.cs`): arrastrar en vacío dibuja el
  **rango A–B** del loop y lo prende; sus bordes y su cuerpo se arrastran; un clic salta; **doble clic lo saca**
  (`LoopClearRequested`). Acento con el loop prendido, gris apagado, y con el loop prendido sus bordes siguen tenues por
  las pistas. Clic derecho: A acá, B acá, sacar el rango. El rango va en `ProjectSettings.LoopStartSec/LoopEndSec`,
  fuera del deshacer (`ProjectMarkers`).
- **Franja de marcadores** (mismos archivos): los **marcadores** son banderitas `Raised` con el nombre en `Dim` y una
  línea tenue hacia abajo: se arrastran, un clic salta, doble clic los renombra en el lugar (`MainWindow.Rename.cs`);
  doble clic en vacío agrega uno (y abre su nombre); apretar en vacío hace scrub como la regla. Todo se imanta como los
  clips (Alt = libre). Clic derecho: el menú de la franja o del marcador (nombres de la lista `MarkerNames`, "Repetir
  hasta el siguiente", borrar); los dos terminan con "Copiar capítulos para YouTube" (`CopyChapters`,
  `ProjectMarkers.Chapters`: "0:00 Inicio" si el primero no está en 0, y avisa si YouTube los ignoraría). Los
  marcadores van en `Project.Markers` y se deshacen. Atajos: L loop, I / O ponen A / B en el cabezal, K marcador,
  `,` / `.` saltan.
- **Franja de acordes** (`Controls/TimelineView.Chords.cs`, `Project.ChordMarkers`, pedido del usuario): cada acorde
  es un bloque de donde empieza a donde termina, separado de los marcadores del usuario; el nombre aparece si entra.
  Vienen de Detectar escala o **se escriben a mano**: doble clic en vacío (o clic derecho → "Agregar acorde acá") abre
  el cuadro de nombre donde va el bloque, que dura hasta el próximo acorde, un compás como mucho (`ChordSlotAt`); el
  acorde se agrega solo si el nombre queda (Esc no deja nada, `ProjectMarkers.AddChord`). Doble clic en un acorde lo
  renombra. Se arrastra por el cuerpo para moverlo (con los otros marcados, si está marcado) y por los bordes para
  estirarlo (5 px, `ChordEdgeGrab`; un bloque angosto solo se mueve), imantado como los clips (Alt = libre) y sin pasar
  por encima de sus vecinos; al soltar va en un paso de deshacer (`ProjectMarkers.SetChordTimes`). Un clic salta y lo
  marca, Ctrl+clic suma o saca, arrastrar en vacío marca todo lo que toca. Supr borra lo marcado;
  clic derecho: ir, renombrar, borrar los marcados o todos. Apretar en otro lado limpia lo marcado. Siguen a "Borrar y
  cerrar el hueco" en todas las pistas.
- **Pista de tempo** (#214, pedido del usuario, como la de Fender Studio Pro; `Controls/TimelineView.Tempo.cs`,
  `MainWindow.Tempo.cs`): una franja plegable **debajo de los números de la regla** (su botón en la tira de
  herramientas; `RulerH` la incluye) que muestra y edita el `TempoMap` del proyecto. Arriba, la fila de **compás**: cada
  cambio ("7/8") en su compás, el primero visible mientras se ven sus compases. Abajo, la **curva de tempo** sobre una
  escala de BPM que se ajusta a los tempos del mapa (los números en la columna de encabezados; queda quieta mientras
  los tempos entran en ella, así una edición no aplasta la curva). Su borde de abajo se arrastra para hacerla más alta
  (32 a 320 px; se recuerda en `config.json`, "timeline.tempoStripHeight"). Mientras se arrastra un punto, la franja
  cambia solo los tempos: los puntos quedan donde los pone el mapa del proyecto y todo el timeline se mueve al soltar. Cada punto es un
  **escalón** (se mantiene y salta) o una **rampa** lineal (en tiempos) hasta el siguiente, con su BPM al lado si entra.
  Doble clic (o clic con el Lápiz) en la curva agrega un punto en el tiempo imantado, al tempo de la altura del puntero
  (si cae dentro de una rampa, sigue la rampa). Un punto se arrastra en tiempo (imantado como los clips, Alt = libre,
  Shift = solo tempo) y en tempo (BPM enteros), sin pasar a sus vecinos; el primero, al comienzo de la canción, solo en
  tempo y no se borra. Un clic lo elige y **Supr lo borra**; clic derecho: Rampa hasta el siguiente, Escribir tempo…
  (con decimales, de 20 a 400), Borrar. Clic derecho en la curva vacía: agregar un punto ahí y el cambio de compás.
  Doble clic o clic derecho en la fila de compás (y clic derecho en los números de la regla, arriba de la grilla):
  **Cambio de compás…** (una tarjeta con el numerador, 1 a 32, y la figura, 1 a 32) y quitarlo (el del primer compás
  queda). Los compases cambian siempre al comienzo de un compás. Cada edición es un paso de deshacer
  (`TempoFollow.SetMap` dentro de `EditTimeline`): lo musical (clips MIDI, pistas de audio con **Seguir tempo**,
  automatización de esas pistas, marcadores, acordes, loop) queda en su tiempo y el cabezal también. Mientras se
  arrastra solo se mueve la franja; el resto cambia al soltar. Importar un .mid con tempo o compases propios a un
  proyecto sin clips trae su mapa (`MidiFiles.UseTempo`, en el mismo paso); con clips, la canción conserva el suyo.
- **Regla, grilla e imán siguen el mapa**: los compases salen del `TempoMap` (cada uno con su largo y su compás; las
  subdivisiones son la figura del compás: corcheas en 6/8, `BuildBars`, recorriendo solo los compases a la vista) y el
  imán corta en el mapa (`SnapGrid.Map`: cada valor arranca de nuevo en cada compás). Lo mismo el piano roll (sus
  compases, su grilla y el cuantizado; un patrón cuenta desde su comienzo), la mini regla del mixer y el largo de un
  compás nuevo (bloque de automatización, B del loop, el pincel).
- **Seguir tempo** (clic derecho en una pista de audio, en el grupo de la pista; `Track.FollowTempo`): sus clips se
  mueven y se estiran con los cambios del mapa, como los MIDI; apagado (de entrada), quedan en segundos. Prenderlo o
  apagarlo es un paso de deshacer y no mueve nada.
- **Marcas de duración por red** (`Controls/TimelineView.Social.cs`, `SocialLengths` en `XtremeDaw.Audio`, pedido del
  usuario): el clic derecho en la regla, debajo de la grilla, prende o apaga cada una (varias a la vez; ninguna de
  entrada; `socialMarks` en config.json, de la app y no del proyecto, como la grilla). En ese menú (pedido del usuario)
  el clic izquierdo elige y cierra, y el **clic derecho elige y lo deja abierto**, para cambiar varias seguidas
  (`MenuChoice`; vale también para la grilla y para los otros menús de opciones: entrada de la pista, plegar una
  carpeta, mostrar u ocultar un separador). Una línea punteada tenue cruza las pistas en el límite, contado desde 0
  como la exportación, con el nombre de la red ("Instagram Reels · YouTube Shorts · 3:00") en una cajita a la
  izquierda, justo debajo de la regla (una fila por marca, así dos en 3:00 no se tapan). Si el tema, o la toma que se
  está grabando, pasa una, se pone ámbar con "no entra". Las historias se repiten: una línea en cada corte que necesita
  el tema ("Historia 1 · Instagram · Facebook · WhatsApp"; con poco lugar, solo "Historia 1"; sin nombre cuando tampoco
  entra). Los largos cambian cada tanto: se actualizan en `SocialLengths`.
- **Loop**: al cruzar B (o el final del tema, sin rango) vuelve a A con el crossfade de los saltos, sin reiniciar los
  efectos (`PlaybackEngine.SetLoopRange`). Si arrancás después de B, sigue hasta el final y desde ahí vuelve a A.
- **Clic derecho** (`ShowTimelineMenu`): sobre un clip, solo cosas del clip (dividir, duplicar, copiar, cortar, al inicio, corregir
  corte, restablecer, quitar clip); en el encabezado o en un tramo vacío del carril, solo cosas de la pista (panel de
  video, renombrar, duplicar con o sin contenido (`DuplicateTracks`, `TrackCopies`: cada copia debajo de su original, en su carpeta; con varias elegidas, todas; Ctrl+Shift+D), carpeta, guardar como plantilla, pegar en el cabezal, quitar pista; quitar pista pregunta antes, igual desde el menú, el tacho y Supr: `AskRemoveTrack`); en la parte vacía debajo de la última fila,
  en los encabezados o en el timeline, el menú de "+ Nueva pista" (pedido del usuario). En los carriles el menú abre al soltar:
  si el clic derecho se arrastra (`RightEraseSlop`), borra los clips que toca, como la goma (`StartRightErase`, pedido del usuario: como en FL Studio). El mismo criterio vale en todo el timeline (#268: "que elimine cualquier cosa"): en los carriles de automatización borra los bloques enteros que toca (un punto suelto se borra con doble clic), y en las tiras de arriba el rango del loop, marcadores, acordes, cambios de compás y puntos de tempo (salvo el primero de cada uno), y su menú abre al soltar (`TimelineView.Erase.cs`, `StartStripErase`). Lo que toca queda tenue hasta soltar (`ErasingOpacity`, #281: igual en clips, bloques y las marcas de las tiras) y se va en un paso del historial; en los carriles un mismo arrastre toma clips y bloques, empiece donde empiece (`ClipsErased` lleva los dos). Mute y solo no van: están en el encabezado. Los
  menús no tienen tope de alto (sin scroll), salvo que la ventana sea más baja que el menú.
- **Ganancia por clip**: `Clip.GainDb` (slider del inspector) y `Clip.GainCurve`, puntos en dB contados desde el sample
  0 de la toma (`ClipGainCurve`, así siguen pegados a las notas al mover, recortar o dividir). El clip seleccionado
  muestra su línea (blanca, puntos de acento): clic en la línea agrega un punto, se arrastra, doble clic lo saca.
  Reproducción y exportación la evalúan cada 64 muestras con rampa (`LiveClip.Curve` es una copia que el hilo de audio
  lee sin locks: toda edición asigna una lista nueva y llama a `RefreshCurve`).
- **Sin ayuda al pasar el mouse**: el timeline no tiene tooltips (el usuario los sacó: estorbaban y trababan la
  ruedita); lo que hace un clic lo dice el cursor (`TimelineView.UpdateCursor`). No los vuelvas a poner sin preguntar.
- **Ruedita**: sola, arriba y abajo; Shift, zoom horizontal; Ctrl, a los costados; Alt, altura de las pistas (los tres
  modificadores se cambian en Configuración → Atajos, `Shortcuts.Wheel`, guardados en `config.json` "wheel"); sobre el
  paneo de un encabezado, el paneo. Ctrl+rueda hacia arriba va a la derecha (más adelante en la canción). Cada gesto
  (también la ruedita sola) tiene su "Invertir" en esa misma sección (`Shortcuts.WheelSign`, "wheelReverse").

## Automatización (`MainWindow.Automation.cs`, `Controls/TimelineView.Automation.cs`)

- **Crear**: clic derecho sobre el control (fader, paneo y sliders del mixer y el inspector, perillas de efecto, volumen, paneo, M y envíos del encabezado) → "Automatizar …", o menú de la pista → Automatización, que lista todo lo de la pista. Un VST3 ofrece "Último tocado" (lo último movido en su ventana) y "Elegir parámetro…" (lista con búsqueda: pueden ser miles). Hace falta que el plugin esté abierto.
- **Automatizar en la ventana del plugin** (`MainWindow.AutomationListen.cs`, pedido del usuario): en la barra de la ventana de un instrumento o de un efecto de pista, el botón "Automatizar" escucha ese plugin: el próximo parámetro que se mueva ahí (`VstBridge.Touches`) recibe su carril, recién al soltar el control y con el valor donde quedó; si no, el carril le sacaría el control a la mano. Se apaga solo; otro clic lo cancela, y cerrar la ventana también. Escucha un plugin por vez. Si el parámetro ya tiene carril, lo muestra. Un efecto de bus no tiene barra: los buses no tienen carriles.
- **Carril hijo**: va justo debajo de su pista, con una rama del color de la pista (`DrawAutoHeader`), nombre, ON y tacho (último, alineado con el de la pista). El tacho (`AutoTrashRect`, #152) y "Borrar automatización" del menú preguntan antes, ahí mismo (`AskRemoveLane`). Se pliega con la flecha al pie del agarre de la pista o de la carpeta abierta, con el ícono de automatización encima si la fila tiene lugar (`AutoFoldColumn`, #159; `AutomationFolded`, vista, sin deshacer), y va con la pista si se pliega su carpeta o se la arrastra. Su altura se cambia del borde inferior, como una pista. Debajo de la curva, una sombra de los clips.
- **Bloques** (como los clips MIDI): un carril nuevo trae uno a lo largo de la canción. Doble clic en el carril vacío crea otro de un compás; la tira de arriba lo mueve (con Shift o Ctrl, una copia, que se puede soltar en otro carril de automatización, de la misma pista o de otra; a un parámetro de encendido o por pasos, con valores enteros; #171) y los bordes lo recortan. Clic derecho: copiar, duplicar, repetir 2 / 3 / 4 / 8 / 16 veces u otra cantidad, dividir, vaciar, borrar, pegar. Atajos de copiar, pegar, duplicar y Supr sobre el bloque elegido. La punta derecha de su tira lo repite en bucle (ver **Bucle** en el timeline): las vueltas son de solo lectura (#296: manda el original), veladas con el fondo bajo la tira (`DrawAutoRepeats`); un clic o Shift+arrastrar en ellas solo elige el bloque. Entre bloques la curva mantiene el último valor; si dos se pisan, manda el que empieza después. Las copias son independientes.
- **Puntos**: clic agrega (imantado a la grilla; Alt lo suelta), arrastrar mueve, doble clic borra, Shift+arrastrar dibuja. Cada tramo tiene su forma y su tensión (`AutoShape`, `AutoShapes`, pedido del usuario, como FL Studio): Curva (exponencial, recta en 0), Curva doble (una S: dos curvas, k y −k), Mantener, Escalones, Escalones suaves, Pulsos y Onda (la tensión da de 2 a 32 escalones o ciclos, 8 en 0). Con el puntero sobre el carril aparece un círculo en cada tramo: en una curva va sobre ella a la mitad y arrastrarlo la hace pasar por el puntero; en las otras (la doble también: su medio no se mueve) va en el medio, entre los puntos, y arrastrar hacia arriba suma tensión, escalones o ciclos; doble clic vuelve la tensión a 0 ("Quitar curvas" deja todo el carril recto). **Clic derecho sobre un punto o un tramo** (el círculo o la línea) abre solo el menú de su forma (`ShowShapeMenu`, pedido del usuario: sin el resto del menú): el de un punto es el tramo que llega a él (como FL), el del primero el que sale; elegir otra forma la empieza en tensión 0, clic derecho en una forma la prueba sin cerrar, y "Usar en todo el bloque" la copia a todos sus tramos. En el resto del carril, clic derecho es el menú del bloque. Como en los carriles de clips (#268), en la curva esos menús abren al soltar: si el clic derecho se arrastra (`RightEraseSlop`), borra los puntos que toca (`DragAutoErase`, a lo largo del trazo; un bloque que se queda sin puntos se va), todo en un paso del historial (`AutomationPointsErased`); en el encabezado del carril el menú abre al apretar. Un punto nuevo toma la forma del tramo que parte. Dividir o recortar un bloque no cambia lo que suena (`AutomationBlock.Cut`: una curva cortada es la misma curva con menos tensión; mantener, escalones y pulsos quedan como puntos de mantener; escalones suaves y onda, como líneas cortas). Interruptores (mute, ON de un efecto) van en escalones. Un punto en el borde de un bloque se agarra también desde afuera o sobre el comienzo del bloque de al lado (`AutoBlockUnder`, #296). Clic derecho sobre un punto empieza con "Escribir valor…" (`AskPointValue`, #296): en las unidades que muestra el parámetro (dB, Hz con k, ms, %, L/R, el nombre de un modo), y la posición es la que más se acerca a esa lectura (`PositionOf`). Clic derecho: borrar punto, apagar, ocultar (sigue sonando; vuelve desde el menú de la pista), vaciar, borrar.
- **Los controles siguen al carril** (`FollowLanes`, `AutomateOn`): fader, paneo y perillas se mueven mientras suena, sin tocar el canal. Mover a mano un parámetro con carril encendido no sirve: el carril lo pisa.
- Cada gesto es un paso de deshacer. Borrar y cerrar el hueco corre los puntos (`Automations.CloseGap`).

## Encabezados de pista

- **Zoom** (pedido del usuario): al pie de la columna, una barra de `FooterH` = 40 px (`TimelineFooter`) con dos
  sliders grandes (pista de 6 px, perilla de 18): zoom horizontal (escala logarítmica de "ver todo" a `MaxPps` = 2 px por muestra, `TimelineView.Zoom` /
  `SetZoom`, se mantiene el cabezal en su lugar) y altura de pista (24 a 320; #168). Debajo de 40 px el encabezado va fino (`SlimAt`, `HeadY`, `HeadBtn`): su fila de
  arriba se centra en la pista y el ícono y los botones se achican a la pista menos 4 px arriba y abajo. Las filas scrollean por encima de la
  barra. Zooms fijos contados en tiempos (`TimelineView.ZoomPresetBeats`: un tiempo, 1 compás, 4, 16 y toda la
  canción), sin tecla de entrada (el usuario les sacó el 1 a 5 para las herramientas): se asignan en Atajos.
- **"+ Nueva pista"** (pedido del usuario): un botón **+** al principio de la barra de zoom (`NewTrackButton`, tooltip con
  el nombre y el atajo), así no tapa ninguna fila; el menú abre arriba del botón y se apaga mientras se graba. El scroll
  deja `TailH` = 64 px vacíos debajo de la última fila, para el clic derecho con la vista llena.
- **Alto por fila** (pedido del usuario): `Track.HeightScale` (múltiplo del alto general, así el slider sigue
  escalándola) / `Folder.Height` en px al alto general por defecto (`Track.DefaultHeight`) para la carpeta abierta y
  `Folder.HeightScale` (múltiplo, como la pista) para la plegada (null = el alto de siempre; el de la carpeta, abierta
  o plegada, es `FolderBaseH`: el alto general de las pistas, #161, así igualar deja todo del mismo alto; lo que no
  entra, volumen y paneo o los knobs de envío, se oculta como en una pista baja). Las carpetas también siguen el zoom
  vertical (`FolderZoomed`, pedido del usuario). Se
  cambia arrastrando el borde de abajo del encabezado (`RowEdgeAt`, gesto "row-height", el borde se ilumina) y un doble
  clic en el borde lo saca; el botón ↺ del pie (`ResetHeightsButton`) iguala todas. Es vista, como plegar: se guarda
  sin deshacer (`SetRowHeight`). Todo lo que dibuja o toca una fila usa su alto (`LaneH(i)`, `RowH`, `FolderRowH`),
  nunca `laneH` directo. Al lado del ↺, **L R** (`SplitAllButton`, pedido del usuario) prende o apaga izquierdo y
  derecho (`Track.SplitWave`) en todas las pistas estéreo y mixtas a la vez: en acento mientras todas lo tienen.
- **Mono, estéreo o mixto** (pedido del usuario): después del nombre, antes del "♪ +2", un badge dice qué son los clips
  de la pista (`LiveTrack.Layout`: un clip con los dos lados iguales cuenta como mono, como en el mixer; uno offline va
  por su archivo; `HeaderTags`, `DrawLayoutTag`). Un aro mono, dos estéreo, un aro y dos chicos mixto. Si el nombre
  quedaría con menos de `NameMinW` = 40 px, el badge no se muestra; tampoco en el encabezado compacto. En una estéreo o
  mixta, el badge solo informa (no toma clics, pedido del usuario): se prende en acento mientras la pista muestra los
  dos lados. "Ver izquierdo y derecho" del menú de la pista y del inspector, o L R al pie de las pistas, prenden
  `Track.SplitWave` (vista, sin deshacer, `SetSplitWave`): cada clip estéreo dibuja izquierdo arriba y derecho abajo con
  una rayita entre los dos (picos por lado, `MediaAudio.PeakMinL`…, null en un mono), si cada mitad tiene
  `SplitWaveMinH` = 14 px; un clip mono sigue con una sola onda. El badge queda en acento mientras está prendido.
- Pasado el detalle de los picos (`MediaAudio.PeakBin`), la onda sale de las muestras (`SampleWave`): mín/máx por
  columna y, con más de un píxel por muestra, una línea que las une. La regla baja hasta 1 ms y, en compases, parte los
  tiempos en mitades ("2.3").
- **Volumen**: más presencia que los sliders de efecto: barra de 5 px, marca en 100 y una tapa de fader clara.
- La columna se ensancha arrastrando su manija: una sola franja de `GripW` = 8 px adentro del borde derecho, de todo
  el alto, con dos rayitas en el medio; nunca pisa un clip en 0 s. Va de 94 a 640 (324 por defecto) y se recuerda en
  `headerWidth`; las filas se arman para 264 y el sobrante va al volumen.
- **Encabezado compacto** (pedido del usuario): con la columna por debajo de `CompactAt` = 276, cada pista muestra solo
  manija, ícono, ● M S y sus medidores verticales, más anchos (`DrawCompactHeader`). Tiene una disposición por lugar
  disponible (`CompactBtn`), para que nada quede suelto: desde 140 de ancho, una fila con el ícono, el nombre si entran
  30 px y ● M S contra los medidores; más angosta, en carriles de 60 o más, una tira con el ícono y ● M S debajo,
  centrados; más angosta y más baja, M sobre S al lado del ícono y de su mismo alto, con el nombre en el medio si
  entra, y sin ● (el carril armado sigue en rojo y se arma con el atajo o desde el mixer). Volumen, paneo, envíos, entrada e instrumento quedan
  en el inspector, el mixer y el menú de la pista. Una carpeta muestra manija, flecha e ícono, y el nombre si entra.
  Quitar pasa al clic derecho. "Renombrar" abre la caja por encima del carril (`CompactEditorW`). La barra de la
  esquina y la del zoom sacan lo que no entra (`FitTimelineBars`): primero el imán a clips (por debajo de 324), después
  ajustar, quitar mute / solo y el imán; quedan el modo de la regla y el zoom.
- Con carriles más altos aparece debajo una fila de knobs, todos dinámicos (pedido del usuario: nada fijo, ni Saturación,
  Comp o Tono): uno por envío. Los envíos son los buses de la lista de la pista (`TimelineView.SendKnobs`, que la ventana arma de nuevo
  solo después de una edición, `SendKnobsOf` y `editStamp`), con el nombre y el ícono del bus; se eligen con clic
  derecho en la pista → "Envíos a buses" (`SendChoices`). Configuración → Apariencia los muestra u oculta todos
  (`TrackKnobs`, "timeline.hiddenKnobs" en `config.json`) y los de cada pista **arrancan a la izquierda**, debajo de la
  insignia (pedido del usuario), cada uno en una celda de 72 px o del ancho de su knob, y solo se angostan si no
  entran (`KnobsLeft`, `KnobCellW`, `TimelineView.SetKnobs`, `KnobCount`). Transponer se toca en el inspector. El de Tono es bipolar: −12…+12 semitonos en 0…1, se ilumina desde arriba hacia
  el lado que va, muestra "+2" adentro, la ruedita mueve un semitono y su nombre no prende ni apaga nada. También
  en pistas vacías (`TimelineView.EffectRequested`). Son tan grandes como dejan el alto de la pista y el ancho
  de la columna (`FxKnobD`: de `FxKnobMin` = 20 a `FxKnobMax` = 64 px) y se achican con ella; si ni el mínimo entra, la
  fila no se muestra. Debajo de cada uno, ícono `FxIcon` + nombre (solo el ícono si no entra): clic prende/apaga. El
  knob se arrastra en vertical (Shift = fino), con la ruedita o doble clic para restablecer; la cantidad va adentro del
  knob si mide 36 o más, y si no, en lugar del nombre mientras lo tocás.
- Los efectos de una pista se abren desde su menú (clic derecho → Efectos): el mismo panel que en el mixer, en su ventana, donde fue el clic.
- Tocar cualquier parte del encabezado (la manija o un control, menos "quitar") selecciona la pista.
- Los botones dibujados del encabezado (armar, M, S y la papelera, también en la fila de carpeta) brillan con el
  mouse encima (`TimelineView.Hot` / `DrawGlow`: acento en M y S, rojo en armar y papelera). La papelera de la pista
  pregunta antes de quitarla (`MainWindow.AskRemoveTrack`).
- Al reordenar con la manija, la pista sigue al puntero por encima de las demás y el resto se desliza a su lugar
  (`shownSlot`, `Slide`).
- **Quitar todos los mute / solo**: M y S al final de la barra de la esquina del timeline (`ClearMutesButton` /
  `ClearSolosButton`; se van con la columna por debajo de 220) y en la barra del mixer. Están prendidas si
  alguna pista tiene uno; un clic los saca todos en un paso de deshacer (`ClearAllToggles`).

## Color e ícono de pista (`MainWindow.TrackLook.cs`)

- Cada pista tiene una insignia (`TrackIcons.DrawBadge`, `Controls/TrackBadge`) con su ícono de instrumento teñida
  con su color (o el acento), la misma en el encabezado, el inspector y el mixer. Sin ícono muestra el de su tipo
  (trazos: onda = audio, teclas = MIDI, cámara = pista que sale en el video, solo en modo Video) y su número chiquito
  abajo a la derecha.
  En el encabezado mide 28 con la columna al mínimo y crece con ella (`BadgeGrowth`) hasta 52, lo que entra al lado
  del nombre y el volumen sin tocar las filas de efectos; lo que crece corre a la derecha el nombre y el volumen.
  Los íconos son rellenos: Fluent Emoji High Contrast de base y lo que le falta de MDI, game-icons y Qlementine
  (`tools/make-track-icons.py`, licencias en `Assets/Icons/LICENSES.txt`); los ids viejos van en `TrackIcons.Legacy`.
- Tocarla abre el selector: "Sin color" y "Color personalizado" arriba, y 48 colores en cuatro filas de 12, de izquierda a derecha en orden cromático, todos de la misma luminosidad (#145, elegidos por el usuario: tonos OKLCH cada 7,5°; `TrackIcons.Colors`, guardados en hex). El personalizado abre debajo un cuadro de saturación y brillo con su barra de tono (`ColorField`, dibujado a mano) y el hex; un color que no es de la paleta se ve en ese botón y los íconos por categoría; el color
  deja el selector abierto y el ícono lo cierra.
- Con color, los clips de la pista se tiñen (relleno, borde y onda) y la tira del mixer lo lleva arriba. Los colores
  de pista son datos del usuario, no tokens: no se usan para estados de la interfaz.

## Carpetas de pistas (`MainWindow.Folders.cs`)

- En el timeline, una fila propia (manija, flecha para plegar, ícono y nombre, M y S donde los tiene una pista
  (`FolderMuteRect` / `FolderSoloRect`, `TimelineView.FolderToggled`; en el encabezado compacto no van) y papelera) cuyo alto de siempre es el mismo abierta o plegada (`FolderBaseH`): `FolderH` = 34 px
  (una línea, el título del grupo) o, si muestra envíos, el título (`FolderTitleH` = 30) y debajo **una perilla por
  envío** como las de las pistas (`DrawFolderKnobs`, de `FolderKnobD` = 36 px a ese alto, en la misma columna que
  las de las pistas de la raíz, `KnobsLeft`; crecen con la fila hasta `FxKnobMax` y se ocultan si no entran;
  `FolderSendKnobs` / `FolderSendRequested`, ediciones por `FolderEdit`; la etiqueta prende o apaga, doble clic en la
  perilla la pone en 0, la rueda la mueve; Configuración → Apariencia las oculta como las de las pistas). Abierta, el carril
  es un **resumen** (`DrawFolderSummary`): una barra de su color por cada tramo donde alguna de sus pistas tiene clips,
  unidos entre pistas (un bloque por clip solo las repetía); plegada, el carril es un **mini
  mapa** (`DrawMiniMap`): una franja por pista con sus clips en el color de la pista y la onda si la franja llega a
  `MiniWaveAt` px. Se redimensiona como una pista (ver "Alto por fila"). Sus pistas con el contenido del encabezado corrido `FolderIndent` = 24 px
  (manija, insignia, nombre y sliders). La carpeta y sus pistas van dentro de un recuadro fino de su color al 45 %
  (`DrawFolderFrames`, encabezados y carriles); sin riel ni tinte en cada pista, que parecían seleccionadas. Sin color
  elegido, una carpeta va en gris neutro (`FolderColor`), no en el acento, que también parecía selección; un separador,
  en el acento (`SeparatorColor`).
- Una carpeta se selecciona como una pista (clic en su fila o en su canal del mixer; `TimelineView.SelectedFolderId`,
  y entonces no hay pista seleccionada): el inspector muestra la carpeta (`InitFolderInspector`: nombre, color,
  nueva pista adentro y quitarla) y abajo su tira
  (`stripFolder`: paneo, M S, fader con su medidor, sus efectos, y sus envíos y su salida con las mismas filas que
  los de una pista, `RebuildInspectorSends(Folder)` → `RebuildStripSends`; #149) y Supr actúa sobre ella. Su ícono abre el selector de color
  (`ShowFolderLook`, los mismos `ColorChips` que el de pista). Elegido un color, abajo pregunta si pinta también
  sus pistas con ese color (`FolderTracksAsk`, un solo paso de deshacer); solo aparece si alguna tiene otro.
- **Renombrar en el lugar** (`MainWindow.Rename.cs`, pedido del usuario): doble clic sobre el texto del nombre de una
  pista o una carpeta, no en el espacio vacío de al lado (`TitleRect` / `FolderTitleRect`), o "Renombrar…" en su menú, pone una caja de texto encima (`TimelineView.NameEditRequested`, `EditingNameId`).
  Enter o un clic afuera lo guarda, Esc lo descarta, la rueda lo guarda. Un paso de deshacer.
- Las filas se arman en píxeles (`TimelineView.Relayout`). Las pistas se arrastran por su manija adentro, afuera o
  entre carpetas (`FindDrop`: el hueco más cercano al borde de arriba de la fila arrastrada, con 8 px de "pegue" para
  que no titile; en el borde de abajo de una carpeta, si ese borde de la fila queda arriba entra al final, si queda
  abajo sale; sobre una carpeta plegada entra última); el resto se corre para hacer lugar, la carpeta destino se marca con su color y una etiqueta dice
  "Entra en…" / "Sale de…". Una carpeta arrastrada viaja plegada.
- **Varias pistas juntas** (pedido del usuario): Ctrl+clic en un encabezado suma o saca esa pista, Shift+clic marca
  el rango desde la última clickeada (`TimelineView.picked`, `Pick`); la seleccionada (la del inspector) es la última
  tocada. Arrastrar la manija de una de ellas las lleva a todas apiladas en su orden (`dragIds`, `BlockTop`) y caen
  juntas (`TrackFolders.Move` con varias). El clic derecho sobre una de ellas abre el menú de todas (pedido del
  usuario; `TrackGroupItems`, también en el mixer): video, bloquear, "Agrupar en carpeta" (directo, Ctrl+G, `GroupInFolderShortcut`: las pistas elegidas y las de los clips seleccionados, #252; el submenú Carpeta solo si hay a dónde mover) y "Quitar N pistas". Quitar también va por
  Supr y el tacho, pregunta antes y es un solo paso de deshacer; las que tienen algo bloqueado se quedan (`RemoveTracks`). Un clic común
  vuelve a una sola.
- **Quitar una carpeta** con pistas adentro pregunta (`MainWindow.RemoveFolder`): "Solo la carpeta" (las pistas
  quedan afuera) o "La carpeta y sus pistas" (`TrackFolders.Remove(…, withTracks)`); vacía se quita sin preguntar.
- **Separadores** (`Folder.Separator`, pedido del usuario): una fila de `SeparatorH` = 22 px con la manija, un tramo corto de
  doble línea, el nombre chico y en mayúsculas en su color y la doble línea a lo ancho (`DrawSeparatorLine`, estilo
  consola, sin brillo: elegido por el usuario, #289; es el diseño por defecto: Configuración → Apariencia → Separadores elige otro, Línea simple, Banda, Etiqueta o Surco, igual en el timeline y el mixer, `Controls/SeparatorLooks.cs`, "separatorLook", #293), sobre el fondo de la ventana para que se lea como un corte entre grupos (`DrawSeparatorHeader`). La fila de
  arriba no dibuja su línea de abajo, que sería redundante (`OverSeparator`, salvo mientras se reordena); se arrastran como una carpeta (nunca entran pistas), doble clic
  renombra y el clic derecho abre su menú (`SeparatorMenu`: renombrar, color, mostrar u ocultar en el timeline,
  quitar; elegir un color cierra el selector). Oculto, no ocupa lugar pero conserva su lugar en el orden. Uno nuevo nace
  oculto en el timeline (pedido del usuario: son sobre todo para el mixer), salvo "Separador arriba" del menú de una
  pista, que lo agrega ahí y visible (`NewSeparatorAbove`).
- Doble clic en cualquier otro lugar libre de la fila de una carpeta (encabezado o carril) la pliega o despliega
  (pedido del usuario). Plegar es vista (sin deshacer); el resto pasa por `FolderEdit` o `EditTimeline`.
- En el mixer, cada carpeta es un grupo teñido con su nombre arriba (una franja de `FolderBandH` en todas las
  columnas, para que los faders queden parejos), un canal de carpeta primero y sus pistas, plegable aparte
  (`Folder.MixerCollapsed`): con la flecha o con doble clic en la franja fuera del nombre (#170; en el nombre,
  renombra). Deja `FolderGap` (6 px) de aire con el canal o separador de al lado (pedido del
  usuario), no con el principio del mixer ni con el espacio libre; del lado derecho, también con el master.
- **La carpeta es un canal** (decisión del usuario, 2026-11-04, #134; antes solo contenía): sus pistas suman en su nodo
  (un `BusDspChain`: sin slots, centrada y a 0 dB deja la suma intacta), pasan por sus **efectos**, su paneo, su fader
  y su mute, y van a su **Salida** (Master o un bus) y a sus **envíos** (PRE como los de una pista). Su **S** vale como
  el solo de cada una de sus pistas (`ChannelDsp.Soloed`, `LiveTrack.FolderSolo`): suenan ellas y la carpeta; su **M**
  calla su nodo y atenúa sus pistas en pantalla (`LiveTrack.FolderMuted`). Su fila del timeline lleva, bajo el título,
  volumen y paneo como una pista (#136: `FolderVolumeRect`, `FolderPanRect`, que siguen sus carriles por
  `FolderShown`; con la fila achicada se ocultan); clic derecho en ellos, en su M o en un envío ofrece su carril, igual
  que el fader y el paneo de su tira en el inspector. Su tira del mixer (`FolderStrip`, del ancho
  del zoom como las demás) es la de un bus: arriba el ícono que crece, "CARPETA" desde `HeadNameW` y el selector de
  salida; sus efectos y envíos como listas (`CompactChain`, `SlotsOf(Folder)`), el paneo, M S, el fader con su medidor
  y los dB abajo. Cada edición es un `FolderEdit` con su deshacer (los controles del canal, `FolderChannelEdit`, solo
  redibujan el timeline). Sus VST3 los maneja `MainWindow.VstFx.cs` como los de un bus. Sus carriles de automatización
  van bajo su fila, arriba de sus pistas, y se ocultan al plegarla; su menú los agrega (Automatización) y los pliega
  (Ver la automatización). Los de un bus van igual, bajo su fila al final del timeline (ver "Buses").

## Plantillas de pista (`TrackPresets`, `%APPDATA%\XTreme\pistas.json`)

Clic derecho en una pista → "Guardar como plantilla" guarda su canal (sin mute ni solo), color, ícono, entrada y si
va en el video; "Nueva pista → Plantilla" crea una pista con eso. En el menú de nueva pista las explicaciones son
tooltips con 1 s de demora (`MenuTipDelay`), no texto al costado. "Usar plantilla" (en el mismo menú de la pista de
audio, `ApplyPresetSubmenu`) le pone a una pista que ya existe el sonido de una plantilla (`TrackPresets.ApplyTo`):
slots con ids nuevos, volumen, paneo y envíos a los buses que el proyecto tiene; tomas, nombre, color, ícono, entrada,
salida, mute/solo y Transponer quedan. Un paso de deshacer; los compresores se vuelven a medir con sus tomas.

## Armar y grabar (`MainWindow.Recording.cs`)

- El ● de cada encabezado de pista y de cada tira del mixer (o R, sobre la pista seleccionada) elige dónde graba
  Shift+R (`armedIds`: null = la que elige `TakeRecording.PickTarget` a partir de la seleccionada, vacía = ninguna). Sin pista armada, Grabar queda
  deshabilitado, en video y en solo audio. Una pista MIDI armada graba el teclado MIDI (ver "Pistas MIDI y piano
  roll"): `RecordTargets` son las de audio y `MidiRecordTargets` las MIDI.
- Se pueden armar varias y se graban a la vez: una toma y un WAV por pista desde el mismo sample. Las pistas destino
  siempre se tiñen de rojo (`TimelineView.ArmedIndices`).
- Se graba **desde el cabezal**: la cuenta lleva hasta ahí, la toma cae ahí (`RecordingJournal.StartSec`) y, en una
  pista con clips, reemplaza solo el tramo que cubre (desde 0, toda la pista). Los clips reemplazados quedan en
  Historial (`TakeRecording.AddTake`) y la pista no suena mientras grabás (`LiveTrack.RecordMuted`).
- Cada pista armada muestra su entrada (`Track.Input`, "3" o "3-4"; null = 1-2 de Configuración) en un chip que abre
  el menú de entradas. El motor abre todas las entradas del driver y tiene un `InputTap` por pista armada (su
  `InputProcessor`, medidor, monitoreo por software sin repetir entradas, y su `TakeRecorder`). El video de la
  cámara va a la primera pista armada con `Video`.

## Mixer (`MainWindow.Mixer.cs`)

- **Botones sin tooltip** en el mixer, el piano roll y Mastering (pedido del usuario, 2026-11-03): la clase `notips` en
  `MixerHost`, `pianoHost` y `masteringScroll` los apaga (`ToolTip.ServiceEnabled`), también en su propia ventana. No los
  vuelvas a poner sin preguntar.
- Ocupa el lugar del timeline (X o "Timeline / Mixer") con su propia altura. Su mínimo (`MixerMinH`) deja siempre
  `FaderRoomH` (120 px) de fader debajo de la cabecera, las listas que se ven y el paneo de cada tira, así que crece al
  estirar una lista o mostrar los envíos (empuja al timeline en vez de aplastar los faders); nunca pasa del 70 % de la
  ventana, y debajo del timeline (distribución vertical) le deja al timeline solo su mínimo. Piano roll y Mastering, que comparten el lugar, siguen con 250. Una tira por pista, también
  las vacías. **El master queda fijo a la derecha** (pedido del usuario, `masterPin`): fuera de lo que se desplaza,
  separado por una línea de 2 px en `Faint` (`PinLineW`, `PinnedColumn`), y con el mismo pie que las demás tiras aunque aparezca la barra de desplazamiento (`AlignPin`).
  Todo va a `StripGap` (6 px, pedido del usuario): entre tiras, de la última a la línea, de la línea al master y contra los bordes.
  **Tiras del mixer** en Configuración → Apariencia (`Controls/MixerLooks.cs`, #250, "mixer.strips" y "mixer.color"):
  **Tarjetas** (lo de arriba) o **Unidas** (por defecto, pedido del usuario): sin `StripGap`, todo el alto del mixer, sin bordes ni radios, y
  entre tira y tira un surco (línea `App` con una línea clara al lado, `GrooveLitA`); la selección y el armado, un marco
  encima (`StripCardLook`); el recuadro de una carpeta queda solo como tinte. Y el color del canal: **Filo** (2 px arriba)
  o **Franja** (`BandH`, por defecto; Teñida, Encabezado y Sin color se sacaron, pedido del usuario, #250); en Tarjetas
  va recta y adentro del borde (`MarkInset`, #267: de borde a borde tomaba las esquinas redondeadas). El mismo surco
  cierra cada canal del timeline (pista o carpeta, debajo de sus carriles de automatización; `DrawRowBottom`/`DrawSeam`,
  `seamUnder`); entre un canal y sus carriles, la línea fina de siempre. Cambiar cualquiera rearma las tiras una vez.
  **Tipo de canal** (#277): debajo de todo, en cada tira, una fila fija (`StripKindH`, `KindTag`) con el ícono del tipo (`TrackIcons.KindIcon`: audio, MIDI, bus, carpeta, master) y su nombre, que se oculta en las tiras angostas.
  **Nombre arriba o abajo** (#277, las vistas "Alt" de FL Studio): Configuración → Apariencia → Mixer, "Nombre al pie
  de las tiras" (`MixerOptions.NameAtFoot`, "mixer.namePos": "top" por defecto o "bottom"). Abajo, `StripLayout` pone
  la cabecera (badge y nombre; en el master, su botón y LIM / MONO) debajo del pie, en todas las tiras, con las
  mismas filas fijas: faders y medidores siguen parejos. Cambiarlo rearma las tiras una vez.
- **Zoom del mixer** (pedido del usuario: un solo flujo, sin botones Compacto / Amplio / Auto, y una sola tira, la de
  lista, en todos los anchos): todas las tiras de pista y bus tienen un ancho, `mixerStripW` (`config.json`
  "mixerStripW", 120 de entrada), que va de `MinStripW` (42) a `WideMax` (150: más ancho no suma nada, pedido del
  usuario). Se cambia con el gesto de zoom del timeline (Shift+rueda por defecto, el de `Shortcuts.Wheel`, con su
  sentido) sobre el mixer, que deja quieta la tira que está bajo el mouse, o con la barra de la cabecera (escala
  logarítmica, como la del timeline); solo cambia anchos, sin rearmar (`ZoomMixer`, `MixerLayout.Resize`). La rueda
  sola y el gesto de costados lo desplazan a los costados (`OnMixerWheel`); sobre una perilla, fader o barra la rueda
  es de ese control, salvo el gesto de zoom, que siempre cambia el ancho (#207: con Shift apretado no se cambia un valor sin querer), y una lista que tiene más para mostrar se queda con la rueda sola. Las tiras de carpeta también
  siguen el zoom; el master también (pedido del usuario), sin bajar de `MasterW`; los separadores no cambian de ancho.
- **La tira se acomoda sola al angostarse** (pedido del usuario: el ícono del canal y los de los efectos quedan
  siempre): la cabecera (`StripHead`) lleva el badge del canal al lado del nombre, de 22 a 28 px según el ancho, y
  debajo de `HeadNameW` (100) el badge de 20 px centrado sobre el nombre, en 10 y centrado (#158: los dos siempre a la
  vista; la línea de abajo, "VACÍA" / entrada / "BUS", se va); debajo de `NarrowW` (90) el medidor
  saca la escala, el pico va en dB enteros, se van los dB del pie y se achican los márgenes; debajo de `TinyW` (64) el
  fader pasa a su tapa angosta (`Fader.Slim`, igual de alta) (`NarrowParts`). Cada fila de efectos se arma por su
  propio ancho: el ícono siempre (solo, centrado), el ⏻ desde `RowPowerW` (sin él, el clic abre el efecto, donde se
  prende), el nombre desde `RowNameW` y lo que tiene puesto (`SlotBrief`: "Válvula · 40%", el preset, "1/4 · 35%")
  desde `RowBriefW`, en a lo sumo `RowBriefShare` de la fila; los envíos sacan su ⏻ igual, los títulos de las listas
  se van, y M S ● se reparten el ancho hasta `MinToggleW`, donde ● deja su lugar (sigue en el encabezado
  de la pista en el timeline).
- **Una tira para todos los canales** (pedido del usuario): pistas y buses usan el mismo ancho (el del zoom), la misma
  tarjeta y selección (`StripCardLook`: hover, seleccionada con el borde en acento, armada en rojo, su color siempre en
  el filo de arriba) y las mismas piezas: `StripHead`, `CompactChain` (efectos, envíos, paneo y sus botones) y los dB al
  pie. Lo que un canal no tiene deja su sección vacía para que los faders queden parejos (un bus no tiene envíos; una
  carpeta, efectos). Un tipo de canal nuevo se arma con estas piezas, no con su propio layout. Doble clic en el título
  de una pista, un bus o una carpeta lo renombra (`RenameOnDoubleClick`; en una tira angosta, sin título, desde el menú).
  **Clic derecho en una pista**: el mismo menú que su encabezado del timeline, más Color e ícono (`TrackMenuItems`,
  pedido del usuario). La salida de una pista se elige en el inspector.
- **Secciones de la tira** (pedido del usuario, como Fender Studio Pro): **Efectos** y **Envíos**, en la cabecera
  (`SectionSwitch`, `config.json` "mixer.fx" / "mixer.sends"), muestran u ocultan esa sección en todas las tiras. Cada
  lista termina en un divisor con una manija en el medio (`ListBox`): arrastrado, cambia el alto de esa sección en
  todas las tiras a la vez (`SetListH`, "mixer.fxH" / "mixer.sendsH", hasta `ListMaxH`); doble clic la ajusta a la
  lista más larga del mixer (`FitListH`, `MixerLayout.FxLists` / `SendLists`) y otro doble clic vuelve al alto por
  defecto. Clic derecho en el botón de la cabecera: "Ajustar a la lista más larga" o "Alto por defecto" (muestra la
  sección si estaba oculta). Solo cambian alturas de filas (`ApplyMixerHeights`), nada se rearma. Paneo y M S ● quedan fijos.
  **Proporción** (#277, los tres puntos del mixer de FL Studio): sobre cada fader, una manija de tres puntos
  (`ProportionGrip`, `MainWindow.MixerProportion.cs`) agranda o achica todas las listas que se ven a la vez, en
  proporción, y el fader toma el resto; con los mismos topes (`ListRoom`, `ListMinH`, `ListMaxH`: nunca agranda el
  mixer). Escribe los mismos altos ("mixer.fxH" / "mixer.sendsH"); doble clic vuelve a los de fábrica. Sin listas a la vista, no aparece.
  Las manijas nunca agrandan el mixer (pedido del usuario, 2026-11-04): la lista crece hasta lo que entra en su alto,
  dejando lugar al fader (`ListRoom`, `ListCap`); si el mixer se achica, las listas se achican con él (`ShownLists`,
  hasta `ListMinH`) y recuperan el alto elegido al agrandarlo. `MixerMinH` no depende del alto de las listas.
- Arriba, sobre el mixer y sobre Mastering (no en solo audio), la mini timeline (`Controls/MiniTimeline.cs`): toda la
  canción, el cabezal (clic o arrastre para moverlo) y una regla en compases o en minutos y segundos, con su propio
  switch de dos botones a la izquierda que es el mismo modo que el de la regla del timeline (`SetRulerMode`).
- Las ediciones de canal pasan por `EditTrack`, las de las carpetas por `FolderEdit` y las de los buses por `BusEdit`
  (`BusesEdit` para agregar y quitar). Los desplegables son `SelectButton`, no `ComboBox`; con grupos abren submenús, como los presets del compresor (`CompPresetTable`: categoría → preset, pedido del usuario). Un preset se aplica directo con valores fijos (`CompPresets.Apply`); el botón **Analizar toma**, al lado, mide la toma y guarda la diferencia (`ThresholdTrim`, `MakeupTrim`), que sigue valiendo al mover Cantidad o cambiar de preset (decisión del usuario).
- **A | B** (`MainWindow.MixCompare.cs`, `MixCompare`): segmentado en la barra del mixer, entre los M/S de limpiar y la
  barra del ancho de los canales. Oculto por defecto (pedido del usuario, #211): se muestra con Configuración → Apariencia
  → Mixer (`MixerOptions`, "mixer.showAb"); ocultarlo no cambia la mezcla que suena. La mezcla que suena es la del proyecto; la otra espera en `ProjectSettings.Compare` (solo ajustes:
  el canal de cada pista, salida y envíos de las carpetas, y volumen, paneo, slots, salida y envíos de los buses, por
  id). El primer clic en la otra letra la arma como copia de esta. Nunca agrega ni saca pistas, carpetas ni buses, y
  no toca mute, solo, nombres, lados ni el master (Mastering es su propia etapa). Se aplica como Deshacer aplica un
  canal (`ApplyChannel`, `ApplyRouting`), sin recargar nada y con los mismos ids de slot, así los efectos no pierden su
  estado; cada cambio es un paso de deshacer (`UndoManager` guarda `Compare`). Clic derecho: "Copiar A en B" y
  "Olvidar la mezcla B".
- Sus medidores usan el degradé verde → amarillo → rojo (`Palette.Meter*`), distinto del `StereoMeter`.
- **Medidores completos** (`Controls/MixerMeter.cs`, en todas las tiras: pistas, carpetas, buses y master, compactas
  y amplias): dos barras, izquierda y derecha (5 px cada una, 7 en el master, con 1 px entre medio); una pista mono
  (todas sus tomas con L y R iguales, `MediaAudio.Mono`, o sin tomas y con una entrada sola o la de Configuración en
  modo mono) muestra una sola barra del ancho del par con el lado más fuerte, así las tiras no cambian de ancho. En
  cada barra, la parte llena es el RMS (~300 ms, `RmsTap`, por lado) y el tramo tenue con su rayita arriba es el pico. Encima de
  las barras, dentro de la misma fila del fader (las filas fijas de `StripLayout` no crecen), van el pico máximo desde el
  último reinicio en dBFS (el mayor de los dos lados; mono 8, `-∞` en silencio, en rojo si hubo clip) y una luz de clip
  por barra, que se prende en rojo arriba de −0,3 dB y queda prendida. Un clic en cualquier parte del medidor reinicia los dos. En las compactas el
  número entra justo (5 caracteres en 27 px). La carpeta muestra la suma de sus pistas y el bus su salida (`MixerLayout.NodeMeters`,
  leídos de `engine.Node`). El pie del
  master muestra los LUFS de corto plazo de la salida (la misma medición que la celda LUFS del transporte,
  `engine.Loudness`), en el lugar donde decía "Salida". Al lado de LIM, **MONO** (`engine.MonoCheck`): pliega la salida
  a mono con 10 ms de fundido, solo lo que se oye (medidores, LUFS y exportación siguen en estéreo); no se guarda.
  Bajo los LUFS, **COR** (`engine.Correlation`, `ShowMasterCorrelation`): la correlación de la salida en décimas, en
  rojo bajo cero y en amarillo bajo +0,3. Cabecera y pie tienen alto fijo: LIM y MONO van en una fila, LUFS y COR en dos líneas cortas.
- Cada canal es una tarjeta (`StripCard`) y todos (pistas, carpetas, buses, master) se arman con `StripLayout`, que les da
  las mismas filas: así faders y medidores tienen siempre la misma altura. Lo que vaya en la cabecera o en los
  controles tiene que entrar en su fila fija, no estirarla.
- La **tira** va como la de una consola (pedido del usuario, `CompactChain`): la lista de EFECTOS
  (un renglón por slot con su ⏻ de encendido; un clic abre sus controles en la ventanita, arrastrado cambia el
  orden, y el + de su última fila suma uno, `ListAddRow` y `ShowAddSlotMenu`, pedido del usuario, #150; una rayita bajo el primer compresor muestra cuánto comprime), la de
  ENVÍOS (una barra por bus de su lista con su nombre, su % y su ⏻, `HBar`; el + de su última fila elige los buses), el paneo como
  barra desde el centro (`HBar` bipolar) y ● M S. Efectos y envíos son secciones de alto fijo, el mismo en todas
  las tiras (pedido del usuario: todo parejo); una manija abajo de cada una (`ListBox`) las estira en todas a la vez
  (`SetListH`, "mixer.fxH" / "mixer.sendsH" en `config.json`; doble clic vuelve al de siempre). `StripTopH` sale de
  las secciones que se ven y su alto, y `ApplyMixerHeights` solo cambia las alturas de las filas registradas
  (`MixerLayout.TopRows`, `FxRows`, `SendRows`), sin rearmar nada. Mixer de 440 de alto por defecto.
- La cabecera del mixer tiene el título con presencia (ícono de mixer en acento y "Mixer") y **Efectos** y **Envíos**
  (`SectionSwitch`, pedido del usuario): muestran u ocultan esas secciones en todas las tiras
  (`mixerFx`, `mixerSends`, guardados en `config.json` "mixer.fx" / "mixer.sends"). **Conexiones** (`RopesSwitch`,
  `MainWindow.MixerRopes.cs`, #264, "mixer.ropes", apagado por defecto) muestra una franja debajo de las tiras, del
  ancho de lo que se desplaza: los envíos prendidos del canal elegido (pista, carpeta o bus)
  como sogas que cuelgan de su tira hasta cada bus, con una flecha al llegar; con un bus elegido, también los
  que le llegan, más tenues. Cada soga termina en una perilla que cambia ahí mismo el nivel del envío, con el nivel
  (y PRE) al lado: en un envío, bajo el bus al que llega; en uno que entra, bajo el canal que lo manda. Las sogas que
  se cruzan cuelgan a distintas alturas (las más cortas más arriba). La franja se agranda arrastrando su borde de
  arriba ("mixer.ropesH") y las perillas crecen con ella (`Controls/SendRopes.cs`; un envío en 0 % sigue con su soga,
  finita, y su perilla: sigue conectado, #292). Las perillas se achican para no pisarse con tiras angostas y el nivel va al lado solo si entra (si no, en el tooltip); una soga a un canal fuera de la vista de su dock termina en el borde, sin perilla (#290). En la tira elegida las sogas salen de **OUT** (a la derecha del centro) y llegan a **IN** (a la
  izquierda), cada punta con su etiqueta; en la otra punta, bajo la perilla, también: IN donde llega un envío, OUT
  donde sale uno que entra. Cada soga baja recta, corre por su altura y sube recta a su punta, con esquinas
  redondeadas y un borde del color del panel para que se lean los cruces; las que comparten puerto se abren en abanico
  para no cruzarse al bajar. La flecha entra derecho hacia arriba, y la soga lleva siempre el color del bus de destino. Bajo
  cada bus al que el canal elegido puede mandar y todavía no manda (sin cerrar un bucle), **Enviar** (una pastilla con
  un + en acento) crea el envío al instante: prendido, en la lista de envíos, con el nivel que tenía
  o 25 % (`QuickSendLevel`). Solo lo lleva un bus que se ve en su dock, y queda dentro de ese dock; si los buses
  quedan más juntos que la pastilla, todos muestran solo el +, y uno que igual tocaría al anterior no se muestra: nunca
  se pisan (#275). La **salida** del canal también es una soga (#301), aparte de los envíos: neutra (el color del
  texto), más gruesa, sólida, sin perilla ni nivel y debajo de las demás, hasta su carpeta (una pista en una), su bus o
  el Master; por eso la franja llega también debajo de la tira del Master y se ve aunque no haya buses. Un bus o una
  carpeta elegidos muestran además las salidas que les llegan; sin nada elegido la franja no dibuja nada (#301:
  era demasiado). El tick del mixer (`TickRopes`) compara sin asignar el canal, sus envíos y dónde están
  las tiras (`MixerLayout.Cards`) y solo rearma las sogas si algo cambió (selección, nivel, scroll, zoom, orden). A la derecha, antes de los M / S
  que limpian mutes y solos, **FX** (`FxBypassButton`, pedido del usuario): pone en bypass todos los efectos de pistas
  y buses, o los vuelve a activar. Solo lo que se oye, como MONO (`PlaybackEngine.FxBypass`, no se guarda): los slots
  van al motor apagados por el mismo camino que su ⏻ (`ChannelDsp.Specs(..., bypass)`), sin tocar el proyecto ni la
  exportación. Prendido se pinta ámbar, como un mute, y las listas de efectos de las tiras se atenúan.
- **Qué se ve** (`MainWindow.MixerFilter.cs`, pedido del usuario, #270): en la cabecera, un segmentado con **Audio**,
  **MIDI**, **Carpetas**, **Buses**, **Separadores** y **Vacías** (pistas sin clips), cada uno prendido o apagado por
  su lado ("mixer.show.*" en `config.json`, todos prendidos por defecto). Solo filtra qué tiras se arman (la clave de
  `RefreshMixer` lo lleva); nada cambia en el proyecto ni en lo que suena. Sin carpetas, las pistas que el filtro deja
  van juntas en el lugar de su carpeta (`FolderBlock`), sin arrastrarse de a una; ocultando todo, el mixer lo dice
  ("mixer.filtered"). Entre los lados va una línea como la del master (`PinLineW` en `Faint`), si los dos tienen algo.
- **Vistas** (`MainWindow.MixerViews.cs`, pedido del usuario, #277, por los layouts del mixer de FL Studio): el
  título de la cabecera ("Mixer · XTreme ▾": el título es un `SelectButton`, `AsTitle`, pedido del usuario) elige la
  disposición. Son disposiciones, no presets (pedido del usuario): nada se marca como cambiado y no se guardan vistas
  propias. **XTreme** (el nombre lo eligió el usuario) es la libre y la única con zoom: ancho, Efectos y Envíos y sus
  altos, nombre arriba o abajo y el inspector los elige el usuario. Las demás son las de FL Studio, fijas (sin zoom y
  con Efectos / Envíos deshabilitados, `MixerLayoutFixed`): **Compacta** (64 px) y **Amplia** (112) dejan las tiras con
  paneo, fader y medidor y pasan efectos, envíos y entrada / salida al inspector, prendido; **Compacta alt** y **Amplia
  alt**, lo mismo con el nombre al pie; **Consola** (el "Extra large" de FL Studio, 150) le da a cada tira su cadena y
  apaga el inspector. Al dejar XTreme se guarda cómo estaba ("mixer.xtreme" en `config.json`) y al volver se repone,
  así lo que pone una fija no la toca; la elegida va en "mixer.view". El filtro "Qué se ve" y Conexiones no son de
  ninguna disposición. Aplicar una (`SetMixerView`) usa los caminos de siempre: anchos y altos en el lugar, y un solo
  rearmado si cambia el lugar del nombre.
- **Inspector del canal** (`MainWindow.MixerInspector.cs`, #277, como el Track inspector de FL Studio): **Inspector**
  en la cabecera ("mixer.inspector", apagado por defecto; Compacta y Amplia lo prenden) muestra un panel de todo el
  alto, a la derecha del master o antes de las tiras (clic derecho en el panel: "Inspector a la izquierda / a la
  derecha", "mixer.inspSide"; columna 0 o 3 de la grilla de las tiras), con el canal
  elegido (pista, carpeta o bus; el master no se elige en el mixer): badge, nombre (doble clic renombra) y tipo,
  Entrada (pistas), Salida (`OutputSelect`; un bus siempre al Master), la lista entera de efectos (`SlotList`: agregar,
  arrastrar, ⏻, abrir), los envíos (`CompactSends`: nivel, PRE, ⏻ y su menú) y el paneo. Sin canal elegido lo dice en
  una línea. Se arma con las mismas piezas de las tiras en un `MixerLayout` propio, solo cuando cambia la selección o
  la clave de las tiras, y se refresca con el mixer; los carriles mueven sus controles como en las tiras. Su borde
  que da a las tiras cambia el ancho (200–480, "mixer.inspectorW"; doble clic, 260).
- El **fader** (`Controls/Fader.cs`) tiene la tapa de una consola (pedido del usuario: grande y fácil de agarrar, 30 × 40; 18 × 40 en las tiras más angostas):
  cuerpo gris sombreado, ranuras, una línea blanca sobre una banda oscura en el nivel (blanco solo no se lee sobre el gris) y una sombra abajo; la ranura oscura, con su contorno, se ilumina en
  acento pleno hasta el nivel. La escala en dB va en el medidor. El volumen del encabezado de pista es el mismo fader acostado (`DrawVolume`, `Fader.DrawSideCap`).
- Las **perillas** son todas iguales (`Knob.Draw`, en el control y en el timeline, pedido del usuario): tapa de consola, como el fader, con faldón estriado que gira con el valor (desde 28 px), tope sombreado, sombra y puntero blanco, y alrededor una escala fina en acento.
- **Slots de efectos** (`SlotOwner`: el canal de una pista o un bus): en la tira, su lista; un clic en un renglón de un
  VST3 cargado abre la ventana del plugin (pedido del usuario: sin un paso en el medio, `OpenEffectWindow`); en los
  demás, o en un VST3 que todavía carga o falló, abre el panel del slot en su ventana (`ShowSlotWindow` → `SlotPanel`: `FxBody`,
  o `VstPanel`, armado en un `MixerLayout` aparte y
  refrescado con el mixer mientras está abierto). Es una ventana propia, como la de un VST3 (pedido del usuario, #140):
  arranca pegada abajo de lo que la abrió (desde un menú, donde fue el clic: `lastPressScreen`), se arrastra desde su
  barra, queda abierta y puede haber varias (`slotWindows`, una por slot: abrirla otra vez la trae al frente); su X o Esc
  la cierran, y se cierra sola si el slot o su dueño se van (`RefreshSlotWindows`) o cambia el proyecto
  (`CloseSlotWindows`). Toma el tamaño de su panel y crece hacia la derecha y abajo sin salirse de la pantalla; un tipo repetido se numera, "EQ 2", y un VST3 lleva el nombre del
  plugin). Su menú (`ShowSlotMenu`, pedido del usuario) sale del ⋯ del título (`SlotMenuButton`) y del clic derecho
  en su renglón del mixer o en el título de su sección del inspector: Abrir (el plugin de un VST3 cargado; el panel,
  desde el mixer), Prender / Apagar, Apagar los demás (deja sonando solo ese en la cadena), Subir, Bajar, Duplicar y
  Eliminar, cada uno un paso de deshacer. Los paneles angostos se reacomodan (`FxNarrowW`, `FxGrid`) en vez de recortar controles.
  La rayita bajo el primer compresor de la lista muestra lo que comprime toda la cadena. La lista y las perillas del
  timeline son un atajo al primer slot de cada tipo; si no hay ninguno, prenderlo ahí lo vuelve a poner en su lugar de
  siempre (`Channel.AdoptSpares`). Cada cambio de la lista es un paso de deshacer y reconstruye las tiras (la clave de
  `RefreshMixer` lleva los ids de los slots). `InsertSlot.Collapsed` (plegado de las tiras amplias que ya no están)
  se conserva en el archivo.
- **La cadena del master es su lista de efectos** (`MasterSlots`, `MasterSettings.Slots`, pedido del usuario): la misma en la
  tira del master y en Mastering, así que agregar, quitar o mover en un lado es la misma edición. Por ahora solo
  nativos. Sin efectos un proyecto nuevo no procesa nada; el primero prende el master. Su "+" trae **Cadena completa**
  (`AddMasterChain`): agrega las etapas que falten (Limpieza, Multibanda, Color, Cinta, Ancho). "+ Efecto" de cualquier
  canal tiene el grupo **Mastering** con esas etapas, Soft clip y Limitador. Un proyecto de antes pasa su cadena fija a
  slots al abrirse y suena igual (`ChainInSlots`, `MoveChainToSlots`).
- **Sidechain** (`KeyChoice` en `MainWindow.FxPanel.cs`): el compresor, los compresores clásicos, la puerta y el Bombeo
  llevan arriba de sus perillas un desplegable "Sidechain": Propia o una pista del proyecto (menos la suya). Una pista
  quitada se ve como "Pista quitada" y mientras tanto escucha su propia señal.
- **Panel del Bombeo** (`MainWindow.PumpPanel.cs`, pedido del usuario: como KickStart): arriba Tempo / Sidechain y la
  división en botones (1/16 a 1/1; con Sidechain, el desplegable de la pista; elegir Sidechain arranca en una pista
  llamada como un bombo, o la primera); la Profundidad como perilla grande al lado del gráfico; debajo, las formas
  (`PumpShapes`: largo y curva de un clic, la elegida en acento); después las perillas. Con sidechain baja
  según el nivel de la otra pista (`PumpInsert.Keyed`: su envolvente sobre el umbral, en `SpanDb`, da cuánto baja, con la
  forma de la Curva) y las perillas son las de un compresor: Umbral (Auto = `SpanDb` debajo del pico reciente), Ataque, Hold, Release (`keyMs`) y
  Curva; se van la división, las formas, Largo y los controles finos.
- **Gráfico del Bombeo** (`PumpView`): un período de la curva, grande, sobre el sonido que pasó por cada tramo
  (`PumpInsert.WaveIn/WaveOut`: lo que entra tenue, lo que sale en acento) y una línea con un punto que la recorre con lo
  que suena (lee el efecto cada 33 ms; sin bloques nuevos, la línea se va y el sonido se atenúa). Con sidechain, a la
  derecha una barra con el nivel de la key (contra su pico reciente, o en dB con umbral), la raya de acento donde dispara
  y la tenue donde se rearma.
- **Balance tonal** (`TonalView`, `MainWindow.Tonal.cs`, pedido del usuario, #210; slot `tonal`, familia Tono): analiza,
  no toca el sonido (pasa bit a bit, sin `FxShell`; sin Dry, Wet, salida ni carriles). En el master lee la salida, después
  del limitador (pedido del usuario, #210: `InsertRack.Analyze`), y en Mastering su ficha va después de Salida, sin mover. Arriba, presets, Empezar de nuevo y
  Referencia… (uno o más archivos: su balance promedio pasa a ser el objetivo propio del slot, `InsertSlot.Tonal`, guardado
  en el proyecto); el objetivo (seis géneros de fábrica, `TonalTargets`, o el propio) y Estéreo / Mid / Side. Compacto: las
  cuatro bandas (Graves 20–120 Hz, Medios graves 120 Hz–1 kHz, Medios agudos 1–5 kHz, Agudos 5–20 kHz), cada una con el
  rango del objetivo y la marca de la mezcla (en `Mute` si se sale), y el punch de los graves (cresta bajo 120 Hz, con su
  valor). Full (y en Mastering): además el espectro de largo plazo (1/6 de octava, suavizado de 1/3, nivelado en 200 Hz–5 kHz)
  sobre la banda del objetivo. Respuesta: cuánto mira hacia atrás el promedio (1–10 s). Analiza solo mientras su panel está a
  la vista (`TonalAnalyzer.Pull` en el tick de la vista); el silencio no cuenta, así que parar deja la lectura.
- **"+ Efecto" por categorías** (`FxGroups`, pedido del usuario): Dinámica, Compresores clásicos, EQ, Saturación, Modulación, Espacio y estéreo, Mastering (las siete etapas de la cadena, en su orden, solo ahí: #257) y Análisis
  (se abren al pasar el puntero), después VST3. Un efecto nuevo va en su grupo. Una perilla con `FxParam.Steps` va por pasos,
  como una llave, y muestra el nombre del paso si tiene uno ("Auto", "Todos").
- **Mastering** (`MainWindow.Master.cs`, rediseñada como rack con editor, pedido del usuario, 2026-11-03): la tercera
  vista del selector Timeline · Mixer · Mastering (la X sigue alternando timeline / mixer). Usa el lugar del mixer
  (`MixerHost`, con su altura y su lugar en solo audio) o su propia ventana: esconde la cabecera y las tiras y muestra
  tres filas. El botón "Master" del canal master (en acento con el master prendido) también lleva ahí.
  - Nada se desplaza de costado (pedido del usuario): la barra (`ToolbarPanel`) y la cadena bajan de renglón en una
    ventana angosta, y el panel del editor reparte sus perillas en el ancho que tiene.
  - **Arriba**, la barra: **Master** con su ON, que prende o apaga **toda la cadena, Salida incluida** (pedido del
    usuario: un solo botón; `MasterSettings.ToParams` apaga también el limitador, el soft clip ya lo leía), "+ Efecto",
    y a la derecha el **Destino** (Como está · YouTube · Instagram · TikTok · Spotify · Apple Music, `MasterLoudness`).
    Apagado, la mezcla pasa sin tocar y el Destino sigue dando su ganancia, para que la exportación caiga donde iba.
    Un proyecto nuevo arranca con el master prendido y la cadena vacía (`ProjectLibrary.Create`); uno viejo apagado
    pierde el limitador que antes quedaba prendido.
  - **La cadena** (`Controls/ChainPanel.cs`), fichas en el orden en que suena, con flechas entre ellas; cuando no
    entran, sigue en el renglón de abajo y una línea sale de la última ficha, pasa por debajo y entra en la primera
    del renglón siguiente (pedido del usuario). Primero los efectos de `MasterSettings.Slots`, numerados, cada uno con
    manija para arrastrarlo a cualquier lugar de la cadena (también entre renglones: las demás se deslizan a su lugar
    nuevo); después **Salida**, siempre al final y en este orden: **Soft clip** (`ClipOn`) y **Limitador** (`LimOn`).
    Cada ficha: arriba su número o "SALIDA", ícono, el nombre entero (la ficha crece con él, nunca se corta), ON y una
    rayita de reducción; clic derecho, su menú. Soft clip y Limitador no son módulos porque van después de la ganancia
    del Empuje o del Destino.
  - **El editor**: el panel de la ficha elegida (`PickModule`, marcada con el borde de acento; se recuerda al
    reconstruir), el de siempre (`FxBody`) con sus perillas en una fila y su reducción (`ModuleMeter`; el primer
    Multibanda, una por banda y la Púa, con `MasterBandsGraph` y sus cruces). Soft clip: Recorte y su barra (en 0
    dice "Auto"). Limitador: Empuje, Techo en pico real (−1 dBTP por defecto), Release y su barra. Todos los paneles
    están armados en un `PagedPanel`, que muestra uno con su tamaño (la caja se ajusta al panel: nada de espacio
    vacío) y recuerda el más alto: la vista tiene ese alto y no cambia al elegir otro.
  - **Fijo a la derecha** (pedido del usuario, `masteringPin`, `LiveMeter`): **En vivo**, el mismo `MixerMeter` del
    master con LUFS de los últimos 3 s e integrado, cuánto falta para el Destino (sin destino, −14), pico real de la
    pasada, COR y MONO, y **LUFS · 30 s** (`LoudnessHistory`: los LUFS de 3 s cada 0,1 s mientras suena, con la
    línea del Destino; se borra con cada pasada) con qué está haciendo el Destino debajo. En su fila de título, arriba
    a la derecha, el botón de desacoplar (`masteringFloat`).
  - Los Looks y Sugerime se sacaron de la app (pedido del usuario, 2026-11-03); `MasterLooks` y `MasterSuggest`
    siguen en el motor para `xtlab`.
- **Destino** (`MainWindow.Loudness.cs`): con un destino, cada edición (`MarkDirty`) espera 1,5 s y mide la mezcla
  entera en segundo plano con el mismo `MixMaster.Render` de la exportación; la ganancia que encuentra va a
  `MasterSettings.TargetGainDb` (se guarda) y la reproducción se desliza hasta ella (50 ms), así que se escucha al
  volumen de esa red mientras mezclás y la exportación arranca de esa ganancia y cae en la misma. Ocupa el lugar del
  Empuje (la perilla se apaga y muestra la ganancia). No mide mientras grabás ni mientras hay audio cargándose o
  transponiéndose (no lee disco en la UI); una edición nueva cancela la medición en curso.
- Por defecto la vista tiene justo el alto de su contenido a su ancho, con el editor del alto de su panel más alto
  (`FitMasteringHeight`, también si uno crece o si el ancho cambia los renglones). Los divisores la agrandan o la
  achican (pedido del usuario, 2026-11-03; lo que no entra se desplaza para abajo): ese alto es suyo
  (`masteringH`, "masteringHeight" en `config.json`), aparte del del mixer, y doble clic en el divisor la vuelve a
  ajustar al contenido. El mixer y el piano roll también tienen cada uno el suyo (`PlaceH`: "mixerHeight" y
  "pianoHeight", pedido del usuario, #139); el del piano roll arranca igual al del mixer hasta que se lo cambia.
- No se mide nada: cada banda sigue su propio nivel mientras suena (`MasterBandComp`) y comprime lo que pasa de ahí,
  así una perilla se escucha en el momento, con cualquier mezcla y a cualquier volumen. Sin limitador, la exportación
  con destino de LUFS frena la ganancia donde el pico real toca el Techo.
- **Docks y orden** (`MainWindow.MixerOrder.cs`, pedido del usuario; #277, como los docks de FL Studio; #303: solo
  izquierda y derecha, el dock izquierdo de #277 se sacó): el mixer tiene dos lados (`MixerOrder.Sides`, por índice de
  `MixerSides`): el izquierdo (columna 0) y el dock derecho (columna 2, pegado al master fijo). Cada uno tiene su
  propio scroll y la línea entre los dos es un divisor
  (`MainWindow.MixerSplit.cs`, pedido del usuario, #275): arrastrada, le da más ancho a uno que al otro (la proporción
  va en `config.json`, "mixerSplit"), pero el dock derecho nunca pasa del ancho de todas sus tiras (`MixerRightFullW`,
  #285: el divisor frena ahí y el lado izquierdo se queda con el resto). Cuando llega una tira al dock derecho (nueva o movida) y
  no se ven todas, el divisor se corre solo hasta mostrarlas, dejándole al lado izquierdo al menos 80 px (`GrowMixerRight`,
  #285; #303: espera a que el dock esté medido, porque una segunda reconstrucción antes de que corra lo dejaba sin abrir); con doble clic vuelve a la de fábrica, donde el dock derecho toma lo que necesita
  hasta la mitad. Uno solo ocupa todo el ancho. Las líneas que separan los lados y el master fijo (también el medidor de
  Mastering) son una ranura (`Controls/Groove.cs`, #275): el fondo más oscuro entre dos líneas `Line`; la del divisor
  lleva una manija al medio que se prende en acento con el mouse encima o al arrastrar. **Dock**, en el menú de una tira en el mixer (pista de la raíz, carpeta,
  separador o bus), la pasa a "Fijar a la izquierda" / "Fijar a la derecha" (`DockMenu`, `MoveToDock`,
  un paso de deshacer; un bus queda después de los canales de su dock, y los buses que seguían a una pista quedan en su
  dock de antes). En el proyecto: `MixerSide` null o "left" / "right" ("leftDock", el dock izquierdo de #277, abre a la izquierda).
  Reemplazó al "Fijar lado" del divisor. La ruedita, el zoom y el arrastre de una tira
  junto al borde mueven el lado que está bajo el mouse; si uno muestra su barra, el otro y el master dejan el mismo
  alto al pie. Por defecto pistas y
  carpetas van a la izquierda y los buses al dock derecho (`MixerSides`; #301: ese lado de los buses se elige en
  Configuración → Apariencia → Mixer, `MixerOptions.BusDock`, "mixer.busDock": izquierda o derecha, y mueve los buses que no se sacaron de
  su lugar por defecto: un bus guarda null en ese dock y su dock si está en otro); cualquier tira se arrastra desde su tarjeta
  (`MixerDraggable`: un clic sigue siendo un clic, esté seleccionada o no, y el arrastre arranca a los 6 px) a otro lugar,
  a una carpeta abierta o al otro lado, deslizándose como todo lo que se reordena (`SlotDrag.SlideEase`, pedido del
  usuario): la tira sigue al mouse (una imagen suya en una capa sobre los dos lados, `LiftMixerGhost`, #280: cada lado
  scrollea aparte y recortaría la tira al pasar a otro), las demás se corren para hacerle lugar (a la derecha en el lado izquierdo, a la
  izquierda en el derecho, que está pegado al master) y al soltar se acomoda antes de cambiar el orden (`MixerDropAt`,
  `MakeMixerRoom`, `FinishMixerDrag`); el mixer scrollea cerca de los bordes. Apretar en el fondo vacío del mixer
  deselecciona todo. Pistas, carpetas y separadores se mueven en el mismo orden del timeline (`TrackFolders`, un
  `EditTimeline` con su deshacer), así que el timeline lo sigue y al revés. Los buses van a cualquier hueco, también
  entre los canales (#270): cada uno sigue a la tira que tiene a su izquierda (`Bus.MixerAfter`: el id de una pista,
  carpeta o separador de arriba, `MixerSides.First` para el principio de su lado, o null y un id que ya no está para
  después de los canales de su lado, como antes); su orden entre buses es el de `ProjectSettings.Buses`. Una tira que se
  mueve deja a sus buses donde estaban (pasan a seguir a la de antes), y los buses que había entre el hueco y la tira
  siguiente pasan a seguir a la que llega (`MixerSlot.Trailing`, `Reanchor`); igual una pista, carpeta, separador o bus
  nuevos del clic derecho, que van al hueco del clic (`NewBusAt`). Una tira oculta por el filtro deja a sus buses en su lugar. Los **separadores** se ven como una tira angosta (`SeparatorStrip`, `SeparatorW`; la misma doble línea que en el timeline) con su
  nombre chico y en mayúsculas escrito hacia arriba y un ojo arriba que los muestra u oculta en el timeline; un clic abre su menú y un doble clic,
  directo su color, o sobre el nombre, renombrar (#194: por eso el menú del clic izquierdo espera `SeparatorClickWait`). Uno nuevo va donde se pide
  (`NewMixerSeparator`, #194): "Nuevo separador" del clic derecho en el fondo, en el hueco más cercano al clic;
  "Separador" en la cabecera (`AddSeparatorButton`), justo después del canal o la carpeta elegidos, de su lado; sin
  nada elegido, al final del lado izquierdo.
- **Paneo**: un solo desplegable (`BuildPanMenu`) con la apertura arriba (30 % por defecto) y las plantillas con su
  descripción; elegir una la aplica y cierra el menú.
- **Buses** (`MainWindow.Buses.cs`, `ProjectSettings.Buses`, pedido del usuario): un solo concepto para bus y FX. A la
  derecha, una tira por bus (`BusStrip`) armada como la de una pista (ver "Una tira para todos los canales"): su badge,
  nombre y "BUS", su lista de EFECTOS (donde "+" también ofrece Reverb y Delay), la sección de envíos vacía, paneo y M,
  y al pie sus dB. La rayita bajo su primer compresor muestra lo que comprime (`MixerLayout.NodeMeters`). Un clic lo
  **selecciona** como a una pista (`SelectBus`, `selBusId`: el timeline suelta su pista o carpeta y elegir otra cosa
  suelta el bus) y el inspector muestra su panel (`InitBusInspector`: badge y nombre, qué tiene, color, ícono y
  "Quitar bus"). Tiene **color e ícono** como una pista (`Bus.Color`, `Bus.Icon`; el mismo selector, `ShowLook` /
  `ShowBusLook`, desde su badge, el inspector o el menú); sin ícono muestra siempre el mismo (`FxIcon.Bus`, pedido del
  usuario: no depende de sus efectos), que también es el de los envíos. Clic
  derecho: Renombrar, Color e ícono…, Quitar bus (sin preguntar y en un paso de deshacer: se van los envíos que le
  llegaban y lo que salía a él pasa al Master). Su SALIDA es siempre el Master (decisión
  del usuario, `ProjectBuses.CanOutput`), pero **manda a otros buses** como una pista (#264): su sección de Envíos en
  la tira del mixer y del inspector (`RouterOf(bus)`), sin ofrecer uno que cerraría un
  bucle (`ProjectBuses.CanFeed`). **Se automatiza como una carpeta** (#267: `Bus` es `IAutomated`): clic derecho en su
  fader, paneo, M, un envío o una perilla de sus efectos (en la tira del mixer y en el inspector) ofrece su carril. Sus
  carriles van en las **filas de bus al final del timeline** (decisión del usuario, como los canales FX de Cubase o los
  aux de Logic; `TimelineView.Buses.cs`): una fila por bus **con automatización** (#288: un bus sin carriles no tiene
  fila; el primero sale de los controles de arriba o de "Automatización" en el clic derecho de su tira, `BusMenu`),
  después de todas las pistas y carpetas, sin clips, con su
  badge, nombre, "BUS" y M, y sus carriles debajo (se editan como los de una carpeta). Un clic la selecciona como en el
  mixer (`BusRowSelected` → `SelectBus`); su chevron pliega los carriles; clic derecho: Automatización, Ver la
  automatización y "Mostrar buses en el timeline". Se ven por defecto, también en proyectos viejos, y se ocultan todas
  juntas con esa opción (también en el menú del espacio vacío del timeline, si algún bus tiene carriles): `ProjectSettings.BusRowsHidden`, del
  proyecto, sin deshacer; ocultas, sus carriles siguen sonando. No hay columna "+ Bus" (pedido del usuario): clic derecho en el mixer, también
  en su fondo vacío (`InitMixerMenu`) → "Nuevo bus / canal FX" agrega uno vacío en el hueco del clic (`ProjectBuses.Add`, `NewBusAt`), y el efecto se
  le suma con "+"; el mismo menú tiene "Nuevo separador" y, arriba, las pistas nuevas del + del timeline (audio, MIDI,
  con instrumento, carpeta, plantilla, importar: `NewTrackItems`; pedido del usuario, #196), que van, como el separador, al hueco más cercano al clic
  (`mixerNewAt`: lo que crea la próxima edición se mueve ahí con `MoveToSlot`, en el mismo paso de deshacer). Un slot apagado deja pasar lo que le llega. Los selectores de
  salida y los envíos de pistas y carpetas ofrecen los buses (`Targets`, `ProjectBuses.CanFeed`), y la clave de
  `RefreshMixer` lleva el ruteo de los buses para rearmar las tiras cuando cambia.
- **Envíos en las tiras** (`Router`: pista, carpeta o bus): una barra por bus de su lista (`CompactSends`), con "+"
  para elegirlos y el mismo menú de clic derecho que en el inspector (`ShowSendMenu`). Cada pista elige sus buses
  (`Send.Hidden`, `ProjectBuses.Shown` / `ShowSend`): una pista o un bus nuevos no traen ninguno (pedido del usuario:
  se suman a mano) y sacar uno lo apaga y guarda su nivel. La clave de `RefreshMixer` lleva los envíos de cada lista
  para rearmar las tiras. La salida (`OutputSelect`) va en el inspector y, en la tira de una carpeta, bajo sus envíos.

## Pistas MIDI y piano roll (`MainWindow.Midi.cs`, `Controls/PianoRollView.cs`, `Controls/TimelineView.Midi.cs`)

Etapa 1 del editor MIDI (pedido del usuario: armar bases "al más puro estilo FL Studio", con clips por pista como
Logic o Studio One). Sigue en PENDIENTES.

- **Pista MIDI** (`Track.Kind = "midi"`, "+ Nueva pista" → Pista MIDI, ícono de piano): nunca lleva panel de video.
  "Pista MIDI con instrumento" (`NewInstrumentTrackMenu`, pedido del usuario) es un submenú con los instrumentos de
  Instrumento (nativos y VST): la pista nace con el elegido y su nombre, en un solo paso de deshacer; un nativo abre su ventana.
  Tomas y clips de audio no entran en ella. Su ● la arma para grabar el teclado MIDI (ver "Teclado MIDI" abajo).
  Tiene su tira en el mixer como cualquier pista (fader, paneo, slots, envíos) y su Transponer sube o baja las notas.
- **Clips MIDI en el timeline**: caja con el color de la pista y sus notas en chiquito (entre su nota más grave y la
  más aguda, con una octava como mínimo). Clic elige, arrastrar el cuerpo lo mueve (en el tiempo y a otra pista MIDI,
  con el imán del timeline; Alt, libre), el borde derecho cambia el largo de a pasos de la grilla, doble clic lo abre
  en el piano roll y doble clic en un tramo vacío del carril crea uno de 4 compases ahí y lo abre. Supr lo quita, D lo
  duplica justo después; clic derecho: abrir, duplicar, copiar, cortar, quitar. Entran en la selección de varios (Ctrl+clic y
  rectángulo, mezclados con tomas): arrastrar uno marcado por el cuerpo los mueve a todos el mismo tiempo, cada uno en
  su pista (`ClipsMoved`, un solo paso de deshacer), y Supr, D y el clic derecho actúan sobre todos
  (`MidiEditing.DuplicateMany`: el bloque entero va justo después de sí mismo), también Ctrl+C / Ctrl+X / Ctrl+V
  (`ClipClipboard.MidiItems`, pedido del usuario). **Dividir** (herramienta, Alt+X y clic derecho, `MainWindow.MidiSplit.cs`), **Unir clips** (Ctrl+J y clic derecho sobre
  varios marcados, #311: uno por pista, del primer inicio al último final, con bucles y patrones escritos como notas y
  los huecos en silencio, `MidiEditing.Join`), **Borrar**, **Escuchar** y el rango también actúan sobre ellos.
  Una división nunca cambia lo que suena (decisión de diseño): la nota que cruza el corte queda en los dos pedazos, la de la derecha con
  `MidiNote.Tie`, que `NoteExpression.JoinTies` une a la que termina justo ahí con el mismo tono, en cualquier clip, antes de armar los eventos: un solo ataque, un solo release, el mismo glide. Sin
  nada que termine ahí es una nota común: al separar los pedazos vuelve a atacar, y una copia (`MidiClip.Copy`) no queda atada. En un clip de patrón, un corte en la raya de una vuelta deja dos clips
  del patrón. En otro lado la izquierda sigue en el patrón y la derecha queda clip propio con sus repeticiones escritas (un clip de patrón no tiene cómo empezar en medio de una vuelta); lo avisa un
  toast. Los otros instrumentos del patrón que empiezan con él se cortan juntos.
- **Patrones** (`MidiPatterns`, pedido del usuario, 2026-11-01: como en FL Studio, pero por pista): clic derecho en un
  clip → "Convertir en patrón". Duplicar, copiar o pegar ese clip da otro clip enlazado: editar las notas de uno cambia todas,
  también en otras pistas MIDI (cada pista lo toca con su instrumento). El clip muestra el nombre del patrón; estirarlo
  por el borde derecho repite la vuelta (una raya entre vueltas) y acortarlo la corta, sin tocar a los demás. Clic
  derecho: "Renombrar…" (renombra el patrón en todos sus usos), "Largo del patrón" (1 a 16 compases; un clip de una sola vuelta acompaña el cambio) y
  "Hacer único" (sale del patrón sonando igual, con las repeticiones escritas como notas). El menú de la pista MIDI
  tiene "Insertar patrón en el cabezal", y la pestaña Patrones de la barra del proyecto los lista. Un clip de patrón abre el patrón solo (pedido del
  usuario, 2026-11-01: editás ese patrón y nada más): una vuelta desde el compás 1, sin los otros clips de la pista ni
  repeticiones, con grilla e imán contados desde su inicio y el largo en la tarjeta de **Patrón** (1 a 16 compases u
  "Otro…"). El cabezal marca dónde va cualquier clip del patrón que esté sonando, volviendo al inicio en cada repetición
  (`PlayBeat`), y la regla salta dentro del clip abierto. Cada clip guarda su copia de las notas y el patrón no tiene lista propia: desaparece con su último
  clip (`Sync` reparte cada cambio; `ClipClipboard.Paste` usa `Adopt` porque lo copiado puede estar viejo).
- **Varios instrumentos en un patrón** (`MidiClip.PatternPart`, `MainWindow.PatternParts.cs`, pedido del usuario, 2026-11-02;
  decisión del usuario: un clip por pista, no un bloque solo): cada instrumento es una parte con sus notas, tocada por
  la pista donde están sus clips, así conserva su canal y el motor no cambia. Nombre y largo son del patrón; las notas, de la parte.
  En el piano roll, el chip **Patrón** (`pianoPartsButton`: Patrón · cantidad) abre con un clic una tarjeta con el largo y la lista: la fila pasa a editar ese
  instrumento, el ojo prende o apaga sus notas fantasma (tenues, del color de su pista, no se tocan; sin guardar:
  todas a la vista al abrir) y el tacho lo quita con sus clips (salvo el último). "Sumar instrumento" (ahí y en el menú
  del clip) elige una pista MIDI que no toque el patrón, o una nueva debajo: suma un clip vacío en cada uso. Los clips
  de otras partes que empiezan juntos son un uso (`Together`): arrastrar el cuerpo los lleva a todos (`UpdateMidiDrag`, por el
  camino de la selección de varios; por eso no cambian de pista), el borde estira los de igual largo (Shift, solo ese) y D los duplica.
  El clic, Supr y copiar actúan sobre el elegido, para poder callar un instrumento en una sección. Insertar un patrón
  con varios instrumentos pone cada uno en su pista, caiga donde caiga. La fila de la pestaña Patrones los dibuja juntos.
  **A la vista** (pedido del usuario, 2026-11-02): la cabecera del piano roll lleva "Convertir en patrón" en un clip común y, en uno de patrón, "Sumar instrumento" en la tarjeta de **Patrón** (2026-11-04: menos chips)
  (`pianoMakePatternButton`), además del menú del clip. En el timeline, un clip de patrón lleva el ícono de cadena junto al nombre, una línea con un punto en cada clip une
  los instrumentos de un uso (`DrawPatternLinks`) y señalar o elegir un clip contornea con el acento todos los de su patrón (`litPattern`).
- **Tiempos**: el clip empieza en segundos (`AtSec`) como todos, pero su largo y sus notas van en tiempos
  (`LengthBeats`, `MidiNote.Beat/Beats`), así siguen al tempo. Una nota que pasa el final del clip (o de la vuelta de su
  patrón) se corta ahí.
- **Piano roll**: una vista más en el lugar del mixer (botón "Piano roll" del transporte, justo después de
  Timeline, pedido del usuario; o doble clic en un clip),
  como Mastering: misma altura y lugar que el mixer, con la mini timeline arriba en Clásico. Arriba, una barra con pocos
  chips, cada uno con un menú por tema (pedido del usuario, 2026-11-04: con todo a la vista no se entendía nada), en un
  `ToolbarPanel` (lo que no entra baja de fila, desde la izquierda): el nombre de la pista con el largo y la cantidad de
  notas (recortado, `PianoTitleMaxW`); **Patrón ▾** (una tarjeta con el largo de la vuelta, sus instrumentos y Sumar
  instrumento) o, en un clip común, **Convertir en patrón**; el instrumento ▾ y su ventana; **Herramientas ▾**; **Escala ▾** y el acorde que suena; el **imán** propio (chip "Imán: …" con la lista del timeline más **Igual
  que el timeline**, `SnapChoices.PianoIds`, `pianoSnap` en config.json, Paso de entrada) y, al final de su menú, la **grilla** ("Grilla: …", también con clic derecho en la regla; pedido del
  usuario: imán y grilla juntos o cada uno por su lado, como en el timeline): **Igual que el imán** (de entrada; con el
  imán en Libre, Auto), **Auto** (se afina con el zoom: el paso más fino con 14 px entre líneas) o una subdivisión fija
  (Compás a 1/64, 1/8T), guardada en `pianoGrid`; las líneas a menos de 6 px no se dibujan (`PianoRollView.GridMode`).
  **Vista ▾** (franja, pistas en gris, onda detrás y, al final, los atajos y gestos) y sacar a una ventana. **Muestra la pista entera** (pedido del usuario): la regla lleva los compases del
  timeline (clic mueve el cabezal) y cada clip de la pista como una franja del color de la pista (el que se edita,
  más fuerte); en la grilla, una línea en el principio y el final de cada clip, fuera del clip que se edita todo más
  oscuro y las notas de los otros clips tenues (sus velocidades también, en la franja VEL). Un clic adentro de otro
  clip lo pasa a editar y sigue como un clic ahí (`ClipPicked`); fuera de todo clip no se escriben notas. Grilla e imán
  cuentan desde el principio del timeline, como los del timeline. **Muestra la pista elegida** (pedido del usuario): al
  abrirlo y mientras está a la vista (`ShowPiano`, `FollowPiano`), clic en un clip MIDI lo muestra (en la misma pista,
  solo lo trae a la vista); clic en otra pista MIDI (su encabezado o un tramo vacío) muestra esa pista entera, en el
  clip bajo el cabezal (si no hay, el siguiente o el último); sin pista elegida, o con una de audio, queda vacío.
  Abrir un clip (doble clic, "Ver notas") elige su pista. "Pista entera" (`PianoRollView.Frame.Track`) encaja todos sus clips a lo ancho si un compás queda de al
  menos 48 px; si no, deja el cabezal en el medio. Las notas van con las puntas redondeadas y una rayita abajo que se
  llena según su velocidad (pedidos del usuario). Teclado a la izquierda (sus teclas suenan; las
  filas de las negras más oscuras, una línea en cada Do; lo que suena en vivo en la pista, del teclado MIDI o el
  virtual, se ilumina en la tecla y su fila y se apaga en un fundido: `PianoRollView.ShowLive`, desde `LiveNotes.IsSounding`,
  en la capa del cabezal), el cabezal de acento y, abajo, la franja **VEL** con una barra por nota
  (se arrastra para cambiarla; si la nota está elegida, cambian con ella todas las elegidas que empiezan en esa vertical,
  un acorde de una vez). La franja se agranda o achica desde la barrita que la separa de la grilla (40 px como mínimo,
  la grilla conserva 60; doble clic vuelve a 64; `pianoVelHeight` en config.json), pedidos del usuario. A la izquierda
  lleva su escala (127, 100, 64, 32, las que entran) y, mientras arrastrás, el valor al lado del puntero. En una barra
  debajo de la franja, no en la cabecera (pedidos del usuario, 2026-11-02: arriba no se entendía de qué eran; `BuildPianoBottomBar`): primero lo que usa un clic, siempre a mano (pedido del usuario, 2026-11-04): Dibujar / Pintar / Cortar y, al lado, **Largo** (el de las notas nuevas: la última tocada, igual que el imán o un valor fijo; `PianoRollView.NoteLength`, `pianoNoteLen`, pedido del usuario, #195), **Escala** y **Acordes** juntos (pedido del usuario, 2026-11-04; el tipo de acorde ▾ aparece al lado mientras está prendido); a la derecha de todo, fijo, "Expresión" muestra u oculta la franja (`pianoVelShown`) y lo suyo se abre a su izquierda (pedido del usuario, 2026-11-04: el botón no se mueve; todos los botones de la barra y la cabecera llevan ícono): su desplegable elige qué muestra (`NoteLane`: velocidad, mod X, mod Y, paneo, release) y, solo con la franja a la vista, Lápiz / Línea (`pianoVelTool`: el lápiz
  pinta cada barra que cruza, la línea deja una rampa recta; las dos solo tocan las elegidas si hay) y "Paso de velocidad"
  (`pianoVelStep`: un chip con los valores comunes y "Otro…"; los valores van a múltiplos o a 127, Alt libre). Sus menús abren hacia arriba.
  El clic derecho en la franja pinta 100 (`MidiNote.DefaultVelocity`). "Ajustar" abre una tarjeta con Escalar (% alrededor
  del promedio: menos de 100 empareja, más da dinámica) y Humanizar (± al azar), cada uno con su Aplicar (un paso de
  deshacer, sobre las elegidas o todo el clip; `MidiEditing.ScaleVelocity` / `HumanizeVelocity`, valores en
  `pianoVelScale` / `pianoVelHumanize`). Gestos como FL: clic en vacío escribe una nota con el largo y la velocidad de la
  última tocada (y se arrastra), arrastrar una nota la mueve (con las elegidas; Alt, libre), su borde derecho la
  alarga, Shift+arrastrar una nota copia las elegidas y las suelta donde soltás (con el imán), clic derecho borra
  (arrastrando, varias; en un lugar vacío también deselecciona), Ctrl+arrastrar en vacío elige con un rectángulo,
  Ctrl+clic suma o saca. La regla mueve el cabezal; sostenido, hace scrub en vivo como la del timeline (pausa mientras
  arrastrás, `ScrubChanged`, y corre la vista en los bordes). Teclas con el foco en el piano roll: Supr borra las
  elegidas (nunca el clip), Ctrl+A todas, ↑ ↓ (o Shift+↑ ↓) un semitono, Ctrl+↑ ↓ una octava, Shift+← → un paso
  del imán (sin salir del clip), Esc, y los atajos copiar / cortar /
  pegar / duplicar del timeline con las notas (pedidos del usuario): Ctrl+V pega en el cabezal si está dentro del clip
  (con el imán) o donde estaban, D duplica justo después de las elegidas (en pasos del imán); todo queda dentro del
  clip que se edita (lo que empezaría después de su final no se copia). El portapapeles de notas sirve entre clips. La ruedita con los mismos modificadores que el timeline (zoom, costados, alto de las filas).
  Cada gesto que cambia algo es un paso de deshacer (`NotesCommitted` → `MidiEditing.SetNotes` por `EditTimeline`).
- **Instrumento VST3** (`MainWindow.Vst.cs`, `Track.Instrument`, `PluginRef`): clic derecho en la pista →
  "Instrumento: …", y lo mismo en la cabecera del piano roll: un chip con el nombre (▾ abre la lista; "(cargando…)"
  mientras abre) y un botón que abre la ventana del plugin. La lista (`InstrumentItems`, pedido del usuario) va en dos
  grupos con título (`MenuGroup`): NATIVOS (el tono de prueba, el Sampler, el Granular y General MIDI) y VST3 (cada instrumento con su marca; desde 12,
  `ByVendorFrom`, un submenú por marca, con el tilde en la del que se usa), y abajo "Buscar plugins de nuevo",
  "Administrar plugins…" y "Abrir la ventana de …".
- **Inspector de una pista MIDI** (`InitMidiInspector`, `RefreshMidiInspector`): INSTRUMENTO, una tarjeta con un
  desplegable (nombre, "Nativo" o "VST3 · marca" y ▾; abre el mismo menú de arriba, nunca una lista a la vista: con
  muchos plugins llenaba el panel), cómo va (listo, abriendo, no abrió) y "Abrir la ventana". Debajo, las mismas secciones
  que una pista de audio (Mezcla, efectos, salida y envíos, Transponer; decisión del usuario, 2026-11-03: sola no las
  mostraba y con otras sí), y en el riel también.
- **Sampler** (`MainWindow.Sampler.cs`, `Dialogs/SamplerWindow.cs`, `Track.Sampler`, pedido del usuario, 2026-11-02: lindo, funcional y fácil; un sample por ahora): en NATIVOS de la lista de instrumentos. Es una ventana por pista, en el lugar de la de un plugin ("Abrir la ventana", clic en el chip), flotando sobre la app (`Show(this)`), sin pestañas: todo a la vista. Arriba, nombre y datos del sample, Reverse, Normalizar, Cargar y Quitar, y la onda (`SamplerWaveView`): sin sample es el lugar donde soltar uno (o un clic para elegirlo; entra siempre vinculado, sin preguntar, también en el Granular: decisión del usuario, 2026-11-03); con sample se arrastran las banderas S y E (inicio y fin) y las L del loop, y las voces corren encima en una capa propia. La onda se acerca hasta ver cada muestra (pedido del usuario, 2026-11-02: clavar el loop en el cruce por cero): la ruedita hace zoom donde está el puntero, con un modificador la corre, arrastrar lo vacío la mueve y doble clic la muestra entera; de cerca dibuja el sonido mismo (no los picos), a la escala de lo que se ve, con su línea de cero. Debajo, "Ver todo", "Inicio del loop" y "Fin del loop" (van a esa bandera) e **Imán al cero** (`samplerZero` en config.json, prendido de entrada): toda bandera cae en el cruce hacia arriba más cercano de la suma de los canales, a menos de 50 ms (`SamplerSound.ZeroCrossing`), con Alt libre; prendido, de cerca los cruces se marcan con un círculo. Abajo, cuatro tarjetas: Reproducción (Nota / One shot, loop, Raíz, Detectado, Tono, Voces, Sigue la tecla, Legato, Fino, Glide, Crossfade), Amplificador (`SamplerEnvelopeView`: la envolvente con sus puntos arrastrables, A D S R, Volumen, Paneo, Vel), Filtro (tipo, Corte, Reso, Drive, su envolvente, Env, Tecla, Vel; atenuado sin filtro) y Modulación (forma, Tempo o Libre, LFO, Espera, a Tono, Filtro y Vol, y la envolvente de tono). Al pie, el teclado (`SamplerKeysView`): clic toca, más abajo más fuerte; clic derecho elige la raíz. Se apaga lo que no aplica (el loop en One shot, Legato y Glide con más de una voz, Crossfade salvo en loop hacia adelante, la perilla LFO con Tempo).
  Un archivo entra soltándolo en la ventana, soltándolo en una pista MIDI sin plugin (`InitImport`) o desde la Biblioteca ("Abrir en el sampler", en la pista elegida o en una nueva): se copia o vincula como una importación (`LoadSample`) y queda como `Media`. Un sample nuevo toma la raíz de su nombre ("Piano_C3") o de su sonido, ya afinado, y si no tiene altura y dura menos de 3 s queda en One shot. Detectado (pedido del usuario) muestra la nota y los cents del tramo entre S y E, se mide de nuevo al mover esas banderas (`MeasurePitch`) y dice "afinado" si la raíz y Fino dejan el sonido a menos de 2 cents; si no, el botón Afinar pone raíz y Fino. Mientras la ventana está al frente el teclado MIDI y el virtual tocan esa pista (`samplerFront`, `UpdateKeyTarget`) y los atajos siguen andando (`Shortcut`). Editar es un paso de deshacer por gesto (`EditSampler`, clave por perilla). Corte, Reso, Drive, Env, Volumen, Paneo y Tono, Filtro y Vol del LFO se automatizan: clic derecho en la perilla (`Automate`, que pasa por `AutomateOn`) o el submenú Sampler de Automatización de la pista; mientras suena, la perilla sigue su carril.
- **Granular** (`MainWindow.Granular.cs`, `Dialogs/GranularWindow.cs`, `Track.Granular`, pedido del usuario, 2026-11-03: un granular como instrumento aparte): en NATIVOS, debajo del Sampler. Una ventana por pista como la del Sampler, sin pestañas. Arriba, nombre y datos del sample, Normalizar, Cargar y Quitar, y la onda (`SamplerWaveView`, la misma): las banderas S y E marcan la parte que recorre el cabezal y el cabezal de cada voz corre encima; "Ver todo" debajo. Cuatro tarjetas: Grano (`GrainShapeView`: la forma de los granos, de la misma tabla que el motor; Sync o Libre, Densidad, Tamaño, Jitter, Forma, Sesgo), Cabezal (Estirar / Recorrer, Posición, Velocidad, Spray, Sync y Reverse, BPM del sample; Velocidad, Sync, Reverse y BPM se apagan en Recorrer), Tono (Raíz, Tono, Voces, Sigue la tecla, Fino, Ancho) y Amplificador (la envolvente, A D S R, Volumen, Paneo, Vel). Al pie, el teclado. Un archivo entra soltándolo en la ventana o desde la Biblioteca ("Abrir en el granular"), por el mismo `LoadSample` del Sampler; un sample nuevo toma la raíz como en el Sampler. Editar es un paso de deshacer por gesto (`EditGranular`). Posición, Velocidad, Densidad, Tamaño, Jitter, Spray, Ancho, Volumen y Paneo se automatizan (clic derecho en la perilla o el submenú Granular de Automatización).
- **General MIDI** (`MainWindow.Gm.cs`, `Track.Gm`, pedido del usuario, #200): en NATIVOS, debajo del Granular, un submenú con las 16 familias GM (8 programas cada una) y **Batería** con los kits GS; el tilde va en el programa y en el título de su familia. El desplegable del inspector y el chip del timeline cambian el programa (un paso de deshacer, `SetGm`); el tono queda. **Su ventana** (`Dialogs/GmWindow`, Abrir la ventana; rediseñada en #212): arriba, el ícono de la familia (`TrackIcons`), el nombre con número, familia y banco, ◀ ▶ (programa o kit vecino, da la vuelta), el botón de lista, **Banco** y la perilla **Volumen** (`GmSettings.VolumeDb`, −∞ a +12 dB, sobre lo que ya suena, con rampa por bloque; Restablecer no la toca). Debajo, la lista: familias y kits con su ícono a la izquierda, sus programas en mosaicos a la derecha y **Buscar instrumento** (nombre, familia o número). El botón de lista la pliega ("gmCompact" en `config.json`): la ventana se ajusta a su contenido y aparece un desplegable con un submenú por familia. Abajo, **Sonido** con **Restablecer** y tres tarjetas: Envolvente (gráfico `SamplerEnvelopeView` de solo lectura con la envolvente del preset en Do4 con el tono encima; A/D/S/R), Filtro (gráfico `GmFilterView`: la curva para una nota fuerte y, punteada, la más suave; Brillo, Resonancia, Brillo vel.) y Tocar (Sensibilidad, Transponer, Afinación, Glide en dos filas, con el chip Mono en su título); y el teclado. Escribir en la búsqueda no dispara atajos. Las perillas son sobre el banco (`GmTone`): las bipolares se imantan al centro, que es el sonido del banco; cada arrastre es un paso de deshacer (`EditGm`). En la batería Transponer queda deshabilitado; Glide, sin Mono. El banco (`GmBank`; todos se bajan, ninguno viene con la app, #304: en **Banco** cada uno dice "bajar" y su tamaño hasta que está) se lee una vez en segundo plano; mientras tanto la pista dice que carga; sin su banco suena con el primero bajado, y sin ninguno calla y lo avisa. Al importar un `.mid` con más de una pista, **Importar MIDI** (`Dialogs/MidiImportDialog`) pregunta cuáles traer (todas marcadas, Todas / Ninguna); cada pista nueva toma su nombre del archivo (sin nombre, el de su instrumento), su programa GM y, en el canal 10, la batería con los nombres GM de sus teclas (`MidiFiles.Import`).
- **Instrumento o efecto** (`VstPluginInfo.IsInstrument`): la búsqueda (`xtvsthost --scan`) crea cada plugin y le
  pregunta sus entradas: de audio principales (un sidechain no cuenta) y de notas. Toma notas y no audio → instrumento;
  toma audio y no notas → efecto, diga lo que diga su categoría; las dos cosas (un afinador por MIDI, un synth con
  sidechain) o ninguna → su categoría ("Instrument", o sin "Fx" y con notas). Un `plugins.json` de antes
  (`PluginCatalog.CurrentFormat`) se vuelve a abrir una vez en la próxima búsqueda, que la primera lista de
  instrumentos lanza sola (`EnsurePlugins`). La lista sale de `PluginCatalog` (`plugins.json`): la primera
  vez hay que buscarlos ("Buscar plugins VST3…", unos segundos, con toast). Elegir uno es un paso de deshacer; el plugin
  carga en segundo plano (su proceso, `xtvsthost.exe`) y mientras tanto la pista suena con el tono de prueba. Lo que el
  usuario cambia en la ventana del plugin se guarda en el proyecto (su estado, en base64) 1,5 s después de tocarla y al
  cerrarla, fuera del deshacer. Si no abre (archivo que falta, error) o se cierra de golpe, un toast lo
  dice (con "Abrirlo de nuevo") y la pista vuelve al tono de prueba; no se reintenta solo. Con la transporte parada el
  instrumento sigue corriendo: las teclas del piano roll suenan a través de él y su cadena. Con la ventana de un plugin
  (instrumento o efecto) en primer plano, las teclas son de XTreme o del plugin según **Teclas al plugin**, un botón de
  la barra de la ventana con el ícono de teclado (acento prendido; pedido del usuario, 2026-11-04: uno solo, sin modo
  automático). Vale para todas las ventanas a la vez y se guarda (`keysToPlugin`, `key-route plugin|app`, `pluginKeys`
  en `config.json`; apagado por defecto, `MainWindow.PluginKeys.cs`). Apagado, el host le saca todas las teclas al plugin
  y se las pasa a `OnKey` (`app-key`), salvo los modificadores y las del sistema (Alt+Tab, Alt+F4, Windows). Si la
  interfaz del plugin corre en otro proceso (WebView2, CEF) o en un hilo propio, esas teclas no pasan por el host: las
  toma un hook de teclado (`ForeignKeys`, en `xtvsthost`, solo con la ventana abierta y al frente). El teclado virtual
  toca también con la ventana de un instrumento o de un efecto adelante (`UpdatePluginFront`: sus procesos cuentan como
  de la app en `RawKeys.AlsoFront`), en la pista de siempre. Prendido, todas las teclas son del plugin mientras su
  ventana tiene el foco y el teclado virtual no toca.
- **Efecto VST3** (`MainWindow.VstFx.cs`, `InsertSlot.VstKind`, `VstSlotSettings`): "+ Efecto" (de una pista o un bus)
  tiene dos entradas que se despliegan al pasar el mouse (pedido del usuario: todo junto era una lista larguísima,
  `AddSlotGroup`): **Nativos** (los propios) y **VST3** (los efectos encontrados, cuántos son, y por marca desde 12, cada
  marca otro submenú); abajo "Buscar plugins de nuevo" y "Administrar plugins…". La primera vez busca solo (`EnsurePlugins`). El slot es uno más
  de la cadena (se mueve, duplica, pliega y apaga igual): su panel (`VstPanel` en el mixer, `VstSection` en la pestaña
  Canal) tiene el ícono de enchufe (`FxIcon.Vst`), el nombre del plugin, "Abrir" (su ventana) y cómo va: andando y
  cuántos ms llega tarde (lo compensa la mezcla), abriendo, o por qué no abrió. Mientras abre, si no abre o si se cierra
  de golpe, el sonido pasa sin él (toast con "Abrirlo de nuevo"); apagado también pasa sin él, igual de atrasado, así
  prenderlo y apagarlo no salta. Lo que se cambia en su ventana se guarda en el slot (`PluginRef.State`) como el de un
  instrumento: 1,5 s después de tocarla y al cerrarla, fuera del deshacer.
- **Miniaturas de plugins** (`PluginThumbs`, `Controls/PluginThumbView.cs`, `MainWindow.PluginThumbs.cs`, decisión del
  usuario, como hacen otros DAW): cada máquina hace las suyas en `%APPDATA%\XTreme\plugin-thumbs`. La primera vez que se
  abre la ventana de un plugin, o si se actualizó, el host espera a que se quede quieta y guarda el plugin sin la barra
  (`snapshot`); una ventana casi de un solo color no cuenta. El botón Miniatura de la barra la rehace con lo que se ve
  (`snapshot-now`). Un VST3 que trae su PNG (`Resources\Snapshots`) lo usa. Se ve chica en las filas de Configuración y
  grande en un tooltip, ahí y en los menús de plugins. No se abre cada plugin para sacarlas: algunos solo se dibujan en
  pantalla y otros muestran su login.
- **Configuración → Plugins** (`Dialogs/SettingsDialog.Plugins.cs`, pedido del usuario): las carpetas donde se buscan
  (las dos de Windows y las que suma el usuario con +, "vst.folders" en `config.json`), "Buscar nuevos" (solo los
  nuevos o actualizados) y "Revisar todos de nuevo" con el avance ("Abriendo X… (3 de 20)") y Cancelar (para entre un
  archivo y otro, lo encontrado queda); nunca mientras se graba. La lista con filtro (Todos / Instrumentos / Efectos) y
  búsqueda por nombre, marca o categoría: destildar uno lo saca de los menús ("vst.hidden"; la pista que ya lo usa lo conserva).
  **Mostrar en los menús** (pedido del usuario): Por marca / Por categoría / Lista ("vst.grouping",
  `PluginCatalog.Grouping`), para los menús de instrumentos, "Pista MIDI con instrumento" y "+ Efecto"
  (`AddPluginItems`; si todos caen en un solo grupo, van sin submenú). La lista de la página se agrupa igual. La
  categoría de cada plugin sale sola (`PluginCatalog.AutoCategory`: su sub-categoría de VST3 o, sin una, palabras de su
  nombre) y cada fila tiene un chip para cambiarla por otra o una nueva ("vst.categories", por `Key`; "Automática" la
  devuelve). Las automáticas se traducen (`vst.cat.<palabra>`), las propias quedan como se escribieron. Los
  que no abrieron, con el motivo y "Probar de nuevo" (`PluginCatalog.Rescan`). La lista y la búsqueda en curso son una
  sola para la ventana (`PluginScan`): el menú de la pista, el inspector MIDI y esta página se actualizan con
  `PluginScan.Changed`. El menú del instrumento lleva "Administrar plugins…" y el toast de la búsqueda, "Administrar".
- **Sonido**: sin instrumento, un tono de prueba (`NoteSynth`, sin estado: igual en la reproducción, la
  exportación y xtlab). Las notas que suenan al tocar el teclado del piano roll o escribir entran antes de la cadena
  de la pista (`LiveTrack.Preview`, `NotePreview`), como las de un instrumento: se oyen con sus slots, fader y envíos, y un mute las calla.
- **Teclado MIDI** (`MainWindow.MidiInput.cs`, etapa 4, decidido con el usuario): suena en **una sola pista**, la
  **MIDI elegida** (como Logic y Studio One, sin armar nada) o, sin ninguna elegida, la primera **armada**; solo
  grabando suena en todas las armadas, y únicamente en ellas (pedido del usuario: nunca dos instrumentos sin querer). Con su instrumento (o el tono de prueba, que suena
  igual que la nota escrita) y su cadena, parado o reproduciendo, también durante la cuenta. El pedal de sustain
  sostiene lo que se suelta (`LiveNotes`; del teclado, al plugin solo le llegan notas). Cada pista elige de dónde toca
  (`Track.MidiInput` / `MidiChannel`): armada, en el chip a la izquierda del ● ("MIDI" = todas, o las primeras letras
  del nombre del aparato), en el chip de la tira del mixer o con clic derecho → "Entrada MIDI: …"; el menú tiene
  "Todas las entradas", cada aparato (enchufado o recordado), el canal y "Configurar las entradas MIDI…". En el
  encabezado el instrumento queda a la izquierda del ● (y del chip, armada); si no entra su nombre, se pliega a su
  flecha, que abre la lista (la ventana del plugin se abre desde el inspector).
- **Teclado virtual** (`MainWindow.VirtualKeys.cs`, `VirtualKeyboard`, `Controls/VirtualKeyboardView.cs`; pedido del
  usuario, para tocar sin aparato): Ctrl+K (o el botón junto a Ver todo, en el transporte) lo prende. Casi todas las
  letras son atajos, por eso es un modo: prendido, la fila de A son las blancas (sigue después de la L: Ñ, { en un
  teclado latino), la de arriba las negras (W E T Y U O P y el +), Z / X la octava, C / V la velocidad y cualquier Shift
  el sustain (con Shift las notas tocan y Z, X, C, V vuelven a ser atajos); los demás atajos y estas letras con Ctrl o
  Alt siguen andando. Estas teclas son fijas y físicas: no salen de `Shortcuts`. Las notas no pasan por la UI: las lee
  `RawKeys` (Raw Input, en su hilo) mientras el modo está prendido y el programa al frente; el `OnKey` de la ventana solo
  frena el atajo y mide cuánto tarda en llegarle (`sesiones.log`, al apagar el modo). Una tira bajo el transporte
  muestra de 1 a 7 octavas (Rango), dónde toca, la octava, la velocidad (guardados en `config.json`) y el Sustain, un botón On/Off con la tecla Shift a la vista: Shift lo prende mientras se aprieta y un clic lo deja prendido, y se
  toca con clic y arrastrando. La tira muestra las notas que suenan: al transponer, las letras (cada una en su recuadro) se corren a donde caen sin mover la vista, y lo mismo con la octava;
  y un clic toca la nota que está debajo. Clic derecho y arrastrar la desplaza por todo el teclado, la ruedita lo escala de forma continua y suave (de
  1 a 7 octavas, alrededor del mouse). El botón arriba a la derecha (como el de cada panel) lo saca a su propia ventana (`OpenVirtualWindow`, pedido del usuario: el mismo panel, movido, con los ajustes arriba de las teclas; con poca altura, solo las teclas), que va encima de la principal, recuerda lugar y tamaño (`virtualFloat`, `virtualWindow` en `config.json`) y al cerrarse devuelve el panel bajo el transporte. Transponer,
  Velocidad y Rango son barras (`ValueBar`): clic o arrastrar las pone donde está el mouse, la ruedita las corre y el
  clic derecho las vuelve a su valor (0, 100 y 2). Con una ventana de instrumento
  abierta, la ventana crece y lleva el mismo panel abajo, integrado (`EmbedPanel`: una ventana nuestra hija de la del
  host, solo las teclas si es angosta); el host avisa si está abierta, y mientras esté abierta (aunque el foco esté en la
  ventana principal) las teclas tocan en la pista de ese plugin (`keyTarget`, `Hears`), no en la elegida; con varias,
  en la última que tuvo el foco. La ventana del host usa los colores y el ícono de la app, con una barra arriba:
  la pista (con su color) y el plugin a la izquierda; Teclado, M, S y Detener notas a la derecha, solo con íconos si no
  entran (`ShowPluginBars`, `PluginBarClicked`; las de efectos, sin barra). Suena y graba como un teclado real, en las mismas pistas, sin importar la entrada ni el
  canal que eligieron (`MidiInputs.VirtualPort`). Al apagarlo o al perder la ventana el foco, las notas se sueltan.
- **Grabar notas**: una pista MIDI armada graba lo que toca su entrada junto con las tomas de audio y la cámara (o
  sola, sin tomas). Al parar, las notas son un clip nuevo en esa pista, del compás donde empezó la toma al compás
  después del final (las de la cuenta entran desde ahí), arriba de lo que había (no reemplaza: los dos suenan) y en el
  mismo paso de deshacer que las tomas (`MidiEditing.AddRecorded`). Mientras se graba, las notas van a disco (`MidiTakeWriter`) y una toma cortada vuelve como clip al abrir el proyecto. Cada nota va donde se oía la canción cuando se
  tocó: el bloque menos la latencia de salida (`midiLatencyFrames`) y menos lo que esperó antes del bloque. Descartar o
  cancelar la cuenta no deja nada. Si no llegó ninguna nota, un toast lo dice y manda a mirar la lucecita.
- **Escala** (pedido del usuario; `MusicScale`, `MainWindow.Scales.cs`): una sola, de la canción (`ProjectSettings.Scale` y `ScaleRoot`, con deshacer; `ApplyScale`, en cada `RefreshPiano`). El chip de la escala del piano roll (en la barra de abajo, al lado de Acordes, en acento si hay escala; abre hacia arriba) y el del teclado virtual abren el mismo selector, compacto como un menú (pedido del usuario): las 12 tónicas en una fila, las escalas en filas simples en tres columnas (mayores y menores, y modos y otras en las dos últimas), Sin escala como chip arriba, al lado de la tónica (pedido del usuario, 2026-11-04: al pie se perdía), y al pie "Llevar las notas a la escala". Queda abierto para probar; desde el panel dentro de la ventana de un plugin abre hacia arriba y un clic en cualquier otro lado lo cierra (`DismissPluginPicker`: ese clic es de otro proceso y no le llega). Piano roll: solo las filas de la escala (pedido del usuario: el resto del teclado no se ve; `PianoRollView.SyncRows`), más las de notas que ya estaban fuera, que se ven con la tecla oscurecida; tónica en acento; lo que escribís o movés cae en la escala y mover va por grados (un acorde sigue en tono), ↑ ↓ también; Alt, libre. Una nota fuera de la escala se dibuja hueca. Teclado virtual: las filas de Z, A y Q son tres octavas de 7 notas desde la tónica (A en la octava elegida; una escala de 8 notas suma una tecla: `,` K I), con sus letras sobre esas notas; Z, X, C, V pasan a ser notas, así que O / P mueven la octava y L / Ñ la velocidad. La primera vez la tira muestra al menos 3 octavas. "Acordes", al lado, hace que cada tecla toque la tríada de su nota.
- **Acordes** (pedido del usuario; `Chords`): "Acordes", en la barra de abajo del piano roll junto a Dibujar / Pintar / Cortar (`H`, atajo `chordTool`; su tipo ▾ aparece al lado mientras está prendido) hace que un clic escriba un acorde, con su vista previa y su nombre bajo el puntero. "De la escala" arma el de cada nota (con escala; sin ella, mayor) y hay formas fijas (`pianoChordKind`). El tipo se elige en una tarjeta (`ShowChordKindCard`, pedido del usuario, 2026-11-04: el menú largo no se entendía): De la escala (Tríada / Con 7ª, con qué escala sigue), Acordes fijos, Escalas y Batería, cada grupo en su grilla, y al pie qué escribe un clic. Todo grupo de notas elegidas que arranca junto y forma un acorde conocido muestra encima una barra (`DrawChordBar`) con su nombre y sus inversiones, cada una con un dibujito de sus notas, también con las teclas 1 a 4 y con la rueda de la altura (Alt) sobre el acorde o sobre el que se va a escribir (`TurnChord`): el acorde recién escrito queda elegido, así que aparece solo, y el siguiente sale en la misma inversión. Cada inversión queda a la octava más cerca de la fundamental (`Chords.Voice`, pedido del usuario: antes el acorde subía), y `Chords.RootOf` recupera esa fundamental al releerlo. La barra del piano roll agrupa sus botones con nombre (`PianoGroup`: Editar, Armonía, Grilla, Ver, con "Atajos").
- **Cuantizar** (pedido del usuario: configurable): a mano con Q (atajo `quantize`) en el piano roll (las elegidas, o
  todas sin ninguna elegida, `PianoRollView.Quantize`), en el timeline sobre los clips MIDI elegidos, o con clic derecho
  en un clip → "Cuantizar a paso"; al grabar, si Configuración → MIDI → "Cuantizar al grabar" está prendido. Siempre
  al **imán del piano roll** y sobre la grilla de la canción, no la del clip; mueve el comienzo y deja el largo. Sin
  imán o en Auto no cuantiza (el resultado cambiaría con el zoom; como FL) y lo dice.
- **Herramientas** (pedido del usuario, todas las de FL Studio; `MidiTools`, `MainWindow.PianoTools.cs`, `Controls/PianoRollView.Tools.cs`): una sola tabla (`MidiTools.All`) da la tarjeta "Herramientas", el panel, los atajos (`Shortcuts.PianoKeys`, ids `piano.*`, solo con el foco en el piano roll) y `xtlab midi herramienta`: una herramienta nueva es una fila y una función. La tarjeta muestra todo a la vista en cinco columnas (Notas, Tiempo, Niveles, Edición, Elegir, Lo elegido, Archivos, En vivo). Sin ajustes, corre al toque; con ajustes abre un panel a la derecha que no tapa las notas: la herramienta corre desde las notas de cuando se abrió cada vez que se mueve un valor (`PreviewTool`), y mientras tanto el resultado va al motor y se oye (`PreviewNotes`, fuera del deshacer). Enter o un clic en las notas aplica (un paso de deshacer: `RestoreBeforeCommit` devuelve el original antes de `EditTimeline`), Esc cancela. Actúa sobre las notas elegidas o, sin ninguna, sobre todo el clip. Los valores se recuerdan por herramienta (`pianoTool.<id>`). Lo que es al azar sale de una semilla: "Otra variante" tira de nuevo.
  **Escribir / Pintar / Cortar** (`PianoTool`): pintar deja una nota por paso del imán (Shift: en la misma fila); cortar parte lo que cruza la línea. Ctrl+Alt+arrastrar elige con un lazo. **Notas**: `MidiNote` lleva `Muted`, `Group` (se eligen juntas), `Color` (15 grupos), `Slide`, `Porta`, `Pan`, `Release`, `ModX`, `ModY`; doble clic abre su tarjeta, la herramienta `note`, que solo cambia lo que se movió. **Vista**: otras pistas MIDI en gris y una forma de onda de fondo (de la sesión, no se guardan); clic derecho en las teclas abre un menú: la notación de las notas (Do, Re, Mi o C, D, E, en toda la app: `NoteNames.Set`, "noteNames" en `config.json`; pedido del usuario) y renombrar esa tecla (`Track.KeyNames`). Al lado, el acorde que se toca o se eligió. **Sellos** (`MidiStamps`): corridas de escala y figuras de batería en la tarjeta del tipo de acorde. **Slide** usa pitch bend de ±2 semitonos: es por canal, así que un acorde se dobla junto, y el sampler todavía no lo oye. **Arpegiador en vivo** (`ArpSettings`, `LiveNotes.RunArp`): arpegia lo que toca el teclado, en la grilla de la canción, y se graba como suena; lo escrito no pasa por él. **Teclado MIDI a la escala** (selector de escala, `midiScaleSnap`, `PlaybackEngine.KeySnap`). **Archivos**: .mid (`MidiFiles`) y partitura PDF (`ScoreSheet`).
- **Configuración → MIDI** (`Dialogs/SettingsDialog.Midi.cs`, `MidiSetup`): una tarjeta por aparato que Windows ve
  (`MidiInputs.Devices`, por nombre; dos iguales, "nombre #2") con una lucecita que titila con lo que llega y lo
  último en palabras ("C4 · 96 · canal 1", "pedal abajo", `MidiSetup.Describe`), su nombre propio (el del aparato de
  marca de agua), prendido (apagado lo suelta para otro programa), canal (Todos o 1–16), velocidad (Suave / Normal /
  Dura / Fija, con su valor) y para qué se usa (Notas · Perillas · Transporte, casillas; las dos últimas todavía no
  hacen nada, quedan para MIDI learn y la pedalera). Los recordados que no están enchufados salen en gris con "Olvidar".
  "Buscar de nuevo" reabre; además cada 2 s se mira cuántas entradas hay y si cambió se reabren solas. Abajo,
  "Cuantizar al grabar". Todo se guarda en `config.json` "midi" (`MidiSettings`), de la máquina.

## Video | Solo audio (`MainWindow.Mode.cs`, `ProjectSettings.Mode`)

- El interruptor de la barra superior (primero en la fila de herramientas, debajo del nombre; estilo propio
  `Border.mode` / `ToggleButton.mode` en `MainWindow.axaml`: riel hundido con el modo elegido encendido en acento,
  fondo, borde, texto e ícono, y el otro tenue) y del "Nuevo proyecto" del gestor.
- En solo audio el timeline (o el mixer) se mueve al lugar de la preview (`AudioArea`) y la fila de abajo se cierra;
  la cámara se suelta (`LiveCamera.SetPaused`), los decodificadores se cierran, se ocultan Color, Encuadre, la
  latencia de cámara, los fades de video y "En el video" (`videoOnly`), y "Exportar MP4" pasa a "Exportar audio"
  (el mismo `ExportDialog` con WAV / MP3, `AudioExporter`).
- Es un paso de deshacer y no borra nada. Todo lo nuevo que solo sirva con imagen va a `videoOnly` o mira `AudioOnly`.

## Exportar

- **Tramo** (MP4 y audio): fila arriba de Destino (`RangePanel`), visible solo si el proyecto tiene un loop A–B:
  "Todo el tema" (viene elegido, no suma pasos) o "Loop A–B" (`ExportOptions.Range`, `ExportJobSpec.Range`). La
  mezcla se renderiza desde 0 igual (lo que suena de antes de A, como una reverb, sigue sonando), las tomas y notas se
  apagan en B (20 ms) y la cola de efectos suena después; el resultado arranca en A con un fundido de 10 ms
  (`ExportRange`, `MixMaster.Render`). El video lleva solo las tomas que se ven en el tramo, recortadas y corridas a
  0:00 (`ExportRange.Takes`); un panel sin toma en el tramo no aparece. El subtítulo y el tamaño estimado usan el largo
  del tramo. No se recuerda: cada exportación arranca en "Todo el tema".
- **Stems**: la casilla del diálogo de exportar (MP4 o audio) deja además "<nombre> - stems" con un WAV float por
  pista que suena, cada una sola con sus envíos y sin el limitador (`AudioExporter.ExportStemsAsync`). Duran lo mismo
  que la exportación, cola incluida, y no llevan la ganancia de loudness.
- **Destino** (solo MP4): fila segmentada arriba de Calidad con Proyecto · Reels · TikTok · YouTube · IG 1:1 · IG 4:5
  (`ExportTargets`). "Proyecto" es el de siempre (el cuadro del proyecto, la mezcla como está) y viene elegido, así que
  no suma pasos; los demás cambian el cuadro solo para ese video (9:16, 16:9, 1:1, 4:5 = 1080×1350). Se recuerda el
  último (`ExportSettings.Target`). El volumen no se elige acá: es el **Destino del master** (ver Mastering), y una
  línea abajo de Calidad (`LoudnessText`, también en "Exportar audio") dice a cuántos LUFS sale.
- **Avanzado** (solo MP4): un link plegado abajo de Calidad (`AdvancedPanel`); plegado dice lo que no está por
  defecto ("Avanzado · 4K · 20 Mbps"). Adentro, Resolución 720p · 1080p · 1440p · 4K (el lado corto del cuadro,
  `Mp4Exporter.ExportSize(aspect, resolución)`; el borde entre paneles crece con ella) y Bitrate del video: "Automático"
  (el de la calidad) o un slider de 1 a 100 Mbps; mover el slider sale de Automático. Con bitrate a mano, x264 pasa de
  CRF a ese bitrate y la placa lo usa en vez del de la calidad; la calidad sigue eligiendo la velocidad del codificador.
  Se recuerda por proyecto (`ExportSettings.Resolution`, `BitrateMbps`) y la estimación del peso lo usa.
- **Avanzado de Exportar audio** (pedido del usuario, #208): el mismo link, con otro cuerpo (`AudioAdvancedBody`).
  Plegado y sin tocar es el modo fácil de siempre (`AudioExportSettings.Defaults`: WAV 24 bit o MP3 320, 48 kHz,
  estéreo, cola automática, sin normalizar, un stem por pista). Adentro: Formato (WAV, MP3, FLAC, AIFF, OGG; los de
  FFmpeg, deshabilitados sin él), Resolución 16 · 24 · 32 float (32 solo WAV) con Dither TPDF en 16, bitrate 128–320 o
  VBR (MP3/OGG), Frecuencia 44,1 · 48 · 96 kHz (96 no en MP3/OGG), Estéreo / Mono, Cola Automática (hasta
  `MixMaster.MaxTailSec`) o Fija (0–30 s, silencio incluido), Normalizar al pico (−6 a 0 dBTP, después del master) y,
  con Stems tildado, qué pistas llevan stem y qué carpetas o buses suman uno. Se guarda por proyecto
  (`ExportSettings.AudioBits`, `AudioRate`, `AudioDither`, `AudioKbps`, `AudioTailSec`, `AudioNormalizeDbtp`,
  `AudioMono`, `StemsSkip`, `StemNodes`); `AudioExporter` lo lleva a cabo y `xtlab comprobar-dsp` lo comprueba.
- **Masterizado y colas** (`Dsp/MixMaster.cs`): la mezcla se renderiza con el mismo `MixRenderer` sin limitador y hasta
  8 s después del último clip; corta donde reverb y delay bajan de −66 dBFS (o funde el último segundo si siguen
  sonando en el límite). Después va el `MasterLimiter` con la ganancia que lleva a los LUFS del Destino del master (arrancando de la que ya midió la ventana), midiendo con
  `LoudnessMeter.Measure` y corrigiendo hasta 4 pasadas (solo el limitador). En el MP4, durante la cola el último
  cuadro queda congelado y se funde al fondo (`tpad` + `fade` en `Mp4Exporter.BuildFfmpegCommand`).
- **Velocidad**: `MixRenderer.RenderTo` mezcla de a tramos de 64 bloques en tres etapas con su hilo cada una (pistas
  en paralelo → reverb y delay → master), con el mismo resultado bit a bit que de a un bloque. En el MP4, cada toma
  tiene su propio grafo de filtros en FFmpeg (loopback decoders, `-dec`, FFmpeg 7+), así las tomas se filtran a la vez.
  El video lo codifica la placa de video si FFmpeg encuentra una (NVENC, AMF o Quick Sync, `VideoEncoders`, con el
  bitrate de la calidad en vez del CRF de x264) o el procesador; se elige en Configuración → Guardado y exportación
  (`config.json` "export.encoder", de la máquina y no del proyecto). Si la placa o los grafos fallan, la exportación
  se repite sola con el procesador y un solo grafo, como antes.
- **Transiciones**: el sonido lo funde `MixRenderer` (`TrackDspChain.AudioJoins` / `SampleClips`, igual que la
  reproducción). La imagen, en `Mp4Exporter.BuildFfmpegCommand`, los clips de un mismo panel con una transición
  extienden su tramo (el `trim` y el `enable` del `overlay`), con `tpad` que congela el borde cuando no hay video
  escondido; el fundido cruzado es un `fade` con alfa (`yuva420p`) en el que queda arriba de los dos y pasar por negro,
  dos `fade` al color del fondo. Mismo resultado que la preview.
- **Al terminar** se ven los LUFS integrados de la mezcla y los segundos de cola (en el pie, o en la línea del audio);
  quedan en `ExportRecord.Lufs`, así que también se ven al abrir la exportación desde Historial.

## Componentes opcionales

Al abrir, `ComponentsDialog` avisa lo que falta y se puede instalar; Omitir deja de preguntar (`componentsSkipped` en `config.json`) y las funciones quedan deshabilitadas. Configuración → Componentes muestra todos con su estado; Acerca de y la página de cámara, lo que falta. Sin FFmpeg: exportar video avisa y MP3 queda deshabilitado. Sin modelos, el menú del clip ofrece instalarlos; en Separar en stems, una opción sin modelos ofrece bajar solo esos.
