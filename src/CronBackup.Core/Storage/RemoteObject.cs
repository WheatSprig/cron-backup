namespace CronBackup.Core.Storage;

public sealed record RemoteObject(string Key, long Size, DateTimeOffset Modified);
