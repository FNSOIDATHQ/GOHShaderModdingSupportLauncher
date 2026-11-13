namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal enum ModPageOperationState { Running, Succeeded, Warning, Failed, Cancelled }

internal sealed record ModPageScope(string? Game, string Provider, ModSourceId? Mod, string Document);

internal sealed record ModPageOperation(long Id, long Context, string Key, ModPageScope Scope, string Phase,
    string Message, ModPageOperationState State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null);

// UI-thread owned. A completion must still own both its context and its key.
// Independent successes never dismiss another request's failure.
internal sealed class ModPageStatus
{
    private readonly Dictionary<string, ModPageOperation> operations = [];
    private long context, sequence;
    internal IReadOnlyCollection<ModPageOperation> Operations => operations.Values;

    internal void Reset() { context++; operations.Clear(); }

    internal ModPageOperation Begin(string key, ModPageScope scope, string phase, string message = "")
    {
        if (operations.Count >= 128 && !operations.ContainsKey(key))
        {
            var oldest = operations.Values.Where(item => item.State is ModPageOperationState.Succeeded or ModPageOperationState.Cancelled)
                .OrderBy(item => item.Id).FirstOrDefault();
            if (oldest is not null) operations.Remove(oldest.Key);
        }
        var operation = new ModPageOperation(++sequence, context, key, scope, phase, message,
            ModPageOperationState.Running, DateTimeOffset.UtcNow);
        operations[key] = operation;
        return operation;
    }

    internal bool Update(ModPageOperation operation, string phase, string message, ModPageOperationState state)
    {
        if (operation.Context != context || !operations.TryGetValue(operation.Key, out var current) || current.Id != operation.Id) return false;
        operations[operation.Key] = current with { Phase = phase, Message = message, State = state,
            FinishedAt = state == ModPageOperationState.Running ? null : DateTimeOffset.UtcNow };
        return true;
    }

    internal void Remove(string key) => operations.Remove(key);
}
