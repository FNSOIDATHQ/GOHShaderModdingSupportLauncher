namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModVerificationRequest(Uri Origin, DateTimeOffset DetectedAt);

// Ephemeral state only. Verification cookies and browser data never enter the subscription library.
internal sealed class ModVerificationState
{
    private readonly object sync = new();
    private readonly Dictionary<string, ModVerificationRequest> pending = new(StringComparer.OrdinalIgnoreCase);
    // A protected mirror URI may be signed. Keep it private and transient; public state is origin-only.
    private readonly Dictionary<string, (Uri Uri, bool FileProbe)> probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<string[]?> allowedRetries = new();
    internal event Action? Changed;

    internal bool IsPaused { get { lock (sync) return pending.Count > 0; } }

    internal static Uri Origin(Uri uri)
    {
        if (!AnonymousModHttpClient.IsPublicHttps(uri)) throw new ArgumentException("Verification requires a public HTTPS origin.", nameof(uri));
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    internal ModVerificationRequest[] GetPending()
    { lock (sync) return pending.Values.OrderBy(item => item.DetectedAt).ToArray(); }

    internal ModVerificationRequest Pause(Uri uri)
    {
        var origin = Origin(uri);
        ModVerificationRequest result;
        bool changed;
        lock (sync)
        {
            changed = !pending.TryGetValue(origin.AbsoluteUri, out result!);
            if (changed) pending.Add(origin.AbsoluteUri, result = new(origin, DateTimeOffset.UtcNow));
        }
        if (changed) NotifyChanged();
        return result;
    }

    internal bool IsPausedFor(Uri uri)
    { lock (sync) return pending.ContainsKey(Origin(uri).AbsoluteUri); }

    internal void RecordProbe(Uri uri, bool fileProbe)
    { lock (sync) probes.TryAdd(Origin(uri).AbsoluteUri, (uri, fileProbe)); }

    internal bool TryGetProbe(Uri origin, out Uri uri, out bool fileProbe)
    {
        lock (sync)
        {
            if (probes.TryGetValue(Origin(origin).AbsoluteUri, out var probe))
            { uri = probe.Uri; fileProbe = probe.FileProbe; return true; }
            uri = Origin(origin); fileProbe = false; return false;
        }
    }

    internal void ThrowIfPaused(Uri uri)
    {
        var origin = Origin(uri);
        if (allowedRetries.Value?.Contains(origin.AbsoluteUri, StringComparer.OrdinalIgnoreCase) == true) return;
        lock (sync)
        {
            if (!pending.ContainsKey(origin.AbsoluteUri)) return;
        }
        throw ModChallengeDetector.Required(uri);
    }

    // A caller may probe the verified origin without waking unrelated scheduled requests.
    // Returning from browser UI is not proof of success: Clear is deliberately separate.
    internal IDisposable AllowRetry(Uri uri)
    {
        var origin = Origin(uri).AbsoluteUri;
        var previous = allowedRetries.Value;
        allowedRetries.Value = [.. previous ?? [], origin];
        return new RetryLease(() => allowedRetries.Value = previous);
    }

    internal void Clear(Uri uri)
    {
        bool changed;
        lock (sync)
        {
            string origin = Origin(uri).AbsoluteUri;
            changed = pending.Remove(origin);
            probes.Remove(origin);
        }
        if (changed) NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* UI notification cannot change request state. */ }
    }

    private sealed class RetryLease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
