using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core.ModelServices;
using Tbox.Desktop.ModelServices;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class ModelServicesPageTests
{
    [AvaloniaFact]
    public async Task EmptyStateShowsOnePrimaryAddActionAndHelp()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        Assert.False(scope.Page.IsEditing);
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "还没有添加连接");
        var accents = scope.Page.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Classes.Contains("accent")).ToArray();
        Assert.Single(accents);
        Assert.Equal("添加连接", accents[0].Content as string);
        Assert.Empty(scope.Page.GetVisualDescendants().OfType<Expander>());
        var help = Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "模型服务说明");
        Assert.Equal(ModelServicesPage.HelpText, ToolTip.GetTip(help));
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == ModelServicesPage.HelpText);
    }

    [AvaloniaFact]
    public async Task DeepSeekPresetSavesWithOnlyAKeyAndBecomesDefault()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        Assert.True(scope.Page.IsEditing);
        Assert.Equal("DeepSeek", scope.Page.ConnectionName.Text);
        Assert.Equal("https://api.deepseek.com", scope.Page.BaseUrl.Text);
        Assert.Contains("deepseek-chat", scope.Page.Models);
        scope.Page.Secret.Text = "simple-test-key";
        await scope.Page.SaveAsync();
        Assert.Equal("deepseek-chat", scope.Service.Configuration.DefaultModel!.ModelId);
        Assert.Equal(0, scope.Calls);
        Assert.Equal("simple-test-key", Assert.Single(scope.Credentials.Values).Value);
        Assert.Equal("已保存", scope.Page.Status);
        Assert.False(scope.Page.IsEditingKey);
        await scope.Page.OpenAsync();
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "DeepSeek");
    }

    [AvaloniaFact]
    public async Task ZenMuxPresetUsesItsOpenAiCompatibleAddressAndDoesNotInventModels()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.SelectPreset("ZenMux");
        Assert.Equal("ZenMux", scope.Page.ConnectionName.Text);
        Assert.Equal("https://zenmux.ai/api/v1", scope.Page.BaseUrl.Text);
        Assert.Empty(scope.Page.Models);
        scope.Page.Secret.Text = "zenmux-test-key";
        await scope.Page.SaveAsync();
        Assert.Null(scope.Service.Configuration.DefaultModel);
        Assert.Equal("https://zenmux.ai/api/v1", scope.Service.Configuration.Connections.Single().BaseUrl);
    }

    [AvaloniaFact]
    public async Task SavingNeverReadsBackTheSecretAndReplacementCanBeCancelled()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "page-test-secret";
        scope.Page.AddModel("page-model");
        await scope.Page.SaveAsync();
        Assert.Equal("", scope.Page.Secret.Text);
        Assert.False(scope.Page.IsEditingKey);
        Assert.Equal(0, scope.Credentials.ReadCount);
        Assert.Equal(0, scope.Calls);
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "••••••••  · 已保存");
        scope.Page.BeginReplaceKey();
        Assert.True(scope.Page.IsEditingKey);
        scope.Page.Secret.Text = "replacement-test-secret";
        scope.Page.CancelReplace();
        Assert.Equal("", scope.Page.Secret.Text);
        Assert.False(scope.Page.IsEditingKey);
        await scope.Page.SaveAsync();
        Assert.Equal("page-test-secret", Assert.Single(scope.Credentials.Values).Value);
    }

    [AvaloniaFact]
    public async Task TestUsesTheSavedConnectionAndDirtyEditsBlockAnotherTest()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "page-test-secret";
        await scope.Page.SaveAsync();
        await scope.Page.TestAsync();
        Assert.Equal("连接正常", scope.Page.Status);
        Assert.Equal(1, scope.Calls);
        scope.Page.AddModel("different-model");
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Page.TestAsync());
        Assert.Equal(1, scope.Calls);
    }

    [AvaloniaFact]
    public async Task FetchModelsUpdatesTheDraftOnlyAndRequiresAnotherSave()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "page-test-secret";
        await scope.Page.SaveAsync();
        await scope.Page.FetchAsync();
        Assert.Equal("已获取，待保存", scope.Page.Status);
        Assert.Equal(2, scope.Page.Models.Count);
        Assert.Equal("fetched-one", scope.Page.Models[0]);
        Assert.Single(scope.Service.Configuration.Connections.Single().Models);
        await scope.Page.SaveAsync();
        Assert.Equal(2, scope.Service.Configuration.Connections.Single().Models.Length);
        Assert.Equal("fetched-one", scope.Service.Configuration.DefaultModel!.ModelId);
    }

    [AvaloniaFact]
    public async Task ListShowsTheSavedConnectionAndDefaultModel()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "list-test-key";
        await scope.Page.SaveAsync();
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        await scope.Page.OpenAsync();
        scope.Page.Width = 530;
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        Assert.False(scope.Page.IsEditing);
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "DeepSeek");
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.Content as string == "添加连接");
        TaskToolbarTests.Preview(scope.Window, "model-services-list");
    }

    [AvaloniaFact]
    public async Task CustomTabStartsWithABlankConnectionAndShowsAllFields()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        scope.Page.Width = 530;
        await scope.Page.BeginAddAsync();
        var custom = Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "自定义接口");
        custom.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        Assert.True(scope.Page.ConnectionName.IsEffectivelyVisible);
        Assert.Equal("自定义服务", scope.Page.ConnectionName.Text);
        Assert.Equal("", scope.Page.BaseUrl.Text);
        Assert.Empty(scope.Page.Models);
        Assert.False(scope.Page.BaseUrl.IsReadOnly);
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "添加模型");
        scope.Page.ConnectionName.Text = "自定义中转";
        scope.Page.BaseUrl.Text = "https://gateway.example/v1";
        scope.Page.Secret.Text = "custom-test-key";
        scope.Page.AddModel("custom-model");
        await scope.Page.SaveAsync();
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        Assert.Equal("自定义中转", scope.Service.Configuration.Connections.Single().Name);
        Assert.Equal(new[] { "custom-model" }, scope.Service.Configuration.Connections.Single().Models);
        Assert.NotNull(scope.Service.Configuration.Connections.Single().CredentialId);
        Assert.False(scope.Page.IsEditingKey);
        var savedKey = Assert.Single(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "••••••••  · 已保存");
        Assert.True(savedKey.IsEffectivelyVisible);
        Assert.True(savedKey.Bounds.Height > 0, "saved key bounds: " + savedKey.Bounds);
        var modelRow = Assert.Single(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "custom-model");
        Assert.True(modelRow.IsEffectivelyVisible);
        Assert.True(modelRow.Bounds.Height > 0, "model row bounds: " + modelRow.Bounds);
        TaskToolbarTests.Preview(scope.Window, "model-services-custom");
    }

    [AvaloniaTheory]
    [InlineData(680)]
    [InlineData(800)]
    [InlineData(1280)]
    public async Task ModelPagesStayInsideTheAvailableContentWidth(double width)
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        scope.Window.Width = width; scope.Window.Height = 600;
        // Match the main window's 208 sidebar and content margins.
        scope.Page.Width = width - 208 - 10 - 52;
        scope.Window.Show(); scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.Classes.Contains("accent"));
        await scope.Page.BeginAddAsync();
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        foreach (var field in scope.Page.GetVisualDescendants().OfType<TextBox>().Where(control => control.IsEffectivelyVisible))
            Assert.True(field.Bounds.Width <= scope.Page.Bounds.Width, $"{field.Bounds} / {scope.Page.Bounds}");
        Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.Classes.Contains("accent"));
        TaskToolbarTests.Preview(scope.Window, $"model-services-{width}");
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbox-model-page-" + Guid.NewGuid().ToString("N"));
        public ModelServiceTests.Credentials Credentials { get; } = new();
        public ModelService Service { get; }
        public ModelServicesPage Page { get; }
        public Window Window { get; } = new();
        public int Calls { get; private set; }
        public Scope()
        {
            Directory.CreateDirectory(_directory);
            Service = new(Path.Combine(_directory, "models.json"), Credentials, new ModelServiceTests.Handler((request, _) =>
            {
                Calls++;
                if (request.Method == HttpMethod.Get)
                    return Task.FromResult(ModelServiceTests.Response("{\"data\":[{\"id\":\"fetched-one\"},{\"id\":\"fetched-two\"}]}", "application/json"));
                return Task.FromResult(ModelServiceTests.Response("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
            }));
            Page = new(Service, () => Window);
            Ui.Configure(Window, "模型服务测试", 800, 600);
            Window.Content = new ScrollViewer { Content = Page, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            Window.Show(); Window.UpdateLayout();
        }
        public void Dispose() { Page.Dispose(); Window.Close(); Service.Dispose(); Directory.Delete(_directory, true); }
    }
}
