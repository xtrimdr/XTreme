# XTreme: details

Requirements, how to build, keyboard shortcuts, how it is built and `xtlab`. The overview is in the [README](README_EN.md).

[Español](DETALLES.md) · **English**

## Requirements

- Windows 10/11, 64-bit.
- An audio interface with an **ASIO** driver.
- **FFmpeg** and **ffprobe** on the `PATH` or installed with winget (camera, video and export).
- [.NET 10 SDK](https://dotnet.microsoft.com/) to build.

## Build and run

```
tools\probar.ps1 "what to try"
```

It builds, runs `xtlab comprobar-dsp`, copies the app to `pruebas\<date>-<commit>` (ignored by git; the last
5 are kept) and opens it from there, marked "TEST <commit>" in the title bar. Since it never runs from `bin\`, an open
app does not block the next build. `-Abrir` opens the newest copy without building (with `-Probar 12`, showing what to try in #12), `-NoAbrir` only builds and copies,
`-SinComprobar` skips `xtlab`, `-Proyecto folder` opens that project. If a XTreme is already open, a dialog asks first whether to close it
(it saves the project before closing), so the new copy does not open the same project read-only. From the desktop shortcut, which has no console, a failure shows in a dialog. `-SoloCompilar` only builds, and `dotnet build` works too.

The text goes on the **Test bench** (`pruebas\lista.json`, it outlives the copies). The bench shows it: a program of its own (`xtbench.exe [--lista file]`, in `pruebas/banco/`, one at a time; building XTreme never closes it, and a newer one comes in the next time it opens), which a test copy opens by itself when something is pending and from the button next to "TEST", and which `-Banco` or the "XT-WorkBench" shortcut also opens: each item is marked Works or Fails, with a note on what went wrong. An item from a copy newer than the open one says it is missing here. Its **Review** buttons open Claude on `/xt_revisar-workbench` (an item, a verdict or the tab), and the **Claude** menu in its title bar the project's other skills; that menu also picks, once, whether they open in the desktop app (the message is prefilled and the folder confirmed) or in a console (Windows Terminal, where it starts by itself; without the app, always the console). `-Lista` shows it by the bench's tabs (Requests, To fix, To try and Resolved); `-Arregla 12` (with `-NoAbrir` or not) sends that item back to be tried on the new copy and counts one more attempt, unless the user already marked it Works (exit code 2: it is not reopened). Each item is a **ticket** with its conversation (the request or what to try, each fix, each verdict with its note, and comments): `-Ticket 12` shows it whole and `-Comenta 12 "text"` says something in it without changing its state; `-Titula 12 "title"` gives it the short title the agent sets when taking it (the bench shows it on top, and the ticket as a timeline, each entry by User or Agent); the `"what to try"` of `-Arregla` becomes the fix's message. Every change also goes to `pruebas\lista.historial.jsonl`, with who made it (the user or the Claude session): `-Historial 12` shows an item's and `-Historial 0` the latest of all. What an agent adds or sends back reaches the bench as **New** until the user sees it.

In the panel's **Requests** tab the user enters what they want done, numbered like the rest and with its kind (**Bug**, **Improvement**, **New feature** or **Idea**); that is where agents take their work from. `-Toma 12` reserves a request, or something marked Fails, Works badly or To improve, for one session (exit code 2 if another one has it, saying which; the same session may take it again; it expires after 6 hours, and is free as soon as the session that took it closes), `-Suelta 12` gives it back and `-Arregla 12` sends it to be tried once it is done. All three take several numbers (`-Arregla 12,14`: one build and one copy). `-Pide "what" -Tipo error|mejora|funcion|idea` adds one from the console (`xtlab lista pedir | tomar | soltar`; `xtlab lista historial [number]` prints the history, `xtlab lista ticket | comentar | titulo` a ticket, a comment and its title).

Each ticket has a **topic**, from a fixed list (the part of the app: Timeline, Mixer, Inspector, Piano roll & MIDI, Instruments, Effects, Mastering, Automation, Plugins, Audio & transport, Video, Export, Analysis, Project & files, Interface, Bench & tools; `xtlab lista temas` says what goes in each). The bench shows it as a badge, filters by topic over the list and lets it be picked when entering a request or from the ticket. `-Tema mixer` gives a topic to what is added (`-Pide`, `"what to try"`, `-Arregla`) and `-Clasifica 12,14 -Tema mixer` to tickets already there (`-Tema ninguno` takes it off; `xtlab lista tema number topic`).

When several sessions work in this folder, building, checking and copying go one at a time: Debug `dotnet build` (`tools\turno.targets`), `probar.ps1` and the heavy `xtlab` commands (`comprobar-dsp`, `exportar`, `separar`…) wait for their turn (`turno.lock`, next to `xtlab.exe`). `probar.ps1` and `xtlab` run below normal priority so the machine stays responsive. If the turn doesn't come within 9 minutes they fail and say who holds it (`probar.ps1` and `xtlab`, code 4). `XtremeDaw.Audio` is built optimized in Debug too, so the test copy and `xtlab` measure the DSP as the installer runs it.

Files also go one session at a time: a Claude Code hook (`tools\bloqueos.ps1`, registered in `.claude\settings.json`) gives each file edited, or changed by a command, to the session that touched it until its turn ends; another session that wants to change it waits (up to 9 minutes) and, if a command overwrote someone else's file, it is told so. It blocks `git restore`, `stash`, `clean`, `reset --hard` and `checkout --`. `tools\bloqueos.ps1 -Lista` shows who holds what and `-Limpiar` releases everything (in case a session died; they expire on their own after 30 unused minutes). Sessions send each other alerts (#263): the hook tells on its own when a file you waited for is released, when another session takes one you changed, when someone else's command changed one of yours and when a build fails in a file of yours; by hand, `-Avisa <#ticket|session|todos> -Texto "…"` (`-Atencion` to make it stop), `-PideArchivo <path>` (asks whoever holds it), `-SueltaArchivo <path>` (releases it before the turn ends) and `-Buzon` (the unread alerts). The agent gets them on its next edit or command, before its turn ends or with the next message, and the bench shows them as they arrive.

The app starts in the **Projects** manager (list, search, templates, rename, duplicate, Recycle Bin). With a
project folder as an argument, it opens it directly. `tools\make-shortcut.ps1` creates the desktop shortcut **XTreme Prueba**, which opens the newest finished test copy (`probar.ps1 -Abrir -Acceso`) without building and without needing any open session; `probar.ps1` remakes it after every copy.
Settings are stored in `%APPDATA%\XTreme`.

## Main shortcuts

All of them can be changed in ⚙ Settings → Keyboard shortcuts (except Esc). There, **Key layout** switches them all at
once to those of Cubase, Logic Pro, FL Studio, Reaper or Studio Pro (`KeyLayouts`); your own changes are kept in each
layout. This table is XTreme's. With a plugin's window in front, every
key goes to XTreme, unless **Keys to plugin** is on in its bar. The interface itself is in
Spanish by default; the language is chosen in Settings.

| Key | Action |
|---|---|
| Space | play / stop |
| Shift+R or Num * | record (while recording: stop and save) |
| R | arm / disarm the selected track |
| Home | back to the start |
| Ctrl+. | cut audio: stops and silences effect tails and notes still sounding |
| L | loop (the A–B range, or the whole song) |
| I / O | range start (A) / end (B) at the playhead |
| K / , / . | marker at the playhead / go to the previous / the next one |
| C / Shift+C / T | click / count-in / tap tempo |
| A | tuner |
| M / S | mute / solo the track |
| Ctrl+K | virtual keyboard: while on, the A row is the white keys, the row above the black ones, Z / X the octave, C / V the velocity and Shift the sustain. With a scale, the Z, A and Q rows are three octaves of it, O / P the octave and L / ; the velocity |
| H | chords in the piano roll: a click writes a chord; 1 to 4 pick the selected chord's inversion |
| F | fit all |
| X | timeline / mixer |
| Alt+X or Insert / D or Ctrl+B | split the clip at the playhead / duplicate it |
| Shift+drag a clip | copy it: the original stays where it was |
| Shift+click a clip | select from the selected clip to that one |
| Ctrl+J | join the selected MIDI clips (one per track) |
| Shift+Q | align the selected clips to the grid |
| Ctrl+A / Ctrl+D | select all clips / clear the selection |
| Del | remove the clip (or the empty track); with a range, delete it |
| Shift+Del | delete the range and close the gap (what follows slides left) |
| 1 to 9 | tools: Select / Pencil / Brush / Range / Split / Erase / Listen / Zoom / Hand |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Ctrl+Shift+N | new track |
| Ctrl+Shift+D | duplicate the track (or the picked ones), with its content |
| F2 | rename depending on where you last clicked: in the inspector, its name; on the track headers, the selected track or folder; in the timeline, the selected clip (without one, the track or folder) |
| Ctrl+G | group what is picked in the timeline into a folder (tracks, or those of the selected clips) |
| Ctrl+click / Shift+click on a header | pick several tracks (one by one / a range) to move them together |
| Ctrl+S | save now |
| Ctrl+Shift+S | save as… |
| B / P | fold or open the channel panel / the project panel (hidden with its title bar button, it shows it again) |
| Enter | maximize the panel you touched last: video, timeline, piano roll, mixer or mastering (Enter again goes back to how it was) |
| F11 | full screen (F11 again goes back to how it was) |
| Ctrl + / Ctrl − / Ctrl 0 | make the interface 1 px larger or smaller, or back to 100 % |
| F1 | help (F1 again closes it) |
| Wheel, Shift+wheel, Ctrl+wheel, Alt+wheel | scroll up and down, zoom, scroll sideways, track height (the modifiers can be changed in Settings → Keyboard shortcuts). In the mixer, the plain wheel and Ctrl+wheel scroll sideways and Shift+wheel (or the bar in its header) widens or narrows the channels |

On the selected clip, a click on its volume line adds a point; points drag, and a double-click removes one.

On the strip above the ruler, a drag sets the A–B range (and turns the loop on); its edges and the whole range drag.
A double-click adds a marker; markers drag, a click jumps there and a double-click renames them. Right-click the strip
to add a marker, start or end the range there or remove it, and on a marker to pick a name from the list or loop to
the next one.

The timeline tools sit in the middle of the project bar: **Select** (as always), **Pencil** (a click on a clip picks it; a click on an empty spot
places a copy, and dragging positions it), **Brush** (the same, but dragging repeats the clip along the stretch, side by
side; a click on a pattern in Patterns or on a sample in the Library loads both), **Range** (marks a stretch of one or
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
  XtremeDaw.Lab        xtlab.exe: console to test audio, DSP and projects without the window
  XtremeDaw.Bench      xtbench.exe: XT-WorkBench, the test bench (a program of its own: building XTreme never closes it)
tools/                 icon, shortcut and verification utilities
```

Dependencies point downward (App → Projects → Audio): the engine and the project model know nothing about
the interface, and everything they do can be exercised from `xtlab`. A project is saved in a folder:

```
Name.xtp                 the project (JSON); a double click opens it
Name.xtp.bak             the previous save
historial/               copies to go back to
media/                   recordings and imports (never rewritten); while recording, the takes' journals and the MIDI takes' notes (.notas)
exports/                 exported videos and audio
```

## xtlab

`xtlab.exe` (`src\XtremeDaw.Lab\bin\Debug\net10.0-windows\xtlab.exe`) tests the engine and projects from the
console without opening the window. Its commands and options are in Spanish (for example `grabar`, `mezclar`,
`exportar`, `proyecto`, `proyectos`); run it without arguments to see the help, or see the command table in
the [Spanish version](DETALLES.md#xtlab).

`xtlab proyecto editar folder tempo-mapa [beat:bpm,…] [--compas bar:n/d,…]`: the tempo track: points in beats (quarter notes from 0), a trailing `~` ramps to the next (`0:120,16:90~,32:140`); meters change at a bar start (`0:4/4,8:7/8`). MIDI, follow-tempo tracks, markers, chords and the loop keep their bar; with nothing, prints the map. `xtlab proyecto editar folder sigue-tempo track si|no`: an audio track that follows the tempo (its clips move and stretch with the map; by default they stay in seconds). `xtlab proyecto verificar-tempo`: checks ramps, meters, the click, what moves, undo and .mid.

`xtlab proyecto verificar-bucles`: checks looped takes, MIDI clips and automation blocks: repeats, following the original, splitting and saving.

`xtlab proyecto verificar-sin-guardar`: checks the project without a place: where it lives, the recent list, finding it again at startup and Save as.

`xtlab componentes [estado | instalar id [--paquete p] [--url u] | comprobar]`: optional component state, install, and a download test against a local server.

`xtlab colaborar comprobar`: the live session (#291) without the window: state diffs, the invitation code and a real session over 127.0.0.1 (changes both ways, audio transfer, undo, solos, another version, another key). With `--internet`, the same session through the session server (#320; `XTREME_RELAY=http://127.0.0.1:8787` points it at an `npx wrangler dev` running in `relay\`; with the key in `XTREME_RELAY_ADMIN` it also tests the admin page, #321). `xtlab colaborar anfitrion folder [--nombre N] [--internet]` hosts a project and prints the code (short, over the internet); `xtlab colaborar unirse code folder [--nombre N]` joins and creates the copy in that folder.

`xtlab proyecto vinculados folder`: each take and where its files are. `xtlab proyecto copiar-vinculado folder media`: copies what is linked into media/. `xtlab proyecto reencontrar folder media --archivo X` or `folder [media] --buscar-en Y`: finds what is missing, by hand or by name and size. `xtlab proyecto verificar-vinculados`: the checks, with a process that really dies mid-copy (`proyecto cortar-copia`).

`xtlab midi herramienta folder --clip id --tool arpeggiate [--rate 5 …]`: runs any piano roll tool (`MidiTools.All`) with its settings by id. `xtlab midi exportar-mid folder --clip id --a x.mid`: the clip as a type 1 MIDI file, at the project's tempo. `xtlab midi importar-mid folder --de x.mid [--pista n] [--en 0]`: with `--pista`, everything in one clip on that MIDI track; without it, a new track per track of the file (per channel in a type 0). `xtlab midi partitura folder --clip id --a x.pdf`: the clip as a PDF score (grand staff, 4/4, quantized to 1/16, with the key signature of the project's scale when it is natural major or minor).

`xtlab balance file [--objetivo pop|rock|edm|hiphop|acoustic|orchestral|dubstep|riddim|deepdubstep|…] [--referencia a.wav,b.mp3] [--modo estereo|mid|side] [--curva] [--comprobar]`: the file's tonal balance against a target: the four bands (20–120 Hz, 120 Hz–1 kHz, 1–5 kHz, 5–20 kHz) and the low-end punch; `--referencia` builds the target from those files, `--curva` lists the 61 points with their range, `--comprobar` runs only its checks.

## Stem separation models

The models belong to Meta (Demucs, https://github.com/facebookresearch/demucs). XTreme neither includes nor hosts them: they download from their original source to your PC when you install them. They have their own terms and are not covered by XTreme's license. The app works without them.
