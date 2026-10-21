# UI Automation and pixel-slot helper for the Node MCP server.
# stdout is one JSON object per line. Diagnostics go to stderr.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if (-not ([System.Management.Automation.PSTypeName]'PdnUiClick').Type) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PdnUiClick {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessage")] public static extern IntPtr SendMessageText(IntPtr hwnd, int msg, IntPtr wParam, System.Text.StringBuilder text);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    public static void Chord(byte key) {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
    public static string WindowText(int hwnd) {
        var text = new System.Text.StringBuilder(2048);
        GetWindowText(new IntPtr(hwnd), text, text.Capacity);
        return text.ToString();
    }
    public static string WmText(int hwnd) {
        var text = new System.Text.StringBuilder(4096);
        SendMessageText(new IntPtr(hwnd), 0x000D, new IntPtr(text.Capacity), text);
        return text.ToString();
    }
    public static void Key(byte key) {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
    }
}
'@
}

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

function Get-Dialogs([int]$procId) {
    # Paint.NET dialogs are modal children of MainForm. History, Layers, Colors,
    # and Tools report IsModal too, and a top-level search never sees New or Save As.
    $skip = @('HistoryForm', 'LayersForm', 'ColorsForm', 'ToolsForm', 'appWorkspace', 'TitleBar')
    $found = @()
    $main = Get-Main $procId
    foreach ($child in (Get-Children $main)) {
        $id = [string]$child.Current.AutomationId
        if ($skip -contains $id) { continue }
        if (Test-ModalWindow $child) {
            # Confirm Save As is nested inside the file dialog. Surface that window first.
            $windowType = [System.Windows.Automation.ControlType]::Window
            $windowCond = New-Object System.Windows.Automation.PropertyCondition (
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $windowType)
            foreach ($nested in @($child.FindAll([System.Windows.Automation.TreeScope]::Descendants, $windowCond))) {
                $nestedName = [string]$nested.Current.Name
                $nestedModal = Test-ModalWindow $nested
                if ($nestedModal -or $nestedName -match 'Confirm|Save Configuration|Replace|already exists') {
                    $found += $nested
                }
            }
            $found += $child
        }
    }
    foreach ($window in (Get-TopWindows $procId)) {
        if ([string]$window.Current.AutomationId -eq 'MainForm') { continue }
        $found += $window
    }
    return $found
}

function Get-Modals([int]$procId) {
    return @(Get-Dialogs $procId)
}

function Get-Main([int]$procId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        foreach ($window in (Get-TopWindows $procId)) {
            if ($window.Current.AutomationId -eq 'MainForm') { return $window }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
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

function Find-AllById($el, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return @($el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
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

function Get-Center($el) {
    $rect = $el.Current.BoundingRectangle
    if ($rect.IsEmpty -or $rect.Width -lt 2 -or $rect.Height -lt 2) { return $null }
    return @{
        X = [int][Math]::Round($rect.X + ($rect.Width / 2))
        Y = [int][Math]::Round($rect.Y + ($rect.Height / 2))
    }
}

function Click-El($el) {
    $center = Get-Center $el
    if (-not $center) { throw 'A Paint.NET control is off screen, so it was not clicked.' }
    [PdnUiClick]::Click($center.X, $center.Y)
}

function Click-Text($el) {
    # The center of the file-name combo is near its drop-down. Click the text instead.
    $rect = $el.Current.BoundingRectangle
    if ($rect.IsEmpty -or $rect.Width -lt 2) { throw 'The file name field is off screen.' }
    $x = [int][Math]::Round($rect.X + [Math]::Min(30, $rect.Width / 4))
    $y = [int][Math]::Round($rect.Y + ($rect.Height / 2))
    [PdnUiClick]::Click($x, $y)
}

function Open-MenuItem([int]$procId, [string]$topName, [string]$itemName) {
    # Invoke on a menu item that opens a dialog does not return until that dialog
    # closes, so the host cannot fill it in. A click returns at once.
    $top = Get-TopMenu $procId $topName
    Expand-El $top
    Start-Sleep -Milliseconds 150
    $item = Find-ByName $top $itemName
    if (-not $item) {
        try { $top.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
        throw "The $topName menu has no item named $itemName."
    }
    Click-El $item
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

function Find-Named($root, [string[]]$names) {
    foreach ($name in $names) {
        $cond = New-Object System.Windows.Automation.PropertyCondition (
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $all = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
        if ($all.Count -eq 0) { continue }
        foreach ($el in $all) {
            $id = [string]$el.Current.AutomationId
            if ($id -eq '1' -or $id -eq '2') { return $el }
        }
        return $all[0]
    }
    return $null
}

function Test-Alive($el) {
    try {
        if ($el.Current.IsOffscreen) { return $false }
        return $true
    } catch {
        return $false
    }
}

function Click-Named($root, [string[]]$names) {
    # The first click on a background window only activates it. Click again when
    # the same dialog is still the only one open.
    $item = Find-Named $root $names
    if (-not $item) { throw ('The dialog has none of these buttons: ' + ($names -join ', ')) }
    Click-El $item
    Start-Sleep -Milliseconds 220
    if (-not (Test-Alive $root)) { return }
    $procId = 0
    try { $procId = [int]$root.Current.ProcessId } catch { }
    if ($procId -le 0) { $procId = [int]$script:UiPid }
    if ($procId -le 0) { return }
    $mine = ''
    try { $mine = [string]$root.Current.Name } catch { return }
    try {
        foreach ($open in @(Get-Dialogs $procId)) {
            if ([string]$open.Current.Name -ne $mine) { return }
        }
    } catch {
        return
    }
    $item = Find-Named $root $names
    if ($item) { Click-El $item }
}

function Close-Dialog($dialog) {
    $cancel = Find-Named $dialog @('Cancel', 'No', "Don't Save", "Don't save")
    if (-not $cancel) { throw 'The dialog has no Cancel button.' }
    Click-El $cancel
}

function Wait-DialogNamed([int]$procId, [string]$name, [int]$timeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($dialog in @(Get-Dialogs $procId)) {
            if ([string]$dialog.Current.Name -eq $name) { return $dialog }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Paint.NET did not open $name."
}

function Wait-DialogId([int]$procId, [string]$id, [int]$timeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($dialog in @(Get-Dialogs $procId)) {
            if ([string]$dialog.Current.AutomationId -eq $id) { return $dialog }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Paint.NET did not open $id."
}

function ConvertTo-SendKeysLiteral([string]$text) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($char in $text.ToCharArray()) {
        switch ([string]$char) {
            '+' { [void]$builder.Append('{+}') }
            '^' { [void]$builder.Append('{^}') }
            '%' { [void]$builder.Append('{%}') }
            '~' { [void]$builder.Append('{~}') }
            '(' { [void]$builder.Append('{(}') }
            ')' { [void]$builder.Append('{)}') }
            '{' { [void]$builder.Append('{{}') }
            '}' { [void]$builder.Append('{}}') }
            default { [void]$builder.Append($char) }
        }
    }
    return $builder.ToString()
}

function Get-EditBesideLabel($dialog, [string]$labelId) {
    $label = Find-ById $dialog $labelId
    if (-not $label) { throw "The dialog has no $labelId label." }
    $labelRect = $label.Current.BoundingRectangle
    $editType = [System.Windows.Automation.ControlType]::Edit
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editType)
    $best = $null
    $bestDx = [double]::MaxValue
    foreach ($edit in @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))) {
        $rect = $edit.Current.BoundingRectangle
        $dy = [Math]::Abs(($rect.Y + ($rect.Height / 2)) - ($labelRect.Y + ($labelRect.Height / 2)))
        if ($dy -gt 14) { continue }
        $dx = $rect.X - ($labelRect.X + $labelRect.Width)
        if ($dx -lt -8) { continue }
        if ($dx -lt $bestDx) { $best = $edit; $bestDx = $dx }
    }
    if (-not $best) { throw "The dialog has no editable field beside $labelId." }
    return $best
}

function Get-EditNumber($edit) {
    $pattern = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return [int][Math]::Round([double]$pattern.Current.Value)
}

function Set-PixelField($dialog, [string]$labelId, [int]$value) {
    $edit = Get-EditBesideLabel $dialog $labelId
    Set-ValueText $edit ([string]$value)
    Start-Sleep -Milliseconds 80
    $edit = Get-EditBesideLabel $dialog $labelId
    if ((Get-EditNumber $edit) -eq $value) { return }
    Click-El $edit
    Start-Sleep -Milliseconds 80
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 40
    [System.Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysLiteral ([string]$value)))
    Start-Sleep -Milliseconds 80
}

function Test-PixelField($dialog, [string]$labelId, [int]$value) {
    return (Get-EditNumber (Get-EditBesideLabel $dialog $labelId)) -eq $value
}

function Set-NewPixelSize($dialog, [int]$width, [int]$height) {
    Set-PixelField $dialog 'newWidthLabel1' $width
    Set-PixelField $dialog 'newHeightLabel1' $height
    if ((Test-PixelField $dialog 'newWidthLabel1' $width) -and (Test-PixelField $dialog 'newHeightLabel1' $height)) {
        return
    }
    $aspect = Find-ByName $dialog 'Maintain aspect ratio'
    if ($aspect) {
        Click-El $aspect
        Start-Sleep -Milliseconds 120
    }
    Set-PixelField $dialog 'newWidthLabel1' $width
    Set-PixelField $dialog 'newHeightLabel1' $height
    $gotWidth = Get-EditNumber (Get-EditBesideLabel $dialog 'newWidthLabel1')
    $gotHeight = Get-EditNumber (Get-EditBesideLabel $dialog 'newHeightLabel1')
    if ($gotWidth -ne $width -or $gotHeight -ne $height) {
        try { Close-Dialog $dialog } catch { }
        throw "New is ${gotWidth}x${gotHeight}, not ${width}x${height}."
    }
}

function Complete-SaveDialogs([int]$procId, [string]$path, [bool]$confirm) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $dialogs = @(Get-Dialogs $procId)
        if ((Test-Path -LiteralPath $path) -and $dialogs.Count -eq 0) { return }
        $acted = $false
        foreach ($dialog in $dialogs) {
            $name = [string]$dialog.Current.Name
            if ($name -eq 'Save As' -or $name -eq 'Save' -or $name -eq 'Open') { continue }
            if ($name -match 'Save Configuration') {
                Click-Named $dialog @('OK', 'Ok')
                $acted = $true
                break
            }
            if ($name -match 'Confirm|already exists|Replace') {
                $detail = ''
                $content = Find-ById $dialog 'ContentText'
                if ($content) { $detail = [string]$content.Current.Name }
                if (-not $confirm) {
                    Click-Named $dialog @('No', 'Cancel')
                    foreach ($open in @(Get-Dialogs $procId)) {
                        $openName = [string]$open.Current.Name
                        if ($openName -eq 'Save As' -or $openName -eq 'Open' -or $openName -eq 'Save') {
                            try { Click-Named $open @('Cancel', 'No') } catch { }
                        }
                    }
                    if ($detail) { throw "Paint.NET asked to replace an existing file. $detail Call again with confirm=true." }
                    throw 'Paint.NET asked to replace an existing file. Call again with confirm=true.'
                }
                Click-Named $dialog @('Yes', 'OK', 'Ok')
                $acted = $true
                break
            }
            throw "Paint.NET showed '$name' during save. It was left open."
        }
        Start-Sleep -Milliseconds 150
    }
    if ((Test-Path -LiteralPath $path) -and @(Get-Dialogs $procId).Count -eq 0) { return }
    foreach ($dialog in @(Get-Dialogs $procId)) {
        if ([string]$dialog.Current.Name -eq 'Save As') {
            try { Close-Dialog $dialog } catch { }
        }
    }
    throw "Save did not write $path."
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

function Find-FileNamePane([int]$procId) {
    # The file dialog is a child of MainForm. Pane 1001 under FileNameControlHost
    # is the edit. A second pane 1001 is the address bar, so it is not used.
    # The edit's UIA name is sometimes the label "File name:" instead of the text.
    $script:FileNamePane = $null
    $main = Get-Main $procId
    $dialog = $null
    foreach ($child in (Get-Children $main)) {
        $name = [string]$child.Current.Name
        if ($name -eq 'Save As' -or $name -eq 'Open') { $dialog = $child }
    }
    if (-not $dialog) { return }
    $hostEl = Find-ById $dialog 'FileNameControlHost'
    if (-not $hostEl) { return }
    foreach ($pane in (Find-AllById $hostEl '1001')) {
        if ([string]$pane.Current.ClassName -eq 'Edit') {
            $script:FileNamePane = $pane
            return
        }
    }
}

function Get-FileNameText([int]$hwnd) {
    if ($hwnd -eq 0) { return '' }
    return ([PdnUiClick]::WmText($hwnd)).Trim().Trim('"')
}

function Set-FileName($dialog, [string]$path) {
    # The edit has no ValuePattern. WM_SETTEXT changes the visible text and leaves
    # the file dialog's own filename alone, so Save would still use Untitled.
    # Keystrokes notify the dialog. Save stays unclicked until the window text is the path.
    Start-Sleep -Milliseconds 400
    Find-FileNamePane ([int]$script:UiPid)
    if (-not $script:FileNamePane) { throw 'The file dialog has no File name field.' }
    $hwnd = 0
    try { $hwnd = [int]$script:FileNamePane.Current.NativeWindowHandle } catch { }
    if ($hwnd -eq 0) { throw 'The file name field has no window.' }
    $previousClip = $null
    $restoreClip = $false
    try {
        if ([System.Windows.Forms.Clipboard]::ContainsText()) {
            $previousClip = [System.Windows.Forms.Clipboard]::GetText()
            $restoreClip = $true
        }
    } catch { }
    [System.Windows.Forms.Clipboard]::SetText($path)
    Click-Text $script:FileNamePane
    Start-Sleep -Milliseconds 220
    Click-Text $script:FileNamePane
    Start-Sleep -Milliseconds 180
    [PdnUiClick]::Chord(0x41)
    Start-Sleep -Milliseconds 60
    [PdnUiClick]::Chord(0x56)
    Start-Sleep -Milliseconds 120
    [PdnUiClick]::Key(0x09)
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    $got = ''
    do {
        Start-Sleep -Milliseconds 150
        $got = Get-FileNameText $hwnd
        if ($got -eq $path) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($got -ne $path) {
        Click-Text $script:FileNamePane
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        Start-Sleep -Milliseconds 80
        [System.Windows.Forms.SendKeys]::SendWait('^v')
        Start-Sleep -Milliseconds 150
        [PdnUiClick]::Key(0x09)
        Start-Sleep -Milliseconds 200
        $got = Get-FileNameText $hwnd
    }
    if ($restoreClip) { try { [System.Windows.Forms.Clipboard]::SetText($previousClip) } catch { } }
    if ($got -ne $path) {
        try { Close-Dialog $dialog } catch { }
        throw "The file name field shows '$got' instead of '$path'."
    }
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
    $script:UiPid = $procId
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
            Open-MenuItem $procId 'File' 'New...'
            $dialog = Wait-DialogId $procId 'ResizeDialog' 5000
            try {
                Set-NewPixelSize $dialog ([int]$req.width) ([int]$req.height)
                Click-Named $dialog @('OK', 'Ok')
            } catch {
                try { Close-Dialog $dialog } catch { }
                throw
            }
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while ([DateTime]::UtcNow -lt $deadline) {
                $still = $false
                foreach ($open in @(Get-Dialogs $procId)) {
                    if ([string]$open.Current.AutomationId -eq 'ResizeDialog') { $still = $true }
                }
                if (-not $still) { return @{ ok = $true } }
                Start-Sleep -Milliseconds 100
            }
            throw 'The New dialog stayed open.'
        }
        'openPath' {
            Open-MenuItem $procId 'File' 'Open...'
            $dialog = Wait-DialogNamed $procId 'Open' 5000
            Set-FileName $dialog ([string]$req.path)
            Click-Named $dialog @('Open', 'OK', 'Ok')
            return @{ ok = $true }
        }
        'save' {
            $window = Get-Main $procId
            $title = [string]$window.Current.Name
            if ($title -match 'Untitled') { return @{ saved = $false } }
            Open-MenuItem $procId 'File' 'Save'
            $deadline = [DateTime]::UtcNow.AddSeconds(8)
            $quietSince = $null
            while ([DateTime]::UtcNow -lt $deadline) {
                $dialogs = @(Get-Dialogs $procId)
                foreach ($dialog in $dialogs) {
                    $name = [string]$dialog.Current.Name
                    if ($name -match 'Save Configuration') {
                        Click-Named $dialog @('OK', 'Ok')
                        $quietSince = $null
                    } elseif ($name -ne 'Save') {
                        throw "Save left a dialog open. $name"
                    }
                }
                $still = @($dialogs | Where-Object { [string]$_.Current.Name -match 'Save Configuration' -or [string]$_.Current.Name -eq 'Save' })
                if ($still.Count -eq 0) {
                    if (-not $quietSince) { $quietSince = [DateTime]::UtcNow }
                    if (([DateTime]::UtcNow - $quietSince).TotalMilliseconds -gt 700) { return @{ saved = $true } }
                } else {
                    $quietSince = $null
                }
                Start-Sleep -Milliseconds 150
            }
            $left = @(Get-Dialogs $procId)
            if ($left.Count -gt 0) { throw ("Save left a dialog open. " + [string]$left[0].Current.Name) }
            return @{ saved = $true }
        }
        'saveAs' {
            Open-MenuItem $procId 'File' 'Save As...'
            $dialog = Wait-DialogNamed $procId 'Save As' 5000
            Set-FileName $dialog ([string]$req.path)
            Click-Named $dialog @('Save')
            Complete-SaveDialogs $procId ([string]$req.path) ([bool]$req.confirm)
            return @{ ok = $true }
        }
        'closeDocument' {
            Open-MenuItem $procId 'File' 'Close'
            $deadline = [DateTime]::UtcNow.AddMilliseconds(1500)
            while ([DateTime]::UtcNow -lt $deadline) {
                $modals = @(Get-Modals $procId)
                if ($modals.Count -gt 0) {
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
                Start-Sleep -Milliseconds 100
            }
            return @{ prompt = 'closed' }
        }
        'layerProperties' {
            Open-MenuItem $procId 'Layers' 'Layer Properties...'
            $dialog = Wait-DialogNamed $procId 'Layer Properties' 4000
            try {
                if ($req.name) {
                    $edit = Find-ById $dialog 'textBox'
                    if (-not $edit) { throw 'The Layer Properties dialog has no Name field.' }
                    Set-ValueText $edit ([string]$req.name)
                    $gotName = [string]$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                    if ($gotName -ne [string]$req.name) { throw "Layer name is '$gotName', not '$($req.name)'." }
                }
                if ($null -ne $req.opacity) {
                    $byteValue = [int][Math]::Round(([Math]::Min(1, [Math]::Max(0, [double]$req.opacity))) * 255)
                    $opacityEdit = $null
                    $editType = [System.Windows.Automation.ControlType]::Edit
                    $editCond = New-Object System.Windows.Automation.PropertyCondition (
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $editType)
                    foreach ($edit in @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond))) {
                        if ([string]$edit.Current.AutomationId -eq 'textBox') { continue }
                        $opacityEdit = $edit
                    }
                    if (-not $opacityEdit) { throw 'The Layer Properties dialog has no Opacity field.' }
                    Set-ValueText $opacityEdit ([string]$byteValue)
                    $gotOpacity = [string]$opacityEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                    if ([int]$gotOpacity -ne $byteValue) { throw "Layer opacity is '$gotOpacity', not '$byteValue'." }
                }
                if ($null -ne $req.visible) {
                    $check = Find-ByName $dialog 'Visible'
                    if (-not $check) { throw 'The Layer Properties dialog has no Visible checkbox.' }
                    $toggle = $null
                    try { $toggle = $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern) } catch { }
                    if (-not $toggle) {
                        foreach ($child in (Get-Children $check)) {
                            try { $toggle = $child.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); break } catch { }
                        }
                    }
                    if (-not $toggle) { throw 'The Visible checkbox does not report its state, so Layer Properties was not changed.' }
                    $wantOn = [bool]$req.visible
                    $isOn = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
                    if ($wantOn -ne $isOn) { $toggle.Toggle() }
                }
                if ($req.blendMode) {
                    $combo = Find-ById $dialog 'comboBox'
                    if (-not $combo) { throw 'The Layer Properties dialog has no blend-mode list.' }
                    $want = [string]$req.blendMode
                    try { Set-ValueText $combo $want } catch { }
                    $gotMode = ''
                    try { $gotMode = [string]$combo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
                    if ($gotMode -ne $want) {
                        try { Expand-El $combo } catch { }
                        Start-Sleep -Milliseconds 120
                        $choice = Find-ByName $combo $want
                        if (-not $choice) { $choice = Find-ByName $dialog $want }
                        if (-not $choice) { throw "The blend-mode list has no item named $want." }
                        try { Invoke-El $choice } catch { Click-El $choice }
                    }
                }
                Click-Named $dialog @('OK', 'Ok')
            } catch {
                try { Close-Dialog $dialog } catch { }
                throw
            }
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
