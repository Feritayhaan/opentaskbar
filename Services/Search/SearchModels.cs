using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace OpenTaskbar.Services.Search;

public enum SearchCategory
{
    TopHit,
    Calculator,
    Applications,
    System,
    Files,
    Web
}

public sealed class SearchResult : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public SearchCategory Category { get; set; }
    public string GroupName { get; set; } = "";
    /// <summary>Stable identity used for usage ranking.</summary>
    public required string Key { get; init; }
    public double Score { get; set; }
    /// <summary>Runs the primary action. Returns false when the search window should stay open.</summary>
    public required Func<bool> Execute { get; init; }
    public Action? Reveal { get; init; }
    /// <summary>Shell parsing name used to load the icon lazily.</summary>
    public string? IconSource { get; init; }
    public string? IconCacheKey { get; init; }
    public Geometry? Glyph { get; init; }
    public Brush? GlyphBackground { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowGlyph)));
        }
    }

    public bool ShowGlyph => _icon is null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Precomputed, diacritic-insensitive representation of a searchable name.</summary>
public sealed class SearchText
{
    public SearchText(string text)
    {
        Normalized = TextMatcher.Normalize(text);
        Words = Normalized.Split([' ', '-', '_', '.', '(', ')', '[', ']', '/', '\\', ','], StringSplitOptions.RemoveEmptyEntries);
        Initials = string.Concat(Words.Select(word => word[0]));
    }

    public string Normalized { get; }
    public string[] Words { get; }
    public string Initials { get; }
}

public static class TextMatcher
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(character);
            builder.Append(lower switch
            {
                'ı' => 'i',
                'ß' => 's',
                _ => lower
            });
        }

        return builder.ToString().Trim();
    }

    /// <summary>Scores how well the normalized query matches the candidate. 0 means no match.</summary>
    public static double Score(string query, string[] queryTokens, SearchText candidate)
    {
        var text = candidate.Normalized;
        if (query.Length == 0 || text.Length == 0) return 0;
        if (text == query) return 1000;
        if (text.StartsWith(query, StringComparison.Ordinal)) return 900 - Math.Min(100, (text.Length - query.Length) * 2);

        foreach (var word in candidate.Words)
        {
            if (word.StartsWith(query, StringComparison.Ordinal)) return 760 - Math.Min(60, word.Length - query.Length);
        }

        if (query.Length >= 2 && candidate.Initials.StartsWith(query, StringComparison.Ordinal)) return 720;

        if (queryTokens.Length > 1 && queryTokens.All(token =>
                candidate.Words.Any(word => word.StartsWith(token, StringComparison.Ordinal))))
            return 680;

        var index = text.IndexOf(query, StringComparison.Ordinal);
        if (index >= 0) return 600 - Math.Min(100, index * 3);

        if (query.Length >= 3)
        {
            var subsequence = SubsequenceScore(query, text);
            if (subsequence > 0) return 250 + subsequence;
        }

        return 0;
    }

    private static double SubsequenceScore(string query, string text)
    {
        var position = -1;
        var gaps = 0;
        foreach (var character in query)
        {
            if (character == ' ') continue;
            var next = text.IndexOf(character, position + 1);
            if (next < 0) return 0;
            if (position >= 0) gaps += next - position - 1;
            position = next;
        }

        var compactness = 1.0 - Math.Min(1.0, gaps / (double)Math.Max(1, text.Length));
        return compactness < 0.45 ? 0 : compactness * 150;
    }
}
