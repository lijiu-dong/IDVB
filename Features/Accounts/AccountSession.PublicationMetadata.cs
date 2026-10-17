using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.Features.Maps;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Accounts;

internal static partial class AccountSession
{
    public static async Task UpdatePublicationMetadataAsync(MapPublicationMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        var accountToken = _publishToken;
        var identity = await RequirePublishAccessAsync(cancellationToken);
        if (!string.Equals(identity.PublisherHandle, update.PublisherHandle, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("修改资料期间账户已切换，请重试。");
        var link = MapSubscriptionLink.Parse(update.SubscriptionLink);
        using var form = new MultipartFormDataContent();
        AddFormPart(form, new ByteArrayContent(update.FeedBytes), "feed", "feed.json");
        AddFormPart(form, new StringContent(update.ExpectedFeedSha256), "expectedFeedSha256");
        AddFormPart(form, new StringContent(Base64Url(link.ContentKey)), "contentKey");
        AddFormPart(form, new StringContent(update.PackageName), "name");
        if (update.CoverPath is { } path)
        {
            if (new FileInfo(path).Length > 5L * 1024 * 1024)
                throw new InvalidOperationException("封面超过官网 5 MB 限制，请选择较小的 PNG 或 JPEG 图片。");
            var cover = new StreamContent(File.OpenRead(path));
            cover.Headers.ContentType = new MediaTypeHeaderValue(
                Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            AddFormPart(form, cover, "cover", Path.GetFileName(path));
        }
        if (!string.Equals(_publishToken, accountToken, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("修改资料期间账户已切换，请重试。");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/maps/{update.PublicationId:D}/metadata") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await PublicationHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("官网尚未提供资料修改接口，请先部署新版社区服务。");
        using var document = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? "官网拒绝了资料修改。" : "官网拒绝了资料修改。");
        var remote = MapSubscriptionLink.Parse(document.RootElement.GetProperty("subscriptionLink").GetString()!);
        if (remote.FeedUri != link.FeedUri || remote.PublisherKeyId != link.PublisherKeyId
            || !CryptographicOperations.FixedTimeEquals(remote.ContentKey, link.ContentKey))
            throw new InvalidDataException("资料修改返回的订阅身份与原地图包不一致。");
        var actual = await PublicationHttp.GetByteArrayAsync(remote.FeedUri, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(update.FeedBytes))
            throw new InvalidDataException("官网资料已提交，但公开清单回读不一致，请刷新确认后再试。");
    }
}
