# XTreme: details

Requirements, how to build, keyboard shortcuts, how it is built and `wtlab`. The overview is in the [README](README_EN.md).

[Español](DETALLES.md) · **English**

## Requirements

- Windows 10/11, 64-bit.
- An audio interface with an **ASIO** driver.
- **FFmpeg** and **ffprobe** on the `PATH` (camera, video and export).
- [.NET 10 SDK](https://dotnet.microsoft.com/) to build.

## Build and run

```
tools\probar.ps1 "what to try"
```

It builds, runs `wtlab comprobar-dsp`, copies the app to `pruebas\<date>-<commit>` (ignored by git; the last
5 are kept) and opens it from there, marked "TEST <commit>" in the title bar. Since it never runs from `bin\`, an open
app does not block the next build. `-Abrir` opens the newest copy without building, `-NoAbrir` only builds and copies,
`-SinComprobar` skips `wtlab`, `-Proyecto folder` opens that project. If a XTreme is already open, a dialog asks first whether to close it
(it saves the project before closing), so the new copy does not open the same project read-only. Plain `dotnet build` still works for building.

The app starts in the **Projects** manager (list, search, templates, rename, duplicate, Recycle Bin). With a
project folder as an argument, it opens it directly. `tools\make-shortcut.ps1` creates a desktop shortcut that opens the newest test copy (`probar.ps1 -Abrir`).
Settings are stored in `%APPDATA%\XTreme`.

## Main shortcuts

All of them can be changed in ⚙ Settings → Keyboard shortcuts (except Esc). The interface itself is in
Spanish by default; the language is chosen in Settings.

| Key | Action |
|---|---|
| Space | play / stop |
| Shift+R or Num * | record (while recording: stop and save) |
| R | arm / disarm the selected track |
| Home | back to the start |
| L | loop (the A–B range, or the whole song) |
| I / O | range start (A) / end (B) at the playhead |
| K / , / . | marker at the playhead / go to the previous / the next one |
| C / Shift+C / T | click / count-in / tap tempo |
| A | tuner |
| M / S | mute / solo the track |
| Ctrl+K | virtual keyboard: while on, the A row is the white keys, the row above the black ones, Z / X the octave, C / V the velocity and Shift the sustain |
| F | fit all |
| X | timeline / mixer |
| Alt+X / D | split the clip at the playhead / duplicate it |
| Del | remove the clip (or the empty track); with a range, delete it |
| Shift+Del | delete the range and close the gap (what follows slides left) |
| 1 to 7 | tools: Select / Range / Split / Erase / Listen / Zoom / Hand |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+Shift+N | new track |
| Ctrl+click / Shift+click on a header | pick several tracks (one by one / a range) to move them together |
| Ctrl+S | save now |
| B / P | fold or open the channel panel / the project panel (hidden with its title bar button, it shows it again) |
| F11 | full screen (F11 again goes back to how it was) |
| F1 | help (F1 again closes it) |
| Wheel, Shift+wheel, Ctrl+wheel, Alt+wheel | scroll up and down, zoom, scroll sideways, track height (the modifiers can be changed in Settings → Keyboard shortcuts). In the mixer, the plain wheel and Ctrl+wheel scroll sideways and Shift+wheel (or the bar in its header) widens or narrows the channels |

On the selected clip, a click on its volume line adds a point; points drag, and a double-click removes one.

On the strip above the ruler, a drag sets the A–B range (and turns the loop on); its edges and the whole range drag.
A double-click adds a marker; markers drag, a click jumps there and a double-click renames them. Right-click the strip
to add a marker, start or end the range there or remove it, and on a marker to pick a name from the list or loop to
the next one.

The timeline tools sit in the middle of the project bar: **Select** (as always), **Range** (marks a stretch of one or
several tracks; right-click: delete it, delete it and close the gap, loop it), **Split** (a click cuts the clip
there, on the grid), **Erase** (removes the clips it touches), **Listen** (hold the button on a clip to hear that
track alone from there), **Zoom** (click zooms in, Alt+click out, a drag shows that stretch) and **Hand** (drag to
move the view). Where two clips of a track overlap, right-click → **Bring to front** picks which one shows.
**Reverse** (right-click on an audio clip) plays it backward without touching the file; it doesn't apply to clips with video.
**Lock clip** or **Lock track** (right-click) pins a take that came out right: it can't be moved, trimmed or deleted
and recording won't replace it; volume, fades and framing still change.

## How it is built

```
src/
  XtremeDaw.Audio      audio engine: DSP, duplex ASIO, click, recording to disk, export
  XtremeDaw.Projects   project model, atomic saving, history, project manager
  XtremeDaw.App        the window (Avalonia 11.3)
  XtremeDaw.Lab        wtlab.exe: console to test audio, DSP and projects without the window
tools/                 icon, shortcut and verification utilities
```

Dependencies point downward (App → Projects → Audio): the engine and the project model know nothing about
the interface, and everything they do can be exercised from `wtlab`. A project is saved in a folder:

```
guitarcam.json        the project
guitarcam.json.bak    the previous save
historial/            copies to go back to
media/                recordings and imports (never rewritten)
exports/              exported videos and audio
```

## wtlab

`wtlab.exe` (`src\XtremeDaw.Lab\bin\Debug\net10.0-windows\wtlab.exe`) tests the engine and projects from the
console without opening the window. Its commands and options are in Spanish (for example `grabar`, `mezclar`,
`exportar`, `proyecto`, `proyectos`); run it without arguments to see the help, or see the command table in
the [Spanish version](DETALLES.md#wtlab).

`wtlab componentes [estado | instalar id [--paquete p] [--url u] | comprobar]`: optional component state, install, and a download test against a local server.

## Stem separation models

The models belong to Meta (Demucs, https://github.com/facebookresearch/demucs). XTreme neither includes nor hosts them: they download from their original source to your PC when you install them. They have their own terms and are not covered by XTreme's license. The app works without them.
