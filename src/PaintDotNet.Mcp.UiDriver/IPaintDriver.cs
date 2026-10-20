namespace PaintDotNet.Mcp.UiDriver;

public interface IPaintDriver
{
    UiState ReadState(int pid);

    string DumpTree(int pid);

    void InvokeEffect(int pid, string effectName);

    void DismissEffectDialog(int pid, bool commit);

    void PressEscape(int pid);

    int? HistoryCount(int pid);

    void Undo(int pid);

    void Redo(int pid);

    void NewDocument(int pid, int width, int height);

    void OpenDocument(int pid, string path);

    bool Save(int pid);

    void SaveAs(int pid, string path, bool overwriteConfirmed);

    ClosePrompt CloseDocument(int pid, bool save, bool discard);

    void AddLayer(int pid);

    void DeleteLayer(int pid);

    void DuplicateLayer(int pid);

    void MergeDown(int pid);

    void LayerStep(int pid, bool pageUp);

    void SetLayerProperties(int pid, string? currentName, string? name, double? opacity, bool? visible, string? blendMode);

    void SelectAll(int pid);

    void SelectNone(int pid);

    void InvertSelection(int pid);

    void CropToSelection(int pid);

    void SetColor(int pid, bool primary, string hex);

    void ActivateDocument(int pid, string title);

    string? ModalText(int pid);
}

public sealed record UiState(string? Title, bool ModalOpen, string? ModalText);

public enum ClosePrompt
{
    Closed,
    DirtyPromptLeftOpen,
    Saved,
    Discarded,
}

public sealed class PaintUiException : Exception
{
    public PaintUiException(string message)
        : base(message)
    {
    }
}
