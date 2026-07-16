# Tools

`verify-release.ps1` runs the clean build, synthetic tests, CLI smoke check, and self-contained single-file publish gate used by CI:

```powershell
.\tools\verify-release.ps1
```

Before a release, add an imported local NTSC-U project to run the copyrighted-data tests and the compact 17-course CLI audit as well:

```powershell
.\tools\verify-release.ps1 -ProjectPath "C:\Mountainizer\MyProject\project.json"
```

The local release gate also launches the published executable and uses Windows UI Automation to load every playable course through the desktop selector. `ui-smoke.ps1` can be run independently when diagnosing the desktop flow.

The supported inspection automation surface lives in `src/Mountainizer.Cli` so the GUI and CLI share the same parser and exporter libraries.
