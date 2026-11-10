using Tbox.Core.ModelServices;
using Tbox.Core.Translation;

namespace Tbox.Desktop.Translation;

internal enum TranslationState { Idle, Running, Completed, Partial, Stopped, Failed }

// Owns only translation behavior and task state. Credentials, protocol, retries and networking belong to the public capability.
internal sealed class TranslationSession(ITextModelService models) : IDisposable
{
    private CancellationTokenSource? _current;
    private bool _disposed;
    public long Revision { get; private set; }
    public TranslationState State { get; private set; }
    public string Text { get; private set; } = "";
    public string Status { get; private set; } = "输入文字后点击翻译，不会自动发送。";
    public ModelUsage? Usage { get; private set; }
    public bool IsRunning => State == TranslationState.Running;
    public event Action? Changed;

    public async Task TranslateAsync(string source, string languageId, ModelReference? model = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = TranslationPolicy.CreateRequest(source, languageId, model);
        _current?.Cancel();
        var operation = _current = new CancellationTokenSource();
        var revision = ++Revision;
        Text = ""; Usage = null; State = TranslationState.Running; Status = "翻译中…可停止生成。"; Notify();
        try
        {
            string? finish = null;
            await foreach (var item in models.GenerateAsync(request, operation.Token))
            {
                if (!Current(revision, operation)) return;
                if (Text.Length + item.Text.Length > 100_000) throw new ModelServiceException(ModelFailure.Response, "译文超过显示上限，已停止接收。");
                if (finish is not null && item.Text.Length > 0) throw new ModelServiceException(ModelFailure.Response, "结束标记后仍收到内容，响应无效。");
                Text += item.Text;
                Usage = item.Usage ?? Usage;
                finish = item.FinishReason ?? finish;
                Notify();
            }
            if (!Current(revision, operation)) return;
            State = finish == "stop" && !string.IsNullOrWhiteSpace(Text) ? TranslationState.Completed
                : Text.Length > 0 ? TranslationState.Partial : TranslationState.Failed;
            Status = State == TranslationState.Completed ? "翻译完成" + UsageLabel()
                : finish == "length" ? "达到输出上限，译文不完整。请缩短原文后手动重试。"
                : string.IsNullOrWhiteSpace(Text) ? "未收到有效译文，请检查模型后手动重试。"
                : "响应未正常完成，以下仅为部分译文。";
        }
        catch (OperationCanceledException)
        {
            if (!Current(revision, operation)) return;
            State = TranslationState.Stopped; Status = "生成已停止，已收到的译文可能不完整。";
        }
        catch (Exception error)
        {
            if (!Current(revision, operation)) return;
            State = Text.Length > 0 ? TranslationState.Partial : TranslationState.Failed;
            var message = error is ModelServiceException ? error.Message : "翻译未完成，请检查模型连接后重试。";
            Status = message + (Text.Length > 0 ? " 已保留部分译文。" : " 原文已保留。") + "不会自动重试。";
        }
        finally
        {
            if (ReferenceEquals(_current, operation)) _current = null;
            operation.Dispose();
            if (Revision == revision && !_disposed) Notify();
        }
    }

    public void Invalidate()
    {
        ++Revision; _current?.Cancel(); _current = null;
        Text = ""; Usage = null; State = TranslationState.Idle;
        Status = "内容或选项已更改，请点击翻译；不会自动发送。"; Notify();
    }
    public void Stop()
    {
        if (!IsRunning) return;
        ++Revision; _current?.Cancel(); _current = null;
        State = TranslationState.Stopped;
        Status = Text.Length == 0 ? "已停止，尚未收到译文。" : "已停止 · 以下仅为部分译文。";
        Notify();
    }
    private bool Current(long revision, CancellationTokenSource operation) => !_disposed && Revision == revision && ReferenceEquals(_current, operation);
    private string UsageLabel() => Usage is null ? " · 用量未返回" : $" · 输入 {Usage.InputTokens?.ToString() ?? "未知"} / 输出 {Usage.OutputTokens?.ToString() ?? "未知"} token";
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose() { Stop(); _disposed = true; ++Revision; Text = ""; Usage = null; Changed = null; }
}
