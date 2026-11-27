# Little Launcher's Store screenshots in one listing language (store-assets/README.md, "Screenshots"). ASCII only.
#
# Run by capture.sh through app_test_ui, in the guest's desktop session, after screenshot-desktop.ps1
# -Mode Prepare, with a dev package installed and Windows in this locale's display language. Reads its
# inputs from C:\apptest\capture-input.json (locale, scale), writes seed-settings.json with every name
# replaced by this locale's from sample-data.json, and saves the eight raw captures with their
# sidecars to C:\apptest\out.
#
# Controls are found by automation ID (Find-ById) and tray icons by their launcher's name, which is
# sample data, never by translated interface text. Only Windows' pinning prompt, which UI Automation
# cannot see, is clicked by position. Sizes and offsets are given at 100% and multiplied by the
# scale, because the helpers work in physical pixels.
$ErrorActionPreference = 'Stop'
. C:\apptest\ui-lib.ps1
Add-Type -AssemblyName System.Windows.Forms

$inputs = Get-Content C:\apptest\capture-input.json -Raw | ConvertFrom-Json
$locale = $inputs.locale
$scale = [int]$inputs.scale / 100
$names = (Get-Content C:\apptest\sample-data.json -Raw -Encoding UTF8 | ConvertFrom-Json).locales.$locale.names
if (-not $names) { throw "sample-data.json has no names for $locale" }

# The host window's title is fixed, not translated (App.HostWindowTitle).
$hostTitle = 'Little Launcher Host'

<# A length at 100% display scale, in physical pixels at the capture's scale. #>
function Px([int]$Logical) { [int]($Logical * $scale) }

<# This locale's name for one of the seed's English names; a missing one stops the run. #>
function Get-LocalName([string]$English) {
    $local = $names.$English
    if (-not $local) { throw "sample-data.json has no $locale name for '$English'" }
    $local
}

<# Click the screen at a physical position with the real mouse. #>
function Invoke-ClickAt([int]$X, [int]$Y) {
    [UiNative]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 300
    [UiNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [UiNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 3
}

# --- The app and its windows -------------------------------------------------------------------

$pkg = Get-AppxPackage | Where-Object Name -like '*LittleLauncher*' | Select-Object -First 1
if (-not $pkg) { throw 'Little Launcher is not installed' }
$dataDir = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalCache\Roaming\LittleLauncher"
$settingsPath = Join-Path $dataDir 'settings.json'

function Start-App { Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App" }
function Stop-App {
    Get-Process LittleLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

<# The settings window: the app window that has the Launchers navigation item. #>
function Get-SettingsWindow {
    Get-AppWindows LittleLauncher | Where-Object { Find-ById $_ 'NavLaunchers' } | Select-Object -First 1
}

<# Open the settings window (a second launch asks the running app to show it) and wait for it. #>
function Open-SettingsWindow {
    Start-App
    $window = $null
    for ($i = 0; $i -lt 30 -and -not $window; $i++) { Start-Sleep -Seconds 2; $window = Get-SettingsWindow }
    if (-not $window) { throw 'No settings window' }
    $window
}

function Close-SettingsWindow {
    Get-SettingsWindow | ForEach-Object { $_.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
    Start-Sleep -Seconds 2
}

<# The open flyout or web flyout: an on-screen app window that is neither the host nor settings. #>
function Get-FlyoutWindow([int]$MinWidth) {
    Get-AppWindows LittleLauncher | Where-Object {
        $_.Current.Name -ne $hostTitle -and
        $_.Current.BoundingRectangle.Width -gt (Px $MinWidth) -and
        $_.Current.BoundingRectangle.X -ge 0 -and
        -not (Find-ById $_ 'NavLaunchers')
    } | Select-Object -First 1
}

<#
Write the seed settings for this locale: every Name replaced by its translation, the app's language
set to this locale, and the installed version as the last run, so no upgrade notice shows. Then
restart the app on them.
#>
function Start-Seeded {
    Stop-App
    $seed = Get-Content C:\apptest\seed-settings.json -Raw -Encoding UTF8 | ConvertFrom-Json
    $seed.PSObject.Properties.Remove('_comment')
    $seed.LanguageOverride = $locale
    $version = [version]$pkg.Version
    $seed.LastRunVersion = '{0}.{1}.{2}' -f $version.Major, $version.Minor, $version.Build
    Convert-Names $seed.Launchers
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    [IO.File]::WriteAllText($settingsPath, ($seed | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding $false))
    Start-App
    Start-Sleep -Seconds 10   # item icons load
}

<# Replace every non-empty Name under $Node (launchers, items, groups, bookmarks) in place. #>
function Convert-Names($Node) {
    foreach ($entry in @($Node)) {
        if ($null -eq $entry -or $entry -is [string] -or $entry -is [ValueType]) { continue }
        if ($entry.PSObject.Properties['Name'] -and $entry.Name) { $entry.Name = Get-LocalName $entry.Name }
        foreach ($child in 'Items', 'Children', 'WebBookmarks') {
            if ($entry.PSObject.Properties[$child]) { Convert-Names $entry.$child }
        }
    }
}

# Promote this app's tray icons out of the overflow for the session, so the overflow panel never
# covers a flyout; the previous values are put back at the end.
$promoted = @()
function Show-OwnTrayIcons {
    Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath
        if ($p.ExecutablePath -like '*LittleLauncher*') {
            $script:promoted += [pscustomobject]@{ Path = $_.PSPath; Old = $p.IsPromoted }
            Set-ItemProperty $_.PSPath -Name IsPromoted -Value 1 -Type DWord
        }
    }
}
function Restore-OwnTrayIcons {
    foreach ($e in $script:promoted) {
        if ($null -eq $e.Old) { Remove-ItemProperty $e.Path -Name IsPromoted -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $e.Path -Name IsPromoted -Value $e.Old -Type DWord }
    }
}

# --- Shots -------------------------------------------------------------------------------------

<#
Open a launcher's flyout from its tray icon (found by the launcher's name, the start of its tooltip)
and capture it. $Wait gives a web page time to load.
#>
function Save-FlyoutCapture([string]$EnglishLauncher, [string]$Name, [int]$MinWidth = 100, [int]$Wait = 3) {
    Close-SettingsWindow
    $launcher = Get-LocalName $EnglishLauncher
    $icon = Find-TrayIcon $launcher
    if (-not $icon) { throw "${Name}: no tray icon for $launcher" }
    Invoke-Click $icon
    # Pointer off the tray, so the icon's tooltip does not cover the flyout.
    [UiNative]::SetCursorPos((Px 400), (Px 300)) | Out-Null
    Start-Sleep -Seconds (2 + $Wait)
    $flyout = Get-FlyoutWindow $MinWidth
    if (-not $flyout) { throw "${Name}: no flyout for $launcher" }
    Save-DesktopCapture $Name $flyout
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 1
}

<#
Turn app notifications on or off for this user, and restart the user's notification service so it
reads the change (it ignores a changed value until then). screenshot-desktop.ps1 -Mode Prepare turns
them off so no toast lands in a capture, but Windows' pin prompt is itself a notification: with them
off, TaskbarManager reports pinning as not allowed and the app shows its manual fallback instead
(Microsoft's "Pin your app to the taskbar" lists system notifications among the requirements).
#>
function Set-AppNotifications([bool]$On) {
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' ToastEnabled ([int]$On) -Type DWord
    $service = Get-Service 'WpnUserService_*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($service) { Restart-Service $service.Name -Force }
    Start-Sleep -Seconds 3
}

<#
Pin Everyday through its own Pin to taskbar button, accept Windows' prompt, then open the launcher
from its taskbar button and capture it. Notifications are on only while the prompt is up. Windows'
prompt is a notification that UI Automation cannot see, so it is answered by position: the bottom
one's first button (Yes), then any left are dismissed with their second.
#>
function Save-TaskbarCapture([string]$Name) {
    $launcher = Get-LocalName 'Everyday'
    $settings = Open-SettingsWindow
    Set-WindowFitted $settings (Px 1100)
    Invoke-Click (Find-ById $settings 'NavLaunchers')
    Start-Sleep -Seconds 3
    Invoke-Click (Find-ById $settings 'LauncherSettings_shot-everyday')
    Start-Sleep -Seconds 5
    # The pinned launcher's taskbar button carries the launcher's name; tray icons are left out.
    $buttons = New-Object System.Windows.Automation.PropertyCondition($UiA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $taskbar = $UiRoot.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($UiA::ClassNameProperty, 'Shell_TrayWnd')))
    function Find-PinnedButton {
        $taskbar.FindAll('Descendants', $buttons) |
            Where-Object { $_.Current.Name -like "*$launcher*" -and $_.Current.AutomationId -ne 'NotifyItemIcon' } |
            Select-Object -First 1
    }

    # Windows' prompt sometimes comes late, after the clicks meant for it (three of seven locales in
    # one run), so check for the taskbar button and ask again, waiting longer each time.
    $button = $null
    foreach ($attempt in 1..3) {
        $pin = Find-ById $UiRoot 'PinToTaskbarButton'
        if (-not $pin) { throw "${Name}: no Pin to taskbar button" }
        Set-AppNotifications $true
        try {
            Invoke-Click $pin
            Start-Sleep -Seconds (4 + 4 * $attempt)
            Invoke-ClickAt (Px 1636) (Px 987)
            foreach ($i in 1..4) { Invoke-ClickAt (Px 1806) (Px 987) }
        }
        finally {
            Set-AppNotifications $false
        }
        Start-Sleep -Seconds 2
        $button = Find-PinnedButton
        if ($button) { break }
        Write-Output "${Name}: not pinned after attempt $attempt"
    }
    $done = Find-ById $UiRoot 'DoneButton'
    if ($done) { Invoke-Click $done; Start-Sleep -Seconds 2 }
    Close-SettingsWindow
    if (-not $button) { $button = Find-PinnedButton }
    if (-not $button) { throw "${Name}: no taskbar button for $launcher" }
    # The first launch through a new pin starts the companion helper, which can take a while; give
    # it time, and click once more if nothing opened.
    $flyout = $null
    foreach ($click in 1..2) {
        Invoke-Click $button
        [UiNative]::SetCursorPos((Px 400), (Px 300)) | Out-Null
        for ($i = 0; $i -lt 12 -and -not $flyout; $i++) {
            Start-Sleep -Seconds 1
            $flyout = Get-FlyoutWindow 100
        }
        if ($flyout) { break }
    }
    if (-not $flyout) { throw "${Name}: no flyout from the taskbar button '$($button.Current.Name)'" }
    Start-Sleep -Seconds 2
    Save-DesktopCapture $Name $flyout
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 1
}

<# The Launchers page, then Everyday's own settings window, each centred. #>
function Save-SettingsCaptures {
    $settings = Open-SettingsWindow
    Set-WindowFitted $settings (Px 1100)
    Invoke-Click (Find-ById $settings 'NavLaunchers')
    Start-Sleep -Seconds 3
    Set-WindowCentered $settings (Px 1100) (Px 700)
    # A neutral click on the page title area, so no keyboard focus rectangle shows.
    $frame = Get-WindowFrame $settings
    Invoke-ClickAt ($frame.X + (Px 700)) ($frame.Y + (Px 90))
    [UiNative]::SetCursorPos($frame.X + $frame.Width + (Px 60), $frame.Y + (Px 20)) | Out-Null
    Start-Sleep -Seconds 1
    Save-DesktopCapture '06-launchers-dark' $settings

    Invoke-Click (Find-ById $settings 'LauncherSettings_shot-everyday')
    Start-Sleep -Seconds 6
    $windows = New-Object System.Windows.Automation.PropertyCondition($UiA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $launcherSettings = $settings.FindAll('Children', $windows) | Where-Object { $_.Current.BoundingRectangle.Width -gt (Px 300) } | Select-Object -First 1
    if (-not $launcherSettings) { throw '07: no launcher settings window' }
    # The main window parked off screen, so only the launcher's settings window shows.
    Set-WindowBounds $settings (Px -3000) (Px 40) (Px 1100) (Px 700)
    Set-WindowCentered $launcherSettings
    [UiNative]::SetCursorPos((Px 100), (Px 100)) | Out-Null
    Save-DesktopCapture '07-launcher-settings-dark' $launcherSettings
}

# --- Run ---------------------------------------------------------------------------------------

# Windows itself must already be in this language; the app is set to it in the seed.
$ui = Get-DisplayLanguage   # not Get-UICulture, which reads en-US for zh-CN and ja-JP in a console (ui-lib.ps1)
if ($ui -ne $locale) { throw "Windows is in $ui, not ${locale}; switch it first (capture.sh, CAPTURE_PREPARE_LOCALE)" }

New-Item -ItemType Directory -Force $UiOut | Out-Null
Get-ChildItem $UiOut -File | Where-Object Extension -in '.png', '.json' | Remove-Item

# One time zone for every language, so the taskbar clocks agree; the clock's format is the locale's.
$savedTimeZone = (tzutil /g).Trim()
tzutil /s "Eastern Standard Time"

try {
    Start-Seeded
    Show-OwnTrayIcons
    # The promotion takes effect when the icons are added again.
    Stop-App
    Start-App
    Start-Sleep -Seconds 8

    Save-FlyoutCapture 'Everyday' '00-icons-dark'
    # The web launcher's page needs time to load.
    Save-FlyoutCapture 'YouTube' '01-web-dark' -MinWidth 400 -Wait 18
    Save-TaskbarCapture '02-taskbar-dark'
    Save-FlyoutCapture 'Admin tools' '03-list-dark'
    Save-FlyoutCapture 'Creative' '04-columns-icons-dark'
    Save-FlyoutCapture 'Web' '05-columns-list-dark'
    Save-SettingsCaptures
}
finally {
    Stop-App
    Restore-OwnTrayIcons
    tzutil /s $savedTimeZone
}

# The Everyday pin stays until screenshot-desktop.ps1 -Mode Restore, which removes pins a run added.
Write-Output "capture finished: $locale"
