using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using AgentPaw.Data;
using AgentPaw.Models;
using AgentPaw.Orchestrator;
using Microsoft.Extensions.Configuration;

namespace AgentPaw.Services;

/// <summary>
/// Flutter/모바일 앱용 REST API 서버 — 포트 47893.
/// TcpListener(Any) 사용으로 관리자 권한 없이 외부 접속 허용.
/// Dev 모드 인증: Authorization: Bearer {MobileApi:DevToken}
/// </summary>
public partial class MobileApiService
{
    private readonly IDbContextFactory<AgentPawDbContext> _dbFactory;
    private readonly ProjectService _projectService;
    private readonly OrchestratorService _orchestrator;
    private readonly ConfigLoaderService _configLoader;
    private readonly WikiService _wikiService;
    private readonly string _devToken;
    private readonly string _devUserId;

    private const int Port = 47893;
    private const int MaxRequestsPerMinute = 60;
    private static int _requestCount;
    private static DateTime _windowStart = DateTime.UtcNow;
    private static readonly object _rateLock = new();

    private static bool IsRateLimited()
    {
        lock (_rateLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _windowStart).TotalSeconds >= 60)
            {
                _windowStart = now;
                _requestCount = 0;
            }
            _requestCount++;
            return _requestCount > MaxRequestsPerMinute;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly Dictionary<string, string> CorsHeaders = new()
    {
        ["Access-Control-Allow-Origin"] = "http://localhost:3000",
        ["Access-Control-Allow-Headers"] = "Authorization, Content-Type",
        ["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS"
    };

    public MobileApiService(
        IDbContextFactory<AgentPawDbContext> dbFactory,
        ProjectService projectService,
        OrchestratorService orchestrator,
        ConfigLoaderService configLoader,
        WikiService wikiService,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _projectService = projectService;
        _orchestrator = orchestrator;
        _configLoader = configLoader;
        _wikiService = wikiService;
        _devToken = configuration["MobileApi:DevToken"] ?? string.Empty;
        _devUserId = configuration["MobileApi:DevUserId"] ?? string.Empty;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_devToken) || string.IsNullOrWhiteSpace(_devUserId))
        {
            Console.WriteLine("[MobileApi] DevToken 또는 DevUserId가 설정되지 않아 시작하지 않습니다.");
            return;
        }

        // 보안: IPAddress.Any 대신 Loopback 바인딩으로 외부 네트워크 노출을 차단한다.
        // 모바일 앱 연동 시에는 SSH 터널 또는 역방향 프록시를 사용한다.
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        Console.WriteLine($"[MobileApi] 포트 {Port} 에서 수신 중 (localhost only, DevUserId={_devUserId})");

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleClientAsync(client), ct);
        }

        listener.Stop();
    }

    // ─── TCP → HTTP 파싱 ───────────────────────────────────────────────────

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        try
        {
            using var client = tcpClient;
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            var req = await ReadRequestAsync(stream);
            if (req == null) return;
            await RouteAsync(stream, req);
        }
        catch { }
    }

    private static async Task<HttpReq?> ReadRequestAsync(NetworkStream stream)
    {
        var buf = new byte[8192];
        var received = new List<byte>(4096);

        // \r\n\r\n 을 찾을 때까지 읽기
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length));
            if (n == 0) return null;
            received.AddRange(buf[..n]);

            for (var i = 0; i <= received.Count - 4; i++)
            {
                if (received[i] != '\r' || received[i + 1] != '\n' ||
                    received[i + 2] != '\r' || received[i + 3] != '\n') continue;

                var hdrText = Encoding.UTF8.GetString(received.GetRange(0, i + 4).ToArray());
                var extra   = received.GetRange(i + 4, received.Count - i - 4).ToArray();

                var lines = hdrText.Split("\r\n", StringSplitOptions.None);
                if (lines.Length == 0) return null;

                var parts = lines[0].Split(' ');
                if (parts.Length < 2) return null;
                var method = parts[0];
                var full   = parts[1];
                var qi     = full.IndexOf('?');
                var path   = (qi >= 0 ? full[..qi] : full).TrimEnd('/');
                if (string.IsNullOrEmpty(path)) path = "/";
                var qs = qi >= 0 ? full[(qi + 1)..] : "";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var j = 1; j < lines.Length; j++)
                {
                    var ci = lines[j].IndexOf(':');
                    if (ci < 0) continue;
                    headers[lines[j][..ci].Trim()] = lines[j][(ci + 1)..].Trim();
                }

                const int MaxBodySize = 1_048_576; // 1MB
                byte[] body = [];
                if (headers.TryGetValue("Content-Length", out var clStr)
                    && int.TryParse(clStr, out var cl) && cl > 0)
                {
                    if (cl > MaxBodySize) return null;

                    body = new byte[cl];
                    var copyLen = Math.Min(extra.Length, cl);
                    Array.Copy(extra, body, copyLen);
                    var offset = copyLen;
                    while (offset < cl)
                    {
                        var r = await stream.ReadAsync(body.AsMemory(offset, cl - offset));
                        if (r == 0) break;
                        offset += r;
                    }
                }

                return new HttpReq(method, path, qs, headers, body);
            }

            // 헤더 크기 상한: 64KB. Content-Length 상한: 1MB (본문 포함).
            if (received.Count > 65_536) return null;
        }
    }

    // ─── Auth ──────────────────────────────────────────────────────────────

    private bool CheckAuth(HttpReq req)
    {
        var auth = req.Headers.TryGetValue("Authorization", out var v) ? v : "";
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            auth = auth[7..].Trim();
        return auth == _devToken;
    }

    // ─── Routing ───────────────────────────────────────────────────────────

    private async Task RouteAsync(NetworkStream stream, HttpReq req)
    {
        if (req.Method == "OPTIONS")
        {
            await WriteResponseAsync(stream, 204, null, CorsHeaders);
            return;
        }

        if (IsRateLimited())
        {
            await WriteJsonAsync(stream, 429, new { error = "Too many requests" });
            return;
        }

        try
        {
            var path   = req.Path;
            var method = req.Method;

            if (path == "/m/health" && method == "GET")
            {
                await WriteJsonAsync(stream, 200, new { ok = true, version = GetVersion() });
                return;
            }

            if (!CheckAuth(req)) { await WriteJsonAsync(stream, 401, new { error = "Unauthorized" }); return; }

            if (path == "/m/me" && method == "GET") { await HandleMeAsync(stream); return; }

            if (path == "/m/projects")
            {
                if (method == "GET")  { await HandleListProjectsAsync(stream); return; }
                if (method == "POST") { await HandleCreateProjectAsync(stream, req); return; }
            }

            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 3 && segs[0] == "m" && segs[1] == "projects")
            {
                var projectId = segs[2];
                var sub = segs.Length >= 4 ? segs[3] : "";

                if (sub == "" && method == "GET")              { await HandleGetProjectAsync(stream, projectId); return; }
                if (sub == "settings" && method == "PATCH")  { await HandlePatchProjectSettingsAsync(stream, req, projectId); return; }
                if (sub == "messages" && method == "GET")    { await HandleMessagesAsync(stream, req, projectId); return; }
                if (sub == "chat" && method == "POST")      { await HandleChatAsync(stream, req, projectId); return; }
                if (sub == "personas" && method == "GET")   { await HandlePersonasAsync(stream, projectId); return; }
                if (sub == "personas" && segs.Length >= 6 && segs[5] == "model" && method == "PATCH")
                {
                    await HandlePatchPersonaModelAsync(stream, req, projectId, segs[4]); return;
                }
                if (sub == "timeline" && method == "GET")   { await HandleTimelineAsync(stream, req, projectId); return; }
                if (sub == "wiki")
                {
                    var wikiId = segs.Length >= 5 ? segs[4] : "";
                    var wikiSub = segs.Length >= 6 ? segs[5] : "";

                    if (string.IsNullOrEmpty(wikiId))
                    {
                        if (method == "GET")  { await HandleWikiListAsync(stream, projectId); return; }
                    }
                    else if (wikiId == "consolidate" && method == "POST")
                    {
                        await HandleWikiConsolidateAsync(stream, projectId); return;
                    }
                    else
                    {
                        if (method == "GET")    { await HandleWikiDetailAsync(stream, projectId, wikiId); return; }
                        if (method == "PATCH")  { await HandleWikiUpdateAsync(stream, req, projectId, wikiId); return; }
                        if (method == "DELETE") { await HandleWikiDeleteAsync(stream, projectId, wikiId); return; }
                    }
                }
            }

            await WriteJsonAsync(stream, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MobileApi] Error: {ex}");
            try { await WriteJsonAsync(stream, 500, new { error = "Internal server error" }); } catch { }
        }
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object? data)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, JsonOpts));
        await WriteResponseAsync(stream, status, body, CorsHeaders);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, byte[]? body, Dictionary<string, string>? extra)
    {
        var statusText = status switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content",
            400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found",
            500 => "Internal Server Error", _ => "Unknown"
        };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {statusText}\r\nConnection: close\r\n");
        if (body is { Length: > 0 })
        {
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
        }
        else sb.Append("Content-Length: 0\r\n");
        if (extra != null)
            foreach (var (k, v) in extra) sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");
        var hdr = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(hdr);
        if (body is { Length: > 0 }) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(string qs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(qs)) return result;
        foreach (var pair in qs.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) { result[Uri.UnescapeDataString(pair)] = ""; continue; }
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    private static string GetVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    }

    private record HttpReq(
        string Method,
        string Path,
        string QueryString,
        Dictionary<string, string> Headers,
        byte[] Body);
}
