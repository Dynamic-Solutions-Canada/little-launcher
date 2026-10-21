# Dot-sourced by the guarded native suite. These cases use its synthetic fixture and cleanup.

# Deliver actual motion; moving only the cursor did not reliably update XAML pointer points.
function Move-TestPointer($To,$From=$null,[int]$Steps=12) {
    if (-not $From) {
        $cursor=[NativeDragInput+Point]::new()
        if (-not [NativeDragInput]::GetCursorPos([ref]$cursor)) { throw 'Could not read pointer position.' }
        $From=@{x=$cursor.x;y=$cursor.y}
    }
    for ($step=1;$step -le $Steps;$step++) {
        $x=$From.x+($To.x-$From.x)*$step/$Steps
        $y=$From.y+($To.y-$From.y)*$step/$Steps
        $absoluteX=[uint32][Math]::Round($x*65535/([NativeDragInput]::GetSystemMetrics(0)-1))
        $absoluteY=[uint32][Math]::Round($y*65535/([NativeDragInput]::GetSystemMetrics(1)-1))
        [NativeDragInput]::mouse_event(32769,$absoluteX,$absoluteY,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 25
    }
}

# Resolve the real top-level HWND from a UIA item, including nested XAML islands.
function Get-TestRootHandle($Element) {
    $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($Element -and $Element.Current.NativeWindowHandle -eq 0) { $Element=$walker.GetParent($Element) }
    if (-not $Element) { throw 'The test element has no window ancestor.' }
    return [NativeDragInput]::GetAncestor([IntPtr]$Element.Current.NativeWindowHandle,2)
}

function Get-TestElementPoint($Element) {
    $bounds=$Element.Current.BoundingRectangle
    return @{x=[int]($bounds.X+$bounds.Width/2);y=[int]($bounds.Y+$bounds.Height/2)}
}

# Preserve actual cursor and hit ancestry alongside screenshots, without pixel assertions.
function Save-TestPointerWitness($Point,[string]$Name) {
    $cursor=[NativeDragInput+Point]::new()
    if (-not [NativeDragInput]::GetCursorPos([ref]$cursor)) { throw 'Could not read pointer position.' }
    $element=[System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($cursor.x,$cursor.y))
    $chain=[Collections.Generic.List[object]]::new()
    $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($depth=0;$element -and $depth -lt 8;$depth++) {
        $chain.Add(@{name=$element.Current.Name;type=$element.Current.ControlType.ProgrammaticName;pid=$element.Current.ProcessId})
        $element=$walker.GetParent($element)
    }
    @{intended=$Point;actual=@{x=$cursor.x;y=$cursor.y};chain=$chain} |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory ($Name+'-hit.json'))
    if ([Math]::Abs($cursor.x-$Point.x) -gt 1 -or [Math]::Abs($cursor.y-$Point.y) -gt 1) {
        throw 'The pointer did not reach the intended interaction target.'
    }
}

# Compare inside/end and sibling drops at the same group with physical, DPI-scaled points.
function Test-GroupBoundaryDrop([string]$Case,[string]$Name,[int]$Mode) {
    $group=Wait-TestElement 'Group One (group)' 'ListItem'
    $bounds=$group.Current.BoundingRectangle
    $lastChild=(Wait-TestElement 'Cedar' 'ListItem').Current.BoundingRectangle
    $scale=[NativeDragInput]::GetDpiForWindow((Get-TestRootHandle $group))/96.0
    $x=[int]($lastChild.X+$lastChild.Width*0.8)
    $inside=@{x=$x;y=[int]($bounds.Bottom-12*$scale)}
    $after=@{x=$x;y=[int]($bounds.Bottom-3*$scale)}
    $waypoints=@(
        @{name='inside-end';point=$inside;hint='Into Group One: at end'},
        @{name='after-group';point=$after;hint='Move after Group One'},
        @{name='inside-again';point=$inside;hint='Into Group One: at end'}
    )
    $target=$after
    $source=Get-TestPoint 'Alpha'
    $cancel=$false
    $expectedHint='Move after Group One'
    $expectedTint=$false
    switch ($Case) {
        'item-into-group-end' {
            $target=$inside
            $expectedHint='Into Group One: at end'
            $expectedTint=$true
        }
        'item-into-group-header' {
            $target=Get-TestPoint 'Group One' 'Text'
            $expectedHint='Into Group One: at end'
            $expectedTint=$true
        }
        'item-before-group' {
            $target=@{x=[int]($bounds.X+$bounds.Width/2);y=[int]($bounds.Y+3*$scale)}
            $source=Get-TestPoint 'Beta'
            $expectedHint='Move before Group One'
        }
        'child-after-own-group' { $source=Get-TestPoint 'Birch' }
        'group-boundary-cancel' { $cancel=$true }
        'group-end-leave' {
            $target=Get-TestPoint 'Beta' 'ListItem' 0.15 0.15
            $cancel=$true
            $expectedHint=if ($Mode -eq 1) { 'Move above Beta' } else { 'Move before Beta' }
        }
        'item-after-group' { }
        default { throw "Unknown boundary case $Case" }
    }
    @{
        group='Group One';groupBefore=@{x=$bounds.X;y=$bounds.Y;width=$bounds.Width;height=$bounds.Height}
        scale=$scale;inside=$inside;after=$after;expectedHint=$expectedHint
        expectedGroupTint=$expectedTint
        expectedCaret= $(if ($expectedTint) { 'inset marker overlapping progressively faded lower edge of last child' } else { 'outside full-width sibling marker or ordinary target caret' })
        expectedLastChildFade=$expectedTint
        expectedFadeDirection='clear upper area, increasing fade toward lower insertion edge'
        expectedUniformDim=$false
        expectedGroupOutlineAboveFade=$true
        geometryMustRemainFixed=$true
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory ($Name+'-boundary-expectations.json'))
    $names=@('Group One (group)','Cedar','Group Two (group)')
    $geometry=Get-TestDragGeometry $names
    Invoke-TestDrag $source $target $Name -Cancel:$cancel -TargetName 'Group One (group)' -Waypoints $waypoints -GeometryNames $names
    if ($cancel) {
        # The regular editor's Escape accelerator closes its window. Bounds were already
        # checked at every waypoint and held sample; do not query destroyed UIA children.
        # The caller still verifies unchanged saved items after this function returns.
        $regularHostClosed=$HostMode -eq 'regular' -and -not (Get-TestElement 'Group One (group)' 'ListItem')
        if ($regularHostClosed) {
            @{
                expectedHostClose=$true
                hostMode=$HostMode
                postCloseGeometryAsserted=$false
                preEscapeGeometryAsserted=$true
            } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory ($Name+'-cancel-host.json'))
        } else {
            Assert-TestDragGeometry $geometry (Get-TestDragGeometry $names) 'Cancelling or leaving end feedback restores exact item geometry'
        }
    }
}

# Capture untouched receiver, final child and following group; cue pixels remain human-reviewed.
function Get-TestDragGeometry([string[]]$Names) {
    return @($Names | ForEach-Object {
        $rect=(Wait-TestElement $_ 'ListItem').Current.BoundingRectangle
        [ordered]@{name=$_;x=$rect.X;y=$rect.Y;width=$rect.Width;height=$rect.Height}
    })
}

function Assert-TestDragGeometry($Before,$During,[string]$Label) {
    Assert-TestEqual @($During) @($Before) $Label
}

# A held gesture may change only passive decoration, never geometry or saved fixture items.
function Save-TestDragGeometry($Before,[string[]]$Names,[string]$SavedItems,[string]$Name) {
    $during=Get-TestDragGeometry $Names
    $unchangedItems=((Read-TestLauncher).Items|ConvertTo-Json -Depth 8 -Compress) -eq $SavedItems
    $stable=(ConvertTo-Json -InputObject @($during) -Depth 5 -Compress) -eq (ConvertTo-Json -InputObject @($Before) -Depth 5 -Compress)
    @{before=$Before;during=$during;stable=$stable;savedItemsUnchanged=$unchangedItems} |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory ($Name+'-geometry.json'))
    Assert-TestDragGeometry $Before $during 'Passive end feedback keeps receiver, last child and following group fixed'
    if (-not $unchangedItems) { throw 'Saved items changed before the physical drop.' }
}

# Click the drawn group pencil itself, cancel its real rename window, and retain geometry.
function Test-GroupPencil([int]$Mode,[string]$Name) {
    $label=Wait-TestElement 'Group One' 'Text'
    $group=$label
    $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($group.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $group=$walker.GetParent($group) }
    $before=$group.Current.BoundingRectangle
    $childBefore=(Wait-TestElement 'Birch' 'ListItem').Current.BoundingRectangle
    Move-TestPointer (Get-TestElementPoint $label)
    Start-Sleep -Milliseconds 350
    $labelBounds=$label.Current.BoundingRectangle
    $scale=[NativeDragInput]::GetDpiForWindow((Get-TestRootHandle $group))/96.0
    $inset=if ($Mode -eq 2) { 9 } else { 12 }
    $point=@{x=[int]($before.Right-$inset*$scale);y=[int]($labelBounds.Y+$labelBounds.Height/2)}
    Move-TestPointer $point
    Start-Sleep -Milliseconds 350
    Save-TestPointerWitness $point ($Name+'-pencil')
    Save-TestScreenshot ($Name+'-pencil-before')
    Invoke-TestClick $point
    $dialog=Wait-TestElement 'Rename Group' 'Window'
    Save-TestScreenshot ($Name+'-rename-open')
    $cancel=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Cancel'))
    if (-not $cancel) { throw 'The real rename dialog has no Cancel action.' }
    Invoke-TestClick (Get-TestElementPoint $cancel)
    Start-Sleep -Milliseconds 350
    if (Get-TestElement 'Rename Group' 'Window') { throw 'The rename dialog did not cancel.' }
    if (-not $before.Equals((Wait-TestElement 'Group One (group)' 'ListItem').Current.BoundingRectangle) -or
        -not $childBefore.Equals((Wait-TestElement 'Birch' 'ListItem').Current.BoundingRectangle)) {
        throw 'Group pencil hover or dialog cancellation changed item geometry.'
    }
}

# Switch to a real isolated Notepad document, finish editing through actual pointer input,
# then click that document again. Native Done must rearm dismissal; regular Done must close.
function Test-FocusReturnDone([string]$Name,[switch]$ReactivateBody) {
    if (Get-Process Notepad -ErrorAction SilentlyContinue) {
        throw 'Focus cases require no existing Notepad process, so unrelated documents are never closed.'
    }
    $surface=Get-TestRootHandle (Wait-TestElement 'Alpha' 'ListItem')
    $documentName='LL-Drag-Focus-'+[Guid]::NewGuid().ToString('N')
    $document=Join-Path $OutputDirectory ($documentName+'.txt')
    'Synthetic Little Launcher focus target. No editing is performed.' | Set-Content $document
    $focus=$null
    $focusProcessId=0
    try {
        Start-Process notepad.exe -ArgumentList ('"'+$document+'"')
        $deadline=(Get-Date).AddSeconds(15)
        do {
            $windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
            foreach ($window in $windows) {
                if ($window.Current.Name -like "*$documentName*" -and $window.Current.NativeWindowHandle -ne 0) {
                    $focus=$window
                    $focusProcessId=$window.Current.ProcessId
                    break
                }
            }
            if ($focus) { break }
            Start-Sleep -Milliseconds 150
        } while ((Get-Date) -lt $deadline)
        if (-not $focus) { throw 'The isolated Notepad document did not appear.' }
        $foreign=Get-TestRootHandle $focus
        if ($HostMode -eq 'regular') {
            # Keep our document's caption at the screen's left edge and below the editor
            # title. A foreground Notepad previously covered the intended return point.
            $placement=$focus.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
            if (-not $placement.Current.CanMove) { throw 'The isolated Notepad document cannot be positioned for this test.' }
            $placement.Move(0,[NativeDragInput]::GetSystemMetrics(1)-180)
            Start-Sleep -Milliseconds 250
        }
        [NativeDragInput]::SetForegroundWindow($foreign)|Out-Null
        $foreignBounds=$focus.Current.BoundingRectangle
        $away=@{x=[int]($foreignBounds.X+100);y=[int]($foreignBounds.Y+16)}
        Invoke-TestClick $away
        Start-Sleep -Milliseconds 300
        $beforeDone=[NativeDragInput]::GetForegroundWindow()
        if ($beforeDone -ne $foreign) { throw 'The foreign document did not take foreground.' }
        if (-not (Get-TestElement 'Done' 'Button')) { throw 'Editing ended while focus was away.' }

        if ($ReactivateBody -or $HostMode -eq 'regular') {
            $surfaceElement=[System.Windows.Automation.AutomationElement]::FromHandle($surface)
            $bounds=$surfaceElement.Current.BoundingRectangle
            $return=if ($HostMode -eq 'native') {
                @{x=[int]($bounds.X+$bounds.Width/2);y=[int]($bounds.Y+3)}
            } else {
                @{x=[int]($bounds.X+100);y=[int]($bounds.Y+16)}
            }
            $hit=[System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($return.x,$return.y))
            if ((Get-TestRootHandle $hit) -ne $surface) { throw 'The intended editor return point is occluded; no product assertion was attempted.' }
            Move-TestPointer $return
            Save-TestPointerWitness $return ($Name+'-return')
            Invoke-TestClick $return
            Start-Sleep -Milliseconds 200
            if ([NativeDragInput]::GetForegroundWindow() -ne $surface) { throw 'The physical return click did not reactivate the editor.' }
        }
        Invoke-TestClick (Get-TestPoint 'Done' 'Button')
        Start-Sleep -Milliseconds 500
        $afterDone=[NativeDragInput]::GetForegroundWindow()
        $doneGone=$null -eq (Get-TestElement 'Done' 'Button')
        $visibleAfterDone=$null -ne (Get-TestElement 'Alpha' 'ListItem')
        Invoke-TestClick $away
        Start-Sleep -Milliseconds 600
        $afterAway=[NativeDragInput]::GetForegroundWindow()
        $stillVisible=$null -ne (Get-TestElement 'Alpha' 'ListItem')
        @{surfaceRoot=$surface.ToInt64();foreignRoot=$foreign.ToInt64();foregroundBeforeDone=$beforeDone.ToInt64();
            foregroundAfterDone=$afterDone.ToInt64();foregroundAfterAway=$afterAway.ToInt64();
            doneGone=$doneGone;visibleAfterDone=$visibleAfterDone;visibleAfterAway=$stillVisible;reactivateBody=[bool]$ReactivateBody} |
            ConvertTo-Json | Set-Content (Join-Path $OutputDirectory ($Name+'-focus-lifecycle.json'))
        Save-TestScreenshot ($Name+'-dismissed')
        if (-not $doneGone -or $stillVisible -or $afterAway -ne $foreign) { throw 'Done followed by an outside click did not finish or dismiss the editor.' }
        if ($HostMode -eq 'native' -and (-not $visibleAfterDone -or $afterDone -ne $surface)) {
            throw 'Native Done did not restore the visible flyout activation before the outside click.'
        }
        if ($HostMode -eq 'regular' -and $visibleAfterDone) { throw 'Regular Done did not close its editor.' }
    } finally {
        # No Notepad existed on entry. Terminate only the exact process owning this document.
        if ($focusProcessId -ne 0) {
            $owned=Get-Process -Id $focusProcessId -ErrorAction SilentlyContinue
            if ($owned -and $owned.ProcessName -eq 'Notepad') { Stop-Process -Id $focusProcessId -Force }
        }
    }
}

# Keep the settings action out of the regular wrapping row, then physically open its dialog.
function Test-ToolbarSettings([string]$Name) {
    $surface=Get-TestRootHandle (Wait-TestElement 'Alpha' 'ListItem')
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($surface)
    $scale=[NativeDragInput]::GetDpiForWindow($surface)/96.0
    $sizes=if ($HostMode -eq 'regular') { @('initial','narrow') } else { @('native') }
    foreach ($size in $sizes) {
        if ($size -eq 'narrow') {
            $resize=$window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
            if (-not $resize.Current.CanResize) { throw 'The regular editor cannot be resized for the wrapping check.' }
            $resize.Resize(430*$scale,$window.Current.BoundingRectangle.Height)
            Start-Sleep -Milliseconds 400
        }
        $settings=Wait-TestElement 'Launcher settings' 'Button'
        $bounds=$settings.Current.BoundingRectangle
        if ($bounds.Width -gt 32*$scale+1 -or $bounds.Height -gt 32*$scale+1) { throw 'Launcher settings is not a compact icon button.' }
        $editor=$window.Current.BoundingRectangle
        $add=(Wait-TestElement 'Add item' 'Button').Current.BoundingRectangle
        @{size=$size;settings=@{x=$bounds.X;y=$bounds.Y;width=$bounds.Width;height=$bounds.Height};
            editor=@{x=$editor.X;y=$editor.Y;width=$editor.Width;height=$editor.Height};
            addItem=@{x=$add.X;y=$add.Y;width=$add.Width;height=$add.Height}} |
            ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory ($Name+'-'+$size+'-toolbar.json'))
        Save-TestScreenshot ($Name+'-'+$size+'-toolbar')
        if ($HostMode -eq 'regular') {
            # UIA outer window bounds include invisible resize borders. The footer shares
            # the toolbar's body padding and is the correct visible right-edge reference.
            $footer=(Wait-TestElement 'Done' 'Button').Current.BoundingRectangle
            if ([Math]::Abs($bounds.Right-$footer.Right) -gt 2*$scale -or
                [Math]::Abs($bounds.Y-$add.Y) -gt 2*$scale) { throw 'The settings icon is not anchored to the top right of the regular toolbar.' }
            foreach ($action in @('Add item','Add group','Add column')) {
                if ((Wait-TestElement $action 'Button').Current.BoundingRectangle.Right -gt $bounds.X-4*$scale) {
                    throw 'A wrapped primary action overlaps the independent settings icon.'
                }
            }
        }
    }
    $point=Get-TestElementPoint (Wait-TestElement 'Launcher settings' 'Button')
    Move-TestPointer $point
    Start-Sleep -Milliseconds 700
    Save-TestPointerWitness $point ($Name+'-settings')
    Save-TestScreenshot ($Name+'-tooltip')
    Invoke-TestClick $point
    $dialog=Wait-TestElement 'Launcher Settings' 'Window'
    Save-TestScreenshot ($Name+'-settings-open')
    $done=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Done'))
    if (-not $done) { throw 'Launcher Settings has no Done action.' }
    Invoke-TestClick (Get-TestElementPoint $done)
    Start-Sleep -Milliseconds 400
    if (Get-TestElement 'Launcher Settings' 'Window') { throw 'Launcher Settings did not close.' }
}
