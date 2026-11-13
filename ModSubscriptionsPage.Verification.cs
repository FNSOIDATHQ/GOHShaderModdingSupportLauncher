using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private Uri? CurrentVerificationOrigin => runtime?.Verification.GetPending().FirstOrDefault()?.Origin;

    private void RefreshVerification()
    {
        if (verificationBanner is null || disposed) return;
        var pending = runtime?.Verification.GetPending().FirstOrDefault();
        var origin = pending?.Origin;
        if (pending?.DetectedAt != verificationRequestAt)
        {
            verificationRequestAt = pending?.DetectedAt;
            if (pending is not null && !verificationBusy) verificationNotice = "VerificationRequired";
        }
        bool supported = origin is null || ModBrowserClearance.IsSupportedOrigin(origin);
        verificationBanner.IsVisible = origin is not null || verificationOpenedFor is not null || verificationBusy;
        string key = !supported && !verificationBusy ? "VerificationUnsupported" :
            verificationNotice ?? (verificationOpenedFor is null ? "VerificationRequired" : "VerificationBrowserOpen");
        verificationMessage.Text = T(key) + (origin is null ? "" : "\n" + origin.Host);
        verificationPrivacy.IsVisible = supported || verificationOpenedFor is not null;
        verificationOpen.IsEnabled = !verificationBusy && origin is not null && supported;
        verificationRetry.IsEnabled = !verificationBusy && origin is not null;
        verificationCancel.IsEnabled = verificationBusy || verificationOpenedFor is not null;
        UpdateOriginal();
    }

    private async Task OpenVerificationAsync()
    {
        if (runtime is null || verificationBusy || CurrentVerificationOrigin is not { } origin) return;
        navigationOriginal = origin; UpdateOriginal();
        if (!ModBrowserClearance.IsSupportedOrigin(origin))
        { verificationNotice = "VerificationUnsupported"; RefreshVerification(); return; }
        verificationBusy = true; verificationNotice = "VerificationOpening";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        verificationCancellation = cancellation;
        RefreshVerification();
        try
        {
            await Task.Run(() => runtime.OpenVerificationAsync(origin, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            verificationOpenedFor = origin;
            verificationNotice = "VerificationBrowserOpen";
        }
        catch (OperationCanceledException) { verificationNotice = "VerificationCancelled"; }
        catch (Exception ex)
        {
            verificationNotice = "VerificationUnavailable";
            AppDiagnostics.Log("ModDB verification browser could not open: " + ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(verificationCancellation, cancellation)) verificationCancellation = null;
            verificationBusy = false; RefreshVerification();
        }
    }

    private async Task CompleteVerificationAsync()
    {
        if (runtime is null || verificationBusy || CurrentVerificationOrigin is not { } origin) return;
        navigationOriginal = origin; UpdateOriginal();
        verificationBusy = true; verificationNotice = "VerificationChecking";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        verificationCancellation = cancellation;
        RefreshVerification();
        try
        {
            bool restored = await Task.Run(() => runtime.CompleteVerificationAsync(origin, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            verificationNotice = restored ? "VerificationPassed" :
                ModBrowserClearance.IsSupportedOrigin(origin) ? "VerificationFailed" : "VerificationUnsupported";
            ReportNotice(T(verificationNotice), failure: !restored, key: "verification");
            if (!restored) return;
            var retry = verificationRetryAction ?? ApplySearchAsync;
            verificationRetryAction = null;
            await actionGate.WaitAsync(cancellation.Token);
            try { await ExecuteAsync(retry); }
            finally { actionGate.Release(); }
        }
        catch (OperationCanceledException) { verificationNotice = "VerificationCancelled"; }
        catch (Exception ex)
        {
            verificationNotice = "VerificationFailed";
            AppDiagnostics.Log("ModDB verification access check failed: " + ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(verificationCancellation, cancellation)) verificationCancellation = null;
            verificationBusy = false; RefreshVerification();
        }
    }

    private async Task CloseVerificationAsync()
    {
        if (runtime is null) return;
        verificationCancellation?.Cancel();
        try
        {
            await Task.Run(() => runtime.CloseVerificationAsync(lifetime.Token), lifetime.Token);
            verificationOpenedFor = null;
            verificationNotice = "VerificationCancelled";
        }
        catch (OperationCanceledException) { verificationNotice = "VerificationCancelled"; }
        catch (Exception ex)
        {
            verificationNotice = "VerificationFailed";
            AppDiagnostics.Log("ModDB verification session could not close: " + ex.GetType().Name);
        }
        finally { RefreshVerification(); }
    }
}
