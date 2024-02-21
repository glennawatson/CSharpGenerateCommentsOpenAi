namespace CSharpGenerateCommentsOpenAi;

/// <summary>
/// Extension methods.
/// </summary>
internal static class Extensions
{
    /// <summary>
    /// Cap a inputted string to a max length.
    /// </summary>
    /// <param name="value">The value to cap.</param>
    /// <param name="maxLength">The maximum length to cut it off at.</param>
    /// <returns>The capped string.</returns>
    public static string WithMaxLength(this string value, int maxLength)
    {
        return value?.Substring(0, Math.Min(value.Length, maxLength)) ?? string.Empty;
    }
}
