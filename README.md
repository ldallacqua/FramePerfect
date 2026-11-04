# DLSS Fix (FFXIV / Dalamud)

A Dalamud plugin that locks Final Fantasy XIV's DLSS to a fixed internal resolution instead of the game's dynamic resolution, so the image no longer goes soft whenever the camera moves.

- **Fixed DLSS resolution:** DLAA, Quality, Balanced, Performance, Ultra Performance, or a custom percentage.
- **Model preset:** the game hard-codes the old CNN preset C. The plugin defaults to the preset NVIDIA recommends for the loaded `nvngx_dlss.dll`, or you can pick J, K, L, M or F.
- **DLSS DLL updater:** FFXIV ships DLSS 3.5.10. One click downloads NVIDIA's latest `nvngx_dlss.dll` from NVIDIA's official DLSS SDK repository. It is checked against GitHub's hash and NVIDIA's signature, and the old DLL is kept as a `.bak`.
- **Sharpness slider:** AMD CAS runs on the DLSS output, before the UI. DLSS has ignored its own sharpness setting since 2.5.1.
- **NVIDIA Reflex:** low-latency mode (On / On + Boost) and a precise driver frame cap, through NVAPI. FFXIV has no Reflex of its own.
- **Correct refresh rate:** the game reads the refresh rate once at startup, from the primary monitor. The plugin gives it the rate of the monitor its window is actually on.
- **Game settings check:** reads the game's own graphics options and tells you what to change, in the window and in chat.
- **Debug tab:** loaded DLL versions, a per-frame flash/flicker detector and frame-pacing stats, for checking changes with numbers instead of by eye.

Works alongside RenoDX's DLSS 5 (Neural Rendering) addon. See [DLSS 5 and G-Sync](#dlss-5-and-g-sync).

## Install (dev plugin)

1. Build it (see [Build](#build)), or take `XivDlssFix.dll`, `XivDlssFix.json` and `XivDlssFix.deps.json` from `bin\Release\`.
2. Copy them to a folder such as `%APPDATA%\XIVLauncher\devPlugins\XivDlssFix\`.
3. In Dalamud: Settings > Experimental > Dev Plugin Locations, add that folder, then enable **DLSS Fix** under `/xlplugins` > Dev Tools.
4. In the game: System Configuration > Graphics Settings > Resolution > **Graphics Upscaling = NVIDIA DLSS**. The plugin does nothing with FSR, and it tells you so.

## Window

`/dlssfix` opens it.

**Settings tab:**
- enable, DLSS mode, resolution lock, model preset, sharpness, chat notices
- under "Latency & frame pacing": NVIDIA Reflex, frame cap (with a one-click G-Sync cap for your monitor), and "Use the refresh rate of the monitor the game is on"
- the game settings checklist and the current render resolution

**Debug tab:**
- flash detector, "Reapply now" (rebuilds the render targets; the screen blinks once), "Reset DLSS history"
- RTM range, evaluate rate, loaded DLLs, RenoDX DLSS 5 state
- monitors and refresh rates, the game's limiter mode, swap chain and presentation mode (composed vs independent flip), present pacing, Reflex status (including whether the driver sees G-Sync) and latency
- the log path

## Commands

| Command | What it does |
|---|---|
| `/dlssfix` | open the window |
| `/dlssfix status` | real DLSS input/output size, loaded DLLs, flash count, game settings to change |
| `/dlssfix mode dlaa\|quality\|balanced\|performance\|ultra\|custom\|game` | DLSS mode (`game` = leave the game's dynamic resolution alone, observe only) |
| `/dlssfix custom 75` | custom scale, 33–100% |
| `/dlssfix preset default\|k\|j\|m\|l\|f\|game` | DLSS model preset |
| `/dlssfix sharp 35` | CAS sharpness in % (0 = off) |
| `/dlssfix lock on\|off` | resolution lock |
| `/dlssfix dll` | show the game folder's `nvngx_dlss.dll` version and check NVIDIA's latest release |
| `/dlssfix dll update` | download, verify and install NVIDIA's latest `nvngx_dlss.dll` (takes effect on the next game start) |
| `/dlssfix reflex off\|on\|boost` | NVIDIA Reflex low-latency mode |
| `/dlssfix cap 80` / `cap gsync` / `cap off` | Reflex frame cap in fps (`gsync` = NVIDIA's G-Sync cap for the monitor the game is on) |
| `/dlssfix test [seconds]` / `test stop` | unattended G-Sync test: caps 60 / 100 / 116 twice (20 s each by default) while the camera turns at a constant 29°/s. It logs one `TEST step` line per step, then restores the cap. Keep the game focused and don't touch the mouse. |
| `/dlssfix on\|off` | enable / disable the plugin |
| `/dlssfix apply` | rebuild the render targets now |
| `/dlssfix reset` | reset DLSS / DLSS 5 history on the next frame (diagnostic) |
| `/dlssfix flash on\|off` | flash detector |
| `/dlssfix mv game\|render` | motion vector scale (diagnostic; keep `game`) |

## Game settings check

For each game option that matters, the window shows `[OK]`, `[CHANGE]`, `[RECOMMENDED]` or `[INFO]`. Option names are the English client's. Chat repeats this once after login and whenever one of them changes.

| Option | Where | What the plugin wants |
|---|---|---|
| Graphics Upscaling | System Configuration > Graphics Settings > Resolution | **NVIDIA DLSS** (required) |
| Frame Rate Threshold | same | anything while the lock is on. Without the lock it decides when the game lowers the resolution. |
| 3D Resolution Scaling | same | ignored with DLSS |
| Radial Blur | System Configuration > Graphics Settings > Effects | **off** (recommended; it blurs the image when the camera moves) |

## How it works

Reverse engineered from `ffxiv_dx11.exe` 2026.10.16. Offsets are relative to the module base and will move with game patches.

| Where | What |
|---|---|
| `GraphicsConfig+0x54` | upscaler: 1 = FSR, 2 = DLSS. With DLSS the game **forces** dynamic resolution and ignores the menu option. |
| `GraphicsConfig+0x55` | hidden DLSS mode: 0 auto, 1 DLAA, 2 Ultra Quality, 3 Quality, 4 Balanced, 5 Performance, 6 Ultra Performance. "Auto" at 4K = Performance. |
| `GraphicsConfig+0x49` | Frame Rate Threshold: 0 Always Enabled, 1 Below 30 fps, 2 Below 60 fps. Read at `+0x2DA946`; it only sets the dynamic-resolution controller's FPS target. |
| `DlssManager::SelectMode` (`+0x374590`) | asks NGX for the optimal / dynamic min / dynamic max size of the mode |
| callback at `RenderTargetManager+0x730` (`+0x352350`) | hands that range (min..max) to the resolution controller |
| controller (`+0x2DA5B0`, fields `RTM+0x700..0x714`) | steps the height by 9/18 lines between min and max from GPU time, **only while the camera moves** |
| `DlssManager::CreateFeature` (`+0x374CB0`) | creates the DLSS feature with preset C, flags `MVLowRes\|DepthInverted` |
| DLSS evaluate args | `+0x08` output texture, `+0x30` motion vector scale, `+0x48` render sub-rectangle, `+0x50` reset flag |
| `Device+0x9F0` ("FrameRate") | refresh rate, written **once** at device creation (`+0x220C90`): `GetDeviceCaps(GetDC(hwnd), VREFRESH)`, 59 or <10 → 60. Never updated afterwards. |
| `Kernel::SwapChain::Present` (`+0x21B600`) | the frame limiter. `Device+0xA4` = Present sync interval (0 none, 1 V-Sync, 2–4 every Nth refresh). Mode 1 adds a `Sleep` cap to `Device+0xAC` fps. Flags always 0. |

The fix:

1. Before `SelectMode`, write the chosen mode to `GraphicsConfig+0x55`. Custom uses the game's own scale path at `DlssManager+0x180`.
2. In the range callback, return **min = max = optimal**, so the dynamic controller has nowhere to go.
3. On feature creation, replace the preset.
4. Request a render-target rebuild (`Device+0x7A`, the same flag the game uses on resize) when settings change, after login, or when you switch FSR → DLSS.
5. After each evaluate, run the CAS and probe compute passes on the DLSS output texture. They run on the game's D3D11 immediate context and save and restore the compute-shader state.

## DLSS 5 and G-Sync

If you use RenoDX's DLSS 5 addon (`renodx-dlss5.addon64` + `nvngx_dlssnr.dll`) with G-Sync, the image can flicker **only while the camera moves**. That flicker is G-Sync (VRR), not the rendering:

- Without DLSS 5 the game sits at its frame cap, so G-Sync has nothing to vary. DLSS 5 costs ~7 ms of GPU time per frame on an RTX 4090 at 4K, which pulls the game below the cap (~87 fps). The refresh rate then follows the frame times, and the panel's brightness changes with the refresh rate. This is the same VRR flicker you sometimes see on Windows desktop windows.
- The rendered image is the same in the flickering and the smooth state: local flicker normalized by motion was ~640 per 1000 tiles in both. The flicker happens on the display.
- Alt-tab stopped it because windowed G-Sync disengaged. "Reapply now" brought it back because rebuilding the buffers re-engaged G-Sync.
- It kept flickering even with very even frames (11.1–12.3 ms, ~1% swings >15%). So the variation comes from how the DLSS 5 path (D3D11 → D3D12 bridge) presents frames, not only from the game's frame times.

**Fix:** NVIDIA Control Panel > Manage 3D settings > Program Settings > `ffxiv_dx11.exe` > Monitor Technology: **Fixed Refresh**. That gave zero flicker at 75–92 fps. Other games keep G-Sync.

Trade-off: possible tearing with V-Sync off, or slight judder with V-Sync on below the refresh rate. For perfectly even frames, use V-Sync on + a 60 fps cap.

FFXIV's own frame limiter is no help here. It works in vblank-sized ticks: at a cap of 60, a frame that misses 16.7 ms waits until 33.3 ms. Frame times then alternate 16.7/33.3 ms (~51 fps while moving) and G-Sync alternates 60/30 Hz. A driver cap (NVIDIA Max Frame Rate, RTSS) spaces frames without skipping ticks.

RenoDX settings that mattered (`ReShade.ini`, `[RenoDX.DLSS5]`):

- `NRSourceEncoding=1`: FFXIV hands DLSS SDR color that is already tonemapped, in a float buffer. As linear HDR, the per-frame brightness normalizer pumped.
- `NRLinearUnitNits` only applies to linear sources. `NRFeedMode=2` falls back to v1, because FFXIV passes no exposure texture.
- Keep `NRNormTrace=0` and `NREditTrace=0` unless you're debugging; the edit trace writes a CSV every frame.

## Updating nvngx_dlss.dll

FFXIV ships DLSS 3.5.10 and doesn't update it. NVIDIA publishes the DLSS runtime in its DLSS SDK repository, [github.com/NVIDIA/DLSS](https://github.com/NVIDIA/DLSS), at `lib/Windows_x86_64/rel/nvngx_dlss.dll` for each release tag.

**How it works:**
- **Check** ("Check for update" in the Settings tab, or `/dlssfix dll`): reads the latest release tag and that file's size and git blob SHA-1 from the GitHub API. Nothing is downloaded, and the plugin never contacts GitHub unless you ask.
- **Install** ("Download and install", or `/dlssfix dll update`):
  1. downloads the file from `raw.githubusercontent.com/NVIDIA/DLSS/<tag>/...`
  2. verifies the size and the git blob hash against the API, a valid Authenticode signature (`WinVerifyTrust`) from **NVIDIA Corporation**, and that the file version matches the tag; it installs nothing if any check fails
  3. renames the current DLL to `nvngx_dlss.dll.<version>.bak` (Windows allows renaming a loaded DLL, but not overwriting it)
  4. moves the new one into place
  
  The game loads it on the next start.
- **Revert:** delete `nvngx_dlss.dll` and rename the `.bak` back. Game patches or a file repair may also restore 3.5.10; check again afterwards.
- **NVIDIA App DLSS override:** if it is on for the game, the driver supplies its own DLL and the game-folder copy isn't used. The window says so.

Tested end to end on 2026-11-04 (in a scratch folder with a copy of the game's 3.5.10):
- 3.5.10 → 310.9.16: 56 MB, hash and signature OK, an unsigned file rejected
- renaming a DLL while it's mapped as an image works; overwriting it is blocked

## Frame pacing, refresh rate and Reflex

### What the game does (`ffxiv_dx11.exe` 2026.10.16)

FFXIV has no V-Sync option because **its frame-rate limit is V-Sync**. `Kernel::SwapChain::Present` (`+0x21B600`) calls `IDXGISwapChain::Present(syncInterval, 0)`, and the sync interval comes straight from the limit setting (`Device+0xA4`):

| `Device+0xA4` | Present sync interval | effect |
|---|---|---|
| 0 | 0 | no limit, no V-Sync |
| 1 | 1 | V-Sync on every refresh, plus a `Sleep` cap to `Device+0xAC` fps when the refresh rate is higher (1 ms granularity, 1–2 ms slack) |
| 2 / 3 / 4 | 2 / 3 / 4 | V-Sync every 2nd / 3rd / 4th refresh |

- **Why the limiter quantizes:** a frame can only last a whole number of refreshes. At 1/2 of 120 Hz, a frame that misses 16.7 ms waits until 33.3 ms. At 1/1 with DLSS 5 the frames alternated 8.75 / 17.5 ms. Both match what the test logs showed.
- **Tearing:** the game never passes `DXGI_PRESENT_ALLOW_TEARING`.
- **Where the refresh rate comes from:** the game reads it once, when the device is created: `GetDeviceCaps(GetDC(hwnd), VREFRESH)` into `Device+0x9F0`. A screen DC reports the **primary** monitor. The value is never refreshed, not when the window moves to another monitor and not when the refresh rate changes.
- **What uses that value:** the limiter (in windowed / borderless) and the dynamic-resolution FPS target (`+0x2DA8FD`).
- **The two-monitor stutter:** on a setup with mixed refresh rates and the game off the primary monitor, both of those work against the wrong refresh rate.
- **Exclusive fullscreen:** the limiter uses the selected display mode's refresh rate instead.

### What the plugin does

- **"Use the refresh rate of the monitor the game is on"** (on by default) checks once a second which monitor the window is on, and writes that monitor's refresh rate to `Device+0x9F0` when it differs. Turning it off restores the value the game read. The checklist flags a mismatch.
- **NVIDIA Reflex** via NVAPI (`nvapi64.dll`, D3D11). It calls:
  - `NvAPI_D3D_SetSleepMode` when the settings change
  - `NvAPI_D3D_Sleep` plus a `SIMULATION_START` marker at the start of every framework tick, before the game samples input
  - `PRESENT_START` / `PRESENT_END` markers around the game's Present
- **Frame cap** (`minimumIntervalUs`): an evenly spaced driver cap. The "G-Sync" button sets NVIDIA's own formula, `3600·Hz / (Hz + 3600)` (116 fps at 120 Hz). With a cap, set the game's frame rate limit to None, or the game's V-Sync steps still apply; the checklist recommends it.
- **Diagnostics:**
  - which monitor the game is on and every monitor's refresh rate
  - the game's limiter mode
  - swap chain model and flags, and the presentation mode: COMPOSED means DWM copies the frame, INDEPENDENT FLIP is direct
  - present-to-present pacing, and time spent inside the game's limiter + Present
  - Reflex sleep status: `bFsVrr` (G-Sync in **exclusive fullscreen** only; always False in borderless), control-panel V-Sync and iFlip
  - Reflex latency (simulation start → GPU done)
  - **G-Sync, read from the driver** (read-only, never changes NVIDIA settings):
    - live: `NvAPI_Disp_GetVRRInfo` on the game's monitor, once a second on a worker thread (the call takes 5–10 ms), plus whether the game window has focus (windowed G-Sync only runs on the focused window)
    - saved: the G-Sync fields of the game's driver profile (`NvAPI_DRS_*`), every 15 s on a worker thread: "G-SYNC" (what NVIDIA Control Panel calls Monitor Technology), the hidden "Application Mode" and "Application Requested State", and the global mode
    - the checklist shows one "G-Sync" line and flags the hidden-fields case below

### First in-game test (2026-11-04, 4K, 120 Hz, single monitor)

- **Reflex works.**
  - The driver reports `lowLatency=True gameSleep=True` (it sees the plugin's `NvAPI_D3D_Sleep` every frame) and `status=0`.
  - The 116 fps cap gives presents at p50 = 8.62 ms with p05–p99 = 8.4–9.2 ms and 0–1% swings. Uncapped it ran ~205 fps (4.8 ms).
- **Latency reports were empty** with only `SIMULATION_START` + `PRESENT_*` (`GetLatency=0 (0 reports)`). The plugin now also sends `SIMULATION_END` and `RENDERSUBMIT_START/END`.
- **Limiter mapping:** game config `Fps=0` ↔ `Device+0xA4 = 0` (None), confirmed. The game read 120 Hz, which is correct on one monitor.
- **The swap chain is the legacy blit model:** `BLT_DISCARD`, 2 buffers, `ALLOW_MODE_SWITCH`, windowed.
  - In borderless, Windows composites every frame: the driver reports `iFlip=False`, and `GetFrameStatisticsMedia` is unsupported (`0x887A0004`).
  - G-Sync in borderless then only works through NVIDIA's windowed-G-Sync path, the most likely home of the DLSS 5 flicker.
  - Windows 11's **Optimizations for windowed games** upgrades blit-model DX11 swap chains to flip model, which allows independent flip. The checklist shows the setting (read from the registry) and whether the driver sees independent flip.
- **DLSS 5:** ~86 fps. The game's Present takes ~7 ms (the D3D11 → D3D12 bridge waits for the NR work inside Present). When DLSS 5 can't hold the cap, pacing swung up to 20% (10.4 / 12 ms frames). That is exactly what G-Sync followed; a cap DLSS 5 can hold (e.g. 80) keeps it flat.

### Testing G-Sync again with DLSS 5

0. Windows 11: Settings > System > Display > Graphics > **Optimizations for windowed games: On**. Restart the game, then check the Swap chain line in the checklist: "driver sees independent flip: yes".
1. Game: frame rate limit **None**. Plugin: Reflex **On**, frame cap at a rate DLSS 5 always holds (e.g. 80), or the G-Sync cap if it can.
2. NVIDIA Control Panel: `ffxiv_dx11.exe` Monitor Technology back to **G-SYNC**, V-Sync **On** (driver). Then check the checklist's "G-Sync" line: if it says "off for this game (hidden profile fields)", click **Restore** for the game in Program Settings (see below) and restart the game.
3. Move the camera with DLSS 5 on (`NR (RenoDX): state=ENGAGED`) and compare the log lines:
   - `GSYNC:` `active=True` with `game window focused=True`. This is the proof G-Sync is running; `REFLEX gsync(fsVrr)` stays False in borderless.
   - `PRESENT`: p05–p99 should be flat and swings near 0%.
   - `SWAPCHAIN`: the presentation mode.
4. If it still flickers with flat presents, the variation is after Present, in the DLSS 5 D3D11 → D3D12 bridge or in DWM. Fixed Refresh stays the answer.

### G-Sync check (2026-11-04): Control Panel said G-SYNC, the driver kept it off

After switching Monitor Technology from Fixed Refresh back to G-SYNC, there was no flicker at all. The driver showed why:

- **Live:** `NvAPI_Disp_GetVRRInfo` on `\\.\DISPLAY1` with FFXIV focused: `active=False requested=False possible=True displayInVrrMode=True`. The monitor was ready for G-Sync, but nothing requested it.
- **Driver profile** ("Final Fantasy XIV: A Realm Reborn", which holds `ffxiv_dx11.exe`):
  - "G-SYNC" (Monitor Technology): not in the profile, so it inherits **allow** from global. That's what the Control Panel shows.
  - "Enable G-SYNC globally" / "G-SYNC - Application Mode" (`0x1194F158`) = **Off**, user-set in the game profile
  - "VRR requested state" / "G-SYNC - Application Requested State" (`0x1094F1F7`) = **Off**, user-set in the game profile
  - global: both full screen and windowed
- **Why:** the Control Panel doesn't show the two hidden fields and doesn't reset them when Monitor Technology goes from Fixed Refresh back to G-SYNC. This is a known Control Panel behavior ([Blur Busters forum](https://forums.blurbusters.com/viewtopic.php?p=108432)).
- **The other user-set entries** in the profile were Power management mode (prefer maximum performance), Vertical Sync (use the 3D application setting), Vertical Sync Tear Control, Preferred refresh rate (application-controlled) and the SLI smooth AFR flag. A Restore resets these too.
- **Fix:** NVIDIA Control Panel > Manage 3D settings > Program Settings > Final Fantasy XIV > **Restore**, or set both fields to "Fullscreen and Windowed" in NVIDIA Profile Inspector (Sync and Refresh). Then restart the game and check `GSYNC: ... active=True`.
- **What the no-flicker session also had:** DLSS 5 ran at a flat 116 fps from 12:55 to 13:07, then was off (F6) from 13:08. G-Sync was off the whole time, so that session didn't test DLSS 5 + G-Sync at all.

### With G-Sync really on (2026-11-04, after Restore)

- **What the user saw:** flicker with DLSS 5 on (116 and 100 fps caps) and off. It goes away on alt-tab (the driver drops windowed G-Sync, `GSYNC: active=False`) and comes back as soon as G-Sync re-engages, 2–4 s after the game regains focus. So the flicker follows G-Sync being live.
- **A plugin bug was in those tests.** Plugin 1.4.0 called `NvAPI_Disp_GetVRRInfo` on the framework thread once a second. That call takes **5–10 ms** (measured: p50 6.3 ms, max 10 ms). So every second, one frame came 5–10 ms late:

  | | `PRESENT` swings >15% | p99 |
  |---|---|---|
  | 1.3.0, G-Sync off, DLSS 5 on, 116 cap | median 0% | median 9.05 ms |
  | 1.4.0, any state | 2.6–8.5% | 9.2–15 ms |

  With G-Sync live, each of those frames is a refresh-rate dip. 1.4.1 polls on a worker thread. A test showed a slow NVAPI call on one thread doesn't block NVAPI calls on another. The `PRESENT` line now also prints the plugin's own per-second cost on the game thread.
- **Retest with 1.4.1 (13:43–13:45, G-Sync live, 116 cap):** "basically solved", with an occasional flicker still seen.

  | | `PRESENT` swings >15% | p99 | plugin game-thread cost per second |
  |---|---|---|---|
  | 1.4.0, DLSS 5 off | 3.2–7.2% | 11.3–14.9 ms | 5–10 ms |
  | 1.4.1, DLSS 5 off | 1.4–2.2% | 9.3–9.6 ms | 0.07–0.18 ms |
  | 1.4.1, DLSS 5 on | 0.2–2.1% | 9.4–9.7 ms | 0.07–0.09 ms |

  The G-Sync poll takes 4–9 ms, now on a worker thread.
- **The leftover flicker:** in two of the three DLSS 5 windows, frames ran at p50 8.82–8.92 ms (~113 fps), and Reflex latency (sim start → GPU done) rose from ~5 to ~23 ms. So DLSS 5 couldn't hold 116 there; the GPU was the bottleneck and frames queued. G-Sync follows that small drift. A cap DLSS 5 always holds (e.g. 110) should remove it and cut that latency. The earlier 100 fps test doesn't count: it ran on 1.4.0, with the hitch.
- **The display:** a **Samsung QN90D** (EDID `SAM7572`, 43"), a VA mini-LED TV. VRR flicker on this series is widely reported by owners and tied to **local dimming** ([Samsung community](https://eu.community.samsung.com/t5/tv/qn90d-50-inch-possible-qa-fault-problem/m-p/11711787/highlight/true), [QN90B thread](https://eu.community.samsung.com/t5/tv/qn90b-vrr-flicker-after-update-1403-6/td-p/6329585)). If 1.4.1 still flickers with flat presents, try Local Dimming Low or Off in Game Mode and the latest firmware. Otherwise, Fixed Refresh for the game is the known flicker-free setting.

### `/dlssfix test` (2026-11-04 13:57 and 14:00): the game's frames are even, DWM's refreshes are not

Two runs: DLSS 5 off, then on. G-Sync was active and the game focused 100% of every step. Swings are refresh-to-refresh changes >15%.

| cap | DLSS 5 | game frames: p05–p99, swings | DWM refreshes: p05 / p50 / p95, swings | user |
|---|---|---|---|---|
| 60 | off | 16.66–16.73 ms, 0.0% | 12.4 / 16.7 / 20.8 ms, **62–63%** | bad |
| 60 | on | 16.66–16.73 ms, 0.0% | 12.4 / 16.7 / 20.8 ms, **55–63%** | bad |
| 100 | off | 10.00–10.07 ms, 0.0% | 9.15 / 10.0 / 10.9 ms, 10–11% | better |
| 100 | on | 10.00–10.07 ms, 0.0% | 9.1 / 10.0 / 12.5 ms, **29–32%** | worse than off |
| 116 | off | 8.62–8.70 ms, 0.0% | 0.7 / 8.6 / 16.8 ms, 21–27% (134–175 gaps of 16.8 ms per 18 s) | |
| 116 | on | 8.61–8.98 ms, 0.2–0.3% | 8.45 / 8.6 / 8.75 ms, 6–9% (40–53 gaps of 16.8 ms per 18 s) | "once in a while" |

- **What the numbers show:** the game hands over perfectly even frames (0.0% swings at every cap). The Windows compositor (DWM) shows each of them, with one composition per game frame, but at uneven times.
  - At 60 fps the refresh interval jumps between ~12.5, 16.7 and 20.8 ms, i.e. 16.7 ± 4.2 ms, half a 120 Hz period. That is a 48–80 Hz swing every frame.
  - At 116 the refreshes run on time, except for 16.8 ms gaps followed by a double refresh.
  - With windowed G-Sync, the TV refreshes when DWM presents. So these uneven times are what G-Sync follows, and on this VA TV the brightness follows the refresh interval. The user's ranking (60 bad, 100 better, DLSS 5 worse at 100, 116 almost clean) matches the DWM swing column, not the game column.
- **Why other games don't do this:** they present with the flip model (all D3D12, most modern D3D11) or in exclusive fullscreen, so the GPU flips their frames straight to the screen (independent flip), and G-Sync's refreshes are the game's frames. FFXIV's D3D11 swap chain is the legacy blit model (`BLT_DISCARD`) in a borderless window: every frame is copied into DWM's composition, and DWM's own presentation timing sits between the game and G-Sync.
- **Caveat:** `DwmGetCompositionTimingInfo` reports DWM's view. Intervals under 8.3 ms can't be real refreshes on a 120 Hz panel, so the exact scanout times may differ. The jitter between DWM and the game's even frames is still measured directly.
- **Repeat at 14:13 and 14:16 (1.5.1, display confirmed HDR10):** same pattern.

  | cap | DWM swings, DLSS 5 on | DWM swings, DLSS 5 off |
  |---|---|---|
  | 60 | 57–60% | 61–65% |
  | 100 | 26–27% | 12–27% |
  | 116 | 4–9% | 24–29% (154–184 gaps of 16.8 ms per 18 s) |

  The game's frames stayed at 0.0–1.1% swings throughout. The earlier "no flicker" moment (14:07–14:09) was at 116 with DLSS 5 on, which is the lowest DWM jitter here. Windows HDR was on in every run. Optimizations for windowed games was already on globally.
- **Next:** put FFXIV on the direct path and run `/dlssfix test` again:
  1. **Windows HDR off** (Win+Alt+B): the SDR swap chain then needs no HDR conversion in DWM, so the flip upgrade can go to independent flip. The TEST line shows `display SDR`.
  2. FFXIV **exclusive fullscreen** (System Configuration > Display Settings > Screen Mode: Full Screen): no DWM at all.

### Exclusive fullscreen (2026-11-04 14:47, 4K 119.88 Hz, DLSS 5 on)

FFXIV's own menu didn't list 3840x2160 for full screen, though DXGI and GDI both offer it at 120 / 119.88 / 144 / 100 / 60 Hz. It was set directly in `FFXIV.cfg`: `ScreenMode 1`, `FullScreenWidth 3840`, `FullScreenHeight 2160`, `Refreshrate 120`. The game accepted it.

| cap | game frames: p05–p99, swings | refresh measured by DXGI | DWM compositions (per 18 s) |
|---|---|---|---|
| 60 | 16.66–16.73 ms, 0.0% | 60.5–61.5 Hz | 0–67 |
| 100 | 10.00–10.08 ms, 0.0% | 100.0 Hz | 0 |
| 116 | 8.62–8.78 ms, 0.0% | 116.0 Hz | 2 |

- **G-Sync on the fullscreen path:** `gsync(fsVrr)=True iFlip=True` the whole time.
- **The compositor is out of the path.** The TV refreshes once per game frame, at the cap's exact rate. At 60 there are a couple of extra refreshes per second.
- **This is the steadiest refresh the setup can produce.** Any flicker left here is not refresh-rate variation.
- **Alt-tab drops the game back to windowed** (`SWAPCHAIN ... windowed`), and returning re-enters fullscreen.

### PresentMon (2026-11-04 15:03, borderless, during `/dlssfix test` step 1: cap 60, G-Sync active, DLSS 5 on)

- **PresentMode = `Composed: Copy with GPU GDI` for all 1219 frames.** Windows' "Optimizations for windowed games" doesn't upgrade FFXIV's swap chain to flip model, although it's on. So in borderless every frame is a GPU copy into DWM's composition.
- **Display timing from ETW (`MsBetweenDisplayChange`):** p05 15.04 / p50 16.65 / p95 17.63 / p99 18.78 ms, 4.0% swings. 90% of intervals are within 15.5–18 ms; about 10% are displaced (50 at 7.5–12.5 ms, 35 at 12.5–15.5, 35 at 18–25, 4 over 25).
- **Present timing:** p05 14.61 / p50 16.66 / p95 17.06 ms, 1.7% swings.
- **Correction:** `DwmGetCompositionTimingInfo`, which the TEST lines use, overstates the jitter. The same 20 s showed 59% swings there. Its vblank/compose times under windowed VRR are DWM's bookkeeping, not scanout. The ranking between caps held, but PresentMon's numbers are the real display timing.
- **So:** borderless still displaces about 1 in 10 frames at 60 fps, while exclusive fullscreen shows the refresh exactly at the cap. Each displaced frame is a short refresh-rate change that the VA TV shows as a brightness blip.

## Findings (2026-11-04 test rounds)

- **Lock works:** `DLSS in=2227x1253 -> 3840x2160 STABLE` while zooming. Without it, Balanced at 4K moved between 1080 and 2160 lines.
- **Motion vector scale:** the game passes the display size even when rendering below 100%. A/B testing showed the game's value is correct (`render` blurs).
- **History reset:** the game resets DLSS history only on the first evaluate after creating the feature, never on camera cuts. Forcing a reset (`/dlssfix reset`) reached DLSS and DLSS 5 but didn't change the flicker.
- **Sharpness "blink on apply":** caused by jumping 0↔100% instantly. Strength now eases at 3 units/s, and the shader compiles on a worker thread when the plugin loads.
- **Random flashes:** each one was a "Reapply now" rebuild (0.56 stop full-screen blink on the rebuild frame). Normal camera motion stays under 0.09 stop per frame.
- **Ruled out for the DLSS 5 flicker:** the brightness normalizer (divisor fixed at 1.000), NR history drift, RenoDX detail stability, and motion vector scale (it flickered in DLAA too). The cause was G-Sync, as above.

## Logs

- Plugin: `%APPDATA%\XIVLauncher\pluginConfigs\XivDlssFix\dlssfix.log`
  - `STATS ...: DLSS in=WxH [min, max, changes N] -> out=WxH | scale=% | STABLE/VARYING` every 5 s. This comes from the DLSS evaluate hook, so it is the real size going into DLSS.
  - `SelectMode`, `RTM range ... LOCKED`, `CreateFeature ... preset X -> Y`, and the reason for every rebuild.
  - With the flash detector on:
    - `FLASH:` short light/dark blinks (≥0.08 stop that come back within 1–3 frames)
    - `HITCH:` long frames
    - `LOCAL flicker:` reversing tiles in a 64×36 grid, per 1000 tile-frames
    - `FRAMES (pacing seen by G-Sync):` frame-time p05/p50/p95/p99 and % of frames that swing >15%
  - Frame pacing:
    - `DISPLAY:` monitors, the game's refresh value and limiter mode, and game config `Fps` / `FPSInActive` / `ScreenMode`. Written when something changes.
    - `refresh rate: game value X Hz -> Y Hz` when the plugin corrects it
    - `SWAPCHAIN:` swap effect, buffers, flags, output and presentation mode. Written when something changes.
    - `PRESENT:` present-to-present p05/p50/p95/p99, swings, time inside the game's limiter + Present, sync interval and measured vblank rate. Every 5 s.
    - `REFLEX:` mode, cap, driver sleep status (`gsync(fsVrr)` = exclusive fullscreen G-Sync only, `cplVsync`, `iFlip`, `gameSleep`, `frameGen`) and latency. Every 5 s while Reflex or the cap is on.
    - `GSYNC:` live VRR state of the game's monitor (`active`, `requested`, `possible`, `displayInVrrMode`, `indicator`), whether the game window has focus, and the G-Sync fields of the game's driver profile. Written when something changes.
    - `TEST step N/M cap=X`, during `/dlssfix test`: focus %, G-Sync active %, DLSS 5 state, then three interval distributions with p05–p99, swings and a histogram:
      - `game frames`: framework ticks
      - `DWM refreshes`: `DwmGetCompositionTimingInfo` vblanks, i.e. what the compositor actually refreshed
      - `DWM compositions`
    - `present thread id=..., framework thread id=...` once, to show whether presenting runs on its own thread
- DLSS 5 (RenoDX): `ReShade.log` in the game folder (`DLSS5 Generic`, `NR-VERDICT`, `frame contract`). The plugin reads it incrementally on a worker thread for the Debug tab.

## Next steps

### Frame generation

Wanted most with DLSS 5, since DLSS 5 takes the game down to ~75–90 fps at 4K.

- **NVIDIA DLSS Frame Generation can't be added in-process.** Streamline's DLSS-G needs DirectX 12, and FFXIV is DX11. Same for AMD's FSR 3 frame generation: the FidelityFX SDK ships DX12/Vulkan backends only.
- **Available now: NVIDIA Smooth Motion.** It is driver-level frame generation for DX11/DX12 games and has officially supported RTX 40 since August 2025. Turn it on in the NVIDIA App > Graphics > Program Settings > FFXIV > Smooth Motion. To measure: frame pacing with Fixed Refresh + V-Sync (e.g. a 60 fps base → 120 Hz), UI ghosting (Smooth Motion interpolates the final image, UI included), and whether it works with ReShade/RenoDX hooking DXGI.
- **In-plugin option (big project):** the plugin already has everything frame generation needs from the DLSS evaluate call: color before the UI, depth, motion vectors, jitter and the render sub-rectangle. A real implementation would have to:
  - share those textures with a D3D12 device
  - run FSR 3.1 / XeFG frame interpolation there
  - present through a D3D12 swap chain that replaces the game's D3D11 one, while keeping Dalamud's ImGui, ReShade and RenoDX working on top
  
  This is what OptiScaler does for DX12 games; its OptiFG is DX12-only today. Investigate only if Smooth Motion falls short.

### Refresh rate, frame limiter and Reflex: what's left

The investigation and the first fix are done (see [Frame pacing, refresh rate and Reflex](#frame-pacing-refresh-rate-and-reflex)). Still open:

- **Verify in-game:**
  - the `DISPLAY` line against the real monitors, including the game's config `Fps` values ↔ `Device+0xA4` (inferred from code, not seen at runtime yet)
  - the present thread vs. the framework thread, which decides how Reflex frame IDs pair up
  - whether Reflex's sleep works through ReShade + the RenoDX DLSS 5 bridge
- **G-Sync + DLSS 5 retest** with a flat Reflex cap, following the steps above.
- **Possibly:** let the plugin bypass the game's V-Sync limiter while the Reflex cap is on (pass sync interval 0 to the original Present), so players don't have to change the game option.
- **Possibly:** `SIMULATION_END` / `RENDERSUBMIT_*` markers from game hooks, for complete latency reports in the NVIDIA overlay.

Reflex is also a prerequisite for frame generation feeling responsive.

## Revert

- Plugin: disable it in `/xlplugins` (Dev Tools), or `/dlssfix off`.
- DLSS 5: delete `dxgi.dll`, `renodx-dlss5.addon64` and `nvngx_dlssnr.dll` from the game folder.
- If you replaced `nvngx_dlss.dll` with a newer one, restore your backup. Game patches can also put the original 3.5.10 back. The Debug tab shows the version that's actually loaded.

## Build

```
dotnet build -c Release
```

Needs the .NET 10 SDK and `Dalamud.NET.Sdk/15.0.0`. It builds against the Dalamud in `%APPDATA%\XIVLauncher\addon\Hooks\dev`. Output: `bin\Release\`.
