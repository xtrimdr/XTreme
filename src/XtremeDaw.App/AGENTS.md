# Ventana (XtremeDaw.App) — reglas de diseño

Se suma a la guía de la raíz ([AGENTS.md](../../AGENTS.md)). Cómo es y dónde vive cada parte de la ventana (barra de
título, transporte, inspector, timeline, mixer, carpetas, grabación…) está en [docs/interfaz.md](../../docs/interfaz.md):
leé el apartado de lo que vas a tocar, y si cambiás cómo funciona, actualizalo. Los controles que dibujan a mano tienen
sus reglas en [Controls/AGENTS.md](Controls/AGENTS.md). El motor en vivo (`Audio/`: `PlaybackEngine`, `LiveTrack`)
sigue la [guía del motor](../XtremeDaw.Audio/AGENTS.md); el modelo que edita la ventana, la de
[Projects](../XtremeDaw.Projects/AGENTS.md).

## Principios

- **Oscura, sobria, de estudio.** Fondo casi negro, superficies apenas más claras, un solo color de acento. Lo que
  resalta es el contenido (el video y las ondas), no los controles. Excepción (decisión del usuario, #187): cada efecto
  nativo lleva el color de su familia (`Palette.FamilyColor`) en su panel, sus perillas y su gráfico. El tema es siempre oscuro (`RequestedThemeVariant="Dark"`).
- **Coherente consigo misma.** Ante la duda, mirá cómo lo hace el resto de la ventana.
- **Un DAW de trabajo** (decisión del usuario, 2026-11-03: ya no se piensa para usarse con la guitarra colgada):
  atajos para todo, edición precisa con el mouse; grabar y reproducir, a una tecla.
- **Siempre se ve el estado**: qué está sonando, si hay audio, si un archivo falta, si se guardó.
- Lo que no está hecho se muestra deshabilitado o con un aviso. Nunca un botón que no hace nada y no dice nada.
- **Un efecto, un panel** (pedido del usuario): sus controles salen de `FxBody` en todo lugar; lo que es de un solo
  lugar (medidores de reducción, gráfico del multibanda) va alrededor, no adentro.
- No hay MVVM ni bindings: code-behind con controles con nombre (`x:Name`). Las ediciones del inspector pasan por
  `Edit(t => ...)` (marca sucio, refresca textos, redibuja el timeline), o `EditPicked` en lo común a varias pistas
  elegidas (`MainWindow.InspectorGroup.cs`); el flag `syncing` evita que llenar el inspector dispare ediciones.

## Textos (`Loc`)

Los textos salen de `Loc` (`Loc.T("clave")`, o `{loc:T clave}` en XAML) y viven en `Assets/Lang/<código>.json`;
**es.json es la referencia** (lo que falta en otro idioma cae al español). Texto nuevo → clave nueva en todos los
idiomas. Los del Banco de pruebas (`test.*`) van aparte, en `Assets/Lang/Bench/<código>.json`: `Loc` lee los dos
archivos de cada idioma. Los nombres que vienen del motor en español (estilos del compresor, presets de EQ, reverb y delay,
divisiones) se traducen al mostrarlos con `Loc.Or(clave, texto del motor)`. Los títulos de sección del inspector,
de Color y de Historial son ids estables en español (`SectionTitle` y `bar.section.*` los traducen al mostrarlos),
porque con ellos se recuerda qué secciones están plegadas. El idioma se elige en Configuración, se guarda en
`config.json` (`language`) y se aplica al reiniciar.

## Colores (tokens)

Definidos **dos veces y tienen que coincidir**: `App.axaml` (XAML) y `Controls/Palette.cs` (lo que se dibuja). El
selector de tema (`UiTheme.cs`: 17 acentos, ordenados por tono, 16 fondos: nueve oscuros, seis grises medios (#272: el texto sigue claro; fondos claros de verdad no, la interfaz es oscura) y Tron Legacy, más Brillo, Contraste, Saturación y Temperatura sobre ellos, #317; los mismos controles, `ThemeEditor`, en el popover y en Configuración → Apariencia) cambia en vivo los pinceles de superficie y acento, así
que usá siempre el pincel (`Palette.Accent`, `{StaticResource Accent}`), nunca un color escrito a mano, y leé
`Palette.AccentColor` y compañía al dibujar, sin guardarlo. Variantes del acento en XAML: `AccentFg` (texto sobre
acento), `AccentHover`, `AccentTint`, `Accent25/40/50/70`. Valores del tema por defecto:

| Token | Valor | Uso |
|---|---|---|
| `App` | `#0b0b0d` | fondo de la ventana, del timeline, de la preview; texto sobre acento |
| `Panel` | `#131316` | barras (título, transporte), inspector, encabezados de pista |
| `Raised` | `#1c1c20` | secciones, chips, botones, pista seleccionada |
| `Hover` | `#26262b` | hover; fondo de toggles apagados y badges |
| `Line` | `#27272c` | todos los bordes y separadores (1 px) |
| `Fg` | `#ececef` | texto principal |
| `Dim` | `#a1a1aa` | texto secundario, valores, íconos |
| `Faint` | `#6b6b74` | etiquetas en mayúscula, ayudas, deshabilitado |
| `Accent` | `#22d3ee` | **el** color: activo, seleccionado, play, playhead, ondas, medidores (hover `#4fdcf1`) |
| `AccentSoft` | `#2422d3ee` | fondo de chips activados |
| `Rec` | `#ef4444` | grabar y clip/saturación de nivel. Nada más |
| `RecSoft` | `#24ef4444` | fondo suave asociado a grabación |
| `Composition` | `#09090b` | fondo del cuadro del video |
| `Mute` | `#f59e0b` | mute activo, zona de −6 a 0 dB en medidores, lo que se pasa de algo (un largo de red social, una nota desafinada). Con un acento cálido (Fuego a Oro) pasa a azul `#60a5fa` (`UiTheme.StateColors`, decisión del usuario, 2026-11-03) |
| gris offline | `#71717a` | clips cuyo archivo falta |
| `InTune` | `#22c55e` | "afinado" en el afinador (el verde de los medidores del mixer, `Palette.MeterGreen`); con un acento verde (Lima a Esmeralda), cyan `#22d3ee` |

No inventes colores para estados nuevos sin necesidad; el acento no es decoración; el rojo es **grabación o peligro
de clip**, nunca "error genérico". Las transparencias salen de `Palette.Alpha(color, a)` (relleno de clip 0,10 / 0,16
seleccionado, borde 0,45 / 0,9). Los colores de pista son datos del usuario, no tokens.

## Tipografía e íconos

- UI: **Segoe UI Variable Text** (fallback Segoe UI). Números y tiempos: **Cascadia Mono** (fallback Consolas).
- La fuente de la UI y el tamaño general se eligen en Configuración → Apariencia (`UiFonts`, "uiFont" y "uiScale";
  pedido del usuario, 2026-11-04). Por eso `Ui` se lee como `DynamicResource` y en el código por `Palette.Ui`/`UiBold`,
  sin guardarlo. El tamaño se elige en px del texto común (12 px = 100 %, de 9 a 18 de a 0,5; Ctrl + / Ctrl − de a 1 px, Ctrl 0 al 100 %; pedido del usuario, #133) y es una escala de toda la interfaz (un `LayoutTransformControl` que `UiFonts` pone en la
  plantilla de cada ventana y popup), no un cambio de `FontSize`: la escala de abajo sigue en valores a 100 %. La fuente
  de los números también se elige ("uiNumberFont", pedido del usuario, 2026-11-04: Cascadia Mono por defecto, o una
  de las de `Assets/Fonts`, OFL: DSEG14 u Orbitron; las de 7 segmentos se sacaron, #132): `Mono` se lee como `DynamicResource` y en el código `Palette.Mono`, sin
  guardarlo; nombres de teclas y texto tipo código van en `Palette.Code` (siempre Cascadia Mono). Los íconos no cambian.
  Cada tarjeta de Apariencia tiene su **Restablecer** (al valor de fábrica, `Default` de cada `*Looks`). Las tarjetas van en
  grupos plegables por zona (General, Controles, Timeline, Inspector, Mixer, Separadores; `SettingsDialog.ViewGroup`, pedido del
  usuario, #293), que se recuerdan abiertos o plegados ("lookGroupsOpen"; la primera vez, todos plegados). Una tarjeta nueva va en su grupo.
  **Todo valor numérico va en mono** (`Classes="dim mono"`), alineado a la derecha de su etiqueta.
- Íconos: **Segoe Fluent Icons** (fallback Segoe MDL2 Assets), `TextBlock` con clase `icon` y el glifo en `Text`. Sin
  paquetes de íconos ni imágenes (las miniaturas de plugins son contenido, como las de video). Excepciones: los efectos (saturación, compresor, EQ, reverb, delay, transponer) siempre con `FxIcon`
  (trazos de lucide), en todo lugar que muestre un efecto; los instrumentos de las pistas con `TrackIcons` (rellenos:
  Fluent Emoji High Contrast y lo que le falta de otros sets libres; licencias en `Assets/Icons/LICENSES.txt`,
  generados con `tools/make-track-icons.py`).
- Escala (no inventes tamaños intermedios):

| Tamaño | Uso |
|---|---|
| 10.5 SemiBold, mayúsculas, espaciado 0.8, `Faint` | etiquetas de sección (`TextBlock.label`) |
| 11 | info secundaria chica, textos dentro del timeline |
| 12 `Dim` | texto secundario (`TextBlock.dim`), chips |
| 12.5 SemiBold | nombre de pista, botón de acento |
| 13.5 SemiBold | nombre de la app en la barra de título |
| 14 / 19 mono | BPM / reloj del transporte |
| 16 SemiBold | título del inspector (nombre del canal), título de un diálogo (`TextBlock.dialog-title`) |

- Escala compacta, solo en las tiras del mixer y en el transporte (decisión del usuario, 2026-11-03: no crecen):
  **8** escala de los medidores e íconos mínimos, **9** etiquetas y valores de la tira, **10** etiquetas del
  transporte (`metro-label`) y valores chicos de la tira, **13** mono valores del transporte (`metro-value`). El nombre
  de la tira va en 11. Fuera de ahí, la escala de arriba.

## Formato de valores

Respetan la cultura del sistema (en es-AR, con coma). Volumen, ganancia, paneo y envíos salen siempre de
`ValueFormat` (no los armes a mano), y las notas de `NoteNames`:

- Ganancia y volumen, siempre en dB (decisión del usuario, 2026-11-03: también el slider del inspector, con la curva
  del fader): `+3,5 dB`; volumen cero: `-∞ dB`. Compacto, sin la unidad, en el encabezado de pista (`+3,5`).
- Paneo: `Centro`, `30 L`, `30 R` en el inspector; `C`, `L30`, `R30` compacto en el encabezado de pista y el mixer.
- Envíos: `35%`, con los dB en el tooltip; la tira del mixer marca PRE.
- Notas en el sistema del idioma (decisión del usuario, 2026-11-03): `Sol3`, `Do#4` en español; `G3`, `C#4` en inglés.
  El usuario puede elegir el otro con clic derecho en las teclas del piano roll (`NoteNames.Set`, "noteNames"; pedido
  del usuario, 2026-11-04): vale para toda la app.
  Los acordes guardados en letras ("Bbm7") se muestran traducidos solo si son un acorde (`NoteNames.Chord`).
- Tiempo: `mm:ss.fff` en el reloj; compás.tiempo (`2.2`) al lado.
- Porcentajes con palabra: `50% · Media`. Separador de datos en una línea: ` · `.
- Fechas relativas: "recién", "hace 5 min", "hoy 14:30", "ayer 09:10", `dd/MM HH:mm`.

## Formas y espaciado

- Radios: **12** diálogos, **10** paneles de la ventana (tarjetas `Border.card`, 2 px de margen, y otros 2 alrededor de su zona para que toda separación, entre tarjetas o contra los bordes y las barras, sea de 4, la más chica que había: pedido del usuario, #193; los paneles neumórficos llevan 5 y 5, separación de 10, para que respiren sus sombras: #191), **8** secciones, **6** botones/chips/cajas, **5** clips y badges, **4** toggles M/S y etiquetas, **2** barras de medidor.
- Bordes de 1 px en `Line`. Sin sombras, sin gradientes, sin animaciones decorativas. Única sombra: el **glow del
  hover** de todo botón (`HoverGlow`, un `DropShadowEffect` del acento que recolorea `UiTheme`; `RecGlow` en los
  rojos), puesto al final de los estilos de botón en `App.axaml` para que ninguna clase lo pierda. **El texto nunca
  brilla**: los botones transparentes con texto (filas enteras `rowmain` / `tplmain`, `link`), los `CheckBox` y las
  muestras de color van sin glow, porque se dibujaría alrededor de las letras. Excepción pedida por el usuario: el
  botón de guardar, mientras hay algo para guardar, lleva clase `pending` (brilla y late suave con la opacidad;
  `failed` en rojo si no se pudo guardar). En las filas el hover lo marca la fila
  (fondo `Raised` y borde `Line`); en el `CheckBox` brilla solo la cajita. Va como `Effect`
  del botón: un `BoxShadow` en el presentador queda cortado. Otra excepción pedida por el usuario: la tapa del fader
  (`Fader`) imita la de una consola, con sombreado, ranuras y una sombra abajo, para que se vea y se agarre fácil (es el look por defecto: tapa y riel tienen diez a elegir en Configuración → Apariencia, `FaderLooks`, "faderLook"; pedido del usuario, 2026-11-04); las perillas (`Knob.Draw`) tienen doce estilos a elegir en Configuración → Apariencia (`KnobLooks`, pedido del usuario, 2026-11-03; Neumórfico por defecto): uno nuevo es un método más ahí, con su nombre `set.look.knob.<id>`. Los de tapa clara (`KnobLooks.LightCap`) llevan oscuro el valor escrito encima. Otra, pedida por el usuario (#191): los paneles de la ventana (inspector, barra del proyecto, timeline, mixer) se eligen en Configuración → Apariencia → Paneles (`PanelLooks`, "panelLook": Plano por defecto, Relieve, Relieve con degradé); los neumórficos llevan dos sombras y van sin borde, con colores sacados del tema (`MainWindow.PanelLook.cs` los repinta al cambiarlo). Lo de adentro de los paneles sigue plano, salvo las perillas y los faders Relieve y Relieve con degradé (`NeuLook`, #191), que toman los colores del panel. Y **Tron Legacy** (#191, `TronLegacy`, en Configuración → Apariencia → General → Tema, bajo "Temas completos"; ya no en el selector de la barra, #317; el usuario sacó los temas con el aspecto de otros DAW, #250): un clic pone el fondo `tron`, el acento `#5ce1ff`, paneles, perillas, faders y medidores Tron (línea del acento con brillo, sin sombras de relieve; los medidores, LED de luz sobre negro), ondas en contorno y Orbitron en los números; cada parte sigue eligiéndose aparte. Los separadores (timeline y mixer) van sin brillo: doble línea fina de su color (el acento si no tiene uno), estilo consola (#289), o el diseño elegido en Apariencia (`SeparatorLooks`, #293). Los botones de color (acento, primario, peligro, grabar,
  `lit`) no cambian de color en hover: el glow es la respuesta. Su fondo y su texto van en el presentador
  (`/template/ ContentPresenter#PART_ContentPresenter`, sin estado en el selector), como los chips ON: puesto solo en
  el `Button`, el texto quieto sale blanco del Fluent.
- Un botón que pinta sus colores desde código (M/S encendidos, armar, C/LIM, muestras de color) lleva la clase
  **`lit`** mientras está encendido: si no, el hover le tapa el color con gris.
- **Scrollbars** (pedido del usuario): de 8 px (`ScrollBarSize`) y sin flechas, siempre de ese tamaño cuando hacen falta, y sin aparecer si el contenido
  entra. El contenido les deja su lugar al costado, no pasa por debajo (estilo global en `App.axaml`). No uses el
  `Padding` de un `ScrollViewer` para separar el contenido: queda fuera de lo que se desplaza. Usá `Margin` en el contenido.
- Padding de sección `12,10`; separación entre bloques 10–12; márgenes del inspector `14,16`. Alturas mínimas: chips 30, transporte 36×36.

## Componentes (clases de `App.axaml`)

Para un control nuevo, primero reutilizá estas. Los controles Fluent (sliders, switches) toman el acento de
`SystemAccentColor*`; no les pongas colores a mano.

| Clase | Qué es |
|---|---|
| `Button.chip` / `ToggleButton.chip` | botón normal de contorno: fondo transparente, borde `Line`, texto `Dim` (`Faint` si es un toggle apagado). Activado (o `.on`): fondo `AccentSoft`, borde acento al 40 %, texto `Accent`. Deshabilitado: opacidad 0,35 |
| `Border.seg` + `ToggleButton.seg` | control segmentado: caja `Raised` y la opción activa como pastilla blanca al 11 %. `.seg.pick` = variante de acento; con `.flat`, filas de texto sin glow (selector de escalas) |
| `Border.metro` + `ToggleButton.cell` + `Border.vsep` | recuadro oscuro del transporte, con celdas valor arriba (`TextBlock.metro-value`) y etiqueta abajo (`TextBlock.metro-label`) |
| `StackPanel.bar-cell` | celda de la barra de transporte (valor arriba, `metro-label` abajo) |
| `Button.tbtn` / `ToggleButton.tbtn` | botones del transporte: planos, 36×36, ícono `Fg`, hover `Hover`; activo (o `.on`), ícono `Accent`. `Button.clock` = el reloj |
| `Button.ghost` | botón de ícono sin fondo (barra de título) |
| `Button.accent` | **la** acción principal; una sola por pantalla ("Exportar MP4" / "Exportar audio") |
| `Border.section` (+ `.off`) | tarjeta del inspector |
| `TextBox.field`, `Button.btn` (`.sm`, `.primary`, `.outline`, `.danger`), `Button.act` (`.danger`, `.close`) | campos y botones de diálogos y listas: botón de fila 28×28, confirmación roja; `.close` es la cruz de un diálogo (32×32). Viven solo en `App.axaml`: un diálogo no los redefine |
| `Button.link` (`.accent`) | enlace de texto: tenue (migas, "Restablecer") o en acento (el que se sigue: "Ver en la ayuda") |
| `Border.dialog`, `TextBlock.dialog-title`, `Scrim` | la tarjeta de un diálogo, su título (16 SemiBold) y el fondo que oscurece la ventana. Anchos: 440 una pregunta, 512 un formulario, 640 una lista o lectura (Configuración, Proyectos y Ayuda, los suyos). Configuración va sin `Scrim`: flota sobre la ventana, que sigue andando, se mueve desde el título y cambia de tamaño desde la esquina (pedido del usuario, #184) |
| `Border.notice` + `TextBlock.notice` / `TextBlock.icon.notice` | **el** aviso dentro de un diálogo (un error, un archivo que ya existe, un permiso): caja `Raised` con el signo de advertencia en acento y el texto en `Fg`. Nunca en rojo. Un error de una línea bajo un campo, solo `TextBlock.notice` |
| `Button.chip.purge` | "Borrar no usados": un chip común (mismo texto que "Desvincular no usados" al lado) que se pone rojo con el hover. Los dos van en un `EvenRow`: lado a lado si entran, si no uno abajo del otro |
| `PowerButton` (`Button.chip.power`) | **el** ON/OFF de un efecto, en todo lugar (inspector, Color, mixer, Mastering, banda del EQ): acento lleno y con el glow prendido (pedido del usuario, 2026-11-03, con o sin el mouse encima), contorno tenue apagado; el tooltip sigue al estado. Hereda de `Button` con `StyleKeyOverride` = `Button`: sin eso los selectores `Button.…` no lo alcanzan (vale para todo control nuevo que herede de un control con estilo). El ⏻ de las filas de efectos y envíos del mixer (`ListPower`) prendido lleva un bloque `Accent25` y el glifo en acento, sin glow (la fila lo cortaría); apagado, transparente con el glifo `Faint` |
| `SelectButton` | **el** desplegable (presets, tipos, modos); nunca un `ComboBox` |
| `TextBlock.label` / `.dim` / `.mono` / `.icon` | etiqueta / secundario / números / ícono |
| `ToolbarPanel` (código) | barra de herramientas que **nunca se superpone** (pedido del usuario): el primer grupo a la izquierda, los demás a la derecha, y los que no entran bajan a otra fila; con más de una fila, todas arrancan a la izquierda (pedido del usuario, 2026-11-04: pegadas a la derecha quedaban escalonadas). `AlignBottom` alinea los botones de grupos con etiqueta. Toda barra nueva con varios grupos va así, nunca un `DockPanel` que los pise |

## Diálogos

- Botones abajo a la derecha, en este orden: `[destructiva]` sola a la izquierda (Descartar), y a
  la derecha `[Cancelar] [otras] [Primario]`. Un solo `Button.btn.primary`, el último. Esc es Cancelar y Enter el primario.
- Los textos comunes salen de `common.cancel`, `common.close` y `common.ok`; un nombre entre comillas, de `common.quoted`.

## Atajos

- Son configurables: la tabla de acciones y sus teclas por defecto es `Shortcuts.cs` (la lista para el usuario está en
  DETALLES.md); Configuración → "Atajos de teclado" las cambia y se guardan en `config.json` ("shortcuts").
- **Distribuciones de teclas** (pedido del usuario): `KeyLayouts` (en `XtremeDaw.Projects`, como texto, para que `xtlab atajos
  comprobar` las revise) cambia la tabla base por la de otro DAW ("shortcutLayout"); solo lista lo que difiere: lo demás sigue
  con la tecla de XTreme, salvo que esa tecla quede para otra acción. Los cambios del usuario van sobre la distribución
  activa y cada una guarda los suyos ("shortcuts.cubase"…). Un atajo nuevo suma su tecla en cada DAW que lo tenga.
- Un atajo nuevo se agrega a `Shortcuts.All` y se atiende en el `switch` de `MainWindow.OnKey` (evento en túnel, vía
  `Shortcuts.Find`) por su id, nunca comparando `Key` a mano. Esc no se configura: siempre cancela.
- Los modificadores de la ruedita en el timeline (zoom, costados, altura) también son configurables: `Shortcuts.Wheel`,
  `Shortcuts.FindWheel` (guardados aparte, "wheel"); no compares `KeyModifiers` a mano para eso.
- El atajo se muestra **solo en el tooltip**, entre paréntesis y leído de `Shortcuts` (`Shortcuts.Tip(texto, id)`;
  los fijos, registrados en `shortcutTips`). Nunca en el texto visible del control.
- Todo botón de ícono tiene `ToolTip.Tip`, salvo en el mixer, el piano roll y Mastering: ahí nada muestra tooltip (botones, etiquetas, medidores, ni los menús que se abren desde ahí)
  (pedido del usuario, 2026-11-03: abajo de la ventana el tooltip se abría donde ibas a hacer clic y el botón no respondía;
  la clase `notips` en la raíz de cada vista, estilo en `App.axaml`). **Sin tooltip en lo que se arrastra o se edita con el mouse** (pedido del
  usuario, 2026-11-03: tapaban lo que ibas a agarrar): perillas, faders, paneos, barras de los medidores, gráficos (EQ, compresor,
  cruces, envolventes), ondas, divisores y manijas, filas que se arrastran, nombres de las tiras, el timeline y la
  preview. Lo que explica una perilla va en el tooltip de su etiqueta (`FxCell`); lo que dice el cursor no se repite.
  Los cambios se aplican en vivo (se escucha mientras movés el slider). El
  paneo se imanta al centro (|x| < 0,04).

## Menús

- Las opciones que se tildan o se prenden y apagan en un menú (grilla, marcas de duración, entrada de la pista, plegar
  carpeta, ocultar separador) van con `MenuCheck` / `MenuChoice` (pedido del usuario): clic izquierdo elige y cierra,
  clic derecho elige y deja el menú abierto, y el tilde o el texto se actualizan en el lugar. Lo que se lee al
  aplicar se busca de nuevo (por id), porque el menú sigue abierto mientras cambia. Las acciones comunes, con `MenuAction`.
- El menú de un clip y el de una pista van por grupos separados (pedido del usuario): en el clip, edición (cortar, copiar,
  duplicar, dividir); lugar (inicio, frente, grilla, bloquear); recorte y transiciones; análisis (tempo, escala, MIDI,
  stems); quitar. En la pista: pegar; la pista (nombre, look, video, bloquear, duplicar); sonido (instrumento, entrada, envíos);
  orden (carpeta, separador); plantillas; quitar. Una entrada nueva va en su grupo, no al final.

## Mensajes al usuario

- Lo que el usuario tiene que ver va a un toast (`Toast`): un error, algo que no se pudo hacer, un resultado que no se
  ve en otro lado. Lo demás (lo que una acción hizo y ya se ve, notas de progreso) va con `Status`, que solo lo anota
  en el registro de la sesión: la campanita se sacó (pedido del usuario, 2026-11-03: nadie la abría). Profesionales y cortos (ver
  "Tono" en la guía de la raíz): **qué pasó y, si hace falta, qué hacer** ("No se pudo guardar: {motivo}. Reintentando…").
- Nada de diálogos modales para errores, ni stack traces, ni jerga ("excepción", "null").
- Un archivo que falta no rompe nada: el clip queda **offline** (gris, "Sin conexión: falta el archivo de audio").
- Cargas largas en segundo plano (`Task.Run`) con un aviso; la UI nunca se congela esperando disco, FFmpeg o el driver.
  Si se puede saber cuánto falta, toast con barra (`Toasts.ShowProgress`).
