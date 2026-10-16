# V3/V4 rendering through the V5 host: 2026-10-17

Status: unresolved. The user confirms these banks play in the same V5 editor.
The headless bridge must not infer that they are unactivated.

The deterministic failing test is:

```powershell
pwsh -NoProfile -File tools/vocaloid-bridge/tests/v5_legacy_acceptance.ps1
```

It enumerates generation 3/4 voices from V5, requires non-silent PCM and
consecutive edited renders, records every failure, and exits nonzero. It
never switches to V4. The known V5 Ning voice is a non-silent control.

Observed against the installed 5.2.0.1 managed/native API:

| Controlled change | V3 result | V5 control |
| --- | --- | --- |
| Remove bridge-only `NumLicenses==0` rejection | Native success, silent PCM | Non-silent |
| Omit note normalization or DVQM validation marker | Silent PCM | Non-silent |
| Keep default style instead of clearing it | Silent PCM | Non-silent |
| Load editor EXE assembly instead of DLL | Silent PCM | Non-silent |
| Use editor app ID `voc5` | Silent PCM | Non-silent |
| Open a matching VSQX through `OpenSequence` | Correct component ID, silent PCM | Non-silent |
| Initialize editor style manager, track/part effects and plain style | Silent PCM | Non-silent |
| Use editor background sequence renderer and read its cached WAV | Silent PCM | Non-silent |

The editor-derived setup also uses 44.1 kHz, 32 tracks, zero undo history,
4/4 time, tempo 120, track/part effect blocks, and effect tempo recreation.
For V3, the resolved plain style is `No Effect`. These observations rule out
the tested differences for the specific host; they do not establish that all
editor initialization has been reproduced.

`NumLicenses` is a native VDM record-count accessor, not an activation-status
API. Its zero value is kept as diagnostic metadata, and no longer prevents a
normal render request. A successful native return with silent PCM remains a
render failure, with the component ID and native result in the error.

The next required comparison is a minimal V3/V4 `.vpr` or `.vsqx` that is
known to play in the user's V5 editor. Use that same input through the host
before changing more initialization variables. No vendor binaries or
activation data were patched, fabricated, or copied during this diagnosis.
