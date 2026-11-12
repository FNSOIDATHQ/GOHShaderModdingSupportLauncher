using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModVerificationBrowserOptions
{
    internal string? ProfileRoot { get; init; }
    // Only local protocol checks use this; the application always opens a visible browser.
    internal bool HeadlessForTests { get; init; }
}

// This browser owns a fresh profile. It never attaches to a user's normal browser or imports its cookies.
internal sealed class ModVerificationBrowser : IAsyncDisposable
{
    private static readonly Uri VerificationPage = new("https://www.moddb.com/games/gates-of-hell/mods");
    private readonly ModBrowserClearance clearance;
    private readonly Action<string>? diagnostic;
    private readonly ModVerificationBrowserOptions options;
    private readonly string profileRoot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ModBrowserCdpConnection? connection;
    private Process? browser;
    private CancellationTokenSource? errorReaderCancellation;
    private Task? errorReader;
    private string? profile;
    private Uri requestedPage = VerificationPage;
    private bool disposed;

    internal ModVerificationBrowser(ModBrowserClearance clearance, Action<string>? diagnostic = null,
        ModVerificationBrowserOptions? options = null)
    {
        this.clearance = clearance;
        this.diagnostic = diagnostic;
        this.options = options ?? new();
        profileRoot = Path.GetFullPath(this.options.ProfileRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GOHSMSLauncher", "verification"));
    }

    internal bool IsOpen => connection?.IsOpen == true;

    internal async Task OpenAsync(Uri origin, CancellationToken cancellationToken = default)
    {
        if (!ModBrowserClearance.IsSupportedOrigin(origin)) throw new ArgumentException("This source does not support browser verification.", nameof(origin));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsOpen) return;
            await CloseCoreAsync(clearSession: true).ConfigureAwait(false);
            requestedPage = origin.AbsolutePath == "/" && origin.Query.Length == 0 ? VerificationPage : new UriBuilder(origin) { Fragment = "" }.Uri;
            string executable = FindBrowser() ?? throw new FileNotFoundException("Microsoft Edge or Google Chrome is required for verification.");
            ManagedModFiles.EnsureSafe(profileRoot);
            Directory.CreateDirectory(profileRoot);
            profile = Path.Combine(profileRoot, Guid.NewGuid().ToString("N"));
            ManagedModFiles.EnsureSafe(profile);
            Directory.CreateDirectory(profile);
            int debuggerPort;
            using (var reservation = new TcpListener(IPAddress.Loopback, 0))
            {
                reservation.Start();
                debuggerPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
            }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = this.options.HeadlessForTests,
                WindowStyle = this.options.HeadlessForTests ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                "--user-data-dir=" + profile, "--remote-debugging-port=" + debuggerPort, "--remote-debugging-address=127.0.0.1",
                "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-extensions", "--new-window"
            }) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(executable.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase) ? "--inprivate" : "--incognito");
            if (this.options.HeadlessForTests) start.ArgumentList.Add("--headless=new");
            start.ArgumentList.Add("about:blank");
            browser = Process.Start(start) ?? throw new IOException("The verification browser did not start.");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            errorReaderCancellation = new();
            var endpointReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            errorReader = ReadErrorOutputAsync(browser.StandardError, debuggerPort, endpointReady, errorReaderCancellation.Token);
            var ownedEndpoint = await endpointReady.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            connection = await ConnectAsync(ownedEndpoint, startup.Token).ConfigureAwait(false);
            await DenyDownloadsAsync(connection, startup.Token).ConfigureAwait(false);
            await connection.CallAsync("Page.navigate", writer => writer.WriteString("url", requestedPage.AbsoluteUri), startup.Token).ConfigureAwait(false);
            Log("Verification browser opened with an isolated profile.");
        }
        catch
        {
            await CloseCoreAsync(clearSession: true).ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    internal async Task<bool> CaptureAsync(Uri origin, CancellationToken cancellationToken = default)
    {
        if (!ModBrowserClearance.IsSupportedOrigin(origin)) return false;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is not { IsOpen: true } cdp) return false;
            var version = await cdp.CallAsync("Browser.getVersion", null, cancellationToken).ConfigureAwait(false);
            string? userAgent = version.TryGetProperty("userAgent", out var agent) ? agent.GetString() : null;
            if (string.IsNullOrEmpty(userAgent)) return false;
            var result = await cdp.CallAsync("Network.getCookies", writer =>
            {
                writer.WritePropertyName("urls"); writer.WriteStartArray(); writer.WriteStringValue(requestedPage.AbsoluteUri); writer.WriteEndArray();
            }, cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return false;
            foreach (var cookie in cookies.EnumerateArray())
            {
                if (!cookie.TryGetProperty("name", out var name) || name.GetString() != "cf_clearance") continue;
                string? value = String(cookie, "value"), domain = String(cookie, "domain"), path = String(cookie, "path");
                bool secure = cookie.TryGetProperty("secure", out var secureValue) && secureValue.ValueKind == JsonValueKind.True;
                DateTimeOffset? expires = null;
                if (cookie.TryGetProperty("expires", out var expiration) && expiration.TryGetDouble(out double seconds) && seconds > 0)
                {
                    if (!double.IsFinite(seconds) || seconds > 253402300799) continue;
                    expires = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                }
                if (value is not null && domain is not null && path is not null &&
                    clearance.Set(origin, userAgent, value, domain, path, secure, expires))
                {
                    Log("Anonymous verification proof captured; access still requires a successful source request.");
                    return true;
                }
            }
            Log("The isolated browser has no valid anonymous verification proof.");
            return false;
        }
        finally { gate.Release(); }
    }

    internal async Task CloseAsync(bool clearSession = true, CancellationToken cancellationToken = default)
    {
        if (clearSession) clearance.Clear();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CloseCoreAsync(clearSession).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task CloseCoreAsync(bool clearSession)
    {
        if (clearSession) clearance.Clear();
        var previous = connection;
        connection = null;
        if (previous is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                if (previous.IsOpen) await previous.CallAsync("Browser.close", null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Log("Verification browser close command: " + ex.GetType().Name); }
            finally { await previous.DisposeAsync().ConfigureAwait(false); }
        }
        if (browser is { } owned)
        {
            browser = null;
            try
            {
                if (!owned.HasExited)
                {
                    try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        // This PID was started with the unique profile above. Never enumerate/kill by browser name.
                        owned.Kill(entireProcessTree: true);
                        await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) { Log("Verification browser cleanup: " + ex.GetType().Name); }
            finally
            {
                await StopErrorReaderAsync().ConfigureAwait(false);
                owned.Dispose();
            }
        }
        else await StopErrorReaderAsync().ConfigureAwait(false);
        string? previousProfile = profile;
        profile = null;
        if (previousProfile is not null)
        {
            try
            {
                // Chromium's utility processes can release profile files shortly after Browser.close.
                // Retry only our exact profile; never broaden cleanup to other browser data.
                for (int attempt = 0; ; attempt++)
                {
                    try { await Task.Run(() => DeleteOwnedProfile(previousProfile)).ConfigureAwait(false); break; }
                    catch (IOException) when (attempt < 8) { await Task.Delay(250).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) when (attempt < 8) { await Task.Delay(250).ConfigureAwait(false); }
                }
            }
            catch (Exception ex) { Log("Isolated verification profile retained after cleanup failure: " + ex.GetType().Name); }
        }
    }

    private static Uri? ParseOwnedEndpoint(string line, int expectedPort)
    {
        const string prefix = "DevTools listening on ";
        string trimmed = line.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal) ||
            !Uri.TryCreate(trimmed[prefix.Length..], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "ws" ||
            endpoint.Port != expectedPort || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.Host is not ("127.0.0.1" or "localhost") ||
            !endpoint.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal) ||
            !Guid.TryParseExact(endpoint.AbsolutePath["/devtools/browser/".Length..], "D", out _)) return null;
        return new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;
    }

    private static async Task ReadErrorOutputAsync(StreamReader reader, int expectedPort,
        TaskCompletionSource<Uri> endpointReady, CancellationToken token)
    {
        // Drain all output without retaining it. Large lines are skipped, never logged or stored.
        var line = new StringBuilder(256);
        var buffer = new char[1024];
        bool oversized = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char character = buffer[i];
                    if (character == '\n')
                    {
                        if (!oversized && ParseOwnedEndpoint(line.ToString(), expectedPort) is { } endpoint) endpointReady.TrySetResult(endpoint);
                        line.Clear(); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length >= 8192) { line.Clear(); oversized = true; }
                        else line.Append(character);
                    }
                }
            }
            endpointReady.TrySetException(new IOException("The verification browser did not provide its own debugger endpoint."));
        }
        catch (OperationCanceledException) { endpointReady.TrySetCanceled(token); }
        catch (Exception) { endpointReady.TrySetException(new IOException("The verification browser endpoint could not be read.")); }
    }

    private async Task StopErrorReaderAsync()
    {
        var cancellation = errorReaderCancellation;
        var pending = errorReader;
        errorReaderCancellation = null; errorReader = null;
        try
        {
            cancellation?.Cancel();
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally { cancellation?.Dispose(); }
    }

    private async Task<ModBrowserCdpConnection> ConnectAsync(Uri ownedEndpoint, CancellationToken token)
    {
        int port = ownedEndpoint.Port;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(5) };
        using (var version = await ReadEndpointJsonAsync(client, port, "version", token).ConfigureAwait(false))
        {
            string? endpointText = String(version.RootElement, "webSocketDebuggerUrl");
            if (endpointText is null || ParseOwnedEndpoint("DevTools listening on " + endpointText, port) is not { } endpoint ||
                endpoint != ownedEndpoint)
                throw new InvalidDataException("The verification browser endpoint identity did not match its own process.");
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var targets = await ReadEndpointJsonAsync(client, port, "list", token).ConfigureAwait(false);
            if (targets.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The verification browser target list is invalid.");
            foreach (var target in targets.RootElement.EnumerateArray())
            {
                if (String(target, "type") != "page" || String(target, "id") is not { } id || id.Length is < 8 or > 128 ||
                    !id.All(char.IsAsciiLetterOrDigit)) continue;
                string? address = String(target, "url");
                if (address != "about:blank" && (!Uri.TryCreate(address, UriKind.Absolute, out var page) ||
                    !ModBrowserClearance.IsSupportedOrigin(page))) continue;
                if (!Uri.TryCreate(String(target, "webSocketDebuggerUrl"), UriKind.Absolute, out var socket) ||
                    socket.Scheme != "ws" || socket.Port != port || socket.UserInfo.Length != 0 || socket.Query.Length != 0 || socket.Fragment.Length != 0 ||
                    socket.Host is not ("127.0.0.1" or "localhost") || socket.AbsolutePath != "/devtools/page/" + id)
                    throw new InvalidDataException("The verification browser socket is invalid.");
                var endpoint = new UriBuilder(socket) { Host = "127.0.0.1" }.Uri;
                return await ModBrowserCdpConnection.ConnectAsync(endpoint, token).ConfigureAwait(false);
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private static async Task<JsonDocument> ReadEndpointJsonAsync(HttpClient client, int port, string resource, CancellationToken token)
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/{resource}", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("The verification browser endpoint is unavailable.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream();
        byte[] buffer = new byte[16 * 1024]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (data.Length + count > ModBrowserCdpConnection.MaxMessageBytes) throw new InvalidDataException("The verification browser endpoint exceeded its size limit.");
            data.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(data.ToArray());
    }

    private void DeleteOwnedProfile(string directory)
    {
        string absolute = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(absolute), profileRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(absolute), "N", out _))
            throw new IOException("The verification profile is outside the owned directory.");
        void Remove(string current)
        {
            if (!current.Equals(absolute, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The verification cleanup path is invalid.");
            ManagedModFiles.EnsureSafe(current);
            if (!Directory.Exists(current)) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                ManagedModFiles.EnsureSafe(child);
                if (Directory.Exists(child)) Remove(child);
                else File.Delete(child);
            }
            ManagedModFiles.EnsureSafe(current);
            Directory.Delete(current);
        }
        Remove(absolute);
    }

    private static string? FindBrowser()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static Task<JsonElement> DenyDownloadsAsync(ModBrowserCdpConnection cdp, CancellationToken token) =>
        cdp.CallAsync("Page.setDownloadBehavior", writer => writer.WriteString("behavior", "deny"), token);
    private void Log(string text) { try { diagnostic?.Invoke(text); } catch { } }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await CloseCoreAsync(clearSession: true).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

internal sealed class ModBrowserCdpConnection : IAsyncDisposable
{
    internal const int MaxMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket socket = new();
    private readonly HttpMessageInvoker transport = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int commandId;

    internal bool IsOpen => socket.State == WebSocketState.Open;

    internal static async Task<ModBrowserCdpConnection> ConnectAsync(Uri endpoint, CancellationToken token)
    {
        var connection = new ModBrowserCdpConnection();
        try { await connection.socket.ConnectAsync(endpoint, connection.transport, token).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async Task<JsonElement> CallAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(method == "Page.navigate" ? 30 : 10));
            using var abort = timeout.Token.Register(socket.Abort);
            int id = checked(++commandId);
            using var request = new MemoryStream();
            using (var writer = new Utf8JsonWriter(request))
            {
                writer.WriteStartObject(); writer.WriteNumber("id", id); writer.WriteString("method", method);
                writer.WritePropertyName("params"); writer.WriteStartObject(); parameters?.Invoke(writer); writer.WriteEndObject(); writer.WriteEndObject();
            }
            await socket.SendAsync(request.ToArray(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                using var message = new MemoryStream();
                var buffer = new byte[16 * 1024]; WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (received.MessageType != WebSocketMessageType.Text) throw new IOException("The verification browser connection closed.");
                    if (message.Length + received.Count > MaxMessageBytes) throw new InvalidDataException("The verification browser response exceeded its size limit.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                if (!document.RootElement.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out int actualId) || actualId != id) continue;
                if (document.RootElement.TryGetProperty("error", out _)) throw new IOException("The verification browser rejected a protocol request.");
                return document.RootElement.GetProperty("result").Clone();
            }
        }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lifetime.Cancel(); socket.Abort(); socket.Dispose(); transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
