# Controles dibujados a mano

Se suma a [src/XtremeDaw.App/AGENTS.md](../AGENTS.md). `TimelineView`, `PreviewView`, `StereoMeter` y los demás que
heredan de `Control` dibujan en `Render`. Lo que corre en cada `Render` o `Tick` no asigna memoria: la basura de la UI
también corta el audio (ver "Audio en tiempo real" en la guía de la raíz).

- Texto, pinceles y plumas siempre de `Palette` (`Palette.Text` / `ctx.DrawText(...)`, `Palette.Pen`, `Palette.Alpha`),
  que los cachea por contenido y color. Nada de `new Pen`, `FormattedText` ni pinceles por cuadro. `Palette.Text` da
  un `TextLayout` que se arma una vez y no se modifica.
- Cacheá lo caro (ver el caché de ondas por clip) y redibujá con `InvalidateVisual()` solo cuando algo cambió (un
  medidor quieto o un cabezal parado no redibujan: `TimelineView.SetPlayhead`, `StereoMeter.Push`).
- Lo que se mueve en cada cuadro va en una capa aparte: el cabezal y los medidores de los encabezados del timeline se
  dibujan en `TimelineView.Layer`, y mientras suena solo se redibuja esa capa.
- Las líneas de 1 px van en medio píxel (`y + laneH - 0.5`) para que queden nítidas.
- El control expone eventos (`SeekRequested`, `SelectionChanged`, `TrackToggled`…) y la ventana decide qué hacer. El
  control no toca el motor ni el guardado.

Una perilla puede llevar su color (`Knob.Tint`, el de la familia de su efecto): `KnobLooks` pinta el arco, los puntos y la
marca con `KnobLooks.Tint` en vez del acento mientras dura su dibujo. El gráfico de cada efecto (`FxScope`) dibuja con
líneas cortas y columnas de 1 px, sin geometrías; solo el de la modulación se redibuja solo, y solo mientras suena.

## Etiquetas de los clips

Todo lo que se escribe o se marca sobre un clip (íconos, pestañas, el nombre, el progreso de un trabajo) pide su lugar a
la franja de arriba del clip (`ClipBand`, `TimelineView.ClipBand.cs`; pedido del usuario, #240: siempre algo terminaba
tapado): `TakeLeft` / `TakeRight` en orden de importancia (congelada, insignia de medio, bucle, candado, al revés,
tono, progreso) y el nombre ocupa lo que queda; lo que no entra no se dibuja. La franja ya deja libres la pestaña del
borde, el final de los fundidos y la esquina del bucle. **Nunca dibujes una etiqueta en una posición fija sobre un
clip**: sumala a la franja (audio en `DrawClip`, MIDI en `DrawMidiLabels`). Abajo del clip solo va la marca del corte
de anclaje. Una pista congelada dibuja sus clips en dos capas (`ClipLayer`): el cuerpo apagado, la onda del render y
después las etiquetas enteras.

## Medidores

`StereoMeter`: ataque instantáneo, caída de 24 dB/s, pico retenido 1,5 s, rango −60 dB (−48 en los de pista). Acento
hasta −6 dB, ámbar de −6 a 0, rojo arriba de −1 dB (pico) o −0,3 dB (luz de clip). La luz de clip queda prendida
hasta que se hace clic en el medidor. Los medidores del mixer usan otro degradé (verde → amarillo → rojo,
`Palette.Meter*`). Los picos los escribe el hilo de audio y los lee y resetea la UI en su `Tick` (16 ms); la caída
visual (×0,82 por tick en pistas, 24 dB/s en el estéreo) se hace del lado de la UI.

`MixerMeter` (las tiras del mixer): dos barras L/R, cada una con RMS sólido + pico tenue con una rayita y su luz de
clip (mismo umbral de −0,3 dB que el `StereoMeter`) que queda prendida, y el pico máximo en dBFS arriba; un clic en el
medidor reinicia pico máximo y luces. Con `Mono` (pista con tomas mono) muestra una sola barra del ancho del par con
el lado más fuerte. El ancho de sus barras sigue el de la tira: `Fit` las ensancha con lo que deja el fader
(con la escala, si se ve) en todo ancho (#199), tomando la mitad de ese lugar (#205: llenarlo entero las dejaba muy
anchas), entre 5 px (el del máster, 7) y 14 px. La ventana le pasa `Set(picoL, picoR, rmsL, rmsR)` (picos con su caída, que también alimentan el
máximo y las luces; RMS como amplitud ya integrada); cualquier tira puede alimentarlo así. El RMS lo suma el hilo de audio en un `RmsTap`
(`XtremeDaw.Audio.Dsp`: suma de cuadrados por bloque en totales que solo crecen, sin reset desde la UI) y la UI lo
toma una vez por tick (`TakeMixerRms`) y suaviza cada lado ~300 ms (`MeanSqL` / `MeanSqR`). El texto del máximo se rearma solo cuando cambia su décima.
Va en tres capas (#138): el control dibuja lo que cambia poco (lectura, luces, fondo de las barras, escala), una capa
hija los niveles y otra las marcas de dB encima; mientras suena se redibuja solo la de los niveles. Cada llamada de
dibujo es un objeto que Avalonia guarda hasta el cuadro siguiente: un medidor entero por tira y por cuadro era la
mayor parte de la basura que frenaba el audio.
La escala va en `Dim` (el 0 en `Fg`) y el fondo de las barras en `Hover`: se leen con cualquier tema y también sobre una
tira seleccionada (`Raised`).

Cómo se pinta la barra lo elige el usuario en Configuración → Apariencia (`MeterLooks`: Continuo por defecto, LED,
Degradé, Fino, Tron: LED de luz sobre negro, los apagados tenues, del tema Tron Legacy; "meterLook"). Un look solo cambia el dibujo de la barra; colores, umbrales, luces de clip, pico retenido
y escala son los mismos. La geometría de los LED se arma una vez por tamaño (`MeterLooks.Segments`) y se usa como clip.
Los colores se eligen aparte (`MeterColors`, #202: "meterColors" y "meterColorsCustom"): Por defecto (los de arriba) o Custom, tres
colores del usuario (Bajo, Caliente, Clip) para todo medidor y todo look, Tron incluido, con los mismos umbrales; los
medidores piden sus pinceles a `MeterColors` (`MixerLevels`, `StereoLow`/`StereoHot`, `MeterLooks.StereoGradient`/`TronLevels`),
que se arman una vez por cambio.

## Ondas

El look de la onda de los clips (`WaveLooks`: Rellena por defecto, Contorno, Rectificada, Barras; "waveLook") decide
cómo las columnas del caché de ondas se hacen geometría (`Emit`) y cómo se pinta (`Paint`); cambiarlo vacía el caché
una vez. Respeta L/R (`SplitWave`), la ganancia y la curva del clip y el clip offline.

## Color de pista

Cómo pinta el color de una pista en el timeline lo elige el usuario en Configuración → Apariencia (`TrackColorLooks`,
#162): los clips de audio y MIDI (Tenue por defecto, Lleno, Solo onda, Barra de título; "trackColor.clips") y marcas que
se prenden aparte (franja en el encabezado, encabezado teñido, carril teñido, selección en el color de la pista;
"trackColor.marks"). Solo cambian las pistas con color; los clips offline y los que muestran fotogramas quedan como siempre. En el inspector, aparte (otra tarjeta): el encabezado (Línea y tinte por defecto, Solo línea, Lleno, Sin color;
"trackColor.inspector") y marcas (franja al costado, secciones teñidas, íconos de sección en su color;
"trackColor.inspectorMarks") y la flecha de la pista elegida al inspector (`InspectorPointer`, "trackColor.pointer",
prendida por defecto, #180); el color sale de lo que muestra el inspector (`MainWindow.PaintInspectorColor`).
