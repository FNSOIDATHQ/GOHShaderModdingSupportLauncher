using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal enum ModHttpFailureKind { Unknown, Dns, Proxy, Tls, Connection, Response, Timeout, RedirectRejected }
internal enum ModHttpStage { Request, Headers, Redirect, Body, Completed, Failed, Cancelled }

// Deliberately contains no URI path/query, request headers, body, or exception message.
// This object is safe to forward to the launcher's ordinary diagnostic log.
internal sealed record ModHttpDiagnostic(string Host, ModHttpStage Stage, int Attempt,
    int? StatusCode = null, ModHttpFailureKind? FailureKind = null, string? ExceptionType = null,
    HttpRequestError? RequestError = null, SocketError? SocketError = null, int? HResult = null,
    long? ElapsedMilliseconds = null, int? NativeErrorCode = null)
{
    public override string ToString() => $"ModHTTP host={Host} stage={Stage} attempt={Attempt}" +
        (StatusCode is { } status ? $" status={status}" : "") +
        (FailureKind is { } kind ? $" failure={kind}" : "") +
        (ExceptionType is { } type ? $" exception={type}" : "") +
        (RequestError is { } error ? $" requestError={error}" : "") +
        (SocketError is { } socket ? $" socketError={socket}" : "") +
        (HResult is { } code ? $" hresult=0x{code:X8}" : "") +
        (NativeErrorCode is { } native ? $" nativeError={native}" : "") +
        (ElapsedMilliseconds is { } elapsed ? $" elapsedMs={elapsed}" : "");
}

internal static class ModHttpDiagnostics
{
    internal static string Host(Uri uri) => uri.IsAbsoluteUri
        ? new string(uri.IdnHost.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']').ToArray())
        : "unknown";

    internal static ModHttpDiagnostic FromException(Uri uri, ModHttpStage stage, int attempt, Exception exception,
        long elapsedMilliseconds)
    {
        HttpRequestException? request = null;
        SocketException? socket = null;
        Win32Exception? native = null;
        bool tls = false;
        var types = new List<string>();
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            request ??= current as HttpRequestException;
            socket ??= current as SocketException;
            native ??= current as Win32Exception;
            tls |= current is AuthenticationException;
            types.Add(new string(current.GetType().Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()));
        }
        var error = request?.HttpRequestError;
        // WinHttpHandler wraps WinHttpException (a Win32Exception) without assigning
        // HttpRequestError. These numeric codes are defined by the Windows WinHTTP API.
        // https://learn.microsoft.com/windows/win32/winhttp/error-messages
        var nativeKind = native?.NativeErrorCode switch
        {
            12002 => ModHttpFailureKind.Timeout,
            12007 => ModHttpFailureKind.Dns,
            12029 or 12030 => ModHttpFailureKind.Connection,
            12037 or 12038 or 12044 or 12045 or 12057 or 12157 or 12169 or 12170 or 12175 or 12179 => ModHttpFailureKind.Tls,
            12166 or 12167 or 12178 or 12180 => ModHttpFailureKind.Proxy,
            _ => ModHttpFailureKind.Unknown
        };
        var kind = error switch
        {
            _ when exception is OperationCanceledException => ModHttpFailureKind.Timeout,
            HttpRequestError.NameResolutionError => ModHttpFailureKind.Dns,
            HttpRequestError.ProxyTunnelError => ModHttpFailureKind.Proxy,
            HttpRequestError.SecureConnectionError => ModHttpFailureKind.Tls,
            _ when tls => ModHttpFailureKind.Tls,
            _ when socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => ModHttpFailureKind.Dns,
            _ when nativeKind != ModHttpFailureKind.Unknown => nativeKind,
            HttpRequestError.ConnectionError => ModHttpFailureKind.Connection,
            _ when socket is not null => ModHttpFailureKind.Connection,
            _ when exception is IOException || error is not null and not HttpRequestError.Unknown => ModHttpFailureKind.Response,
            _ => ModHttpFailureKind.Unknown
        };
        return new(Host(uri), stage, attempt, request?.StatusCode is { } status ? (int)status : null,
            kind, string.Join("/", types), error, socket?.SocketErrorCode, exception.HResult, elapsedMilliseconds, native?.NativeErrorCode);
    }

    internal static string FailureMessage(ModHttpDiagnostic diagnostic)
    {
        string description = diagnostic.FailureKind switch
        {
            ModHttpFailureKind.Dns => "DNS resolution failed.",
            ModHttpFailureKind.Proxy => "The proxy tunnel could not be established.",
            ModHttpFailureKind.Tls => "The TLS connection could not be established.",
            ModHttpFailureKind.Connection => "The network connection could not be established.",
            ModHttpFailureKind.Response => "The source response was interrupted or invalid.",
            ModHttpFailureKind.Timeout => "The anonymous source request timed out.",
            _ when diagnostic.NativeErrorCode == 12015 => "The native HTTP authentication attempt failed.",
            _ => "The anonymous source could not be reached."
        };
        return description + (diagnostic.RequestError is { } error ? $" HTTP transport: {error}." : "") +
            (diagnostic.SocketError is { } socket ? $" Socket: {socket}." : "") +
            (diagnostic.NativeErrorCode is { } native ? $" Native HTTP error: {native}." : "");
    }

    internal static void Report(Action<ModHttpDiagnostic>? callback, ModHttpDiagnostic diagnostic)
    {
        try { callback?.Invoke(diagnostic); }
        catch { /* Diagnostic consumers must never change request or retry behavior. */ }
    }
}
