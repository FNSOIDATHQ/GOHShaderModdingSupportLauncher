using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private IModTextTranslator textTranslator = new GoogleModTextTranslator();
    private ModDocumentTranslator? documentTranslator;
    private ModDocumentTranslator DocumentTranslator => documentTranslator ??= new(textTranslator);
    private ModTranslationSettings translationPreferences = new();
    private readonly SemaphoreSlim translationSettingsGate = new(1, 1);
    private CheckBox translateToggle = null!;
    private ComboBox translationLanguage = null!, translationDisplay = null!;
    private StackPanel translationToolbar = null!;
    private Grid subscriptionHeader = null!;
    private CancellationTokenSource? translationCancellation;
    private int translationGeneration;
    private string? translationContext, translationSource;
    private bool updatingTranslationControls;
    private int translationPreferenceRevision;
    private readonly Dictionary<string, ModDocumentTranslation?> translationViews = [];

    private string TargetLanguage => translationPreferences.Language.Length > 0 ? translationPreferences.Language
        : LocalizedStrings.Instance.Culture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";

    private Control BuildTranslationToolbar()
    {
        translationToolbar = Column(); translationToolbar.Spacing = 2;
        translationToolbar.Name = "subscriptionTranslation";
        translationToolbar.HorizontalAlignment = HorizontalAlignment.Center;
        var controls = Row(); controls.HorizontalAlignment = HorizontalAlignment.Center;
        translateToggle = new CheckBox { Name = "subscriptionTranslate", Content = T("AutoTranslate"), VerticalAlignment = VerticalAlignment.Center };
        translationLanguage = new ComboBox { Name = "subscriptionTranslationLanguage", Width = 138,
            ItemsSource = new[] { T("TranslationFollowUI"), "English", "简体中文", "繁體中文", "日本語", "한국어", "Русский", "Deutsch", "Français", "Español", "Português", "Українська", "Polski" } };
        translationDisplay = new ComboBox { Name = "subscriptionTranslationDisplay", Width = 116, ItemsSource = new[] { T("TranslatedText"), T("OriginalText") } };
        controls.Children.Add(translateToggle); controls.Children.Add(translationLanguage); controls.Children.Add(translationDisplay);
        translationToolbar.Children.Add(controls);
        ToolTip.SetTip(translationToolbar, T("TranslationHint"));
        ApplyTranslationControls();
        translateToggle.IsCheckedChanged += async (_, _) => await TranslationPreferencesChangedAsync();
        translationLanguage.SelectionChanged += async (_, _) => await TranslationPreferencesChangedAsync();
        translationDisplay.SelectionChanged += async (_, _) => await TranslationPreferencesChangedAsync();
        return translationToolbar;
    }

    private void ApplyTranslationControls()
    {
        updatingTranslationControls = true;
        translateToggle.IsChecked = translationPreferences.Enabled;
        translationLanguage.SelectedIndex = Array.IndexOf(ModTranslationSettings.Languages, translationPreferences.Language) + 1;
        translationDisplay.SelectedIndex = translationPreferences.ShowOriginal ? 1 : 0;
        translationDisplay.IsEnabled = translationPreferences.Enabled;
        updatingTranslationControls = false;
    }

    private async Task LoadTranslationOptionsAsync()
    {
        int revision = translationPreferenceRevision;
        ModTranslationSettings value;
        try { value = await Task.Run(() => runtime!.TranslationSettings.Load(), lifetime.Token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { value = new(); if (revision == translationPreferenceRevision) ReportNotice(T("TranslationSettingsInvalid"), attention: true, key: "translationSettings"); }
        if (disposed || revision != translationPreferenceRevision) return;
        translationPreferences = value;
        ApplyTranslationControls();
    }

    private async Task TranslationPreferencesChangedAsync()
    {
        if (updatingTranslationControls || rebuilding || disposed) return;
        translationPreferenceRevision++;
        SaveReadingPosition();
        var value = new ModTranslationSettings { Enabled = translateToggle.IsChecked == true,
            Language = translationLanguage.SelectedIndex > 0 ? ModTranslationSettings.Languages[translationLanguage.SelectedIndex - 1] : "",
            ShowOriginal = translationDisplay.SelectedIndex == 1 };
        translationPreferences = value;
        ApplyTranslationControls(); CancelTranslation(); RerenderTranslation();
        if (runtime is null) return;
        try
        {
            await runtime.Ready.WaitAsync(lifetime.Token);
            await translationSettingsGate.WaitAsync(lifetime.Token);
            try { await Task.Run(() => runtime.TranslationSettings.Save(value), lifetime.Token); }
            finally { translationSettingsGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!disposed) ReportNotice(T("TranslationSettingsFailed"), attention: true, key: "translationSettings"); }
    }

    private void CancelTranslation()
    {
        translationGeneration++;
        translationCancellation?.Cancel(); translationCancellation?.Dispose(); translationCancellation = null;
        foreach (string key in translationViews.Keys) { pageStatus.Remove("translation:" + key); statusRetries.Remove("translation:" + key); }
        translationViews.Clear(); translationContext = null; translationSource = null;
        RenderStatus();
    }

    private void PrepareTranslationSource(params ModReadingDocument[] documents)
    {
        if (!translationPreferences.Enabled || translationPreferences.ShowOriginal) return;
        string key = string.Join("\n", documents.Select(document => DocumentTranslator.Key(document, TargetLanguage)));
        if (translationSource is not null && translationSource != key) CancelTranslation();
        translationSource = key;
    }

    private ModReadingDocument DisplayDocument(StackPanel panel, ModReadingDocument original)
    {
        bool visible = details.SelectedIndex == 0 ? panel == reading || panel == summary : panel == fileReader;
        if (!visible || !translationPreferences.Enabled || translationPreferences.ShowOriginal || session is null || operation.IsVisible || !detail.IsVisible) return original;
        string context = string.Join("\n", main.SubscriptionGameDirectory, session.Mod.Id, details.SelectedIndex,
            details.SelectedIndex == 0 ? session.Article?.AbsoluteUri ?? "overview" : session.File, TargetLanguage);
        if (context != translationContext)
        {
            string? sourceKey = translationSource;
            CancelTranslation(); translationContext = context; translationSource = sourceKey;
            translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(DetailToken);
        }
        string key = DocumentTranslator.Key(original, TargetLanguage);
        if (DocumentTranslator.TryGet(key, out var cached)) return cached!.Document;
        if (translationViews.TryGetValue(key, out var view)) return view?.Document ?? original;
        translationViews.Add(key, null);
        _ = TranslateDocumentAsync(key, original, session, TargetLanguage, translationGeneration, translationCancellation!.Token);
        return original;
    }

    private async Task TranslateDocumentAsync(string key, ModReadingDocument original, ReadingSession expected, string language, int generation, CancellationToken token)
    {
        // Finish the current visual-tree render before even an immediately completed provider can repaint it.
        await Task.Yield();
        if (generation != translationGeneration || !IsCurrent(expected, token)) return;
        var operation = BeginStatus("translation:" + key, "Translation", T("TranslationDocument"));
        bool finished = false;
        bool Current() => generation == translationGeneration && IsCurrent(expected, token);
        try
        {
            var progress = new Progress<ModTranslationProgress>(value =>
            {
                if (!Current() || finished || value.Total == 0) return;
                if (pageStatus.Update(operation, "Translation", string.Format(T("TranslationProgress"), value.Completed, value.Total), ModPageOperationState.Running)) RenderStatus();
            });
            var result = await Task.Run(() => DocumentTranslator.TranslateAsync(original, language, progress, token), token);
            if (!Current()) return;
            finished = true;
            translationViews[key] = result;
            EndStatus(operation, result.FailedParts > 0 ? ModPageOperationState.Warning : ModPageOperationState.Succeeded,
                result.FailedParts > 0 ? TranslationFailure(result.Error, result.RetryAfter, result.FailedParts) : T("TranslationCompleted"),
                retry: result.FailedParts > 0 ? RetryTranslationAsync : null);
            RerenderTranslation();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { EndStatus(operation, ModPageOperationState.Cancelled); }
        catch (Exception ex)
        {
            if (!Current()) return;
            var failure = ex as ModTranslationException;
            translationViews[key] = new(original, 1, failure?.Error ?? ModTranslationError.Unavailable, failure?.RetryAfter);
            EndStatus(operation, ModPageOperationState.Warning, TranslationFailure(failure?.Error, failure?.RetryAfter, 1), retry: RetryTranslationAsync);
        }
        finally { finished = true; }
    }

    private static string TranslationFailure(ModTranslationError? error, DateTimeOffset? retryAfter, int count) =>
        string.Format(T("TranslationFallback"), count) + " · " + T("TranslationError_" + (error ?? ModTranslationError.Unavailable)) +
        (retryAfter is { } retry ? " · " + T("RetryAfter") + ": " + retry.ToLocalTime().ToString("T") : "");

    private void RerenderTranslation()
    {
        if (disposed || session is null || operationBusy || operation.IsVisible) return;
        if (details.SelectedIndex == 0) { renderedBody = null; RenderReading(); }
        else { SaveFileReadingPosition(); RenderSelectedFile(); }
    }

    private void PrepareTranslationRetry()
    {
        var failed = translationViews.Where(pair => pair.Value is { FailedParts: > 0 }).Select(pair => pair.Key).ToArray();
        foreach (string key in failed)
        { translationViews.Remove(key); pageStatus.Remove("translation:" + key); statusRetries.Remove("translation:" + key); }
        if (failed.Length > 0) RerenderTranslation();
    }

    private Task RetryTranslationAsync()
    {
        if (operationBusy || operation.IsVisible) return Task.CompletedTask;
        PrepareTranslationRetry(); return Task.CompletedTask;
    }
}
