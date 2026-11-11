using System.Text;
using CronBackup.Core.Configuration;

namespace CronBackup.Core.Rclone;

public sealed class RcloneConfRenderer
{
    private static readonly HashSet<string> KnownSecretOptions =
        new(StringComparer.OrdinalIgnoreCase) { "pass", "password", "client_secret" };

    public async Task<RcloneConfRenderResult> RenderAsync(
        RootConfig config,
        IRcloneSecretObscurer obscurer,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var destinations = new List<RenderedRcloneDestination>();
        var usedSectionNames = new HashSet<string>(StringComparer.Ordinal);

        // section 名由 job/destination 生成并去重，用户不需要知道 rclone remote 名；
        // 同一个 conf 里的所有 section 在进程退出时一起删除。
        foreach (var job in config.Jobs.Where(job => job.IsEnabled))
        {
            foreach (var destination in job.Destinations)
            {
                var kind = destination.Kind.ToLowerInvariant();
                if (kind == "local")
                {
                    continue;
                }

                if (kind is not ("s3" or "sftp" or "rclone"))
                {
                    throw new ConfigException(
                        $"jobs.{job.Name}.destinations.{destination.Name}.kind",
                        $"cannot render rclone backend for kind '{destination.Kind}'");
                }

                var sectionName = BuildSectionName(job.Name, destination.Name, usedSectionNames);
                content.Append('[').Append(sectionName).AppendLine("]");

                switch (kind)
                {
                    case "s3":
                        RenderS3(content, destination);
                        break;
                    case "sftp":
                        await RenderSftpAsync(content, destination, obscurer, cancellationToken);
                        break;
                    case "rclone":
                        await RenderRcloneAsync(content, destination, obscurer, cancellationToken);
                        break;
                }

                content.AppendLine();
                destinations.Add(new RenderedRcloneDestination(
                    job.Name,
                    destination.Name,
                    sectionName,
                    BuildBasePath(kind, sectionName, destination)));
            }
        }

        return new RcloneConfRenderResult(content.ToString(), destinations);
    }

    private static void RenderS3(StringBuilder content, DestinationConfig destination)
    {
        Append(content, "type", "s3");
        Append(content, "provider", destination.Provider ?? "AWS");
        Append(content, "env_auth", (destination.EnvAuth ?? false) ? "true" : "false");

        if (destination.EnvAuth != true)
        {
            Append(content, "access_key_id", destination.AccessKeyId);
            Append(content, "secret_access_key", destination.SecretAccessKey);
        }

        Append(content, "region", destination.Region);
        Append(content, "endpoint", destination.Endpoint);
        Append(content, "acl", destination.Acl);
        Append(content, "no_check_bucket", (destination.NoCheckBucket ?? true) ? "true" : "false");
    }

    private static async Task RenderSftpAsync(
        StringBuilder content,
        DestinationConfig destination,
        IRcloneSecretObscurer obscurer,
        CancellationToken cancellationToken)
    {
        Append(content, "type", "sftp");
        Append(content, "host", destination.Host);
        Append(content, "user", destination.User);
        Append(content, "port", (destination.Port ?? 22).ToString());

        // rclone 的 SFTP pass 必须是 obscured 值，明文会让它解密失败；
        // 通过 stdin 调 rclone obscure，避免密码出现在进程参数里。
        if (!string.IsNullOrEmpty(destination.Password))
        {
            var obscured = await obscurer.ObscureAsync(destination.Password, cancellationToken);
            Append(content, "pass", obscured);
        }

        Append(content, "key_file", destination.KeyFile);
    }

    private static async Task RenderRcloneAsync(
        StringBuilder content,
        DestinationConfig destination,
        IRcloneSecretObscurer obscurer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.Type))
        {
            throw new ConfigException("rclone.type", "rclone destination requires a type");
        }

        Append(content, "type", destination.Type);
        if (destination.Options is null)
        {
            return;
        }

        // 逃生口：type + options 原样透传，但已知密码字段和用户显式列出的字段
        // 仍然走 obscure，避免把明文写进临时 conf。
        var explicitlyObscured = new HashSet<string>(
            destination.Obscure ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var option in destination.Options)
        {
            if (string.Equals(option.Key, "type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = option.Value;
            if (explicitlyObscured.Contains(option.Key) || KnownSecretOptions.Contains(option.Key))
            {
                value = await obscurer.ObscureAsync(value, cancellationToken);
            }

            Append(content, option.Key, value);
        }
    }

    // S3 的 root 是 bucket 内的 key 前缀，不能带前导斜杠；SFTP/rclone 的 root
    // 保留用户写的绝对/相对形式，后续只补对象 key。
    private static string BuildBasePath(
        string kind,
        string sectionName,
        DestinationConfig destination)
    {
        if (kind == "s3")
        {
            var bucket = destination.Bucket?.Trim('/') ?? string.Empty;
            if (bucket.Length == 0)
            {
                throw new ConfigException("rclone.s3.bucket", "s3 destination requires a bucket");
            }

            var s3Root = destination.Root?.Trim('/') ?? string.Empty;
            return s3Root.Length == 0
                ? $"{sectionName}:{bucket}"
                : $"{sectionName}:{bucket}/{s3Root}";
        }

        var root = destination.Root?.TrimEnd('/') ?? string.Empty;
        if (root == "/")
        {
            root = string.Empty;
        }

        return root.Length == 0
            ? $"{sectionName}:"
            : $"{sectionName}:{root}";
    }

    private static string BuildSectionName(
        string jobName,
        string destinationName,
        HashSet<string> usedSectionNames)
    {
        var baseName = $"cb_{Sanitize(jobName)}_{Sanitize(destinationName)}";
        var candidate = baseName;
        var counter = 2;
        while (!usedSectionNames.Add(candidate))
        {
            candidate = $"{baseName}_{counter}";
            counter++;
        }

        return candidate;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        var sanitized = builder.ToString().Trim('_');
        if (sanitized.Length == 0)
        {
            return "job";
        }

        return sanitized.Length > 48 ? sanitized[..48] : sanitized;
    }

    private static void Append(StringBuilder content, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ConfigException($"rclone.{key}", "value cannot contain a newline");
        }

        content.Append(key).Append(" = ").Append(value).AppendLine();
    }
}
