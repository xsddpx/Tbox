using Tbox.Core;

namespace Tbox.Desktop.Platform;

// What the system actually granted; Message explains a refusal (shown in the main window status bar).
public sealed record KeepAwakeResult(KeepAwakeRequest Applied, string? Message = null, bool RequiresLidApproval = false);

// Keep-awake system integration (AWAKE-001–003). Only system power requests are used; input is never simulated.
public interface IKeepAwake : IDisposable
{
    // False on devices without a lid sensor, where the lid option is unavailable.
    bool HasLid { get; }
    // True when changes to the lid setting may need system approval.
    bool LidNeedsAuthorization => false;
    string? RequestLidApproval() => null;
    // Applies the request and reports what is in effect. KeepAwakeRequest.None releases everything,
    // including undoing a lid change left behind by an earlier run that ended abnormally.
    Task<KeepAwakeResult> ApplyAsync(KeepAwakeRequest request);
}
