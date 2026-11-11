using System.Text;

namespace CronBackup.Core.Configuration;

public sealed class EnvInterpolator
{
    private readonly IReadOnlyDictionary<string, string?> _environment;

    public EnvInterpolator(IReadOnlyDictionary<string, string?> environment)
    {
        _environment = environment;
    }

    public string Interpolate(string value, string fieldPath)
    {
        if (value.IndexOf('$') < 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '$')
            {
                result.Append(current);
                continue;
            }

            if (index + 2 < value.Length && value[index + 1] == '$' && value[index + 2] == '{')
            {
                // $${VAR} is an escaped placeholder: emit a marker so the literal
                // ${VAR} is not expanded, then restore the dollar sign at the end.
                result.Append('\u0001');
                index++;
                continue;
            }

            if (index + 1 >= value.Length || value[index + 1] != '{')
            {
                result.Append(current);
                continue;
            }

            var end = value.IndexOf('}', index + 2);
            if (end < 0)
            {
                throw new ConfigException(fieldPath, "unterminated environment placeholder");
            }

            var token = value.Substring(index + 2, end - index - 2);
            var separator = token.IndexOf(":-", StringComparison.Ordinal);
            var variableName = separator < 0 ? token : token[..separator];
            var defaultValue = separator < 0 ? null : token[(separator + 2)..];

            if (!IsValidVariableName(variableName))
            {
                throw new ConfigException(fieldPath, $"invalid environment variable name '{variableName}'");
            }

            if (_environment.TryGetValue(variableName, out var environmentValue) &&
                !string.IsNullOrEmpty(environmentValue))
            {
                result.Append(environmentValue);
            }
            else if (defaultValue is not null)
            {
                result.Append(defaultValue);
            }
            else
            {
                throw new ConfigException(fieldPath, $"environment variable '{variableName}' is not set");
            }

            index = end;
        }

        return result.Replace('\u0001', '$').ToString();
    }

    private static bool IsValidVariableName(string name)
    {
        if (name.Length == 0 || (!char.IsAsciiLetter(name[0]) && name[0] != '_'))
        {
            return false;
        }

        return name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}
