using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

// Delivered offline diagnostics. Synthetic downloads are read as bytes and never executed.
internal static class LauncherUpdateSmokeTest
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static void Require(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException("Launcher update: " + reason); }

    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ModSourceException) { return; }
        throw new InvalidOperationException("Launcher update accepted " + reason);
    }

    private static string Json(string tag, string digest, int size = 512) => $$"""
        {"id":123,"tag_name":"{{tag}}","draft":false,"prerelease":false,
        "html_url":"https://github.com/{{GitHubLauncherUpdateClient.Repository}}/releases/tag/{{tag}}",
        "body":"Release notes\nSecond line", "published_at":"2026-11-14T00:00:00Z",
        "assets":[{"id":456,"name":"{{GitHubLauncherUpdateClient.AssetName}}","state":"uploaded","size":{{size}},
        "digest":"sha256:{{digest}}","browser_download_url":"https://github.com/{{GitHubLauncherUpdateClient.Repository}}/releases/download/{{tag}}/{{GitHubLauncherUpdateClient.AssetName}}"}]}
        """;

    private static HttpResponseMessage Response(HttpStatusCode code, string json = "", string? etag = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static GitHubLauncherUpdateClient Client(string path,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> get,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? download = null,
        ModHostLimiter? limiter = null, Func<DateTimeOffset>? clock = null)
    {
        limiter ??= new();
        return new(path,
            new AnonymousModHttpClient(new Handler(get), new ModHttpOptions { HostInterval = TimeSpan.Zero, MaxRetries = 0 }, limiter),
            new ModFileTransferClient(new Handler(download ?? ((_, _) => throw new InvalidOperationException("Unexpected download"))),
                new ModTransferOptions { AllowHost = GitHubLauncherUpdateClient.IsDownloadHost, HostInterval = TimeSpan.Zero, MaxRetries = 0 }, limiter), clock);
    }

    internal static void Run(string root)
    {
        string fixture = Directory.CreateDirectory(Path.Combine(root, "launcher-update")).FullName;
        int passed = 0;
        void Check(string name, Action action)
        {
            action(); passed++;
            AppDiagnostics.Log("Launcher update self-test: " + name);
            Console.WriteLine("PASS Launcher updater: " + name);
        }
        byte[] executable = new byte[512];
        using (var bytes = new MemoryStream(executable))
        using (var writer = new BinaryWriter(bytes))
        {
            writer.Write((ushort)0x5a4d); bytes.Position = 0x3c; writer.Write(64);
            bytes.Position = 64; writer.Write(0x4550u); writer.Write((ushort)0x8664);
            bytes.Position = 64 + 22; writer.Write((ushort)2); writer.Write((ushort)0x20b);
        }
        string digest = Convert.ToHexString(SHA256.HashData(executable));
        string valid = Json("v4.01", digest);
        var release = GitHubLauncherUpdateClient.ParseRelease(valid, "4.00");
        Check("numeric versions and v4.00 formatting", () =>
        {
            Require(GitHubLauncherUpdateClient.TryVersion("v4.00", out var a) && GitHubLauncherUpdateClient.TryVersion("4.0.0", out var b) && a == b, "version normalization");
            Require(GitHubLauncherUpdateClient.TryVersion("4.10", out a) && GitHubLauncherUpdateClient.TryVersion("4.9", out b) && a > b, "integer component ordering");
            foreach (string tag in new[] { "2.21Beta", "v4.01-beta", "4", "4.-1", "4.0+abc", "4.999999", "4.0.0.0.1" })
                Require(!GitHubLauncherUpdateClient.TryVersion(tag, out _), "invalid version " + tag);
        });
        Check("strictly newer release and exact asset", () => Require(release.State == LauncherUpdateState.Available && release.Asset?.Sha256 == digest, "valid release"));
        Check("same version and local newer never downgrade", () =>
        {
            Require(GitHubLauncherUpdateClient.ParseRelease(valid, "4.01.0").State == LauncherUpdateState.Current, "same version");
            Require(GitHubLauncherUpdateClient.ParseRelease(valid, "4.02").State == LauncherUpdateState.LocalNewer, "newer local");
        });
        Check("legacy Beta and preview flags", () =>
        {
            Require(GitHubLauncherUpdateClient.ParseRelease(Json("2.21Beta", digest), "4.00").State == LauncherUpdateState.Unsupported, "old WPF Beta");
            Require(GitHubLauncherUpdateClient.ParseRelease(valid.Replace("\"prerelease\":false", "\"prerelease\":true"), "4.00").State == LauncherUpdateState.Unsupported, "prerelease");
            Require(GitHubLauncherUpdateClient.ParseRelease(valid.Replace("\"draft\":false", "\"draft\":true"), "4.00").State == LauncherUpdateState.Unsupported, "draft");
        });
        Check("missing asset and unknown current version", () =>
        {
            Require(GitHubLauncherUpdateClient.ParseRelease(valid.Replace(GitHubLauncherUpdateClient.AssetName, "GOHSMSLauncherWPF.exe"), "4.00").IssueKey == "LU_MissingAsset", "legacy asset");
            Require(GitHubLauncherUpdateClient.ParseRelease(valid, "Unknown").IssueKey == "LU_UnknownVersion", "unknown local");
        });
        Check("missing or invalid digest blocks installation", () =>
        {
            Require(GitHubLauncherUpdateClient.ParseRelease(valid.Replace("\"sha256:" + digest + "\"", "null"), "4.00").IssueKey == "LU_MissingDigest", "missing digest");
            Require(GitHubLauncherUpdateClient.ParseRelease(valid.Replace(digest, new string('z', 64)), "4.00").IssueKey == "LU_MissingDigest", "invalid digest");
        });
        Check("foreign repository and download URL are rejected", () =>
        {
            Reject(() => GitHubLauncherUpdateClient.ParseRelease(valid.Replace("https://github.com/", "https://example.com/"), "4.00"), "foreign release page");
            Require(GitHubLauncherUpdateClient.ParseRelease(valid.Replace("/releases/download/", "/other/download/"), "4.00").IssueKey == "LU_InvalidAsset", "foreign attachment");
            Reject(() => GitHubLauncherUpdateClient.ParseRelease("{}", "4.00"), "malformed metadata");
        });
        Check("conditional cache, restart reuse and forced refresh", () =>
        {
            string cache = Path.Combine(fixture, "cache.json");
            int requests = 0;
            var now = DateTimeOffset.UtcNow;
            using var client = Client(cache, (request, _) =>
            {
                Require(request.RequestUri == GitHubLauncherUpdateClient.LatestApi && request.Headers.Authorization is null, "fixed anonymous endpoint");
                Require(request.Headers.UserAgent.Count > 0 && request.Headers.Contains("X-GitHub-Api-Version"), "GitHub headers");
                requests++;
                if (requests == 1) return Task.FromResult(Response(HttpStatusCode.OK, valid, "\"release1\""));
                Require(request.Headers.IfNoneMatch.Single().Tag == "\"release1\"", "conditional ETag");
                return Task.FromResult(Response(HttpStatusCode.NotModified));
            }, clock: () => now);
            Require(!client.CheckAsync("4.00", false).GetAwaiter().GetResult().FromCache, "initial check");
            Require(client.CheckAsync("4.00", false).GetAwaiter().GetResult().FromCache && requests == 1, "six-hour cache");
            using (var reopened = Client(cache, (_, _) => throw new InvalidOperationException("Unexpected network"), clock: () => now))
                Require(reopened.CheckAsync("4.02", false).GetAwaiter().GetResult().Release.State == LauncherUpdateState.LocalNewer, "cached snapshot compares current version again");
            _ = client.CheckAsync("4.00", true).GetAwaiter().GetResult();
            Require(requests == 2, "manual refresh");
            now += TimeSpan.FromHours(7);
            _ = client.CheckAsync("4.00", false).GetAwaiter().GetResult();
            Require(requests == 3, "cache expiry");
        });
        Check("network and parse failures preserve the last snapshot", () =>
        {
            string cache = Path.Combine(fixture, "cache.json"), before = File.ReadAllText(cache);
            using (var client = Client(cache, (_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}"))))
                Reject(() => client.CheckAsync("4.00", true).GetAwaiter().GetResult(), "invalid network metadata");
            Require(File.ReadAllText(cache) == before, "parse failure cache preservation");
            using (var client = Client(cache, (_, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable))))
                Reject(() => client.CheckAsync("4.00", true).GetAwaiter().GetResult(), "network failure");
            Require(File.ReadAllText(cache) == before, "network failure cache preservation");
        });
        Check("shared anonymous GitHub quota cooldown", () =>
        {
            var limiter = new ModHostLimiter();
            using var client = Client(Path.Combine(fixture, "rate.json"), (_, _) =>
            {
                var response = Response(HttpStatusCode.Forbidden);
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(response);
            }, limiter: limiter);
            try { _ = client.CheckAsync("4.00", true).GetAwaiter().GetResult(); throw new Exception("Expected rate limit"); }
            catch (ModSourceException ex) { Require(ex.Failure.Kind == ModSourceError.RateLimited && ex.Failure.RetryAfter > DateTimeOffset.UtcNow, "quota retry time"); }
            Require(limiter.GitHubCooldowns.ContainsKey("core"), "shared core quota");
        });
        Check("404 and damaged cache recover independently", () =>
        {
            string path = Path.Combine(fixture, "missing.json");
            File.WriteAllText(path, "damaged");
            using var client = Client(path, (_, _) => Task.FromResult(Response(HttpStatusCode.NotFound)));
            Require(client.CheckAsync("4.00", false).GetAwaiter().GetResult().Release.State == LauncherUpdateState.NoRelease, "no public release");
        });

        string install = Directory.CreateDirectory(Path.Combine(fixture, "安装 O'Brien [test] $data")).FullName;
        string target = Path.Combine(install, "installed launcher.exe"), backup = Path.Combine(install, "old.exe");
        File.WriteAllText(target, "old version");
        LauncherUpdate.Candidate Download(byte[] bytes, LauncherRelease selected, Action<long, long?>? report = null, CancellationToken token = default)
        {
            using var client = Client(Path.Combine(fixture, "download.json"), (_, _) => throw new Exception("Unexpected metadata request"), (request, _) =>
            {
                Require(request.Headers.Authorization is null, "anonymous binary request");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new("application/octet-stream");
                return Task.FromResult(response);
            });
            return client.DownloadAsync(selected, target, report ?? ((_, _) => { }), token).GetAwaiter().GetResult();
        }
        Check("verified streaming download preserves the installed file", () =>
        {
            long progress = 0;
            var candidate = Download(executable, release, (done, _) => progress = done);
            Require(progress == 512 && File.ReadAllText(target) == "old version" && LauncherUpdate.Hash(candidate.Source) == digest, "verified download");
            using var cleanup = Client(Path.Combine(fixture, "download.json"), (_, _) => throw new Exception());
            cleanup.CleanDownload(candidate);
        });
        Check("checksum mismatch leaves target and download storage intact", () =>
        {
            var changed = executable.ToArray(); changed[^1] = 1;
            Reject(() => Download(changed, release), "checksum mismatch");
            Require(File.ReadAllText(target) == "old version", "checksum target preservation");
            Require(!Directory.EnumerateDirectories(Path.Combine(fixture, "Updates")).Any(), "failed download cleanup");
        });
        Check("declared size and executable type", () =>
        {
            Reject(() => Download(executable[..128], release), "truncated file");
            var wrong = executable.ToArray(); wrong[68] = 0x4c; wrong[69] = 0x01;
            var selected = GitHubLauncherUpdateClient.ParseRelease(Json("v4.01", Convert.ToHexString(SHA256.HashData(wrong))), "4.00");
            Reject(() => Download(wrong, selected), "x86 executable");
        });
        Check("cancelled downloads keep the installed program", () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { _ = Download(executable, release, token: cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Require(File.ReadAllText(target) == "old version", "cancel target preservation");
        });
        Check("download redirects stay on official hosts", () =>
        {
            using var client = Client(Path.Combine(fixture, "redirect.json"), (_, _) => throw new Exception(), (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://example.com/update.exe");
                return Task.FromResult(response);
            });
            Reject(() => client.DownloadAsync(release, target, (_, _) => { }, default).GetAwaiter().GetResult(), "foreign redirect");
        });

        string source = Path.Combine(fixture, "verified.exe");
        File.WriteAllBytes(source, executable);
        LauncherUpdate.Candidate Candidate() => new(source, target, LauncherUpdate.Hash(source), LauncherUpdate.Hash(target));
        int Apply(LauncherUpdate.Prepared update, bool restartFailure = false)
        {
            string script = LauncherUpdate.BuildScript(update, int.MaxValue, restart: false);
            if (restartFailure) script = script.Replace("# Restart disabled for regression tests.", "throw 'Simulated restart failure.'");
            var info = LauncherUpdate.CreateHelperStartInfo(script);
            info.RedirectStandardError = true;
            info.Environment["PSModulePath"] = Path.Combine(fixture, "empty-modules");
            using var helper = Process.Start(info)!;
            if (!helper.WaitForExit(45000)) { helper.Kill(); throw new TimeoutException("Update helper timed out."); }
            if (helper.ExitCode != 0)
            {
                string failure = helper.StandardError.ReadToEnd();
                AppDiagnostics.Log("Update helper diagnostic: " + failure);
                Console.WriteLine("Update helper diagnostic: " + failure.Replace("#< CLIXML", ""));
            }
            return helper.ExitCode;
        }
        Check("atomic replacement and old.exe preservation", () =>
        {
            var prepared = LauncherUpdate.Prepare(Candidate());
            Require(File.ReadAllText(target) == "old version" && !File.Exists(backup), "preparation does not replace target");
            Require(Apply(prepared) == 0 && LauncherUpdate.Hash(target) == digest && File.ReadAllText(backup) == "old version", "atomic replacement");
            Reject(() => LauncherUpdate.Prepare(Candidate()), "existing backup");
            File.Delete(backup); File.WriteAllText(target, "old version");
        });
        Check("failed restart restores the previous executable", () =>
        {
            Require(Apply(LauncherUpdate.Prepare(Candidate()), restartFailure: true) == 1, "restart failure status");
            Require(File.ReadAllText(target) == "old version" && !File.Exists(backup), "automatic recovery");
            Require(File.Exists(target + ".update-error.log"), "recovery log");
        });
        Check("changed staged bytes cannot be applied", () =>
        {
            var prepared = LauncherUpdate.Prepare(Candidate());
            File.WriteAllText(prepared.Staged, "tampered");
            Require(Apply(prepared) == 1 && File.ReadAllText(target) == "old version" && !File.Exists(backup), "staged hash check");
        });
        Check("changed installed bytes cannot be overwritten", () =>
        {
            var prepared = LauncherUpdate.Prepare(Candidate());
            File.WriteAllText(target, "another update");
            Require(Apply(prepared) == 1 && File.ReadAllText(target) == "another update" && !File.Exists(backup), "target hash check");
            File.WriteAllText(target, "old version");
        });
        Check("late backup conflicts preserve every file", () =>
        {
            var prepared = LauncherUpdate.Prepare(Candidate());
            File.WriteAllText(backup, "existing backup");
            Require(Apply(prepared) == 1 && File.ReadAllText(target) == "old version" && File.ReadAllText(backup) == "existing backup", "late backup conflict");
            File.Delete(backup);
        });
        Check("preparation rejects changed source and shared dotnet host", () =>
        {
            var candidate = Candidate(); File.WriteAllText(source, "changed source");
            Reject(() => LauncherUpdate.Prepare(candidate), "changed source");
            Require(!Directory.EnumerateFiles(install, "*.tmp").Any(), "failed preparation cleanup");
            string host = Path.Combine(install, "dotnet.exe"); File.WriteAllText(host, "shared host");
            Reject(() => LauncherUpdate.CheckTarget(host), "dotnet host");
        });
        AppDiagnostics.Log($"Launcher update self-test passed: {passed} checks.");
    }
}
