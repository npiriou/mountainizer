[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ProjectPath,
    [Parameter(Mandatory)] [string] $Executable
)

$ErrorActionPreference = "Stop"
$project = (Resolve-Path $ProjectPath).Path
$app = (Resolve-Path $Executable).Path
$codes = @("ARA1", "ASS1", "BRA2", "ABA1", "BHP1", "ABC1", "CRA3", "DRA4", "DSS2",
    "CBA2", "CHP2", "DBC2", "ERA5", "ESS3", "EBA3", "EHP3", "EBC3")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ByAutomationId($root, [string] $id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$process = Start-Process -FilePath $app -ArgumentList ('"' + $project + '"') -PassThru
try {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Mountainizer exited before opening its main window" }
        if ($process.MainWindowHandle -ne 0) { break }
    }
    if ($process.MainWindowHandle -eq 0) { throw "Mountainizer did not open a main window" }

    $initialReady = $false
    for ($attempt = 0; $attempt -lt 240; $attempt++) {
        Start-Sleep -Milliseconds 125
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $progress = Find-ByAutomationId $root "ProgressText"
        $status = Find-ByAutomationId $root "StatusText"
        $isInitialReady = $null -ne $progress -and $progress.Current.Name -eq "Ready"
        $hasInitialStatus = $null -ne $status -and $status.Current.Name -match "\[[A-Z0-9]+\]"
        if ($isInitialReady -and $hasInitialStatus) { $initialReady = $true; break }
    }
    if (-not $initialReady) { throw "Mountainizer did not finish its initial course load" }

    foreach ($code in $codes) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $combo = Find-ByAutomationId $root "LevelPicker"
        if ($null -eq $combo) { throw "Course selector was not found" }
        $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        Start-Sleep -Milliseconds 150
        $itemCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
        $item = $null
        for ($index = 0; $index -lt [Math]::Min(17, $items.Count); $index++) {
            if ($items[$index].Current.Name -match ("Code = " + $code + "[,}]")) { $item = $items[$index]; break }
        }
        if ($null -eq $item) { throw "Course $code was not found in the selector" }
        $selection = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()

        $loaded = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            Start-Sleep -Milliseconds 125
            $process.Refresh()
            if ($process.HasExited) { throw "Mountainizer exited while loading $code" }
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $status = Find-ByAutomationId $root "StatusText"
            $progress = Find-ByAutomationId $root "ProgressText"
            $hasExpectedStatus = $null -ne $status -and $status.Current.Name -match ("\[" + $code + "\]")
            $isReady = $null -ne $progress -and $progress.Current.Name -eq "Ready"
            if ($hasExpectedStatus -and $isReady) {
                Write-Host "$code UI load passed: $($status.Current.Name)"
                $loaded = $true
                break
            }
        }
        if (-not $loaded) { throw "Course $code did not reach the Ready state" }
    }
    Write-Host "All 17 playable course UI loads passed."
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
}
