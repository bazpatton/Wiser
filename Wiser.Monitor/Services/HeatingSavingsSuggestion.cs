namespace Wiser.Monitor.Services;

public enum HeatingSavingsSuggestionLevel
{
    Info,
    Warning,
}

public sealed record HeatingSavingsSuggestion(
    string Title,
    string Body,
    string ActionHref,
    string ActionText,
    HeatingSavingsSuggestionLevel Level);
