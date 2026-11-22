using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
    public async Task SuccessfulTestsSurviveRestartAndOnlyApplyToTheTestedConfiguration()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "fixture-key";
        await scope.Page.TestAsync();
        await scope.Page.SaveAsync();
        var id = scope.Page.ConnectionId;

        async Task<bool> ReloadVerified()
        {
            using var reopened = new ModelService(scope.ConfigPath);
            await reopened.InitializeAsync();
            return reopened.Configuration.Connections.Single().IsVerified;
        }

        using (var reopened = new Scope())
        {
            File.Copy(scope.ConfigPath, reopened.ConfigPath);
            await reopened.Page.OpenAsync();
            reopened.Window.UpdateLayout();
            Assert.Contains(reopened.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "可用");
            TaskToolbarTests.Preview(reopened.Window, "model-verified-after-restart");
            await reopened.Page.BeginEditAsync(id);
            Assert.Contains(reopened.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "连接正常");
            Assert.Empty(reopened.Requests);
        }
        await scope.Page.BeginEditAsync(id);
        scope.Page.Secret.Text = "other-fixture-key";
        await scope.Page.SaveAsync();
        Assert.False(await ReloadVerified());
        await scope.Page.TestAsync();
        Assert.True(await ReloadVerified());

        scope.Page.BaseUrl.Text = "https://example.org/v1";
        await scope.Page.SaveAsync();
        Assert.False(await ReloadVerified());
        await scope.Page.TestAsync();
        scope.Page.AddModel("another-model");
        await scope.Page.SaveAsync();
        Assert.False(await ReloadVerified());
        await scope.Page.TestAsync();
        Assert.True(await ReloadVerified());
        var config = await File.ReadAllTextAsync(scope.ConfigPath);
        await File.WriteAllTextAsync(scope.ConfigPath, config.Replace("other-fixture-key", "edited-outside-the-app"));
        Assert.False(await ReloadVerified());
    }

    [AvaloniaFact]
    public async Task EmptyStateShowsOnePrimaryAddAction()
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
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "模型服务说明");
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
        Assert.Equal(["deepseek-flash"], scope.Page.Models);
        scope.Page.Secret.Text = "simple-test-key";
        await scope.Page.SaveAsync();
        Assert.Equal("deepseek-flash", scope.Service.Configuration.ModelOrder[0].ModelId);
        Assert.Equal(0, scope.Calls);
        Assert.Equal("simple-test-key", scope.Service.KeyOf(scope.Page.ConnectionId));
        Assert.Equal("已保存", scope.Page.Status);
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
        Assert.Empty(scope.Service.Configuration.ModelOrder);
        Assert.Equal("https://zenmux.ai/api/v1", scope.Service.Configuration.Connections.Single().BaseUrl);
    }

    [AvaloniaFact]
    public async Task SavedKeyIsMaskedUntilRevealedAndEditedInPlace()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "page-test-secret";
        scope.Page.AddModel("page-model");
        await scope.Page.SaveAsync();
        Assert.Equal(0, scope.Calls);
        await scope.Page.OpenAsync();
        await scope.Page.BeginEditAsync(scope.Page.ConnectionId);
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Equal("page-test-secret", scope.Page.Secret.Text);
        Assert.False(scope.Page.IsKeyRevealed);
        Assert.NotEqual('\0', scope.Page.Secret.PasswordChar);
        var reveal = Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "显示密钥");
        reveal.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(scope.Page.IsKeyRevealed);
        Assert.IsType<LineIcon>(reveal.Content);
        Assert.Same(reveal, scope.Page.Secret.InnerRightContent);
        Assert.Equal("隐藏密钥", AutomationProperties.GetName(reveal));
        foreach (var removed in new[] { "替换", "删除密钥", "取消替换" })
            Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == removed);
        Assert.Empty(scope.Page.GetVisualDescendants().OfType<CheckBox>());
        scope.Page.Secret.Text = "edited-test-secret";
        await scope.Page.SaveAsync();
        Assert.Equal("edited-test-secret", scope.Service.KeyOf(scope.Page.ConnectionId));
        scope.Page.Leave();
        Assert.False(scope.Page.IsKeyRevealed);
    }

    [AvaloniaFact]
    public async Task FailedTestShowsTheReasonOnTheEditPageAndInTheList()
    {
        using var scope = new Scope(unauthorized: true);
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "rejected-test-key";
        await scope.Page.SaveAsync();
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Page.TestAsync());
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text?.StartsWith("测试失败：服务拒绝访问") == true);
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == scope.Page.Status);
        await scope.Page.OpenAsync();
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "密钥无效或无权限");
    }

    [AvaloniaFact]
    public async Task AddAndEditTestTheCurrentFormWithoutSavingIt()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        var test = FindButton(scope.Page, "测试连接");
        Assert.False(test.IsEnabled);
        scope.Page.Secret.Text = "before-save-key";
        Assert.True(test.IsEnabled);
        test.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await scope.WaitForIdleAsync();
        Assert.Equal("连接正常", scope.Page.Status);
        Assert.Equal(("https://api.deepseek.com/chat/completions", "before-save-key", "deepseek-flash"), Assert.Single(scope.Requests));
        Assert.Empty(scope.Service.Configuration.Connections);
        Assert.False(File.Exists(scope.ConfigPath));
        scope.Window.UpdateLayout();
        TaskToolbarTests.Preview(scope.Window, "model-services-add-tested");
        await scope.Page.SaveAsync();
        Assert.Equal(1, scope.Calls);
        var savedConfig = await File.ReadAllTextAsync(scope.ConfigPath);
        var id = scope.Page.ConnectionId;
        await scope.Page.OpenAsync();
        scope.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "可用");

        await scope.Page.BeginEditAsync(id);
        scope.Page.BaseUrl.Text = "https://gateway.example/v1";
        scope.Page.Secret.Text = "new-draft-key";
        FindButton(scope.Page, "移除模型 deepseek-flash").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(FindButton(scope.Page, "测试连接").IsEnabled);
        scope.Page.AddModel("replacement");
        await scope.Page.TestAsync();
        Assert.Equal("连接正常", scope.Page.Status);
        Assert.Equal(("https://gateway.example/v1/chat/completions", "new-draft-key", "replacement"), scope.Requests.Last());
        Assert.Equal("before-save-key", scope.Service.KeyOf(id));
        Assert.Equal(savedConfig, await File.ReadAllTextAsync(scope.ConfigPath));
        scope.Page.Secret.Text = "another-draft-key";
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "连接正常");
        await scope.Page.SaveAsync();
        Assert.Equal(2, scope.Calls);
        Assert.Equal("another-draft-key", scope.Service.KeyOf(id));
        Assert.Equal(["replacement"], scope.Service.Configuration.Connections.Single().Models);
    }

    [AvaloniaFact]
    public async Task DiscardingAFailedDraftDoesNotChangeTheSavedTestResult()
    {
        using var scope = new Scope();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "working-key";
        await scope.Page.TestAsync();
        await scope.Page.SaveAsync();
        scope.Page.Secret.Text = "rejected-test-key";
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Page.TestAsync());
        Assert.Equal("working-key", scope.Service.KeyOf(scope.Page.ConnectionId));
        scope.Page.Leave();
        scope.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "可用");
        Assert.DoesNotContain(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "密钥无效或无权限");
    }

    [AvaloniaTheory]
    [InlineData("返回连接列表", false)]
    [InlineData("返回连接列表", true)]
    [InlineData("取消", false)]
    [InlineData("取消", true)]
    public async Task LeavingADirtyConnectionReturnsToListWithoutAPrompt(string action, bool editingSavedConnection)
    {
        using var scope = new Scope();
        await scope.Page.BeginAddAsync();
        var savedKey = "saved-test-key";
        string? savedConfig = null;
        if (editingSavedConnection)
        {
            scope.Page.Secret.Text = savedKey;
            var id = scope.Page.ConnectionId;
            await scope.Page.SaveAsync();
            savedConfig = await File.ReadAllTextAsync(scope.ConfigPath);
            await scope.Page.BeginEditAsync(id);
        }

        scope.Page.BaseUrl.Text = "https://unsaved.example/v1";
        scope.Page.Secret.Text = "unsaved-test-key";
        FindButton(scope.Page, action).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        scope.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(scope.Page.IsEditing);
        Assert.Empty(scope.Window.OwnedWindows);
        Assert.Equal("", scope.Page.Secret.Text);
        Assert.True(FindButton(scope.Page, "添加连接").IsEffectivelyVisible);
        Assert.Equal(0, scope.Calls);
        if (savedConfig is not null)
        {
            Assert.Equal(savedConfig, await File.ReadAllTextAsync(scope.ConfigPath));
            Assert.Equal(savedKey, scope.Service.KeyOf(scope.Service.Configuration.Connections.Single().Id));
        }
        else
        {
            Assert.False(File.Exists(scope.ConfigPath));
            Assert.Empty(scope.Service.Configuration.Connections);
        }
        if (action == "返回连接列表" && !editingSavedConnection)
            TaskToolbarTests.Preview(scope.Window, "model-connection-return");
    }

    [AvaloniaFact]
    public async Task NewPresetCanFetchAndTestAModelBeforeItsFirstSave()
    {
        using var scope = new Scope();
        await scope.Page.BeginAddAsync();
        scope.Page.SelectPreset("OpenAI");
        scope.Page.Secret.Text = "new-catalog-key";
        Assert.False(FindButton(scope.Page, "测试连接").IsEnabled);
        Assert.True(FindButton(scope.Page, "获取模型").IsEnabled);
        var fetching = scope.Page.FetchModelsAsync();
        var picker = Assert.IsType<ModelPickerWindow>(Assert.Single(scope.Window.OwnedWindows));
        Assert.Single(picker.GetVisualDescendants().OfType<CheckBox>(), checkbox => AutomationProperties.GetName(checkbox) == "openai/gpt-5").IsChecked = true;
        ClickPickerButton(picker, "更新列表");
        await fetching;
        Assert.Equal(("https://api.openai.com/v1/models", "new-catalog-key", null), Assert.Single(scope.Requests));
        Assert.True(FindButton(scope.Page, "测试连接").IsEnabled);
        await scope.Page.TestAsync();
        Assert.Equal("openai/gpt-5", scope.Requests.Last().Model);
        Assert.False(File.Exists(scope.ConfigPath));
    }

    [AvaloniaFact]
    public async Task DownloadedModelsCanBeFilteredAndOnlyTheConfirmedSelectionIsSaved()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "page-test-secret";
        scope.Page.AddModel("custom-model");
        await scope.Page.SaveAsync();

        var fetching = scope.Page.FetchModelsAsync();
        var picker = Assert.IsType<ModelPickerWindow>(Assert.Single(scope.Window.OwnedWindows));
        var search = Assert.Single(picker.GetVisualDescendants().OfType<TextBox>());
        var choices = picker.GetVisualDescendants().OfType<CheckBox>()
            .ToDictionary(box => AutomationProperties.GetName(box)!);
        Assert.True(choices["deepseek-flash"].IsChecked);
        Assert.True(choices["custom-model"].IsChecked);
        Assert.False(choices["zenmux/catalog-100"].IsChecked);
        choices["deepseek-flash"].IsChecked = false;

        search.Text = "CATALOG-100";
        Dispatcher.UIThread.RunJobs();
        Assert.False(choices["custom-model"].IsVisible);
        Assert.True(choices["zenmux/catalog-100"].IsVisible);
        ClickPickerButton(picker, "全选结果");
        Assert.True(choices["zenmux/catalog-100"].IsChecked);
        Assert.True(choices["custom-model"].IsChecked);
        Assert.False(choices["zenmux/catalog-099"].IsChecked);

        ClickPickerButton(picker, "更新列表");
        await fetching;
        Assert.Equal("模型列表待保存", scope.Page.Status);
        Assert.Equal(new[] { "custom-model", "zenmux/catalog-100" }, scope.Page.Models);
        Assert.Equal(new[] { "deepseek-flash", "custom-model" }, scope.Service.Configuration.Connections.Single().Models);
        await scope.Page.SaveAsync();
        Assert.Equal(new[] { "custom-model", "zenmux/catalog-100" }, scope.Service.Configuration.Connections.Single().Models);
        Assert.Equal("custom-model", scope.Service.Configuration.ModelOrder[0].ModelId);
    }

    [AvaloniaFact]
    public async Task CancellingModelSelectionKeepsTheSavedConnectionAndOrder()
    {
        using var scope = new Scope();
        await scope.Page.OpenAsync();
        await scope.Page.BeginAddAsync();
        scope.Page.Secret.Text = "page-test-secret";
        await scope.Page.SaveAsync();

        var fetching = scope.Page.FetchModelsAsync();
        var picker = Assert.IsType<ModelPickerWindow>(Assert.Single(scope.Window.OwnedWindows));
        picker.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        picker.UpdateLayout();
        TaskToolbarTests.Preview(picker, "model-picker");
        ClickPickerButton(picker, "全选结果");
        Assert.False(Assert.Single(picker.GetVisualDescendants().OfType<Button>(),
            button => button.Content as string == "更新列表").IsEnabled);
        ClickPickerButton(picker, "取消全选");
        ClickPickerButton(picker, "取消");
        await fetching;

        Assert.Equal("deepseek-flash", Assert.Single(scope.Page.Models));
        Assert.Equal("deepseek-flash", scope.Service.Configuration.ModelOrder[0].ModelId);
        Assert.Equal("已保存", scope.Page.Status);
    }

    private static void ClickPickerButton(Window picker, string label) =>
        Assert.Single(picker.GetVisualDescendants().OfType<Button>(), button => button.Content as string == label)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task ListShowsTheSavedConnectionAndModels()
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
    public async Task DraggingAModelAcrossConnectionsSavesTheOrderUsedByTranslation()
    {
        using var scope = new Scope();
        var first = new ModelConnection(Guid.NewGuid(), "DeepSeek", "https://api.deepseek.com", ["deepseek-flash", "deepseek-v4-pro"]);
        var second = new ModelConnection(Guid.NewGuid(), "OpenAI", "https://api.openai.com/v1", ["gpt-5-mini"]);
        await scope.Service.SaveConnectionAsync(first, "fixture-key");
        await scope.Service.SaveConnectionAsync(second, "fixture-key");
        await scope.Page.OpenAsync();
        scope.Page.Margin = new Thickness(24);
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        var handles = scope.Page.GetVisualDescendants().OfType<Control>().Where(control =>
            AutomationProperties.GetName(control)?.StartsWith("调整 ", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(3, handles.Length);
        var start = handles[2].TranslatePoint(new Point(14, 15), scope.Window)!.Value;
        var finish = handles[0].TranslatePoint(new Point(14, 4), scope.Window)!.Value;
        scope.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseMove(finish, RawInputModifiers.LeftMouseButton);
        scope.Window.MouseUp(finish, MouseButton.Left, RawInputModifiers.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (scope.Service.Configuration.ModelOrder[0].ConnectionId != second.Id)
            await Task.Delay(10, deadline.Token);
        Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        TaskToolbarTests.Preview(scope.Window, "model-services-order");

        using var reloaded = new ModelService(scope.ConfigPath);
        await reloaded.InitializeAsync();
        using var translation = new TranslationPage(reloaded, () => Task.FromResult(reloaded.Configuration), () => { });
        await translation.OpenAsync();
        Assert.Equal("gpt-5-mini", translation.Model.SelectedItem?.ToString());
        Assert.Equal(0, scope.Calls);
        translation.Margin = new Thickness(24);
        scope.Window.Content = translation;
        Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        TaskToolbarTests.Preview(scope.Window, "translation-model-order");
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
        Assert.Contains(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "手动添加");
        scope.Page.ConnectionName.Text = "自定义中转";
        scope.Page.BaseUrl.Text = "https://gateway.example/v1";
        scope.Page.Secret.Text = "custom-test-key";
        scope.Page.AddModel("custom-model");
        await scope.Page.SaveAsync();
        scope.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); scope.Window.UpdateLayout();
        Assert.Equal("自定义中转", scope.Service.Configuration.Connections.Single().Name);
        Assert.Equal(new[] { "custom-model" }, scope.Service.Configuration.Connections.Single().Models);
        Assert.Equal("custom-test-key", scope.Service.KeyOf(scope.Page.ConnectionId));
        Assert.True(scope.Page.Secret.IsEffectivelyVisible);
        Assert.True(scope.Page.Secret.Bounds.Height > 0, "key bounds: " + scope.Page.Secret.Bounds);
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
        var title = Assert.Single(scope.Page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "添加连接");
        var header = title.FindAncestorOfType<Grid>()!;
        var presetTab = Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "预设服务商");
        var tabs = presetTab.GetVisualAncestors().OfType<Border>().First(border => border.Child is StackPanel);
        var headerOrigin = header.TranslatePoint(default, scope.Page)!.Value;
        var tabsOrigin = tabs.TranslatePoint(default, scope.Page)!.Value;
        Assert.Equal(16, tabsOrigin.Y - (headerOrigin.Y + header.Bounds.Height), precision: 2);
        Assert.Equal(headerOrigin.X, tabsOrigin.X, precision: 2);
        Assert.Equal(tabs.Child!.Bounds.Width + tabs.Padding.Left + tabs.Padding.Right, tabs.Bounds.Width, precision: 2);
        Assert.True(tabs.Bounds.Width < header.Bounds.Width);

        var actions = new[] { "获取模型", "手动添加", "测试连接" }.Select(text => FindButton(scope.Page, text)).ToArray();
        Assert.All(actions, action => Assert.Contains("link", action.Classes));
        Assert.All(actions, action =>
        {
            var origin = action.TranslatePoint(default, scope.Page)!.Value;
            Assert.True(origin.X >= 0 && origin.X + action.Bounds.Width <= scope.Page.Bounds.Width);
        });
        var remove = FindButton(scope.Page, "移除模型 deepseek-flash");
        var circle = Assert.IsType<Border>(remove.Content);
        Assert.IsType<LineIcon>(circle.Child);
        Assert.Equal(circle.Width / 2, circle.CornerRadius.TopLeft);
        Assert.NotNull(circle.Background);
        foreach (var field in scope.Page.GetVisualDescendants().OfType<TextBox>().Where(control => control.IsEffectivelyVisible))
            Assert.True(field.Bounds.Width <= scope.Page.Bounds.Width, $"{field.Bounds} / {scope.Page.Bounds}");
        Assert.Single(scope.Page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.Classes.Contains("accent"));
        TaskToolbarTests.Preview(scope.Window, $"model-services-{width}");
    }

    private static Button FindButton(ModelServicesPage page, string name)
    {
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(page.GetVisualDescendants().OfType<Button>(),
            button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == name);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbox-model-page-" + Guid.NewGuid().ToString("N"));
        public ModelServiceTests.Credentials Credentials { get; } = new();
        public ModelService Service { get; }
        public ModelServicesPage Page { get; }
        public Window Window { get; } = new();
        public List<(string Endpoint, string? Key, string? Model)> Requests { get; } = [];
        public int Calls => Requests.Count;
        public string ConfigPath => Path.Combine(_directory, "models.json");
        public Scope(bool unauthorized = false)
        {
            Directory.CreateDirectory(_directory);
            Service = new(ConfigPath, Credentials, new ModelServiceTests.Handler((request, ct) =>
            {
                string? model = null;
                if (request.Content is { } content)
                {
                    using var json = System.Text.Json.JsonDocument.Parse(content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
                    model = json.RootElement.GetProperty("model").GetString();
                }
                var key = request.Headers.Authorization?.Parameter;
                Requests.Add((request.RequestUri!.AbsoluteUri, key, model));
                if (unauthorized || key == "rejected-test-key") return Task.FromResult(ModelServiceTests.Response("{}", "application/json", System.Net.HttpStatusCode.Unauthorized));
                if (request.Method == HttpMethod.Get)
                {
                    var sample = new[] { "deepseek-flash", "openai/gpt-5", "anthropic/claude-sonnet-4.6",
                        "google/gemini-2.5-flash", "deepseek/deepseek-v3.2", "qwen/qwen3.7-flash" };
                    var catalog = sample.Concat(Enumerable.Range(0, 101).Select(index => $"zenmux/catalog-{index:D3}"));
                    var json = System.Text.Json.JsonSerializer.Serialize(new { data = catalog.Select(id => new { id }) });
                    return Task.FromResult(ModelServiceTests.Response(json, "application/json"));
                }
                return Task.FromResult(ModelServiceTests.Response("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
            }));
            Page = new(Service, () => Window);
            Ui.Configure(Window, "模型服务测试", 800, 600);
            Window.Content = new ScrollViewer { Content = Page, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            Window.Show(); Window.UpdateLayout();
        }
        public async Task WaitForIdleAsync()
        {
            for (var attempt = 0; Page.IsBusy && attempt < 50; attempt++) await Task.Delay(10);
            Assert.False(Page.IsBusy);
        }

        public void Dispose() { Page.Dispose(); Window.Close(); Service.Dispose(); Directory.Delete(_directory, true); }
    }
}
