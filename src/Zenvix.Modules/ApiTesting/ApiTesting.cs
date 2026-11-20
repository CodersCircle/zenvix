using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Zenvix.Modules.ApiTesting
{
    public enum BodyKind { None, Json, FormData, UrlEncoded, Text }
    public enum AuthKind { None, Bearer, ApiKey, Basic }

    public sealed class KeyValue
    {
        public bool Enabled { get; set; } = true;
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public sealed class ApiRequest
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New request";
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "{{baseUrl}}/api";
        public List<KeyValue> Query { get; set; } = new();
        public List<KeyValue> Headers { get; set; } = new();
        public BodyKind BodyKind { get; set; } = BodyKind.None;
        public string Body { get; set; } = "";
        public List<KeyValue> Form { get; set; } = new();
        public AuthKind Auth { get; set; } = AuthKind.None;
        public string Token { get; set; } = "";
        public string ApiKeyName { get; set; } = "X-API-Key";
        public string ApiKeyValue { get; set; } = "";
        public bool ApiKeyInQuery { get; set; }
        public string BasicUser { get; set; } = "";
        public string BasicPassword { get; set; } = "";

        public ApiRequest Clone() => JsonSerializer.Deserialize<ApiRequest>(JsonSerializer.Serialize(this))!;
    }

    public sealed class ApiCollection
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "My collection";
        public List<ApiRequest> Requests { get; set; } = new();
    }

    public sealed class ApiEnvironment
    {
        public string Name { get; set; } = "Local";
        public List<KeyValue> Variables { get; set; } = new();
    }

    public sealed class ApiHistoryEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public ApiRequest Request { get; set; } = new();
        public int Status { get; set; }
        public long Milliseconds { get; set; }
    }

    public sealed class ApiWorkspace
    {
        public List<ApiCollection> Collections { get; set; } = new();
        public List<ApiEnvironment> Environments { get; set; } = new();
        public string? ActiveEnvironment { get; set; }
        public List<ApiHistoryEntry> History { get; set; } = new();
    }

    public sealed class ApiResponse
    {
        public int Status { get; init; }
        public string Reason { get; init; } = "";
        public long Milliseconds { get; init; }
        public long SizeBytes { get; init; }
        public string Body { get; init; } = "";
        public bool IsJson { get; init; }
        public List<KeyValuePair<string, string>> Headers { get; init; } = new();
        public string? Error { get; init; }
    }

    public interface IApiTestingService
    {
        ApiWorkspace Workspace { get; }
        void Save();
        Task<ApiResponse> SendAsync(ApiRequest request, ApiEnvironment? env, CancellationToken ct = default);
        string Resolve(string text, ApiEnvironment? env);
    }

    /// <summary>Postman-style request runner. Everything is stored locally in config/api-testing.json.</summary>
    public sealed class ApiTestingService : IApiTestingService
    {
        private const int MaxHistory = 100;
        private readonly string _file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "api-testing.json");
        private readonly HttpClient _http;

        public ApiWorkspace Workspace { get; }

        public ApiTestingService()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                // Local sites use Zenvix's own certificates; only local hosts skip certificate checks.
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                        errors == System.Net.Security.SslPolicyErrors.None
                        || (sender is System.Net.Security.SslStream ssl && IsLocal(new Uri("https://" + ssl.TargetHostName))),
                },
                // IPv4 first: on Windows "localhost" tries ::1 first and waits ~2 s when the server only listens on IPv4.
                ConnectCallback = async (ctx, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    Exception? last = null;
                    foreach (var a in addresses.OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 1 : 0))
                    {
                        var socket = new System.Net.Sockets.Socket(a.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(a, ctx.DnsEndPoint.Port), ct);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception ex) { last = ex; socket.Dispose(); }
                    }
                    throw last ?? new HttpRequestException($"Could not resolve {ctx.DnsEndPoint.Host}");
                },
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
            Workspace = Load();
        }

        private static bool IsLocal(Uri? uri)
        {
            if (uri == null) return false;
            var h = uri.Host.ToLowerInvariant();
            return h is "localhost" or "127.0.0.1" or "::1" || h.EndsWith(".test") || h.EndsWith(".zenvix") || h.EndsWith(".local") || h.EndsWith(".localhost");
        }

        private ApiWorkspace Load()
        {
            try
            {
                if (File.Exists(_file))
                {
                    var ws = JsonSerializer.Deserialize<ApiWorkspace>(File.ReadAllText(_file));
                    if (ws != null) return ws;
                }
            }
            catch (Exception ex) { Log.Warning(ex, "[API] could not read {File}", _file); }

            // First run: a local environment and a sample collection.
            var env = new ApiEnvironment
            {
                Name = "Local",
                Variables = new() { new() { Key = "baseUrl", Value = "http://localhost" }, new() { Key = "token", Value = "" }, new() { Key = "apiKey", Value = "" } },
            };
            return new ApiWorkspace
            {
                Environments = new() { env },
                ActiveEnvironment = env.Name,
                Collections = new() { new ApiCollection { Name = "My API", Requests = new() { new ApiRequest { Name = "Health check", Url = "{{baseUrl}}/" } } } },
            };
        }

        public void Save()
        {
            try
            {
                if (Workspace.History.Count > MaxHistory) Workspace.History.RemoveRange(MaxHistory, Workspace.History.Count - MaxHistory);
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Workspace, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex) { Log.Warning(ex, "[API] could not save {File}", _file); }
        }

        /// <summary>Replaces {{name}} with the environment's variable.</summary>
        public string Resolve(string text, ApiEnvironment? env)
        {
            if (string.IsNullOrEmpty(text) || env == null) return text ?? "";
            return Regex.Replace(text, @"\{\{\s*([\w.-]+)\s*\}\}", m =>
                env.Variables.FirstOrDefault(v => v.Enabled && v.Key == m.Groups[1].Value)?.Value ?? m.Value);
        }

        public async Task<ApiResponse> SendAsync(ApiRequest r, ApiEnvironment? env, CancellationToken ct = default)
        {
            string R(string s) => Resolve(s, env);
            var sw = Stopwatch.StartNew();
            try
            {
                // URL + query parameters
                var url = R(r.Url).Trim();
                if (!Regex.IsMatch(url, "^[a-zA-Z][a-zA-Z0-9+.-]*://")) url = "http://" + url;
                var query = r.Query.Where(q => q.Enabled && q.Key.Length > 0).Select(q => (R(q.Key), R(q.Value))).ToList();
                if (r.Auth == AuthKind.ApiKey && r.ApiKeyInQuery && r.ApiKeyName.Length > 0) query.Add((R(r.ApiKeyName), R(r.ApiKeyValue)));
                if (query.Count > 0)
                    url += (url.Contains('?') ? "&" : "?") + string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Item1)}={Uri.EscapeDataString(q.Item2)}"));

                using var msg = new HttpRequestMessage(new HttpMethod(r.Method.ToUpperInvariant()), url);

                // Body
                var contentHeaders = new List<(string, string)>();
                switch (r.BodyKind)
                {
                    case BodyKind.Json:
                        msg.Content = new StringContent(R(r.Body), Encoding.UTF8, "application/json");
                        break;
                    case BodyKind.Text:
                        msg.Content = new StringContent(R(r.Body), Encoding.UTF8, "text/plain");
                        break;
                    case BodyKind.UrlEncoded:
                        msg.Content = new FormUrlEncodedContent(r.Form.Where(f => f.Enabled && f.Key.Length > 0)
                            .Select(f => new KeyValuePair<string, string>(R(f.Key), R(f.Value))));
                        break;
                    case BodyKind.FormData:
                        var multi = new MultipartFormDataContent();
                        foreach (var f in r.Form.Where(f => f.Enabled && f.Key.Length > 0))
                        {
                            var value = R(f.Value);
                            // "@C:\path\file.png" sends a file, like curl.
                            if (value.StartsWith("@") && File.Exists(value[1..]))
                            {
                                var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(value[1..], ct));
                                multi.Add(bytes, R(f.Key), Path.GetFileName(value[1..]));
                            }
                            else multi.Add(new StringContent(value), R(f.Key));
                        }
                        msg.Content = multi;
                        break;
                }

                // Auth
                switch (r.Auth)
                {
                    case AuthKind.Bearer when r.Token.Length > 0:
                        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", R(r.Token));
                        break;
                    case AuthKind.Basic:
                        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{R(r.BasicUser)}:{R(r.BasicPassword)}"));
                        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
                        break;
                    case AuthKind.ApiKey when !r.ApiKeyInQuery && r.ApiKeyName.Length > 0:
                        msg.Headers.TryAddWithoutValidation(R(r.ApiKeyName), R(r.ApiKeyValue));
                        break;
                }

                // Headers (Content-Type & co. belong to the content)
                foreach (var h in r.Headers.Where(h => h.Enabled && h.Key.Length > 0))
                {
                    var (k, v) = (R(h.Key), R(h.Value));
                    if (!msg.Headers.TryAddWithoutValidation(k, v) && msg.Content != null)
                    {
                        msg.Content.Headers.Remove(k);
                        msg.Content.Headers.TryAddWithoutValidation(k, v);
                    }
                }
                if (!msg.Headers.Contains("User-Agent")) msg.Headers.TryAddWithoutValidation("User-Agent", "Zenvix-API-Tester");

                using var res = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct);
                var bytesBody = await res.Content.ReadAsByteArrayAsync(ct);
                sw.Stop();

                var headers = res.Headers.Concat(res.Content.Headers)
                    .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value))).ToList();
                var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";
                var text = Encoding.UTF8.GetString(bytesBody);
                var (pretty, isJson) = PrettyJson(text, mediaType);

                return new ApiResponse
                {
                    Status = (int)res.StatusCode,
                    Reason = res.ReasonPhrase ?? res.StatusCode.ToString(),
                    Milliseconds = sw.ElapsedMilliseconds,
                    SizeBytes = bytesBody.LongLength,
                    Body = pretty,
                    IsJson = isJson,
                    Headers = headers,
                };
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ApiResponse { Milliseconds = sw.ElapsedMilliseconds, Error = "Timed out after 100 seconds." };
            }
            catch (Exception ex)
            {
                return new ApiResponse { Milliseconds = sw.ElapsedMilliseconds, Error = ex.InnerException?.Message ?? ex.Message };
            }
        }

        private static (string Text, bool IsJson) PrettyJson(string text, string mediaType)
        {
            var t = text.TrimStart();
            if (!mediaType.Contains("json") && !(t.StartsWith("{") || t.StartsWith("["))) return (text, false);
            try
            {
                using var doc = JsonDocument.Parse(text);
                return (JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), true);
            }
            catch { return (text, false); }
        }
    }
}
