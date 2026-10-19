using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Announcements;

public sealed class AnnouncementService
{
    public static AnnouncementService Instance { get; } = new();
    public static HttpClient? CustomHttpClient { get; set; }
    public event Action? UnreadCountChanged { add { } remove { } }
    public AnnouncementService(string? customStorageDir = null) { }
    public Task EnsureInitializedAsync() => Task.CompletedTask;
    public List<AnnouncementItem> GetCachedAnnouncements(string? category = null) => [];
    public int GetUnreadCount() => 0;
    public Task<List<AnnouncementItem>> GetAnnouncementsAsync(string? category = null, bool forceRefresh = false) =>
        Task.FromException<List<AnnouncementItem>>(new OnlineServiceUnavailableException("announcements.list"));
    public Task<AnnouncementItem?> GetImportantUnreadAsync(bool forceRefresh = false) =>
        Task.FromException<AnnouncementItem?>(new OnlineServiceUnavailableException("announcements.list"));
    public Task MarkAsReadAsync(string announcementId) => Task.FromException(new OnlineServiceUnavailableException("announcements.read"));
}
