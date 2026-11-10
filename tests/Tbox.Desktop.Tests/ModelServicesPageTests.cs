using Avalonia;
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
    public async Task DeepSeekSetupOnlyRequiresAKeyAndHasNoCollapsedSettingsOrExplanatoryParagraphs()
    {
        using var scope = new Scope(); await scope.Page.OpenAsync();
        Assert.Empty(scope.Page.GetVisualDescendants().OfType<Expander>());
        Assert.Equal("https://api.deepseek.com", scope.Page.BaseUrl.Text);
        Assert.Equal("deepseek-chat", scope.Page.Models.Text);
        scope.Page.Secret.Text = "simple-test-key";
        await scope.Page.SaveAsync();
        Assert.Equal("deepseek-chat", scope.Service.Configuration.DefaultModel!.ModelId);
        Assert.Equal(0, scope.Calls);
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("保存不会发起调用") == true || text.Text?.Contains("密钥由系统安全保存") == true);
        var help = Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => Avalonia.Automation.AutomationProperties.GetName(button) == "模型服务说明");
        Assert.Equal(ModelServicesPage.HelpText, ToolTip.GetTip(help));
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == ModelServicesPage.HelpText);
        Assert.Equal("simple-test-key", Assert.Single(scope.Credentials.Values).Value);
    }

    [AvaloniaFact]
    public async Task ChoosingCustomServiceKeepsFieldsVisibleAndDoesNotReuseTypedKey()
    {
        using var scope = new Scope(); await scope.Page.OpenAsync(); scope.Page.Secret.Text = "unsaved-test-key";
        scope.Page.Provider.SelectedIndex = 1;
        Assert.True(scope.Page.BaseUrl.IsEffectivelyVisible);
        Assert.Equal("", scope.Page.BaseUrl.Text); Assert.Equal("", scope.Page.Secret.Text);
        Assert.Equal(0, scope.Calls);
    }
    [AvaloniaFact]
    public async Task SavingShowsAnOpaquePlaceholderWithoutReadingBackTheSecretAndDoesNotTest()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        scope.Fill();
        await scope.Page.SaveAsync();
        Assert.Equal("", scope.Page.Secret.Text);
        Assert.False(scope.Page.IsEditingKey);
        Assert.Equal("已保存", scope.Page.Status);
        Assert.Equal(0, scope.Credentials.ReadCount); Assert.Equal(0, scope.Calls);
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "••••••••••••  · 已保存");
        scope.Page.BeginReplaceKey(); Assert.True(scope.Page.IsEditingKey);
        scope.Page.Secret.Text = "replacement-test-secret";
        scope.Page.CancelReplaceKey();
        Assert.Equal("", scope.Page.Secret.Text);
        await scope.Page.SaveAsync();
        Assert.Equal("page-test-secret", Assert.Single(scope.Credentials.Values).Value);
    }

    [AvaloniaFact]
    public async Task TestStateIsSeparateFromSavedStateAndEditsInvalidateIt()
    {
        using var scope = new Scope(); await scope.Page.OpenAsync(); scope.Fill(); await scope.Page.SaveAsync();
        await scope.Page.TestAsync();
        Assert.Equal("连接正常", scope.Page.Status); Assert.Equal(1, scope.Calls);
        scope.Page.Models.Text = "different-model";
        Assert.Equal("未保存", scope.Page.Status);
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Page.TestAsync());
        Assert.Equal(1, scope.Calls);
    }

    [AvaloniaTheory]
    [InlineData(680)]
    [InlineData(800)]
    [InlineData(1280)]
    public async Task ModelSettingsKeepFieldsInsideTheAvailableContentWidth(double width)
    {
        using var scope = new Scope(); await scope.Page.OpenAsync(); scope.Fill();
        scope.Window.Width = width; scope.Window.Height = 600;
        // Match the main window's 208 sidebar and content margins.
        scope.Page.Width = width - 208 - 10 - 52;
        scope.Window.Show(); scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        foreach (var field in scope.Page.GetVisualDescendants().OfType<TextBox>().Where(c => c.IsEffectivelyVisible))
            Assert.True(field.Bounds.Width <= scope.Page.Bounds.Width, $"{field.Bounds} / {scope.Page.Bounds}");
        Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("accent"));
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
            Service = new(Path.Combine(_directory, "models.json"), Credentials, new ModelServiceTests.Handler((_, _) =>
            {
                Calls++;
                return Task.FromResult(ModelServiceTests.Response("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
            }));
            Page = new(Service, () => Window);
            Ui.Configure(Window, "模型服务测试", 800, 600);
            Window.Content = new ScrollViewer { Content = Page, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            Window.Show(); Window.UpdateLayout();
        }
        public void Fill()
        {
            Page.ConnectionName.Text = "示例连接"; Page.BaseUrl.Text = "https://api.example.com/v1";
            Page.Secret.Text = "page-test-secret"; Page.Models.Text = "text-model\nsecond-model"; Page.UseAsDefault.IsChecked = true;
        }
        public void Dispose() { Page.Dispose(); Window.Close(); Service.Dispose(); Directory.Delete(_directory, true); }
    }
}
