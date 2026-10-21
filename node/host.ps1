# UI Automation and pixel-slot helper for the Node MCP server.
# stdout is one JSON object per line. Diagnostics go to stderr.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$script:Maps = @{}

function Reply($id, $ok, $result, $error) {
    $payload = @{ id = $id; ok = [bool]$ok }
    if ($ok) { $payload.result = $result } else { $payload.error = [string]$error }
    $json = $payload | ConvertTo-Json -Compress -Depth 8
    [Console]::Out.WriteLine($json)
    [Console]::Out.Flush()
}

function Test-MouseUp {
    Add-Type -AssemblyName System.Windows.Forms
    $buttons = [System.Windows.Forms.Control]::MouseButtons
    if ($buttons -ne [System.Windows.Forms.MouseButtons]::None) {
        throw 'A mouse button is down, so Paint.NET was left alone.'
    }
}

function Get-TopWindows([int]$procId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
}

function Test-ModalWindow($el) {
    try {
        $pattern = $el.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        return [bool]$pattern.Current.IsModal
    } catch {
        return $false
    }
}

function Get-Modals([int]$procId) {
    $found = @()
    foreach ($window in (Get-TopWindows $procId)) {
        if (Test-ModalWindow $window) { $found += $window }
    }
    return $found
}

function Get-Main([int]$procId) {
    foreach ($window in (Get-TopWindows $procId)) {
        if ($window.Current.AutomationId -eq 'MainForm') { return $window }
    }
    throw "Paint.NET process $procId has no main window."
}

function Assert-NoModal([int]$procId) {
    $modals = @(Get-Modals $procId)
    if ($modals.Count -gt 0) {
        $text = $modals[0].Current.Name
        throw "Paint.NET already has a dialog open. $text"
    }
}

function Find-ByName($el, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ById($el, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-Children($el) {
    return @($el.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
}

function Expand-El($el) {
    $pattern = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $pattern.Expand()
}

function Invoke-El($el) {
    $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-MenuBar($window) {
    # WinForms exposes a System menu bar before PdnMainMenu. FindFirst would
    # return that bar, whose only child is System, and miss File through Effects.
    $bars = @()
    foreach ($typeName in @('MenuBar', 'Menu')) {
        $type = [System.Windows.Automation.ControlType]::$typeName
        $cond = New-Object System.Windows.Automation.PropertyCondition (
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
        $bars += @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
    }
    foreach ($bar in $bars) {
        if ($bar.Current.AutomationId -eq 'PdnMainMenu') { return $bar }
    }
    foreach ($bar in $bars) {
        foreach ($child in (Get-Children $bar)) {
            if ($child.Current.Name -eq 'File') { return $bar }
        }
    }
    throw 'Paint.NET has no UI Automation menu bar.'
}

function Get-TopMenu($procId, [string]$topName) {
    Test-MouseUp
    Assert-NoModal $procId
    $window = Get-Main $procId
    $bar = Get-MenuBar $window
    foreach ($child in (Get-Children $bar)) {
        if ($child.Current.Name -eq $topName) { return $child }
    }
    throw "Paint.NET has no $topName menu."
}

function Invoke-MenuItem([int]$procId, [string]$topName, [string]$itemName) {
    $top = Get-TopMenu $procId $topName
    Expand-El $top
    Start-Sleep -Milliseconds 120
    $item = Find-ByName $top $itemName
    if (-not $item) { throw "The $topName menu has no item named $itemName." }
    Invoke-El $item
}

function Invoke-Effect([int]$procId, [string]$effectName) {
    Test-MouseUp
    Assert-NoModal $procId
    $window = Get-Main $procId
    $bar = Get-MenuBar $window
    $names = @($effectName, ($effectName + '...'), ($effectName + [char]0x2026))
    foreach ($top in (Get-Children $bar)) {
        try { Expand-El $top } catch { continue }
        Start-Sleep -Milliseconds 80
        $mcp = Find-ByName $top 'MCP'
        if (-not $mcp) { continue }
        Expand-El $mcp
        Start-Sleep -Milliseconds 80
        foreach ($candidate in $names) {
            $item = Find-ByName $mcp $candidate
            if ($item) {
                Invoke-El $item
                return
            }
        }
        throw "The MCP menu has no item named $effectName."
    }
    throw 'No top-level menu contains an MCP submenu. Install the plugin and restart Paint.NET.'
}

function Wait-Modal([int]$procId, [int]$timeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $modals = @(Get-Modals $procId)
        if ($modals.Count -gt 0) { return $modals[0] }
        Start-Sleep -Milliseconds 100
    }
    throw 'Paint.NET did not open a dialog.'
}

function Click-Named($root, [string[]]$names) {
    foreach ($name in $names) {
        $item = Find-ByName $root $name
        if ($item) {
            Invoke-El $item
            return
        }
    }
    throw ('The dialog has none of these buttons: ' + ($names -join ', '))
}

function Set-ValueText($el, [string]$text) {
    $pattern = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($text)
}

function Set-LabeledText($dialog, [string]$label, [string]$text) {
    $named = Find-ByName $dialog $label
    if (-not $named) { $named = Find-ByName $dialog ($label + ':') }
    if (-not $named) { throw "The dialog has no $label field." }
    try {
        Set-ValueText $named $text
        return
    } catch {
    }
    $editType = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editType)
    $edit = $named.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $edit) { throw "The dialog has no editable $label field." }
    Set-ValueText $edit $text
}

function Set-LabeledNumber($dialog, [string]$label, [int]$value) {
    $named = Find-ByName $dialog $label
    if (-not $named) { $named = Find-ByName $dialog ($label + ':') }
    if (-not $named) { throw "The dialog has no $label field." }
    try {
        Set-ValueText $named ([string]$value)
        return
    } catch {
    }
    $editType = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editType)
    $edit = $named.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $edit) { throw "The dialog has no editable $label field." }
    Set-ValueText $edit ([string]$value)
}

function Set-FileName($dialog, [string]$path) {
    $combo = Find-ByName $dialog 'File name:'
    if (-not $combo) { $combo = Find-ById $dialog '1148' }
    if (-not $combo) { throw 'The file dialog has no File name field.' }
    try {
        Set-ValueText $combo $path
        return
    } catch {
    }
    $editType = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editType)
    $edit = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $edit) { $edit = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
    if (-not $edit) { throw 'The file dialog has no editable File name field.' }
    Set-ValueText $edit $path
}

function Get-BgraBytes([string]$pngPath) {
    $source = [System.Drawing.Bitmap]::FromFile($pngPath)
    try {
        $bmp = New-Object System.Drawing.Bitmap $source.Width, $source.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bmp)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.DrawImage($source, 0, 0, $source.Width, $source.Height)
        $graphics.Dispose()
        $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $bmp.PixelFormat)
        try {
            $stride = $data.Stride
            $rawCount = $stride * $bmp.Height
            $raw = New-Object byte[] $rawCount
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $rawCount)
            $tight = New-Object byte[] ($bmp.Width * $bmp.Height * 4)
            for ($y = 0; $y -lt $bmp.Height; $y++) {
                [Array]::Copy($raw, $y * $stride, $tight, $y * $bmp.Width * 4, $bmp.Width * 4)
            }
            return @{ Width = $bmp.Width; Height = $bmp.Height; Bytes = $tight }
        } finally {
            $bmp.UnlockBits($data)
            $bmp.Dispose()
        }
    } finally {
        $source.Dispose()
    }
}

function New-PixelMap([int]$procId, [string]$slot, [int]$width, [int]$height, [byte[]]$bgra) {
    $cleaned = -join ($slot.ToCharArray() | Where-Object { $_ -match '[A-Za-z0-9_-]' })
    if (-not $cleaned) { $cleaned = 'slot' }
    $name = "Local\pdn-mcp-$procId-$cleaned"
    $capacity = [int64](32 + $bgra.Length)
    $map = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($name, $capacity)
    $view = $map.CreateViewStream()
    $writer = New-Object System.IO.BinaryWriter $view
    $writer.Write([int]0x314E4450)
    $writer.Write([int]$width)
    $writer.Write([int]$height)
    $writer.Write([int]($width * 4))
    $writer.Write([int]1)
    $writer.Write([int]-1)
    $writer.Write($bgra)
    $writer.Flush()
    $script:Maps[$name] = $map
    return @{ map = $name; width = $width; height = $height }
}

function Save-Fit($bmp, [string]$path, [int]$longEdge) {
    $edge = [Math]::Max($bmp.Width, $bmp.Height)
    if ($edge -le $longEdge) {
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        return
    }
    $scale = $longEdge / $edge
    $nw = [Math]::Max(1, [int][Math]::Round($bmp.Width * $scale))
    $nh = [Math]::Max(1, [int][Math]::Round($bmp.Height * $scale))
    $dest = New-Object System.Drawing.Bitmap $nw, $nh
    $graphics = [System.Drawing.Graphics]::FromImage($dest)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($bmp, 0, 0, $nw, $nh)
    $graphics.Dispose()
    $dest.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $dest.Dispose()
}

function Read-SlotPng($req) {
    $map = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
        [string]$req.map, [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
    $view = $map.CreateViewStream(0, 0, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
    $reader = New-Object System.IO.BinaryReader $view
    try {
        $magic = $reader.ReadInt32()
        $width = $reader.ReadInt32()
        $height = $reader.ReadInt32()
        [void]$reader.ReadInt32()
        [void]$reader.ReadInt32()
        [void]$reader.ReadInt32()
        if ($magic -ne 0x314E4450 -or $width -le 0 -or $height -le 0) {
            throw "Pixel slot '$($req.map)' has an unexpected header."
        }
        $count = $width * $height * 4
        $pixels = $reader.ReadBytes($count)
        if ($pixels.Length -ne $count) { throw "Pixel slot '$($req.map)' ended early." }
        $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
        try {
            [System.Runtime.InteropServices.Marshal]::Copy($pixels, 0, $data.Scan0, $count)
        } finally {
            $bmp.UnlockBits($data)
        }
        Save-Fit $bmp ([string]$req.imagePath) ([int]$req.imageLongEdge)
        Save-Fit $bmp ([string]$req.previewPath) ([int]$req.previewLongEdge)
        $bmp.Dispose()
        return @{ width = $width; height = $height }
    } finally {
        $reader.Dispose()
        $map.Dispose()
    }
}

function Invoke-Request($req) {
    $procId = 0
    if ($req.PSObject.Properties.Name -contains 'pid' -and $req.pid) { $procId = [int]$req.pid }
    switch ([string]$req.cmd) {
        'processes' {
            $ids = @(Get-Process -Name paintdotnet -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
            return @{ pids = $ids }
        }
        'paths' {
            $docs = [Environment]::GetFolderPath('MyDocuments')
            return @{
                documents = $docs
                plugin = (Join-Path $docs 'paint.net App Files\Effects\PaintDotNet.Mcp')
                localAppData = $env:LOCALAPPDATA
            }
        }
        'readState' {
            $window = Get-Main $procId
            $modals = @(Get-Modals $procId)
            $modalText = $null
            if ($modals.Count -gt 0) { $modalText = [string]$modals[0].Current.Name }
            return @{ title = [string]$window.Current.Name; modalOpen = ($modals.Count -gt 0); modalText = $modalText }
        }
        'modalText' {
            $modals = @(Get-Modals $procId)
            if ($modals.Count -eq 0) { return @{ text = $null } }
            return @{ text = [string]$modals[0].Current.Name }
        }
        'undoEnabled' {
            $top = Get-TopMenu $procId 'Edit'
            Expand-El $top
            Start-Sleep -Milliseconds 80
            $undo = Find-ByName $top 'Undo'
            $enabled = $false
            if ($undo) { $enabled = [bool]$undo.Current.IsEnabled }
            try { $top.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
            return @{ enabled = $enabled; count = $(if ($enabled) { 1 } else { 0 }) }
        }
        'menu' { Invoke-MenuItem $procId ([string]$req.top) ([string]$req.item); return @{ ok = $true } }
        'effect' { Invoke-Effect $procId ([string]$req.name); return @{ ok = $true } }
        'dismiss' {
            $modals = @(Get-Modals $procId)
            if ($modals.Count -eq 0) { return @{ ok = $true } }
            if ([bool]$req.commit) { Click-Named $modals[0] @('OK', 'Ok') } else { Click-Named $modals[0] @('Cancel') }
            return @{ ok = $true }
        }
        'newDocument' {
            Invoke-MenuItem $procId 'File' 'New...'
            $dialog = Wait-Modal $procId 5000
            Set-LabeledNumber $dialog 'Width' ([int]$req.width)
            Set-LabeledNumber $dialog 'Height' ([int]$req.height)
            Click-Named $dialog @('OK', 'Ok')
            return @{ ok = $true }
        }
        'openPath' {
            Invoke-MenuItem $procId 'File' 'Open...'
            $dialog = Wait-Modal $procId 5000
            Set-FileName $dialog ([string]$req.path)
            Click-Named $dialog @('Open', 'OK', 'Ok')
            return @{ ok = $true }
        }
        'save' {
            $window = Get-Main $procId
            $title = [string]$window.Current.Name
            if ($title -match 'Untitled') { return @{ saved = $false } }
            Invoke-MenuItem $procId 'File' 'Save'
            return @{ saved = $true }
        }
        'saveAs' {
            Invoke-MenuItem $procId 'File' 'Save As...'
            $dialog = Wait-Modal $procId 5000
            Set-FileName $dialog ([string]$req.path)
            Click-Named $dialog @('Save', 'OK', 'Ok')
            Start-Sleep -Milliseconds 300
            $follow = @(Get-Modals $procId)
            if ($follow.Count -eq 0) { return @{ ok = $true } }
            if (-not [bool]$req.confirm) {
                try { Click-Named $follow[0] @('No', 'Cancel') } catch { }
                throw 'Paint.NET asked to replace an existing file. Call again with confirm=true.'
            }
            Click-Named $follow[0] @('Yes', 'OK', 'Ok')
            return @{ ok = $true }
        }
        'closeDocument' {
            Invoke-MenuItem $procId 'File' 'Close'
            Start-Sleep -Milliseconds 300
            $modals = @(Get-Modals $procId)
            if ($modals.Count -eq 0) { return @{ prompt = 'closed' } }
            if ([bool]$req.save) {
                Click-Named $modals[0] @('Save', 'Yes', 'OK')
                return @{ prompt = 'saved' }
            }
            if ([bool]$req.discard) {
                Click-Named $modals[0] @("Don't Save", "Don't save", 'No')
                return @{ prompt = 'discarded' }
            }
            return @{ prompt = 'dirty' }
        }
        'layerProperties' {
            Invoke-MenuItem $procId 'Layers' 'Layer Properties...'
            $dialog = Wait-Modal $procId 4000
            if ($req.name) { Set-LabeledText $dialog 'Name' ([string]$req.name) }
            if ($null -ne $req.opacity) {
                $byteValue = [int][Math]::Round(([Math]::Min(1, [Math]::Max(0, [double]$req.opacity))) * 255)
                Set-LabeledText $dialog 'Opacity' ([string]$byteValue)
            }
            if ($null -ne $req.visible) {
                $check = Find-ByName $dialog 'Visible'
                if (-not $check) { throw 'The Layer Properties dialog has no Visible checkbox.' }
                $toggle = $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $wantOn = [bool]$req.visible
                $isOn = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
                if ($wantOn -ne $isOn) { $toggle.Toggle() }
            }
            if ($req.blendMode) {
                $combo = Find-ByName $dialog 'Blend Mode'
                if (-not $combo) { throw 'The Layer Properties dialog has no blend-mode list.' }
                try { Expand-El $combo } catch { }
                $choice = Find-ByName $combo ([string]$req.blendMode)
                if (-not $choice) { throw "The blend-mode list has no item named $($req.blendMode)." }
                Invoke-El $choice
            }
            Click-Named $dialog @('OK', 'Ok')
            return @{ ok = $true }
        }
        'color' {
            $root = [System.Windows.Automation.AutomationElement]::RootElement
            $colors = $null
            foreach ($window in (Get-TopWindows $procId)) {
                if ($window.Current.AutomationId -eq 'ColorsForm') { $colors = $window }
            }
            if (-not $colors) {
                Invoke-MenuItem $procId 'View' 'Colors'
                Start-Sleep -Milliseconds 200
                foreach ($window in (Get-TopWindows $procId)) {
                    if ($window.Current.AutomationId -eq 'ColorsForm') { $colors = $window }
                }
            }
            if (-not $colors) { throw 'The Colors window is not open.' }
            $box = Find-ById $colors 'hexBox'
            if (-not $box) { throw 'The Colors window has no hex field.' }
            Set-ValueText $box ([string]$req.hex)
            return @{ ok = $true }
        }
        'createSlot' {
            if ($req.png) {
                $image = Get-BgraBytes ([string]$req.png)
                return New-PixelMap $procId ([string]$req.slot) $image.Width $image.Height $image.Bytes
            }
            $raw = [System.IO.File]::ReadAllBytes([string]$req.raw)
            $width = [int]$req.width
            $height = [int]$req.height
            if ($raw.Length -ne ($width * $height * 4)) {
                throw 'BGRA buffer length does not match width and height.'
            }
            return New-PixelMap $procId ([string]$req.slot) $width $height $raw
        }
        'readSlot' { return Read-SlotPng $req }
        'disposeSlots' {
            foreach ($key in @($script:Maps.Keys)) {
                try { $script:Maps[$key].Dispose() } catch { }
            }
            $script:Maps = @{}
            return @{ ok = $true }
        }
        default { throw "Unknown host command $($req.cmd)." }
    }
}

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $requestId = 0
    try {
        $req = $line | ConvertFrom-Json
        $requestId = $req.id
        $result = Invoke-Request $req
        Reply $requestId $true $result $null
    } catch {
        Reply $requestId $false $null $_.Exception.Message
    }
}
