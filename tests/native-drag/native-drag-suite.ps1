#requires -Version 7.0
<## Exercises actual WinUI drag gestures and checks the settings saved by the installed app.
Run only on an explicitly designated disposable Windows desktop. It moves that desktop's
pointer, closes Little Launcher between cases, and temporarily replaces its local settings.
##>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedComputerName,
    [Parameter(Mandatory)][string]$SettingsPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$AllowDesktopInteraction,
    [ValidateSet('regular','native')][string]$HostMode = 'regular',
    [ValidateSet(1,2)][int]$Theme = 2,
    [ValidateSet(0,1,2)][int[]]$Modes = @(1,0,2),
    [ValidateSet('child-reorder','child-between-groups','child-to-column','item-to-empty-group','group-cross-column','group-over-child','group-same-column','cancel','group-pencil','focus-return-done','focus-body-done','preview-target-change','preview-target-cancel','toolbar-settings','item-into-group-end','item-after-group','child-after-own-group','item-before-group','item-into-group-header','group-boundary-cancel','group-end-leave')][string[]]$Cases = @('child-reorder','child-between-groups','child-to-column','item-to-empty-group','group-cross-column','group-over-child','group-same-column','cancel')
)
$ErrorActionPreference='Stop'
if (-not $AllowDesktopInteraction -or $env:COMPUTERNAME -ne $ExpectedComputerName) {
    throw 'An explicit dedicated-desktop opt-in and matching computer name are required.'
}
if ((Get-Process -Id $PID).SessionId -eq 0) { throw 'Run in an interactive desktop session, not the SSH service session.' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase,System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeDragInput {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr info);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr info);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr handle,uint flags);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle,out uint process);
 [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int left,top,right,bottom;}
 [StructLayout(LayoutKind.Sequential)] public struct GuiInfo {public uint cbSize,flags;public IntPtr active,focus,capture,menuOwner,moveSize,caret;public Rect caretRect;}
 [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int x;public int y; }
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
 [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
}
'@
if ([NativeDragInput]::GetSystemMetrics(80) -ne 1) {
    throw 'The absolute input and screenshots require a dedicated single-monitor test desktop.'
}
foreach ($existing in @('settings-before.json','results.json','summary.json')) {
    if (Test-Path (Join-Path $OutputDirectory $existing)) {
        throw 'Use a new output directory. Previous results and the original settings backup must be preserved.'
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$package=Get-AppxPackage -Name '27766TechnicallyReal.LittleLauncher' | Where-Object SignatureKind -eq Developer | Select-Object -First 1
if (-not $package) { throw 'A sideloaded Developer Little Launcher package is required.' }
$executable=Join-Path $package.InstallLocation 'LittleLauncher.exe'
$fixtureId='19d9b164-aef5-5cb1-bab2-f78a35907d20'
$script:appProcessId=0
$results=[Collections.Generic.List[object]]::new()
$backup=Join-Path $OutputDirectory 'settings-before.json'
$settingsExisted=$false
$backupReady=$false
$script:caseName='setup'

function New-TestItem([string]$Name) {
    return @{Name=$Name;Path='C:\Windows\System32\notepad.exe';IconGlyph=[string][char]0xE8A5}
}
function New-TestGroup([string]$Name,[string[]]$Children) {
    return @{Name=$Name;IsGroup=$true;Children=@($Children | ForEach-Object { New-TestItem $_ })}
}

# Each case starts from the same two-column topology, including a genuinely empty group.
function Write-Fixture([int]$Mode) {
    $items=@(
        (New-TestItem 'Alpha'),
        (New-TestGroup 'Group One' @('Birch','Cedar')),
        (New-TestGroup 'Group Two' @('Dogwood','Elm')),
        (New-TestGroup 'Empty Group' @()),
        (New-TestItem 'Omega'),
        @{IsColumnBreak=$true},
        (New-TestItem 'Beta'),
        (New-TestGroup 'Group Three' @('Fir','Hazel')),
        (New-TestItem 'Zeta')
    )
    $fixture=@{AppTheme=$Theme;FlyoutAnimationsEnabled=$false;Startup=$false;LastRunVersion=([version]$package.Version).ToString(3);LastKnownVersion=('v'+([version]$package.Version).ToString(3));Launchers=@(@{Id=$fixtureId;Name='Drag Tests';ViewMode=$Mode;IconModeIconsPerRow=3;Items=$items})}
    $parent=Split-Path $SettingsPath -Parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $fixture | ConvertTo-Json -Depth 12 | Set-Content $SettingsPath -Encoding utf8
}

# Target only this installed package; never terminate another build or another application.
function Stop-TestApp {
    $processes=@(Get-Process LittleLauncher -ErrorAction SilentlyContinue | Where-Object Path -eq $executable)
    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
            throw 'The test package is still running. Settings have not been replaced or restored.'
        }
    }
    $script:appProcessId=0
    Start-Sleep -Milliseconds 250
}

# UIA exposes parked native flyouts too. Exclude their negative/off-desktop bounds explicitly.
function Get-TestElement([string]$Name,[string]$ControlType) {
    $root=[System.Windows.Automation.AutomationElement]::RootElement
    $nameCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name)
    $matches=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$nameCondition)
    foreach ($element in $matches) {
        try {
            $current=$element.Current
            $bounds=$current.BoundingRectangle
            if ($current.ProcessId -eq $script:appProcessId -and $current.ControlType.ProgrammaticName -eq "ControlType.$ControlType" -and -not $current.IsOffscreen -and $bounds.X -ge 0 -and $bounds.Y -ge 0 -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) { return $element }
        } catch [System.Windows.Automation.ElementNotAvailableException] {}
    }
    return $null
}

function Wait-TestElement([string]$Name,[string]$ControlType) {
    $deadline=(Get-Date).AddSeconds(15)
    do {
        $element=Get-TestElement $Name $ControlType
        if ($element) { return $element }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    throw "Visible $ControlType '$Name' did not appear."
}

function Get-TestPoint([string]$Name,[string]$ControlType='ListItem',[double]$XFraction=0.5,[double]$YFraction=0.5) {
    $bounds=(Wait-TestElement $Name $ControlType).Current.BoundingRectangle
    return @{x=[int]($bounds.X+$bounds.Width*$XFraction);y=[int]($bounds.Y+$bounds.Height*$YFraction)}
}

function Invoke-TestClick($Point) {
    [NativeDragInput]::SetCursorPos($Point.x,$Point.y)|Out-Null
    Start-Sleep -Milliseconds 80
    [NativeDragInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [NativeDragInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
}

function Save-TestScreenshot([string]$Name) {
    $width=[NativeDragInput]::GetSystemMetrics(0)
    $height=[NativeDragInput]::GetSystemMetrics(1)
    $bitmap=[Drawing.Bitmap]::new($width,$height)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(0,0,0,0,$bitmap.Size)
        $bitmap.Save((Join-Path $OutputDirectory "$Name.png"),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose();$bitmap.Dispose() }
}

# Use a real press/move/release sequence through WinUI's native ListView gesture recognizer.
function Invoke-TestDrag($From,$To,[string]$ScreenshotName,[switch]$Cancel,[string]$TargetName,[string]$TargetType='ListItem',[object[]]$Waypoints=@(),[string[]]$GeometryNames=@()) {
    $targetBefore=$null
    $escapeHeld=$false
    $geometryBefore=$null
    $savedItems=$null
    if ($GeometryNames.Count -gt 0) {
        $geometryBefore=Get-TestDragGeometry $GeometryNames
        $savedItems=(Read-TestLauncher).Items|ConvertTo-Json -Depth 8 -Compress
    }
    if ($TargetName) {
        $targetBefore=(Wait-TestElement $TargetName $TargetType).Current.BoundingRectangle
    }
    @{from=$From;to=$To;foreground=[NativeDragInput]::GetForegroundWindow().ToInt64()}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $OutputDirectory ($ScreenshotName+'-input.json'))
    [NativeDragInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [NativeDragInput]::SetCursorPos($From.x,$From.y)|Out-Null
    Start-Sleep -Milliseconds 100
    [NativeDragInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    try {
        Start-Sleep -Milliseconds 150
        $cursor=[NativeDragInput+Point]::new()
        $gotCursor=[NativeDragInput]::GetCursorPos([ref]$cursor)
        @{phase='down';cursor=@{x=$cursor.x;y=$cursor.y};leftDown=([NativeDragInput]::GetAsyncKeyState(1) -lt 0);foreground=[NativeDragInput]::GetForegroundWindow().ToInt64()}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $OutputDirectory ($ScreenshotName+'-down.json'))
        # Cursor relocation alone did not deliver advancing WinUI PointerMoved points in
        # the VM. Inject absolute native movement while retaining the same press/release.
        $previous=$From
        foreach ($waypoint in $Waypoints) {
            Move-TestPointer $waypoint.point $previous -Steps 40
            Start-Sleep -Milliseconds 350
            Save-TestPointerWitness $waypoint.point ($ScreenshotName+'-'+$waypoint.name)
            @{expectedHint=$waypoint.hint;hintMustBeAboveBody=$true;point=$waypoint.point} |
                ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory ($ScreenshotName+'-'+$waypoint.name+'-preview.json'))
            Save-TestScreenshot ($ScreenshotName+'-'+$waypoint.name)
            if ($GeometryNames.Count -gt 0) {
                Save-TestDragGeometry $geometryBefore $GeometryNames $savedItems ($ScreenshotName+'-'+$waypoint.name)
            }
            $previous=$waypoint.point
        }
        Move-TestPointer $To $previous -Steps 40
        Start-Sleep -Milliseconds 350
        $cursor=[NativeDragInput+Point]::new()
        $gotCursor=[NativeDragInput]::GetCursorPos([ref]$cursor)
        @{phase='end';cursor=@{x=$cursor.x;y=$cursor.y};leftDown=([NativeDragInput]::GetAsyncKeyState(1) -lt 0);foreground=[NativeDragInput]::GetForegroundWindow().ToInt64()}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $OutputDirectory ($ScreenshotName+'-end.json'))
        Save-TestScreenshot $ScreenshotName
        if ($GeometryNames.Count -gt 0) {
            Save-TestDragGeometry $geometryBefore $GeometryNames $savedItems ($ScreenshotName+'-held-start')
            Start-Sleep -Milliseconds 600
            Save-TestDragGeometry $geometryBefore $GeometryNames $savedItems ($ScreenshotName+'-held-end')
            Save-TestScreenshot ($ScreenshotName+'-held-end')
        }
        if ($TargetName) {
            # Collections have not been mutated yet. The untouched target must stay still
            # while the insertion caret is held over it, independent of source opacity.
            $targetDuring=(Wait-TestElement $TargetName $TargetType).Current.BoundingRectangle
            $stable=$targetBefore.Equals($targetDuring)
            @{
                target=$TargetName
                before=@{x=$targetBefore.X;y=$targetBefore.Y;width=$targetBefore.Width;height=$targetBefore.Height}
                during=@{x=$targetDuring.X;y=$targetDuring.Y;width=$targetDuring.Width;height=$targetDuring.Height}
                stable=$stable
            } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory ($ScreenshotName+'-target-bounds.json'))
            if (-not $stable) { throw "Insertion feedback moved target '$TargetName' before the drop." }
        }
        if ($Cancel) {
            [NativeDragInput]::keybd_event(27,0,0,[UIntPtr]::Zero)
            $escapeHeld=$true
            # The native drag loop must observe Escape before mouse release. An immediate
            # key-down/key-up followed by mouse-up raced cancellation in the VM tray host.
            Start-Sleep -Milliseconds 150
            $escapeDown=[NativeDragInput]::GetAsyncKeyState(27) -lt 0
            $leftDown=[NativeDragInput]::GetAsyncKeyState(1) -lt 0
            @{
                phase='cancel-held'
                escapeDown=$escapeDown
                leftDown=$leftDown
                foreground=[NativeDragInput]::GetForegroundWindow().ToInt64()
                editToolbarVisible=$null -ne (Get-TestElement 'Add item' 'Button')
            } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory ($ScreenshotName+'-escape-held.json'))
            Save-TestScreenshot ($ScreenshotName+'-escape-held')
            if (-not $escapeDown -or -not $leftDown) { throw 'Escape and the drag button were not both held before cancellation.' }
        }
    } finally {
        try {
            [NativeDragInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
            if ($escapeHeld) { Start-Sleep -Milliseconds 150 }
        } finally {
            if ($escapeHeld) { [NativeDragInput]::keybd_event(27,0,2,[UIntPtr]::Zero) }
        }
    }
    Start-Sleep -Milliseconds 350
}

# Use the same physical companion deployment as shell shortcuts. Executing the copy
# inside WindowsApps from an external process can fail with AccessDenied.
function Get-TestCompanion {
    $dataDirectory=Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\RoamingState\LittleLauncher"
    $companion=Join-Path $dataDirectory 'LittleLauncherFlyout.exe'
    $breadcrumb=Join-Path $dataDirectory 'main-exe-path.txt'
    $packagedCompanion=Join-Path $package.InstallLocation 'LittleLauncherFlyout.exe'
    $expectedHash=(Get-FileHash -LiteralPath $packagedCompanion -Algorithm SHA256).Hash
    $deadline=(Get-Date).AddSeconds(15)
    $reason='The app has not deployed its physical companion yet.'

    # Process creation precedes EnsureFlyoutShortcut. Wait for its copy and breadcrumb
    # to finish together, without copying files or falling back to another installation.
    do {
        if ((Test-Path -LiteralPath $companion) -and (Test-Path -LiteralPath $breadcrumb)) {
            try {
                $mainPath=(Get-Content -LiteralPath $breadcrumb -Raw).Trim()
                $actualHash=(Get-FileHash -LiteralPath $companion -Algorithm SHA256).Hash
                $mainMatches=[StringComparer]::OrdinalIgnoreCase.Equals($mainPath,$executable)
                if ($mainMatches -and $actualHash -eq $expectedHash) {
                    @{path=$companion;mainExecutable=$mainPath;sha256=$actualHash;packagedSource=$packagedCompanion} |
                        ConvertTo-Json | Set-Content (Join-Path $OutputDirectory ($script:caseName+'-companion.json')) -Encoding utf8
                    return $companion
                }
                $reason="The deployed companion does not match the intended package (breadcrumb=$mainMatches, hash=$($actualHash -eq $expectedHash))."
            } catch {
                $reason="The app's companion deployment is not readable: $($_.Exception.Message)"
            }
        }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    throw $reason
}

function Start-TestSurface([int]$Mode) {
    Stop-TestApp
    Write-Fixture $Mode
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($package.PackageFamilyName)!App"
    $deadline=(Get-Date).AddSeconds(15)
    do {
        $process=Get-Process LittleLauncher -ErrorAction SilentlyContinue | Where-Object Path -eq $executable | Select-Object -First 1
        if ($process) { $script:appProcessId=$process.Id;break }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    if (-not $script:appProcessId) { throw 'The installed package did not launch.' }
    if ($HostMode -eq 'regular') {
        # App activation can leave Settings hidden after a previous tray-host run.
        # Companion action 4 explicitly opens AppSettings before UIA navigation.
        $companion=Get-TestCompanion
        Start-Process -FilePath $companion -ArgumentList @('--launcher',$fixtureId,'--action','4')
        # UIA performs setup only. Actual reorder actions below always use pointer input.
        $navigation=Wait-TestElement 'Launchers' 'ListItem'
        $navigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $button=Wait-TestElement 'Edit items in Drag Tests' 'Button'
        Start-Sleep -Milliseconds 400
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } else {
        # Companion action 2 opens the actual tray flyout directly in edit mode.
        $companion=Get-TestCompanion
        Start-Process -FilePath $companion -ArgumentList @('--launcher',$fixtureId,'--action','2')
    }
    $alpha=Wait-TestElement 'Alpha' 'ListItem'
    if ($HostMode -eq 'regular') {
        (Wait-TestElement 'Add item' 'Button').SetFocus()
    }
    # The native toolbar has a separate HWND. Derive the input window from the item,
    # and never focus the native toolbar when validating the flyout's active window.
    $window=$alpha
    $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($window.Current.NativeWindowHandle -eq 0) { $window=$walker.GetParent($window) }
    $rootHandle=[NativeDragInput]::GetAncestor([IntPtr]$window.Current.NativeWindowHandle,2)
    $activated=[NativeDragInput]::SetForegroundWindow($rootHandle)
    Start-Sleep -Milliseconds 200
    if ([NativeDragInput]::GetForegroundWindow() -ne $rootHandle) {
        $bounds=$window.Current.BoundingRectangle
        Invoke-TestClick @{x=[int]($bounds.X+100);y=[int]($bounds.Y+16)}
        Start-Sleep -Milliseconds 200
    }
    if ([NativeDragInput]::GetForegroundWindow() -ne $rootHandle) { throw "Editor foreground activation failed (SetForegroundWindow=$activated)." }
    $gui=[NativeDragInput+GuiInfo]::new()
    $gui.cbSize=[uint32][Runtime.InteropServices.Marshal]::SizeOf($gui)
    $ownerProcess=[uint32]0
    $thread=[NativeDragInput]::GetWindowThreadProcessId($rootHandle,[ref]$ownerProcess)
    $guiRead=[NativeDragInput]::GetGUIThreadInfo($thread,[ref]$gui)
    @{root=$rootHandle.ToInt64();foreground=[NativeDragInput]::GetForegroundWindow().ToInt64();active=$gui.active.ToInt64();focus=$gui.focus.ToInt64();focusRoot=([NativeDragInput]::GetAncestor($gui.focus,2)).ToInt64();capture=$gui.capture.ToInt64();pid=$ownerProcess;read=$guiRead}|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory ($script:caseName+'-focus-precondition.json'))
    if (-not $guiRead -or $gui.active -ne $rootHandle) {
        throw 'The intended item surface is not the active input window.'
    }
    if ($HostMode -eq 'regular' -and [NativeDragInput]::GetAncestor($gui.focus,2) -ne $rootHandle) {
        throw 'Keyboard focus is outside the intended regular editor.'
    }
    Start-Sleep -Seconds 3
}

function Read-TestLauncher {
    return (Get-Content $SettingsPath -Raw|ConvertFrom-Json).Launchers | Where-Object Id -eq $fixtureId
}
function Assert-TestEqual($Actual,$Expected,[string]$Description) {
    $actualJson=ConvertTo-Json -InputObject @($Actual) -Compress -Depth 8
    $expectedJson=ConvertTo-Json -InputObject @($Expected) -Compress -Depth 8
    if ($actualJson -ne $expectedJson) { throw "$Description. Expected $expectedJson; actual $actualJson" }
}
function Assert-TestIntegrity($Launcher) {
    $names=@($Launcher.Items | Where-Object {-not $_.IsColumnBreak} | ForEach-Object { $_.Name; $_.Children.Name } | Where-Object {$_})
    Assert-TestEqual ($names|Sort-Object) (@('Alpha','Group One','Birch','Cedar','Group Two','Dogwood','Elm','Empty Group','Omega','Beta','Group Three','Fir','Hazel','Zeta')|Sort-Object) 'All original items occur exactly once'
    if (@($Launcher.Items|Where-Object IsColumnBreak).Count -ne 1) { throw 'Column boundary was lost or duplicated.' }
    if (@($Launcher.Items.Children|Where-Object IsGroup).Count) { throw 'A group was nested inside another group.' }
}

. (Join-Path $PSScriptRoot 'interaction-cases.ps1')

try {
    [NativeDragInput]::SetThreadExecutionState(2147483651)|Out-Null
    # Stop before the snapshot so pending writes cannot race the original backup.
    Stop-TestApp
    $settingsExisted=Test-Path $SettingsPath
    if ($settingsExisted) { Copy-Item $SettingsPath $backup }
    $backupReady=$true
    foreach ($mode in $Modes) {
        foreach ($case in $Cases) {
            $name="$HostMode-theme-$Theme-mode-$mode-$case"
            $script:caseName=$name
            $timer=[Diagnostics.Stopwatch]::StartNew()
            try {
                $stage='setup'
                Start-TestSurface $mode
                (Get-Process -Id $script:appProcessId).Modules | Where-Object ModuleName -eq 'Microsoft.UI.Xaml.dll' | Select-Object FileName,@{n='FileVersion';e={$_.FileVersionInfo.FileVersion}} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory ($name+'-winui-runtime.json'))
                $stage='gesture'
                $before=Read-TestLauncher
                switch ($case) {
                    'child-reorder' {
                        $target=Get-TestPoint 'Cedar' 'ListItem' 0.8 0.65
                        Invoke-TestDrag (Get-TestPoint 'Birch') $target $name -TargetName 'Cedar'
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Cedar','Birch') 'Child reorder'
                    }
                    'child-between-groups' {
                        Invoke-TestDrag (Get-TestPoint 'Birch') (Get-TestPoint 'Elm' 'ListItem' 0.15 0.15) $name -TargetName 'Elm'
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Cedar') 'Source group after child move'
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group Two').Children.Name @('Dogwood','Birch','Elm') 'Destination group after child move'
                    }
                    'child-to-column' {
                        Invoke-TestDrag (Get-TestPoint 'Birch') (Get-TestPoint 'Beta' 'ListItem' 0.15 0.15) $name -TargetName 'Beta'
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Cedar') 'Source group after extraction'
                        $split=[array]::FindIndex([object[]]$after.Items,[Predicate[object]]{param($i) [bool]$i.IsColumnBreak})
                        Assert-TestEqual $after.Items[($split+1)..($after.Items.Count-1)].Name @('Birch','Beta','Group Three','Zeta') 'Child extracted into other column'
                    }
                    'item-to-empty-group' {
                        $bounds=(Wait-TestElement 'Empty Group (group)' 'ListItem').Current.BoundingRectangle
                        $target=@{x=[int]($bounds.X+$bounds.Width/2);y=[int]($bounds.Bottom-10)}
                        Invoke-TestDrag (Get-TestPoint 'Alpha') $target $name -TargetName 'Empty Group (group)'
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Empty Group').Children.Name @('Alpha') 'Empty group receives item'
                    }
                    'group-cross-column' {
                        Invoke-TestDrag (Get-TestPoint 'Group One' 'Text') (Get-TestPoint 'Beta' 'ListItem' 0.15 0.15) $name -TargetName 'Beta'
                        $after=Read-TestLauncher
                        $split=[array]::FindIndex([object[]]$after.Items,[Predicate[object]]{param($i) [bool]$i.IsColumnBreak})
                        Assert-TestEqual $after.Items[($split+1)..($after.Items.Count-1)].Name @('Group One','Beta','Group Three','Zeta') 'Whole group crosses columns'
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Birch','Cedar') 'Group children stay together'
                    }
                    'group-over-child' {
                        Invoke-TestDrag (Get-TestPoint 'Group One' 'Text') (Get-TestPoint 'Hazel' 'ListItem' 0.8 0.85) $name -TargetName 'Hazel'
                        $after=Read-TestLauncher
                        $split=[array]::FindIndex([object[]]$after.Items,[Predicate[object]]{param($i) [bool]$i.IsColumnBreak})
                        Assert-TestEqual $after.Items[($split+1)..($after.Items.Count-1)].Name @('Beta','Group Three','Group One','Zeta') 'Group body accepts whole-group reorder without nesting'
                    }
                    'group-same-column' {
                        Invoke-TestDrag (Get-TestPoint 'Group One' 'Text') (Get-TestPoint 'Elm' 'ListItem' 0.8 0.85) $name -TargetName 'Elm'
                        $after=Read-TestLauncher
                        $split=[array]::FindIndex([object[]]$after.Items,[Predicate[object]]{param($i) [bool]$i.IsColumnBreak})
                        Assert-TestEqual $after.Items[0..($split-1)].Name @('Alpha','Group Two','Group One','Empty Group','Omega') 'Group reorder over child in same column'
                    }
                    'cancel' {
                        Invoke-TestDrag (Get-TestPoint 'Group One' 'Text') (Get-TestPoint 'Beta') $name -Cancel -TargetName 'Beta'
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Escape cancels without changing saved items'
                    }
                    'group-pencil' {
                        Test-GroupPencil $mode $name
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Cancelling pencil rename preserves saved items'
                    }
                    'focus-return-done' {
                        Test-FocusReturnDone $name
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Focus return and Done preserve saved items'
                    }
                    'focus-body-done' {
                        Test-FocusReturnDone $name -ReactivateBody
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Body return and Done preserve saved items'
                    }
                    'preview-target-change' {
                        $direction=if ($mode -eq 1) { 'above' } else { 'before' }
                        $waypoints=@(
                            @{name='column';point=(Get-TestPoint 'Beta' 'ListItem' 0.15 0.15);hint="Move $direction Beta"},
                            @{name='other-group';point=(Get-TestPoint 'Hazel' 'ListItem' 0.15 0.15);hint='Into Group Three: before Hazel'}
                        )
                        Invoke-TestDrag (Get-TestPoint 'Birch') (Get-TestPoint 'Elm' 'ListItem' 0.15 0.15) $name -TargetName 'Elm' -Waypoints $waypoints
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Cedar') 'Preview target changes preserve source identity'
                        Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group Two').Children.Name @('Dogwood','Birch','Elm') 'Drop uses final target after changing preview captions'
                    }
                    'preview-target-cancel' {
                        $direction=if ($mode -eq 1) { 'above' } else { 'before' }
                        $waypoints=@(
                            @{name='column';point=(Get-TestPoint 'Beta' 'ListItem' 0.15 0.15);hint="Move $direction Beta"},
                            @{name='other-group';point=(Get-TestPoint 'Hazel' 'ListItem' 0.15 0.15);hint='Into Group Three: before Hazel'}
                        )
                        Invoke-TestDrag (Get-TestPoint 'Birch') (Get-TestPoint 'Elm' 'ListItem' 0.15 0.15) $name -Cancel -TargetName 'Elm' -Waypoints $waypoints
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Escape after changing preview targets preserves saved items'
                    }
                    { $_ -in @('item-into-group-end','item-after-group','child-after-own-group','item-before-group','item-into-group-header','group-boundary-cancel','group-end-leave') } {
                        Test-GroupBoundaryDrop $case $name $mode
                        $after=Read-TestLauncher
                        $firstColumn=@($after.Items | Select-Object -First ([array]::FindIndex([object[]]$after.Items,[Predicate[object]]{param($i) [bool]$i.IsColumnBreak})))
                        if ($case -in @('item-into-group-end','item-into-group-header')) {
                            Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Birch','Cedar','Alpha') 'Interior and header drops append inside the named group'
                            Assert-TestEqual $firstColumn.Name @('Group One','Group Two','Empty Group','Omega') 'Inside drops do not create a top-level sibling'
                        } elseif ($case -eq 'item-after-group') {
                            Assert-TestEqual $firstColumn.Name @('Group One','Alpha','Group Two','Empty Group','Omega') 'Outer bottom band inserts after the whole group'
                            Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Birch','Cedar') 'After-group drop preserves group membership'
                        } elseif ($case -eq 'child-after-own-group') {
                            Assert-TestEqual $firstColumn.Name @('Alpha','Group One','Birch','Group Two','Empty Group','Omega') 'Child leaves its own group through its bottom band'
                            Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Cedar') 'Extraction retains other children'
                        } elseif ($case -eq 'item-before-group') {
                            Assert-TestEqual $firstColumn.Name @('Alpha','Beta','Group One','Group Two','Empty Group','Omega') 'Outer top band inserts before the whole group'
                            Assert-TestEqual ($after.Items|Where-Object Name -eq 'Group One').Children.Name @('Birch','Cedar') 'Before-group drop preserves group membership'
                        } else {
                            Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Cancelling across inside/outside targets preserves saved items'
                        }
                    }
                    'toolbar-settings' {
                        Test-ToolbarSettings $name
                        $after=Read-TestLauncher
                        Assert-TestEqual ($after.Items|ConvertTo-Json -Depth 8 -Compress) ($before.Items|ConvertTo-Json -Depth 8 -Compress) 'Opening compact toolbar settings preserves saved items'
                    }
                    default { throw "Unknown case $case" }
                }
                Assert-TestIntegrity $after
                Save-TestScreenshot "$name-after"
                $results.Add(@{hostMode=$HostMode;theme=$Theme;mode=$mode;case=$case;passed=$true;seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2)})
            } catch {
                Save-TestScreenshot "$name-failure"
                $results.Add(@{hostMode=$HostMode;theme=$Theme;mode=$mode;case=$case;stage=$stage;passed=$false;error=$_.Exception.Message;seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2)})
            }
            $lastLog=Get-ChildItem (Join-Path (Split-Path $SettingsPath -Parent) 'logs*.txt') | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($lastLog) { Copy-Item $lastLog.FullName (Join-Path $OutputDirectory ($name+'-events.log')) -Force }
            $results|ConvertTo-Json -Depth 8|Set-Content (Join-Path $OutputDirectory 'results.json') -Encoding utf8
        }
    }
} finally {
    try {
        Stop-TestApp
        if ($backupReady) {
            if ($settingsExisted) {
                Copy-Item $backup $SettingsPath -Force
            } else {
                Remove-Item $SettingsPath -ErrorAction SilentlyContinue
            }
        }
    } finally {
        [NativeDragInput]::SetThreadExecutionState(2147483648)|Out-Null
    }
}
$failures=@($results|Where-Object {-not $_.passed})
@{hostMode=$HostMode;theme=$Theme;packageVersion=[string]$package.Version;packagePath=$package.InstallLocation;signatureKind=[string]$package.SignatureKind;passed=$results.Count-$failures.Count;failed=$failures.Count;cases=$results}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
if ($failures.Count) { exit 1 }
