using System;
using System.Globalization;
using System.Text;

namespace VoxelRush.Core.Diagnostics
{
    /// <summary>Formats logfmt-style structured log lines: <c>event=name key=value ...</c>.</summary>
    public static class LogEvent
    {
        public static string Format(string eventName, params (string Key, object? Value)[] fields)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("Event name is required.", nameof(eventName));
            }

            var builder = new StringBuilder(64);
            builder.Append("event=");
            AppendValue(builder, eventName);

            foreach (var (key, value) in fields)
            {
                if (string.IsNullOrWhiteSpace(key) || !IsBareToken(key))
                {
                    throw new ArgumentException($"Invalid log field key '{key}'.", nameof(fields));
                }

                builder.Append(' ').Append(key).Append('=');
                AppendValue(builder, value);
            }

            return builder.ToString();
        }

        private static void AppendValue(StringBuilder builder, object? value)
        {
            string text = value switch
            {
                null => "null",
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };

            if (text.Length > 0 && IsBareToken(text))
            {
                builder.Append(text);
                return;
            }

            // Quote and escape so values with spaces or newlines cannot forge extra fields or lines.
            builder.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(c); break;
                }
            }

            builder.Append('"');
        }

        private static bool IsBareToken(string text)
        {
            foreach (char c in text)
            {
                if (c <= ' ' || c == '"' || c == '=' || c == '\\' || c > '~')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
