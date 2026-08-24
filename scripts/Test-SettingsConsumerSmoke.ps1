param(
    [string]$ApplicationPath,
    [string]$DataRoot,
    [int]$TimeoutSeconds = 30,
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $workspace `
        'src\Dextromethorphan.App\bin\Release\net10.0-windows10.0.19041.0\Dextromethorphan.exe'
}
$ApplicationPath = [System.IO.Path]::GetFullPath($ApplicationPath)
if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) {
    throw "Application was not found: $ApplicationPath"
}

$smokeRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $workspace 'artifacts\smoke'))
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $smokeRoot (
        'settings-' + [Guid]::NewGuid().ToString('N'))
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class SettingsSmokeNativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);
}
'@

function Wait-AutomationElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.Condition]$Condition,
        [System.Windows.Automation.TreeScope]$Scope =
            [System.Windows.Automation.TreeScope]::Descendants,
        [string]$Description = 'automation element'
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst($Scope, $Condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Wait-ProcessWindow {
    param(
        [int]$ProcessId,
        [string]$WindowName = ''
    )
    $conditions = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId))
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window))
    if (-not [string]::IsNullOrWhiteSpace($WindowName)) {
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $WindowName))
    }
    $condition = [System.Windows.Automation.AndCondition]::new(
        $conditions.ToArray())
    Wait-AutomationElement `
        -Root ([System.Windows.Automation.AutomationElement]::RootElement) `
        -Condition $condition `
        -Scope ([System.Windows.Automation.TreeScope]::Descendants) `
        -Description $(if ($WindowName) { $WindowName } else { 'application window' })
}

function Wait-NativeWindowClosed {
    param(
        [IntPtr]$Handle,
        [int]$ProcessId,
        [string]$WindowName
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (-not [SettingsSmokeNativeMethods]::IsWindow($Handle)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windowCondition = [System.Windows.Automation.AndCondition]::new(
        $processCondition,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window))
    $openWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $windowCondition) | ForEach-Object { $_.Current.Name }
    throw "Timed out waiting for $WindowName to close. Open windows: $($openWindows -join ', ')"
}

$process = $null
$result = [ordered]@{
    schemaVersion = 1
    started = $false
    settingsOpened = $false
    tabsVisited = @()
    searchExercised = $false
    themesExercised = @()
    cleanExit = $false
    error = $null
}
$failure = $null
try {
    $start = [System.Diagnostics.ProcessStartInfo]::new($ApplicationPath)
    $start.UseShellExecute = $false
    $start.Arguments = '--open-settings'
    $start.Environment['DEXTROMETHORPHAN_DATA_ROOT'] = $DataRoot
    $process = [System.Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Application process did not start.' }
    $result.started = $true

    $mainWindow = Wait-ProcessWindow `
        -ProcessId $process.Id `
        -WindowName 'Dextromethorphan'

    try {
        $settingsWindow = Wait-ProcessWindow `
            -ProcessId $process.Id `
            -WindowName 'Dextromethorphan settings'
    }
    catch {
        $processCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $windowCondition = [System.Windows.Automation.AndCondition]::new(
            $processCondition,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window))
        $visibleWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $windowCondition) | ForEach-Object { $_.Current.Name }
        throw "Settings did not open through --open-settings. Process windows: $($visibleWindows -join ', ')"
    }
    $result.settingsOpened = $true
    $tabCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $settingsWindow.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $tabCondition)
    # Leave Appearance selected for the live-theme checks below. Returning to
    # an unloaded ComboBox can expose a stale WPF item-container automation peer.
    $expected = @(
        'Audio', 'Playback', 'Library', 'Metadata', 'Lyrics', 'Views',
        'Diagnostics', 'Data', 'Shortcuts', 'About', 'Appearance')
    foreach ($name in $expected) {
        $tab = @($tabs | Where-Object { $_.Current.Name -eq $name })[0]
        if ($null -eq $tab) { throw "Settings tab '$name' was not exposed." }
        $tab.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 80
        if (-not $tab.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
            throw "Settings tab '$name' did not become selected."
        }
        $result.tabsVisited += $name
    }

    $search = Wait-AutomationElement `
        -Root $settingsWindow `
        -Condition ([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Search settings')) `
        -Description 'settings search'
    $valuePattern = $search.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue('theme')
    Start-Sleep -Milliseconds 180
    if ($valuePattern.Current.Value -ne 'theme') {
        throw 'Settings search did not retain the entered query.'
    }
    $valuePattern.SetValue('')
    $result.searchExercised = $true

    Start-Sleep -Milliseconds 120
    $comboCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $themeCombo = $settingsWindow.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $comboCondition)
    if ($null -eq $themeCombo) { throw 'Theme selector was not exposed.' }
    foreach ($theme in @('Light', 'Amoled', 'Dark')) {
        $settingsWindow = Wait-ProcessWindow `
            -ProcessId $process.Id `
            -WindowName 'Dextromethorphan settings'
        $themeCombo = Wait-AutomationElement `
            -Root $settingsWindow `
            -Condition $comboCondition `
            -Description 'theme selector'
        $themeCombo.SetFocus()
        $themeCombo.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 80
        $themeItems = $themeCombo.GetCurrentPattern(
            [System.Windows.Automation.ItemContainerPattern]::Pattern)
        $themeItem = $themeItems.FindItemByProperty(
            $null,
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $theme)
        if ($null -eq $themeItem) {
            throw "Theme option '$theme' was not exposed."
        }
        $selection = $themeItem.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        try {
            $selection.Select()
        }
        catch {
            # Theme application replaces the live resource dictionary. WPF can
            # invalidate the source automation peer after accepting Select().
            if ($_.Exception.InnerException -isnot `
                [System.Windows.Automation.ElementNotAvailableException]) {
                throw
            }
        }
        Start-Sleep -Milliseconds 220
        $settingsWindow = Wait-ProcessWindow `
            -ProcessId $process.Id `
            -WindowName 'Dextromethorphan settings'
        $themeCombo = Wait-AutomationElement `
            -Root $settingsWindow `
            -Condition $comboCondition `
            -Description 'theme selector after theme change'
        $currentTheme = $themeCombo.GetCurrentPattern(
            [System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if (@($currentTheme | ForEach-Object { $_.Current.Name }) -notcontains $theme) {
            throw "Theme option '$theme' did not become selected."
        }
        $result.themesExercised += $theme
    }

    $settingsHandle = [IntPtr]$settingsWindow.Current.NativeWindowHandle
    $settingsClose = Wait-AutomationElement `
        -Root $settingsWindow `
        -Condition ([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Close settings')) `
        -Description 'settings close button'
    $settingsClose.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-NativeWindowClosed `
        -Handle $settingsHandle `
        -ProcessId $process.Id `
        -WindowName 'Dextromethorphan settings'
    $mainWindow = Wait-ProcessWindow `
        -ProcessId $process.Id `
        -WindowName 'Dextromethorphan'
    $mainWindow.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        throw 'Application did not exit after the main window closed.'
    }
    $result.cleanExit = $process.ExitCode -eq 0
    if (-not $result.cleanExit) {
        throw "Application exited with code $($process.ExitCode)."
    }
}
catch {
    $failure = $_
    $result.error = $_.Exception.Message
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
    $result | ConvertTo-Json -Depth 4
    if (-not $KeepData -and (Test-Path -LiteralPath $DataRoot)) {
        $dataPrefix = $DataRoot.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        $allowedPrefix = $smokeRoot.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if ($dataPrefix.StartsWith(
            $allowedPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $DataRoot -Recurse -Force
        }
    }
}
if ($null -ne $failure) { throw $failure }
