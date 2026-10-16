# OpenUtau VOCALOID bridge

[中文使用说明](README.zh-CN.md)

This bridge connects OpenUtau to the VOCALOID engine and voicebanks already installed on the Windows machine. The integrated default routes voice discovery and render requests through **VOCALOID 5**. Actual sound output is verified for the installed V5 banks; the installed V3/V4 banks currently enumerate but are not render-ready on this machine. The bridge does not copy, convert, or redistribute Yamaha engine or voicebank files. A separately configured VOCALOID 4 backend remains available as an optional legacy path.

The OpenUtau integration and bridge source are being developed on the fork [`1w6qn/OpenUtau`, branch `codex/vocaloid-v5-bridge`](https://github.com/1w6qn/OpenUtau/tree/codex/vocaloid-v5-bridge); the fork places these files under `tools/vocaloid-bridge`. The packaged test build is `dist/OpenUtau-V5-AutoPitch`.

## Current support and verification

The V5 backend has produced real, non-silent PCM for the three installed V5 banks: Luo Tianyi Ning (凝), Wan (婉), and Meng (萌). Their OpenUtau pipeline test checks nine notes across three banks, phrase placement, and fractional-semitone pitch offsets. The V5 API also enumerates the installed V3 and V4 banks after import, but this machine reports `LicenseCount=0` for those three legacy banks. The bridge marks them not ready and refuses rendering. **Importing a bank folder does not prove V5 compatibility or grant a synthesis license.** The V3/V4 phrases previously rendered through the optional V4 backend are separate tests and do not count as V5 compatibility verification.

The current six-bank catalog is expected to contain three ready V5 banks and three V3/V4 banks awaiting official V5 authorization. The readiness state shown by the bridge is authoritative for the current machine; it can differ on another installation.

“Real-time preview” means the renderer synthesizes a complete phrase in a background host and then gives the PCM to OpenUtau’s player. It reuses a warm host and caches phrase renders. It does not synthesize inside the audio callback, so first renders and changed phrases can take time.

## Install and import voicebanks

Use the integrated build at `dist/OpenUtau-V5-AutoPitch/OpenUtau.exe`, or build the fork from branch `codex/vocaloid-v5-bridge`. In OpenUtau, choose **File → “导入 VOCALOID 声库…”** and select the V5 engine directory and an installed voicebank directory. The importer recognizes V5 banks already visible to VDM and registered V3/V4 banks whose installed metadata and DDB/DDI files are available. It creates proxy singers, refreshes the singer list, and preserves custom names and padding on repeated import. It writes only ordinary V5 component metadata when needed; it does not transfer activation or license data. Unsupported or unregistered folders are rejected instead of being guessed from loose files.

If proxy singers are not present in the build’s `Singers` directory, generate them from the V5 catalog after import:

```powershell
pwsh -NoProfile -File .\tools\vocaloid-bridge\register-singers.ps1 `
  -Config .\tools\vocaloid-bridge\vocaloid5.example.json `
  -SingerDirectory .\dist\OpenUtau-V5-AutoPitch\Singers
```

The command-line import operation is also available for diagnosis:

```powershell
pwsh -NoProfile -File .\tools\vocaloid-bridge\bridge.ps1 `
  -Operation import `
  -Config .\tools\vocaloid-bridge\vocaloid5.example.json `
  -VoiceDirectory 'D:\Program Files (x86)\VoiceDB\LuoV4_Chn'
```

Import results include `Generation`, `ComponentId`, `DatabasePath`, `LicenseCount`, and `Ready`. A bank with `Ready=false` needs to be registered for V5 synthesis through Yamaha’s official process before it can render. The bridge will not fall back to VOCALOID 4 for that bank.

For the current V5 engine installation, `vocaloid5.example.json` points at `Editor/VOCALOID5.dll` and launches the 64-bit Windows PowerShell .NET Framework host. The V5 engine and the user’s installed banks must remain in their normal installation locations. The bridge itself is driven by PowerShell 7.4+; the native V5 host runs under 64-bit Windows PowerShell.

## V5 rendering and automatic F0

The V5 host uses the installed VOCALOID 5 managed API to create a sequence, process Simplified Chinese pinyin through the vendor G2P, render a WAV, and return mono PCM to OpenUtau. OpenUtau’s `zh-CN` lyric language converts Chinese lyrics to pinyin before G2P. Direct CLI render requests should provide one pinyin syllable per note. The current native backend supports CHS voicebanks, ordinary singing notes, and an absolute MIDI pitch curve; unsupported automation, note properties, effects, and other languages are rejected rather than silently ignored. DYN is applied by OpenUtau after rendering.

After rendering, the piano roll's final-pitch overlay automatically shows pitch extracted from the actual PCM using OpenUtau's bundled WORLDLINE DIO extractor. Unvoiced sections break the contour. The renderer also exposes the same cached result through OpenUtau's render-pitch command (Ctrl+R). Automatic display does not rewrite project pitch curves; explicitly loading rendered pitch does. The included analyzer pipeline checks F0 against the V5 demo project.

## Build and tests

Clone the fork and select the integration branch, then build OpenUtau from its solution:

```powershell
git clone https://github.com/1w6qn/OpenUtau.git
Set-Location OpenUtau
git switch codex/vocaloid-v5-bridge
dotnet publish .\OpenUtau\OpenUtau.csproj -c Release -r win-x64 --self-contained true `
  -o .\tools\vocaloid-bridge\dist\OpenUtau-V5-AutoPitch
```

Run the bridge and OpenUtau acceptance checks from the repository root with PowerShell 7.4+ on a machine with the relevant installed engine and voicebanks:

The OpenUtau source currently targets .NET 10. Audio acceptance scripts additionally require Python 3 with NumPy. The published OpenUtau application includes its .NET runtime and the bridge helper sources.

```powershell
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\v5_acceptance.ps1
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\vocaloid_import.ps1
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\openutau_v5_pipeline.ps1 `
  -AppDirectory .\tools\vocaloid-bridge\dist\OpenUtau-V5-AutoPitch
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\vocaloid_pitch_pipeline.ps1 `
  -Generation V5 -AppDirectory .\tools\vocaloid-bridge\dist\OpenUtau-V5-AutoPitch
```

These tests validate consecutive native V5 renders, six-bank import and repeated import, the three-bank OpenUtau demo audio, and automatic F0 extraction and render-pitch integration. The import acceptance test expects this machine's catalog of six banks (three ready, three pending); adjust the engine and voice directories for another installation. The complete V5 audio test requires the three ready V5 banks. A doctor or voice-list response verifies initialization and enumeration only, not audible synthesis.

The external bridge also supports a direct request/response smoke test:

```powershell
pwsh -NoProfile -File .\tools\vocaloid-bridge\bridge.ps1 `
  -Operation doctor -Config .\tools\vocaloid-bridge\vocaloid5.example.json
pwsh -NoProfile -File .\tools\vocaloid-bridge\bridge.ps1 `
  -Operation render `
  -Config .\tools\vocaloid-bridge\vocaloid5.example.json `
  -Request .\tools\vocaloid-bridge\request.vocaloid5.json `
  -Output .\tmp\vocaloid-v5-smoke.wav
```

The command reports render duration and `start_seconds`; the generated WAV includes the engine’s leading and trailing audio, so use that offset when aligning it to the score. It refuses to overwrite an existing output file.

## Optional VOCALOID 4 backend

`vocaloid4.example.json` and `V4/run-native-host.ps1` preserve the independently tested x86 VOCALOID 4 path for machines that need it. It uses the local V4 editor and DLLs and can render the locally verified V3/V4 banks through that engine. It is an explicitly configured alternative; the integrated default route is VOCALOID 5. Results from the V4 backend must not be used to claim that the V5 engine can synthesize a legacy bank.

The optional V4 backend uses a private x86 ABI and currently supports the tested Bank 4 Chinese singing path. Its host requires 32-bit Windows PowerShell. A separate generic TuneLab legacy bridge is also available through `vocaloid-bridge.example.json` when the user already has a compatible legacy engine implementation; neither backend is bundled with Yamaha binaries.

## Additional checks and limitations

Protocol/cache tests and the OpenUtau renderer fixture probe can run without VOCALOID:

```powershell
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\run.ps1
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\openutau_probe.ps1
pwsh -NoProfile -File .\tools\vocaloid-bridge\tests\legacy_contract_probe.ps1
```

Real native render tests require the matching local engine, installed voicebanks, and their legitimate vendor registration. The bridge does not bypass licensing or manufacture V5 authorization. Performance results are machine-specific, and phrase rendering remains background work rather than audio-callback streaming.
