using System.Xml.Linq;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class UiLayoutContractTests
{
    [Fact]
    public void SettingsPagesDisableHorizontalScrollingAndBindToTheViewportWidth()
    {
        var document = XDocument.Load(RepositoryFile("src", "Tbox.App", "MainWindow.xaml"));
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var panelNames = new[] { "HomePanel", "GeneralPanel", "FolderJumpPanel", "ScreenPastePanel" };

        foreach (var panelName in panelNames)
        {
            var panel = document.Descendants()
                .Single(element => (string?)element.Attribute(xaml + "Name") == panelName);

            Assert.Equal("Stretch", (string?)panel.Attribute("HorizontalContentAlignment"));
            Assert.Equal("Disabled", (string?)panel.Attribute("HorizontalScrollMode"));
            Assert.Equal("Disabled", (string?)panel.Attribute("HorizontalScrollBarVisibility"));
            var content = panel.Elements().Single();
            Assert.Equal("Center", (string?)content.Attribute("HorizontalAlignment"));
            Assert.Equal("1120", (string?)content.Attribute("MaxWidth"));
            Assert.Contains("ViewportWidth", (string?)content.Attribute("Width"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DisplayModeHandlerRestoresTheRequiredFixedPaneAtWideWidths()
    {
        var source = File.ReadAllText(RepositoryFile("src", "Tbox.App", "MainWindow.xaml.cs"));
        var handlerStart = source.IndexOf("private void OnNavigationDisplayModeChanged", StringComparison.Ordinal);
        var nextHandler = source.IndexOf("private void OnSearchQuerySubmitted", handlerStart, StringComparison.Ordinal);

        Assert.True(handlerStart >= 0 && nextHandler > handlerStart);
        var handler = source[handlerStart..nextHandler];
        Assert.Contains("args.DisplayMode == NavigationViewDisplayMode.Expanded", handler, StringComparison.Ordinal);
        Assert.Contains("RootNavigation.IsPaneOpen = true", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RootNavigation.IsPaneOpen = false", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureOverlayExposesDetectionRestoreAndToolbarBindings()
    {
        var source = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));

        Assert.Contains("VirtualKey.Tab", source, StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown += OnRootPreviewKeyDown", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddKeyboardAccelerator(VirtualKey.Tab",
            source,
            StringComparison.Ordinal);
        Assert.Contains("DetectionModeChangeRequested", source, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.R", source, StringComparison.Ordinal);
        Assert.Contains("RestoreLastRegionRequested", source, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Space", source, StringComparison.Ordinal);
        Assert.Contains("ToggleToolbar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TogglePairedTool", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureSelectionExposesEightResizableHandlesAndPreservesAnnotations()
    {
        var overlay = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));
        var controller = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureAnnotationController.cs"));

        Assert.Contains("Enum.GetValues<CaptureSelectionHandle>()", overlay, StringComparison.Ordinal);
        Assert.Contains("TryBeginSelectionAdjustment", overlay, StringComparison.Ordinal);
        Assert.Contains("CaptureSelectionAdjustmentPolicy.Resize", overlay, StringComparison.Ordinal);
        Assert.Contains("PositionSelectionHandles", overlay, StringComparison.Ordinal);
        Assert.Contains("CursorForHandle", overlay, StringComparison.Ordinal);

        var adjustmentCheck = overlay.IndexOf(
            "TryBeginSelectionAdjustment(point.Position)",
            StringComparison.Ordinal);
        var annotationCheck = overlay.IndexOf(
            "_annotations.CurrentTool is not null",
            adjustmentCheck,
            StringComparison.Ordinal);
        Assert.True(adjustmentCheck >= 0 && annotationCheck > adjustmentCheck);

        var updateRegionStart = controller.IndexOf("internal void UpdateRegion", StringComparison.Ordinal);
        var resetStart = controller.IndexOf("internal void Reset()", updateRegionStart, StringComparison.Ordinal);
        Assert.True(updateRegionStart >= 0 && resetStart > updateRegionStart);
        Assert.DoesNotContain(
            "_document.Reset()",
            controller[updateRegionStart..resetStart],
            StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedImageOffersContextMenuAndSpaceToolbarAccess()
    {
        var source = File.ReadAllText(RepositoryFile("src", "Tbox.App", "PinnedImageWindow.cs"));
        var toolbar = File.ReadAllText(RepositoryFile("src", "Tbox.App", "AnnotationToolbar.cs"));

        Assert.Contains("new MenuFlyoutItem { Text = \"显示工具条\" }", source, StringComparison.Ordinal);
        Assert.Contains("new MenuFlyoutItem { Text = \"恢复完整贴图\" }", source, StringComparison.Ordinal);
        Assert.Contains("new MenuFlyoutItem { Text = \"文字识别\" }", source, StringComparison.Ordinal);
        Assert.Contains("RecognizeVisibleText", source, StringComparison.Ordinal);
        Assert.Contains("PinCropPolicy.TryCreate", source, StringComparison.Ordinal);
        Assert.Contains("_screen.Crop(", source, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Space", source, StringComparison.Ordinal);
        Assert.Contains("new AnnotationToolbar(", source, StringComparison.Ordinal);
        Assert.Contains("PinnedAnnotationToolbarWindow", source, StringComparison.Ordinal);
        Assert.Contains("FinishAnnotationEditing", source, StringComparison.Ordinal);
        Assert.Contains("\"\\uE73E\"", source, StringComparison.Ordinal);
        Assert.Contains("_annotations.RenderAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_root.Children.Add(_toolbar.View)", source, StringComparison.Ordinal);
        Assert.Contains("actions.RecognizeText", toolbar, StringComparison.Ordinal);
        Assert.Contains("actions.Terminal", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer", toolbar, StringComparison.Ordinal);

        var toolbarWindow = File.ReadAllText(RepositoryFile(
            "src",
            "Tbox.App",
            "PinnedAnnotationToolbarWindow.cs"));
        Assert.Contains("PinToolbarPlacementPolicy.PlaceAdjacent", toolbarWindow, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Escape", toolbarWindow, StringComparison.Ordinal);
        Assert.Contains(
            "Measure(new Size(double.PositiveInfinity, double.PositiveInfinity))",
            toolbarWindow,
            StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow(_ownerHandle)", toolbarWindow, StringComparison.Ordinal);
        Assert.Contains("WindowDpiScalePolicy.LogicalToPhysicalPixels", toolbarWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumWidth", toolbarWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureAndPinnedImageUseSharedToolbarAndPinnedCompletionCommitsValidWork()
    {
        var overlay = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));
        var pinned = File.ReadAllText(RepositoryFile("src", "Tbox.App", "PinnedImageWindow.cs"));
        var toolbar = File.ReadAllText(RepositoryFile("src", "Tbox.App", "AnnotationToolbar.cs"));
        var controller = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureAnnotationController.cs"));

        Assert.Contains("new AnnotationToolbar(", overlay, StringComparison.Ordinal);
        Assert.Contains("new AnnotationToolbar(", pinned, StringComparison.Ordinal);
        Assert.Contains("\"\\uE718\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"\\uE73E\"", pinned, StringComparison.Ordinal);
        Assert.Contains("文字识别", toolbar, StringComparison.Ordinal);
        Assert.Contains("_view.Child = tools", toolbar, StringComparison.Ordinal);

        var completionStart = controller.IndexOf("internal void CompleteEditing()", StringComparison.Ordinal);
        var nextMethod = controller.IndexOf("internal void CancelPointerGesture()", completionStart, StringComparison.Ordinal);
        Assert.True(completionStart >= 0 && nextMethod > completionStart);
        var completion = controller[completionStart..nextMethod];
        Assert.Contains("CommitTextEditor()", completion, StringComparison.Ordinal);
        Assert.Contains("FinishPolyline()", completion, StringComparison.Ordinal);
        Assert.Contains("CancelGesture()", completion, StringComparison.Ordinal);
        Assert.Contains("CurrentTool = null", completion, StringComparison.Ordinal);

        Assert.Contains("_annotations.FinishRequested += FinishAnnotationEditing", pinned, StringComparison.Ordinal);
        Assert.Contains("_annotations.CompleteEditing()", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (_annotations.CancelCurrentOperation()) return;",
            pinned,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureToolbarAddsLongCaptureAndRecordingWithoutAddingThemToPinnedMode()
    {
        var overlay = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));
        var pinned = File.ReadAllText(RepositoryFile("src", "Tbox.App", "PinnedImageWindow.cs"));
        var toolbar = File.ReadAllText(RepositoryFile("src", "Tbox.App", "AnnotationToolbar.cs"));
        var recording = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingReviewWindow.cs"));

        Assert.Contains("StartLongCapture: StartLongCaptureAsync", overlay, StringComparison.Ordinal);
        Assert.Contains("StartRecording: StartRecordingAsync", overlay, StringComparison.Ordinal);
        Assert.Contains("滚动长截图（手动滚动）", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("支持自动或手动滚动", toolbar, StringComparison.Ordinal);
        Assert.Contains("区域录屏", toolbar, StringComparison.Ordinal);
        Assert.DoesNotContain("StartLongCapture:", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("StartRecording:", pinned, StringComparison.Ordinal);
        Assert.Contains("RecordingExportFormat _format = RecordingExportFormat.Mp4", recording, StringComparison.Ordinal);
        Assert.Contains("图像格式不包含声音", recording, StringComparison.Ordinal);
        Assert.Contains("PickerLocationId.VideosLibrary", recording, StringComparison.Ordinal);
        Assert.Contains("PickerLocationId.PicturesLibrary", recording, StringComparison.Ordinal);
        Assert.Contains("_playerElement.SetMediaPlayer(null!)", recording, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(150)", recording, StringComparison.Ordinal);
        Assert.Contains("if (rerecord is { } region) RerecordRequested?.Invoke(region)", recording, StringComparison.Ordinal);

        var recordingControl = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingControlWindow.cs"));
        Assert.Contains("RecordingFrameRatePolicy.AllowedFrameRates", recordingControl, StringComparison.Ordinal);
        Assert.Contains("RecordingFrameRatePolicy.DefaultFrameRate", recordingControl, StringComparison.Ordinal);

        var longCapture = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCaptureWindow.cs"));
        Assert.Contains("缓慢向上或向下滚动", longCapture, StringComparison.Ordinal);
        Assert.Contains("LongCaptureCompletionAction.Copy", longCapture, StringComparison.Ordinal);
        Assert.Contains("LongCaptureCompletionAction.Pin", longCapture, StringComparison.Ordinal);
        Assert.Contains("LongCaptureCompletionAction.Save", longCapture, StringComparison.Ordinal);
        Assert.Contains("LongCaptureCompletionAction.Fullscreen", longCapture, StringComparison.Ordinal);
        Assert.Contains("AnnotationToolbar.CreateToolbarRoot(Visibility.Visible)", longCapture, StringComparison.Ordinal);
        Assert.Contains("AnnotationToolbar.CreateButton", longCapture, StringComparison.Ordinal);
        Assert.Contains("AnnotationToolbar.CreateSeparator()", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("立即贴图并打开截图工具栏", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("PinAndOpenToolbar", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("\"\\uE70F\"", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("Text = \"⠿\"", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("new ComboBox", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("查看长截图状态与操作说明", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("\"✂\"", longCapture, StringComparison.Ordinal);
        Assert.Contains("ScrollBarVisibility.Disabled", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleAutoScroll", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMouseWheel", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("_autoScrollTimer", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("Color.FromArgb(255, 32, 201, 87)", longCapture, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingControlAndReviewUseConfirmedCompactUi()
    {
        var control = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingControlWindow.cs"));
        var review = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingReviewWindow.cs"));

        Assert.Contains("TboxUi.CreateTaskSurface(tools)", control, StringComparison.Ordinal);
        Assert.Contains("_frameRateButton.Visibility = Visibility.Collapsed", control, StringComparison.Ordinal);
        Assert.Contains("_audioButton.Visibility = Visibility.Collapsed", control, StringComparison.Ordinal);
        Assert.Contains("_pausedText.Visibility = Visibility.Visible", control, StringComparison.Ordinal);
        Assert.Contains("Color.FromArgb(255, 255, 176, 32)", control, StringComparison.Ordinal);
        Assert.Contains("FontFamily = new FontFamily(\"Segoe Fluent Icons\")", control, StringComparison.Ordinal);
        Assert.DoesNotContain("IconButton(\"●\"", control, StringComparison.Ordinal);
        Assert.DoesNotContain("IconButton(\"■\"", control, StringComparison.Ordinal);
        Assert.DoesNotContain("IconButton(\"Ⅱ\"", control, StringComparison.Ordinal);

        Assert.Contains("Title = \"Tbox 录屏预览\"", review, StringComparison.Ordinal);
        Assert.Contains("ExtendsContentIntoTitleBar = true", review, StringComparison.Ordinal);
        Assert.Contains("SetTitleBar(titleBar)", review, StringComparison.Ordinal);
        Assert.Contains("系统声音已开启", review, StringComparison.Ordinal);
        Assert.Contains("FormatDescription(_format)", review, StringComparison.Ordinal);
        Assert.Contains("ApplyRecordingSliderVisual(_positionSlider)", review, StringComparison.Ordinal);
        Assert.Contains("SliderThumbBackground", review, StringComparison.Ordinal);
        Assert.Contains("SliderTrackValueFill", review, StringComparison.Ordinal);
        Assert.Contains("LabeledButton(\"\\uE777\", \"重新录制\"", review, StringComparison.Ordinal);
        Assert.Contains("LabeledButton(\"\\uE74E\", \"保存\"", review, StringComparison.Ordinal);
        Assert.Contains("Key = VirtualKey.Space", review, StringComparison.Ordinal);
        Assert.Contains("Modifiers = VirtualKeyModifiers.Control", review, StringComparison.Ordinal);
        Assert.DoesNotContain("_closeButton", review, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureModesShareEightHandleFrameStyleAndUseDistinctColors()
    {
        var frame = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureRegionFrame.cs"));
        var overlay = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));
        var longCapture = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCaptureWindow.cs"));
        var recording = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingControlWindow.cs"));
        var native = File.ReadAllText(RepositoryFile("src", "Tbox.App", "NativeMethods.cs"));

        Assert.Contains("Screenshot = Color.FromArgb(255, 0, 120, 212)", frame, StringComparison.Ordinal);
        Assert.Contains("LongCapture = Color.FromArgb(255, 255, 204, 0)", frame, StringComparison.Ordinal);
        Assert.Contains("Recording = Color.FromArgb(255, 255, 59, 48)", frame, StringComparison.Ordinal);
        Assert.Contains("var centers = new[]", frame, StringComparison.Ordinal);
        Assert.Contains("private readonly Grid _root", frame, StringComparison.Ordinal);
        Assert.Contains("private readonly Rectangle? _handleVisual", frame, StringComparison.Ordinal);
        Assert.Contains("Background = new SolidColorBrush(isHandle ? Colors.White : color)", frame, StringComparison.Ordinal);
        Assert.Contains("CaptureSelectionVisualStyle.HandleStrokeThickness", frame, StringComparison.Ordinal);
        Assert.Contains("CaptureSelectionVisualStyle.HandleStrokeThickness", overlay, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.RemoveWindowBorderAndRounding(hwnd)", frame, StringComparison.Ordinal);
        Assert.Contains("DwmwaWindowCornerPreference = 33", native, StringComparison.Ordinal);
        Assert.Contains("DwmwaBorderColor = 34", native, StringComparison.Ordinal);
        Assert.Contains("GwlStyle = -16", native, StringComparison.Ordinal);
        Assert.Contains("WsCaption = 0x00C00000L", native, StringComparison.Ordinal);
        Assert.Contains("SwpFramechanged = 0x0020", native, StringComparison.Ordinal);
        Assert.Contains("CaptureFramePalette.Screenshot", overlay, StringComparison.Ordinal);
        Assert.Contains("CaptureFramePalette.LongCapture", longCapture, StringComparison.Ordinal);
        Assert.Contains("CaptureFramePalette.Recording", recording, StringComparison.Ordinal);
    }

    [Fact]
    public void LongCaptureUsesManualBidirectionalStitchingAndRightSidePreview()
    {
        var longCapture = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCaptureWindow.cs"));
        var preview = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCapturePreviewWindow.cs"));
        var placement = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureModeWindowPlacement.cs"));

        Assert.Contains("ScrollCaptureFrameDecision.Moved", longCapture, StringComparison.Ordinal);
        Assert.Contains("ScrollCaptureDirection.Up", longCapture, StringComparison.Ordinal);
        Assert.Contains("ScrollCaptureDirection.Down", longCapture, StringComparison.Ordinal);
        Assert.Contains("_preview.UpdateViewport", longCapture, StringComparison.Ordinal);
        Assert.Contains("new LongCapturePreviewWindow(region)", longCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMouseWheel", longCapture, StringComparison.Ordinal);
        Assert.Contains("实时预览", preview, StringComparison.Ordinal);
        Assert.Contains("_viewportMarker", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFramePalette.LongCapture", preview, StringComparison.Ordinal);
        Assert.Contains("TboxUi.CreateTaskSurface", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureFramePalette.LongCapture", preview, StringComparison.Ordinal);
        Assert.Contains("PositionSidePreview", placement, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureDragAndToolbarHoverDoNotRequantizeOrRestartStableStates()
    {
        var overlay = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));
        var toolbar = File.ReadAllText(RepositoryFile("src", "Tbox.App", "AnnotationToolbar.cs"));

        Assert.Contains("PreviewAdjustedSelection", overlay, StringComparison.Ordinal);
        Assert.Contains("CommitAdjustedSelection", overlay, StringComparison.Ordinal);
        Assert.Contains("new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height)", overlay, StringComparison.Ordinal);
        Assert.Contains("private sealed class StableToolbarButton", toolbar, StringComparison.Ordinal);
        Assert.Contains("if (_selected == selected) return;", toolbar, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureModeControlsUseTargetDisplayDpiAfterShowing()
    {
        var placement = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureModeWindowPlacement.cs"));
        var longCapture = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCaptureWindow.cs"));
        var recording = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingControlWindow.cs"));

        Assert.Contains("PrepareOnTargetDisplay", placement, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow(hwnd)", placement, StringComparison.Ordinal);
        Assert.Contains("PrepareOnTargetDisplay(_windowHandle, region)", longCapture, StringComparison.Ordinal);
        Assert.Contains("PrepareOnTargetDisplay(_windowHandle, region)", recording, StringComparison.Ordinal);

        var longStart = longCapture.IndexOf("internal async Task StartAsync", StringComparison.Ordinal);
        var longShow = longCapture.IndexOf("_appWindow.Show", longStart, StringComparison.Ordinal);
        var longPosition = longCapture.IndexOf(
            "CaptureModeWindowPlacement.PositionAdjacent",
            longShow,
            StringComparison.Ordinal);
        Assert.True(longStart >= 0 && longShow > longStart && longPosition > longShow);

        var recordingShowMethod = recording.IndexOf("internal void Show()", StringComparison.Ordinal);
        var recordingShow = recording.IndexOf("_appWindow.Show", recordingShowMethod, StringComparison.Ordinal);
        var recordingPosition = recording.IndexOf(
            "CaptureModeWindowPlacement.PositionAdjacent",
            recordingShow,
            StringComparison.Ordinal);
        Assert.True(recordingShowMethod >= 0 && recordingShow > recordingShowMethod && recordingPosition > recordingShow);
    }

    [Fact]
    public void OcrEntrypointsUseOriginalPixelsAndResultWindowSupportsRequiredStates()
    {
        var overlay = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CaptureOverlayWindow.cs"));
        var resultWindow = File.ReadAllText(RepositoryFile("src", "Tbox.App", "OcrResultWindow.cs"));
        var service = File.ReadAllText(RepositoryFile("src", "Tbox.App", "TextRecognition.cs"));

        Assert.Contains("CaptureCompletionAction.RecognizeText", overlay, StringComparison.Ordinal);
        Assert.Contains("? _selectedScreen", overlay, StringComparison.Ordinal);
        Assert.Contains(": await _annotations.RenderAsync()", overlay, StringComparison.Ordinal);
        Assert.Contains("TextWrapping = TextWrapping.Wrap", resultWindow, StringComparison.Ordinal);
        Assert.Contains("_editor.SelectAll()", resultWindow, StringComparison.Ordinal);
        Assert.Contains("package.SetText(_editor.Text)", resultWindow, StringComparison.Ordinal);
        Assert.Contains("_recognitionCancellation?.Cancel()", resultWindow, StringComparison.Ordinal);
        Assert.Contains("TextRecognitionUnavailableException", resultWindow, StringComparison.Ordinal);
        Assert.Contains("private OcrResultWindow? _window", resultWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("List<OcrResultWindow>", resultWindow, StringComparison.Ordinal);
        Assert.Contains("BringToFront(keepAboveSource)", resultWindow, StringComparison.Ordinal);
        Assert.Contains("_topmostButton.IsChecked = true", resultWindow, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow(_windowHandle)", resultWindow, StringComparison.Ordinal);
        Assert.Contains("keepAboveSource: true", File.ReadAllText(RepositoryFile(
            "src", "Tbox.App", "ScreenPasteModule.cs")), StringComparison.Ordinal);
        var capturedScreen = File.ReadAllText(RepositoryFile("src", "Tbox.App", "CapturedScreen.cs"));
        Assert.Contains("DecodePixelType = DecodePixelType.Physical", capturedScreen, StringComparison.Ordinal);
        Assert.Contains("var decodeWidth = Width", capturedScreen, StringComparison.Ordinal);
        Assert.Contains("var decodeHeight = Height", capturedScreen, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = decodeWidth", capturedScreen, StringComparison.Ordinal);
        Assert.Contains("DecodePixelHeight = decodeHeight", capturedScreen, StringComparison.Ordinal);
        Assert.Contains("OcrEngine.TryCreateFromUserProfileLanguages()", service, StringComparison.Ordinal);
        Assert.Contains("OcrEngine.MaxImageDimension", service, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedUiPrototypeUsesApprovedTokensAndCoversPrimarySurfaces()
    {
        var styles = File.ReadAllText(RepositoryFile("docs", "ui-prototypes", "styles.css"));
        var prototype = File.ReadAllText(RepositoryFile("docs", "ui-prototypes", "prototype.js"));
        var specification = File.ReadAllText(RepositoryFile("docs", "ui-design-system.md"));

        Assert.Contains("--type-page-title: 28px", styles, StringComparison.Ordinal);
        Assert.Contains("--type-section-title: 16px", styles, StringComparison.Ordinal);
        Assert.Contains("--type-body: 14px", styles, StringComparison.Ordinal);
        Assert.Contains("--type-caption: 12px", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1080px)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px)", styles, StringComparison.Ordinal);
        Assert.Contains(".task-toolbar", styles, StringComparison.Ordinal);
        Assert.Contains("designSystemShowcase", prototype, StringComparison.Ordinal);
        Assert.Contains("captureToolbar", prototype, StringComparison.Ordinal);
        Assert.Contains("longCaptureToolbar", prototype, StringComparison.Ordinal);
        Assert.Contains("recordingToolbar", prototype, StringComparison.Ordinal);
        Assert.Contains("ocrSample", prototype, StringComparison.Ordinal);
        Assert.Contains("recordingReviewSample", prototype, StringComparison.Ordinal);
        Assert.Contains("页面标题 20", specification, StringComparison.Ordinal);
        Assert.Contains("30×32", specification, StringComparison.Ordinal);
        Assert.Contains("WCAG AA", specification, StringComparison.Ordinal);

        var preview = new FileInfo(RepositoryFile(
            "docs",
            "ui-prototypes",
            "screenshots",
            "tbox-ui-system-overview.webp"));
        Assert.True(preview.Exists);
        Assert.InRange(preview.Length, 1, (250 * 1024) - 1);
    }

    [Fact]
    public void WinUiSurfacesUseTheApprovedSharedDesignSystem()
    {
        var tokens = File.ReadAllText(RepositoryFile("src", "Tbox.App", "TboxUi.cs"));
        var resources = File.ReadAllText(RepositoryFile("src", "Tbox.App", "App.xaml"));
        var settings = File.ReadAllText(RepositoryFile("src", "Tbox.App", "MainWindow.xaml"));
        var settingsCode = File.ReadAllText(RepositoryFile("src", "Tbox.App", "MainWindow.xaml.cs"));
        var toolbar = File.ReadAllText(RepositoryFile("src", "Tbox.App", "AnnotationToolbar.cs"));
        var longControl = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCaptureWindow.cs"));
        var longPreview = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCapturePreviewWindow.cs"));
        var longReview = File.ReadAllText(RepositoryFile("src", "Tbox.App", "LongCaptureReviewWindow.cs"));
        var recordingControl = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingControlWindow.cs"));
        var recordingReview = File.ReadAllText(RepositoryFile("src", "Tbox.App", "RecordingReviewWindow.cs"));
        var ocr = File.ReadAllText(RepositoryFile("src", "Tbox.App", "OcrResultWindow.cs"));

        Assert.Contains("PageTitleFontSize = 28", tokens, StringComparison.Ordinal);
        Assert.Contains("SectionTitleFontSize = 16", tokens, StringComparison.Ordinal);
        Assert.Contains("BodyFontSize = 14", tokens, StringComparison.Ordinal);
        Assert.Contains("CaptionFontSize = 12", tokens, StringComparison.Ordinal);
        Assert.Contains("TaskButtonWidth = 30", tokens, StringComparison.Ordinal);
        Assert.Contains("TaskButtonHeight = 32", tokens, StringComparison.Ordinal);
        Assert.Contains("TaskIconBoxSize = 20", tokens, StringComparison.Ordinal);
        Assert.Contains("CreateTaskSurface", tokens, StringComparison.Ordinal);
        Assert.Contains("ApplyTaskButtonVisual", tokens, StringComparison.Ordinal);
        Assert.Contains("ConfigureTitleBar", tokens, StringComparison.Ordinal);

        Assert.Contains("<Setter Property=\"FontSize\" Value=\"28\"/>", resources, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"16\"/>", resources, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"14\"/>", resources, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"12\"/>", resources, StringComparison.Ordinal);
        Assert.Contains("CompactModeThresholdWidth=\"1080\"", settings, StringComparison.Ordinal);
        Assert.Contains("ExpandedModeThresholdWidth=\"1080\"", settings, StringComparison.Ordinal);
        Assert.Contains("RootGrid.ActualWidth < 720", settingsCode, StringComparison.Ordinal);
        Assert.Contains("TboxUi.SetThemePreference(theme)", settingsCode, StringComparison.Ordinal);
        foreach (var offScaleSpacing in new[] { 3, 5, 6, 7, 9, 10, 14, 18, 20, 28 })
        {
            Assert.DoesNotContain($"Spacing=\"{offScaleSpacing}\"", settings, StringComparison.Ordinal);
            Assert.DoesNotContain($"ColumnSpacing=\"{offScaleSpacing}\"", settings, StringComparison.Ordinal);
            Assert.DoesNotContain($"RowSpacing=\"{offScaleSpacing}\"", settings, StringComparison.Ordinal);
        }

        Assert.Contains("TboxUi.CreateTaskSurface", toolbar, StringComparison.Ordinal);
        Assert.Contains("TboxUi.ApplyTaskButtonVisual", toolbar, StringComparison.Ordinal);
        Assert.Contains("TboxUi.CaptionFontSize", longControl, StringComparison.Ordinal);
        Assert.Contains("TboxUi.CreateTaskSurface", longPreview, StringComparison.Ordinal);
        Assert.Contains("RequestedTheme = TboxUi.RequestedTheme", longReview, StringComparison.Ordinal);
        Assert.Contains("RequestedTheme = TboxUi.RequestedTheme", ocr, StringComparison.Ordinal);
        Assert.Contains("RequestedTheme = ElementTheme.Dark", recordingReview, StringComparison.Ordinal);
        Assert.Contains("TboxUi.CreateTaskSurface", recordingControl, StringComparison.Ordinal);
        Assert.Contains("TboxUi.RecordingModeColor", recordingControl, StringComparison.Ordinal);
        Assert.Contains("QueuePlacementRefresh()", recordingControl, StringComparison.Ordinal);
        Assert.Contains("UpdatePauseAccessibility(\"继续录制\")", recordingControl, StringComparison.Ordinal);
        Assert.Contains("录制系统声音（已开启）", recordingControl, StringComparison.Ordinal);
        Assert.DoesNotContain("_statusText.MinWidth", recordingControl, StringComparison.Ordinal);
        Assert.Contains("TboxUi.LongCaptureModeColor", longControl, StringComparison.Ordinal);
        Assert.Contains("ScheduleLineNumberUpdate", ocr, StringComparison.Ordinal);
        Assert.Contains("_editor.UpdateLayout()", ocr, StringComparison.Ordinal);
        Assert.Contains("presenter.SetBorderAndTitleBar(true, true)", recordingReview, StringComparison.Ordinal);
        Assert.Contains("_appWindow.IsShownInSwitchers = true", recordingReview, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness = new Thickness(2)", recordingReview, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tbox.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(relativeSegments).ToArray());
    }
}
