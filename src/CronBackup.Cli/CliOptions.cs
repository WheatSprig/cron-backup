namespace CronBackup.Cli;

public sealed class CliOptions
{
    public string Command { get; init; } = "help";

    public string? ConfigPath { get; init; }

    public string? Job { get; init; }

    public string? Key { get; init; }

    public string? To { get; init; }

    public string? LogLevel { get; init; }

    public bool Json { get; init; }

    public bool DryRun { get; init; }

    public bool Online { get; init; }

    public bool Strict { get; init; }

    public bool Once { get; init; }

    public bool Apply { get; init; }

    public bool Latest { get; init; }

    public bool Extract { get; init; }

    public bool Help { get; init; }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions();
        }

        string? command = null;
        string? configPath = null;
        string? job = null;
        string? key = null;
        string? to = null;
        string? logLevel = null;
        var json = false;
        var dryRun = false;
        var online = false;
        var strict = false;
        var once = false;
        var apply = false;
        var latest = false;
        var extract = false;
        var help = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? Value()
            {
                if (index + 1 >= args.Length)
                {
                    throw new CliUsageException($"option '{argument}' requires a value");
                }

                index++;
                return args[index];
            }

            switch (argument)
            {
                case "-h":
                case "--help":
                    help = true;
                    break;
                case "--config":
                    configPath = Value();
                    break;
                case "--job":
                    job = Value();
                    break;
                case "--key":
                    key = Value();
                    break;
                case "--to":
                    to = Value();
                    break;
                case "--log-level":
                    logLevel = Value();
                    break;
                case "--json":
                    json = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--online":
                    online = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--once":
                    once = true;
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--latest":
                    latest = true;
                    break;
                case "--extract":
                    extract = true;
                    break;
                case "--version":
                    command ??= "version";
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliUsageException($"unknown option '{argument}'");
                    }

                    if (command is not null)
                    {
                        throw new CliUsageException($"unexpected argument '{argument}'");
                    }

                    command = argument;
                    break;
            }
        }

        return new CliOptions
        {
            Command = command ?? "help",
            ConfigPath = configPath,
            Job = job,
            Key = key,
            To = to,
            LogLevel = logLevel,
            Json = json,
            DryRun = dryRun,
            Online = online,
            Strict = strict,
            Once = once,
            Apply = apply,
            Latest = latest,
            Extract = extract,
            Help = help,
        };
    }
}

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}
