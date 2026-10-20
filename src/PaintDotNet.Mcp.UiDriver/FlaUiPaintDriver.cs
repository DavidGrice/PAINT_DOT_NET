using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace PaintDotNet.Mcp.UiDriver;

public sealed class FlaUiPaintDriver : IPaintDriver, IDisposable
{
    private readonly UIA3Automation automation = new();

    public void Dispose() => automation.Dispose();

    public UiState ReadState(int pid)
    {
        RefuseIfMouseDown();
        var window = FindWindow(pid);
        var modal = ModalText(window);
        return new UiState(window.Title, modal is not null, modal);
    }

    public string DumpTree(int pid)
    {
        var window = FindWindow(pid);
        var builder = new StringBuilder();
        AppendTree(window, builder, 0);
        foreach (var dialog in window.ModalWindows)
        {
            builder.AppendLine("--- modal ---");
            AppendTree(dialog, builder, 0);
        }

        return builder.ToString();
    }

    public void InvokeEffect(int pid, string effectName)
    {
        var window = Prepare(pid);
        var menuBar = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar))
            ?? window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Menu));
        if (menuBar is null)
        {
            throw new PaintUiException("Paint.NET has no UI Automation menu bar. " + Summarize(window));
        }

        foreach (var top in menuBar.FindAllChildren())
        {
            Expand(top);
            var mcp = top.FindFirstDescendant(cf => cf.ByName("MCP"));
            if (mcp is null)
            {
                continue;
            }

            Expand(mcp);
            // Configurable effects are shown with an ellipsis. Paint.NET 5.1.12 shows "MCP Inspect..." and "MCP Apply...".
            var item = mcp.FindFirstDescendant(cf => cf.ByName(effectName))
                ?? mcp.FindFirstDescendant(cf => cf.ByName(effectName + "..."))
                ?? mcp.FindFirstDescendant(cf => cf.ByName(effectName + "…"));
            if (item is null)
            {
                throw new PaintUiException($"The MCP menu has no item named {effectName}.");
            }

            Invoke(item);
            return;
        }

        throw new PaintUiException("No top-level menu contains an MCP submenu. Install the plugin and restart Paint.NET.");
    }

    public void DismissEffectDialog(int pid, bool commit)
    {
        if (!TryFindWindow(pid, out var window))
        {
            return;
        }

        var dialog = FindEffectDialog(window);
        if (dialog is null)
        {
            return;
        }

        var button = commit ? new[] { "OK", "Ok" } : new[] { "Cancel" };
        ClickButton(dialog, button);
    }

    public void PressEscape(int pid)
    {
        var window = Prepare(pid);
        window.Focus();
        Keyboard.Type(VirtualKeyShort.ESCAPE);
    }

    public int? HistoryCount(int pid)
    {
        var window = Prepare(pid);
        var list = FindHistoryList(window);
        var opened = false;
        if (list is null)
        {
            Chord(VirtualKeyShort.F6);
            Thread.Sleep(200);
            list = FindHistoryList(window);
            opened = list is not null;
        }

        if (list is null)
        {
            return null;
        }

        int count = list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length;
        if (count == 0)
        {
            count = list.FindAllChildren().Length;
        }

        if (opened)
        {
            Chord(VirtualKeyShort.F6);
        }

        return count;
    }

    public void Undo(int pid) => InvokeTopMenu(pid, "Edit", "Undo");

    public void Redo(int pid) => InvokeTopMenu(pid, "Edit", "Redo");

    public void NewDocument(int pid, int width, int height)
    {
        var window = Prepare(pid);
        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_N);
        var dialog = WaitForDialog(window, TimeSpan.FromSeconds(5));
        SetLabeledNumber(dialog, "Width", width);
        SetLabeledNumber(dialog, "Height", height);
        ClickButton(dialog, "OK", "Ok");
        WaitUntilGone(dialog, TimeSpan.FromSeconds(5));
    }

    public void OpenDocument(int pid, string path)
    {
        var window = Prepare(pid);
        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_O);
        var dialog = WaitForDialog(window, TimeSpan.FromSeconds(5));
        SetFileName(dialog, path);
        ClickButton(dialog, "Open", "OK", "Ok");
    }

    public bool Save(int pid)
    {
        var window = Prepare(pid);
        var title = window.Title ?? string.Empty;
        if (title.Contains("Untitled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        Thread.Sleep(200);
        return true;
    }

    public void SaveAs(int pid, string path, bool overwriteConfirmed)
    {
        var window = Prepare(pid);
        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_S);
        var dialog = WaitForDialog(window, TimeSpan.FromSeconds(5));
        SetFileName(dialog, path);
        ClickButton(dialog, "Save", "OK", "Ok");
        Thread.Sleep(250);
        var follow = window.ModalWindows.FirstOrDefault();
        if (follow is null)
        {
            return;
        }

        var text = follow.Name ?? string.Empty;
        if (!overwriteConfirmed)
        {
            ClickButton(follow, "No", "Cancel");
            throw new PaintUiException("Paint.NET asked to replace an existing file. Call again with confirm=true. " + text);
        }

        ClickButton(follow, "Yes", "OK", "Ok");
    }

    public ClosePrompt CloseDocument(int pid, bool save, bool discard)
    {
        var window = Prepare(pid);
        Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        Thread.Sleep(250);
        var dialog = window.ModalWindows.FirstOrDefault();
        if (dialog is null)
        {
            return ClosePrompt.Closed;
        }

        if (save)
        {
            ClickButton(dialog, "Save", "Yes", "OK");
            return ClosePrompt.Saved;
        }

        if (discard)
        {
            ClickButton(dialog, "Don't Save", "Don't save", "No");
            return ClosePrompt.Discarded;
        }

        return ClosePrompt.DirtyPromptLeftOpen;
    }

    public void AddLayer(int pid) => InvokeTopMenu(pid, "Layers", "Add New Layer");

    public void DeleteLayer(int pid) => InvokeTopMenu(pid, "Layers", "Delete Layer");

    public void DuplicateLayer(int pid) => InvokeTopMenu(pid, "Layers", "Duplicate Layer");

    public void MergeDown(int pid) => InvokeTopMenu(pid, "Layers", "Merge Layer Down");

    public void LayerStep(int pid, bool pageUp) =>
        InvokeTopMenu(pid, "Layers", pageUp ? "Go to Layer Above" : "Go to Layer Below");

    public void SetLayerProperties(int pid, string? currentName, string? name, double? opacity, bool? visible, string? blendMode)
    {
        var window = Prepare(pid);
        Chord(VirtualKeyShort.F4);
        var dialog = WaitForDialog(window, TimeSpan.FromSeconds(4));
        if (name is not null)
        {
            var box = FindNameBox(dialog, currentName);
            box.Text = name;
        }

        if (opacity is not null)
        {
            var box = FindLabeledBox(dialog, "Opacity");
            int byteValue = (int)Math.Round(Math.Clamp(opacity.Value, 0, 1) * 255);
            box.Text = byteValue.ToString();
        }

        if (visible is not null)
        {
            var check = FindByName(dialog, "Visible")?.AsCheckBox()
                ?? throw new PaintUiException("The Layer Properties dialog has no Visible checkbox. " + Summarize(dialog));
            check.IsChecked = visible.Value;
        }

        if (blendMode is not null)
        {
            var combo = FindByName(dialog, "Blend Mode")?.AsComboBox()
                ?? dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))?.AsComboBox()
                ?? throw new PaintUiException("The Layer Properties dialog has no blend-mode list. " + Summarize(dialog));
            try
            {
                combo.Select(blendMode);
            }
            catch (Exception)
            {
                combo.Select(SplitWords(blendMode));
            }
        }

        ClickButton(dialog, "OK", "Ok");
        WaitUntilGone(dialog, TimeSpan.FromSeconds(4));
    }

    public void SelectAll(int pid) => ChordOn(pid, VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);

    public void SelectNone(int pid) => ChordOn(pid, VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_D);

    public void InvertSelection(int pid) => ChordOn(pid, VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_I);

    public void CropToSelection(int pid) => ChordOn(pid, VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_X);

    public void SetColor(int pid, bool primary, string hex)
    {
        var window = Prepare(pid);
        Chord(VirtualKeyShort.F8);
        Thread.Sleep(150);
        if (!primary)
        {
            Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_X);
            Thread.Sleep(80);
        }

        var box = FindHexBox(window);
        box.Text = hex;
        Keyboard.Type(VirtualKeyShort.RETURN);
    }

    public void ActivateDocument(int pid, string title)
    {
        var window = Prepare(pid);
        if (TitleMatches(window.Title, title))
        {
            return;
        }

        for (int i = 0; i < 16; i++)
        {
            Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
            Thread.Sleep(120);
            if (TitleMatches(window.Title, title))
            {
                return;
            }
        }

        throw new PaintUiException($"No open document title contains '{title}'. The active title is '{window.Title}'.");
    }

    public string? ModalText(int pid)
    {
        if (!TryFindWindow(pid, out var window))
        {
            return null;
        }

        return ModalText(window);
    }

    private Window Prepare(int pid)
    {
        RefuseIfMouseDown();
        var window = FindWindow(pid);
        var modal = ModalText(window);
        if (modal is not null)
        {
            throw new PaintUiException("Paint.NET already has a dialog open. " + modal);
        }

        BringToFront(window);
        return window;
    }

    private Window FindWindow(int pid)
    {
        if (TryFindWindow(pid, out var window))
        {
            return window;
        }

        throw new PaintUiException($"Paint.NET process {pid} has no main window.");
    }

    private bool TryFindWindow(int pid, out Window window)
    {
        try
        {
            var app = Application.Attach(pid);
            Window? main = app.GetMainWindow(automation, TimeSpan.FromSeconds(3));
            if (main is null)
            {
                window = null!;
                return false;
            }

            window = main;
            return true;
        }
        catch (Exception)
        {
            window = null!;
            return false;
        }
    }

    private static string? ModalText(Window window)
    {
        var modal = window.ModalWindows.FirstOrDefault();
        if (modal is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(modal.Name) ? Summarize(modal) : modal.Name;
    }

    private AutomationElement? FindEffectDialog(Window window)
    {
        foreach (var dialog in window.ModalWindows)
        {
            if (dialog.FindFirstDescendant(cf => cf.ByName("Paint.NET MCP")) is not null
                || dialog.Name is "MCP Inspect" or "MCP Apply")
            {
                return dialog;
            }
        }

        return window.FindFirstDescendant(cf => cf.ByName("Paint.NET MCP"))?.Parent;
    }

    private static AutomationElement? FindHistoryList(Window window)
    {
        var titled = window.FindFirstDescendant(cf => cf.ByName("History"));
        if (titled is null)
        {
            return null;
        }

        return titled.FindFirstDescendant(cf => cf.ByControlType(ControlType.List)) ?? titled;
    }

    private AutomationElement WaitForDialog(Window window, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var dialog = window.ModalWindows.FirstOrDefault();
            if (dialog is not null)
            {
                return dialog;
            }

            Thread.Sleep(50);
        }

        throw new PaintUiException("The expected dialog did not open. " + Summarize(window));
    }

    private static void WaitUntilGone(AutomationElement dialog, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (dialog.Properties.IsOffscreen.ValueOrDefault)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }

    private static void SetLabeledNumber(AutomationElement dialog, string label, int value)
    {
        var box = FindLabeledBox(dialog, label);
        box.Text = value.ToString();
    }

    private static TextBox FindLabeledBox(AutomationElement root, string label)
    {
        var direct = root.FindFirstDescendant(cf => cf.ByName(label))
            ?? root.FindFirstDescendant(cf => cf.ByName(label + ":"));
        if (direct is not null && direct.ControlType == ControlType.Edit)
        {
            return direct.AsTextBox();
        }

        foreach (var box in root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
        {
            var name = box.Name ?? string.Empty;
            if (name.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                return box.AsTextBox();
            }
        }

        throw new PaintUiException($"The dialog has no {label} field. " + Summarize(root));
    }

    private static TextBox FindNameBox(AutomationElement dialog, string? currentName)
    {
        if (!string.IsNullOrEmpty(currentName))
        {
            foreach (var box in dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
            {
                if (string.Equals(box.AsTextBox().Text, currentName, StringComparison.Ordinal))
                {
                    return box.AsTextBox();
                }
            }
        }

        return FindLabeledBox(dialog, "Name");
    }

    private static TextBox FindHexBox(AutomationElement window)
    {
        string[] ids = ["hexBox", "hexTextBox", "colorHexText", "hexColorTextBox", "txtHex"];
        foreach (var id in ids)
        {
            var byId = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (byId is not null)
            {
                return byId.AsTextBox();
            }
        }

        foreach (var box in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
        {
            var name = box.Name ?? string.Empty;
            if (name.Contains("Hex", StringComparison.OrdinalIgnoreCase)
                || name.Contains("HTML", StringComparison.OrdinalIgnoreCase))
            {
                return box.AsTextBox();
            }
        }

        throw new PaintUiException("The Colors window has no hex field. " + Summarize(window));
    }

    private static void SetFileName(AutomationElement dialog, string path)
    {
        string[] ids = ["FileNameControlHost", "1148", "1001"];
        foreach (var id in ids)
        {
            var host = dialog.FindFirstDescendant(cf => cf.ByAutomationId(id));
            var edit = host?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? host;
            if (edit?.ControlType == ControlType.Edit)
            {
                edit.AsTextBox().Text = path;
                return;
            }
        }

        var named = dialog.FindFirstDescendant(cf => cf.ByName("File name:"))
            ?? dialog.FindFirstDescendant(cf => cf.ByName("File name"));
        if (named?.ControlType == ControlType.Edit)
        {
            named.AsTextBox().Text = path;
            return;
        }

        throw new PaintUiException("The file dialog has no file-name field. " + Summarize(dialog));
    }

    private static AutomationElement? FindByName(AutomationElement root, string name) =>
        root.FindFirstDescendant(cf => cf.ByName(name));

    private static void ClickButton(AutomationElement root, params string[] names)
    {
        foreach (var name in names)
        {
            var found = root.FindFirstDescendant(cf => cf.ByAutomationId(name))
                ?? root.FindFirstDescendant(cf => cf.ByName(name));
            if (found is not null)
            {
                Invoke(found);
                return;
            }
        }

        throw new PaintUiException("Could not find a button named " + string.Join(", ", names) + ". " + Summarize(root));
    }

    private void InvokeTopMenu(int pid, string topName, string itemName)
    {
        var window = Prepare(pid);
        var menuBar = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar))
            ?? window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Menu))
            ?? throw new PaintUiException("Paint.NET has no UI Automation menu bar. " + Summarize(window));
        var top = menuBar.FindAllChildren().FirstOrDefault(child => string.Equals(child.Name, topName, StringComparison.Ordinal))
            ?? throw new PaintUiException($"Paint.NET has no {topName} menu. " + Summarize(window));
        Expand(top);
        var item = top.FindFirstDescendant(cf => cf.ByName(itemName))
            ?? throw new PaintUiException($"The {topName} menu has no item named {itemName}.");
        Invoke(item);
    }

    private static void Expand(AutomationElement element)
    {
        if (element.Patterns.ExpandCollapse.IsSupported)
        {
            var pattern = element.Patterns.ExpandCollapse.Pattern;
            if (pattern.ExpandCollapseState != ExpandCollapseState.Expanded)
            {
                pattern.Expand();
            }

            return;
        }

        element.Click();
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
    }

    private void ChordOn(int pid, params VirtualKeyShort[] keys)
    {
        Prepare(pid);
        Chord(keys);
    }

    private static void Chord(params VirtualKeyShort[] keys)
    {
        Keyboard.TypeSimultaneously(keys);
        Thread.Sleep(60);
    }

    private static bool TitleMatches(string? actual, string expected) =>
        actual is not null && actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string SplitWords(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        foreach (char ch in value)
        {
            if (builder.Length > 0 && char.IsUpper(ch))
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static void RefuseIfMouseDown()
    {
        if (MouseDown(0x01) || MouseDown(0x02) || MouseDown(0x04))
        {
            throw new PaintUiException("A mouse button is down, so the driver will not take the Paint.NET window.");
        }
    }

    private static bool MouseDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void BringToFront(Window window)
    {
        var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
        if (handle == IntPtr.Zero)
        {
            window.Focus();
            return;
        }

        var foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            window.Focus();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static void AppendTree(AutomationElement element, StringBuilder builder, int depth)
    {
        if (depth > 5)
        {
            return;
        }

        builder.Append(' ', depth * 2);
        builder.Append(element.ControlType);
        builder.Append(" name=");
        builder.Append(element.Name);
        builder.Append(" id=");
        builder.AppendLine(element.AutomationId);
        foreach (var child in element.FindAllChildren())
        {
            AppendTree(child, builder, depth + 1);
        }
    }

    private static string Summarize(AutomationElement element)
    {
        var builder = new StringBuilder();
        AppendTree(element, builder, 0);
        var text = builder.ToString();
        return text.Length <= 1200 ? text : text[..1200];
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
