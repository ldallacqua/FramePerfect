<div align="center">

# Frame Perfect

**Sharper DLSS. Perfectly paced frames. Less input lag.**

A Dalamud plugin that makes Final Fantasy XIV look and feel the way your NVIDIA graphics card can run it.

![Dalamud](https://img.shields.io/badge/Dalamud-API%2015-7c5cff)
![Version](https://img.shields.io/badge/version-2.0.0-2ea44f)
![NVIDIA RTX](https://img.shields.io/badge/NVIDIA-RTX-76b900?logo=nvidia&logoColor=white)
![Setup](https://img.shields.io/badge/setup-none-blue)

</div>

---

Install it, log in, play. Frame Perfect sets itself up, fixes the game options that hold it back, and tells you in plain words about the one or two things only you can change.

<!-- Screenshot: put the Overview page here, e.g. ![Frame Perfect](docs/overview.png) -->

## The problem

Out of the box, FFXIV leaves a lot of a modern NVIDIA card on the table:

- **It goes blurry exactly when you look around.** The game's dynamic resolution drops the DLSS resolution whenever the camera moves.
- **Its frame limiter is V-Sync in disguise.** It can only wait whole screen refreshes, so one slow frame doubles the frame time (16.7 → 33.3 ms), and that's a stutter you feel.
- **Frames get dropped in borderless.** Windows picks the frame for each refresh about a millisecond before it's shown. Any frame that arrives in that last millisecond waits a whole refresh, and the next one replaces it.
- **No NVIDIA Reflex.** Every input waits behind a queue of frames the GPU hasn't drawn yet.
- **An old DLSS.** It ships DLSS 3.5 with an old model, and it ignores your monitor if it isn't the primary one.

## What Frame Perfect does

| | |
|---|---|
| 🎯 **DLSS stays sharp** | Locks DLSS at the resolution you pick, from DLAA to Ultra Performance or any custom percentage. The game can't lower it anymore, so the picture stays crisp while the camera moves. |
| ⚡ **NVIDIA Reflex** | Adds Reflex low-latency mode to a game that never had it: the GPU stops queueing frames and each frame starts right before it's needed. |
| 🎚️ **A frame cap that's actually precise** | Driver-level pacing through Reflex instead of the game's V-Sync steps. **Automatic** mode picks the right cap for your screen: just under the refresh rate with G-Sync, exactly the refresh rate without it. |
| 🧷 **Every frame on time** | With a fixed refresh rate, Frame Perfect times every frame to your screen's refresh so none arrive too late. Measured: **0 dropped frames**, against 343 in 20 seconds without it. |
| 🚀 **NVIDIA Smooth Motion ready** | Detects NVIDIA's driver frame generation and caps the game at exactly half, so 60 rendered frames become a clean 120 on your 120 Hz screen. |
| ✨ **Sharpening and newer DLSS** | AMD CAS sharpening on the DLSS image (UI untouched), NVIDIA's recommended DLSS model, and one-click updates to the latest DLSS from NVIDIA's official repository, signature checked. |
| 🛠️ **Sets the game up for you** | Switches the game options it needs (DLSS on, frame limit off, Radial Blur off), saves yours first, and puts them back with one click. |
| 🩺 **Explains, doesn't lecture** | An Overview page that says whether everything's working, what it's doing for you, and what (if anything) needs you, with a "fix it" button where it can. |

## Measured, not guessed

Every claim here comes from measurements, not from eyeballing. PresentMon captures were taken on an RTX 4090 at 4K, 120 Hz, with DLSS 5 and NVIDIA Smooth Motion, 20 seconds each while turning the camera:

| | without Frame Perfect's timing | with Frame Perfect |
|---|---|---|
| frames reaching the screen | 102.9 a second | **120.0 a second** (of 120) |
| frames dropped in 20 s | 343 | **0** |
| how long each frame stays on screen | a mix of 8.3 and 16.7 ms | **always 8.32 – 8.36 ms** |
| input to photon (PresentMon) | ~17 ms | **~14 ms** |

How the problems were found and measured, step by step: [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md).

## Zero setup, nothing hidden

**Frame Perfect sets these game options for you** (System Configuration), and only while it needs them:

| option | set to | why |
|---|---|---|
| Graphics Upscaling | NVIDIA DLSS | Frame Perfect controls DLSS (only on RTX cards) |
| Frame Rate Limit | None | its own cap replaces the game's V-Sync limiter |
| Radial Blur | Off | it blurs the whole picture whenever the camera turns |

Your own values are saved first. Untick any option to keep your own, or press **Restore my original settings** to put everything back. It never changes anything while System Configuration is open.

**It never touches your NVIDIA driver settings.** When one would help, it tells you exactly where to click. The main one is below.

## Recommended setup

- **Borderless and a G-Sync monitor?** If the picture flickers in brightness (briefly brighter or darker, mostly while the camera moves), set NVIDIA Control Panel > Manage 3D settings > Program Settings > Final Fantasy XIV > **Monitor Technology: Fixed Refresh**. That's G-Sync reacting to how Windows composites borderless games, and it's gone with Fixed Refresh. Frame Perfect notices and switches its cap to match. Other games keep G-Sync.
- **Want more frames?** Turn on **Smooth Motion** for FFXIV in the NVIDIA app (RTX 40 / 50), then restart the game. Frame Perfect does the rest.
- **Everything else:** leave it on Automatic.

## Install

Frame Perfect isn't in a plugin repository yet, so for now it installs as a dev plugin:

1. Build it (`dotnet build -c Release`, see [Build](docs/HOW-IT-WORKS.md#build)), or take `FramePerfect.dll`, `FramePerfect.json` and `FramePerfect.deps.json` from a release.
2. Copy the three files to `%APPDATA%\XIVLauncher\devPlugins\FramePerfect\`.
3. In the game: `/xlsettings` > Experimental > Dev Plugin Locations, add `FramePerfect.dll` from that folder, then enable **Frame Perfect** in `/xlplugins` > Dev Tools.

The window opens on first login. After that: `/frameperfect`, or click the frame rate in the server info bar.

**Coming from DLSS Fix?** That's this plugin's old name. Your settings carry over. Remove the old DLSS Fix dev plugin first: Frame Perfect won't start next to it, because both would hook the same game code.

### Requirements

- FFXIV with [XIVLauncher](https://goatcorp.github.io/) and Dalamud.
- **An NVIDIA RTX card** for DLSS. Reflex, the frame cap and frame timing work on any NVIDIA card Reflex supports (GTX 900 and newer).
- **Smooth Motion:** RTX 40 or 50 and a recent driver (RTX 40: 590.26 or newer).
- AMD and Intel cards: the game's DLSS and NVIDIA Reflex aren't available, so only the refresh-rate fix applies.

## The window

| page | what it's for |
|---|---|
| **Overview** | Is everything working? Live image, frame rate and latency, what Frame Perfect is doing for you, and anything that needs you. |
| **Image quality** | DLSS resolution (with the exact pixel size for your screen), resolution lock, sharpening, DLSS version and model. |
| **Smoothness** | Reflex, the frame cap (Automatic / Custom / Off), frame timing, Smooth Motion status. |
| **Game settings** | The game options Frame Perfect sets, what they are now, and Restore. |
| **Help & fixes** | Brightness flicker, stutter, input lag, Smooth Motion, undoing everything, in plain words. |
| **Diagnostics** | Every technical detail, and a "Copy report" button for bug reports. |

## Commands

| command | what it does |
|---|---|
| `/frameperfect` | open the window |
| `/frameperfect status` | everything in chat |
| `/frameperfect mode <dlaa\|quality\|balanced\|performance\|ultra\|custom\|game>` | DLSS resolution |
| `/frameperfect sharp <0-100>` | sharpening |
| `/frameperfect reflex <off\|on\|boost>` | NVIDIA Reflex |
| `/frameperfect cap <auto\|off\|fps>` | frame cap (the frame rate you see) |
| `/frameperfect refreshlock <on\|off>` | frame timing for fixed refresh rates |
| `/frameperfect gamesettings <on\|off\|restore>` | automatic game options; `restore` puts yours back |
| `/frameperfect bar <on\|off>` | frame rate in the server info bar |
| `/frameperfect dll [update]` | check for, or install, the latest DLSS |
| `/frameperfect on` / `off` | turn Frame Perfect on or off |

Advanced: `custom <33-100>`, `preset <default|j|k|l|m|f|game>`, `lock <on|off>`, `test [seconds|stop]`, `flash <on|off>`, `reset`, `apply`. The old `/dlssfix` command still works.

## FAQ

**Does it change game files?** Only if you ask it to update DLSS: it downloads `nvngx_dlss.dll` from NVIDIA's official repository, checks GitHub's hash and NVIDIA's signature, and keeps your old file as a backup. Everything else happens in memory while the game runs, and game options go through Dalamud's own API.

**Does it work with ReShade, RenoDX and DLSS 5?** Yes. It was built and measured with ReShade, RenoDX's DLSS 5 addon and Smooth Motion all running together.

**Will it make my GPU work harder?** Usually less: the automatic cap stops it from rendering frames your screen can't show. Sharpening costs a fraction of a millisecond.

**How do I undo everything?** Game settings > Restore my original settings, then turn the plugin off. An updated DLSS keeps the original next to it as `nvngx_dlss.dll.<version>.bak`.

**Is it allowed?** Like every Dalamud plugin it's a third-party tool, so use it at your own discretion. It only changes how the game is drawn and paced, and it never automates or reads anything about gameplay.

## How it works

The short version:
- It hooks the game's DLSS setup to pin the render resolution and pick the model.
- It calls NVIDIA Reflex (NVAPI) at the start of every frame.
- It reads the exact refresh timing from Windows to time each frame.
- It reads (never writes) the NVIDIA driver profile to know whether G-Sync or Smooth Motion is on.

The long version, with every measurement: [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md).

<div align="center">
<sub>Made for Eorzea, measured with PresentMon.</sub>
</div>
