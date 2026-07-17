namespace Tbox.Core;

public enum CapturePointerCancellationAction
{
    PreserveSelection,
    CancelAnnotationGesture,
    CancelRegionSelection
}

public static class CaptureInteractionPolicy
{
    public static CapturePointerCancellationAction ResolvePointerCancellation(
        bool annotationGestureInProgress,
        bool regionSelectionInProgress)
    {
        if (annotationGestureInProgress)
        {
            return CapturePointerCancellationAction.CancelAnnotationGesture;
        }

        return regionSelectionInProgress
            ? CapturePointerCancellationAction.CancelRegionSelection
            : CapturePointerCancellationAction.PreserveSelection;
    }
}
