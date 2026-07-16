[CmdletBinding()]
param(
    [string] $ProjectPath,
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet restore Mountainizer.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    dotnet build Mountainizer.sln -c $Configuration --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        dotnet test src\Mountainizer.Tests\Mountainizer.Tests.csproj -c $Configuration --no-build --filter "TestCategory!=LocalGameData"
    }
    else {
        $resolvedProject = (Resolve-Path $ProjectPath).Path
        $env:MOUNTAINIZER_TEST_PROJECT = $resolvedProject
        dotnet test src\Mountainizer.Tests\Mountainizer.Tests.csproj -c $Configuration --no-build
        if ($LASTEXITCODE -eq 0) {
            dotnet run --project src\Mountainizer.Cli\Mountainizer.Cli.csproj -c $Configuration --no-build -- audit $resolvedProject --json artifacts\release-course-audit.json
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "test or local course audit failed" }

    dotnet run --project src\Mountainizer.Cli\Mountainizer.Cli.csproj -c $Configuration --no-build -- --help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CLI smoke check failed" }

    if (-not $SkipPublish) {
        $publishDirectory = Join-Path $root "artifacts\release-verify"
        if (Test-Path $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
        dotnet publish src\Mountainizer.App\Mountainizer.App.csproj -c $Configuration -r $Runtime --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
            -o $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "self-contained publish failed" }
        $executable = Join-Path $publishDirectory "Mountainizer.App.exe"
        if (-not (Test-Path $executable -PathType Leaf)) { throw "publish did not produce Mountainizer.App.exe" }
        $unexpected = @(Get-ChildItem $publishDirectory -File | Where-Object Name -ne "Mountainizer.App.exe")
        if ($unexpected.Count -ne 0) { throw "single-file publish left unexpected companion files: $($unexpected.Name -join ', ')" }
        Write-Host "Verified self-contained single-file release: $executable"
        if (-not [string]::IsNullOrWhiteSpace($ProjectPath)) {
            & (Join-Path $PSScriptRoot "ui-smoke.ps1") -ProjectPath $resolvedProject -Executable $executable
        }
    }

    Write-Host "Mountainizer release verification passed."
}
finally {
    Remove-Item Env:\MOUNTAINIZER_TEST_PROJECT -ErrorAction SilentlyContinue
    Pop-Location
}
