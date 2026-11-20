# SnipVid

SnipVid is inspired by the Windows Snipping Tool. That recorder runs at roughly
30 fps, sometimes less because the frame rate is variable, and there is no
setting to change it. SnipVid keeps the same gesture: one hotkey, drag a box,
record. It records at a fixed 30 or 60 fps, with quality presets. Capture and
encoding run through ffmpeg on the GPU or CPU, whichever the machine can do.

## Download

Download `SnipVid-1.0.0-win64.zip` from the Releases page. Extract it anywhere
and run `SnipVid.exe`. `ffmpeg.exe` is included; keep both files in the same
folder. Run `install.cmd` to add Desktop and Start menu shortcuts and start
SnipVid with Windows.

## Requirements

- Windows 10 version 2004 (build 19041) or newer.
- An encoder SnipVid can use: NVIDIA (NVENC), AMD (AMF), Intel Quick Sync, or
  software x264. It probes for one on the first recording and remembers the answer.
- .NET Framework 4.8, included in Windows 10 2004 and newer.

## Usage

1. Press `Ctrl+Alt+Shift+S`. The screen dims.
2. Drag a rectangle around the area to record.
3. Press `Enter` to start recording.

While recording, the pill in the bottom-right corner has **Pause** and **Stop**.
The gear button sets video quality (`cq` 19, 22 or 25), frame rate (30 or 60) and
the save folder. The **Mic** and **System** items turn microphone and
system-sound recording on or off.

Recordings are saved to `Videos\Screen Recordings` unless you change the folder.

## Limitations

- Hardware encoding depends on the machine. If no hardware encoder can read the
  captured frames, SnipVid falls back to software x264, which uses noticeably more
  CPU and may not hold 60 fps at large regions.
- Recording is limited to one monitor at a time.
- The executable is unsigned, so Windows SmartScreen warns on the first run.
  Choose "More info" then "Run anyway".
- System audio binds to the output device that is active when recording starts.
  Changing the default output device mid-recording is not picked up.

## License

MIT, see `LICENSE`. The bundled ffmpeg is GPLv3, see `THIRD-PARTY-NOTICES.txt`.
Developed with agentic tooling: spec-first planning, implementation and
verification passes.
