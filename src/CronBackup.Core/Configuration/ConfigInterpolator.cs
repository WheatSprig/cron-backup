using System.Collections;
using System.Reflection;

namespace CronBackup.Core.Configuration;

public static class ConfigInterpolator
{
    public static void Interpolate(RootConfig config, EnvInterpolator interpolator)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(config, "config", interpolator, visited);
    }

    private static void Walk(object? node, string path, EnvInterpolator interpolator, HashSet<object> visited)
    {
        if (node is null || node is string || node.GetType().IsPrimitive || node.GetType().IsEnum)
        {
            return;
        }

        if (!node.GetType().IsValueType && !visited.Add(node))
        {
            return;
        }

        if (node is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "?";
                var childPath = $"{path}.{key}";
                if (entry.Value is string text)
                {
                    dictionary[entry.Key!] = interpolator.Interpolate(text, childPath);
                }
                else
                {
                    Walk(entry.Value, childPath, interpolator, visited);
                }
            }

            return;
        }

        if (node is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                Walk(item, $"{path}[{index}]", interpolator, visited);
                index++;
            }

            return;
        }

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.SetMethod is null)
            {
                continue;
            }

            var childPath = $"{path}.{property.Name}";
            var value = property.GetValue(node);
            if (value is string text)
            {
                var interpolated = interpolator.Interpolate(text, childPath);
                if (!string.Equals(text, interpolated, StringComparison.Ordinal))
                {
                    property.SetValue(node, interpolated);
                }
            }
            else
            {
                Walk(value, childPath, interpolator, visited);
            }
        }
    }
}
