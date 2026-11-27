# WPF runtime smoke (SIMULATED)

Executable Windows/.NET 10 test with no additional packages. It opens the actual compiled
`MainWindow` off screen, exercises the existing view model against an in-memory
`HttpMessageHandler`, runs WPF layout, and saves `RenderTargetBitmap` PNG evidence.
It never sends a network request and never contacts SQL Server or ComfyUI.

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet restore tests/CharacterConceptAI.Desktop.Smoke/CharacterConceptAI.Desktop.Smoke.csproj --ignore-failed-sources
dotnet build tests/CharacterConceptAI.Desktop.Smoke/CharacterConceptAI.Desktop.Smoke.csproj -c Release --no-restore /p:UseSharedCompilation=false /m:1 /nodeReuse:false
dotnet tests/CharacterConceptAI.Desktop.Smoke/bin/Release/net10.0-windows/CharacterConceptAI.Desktop.Smoke.dll evidence/ui
```

Read `evidence/ui/wpf-smoke-report.json` for checks, binding errors, fixture requests,
capture dimensions, and limitations. `PASS_SIMULATED` describes this test only.
It does not prove real SQL/GPU/LAN operation, physical monitor DPI behavior, accessibility,
native titlebar behavior, or human usability. Captures are actual WPF visual-tree renders,
not operating-system screenshots; their content is explicitly synthetic.

Semantic-token and contrast verification is separate:

```powershell
pwsh -NoProfile -File scripts/validate-theme.ps1
```
