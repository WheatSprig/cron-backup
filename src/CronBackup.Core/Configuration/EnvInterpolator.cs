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

            // $${VAR} 是转义写法：写入一个临时标记，避免第二轮再被识别为占位符，
            // 最后统一把标记换回 '$'。Docker Compose 场景依赖这条规则。
            if (index + 2 < value.Length && value[index + 1] == '$' && value[index + 2] == '{')
            {
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

            // 与 shell 的 ${VAR:-default} 对齐：变量未设置或为空串时都取默认值。
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
