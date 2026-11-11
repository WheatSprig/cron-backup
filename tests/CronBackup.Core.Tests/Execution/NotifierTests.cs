using System.Net;
using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;

namespace CronBackup.Core.Tests.Execution;

public sealed class NotifierTests
{
    [Fact]
    public async Task SendAsync_ShouldPostWebhookJson()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var notifier = new HttpNotifier(client);
        var message = new NotificationMessage(
            "photos",
            true,
            "backup_20261111_020000.tar.gz",
            ["local"],
            null,
            DateTimeOffset.UtcNow);

        var results = await notifier.SendAsync(
            [new NotifyConfig { Kind = "webhook", Url = "https://example.com/hook" }],
            NotificationPhase.Success,
            message,
            CancellationToken.None);

        Assert.True(Assert.Single(results).Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"job\":\"photos\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"success\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ShouldUseHealthchecksStartAndFailPaths()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var notifier = new HttpNotifier(client);
        var target = new[] { new NotifyConfig { Kind = "healthchecks", Url = "https://hc.example.com/ping/abc" } };
        var message = new NotificationMessage("photos", false, "archive", ["local"], "boom", DateTimeOffset.UtcNow);

        await notifier.SendAsync(target, NotificationPhase.Start, message, CancellationToken.None);
        await notifier.SendAsync(target, NotificationPhase.Failure, message, CancellationToken.None);

        Assert.Equal("https://hc.example.com/ping/abc/start", handler.Requests[0].Url);
        Assert.Equal("https://hc.example.com/ping/abc/fail", handler.Requests[1].Url);
    }

    [Fact]
    public async Task SendAsync_ShouldAppendNtfyTopicAndHeaders()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var notifier = new HttpNotifier(client);
        var message = new NotificationMessage("photos", true, "archive", ["local"], null, DateTimeOffset.UtcNow);

        await notifier.SendAsync(
            [new NotifyConfig { Kind = "ntfy", Url = "https://ntfy.example.com", Topic = "backups" }],
            NotificationPhase.Success,
            message,
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ntfy.example.com/backups", request.Url);
        Assert.Contains("cron-backup photos: success", request.Body, StringComparison.Ordinal);
        Assert.Equal("default", request.Headers["Priority"]);
    }

    [Fact]
    public async Task SendAsync_ShouldReportHttpFailure()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);
        var notifier = new HttpNotifier(client);
        var message = new NotificationMessage("photos", true, "archive", ["local"], null, DateTimeOffset.UtcNow);

        var results = await notifier.SendAsync(
            [new NotifyConfig { Kind = "webhook", Url = "https://example.com/hook" }],
            NotificationPhase.Success,
            message,
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public List<(HttpMethod Method, string Url, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body, headers));
            return new HttpResponseMessage(_statusCode);
        }
    }
}
