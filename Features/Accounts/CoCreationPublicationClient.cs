using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Accounts;

internal static class CoCreationPublicationClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        BaseAddress = new("https://community.idvb.xgflee.com/"), Timeout = TimeSpan.FromSeconds(20)
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task<IReadOnlyList<MapCoCreationPublication>> GetAsync(CancellationToken cancellationToken)
    {
        var identity = AccountSession.Identity;
        var token = AccountSession.PublishToken;
        if (identity is null || string.IsNullOrEmpty(token)) return [];
        var result = new List<MapCoCreationPublication>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/maps/mine"
                + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            EnsureCurrent(identity, token);
            await RequireSuccessAsync(response, token, cancellationToken);
            var body = JsonSerializer.Deserialize<PublicationPage>(await response.Content.ReadAsByteArrayAsync(cancellationToken), JsonOptions)
                ?? throw new InvalidDataException("云端作品列表为空。");
            if (!string.Equals(body.PublisherHandle, identity.PublisherHandle, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("云端作品列表与当前账号不匹配。");
            result.AddRange(body.Publications.Where(p => string.Equals(p.PublisherHandle, identity.PublisherHandle, StringComparison.OrdinalIgnoreCase)));
            cursor = body.NextCursor;
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidDataException("云端作品列表重复分页。");
        } while (cursor is not null);
        EnsureCurrent(identity, token);
        return result.DistinctBy(p => p.Id).ToArray();
    }

    internal static async Task<MapCoCreationCommunityCatalog> GetCommunityAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync("api/maps", cancellationToken);
        response.EnsureSuccessStatusCode();
        var page = JsonSerializer.Deserialize<CatalogPage>(await response.Content.ReadAsByteArrayAsync(cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("社区地图列表为空。");
        return new(page.Publications.DistinctBy(p => p.Id).ToArray(), page.Maps);
    }

    internal static async Task<MapCoCreationCommunityCatalog> GetAcquiredAsync(CancellationToken cancellationToken)
    {
        var identity = AccountSession.Identity ?? throw new UnauthorizedAccessException("请先登录。");
        var token = AccountSession.PublishToken;
        var publications = new List<MapCoCreationPublication>();
        var maps = new List<MapCoCreationDownload>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/maps/acquired"
                + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            EnsureCurrent(identity, token);
            await RequireSuccessAsync(response, token, cancellationToken);
            var page = JsonSerializer.Deserialize<CatalogPage>(await response.Content.ReadAsByteArrayAsync(cancellationToken), JsonOptions)
                ?? throw new InvalidDataException("获取记录为空。");
            EnsureCurrent(identity, token);
            if (!string.Equals(page.PublisherHandle, identity.PublisherHandle, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("获取记录与当前账号不匹配。");
            publications.AddRange(page.Publications);
            maps.AddRange(page.Maps);
            cursor = page.NextCursor;
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidDataException("获取记录重复分页。");
        } while (cursor is not null);
        return new(publications.DistinctBy(p => p.Id).ToArray(), maps.DistinctBy(m => m.ResourceKey ?? m.Filename).ToArray());
    }

    internal static async Task AcquireAsync(Guid? publicationId, string? filename, CancellationToken cancellationToken)
    {
        var identity = AccountSession.Identity ?? throw new UnauthorizedAccessException("请先登录后获取地图。");
        var token = AccountSession.PublishToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/maps/acquired")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { publicationId, filename }),
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        EnsureCurrent(identity, token);
        await RequireSuccessAsync(response, token, cancellationToken);
    }

    private static void EnsureCurrent(AccountIdentity identity, string? token)
    {
        if (!ReferenceEquals(identity, AccountSession.Identity) || token != AccountSession.PublishToken)
            throw new OperationCanceledException("账号已切换。");
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, string? accountToken, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            AccountSession.ClearIfCurrent(accountToken);
            throw new UnauthorizedAccessException("登录已过期，请重新登录后刷新。");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        if (response.StatusCode == HttpStatusCode.NotFound && json.RootElement.TryGetProperty("error", out var error)
            && error.GetString() == "not_found")
            throw new InvalidOperationException("账号地图服务暂时不可用，请稍后刷新。");
        throw new InvalidOperationException(json.RootElement.TryGetProperty("message", out var message)
            ? message.GetString() : "无法读取账号地图记录。");
    }

    private sealed class CatalogPage
    {
        public string PublisherHandle { get; set; } = string.Empty;
        public List<MapCoCreationPublication> Publications { get; set; } = [];
        public List<MapCoCreationDownload> Maps { get; set; } = [];
        public string? NextCursor { get; set; }
    }
    private sealed class PublicationPage
    {
        public string PublisherHandle { get; set; } = string.Empty;
        public List<MapCoCreationPublication> Publications { get; set; } = [];
        public string? NextCursor { get; set; }
    }
}
