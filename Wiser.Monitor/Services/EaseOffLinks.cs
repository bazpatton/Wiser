using System.Globalization;

namespace Wiser.Monitor.Services;

/// <summary>
/// Room links that pre-fill an ease-off, e.g. <c>/rooms/living-room?ease=1-60</c> or <c>?ease=off-next</c>.
/// The preset is spelled out in the link (not an index) so old notifications still mean what they said.
/// The Rooms page always asks for confirmation; opening a link never changes the heating by itself.
/// </summary>
public static class EaseOffLinks
{
    private const string OffToken = "off";
    private const string NextToken = "next";

    public static string ToQueryValue(EaseOffPreset preset)
    {
        var amount = preset.TurnOff ? OffToken : preset.DropC.ToString("0.#", CultureInfo.InvariantCulture);
        var duration = preset.UntilNextChange ? NextToken : preset.Minutes.ToString(CultureInfo.InvariantCulture);
        return $"{amount}-{duration}";
    }

    public static EaseOffPreset? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Trim().Split('-', 2);
        if (parts.Length != 2)
            return null;

        var turnOff = parts[0].Equals(OffToken, StringComparison.OrdinalIgnoreCase);
        var drop = 0.0;
        if (!turnOff && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out drop))
            return null;

        int minutes;
        if (parts[1].Equals(NextToken, StringComparison.OrdinalIgnoreCase))
            minutes = QuickActionPresets.UntilNextChange;
        else if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)
                 || minutes < QuickActionPresets.MinMinutes)
            return null;

        // Normalize clamps the values to what the presets editor allows.
        var normalized = QuickActionPresets.Normalize(
            new QuickActionPresets(QuickActionPresets.Default.Boosts, [new EaseOffPreset(drop, minutes, turnOff)], 0),
            0);
        return normalized.EaseOffs[0];
    }

    /// <summary>ASCII-only button label (ntfy action headers can't carry "°" or "½"), e.g. "Lower 1C for 1h".</summary>
    public static string ActionLabel(EaseOffPreset preset)
    {
        var duration = preset.UntilNextChange
            ? "until next change"
            : preset.Minutes % 60 == 0 ? $"for {preset.Minutes / 60}h" : $"for {preset.Minutes}min";
        return preset.TurnOff
            ? $"Heating off {duration}"
            : $"Lower {preset.DropC.ToString("0.#", CultureInfo.InvariantCulture)}C {duration}";
    }

    /// <summary>
    /// ntfy <c>Actions</c> header value: one "view" button per preset (ntfy shows at most 3).
    /// Null when <c>APP_PUBLIC_BASE_URL</c> is unset or there are no presets.
    /// </summary>
    public static string? NtfyActions(string? appPublicBaseUrl, string roomName, IEnumerable<EaseOffPreset> presets)
    {
        var actions = presets
            .Take(3)
            .Select(p => Build(appPublicBaseUrl, roomName, p) is { } url ? $"view, {ActionLabel(p)}, {url}, clear=true" : null)
            .OfType<string>()
            .ToList();
        return actions.Count == 0 ? null : string.Join("; ", actions);
    }

    /// <summary>Absolute link for an ntfy action; null when <c>APP_PUBLIC_BASE_URL</c> is unset.</summary>
    public static string? Build(string? appPublicBaseUrl, string roomName, EaseOffPreset preset)
    {
        if (string.IsNullOrWhiteSpace(appPublicBaseUrl))
            return null;
        var b = appPublicBaseUrl.Trim().TrimEnd('/');
        return $"{b}{RoomLinkHelper.RoomsPagePath(roomName)}?ease={Uri.EscapeDataString(ToQueryValue(preset))}";
    }

    /// <summary>Absolute link to the room page; null when <c>APP_PUBLIC_BASE_URL</c> is unset.</summary>
    public static string? RoomPage(string? appPublicBaseUrl, string roomName) =>
        string.IsNullOrWhiteSpace(appPublicBaseUrl)
            ? null
            : $"{appPublicBaseUrl.Trim().TrimEnd('/')}{RoomLinkHelper.RoomsPagePath(roomName)}";
}
