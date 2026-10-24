namespace IDVBuff.Features.Feedback;

public sealed class OfficialFeedbackService : IFeedbackService
{
    public static OfficialFeedbackService Instance { get; } = new();
    public static Func<string?>? TokenProvider { get; set; }
    public static Func<string>? ClientVersionProvider { get; set; }
    public static HttpClient? CustomHttpClient { get; set; }
    public Task<FeedbackSubmissionResult> SubmitFeedbackAsync(FeedbackSubmissionPayload payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(FeedbackSubmissionResult.Fail("在线服务未配置，无法提交反馈。"));
}
