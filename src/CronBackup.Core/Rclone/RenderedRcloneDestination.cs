namespace CronBackup.Core.Rclone;

public sealed record RenderedRcloneDestination(
    string JobName,
    string DestinationName,
    string SectionName,
    string BasePath)
{
    public string BuildObjectPath(string objectKey)
    {
        var key = objectKey.TrimStart('/');
        if (string.IsNullOrEmpty(BasePath))
        {
            return $"{SectionName}:{key}";
        }

        return BasePath.EndsWith(':')
            ? $"{BasePath}{key}"
            : $"{BasePath.TrimEnd('/')}/{key}";
    }
}

public sealed record RcloneConfRenderResult(
    string Content,
    IReadOnlyList<RenderedRcloneDestination> Destinations);
