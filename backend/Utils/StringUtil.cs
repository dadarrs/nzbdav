namespace NzbWebDAV.Utils;

public static class StringUtil
{
    public static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string TruncateToLength(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}