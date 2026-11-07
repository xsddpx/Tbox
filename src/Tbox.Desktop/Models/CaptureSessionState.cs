using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class CaptureSessionState
{
    public uint? ActiveDisplay { get; private set; }
    public CaptureSelectionBounds? Selection { get; private set; }
    public bool IsComplete { get; private set; }
    public bool SelectionWasDetected { get; private set; }
    public long Revision { get; private set; }

    public void Select(uint display, CaptureSelectionBounds? selection, bool detected = false)
    {
        if (IsComplete) return;
        Revision++;
        ActiveDisplay = display;
        Selection = selection;
        SelectionWasDetected = selection is not null && detected;
    }

    public bool TryComplete()
    {
        if (IsComplete || Selection is not { Width: > 0, Height: > 0 }) return false;
        IsComplete = true;
        Revision++;
        return true;
    }

    public void Cancel()
    {
        IsComplete = true;
        Revision++;
        Selection = null;
        ActiveDisplay = null;
        SelectionWasDetected = false;
    }
}
