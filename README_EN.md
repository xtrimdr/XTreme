<p align="center">
  <img src="docs/xtreme-icon.png" alt="XTreme icon: a vinyl record with a black X on its orange label" width="220">
</p>

<h1 align="center">XTreme</h1>

<p align="center">
  <b>A DAW to record, mix, master and export music on Windows.</b><br>
  Audio and MIDI, VST3 instruments and effects, buses and mastering. And if you record with a camera, the video too.
</p>

<p align="center">
  <a href="README.md">Español</a> · <b>English</b>
</p>

<p align="center">
  <a href="https://github.com/xtrimdr/XTreme/releases/latest/download/XTreme-Setup.exe">
    <img src="docs/descargar-en.svg" alt="Download for Windows: free installer for 64-bit Windows 10 and 11" width="424">
  </a>
  <br>
  <sub>Brings everything it needs, no .NET install · <a href="https://github.com/xtrimdr/XTreme/releases">All versions</a></sub>
</p>

**XTreme** is a DAW for any instrument and any style: guitar, bass, vocals, keys, drums, a whole band or a
production built in the computer. You record, edit, mix, master and export with audio and MIDI tracks, a piano roll,
VST3 instruments and effects, buses and a full master chain.

It also makes **videos**: if you record with a camera, every take keeps its picture and XTreme builds a video with
one panel per take, with color grading and formats for social media. It is an extra for when you need it; without a
camera, it works like any other DAW.

XTreme is **built from scratch**: the audio engine, effects, instruments and interface are its own. It borrows ideas
from the best DAWs on the market, but is not based on the code of any of them.

<details>
<summary><b>Before installing</b></summary>

- **FFmpeg** and **ffprobe** on the `PATH`, for the camera, video and export.
- An audio interface with an **ASIO** driver, to record.
- The installer isn't signed: if Windows SmartScreen warns you, choose **More info → Run anyway**.

More details in [Requirements](DETALLES_EN.md#requirements).
</details>

![The timeline: audio and MIDI tracks in folders, markers, sends and VST3 instruments](docs/screenshots/xtreme-timeline.png)

![The mixer while playing: effects, sends, folders, reverb and delay buses and the master](docs/screenshots/xtreme-mixer.png)

![The piano roll: a MIDI track's notes with its VST3 instrument and velocities](docs/screenshots/xtreme-piano.png)

*The screenshots show a demo project.*

## What it does

### Record

- Audio over **ASIO** (input and output in the same callback, with monitoring). Each take is written to disk as it
  happens, so a crash does not lose it.
- Recording starts at the playhead, so you can redo just one part, with a **metronome** (time signature, tempo, tap
  tempo, count-in and six click sounds) and **latency calibration** with a cable or by playing.
- **MIDI** from keyboards and controllers: each MIDI track picks its input and channel, plays live through its
  instrument and records the notes into a clip.
- A **tuner** with arrows that show which way to turn the peg and adjustable sensitivity.

### Edit

- A multitrack **timeline**: clips you can move, trim, split and duplicate, fades, clip gain with its own **volume
  curve**, range delete, unlimited undo and redo, and import of nearly any audio or video (WAV, MP3, FLAC, OGG, M4A, AIFF and more). **Detect tempo**
  on any clip (right-click) and set the project to those BPM.
- **MIDI tracks** with a **piano roll**: double-click on the lane creates a clip and opens it. Click writes a note,
  dragging moves or lengthens it, right-click deletes it, and each note's velocity sits below.
- **Extract MIDI**: the notes of an audio clip (chords or melody) go to a MIDI track, from the right-click or by
  dragging the clip onto a MIDI track (Spotify's Basic Pitch, on the CPU).
- **Detect scale and chords**: a clip's scale, its notes and its chords in a summary, and the chords on their own timeline strip if you want.
- **Split into stems**: an audio clip splits into 2, 4 or 6 tracks (vocals, drums, bass, guitar, piano and rest), at
  Fast, High or Maximum quality, from the right-click (Demucs, on the graphics card or the CPU).
- **Folders** that fold and group tracks; Ctrl+click / Shift+click marks several tracks to move them together. Each
  track gets its own **color** (24, in rainbow order), an **instrument icon** (guitars, bass, drums, synths, strings,
  winds, percussion…), its own height and Saturation, Comp, Reverb, Delay and Pitch knobs in its header (which ones show,
  in Settings → Appearance).
- A sound you like saves as a **track preset** and comes back from New track → Preset.
- Above the ruler, an **A–B range** loops a stretch and named **markers** (Intro, Chorus, Solo…) jump from one part
  to another. Zoom from one beat to the whole song with shortcuts you assign in Settings → Keyboard shortcuts.

### VST3 instruments and effects

- Each MIDI track plays through a **VST3 instrument** (Surge XT, MT-PowerDrumKit…: right-click the track →
  Instrument, or the piano roll button, which also opens the plugin window) or, with no plugin, a test tone.
- **VST3 effects** on channels and buses (amps, pedals, third-party reverbs): "+ Effect" → VST3, in any order
  alongside the built-in ones; "Open" shows its window and what you set there is saved in the project.
- It also opens **VST2** and **32-bit** plugins: it finds them on its own and marks them in the list.
- Each plugin runs in its own process: if one hangs, only that one goes quiet and the rest keeps playing. Plugin
  latency is compensated across the whole mix, the click and the takes you record on top (PDC), and the export runs
  through the same plugins.

### Mix

- A per-channel effects chain, in any order: saturation (**Vida**, tube or tape), a **compressor** with presets
  by category (vocals, guitars, bass, drums, percussion and more), a **7-band EQ** with live spectrum, dynamic bands and a large view and a **noise gate**, plus pan templates, a
  **MONO** button to check the mix and **FX** to hear it without effects.
- **Transpose** per track (−12 to +12 semitones) without changing the length: it is computed in the background
  (about 4 s per 3 min) with a high-quality phase vocoder, progress shows on the clips and the result is kept in
  `cache/`.
- A full **mixer** with **buses**: every track or folder picks its output (the master or a bus) and sends to the
  buses (before or after the fader); the buses go to the master. A bus has volume, pan and effects: use it for a
  reverb, a delay or to process a group. A folder is a channel too: effects, pan, fader, M and S (its solo plays its tracks). Strips are reordered by dragging (the timeline
  follows), sit on the left or the right, and named separators keep big projects tidy.
- **A | B** on the mixer bar: two mixes of the same takes (channels, sends and buses) to compare while it plays;
  B starts as a copy of A, and right-click copies one over the other or forgets the other.
- Professional-grade audio: starting, stopping, jumping and dragging the playhead all fade, with no clicks or dropouts.

### Master

- A **Mastering** view with the master chain: look, cleanup, multiband, color, tape, width, soft clip and a true-peak
  limiter. **Suggest** listens to the mix and proposes settings.
- A loudness **Target** (YouTube, Instagram, TikTok, Spotify, Apple Music): you hear it while mixing and the export
  follows it. A **LUFS** meter sits in the transport bar.

### Export

- Audio as 24-bit **WAV** or 320 kbps **MP3**; optionally each track in its own WAV too (**stems**).
- **Just the A–B loop** ("Range" in the dialog) to cut a part of the song, with reverb and delay ringing out after B.
- The mix uses every processor core.

### Videos

- With a camera (DirectShow or DroidCam over IP), every take also records its picture, in sync with the audio.
- A panel per track with cover-fit, zoom, pan and mirror; **16:9 / 9:16 / 1:1** aspect ratios, side-by-side or
  stacked layout, and the **Vertical** window layout to work in 9:16.
- A **color** tab with 28 looks (Brujo, Cine, Moody, Fresco, Noche azul, Hielo, Soñado, HDR…), adjustments
  (exposure, contrast, whites, blacks, light…), effects (sharpness, clarity, glow, vignette, grain, cinema bars) and
  templates. What you see in the preview is what gets exported.
- **MP4** export with presets for **Reels · TikTok**, **YouTube** and **Instagram** (1:1 and 4:5); under
  **Advanced**, the resolution (720p to 4K) and bitrate. The video is encoded on the **video card** (NVIDIA, AMD or
  Intel) when there is one; if it fails, it carries on with the processor.
- **Social length marks** on the ruler (right-click): where a story, an X post, a Reel or Short and a TikTok end. If
  the song runs over, the line turns amber.

### And also

- **Never loses anything**: atomic saving (after every edit, every X minutes or by hand with Ctrl+S or the disk
  button, chosen in Settings → Saving and export; takes are always saved), the **History** tab to go back (every step
  of the session by name, one click away, and saved versions: automatic, named or the one of each export), recovery
  of interrupted recordings and a project manager with a Recycle Bin and **ZIP packages** to take a project to
  another computer or keep a full backup.
- Configurable language, theme, keyboard shortcuts and window layout (Classic or Vertical).

## Where it is going

XTreme is **under constant development**. The goal is to take it to a level that works both
for people just starting out who want to make music quickly and without complications, and for professionals
who want to make music seriously.

MIDI tracks with a piano roll, a built-in sampler and VST3 instruments and effects are in; next up are knobs of your
own for the plugins' parameters and more studio tools. What is planned and what is missing is
in [PENDIENTES.md](PENDIENTES.md) (in Spanish).

## More information

[DETALLES_EN.md](DETALLES_EN.md): requirements, how to build, keyboard shortcuts, how it is built and `xtlab`.

## Contributing and license

XTreme is **free and non-profit**: it is made for the community, so anyone can record and
make music without paying a cent. Contributions are welcome and **all of them go through the maintainer's
approval**: open an issue or a Pull Request following [CONTRIBUTING.md](CONTRIBUTING.md). Before touching code,
read [AGENTS.md](AGENTS.md) (the project guide, in Spanish) and see [PENDIENTES.md](PENDIENTES.md) for what is
missing.

It is released under the [GPL-3.0](LICENSE) license: you can use, study, modify and share it freely, and
distributed modified versions must stay free under the same license.

## Code signing policy

The installer is built by GitHub Actions from this repository ([instalador.yml](.github/workflows/instalador.yml)),
never on anyone's machine, so what you download comes from this code. Free code signing provided by
[SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [xtrimdr](https://github.com/xtrimdr) (every outside change goes through their approval).
- Approvers: [xtrimdr](https://github.com/xtrimdr).

**Privacy policy**: this program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it: it checks GitHub's latest release for a new version
(it can be turned off in About) and connects to the network camera you set up.

## Stem separation models

The models belong to Meta (Demucs, https://github.com/facebookresearch/demucs). XTreme neither includes nor hosts them: they download from their original source to your PC when you install them. They have their own terms and are not covered by XTreme's license. The app works without them.
