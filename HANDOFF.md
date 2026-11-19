# Handoff: AutoExile init failure (2026-11-19)

## Get the changes onto the HUD machine first
The fixes below are on branch `fix/fear-encounter-and-init` of https://github.com/letaicho/AutoExile.
The plugin source on the HUD machine is at `C:\EAPI\ExileApi-Compiled-330.9\Plugins\Source\AutoExile\`.
Before doing anything else, get these two files there, either with a git checkout of the branch or by downloading them:

- `Modes/BossEncounters/FearEncounter.cs`
- `BotCore.cs`

## Done
1. **FearEncounter.cs compile error (CS1513 `} expected` at 1,244): fixed.**
   Upstream commit `33c7421` replaced every newline in the file with a space, so the first `///` comment swallowed the rest of the file.
   The line breaks and indentation were restored. The result was checked token-for-token against `33c7421`, and no code changed.

2. **NullReferenceException spam + "Web server not created": diagnosed, not yet fixed.**
   - Stack traces: `BotCore.Tick()` line 503 (`_ctx.DeltaTime`) and `BotCore.Render()` line 838 (`_ctx.Graphics = Graphics`). `_ctx` is null.
   - `_ctx` is assigned near the top of `Initialise()`, and the web server is created at the end of it. Both being null means `Initialise()` threw before the `_ctx = new BotContext {...}` line. The WebUiEnabled setting is not the problem.
   - Code that runs before that line: `_recorder.SetOutputDir`, `_mavenRecorder.Initialize`, `_humanRecorder.Initialize` (calls `Directory.CreateDirectory`), `_ninjaPrice.Initialize`, and `new MapDatabase` + `Initialize` (`CreateDirectory`, `File.Copy`; `Load()` catches its own errors).
   - Suspects: an IO/permission failure creating `Recordings\Human` or `Data` under the plugin folder, or a type/assembly load failure. A load failure makes the whole method fail before its first line runs.

3. **Diagnostics added to BotCore.cs (dev build: 0 errors):**
   - `Initialise()` now wraps a new `InitialiseCore()` in a try/catch. The full exception goes to the log via `LogError` and is stored in `_initError`.
   - `Tick`, `Render` and `AreaChange` return early when `_initError != null`.
   - The settings panel shows the full error text and has a **Copy error** button. The web-server status line points to it.

## Next steps
1. Get the two files onto the HUD machine (see above), then reload the plugin in HUD.
2. Open the AutoExile settings panel and copy the error. It is also in the HUD `Logs` folder (search for `Initialise failed`).
3. Fix the root cause shown in that stack trace. Confirm that the web dashboard URL appears and that the null-reference spam has stopped.
4. Optional: once fixed, decide whether to keep the try/catch wrapper. It's cheap and makes future init failures visible, so keeping it is recommended.

## Notes
- HUD compiles the plugin from source. Embedded resources (`uniqueArtMapping.json`, `wwwroot/index.html`) are loaded lazily, so they are not the cause of the init failure. If the dashboard loads blank after the fix, check whether HUD's compiler embeds them.
