namespace CronBackup.Core.Configuration;

public sealed class ConfigException : Exception
{
    public ConfigException(string fieldPath, string message)
        : base($"{fieldPath}: {message}")
    {
        FieldPath = fieldPath;
    }

    public ConfigException(string fieldPath, string message, Exception innerException)
        : base($"{fieldPath}: {message}", innerException)
    {
        FieldPath = fieldPath;
    }

    public string FieldPath { get; }
}
