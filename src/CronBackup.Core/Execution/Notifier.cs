using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CronBackup.Core.Configuration;

namespace CronBackup.Core.Execution;

public enum NotificationPhase
{
    Start,
    Success,
    Failure,
}

public sealed record NotificationMessage(
    string JobName,
    bool Success,
    string ArchiveName,
    IReadOnlyList<string> Destinations,
    string? Error,
    DateTimeOffset Timestamp);

public sealed record NotificationResult(string Kind, bool Success, string? Error);

public interface INotifier
{
    Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyList<NotifyConfig>? targets,
        NotificationPhase phase,
        NotificationMessage message,
        CancellationToken cancellationToken);
}

public sealed class HttpNotifier : INotifier
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _httpClient;

    public HttpNotifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyList<NotifyConfig>? targets,
        NotificationPhase phase,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        if (targets is null || targets.Count == 0)
        {
            return Array.Empty<NotificationResult>();
        }

        var results = new List<NotificationResult>();
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                results.Add(new NotificationResult(target.Kind, false, "url is empty"));
                continue;
            }

            if (phase == NotificationPhase.Start &&
                !target.Kind.Equals("healthchecks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                results.Add(await SendOneAsync(target, phase, message, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // 通知是旁路：单个目标失败只返回结果，不让调用方把备份判成失败。
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                results.Add(new NotificationResult(target.Kind, false, exception.Message));
            }
        }

        return results;
    }

    private async Task<NotificationResult> SendOneAsync(
        NotifyConfig target,
        NotificationPhase phase,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(RequestTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        using var request = BuildRequest(target, phase, message);
        using var response = await _httpClient.SendAsync(request, linkedSource.Token);
        if (!response.IsSuccessStatusCode)
        {
            return new NotificationResult(
                target.Kind,
                false,
                $"HTTP {(int)response.StatusCode}");
        }

        return new NotificationResult(target.Kind, true, null);
    }

    private static HttpRequestMessage BuildRequest(
        NotifyConfig target,
        NotificationPhase phase,
        NotificationMessage message)
    {
        // healthchecks.io 用 URL 后缀表示阶段：/start、/fail，成功直接打基础 URL。
        if (target.Kind.Equals("healthchecks", StringComparison.OrdinalIgnoreCase))
        {
            var url = target.Url!;
            if (phase == NotificationPhase.Start)
            {
                url = AppendPath(url, "start");
            }
            else if (phase == NotificationPhase.Failure)
            {
                url = AppendPath(url, "fail");
            }

            return new HttpRequestMessage(HttpMethod.Get, url);
        }

        if (target.Kind.Equals("ntfy", StringComparison.OrdinalIgnoreCase))
        {
            var url = string.IsNullOrWhiteSpace(target.Topic)
                ? target.Url!
                : AppendPath(target.Url!, target.Topic);

            var text = message.Success
                ? $"cron-backup {message.JobName}: success\narchive: {message.ArchiveName}\ndestinations: {string.Join(", ", message.Destinations)}"
                : $"cron-backup {message.JobName}: FAILED\narchive: {message.ArchiveName}\nerror: {message.Error}";

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(text, Encoding.UTF8, "text/plain"),
            };
            request.Headers.TryAddWithoutValidation(
                "Title",
                $"cron-backup {message.JobName}: {(message.Success ? "success" : "FAILED")}");
            request.Headers.TryAddWithoutValidation(
                "Priority",
                message.Success ? "default" : "high");
            return request;
        }

        var payload = new
        {
            job = message.JobName,
            status = message.Success ? "success" : "failure",
            archive = message.ArchiveName,
            destinations = message.Destinations,
            error = message.Error,
            timestamp = message.Timestamp,
        };

        return new HttpRequestMessage(HttpMethod.Post, target.Url)
        {
            Content = JsonContent.Create(payload),
        };
    }

    private static string AppendPath(string url, string segment)
    {
        var builder = new UriBuilder(url);
        var path = builder.Path.TrimEnd('/');
        builder.Path = $"{path}/{segment.Trim('/')}";
        return builder.Uri.ToString();
    }
}
