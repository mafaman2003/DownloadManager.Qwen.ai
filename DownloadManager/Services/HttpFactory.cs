using System;
using System.Net;
using System.Net.Http;
using DownloadManager.Models;

namespace DownloadManager.Services;

public sealed class TransientHttpException : HttpRequestException
{
    public HttpStatusCode Status { get; }
    public TransientHttpException(HttpStatusCode status)
        : base($"Server returned transient HTTP {(int)status}.") => Status = status;
}

public sealed class ResumeNotSupportedException : System.IO.IOException
{
    public ResumeNotSupportedException()
        : base("Server does not support resuming this download.") { }
}

public static class HttpFactory
{
    public static HttpClient Create(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };

        switch (settings.ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;

            case ProxyMode.Custom when Uri.TryCreate(settings.ProxyUrl, UriKind.Absolute, out var proxyUri):
                var proxy = new WebProxy(proxyUri, BypassOnLocal: false);
                if (!string.IsNullOrEmpty(settings.ProxyUser))
                    proxy.Credentials = new NetworkCredential(settings.ProxyUser, settings.ProxyPassword);
                handler.Proxy = proxy;
                handler.UseProxy = true;
                break;

            default: // System — use OS proxy settings
                handler.UseProxy = true;
                break;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromHours(24) };
    }
}