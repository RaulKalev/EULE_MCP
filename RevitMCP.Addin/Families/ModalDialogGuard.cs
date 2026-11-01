using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Closes Revit's dialogs while it is alive, so a call cannot wait on a window nobody is watching, and keeps what they said.
/// </summary>
internal sealed class ModalDialogGuard : IDisposable
{
    private const int MessageBoxOk = 1;

    private readonly UIApplication _uiapp;

    public ModalDialogGuard(UIApplication uiapp)
    {
        _uiapp = uiapp;
        _uiapp.DialogBoxShowing += OnDialogBoxShowing;
    }

    public List<string> Dismissed { get; } = new();

    private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        switch (e)
        {
            case TaskDialogShowingEventArgs taskDialog:
                Dismissed.Add(taskDialog.Message);
                e.OverrideResult((int)TaskDialogResult.Close);
                break;
            case MessageBoxShowingEventArgs messageBox:
                Dismissed.Add(messageBox.Message);
                e.OverrideResult(MessageBoxOk);
                break;
            default:
                Dismissed.Add(e.DialogId);
                e.OverrideResult((int)TaskDialogResult.Close);
                break;
        }
    }

    public void Dispose() => _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
}
