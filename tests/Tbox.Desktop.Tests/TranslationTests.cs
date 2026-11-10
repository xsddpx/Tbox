using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core.ModelServices;
using Tbox.Core.Translation;
using Tbox.Desktop.Translation;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class TranslationTests
{
    [Fact]
    public async Task StreamsTranslationAndRequiresANormalNonemptyCompletion()
    {
        var fake = new Capability();
        using var session = new TranslationSession(fake);
        var observed = new List<string>(); session.Changed += () => observed.Add(session.Text);
        await session.TranslateAsync("Hello world", "zh-Hans");
        Assert.Equal("你好，世界", session.Text); Assert.Equal(TranslationState.Completed, session.State);
        Assert.Contains("你好，", observed); Assert.Contains("输入 10 / 输出 3", session.Status);
        Assert.Single(fake.Requests); Assert.Equal("Hello world", fake.Requests[0].Messages[1].Content);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData(null)]
    public async Task NeverMarksTruncatedOrUnfinishedOutputAsSuccessful(string? reason)
    {
        var fake = new Capability { Finish = reason };
        using var session = new TranslationSession(fake);
        await session.TranslateAsync("Hello", "en");
        Assert.Equal(TranslationState.Partial, session.State); Assert.NotEmpty(session.Text); Assert.DoesNotContain("翻译完成", session.Status);
    }

    [Fact]
    public async Task PreservesPartialOutputAndDoesNotExposeUnknownExceptionContents()
    {
        using var session = new TranslationSession(new Capability { Error = new IOException("private original text or credential") });
        await session.TranslateAsync("Hello", "en");
        Assert.Equal(TranslationState.Partial, session.State); Assert.NotEmpty(session.Text);
        Assert.DoesNotContain("private", session.Status); Assert.Contains("不会自动重试", session.Status);
    }

    [Fact]
    public async Task StoppingAndNewRequestsIgnoreLateFragmentsEvenWhenBackendIgnoresCancellation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new Capability { Gate = gate.Task };
        using var session = new TranslationSession(fake);
        var old = session.TranslateAsync("old", "en");
        Assert.True(session.IsRunning); Assert.Equal("你好，", session.Text);
        session.Stop(); Assert.Equal(TranslationState.Stopped, session.State); Assert.Contains("部分", session.Status);
        fake.Gate = null;
        await session.TranslateAsync("new", "zh-Hans");
        var latest = session.Text;
        gate.SetResult(); await old;
        Assert.Equal(latest, session.Text); Assert.Equal(TranslationState.Completed, session.State);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task InvalidatingChangedInputClearsOldResultsAndSuppressesLateCompletion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new TranslationSession(new Capability { Gate = gate.Task });
        var running = session.TranslateAsync("old", "en"); session.Invalidate(); gate.SetResult(); await running;
        Assert.Equal("", session.Text); Assert.Equal(TranslationState.Idle, session.State);
    }

    [AvaloniaFact]
    public async Task OpeningAndChangingInputsNeverReadsClipboardOrSendsRequests()
    {
        using var scope = new PageScope();
        await scope.Page.OpenAsync();
        scope.Page.Source.Text = "Hello";
        scope.Page.Target.SelectedItem = TranslationPolicy.Languages[2];
        Assert.Empty(scope.Capability.Requests); Assert.Equal(0, scope.ClipboardReads);
        await scope.Page.PasteAsync(); Assert.Equal(1, scope.ClipboardReads); Assert.Equal("Clipboard text", scope.Page.Source.Text);
        await scope.Page.TranslateOrStopAsync();
        Assert.Single(scope.Capability.Requests);
        Assert.Equal("Clipboard text", scope.Capability.Requests[0].Messages[1].Content);
        await scope.Page.CopyAsync(); Assert.Equal(scope.Page.Session.Text, scope.Copied);
        scope.Page.Source.Text = "Changed";
        Assert.Equal("", scope.Page.Result.Text); Assert.Single(scope.Capability.Requests);
    }

    [AvaloniaFact]
    public async Task MissingConfigurationPreservesInputAndNeverCallsTheCapability()
    {
        using var scope = new PageScope { Config = new() };
        await scope.Page.OpenAsync(); scope.Page.Source.Text = "Keep this draft";
        await scope.Page.TranslateOrStopAsync();
        Assert.Empty(scope.Capability.Requests); Assert.Equal("Keep this draft", scope.Page.Source.Text);
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "配置模型"));
    }

    [AvaloniaFact]
    public async Task ExplicitModelChoiceDoesNotModifyTheGlobalDefaultAndDoesNotSilentlyFallbackWhenDeleted()
    {
        using var scope = new PageScope(); await scope.Page.OpenAsync();
        var originalDefault = scope.Config.DefaultModel;
        scope.Page.Model.SelectedIndex = 2; scope.Page.Source.Text = "Hello";
        await scope.Page.TranslateOrStopAsync();
        Assert.Equal("second", scope.Capability.Requests.Single().Model!.ModelId);
        Assert.Equal(originalDefault, scope.Config.DefaultModel);
        scope.Config = scope.Config with { Connections = [] , DefaultModel = null };
        await scope.Page.OpenAsync(); await scope.Page.TranslateOrStopAsync();
        Assert.Single(scope.Capability.Requests);
    }

    [AvaloniaFact]
    public async Task LeavingStopsGenerationAndCopyingPartialOutputIsExplicit()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new PageScope(); scope.Capability.Gate = gate.Task;
        await scope.Page.OpenAsync(); scope.Page.Source.Text = "Hello";
        var running = scope.Page.TranslateOrStopAsync();
        Assert.True(scope.Page.Session.IsRunning); scope.Page.Leave();
        await scope.Page.CopyAsync(); Assert.Equal("你好，", scope.Copied);
        gate.SetResult(); await running;
        Assert.Equal(TranslationState.Stopped, scope.Page.Session.State);
    }

    [AvaloniaTheory]
    [InlineData(680)]
    [InlineData(800)]
    [InlineData(1280)]
    public async Task EditorsReflowWithoutLosingSourceOrTranslation(double width)
    {
        using var scope = new PageScope(); await scope.Page.OpenAsync(); scope.Page.Source.Text = "Hello";
        await scope.Page.TranslateOrStopAsync();
        scope.Page.Width = width - 208 - 10 - 52;
        scope.Window.Width = width; scope.Window.Height = 600;
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        var source = scope.Page.Source.TranslatePoint(default, scope.Window)!.Value;
        var target = scope.Page.Result.TranslatePoint(default, scope.Window)!.Value;
        if (width == 680) Assert.True(target.Y > source.Y); else Assert.True(target.X > source.X);
        Assert.Equal("Hello", scope.Page.Source.Text); Assert.Equal("你好，世界", scope.Page.Result.Text);
        Assert.All(scope.Page.GetVisualDescendants().OfType<ComboBox>(), c => Assert.True(c.Bounds.Width <= scope.Page.Width));
        TaskToolbarTests.Preview(scope.Window, $"translation-{width}");
    }

    private sealed class Capability : ITextModelService
    {
        public List<TextGenerationRequest> Requests { get; } = [];
        public string? Finish { get; set; } = "stop";
        public Exception? Error { get; set; }
        public Task? Gate { get; set; }
        public async IAsyncEnumerable<TextGenerationEvent> GenerateAsync(TextGenerationRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); var gate = Gate;
            yield return new("你好，");
            if (gate is not null) await gate; // Deliberately ignores cancellation, to verify stale-result protection.
            if (Error is not null) throw Error;
            yield return new("世界");
            if (Finish is not null) yield return new(FinishReason: Finish, Usage: new(10, 3));
        }
    }
    private sealed class PageScope : IDisposable
    {
        public Capability Capability { get; } = new();
        public ModelConfiguration Config { get; set; }
        public TranslationPage Page { get; }
        public Window Window { get; } = new();
        public int ClipboardReads { get; private set; }
        public string? Copied { get; private set; }
        public PageScope()
        {
            var connection = new ModelConnection(Guid.NewGuid(), "示例模型服务", "https://api.example.invalid/v1", ["first", "second"]);
            Config = new() { Connections = [connection], DefaultModel = new(connection.Id, "first") };
            Page = new(Capability, () => Task.FromResult(Config), () => { },
                () => { ClipboardReads++; return Task.FromResult<string?>("Clipboard text"); }, text => { Copied = text; return Task.CompletedTask; });
            Ui.Configure(Window, "翻译测试", 800, 600);
            Window.Content = new ScrollViewer { Content = Page, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            Page.Width = 530; Window.Show(); Window.UpdateLayout();
        }
        public void Dispose() { Page.Dispose(); Window.Close(); }
    }
}
