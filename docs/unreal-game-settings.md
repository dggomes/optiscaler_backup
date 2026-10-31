# Read-only Unreal and OptiScaler settings extraction

Available from **v0.3.3**. It reports saved graphics preferences from approved/captured Unreal-style INIs; it does not edit settings, inspect a running game, choose an effective configuration layer or prove which upscaler is active. File parsing is local. Backup schema 2 remains unchanged.

The game upscaling/frame-generation and `OptiScaler.ini` extension below is available from **v0.3.4**. Its additional fields, `Scope` classification and separate OptiScaler tab build on the original v0.3.3 Unreal report.

## Approved current files and historical coverage

Supported filenames are `GameUserSettings.ini`, `Engine.ini`, `Scalability.ini`, `DefaultGameUserSettings.ini`, `DefaultEngine.ini` and `DefaultScalability.ini`, case-insensitively.

From v0.3.4, the extension also accepts `OptiScaler.ini` through the same current-file approval and captured-payload verification rules. OptiScaler-specific keys are interpreted only in that filename, not inferred from similarly named sections in a game's INI.

- **Current game:** read only matching targets already approved through managed-file selection, including explicitly approved external settings. No unrelated AppData/Documents tree is read automatically by this report.
- **Saved snapshot:** read only matching files whose bytes were actually captured, verifying their stored hash/size first. Missing, corrupt, absent or uncaptured settings produce no invented values. There is no fallback to current game files or live external settings for an old snapshot.

An inventory hash cannot reconstruct INI contents. A backup with inventory-only settings can identify that a file existed, but cannot report its settings unless its bytes were captured. Choose the game's actual settings files before creating the versions whose preferences you want to inspect.

Input is limited to **2 MiB per file** and at most 1,000 matching files per report. UTF-8 and BOM-marked UTF-16 are supported. NUL-containing/oversized text is rejected; malformed UTF-8, access/integrity failures and oversized lines are reported as warnings. Scalar assignments require a section; blank/comment lines and Unreal array operators (`+`, `-`, `.`, `!`) are not interpreted as scalar settings. INI lines exceeding 16,384 characters are skipped with a warning.

## Evidence, layers and conflicts

Every observation retains file, layer hint, section, key, scalar raw value, line number, interpreted label/value, confidence text and source link. The raw scalar has surrounding quotes/inline semicolon comments stripped; original file bytes remain in the backup. Unmapped/custom values stay visible instead of being forced into an assumed enum.

Epic documents a layered configuration hierarchy, including engine/project/platform/user configuration, so seeing one assignment does not establish the final runtime value. This reader labels `Default*` filenames as **Project default** and other supported files as **Saved/config candidate**; these are location hints, not a complete engine hierarchy evaluation. It preserves conflicting saved values and warns instead of guessing precedence. [Epic configuration-file documentation](https://dev.epicgames.com/documentation/en-us/unreal-engine/configuration-files-in-unreal-engine).

Requested/current preferences and `LastUserConfirmed*` / `LastConfirmed*` fields remain separate. The latter describe saved confirmation state, not independently observed monitor output. Missing settings do not become inferred engine defaults. [Epic UGameUserSettings reference](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/UGameUserSettings).

## Recognized observations

| Source/key family | Reported meaning |
| --- | --- |
| User-settings resolution and last-confirmed resolution | Requested and last-confirmed width/height separately |
| User-settings `FullscreenMode` / `LastConfirmedFullscreenMode` | Saved window mode, separately confirmed mode; 0 fullscreen, 1 borderless fullscreen, 2 windowed |
| User-settings VSync/dynamic resolution/frame limit/HDR | Saved toggles, FPS cap and HDR luminance; saved zero frame limit is labeled uncapped |
| `[ScalabilityGroups] sg.ResolutionQuality` | Saved scale; zero is retained as an engine-default request rather than 0% rendering |
| CVar `r.ScreenPercentage` | Saved scale override, not the effective runtime render resolution |
| `r.AntiAliasingMethod` | 0 off, 1 FXAA, 2 TAA, 3 MSAA, 4 TSR/temporal-plugin-hook preference |
| Temporal upsampling/dynamic-resolution policy/VSync CVars | Saved preferences, with unknown values retained |

User-settings fields follow [Epic's API](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/UGameUserSettings); the CVar mappings follow [Epic's console-variable reference](https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-console-variables-reference). Documented base fields are mapped in UGameUserSettings-style sections. The same keys in generic `[Graphics]` or `[Display]` sections remain **raw game-specific values** requiring a game-specific rule; a numeric `FullscreenMode` there does not inherit Epic's enum automatically. The reader also considers scalability groups and recognized CVar sections (`[SystemSettings]`, `[ConsoleVariables]`, `[/Script/Engine.RendererSettings]`).

**100% scale is not a DLAA detector.** A full-resolution preference does not distinguish native rendering, Native AA or DLAA, and dynamic resolution/plugins can alter the effective result. Likewise an AA method value of 4 does not prove stock TSR is running: Unreal supports third-party temporal upscalers in the same integration path. The report retains the preference and warns about inference limits. [Epic temporal-upscaler documentation](https://dev.epicgames.com/documentation/en-us/unreal-engine/temporal-upscalers-in-unreal-engine).

## FSR modes and plugin-specific values

The recognized `r.FidelityFX.FSR.QualityMode` and `r.FidelityFX.FSR3.QualityMode` mappings are:

| Saved value | Documented preference |
| --- | --- |
| 0 | Native AA |
| 1 | Quality |
| 2 | Balanced |
| 3 | Performance |
| 4 | Ultra Performance |

These mappings come from AMD's [FSR Unreal plugin guide](https://gpuopen.com/learn/ue-fsr/) and [FSR 3 plugin guide](https://gpuopen.com/learn/ue-fsr3/). They are not applied to arbitrary game-specific `FSRQuality` keys or to FSR2 numeric quality modes without a supported rule. Recognized FSR/FSR2/FSR3 enable keys remain saved enable preferences; a quality mode alone does not prove the plugin is enabled.

Game-specific upscaler/quality/AA keys and plugin-specific quality modes retain their raw values. A custom DLSS number is not translated into Quality/Performance/DLAA without a primary-source rule for that game's/plugin's enum. Unknown numeric values are not aliases for a known mode. These reports do not combine filename presence and INI preferences into a claim of runtime activation.

## Game upscaling and frame-generation extension

From v0.3.4, the reader recognizes the following saved plugin keys in supported game CVar sections. These are separate preferences, not a resolved active upscaler/frame-generator pair:

| Saved key | Reported preference / mapper reference |
| --- | --- |
| `r.NGX.DLSS.Enable`, `r.NGX.DLAA.Enable` | Game DLSS upscaling / DLAA; [NVIDIA DLSS](https://developer.nvidia.com/rtx/dlss) |
| `r.Streamline.DLSSG.Enable` | Game DLSS frame generation; [NVIDIA Unreal frame-generation guide](https://d29g4g2dyqv443.cloudfront.net/sites/default/files/DLSS_Frame_Generation_Quick_Start_Guide.pdf) |
| `r.FidelityFX.FSR.Enabled`, `FSR2.Enabled`, `FSR3.Enabled` | Game FSR upscaling; [AMD plugin guide](https://gpuopen.com/learn/ue-fsr/) |
| `r.FidelityFX.FI.Enabled` | Game FSR frame generation, separately from upscaling; [AMD plugin guide](https://gpuopen.com/learn/ue-fsr/) |
| `r.XeSS.Enabled`, `r.XeFG.Enabled` | Game XeSS upscaling / frame generation; [Intel Unreal plugin README](https://github.com/GameTechDev/XeSSUnrealPlugin/blob/main/README.md) |

The full FSR2/FSR3 enable names use the same `r.FidelityFX.` prefix. Documented enable toggles are interpreted as saved preferences, never runtime measurements. `r.XeSS.Quality` remains a raw plugin-version-specific value. Generic user-settings keys such as `UpscalingType`, `FrameGeneration`, `FrameGenerationMode`, `bUseFrameGeneration` and custom DLSS/FSR/XeSS frame-generation selectors remain raw and require a game-specific mapping; familiar names or numeric values do not establish universal enums.

### OptiScaler configuration

Approved current or verified captured `OptiScaler.ini` observations have `Scope: OptiScaler` and layer **OptiScaler configuration**. Game-file observations have `Scope: Game`. This is file-source classification, not measured API/plugin activity. No uncaptured historical INI is replaced with a live file.

| INI section / key | Separately reported saved preference |
| --- | --- |
| `[Upscalers] Dx11Upscaler`, `Dx12Upscaler`, `VulkanUpscaler` | Output backend for each API; the reader does not select the API the game actually uses |
| `[FrameGen] Enabled` | Frame-generation enable preference |
| `[FrameGen] FGInput`, `FGOutput` | Input and output selectors, retained separately |
| `[FrameGen] FGNvngxReplacement` | NVNGX replacement selector, separately from input/output |
| `[FrameGen] FGType`, `[OptiFG] Enabled` | Legacy type/OptiFG enable preferences, not silently merged into newer fields |

Mappings cite the official [v0.9.4 configuration](https://github.com/optiscaler/OptiScaler/blob/v0.9.4/OptiScaler.ini), [v0.7.9 legacy configuration](https://github.com/optiscaler/OptiScaler/blob/v0.7.9/OptiScaler.ini) and [current upstream configuration](https://github.com/optiscaler/OptiScaler/blob/master/OptiScaler.ini). The current source recognizes DLSS/XeSS, FSR 2.1/2.2 and FSR 3.1/backend tokens, including DX12-translation variants; `ffx` retains an unresolved FidelityFX version. **`fsr31` is a saved FSR 3.1 / FSR 4-capable backend selection, not proof that FSR 4 is running.**

Frame-generation selectors distinguish no-FG, Streamline/NVNGX inputs, upscaler-driven input, FSR/OptiFG, XeSS and Nukem paths. Replacement values distinguish none, Nukem, DLSS Enabler, FidelityFX and combined paths. Missing fields create no inferred defaults. `auto` remains **Automatic / unspecified**; unknown selectors remain custom/unmapped, with raw values preserved. Saved enable/selector fields may disagree or depend on defaults, hardware and API; the report does not guess an effective runtime configuration.

From v0.3.4, the desktop splits **Graphics settings** (Game scope) from **OptiScaler settings**. Both show interpreted/raw values, file/line/layer, section/key and confidence/source links. Existing `settings <game-id> [snapshot-id]` CLI output adds `Scope` per observation from v0.3.4; it does not modify files or change backup schema 2.

## CLI and desktop

```powershell
$gameId = Read-Host 'Catalog game ID'
.\optibackup.exe settings $gameId
$snapshotId = Read-Host 'Snapshot ID'
.\optibackup.exe settings $gameId $snapshotId
```

The first command reads current approved INIs; the second reads verified captured INIs only. Both return observations/warnings as structured output.

Desktop **Components / settings…** on a selected backup includes a **Graphics settings** tab beside component provenance. Its columns show interpreted value plus raw scalar, file/line/layer, section/key, and confidence/source URL; full tooltips preserve text truncated by the table. **Files → Current settings…** reads approved current INIs, as does the CLI's current mode. Warnings and missing coverage remain visible. The initial mappings focus on Unreal conventions; this is not a universal parser for every game's custom settings format, nor a full Unreal configuration interpreter. These read-only reports do not measure runtime activation or establish compatibility with every Unreal game.
