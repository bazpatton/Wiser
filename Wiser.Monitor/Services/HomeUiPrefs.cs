using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace Wiser.Monitor.Services;

/// <summary>
/// Per-browser Home choices kept in a cookie, so the server renders the page in its final state on first paint
/// (localStorage is only readable after render). Null fields mean "not set"; the page keeps its default.
/// </summary>
public sealed record HomeUiPrefs(
    bool? QuickCollapsed = null,
    double? BoostTempC = null,
    int? BoostMinutes = null,
    bool? AdjustMode = null,
    double? AdjustDeltaC = null)
{
    public const string CookieName = "wiser_home";
    public const int CookieDays = 400;

    public bool IsEmpty => this == new HomeUiPrefs();

    /// <summary>Parses "c=1&amp;t=21&amp;m=30&amp;a=0&amp;d=-1"; bad or out-of-range values are ignored.</summary>
    public static HomeUiPrefs Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new HomeUiPrefs();

        // The browser writes it with encodeURIComponent; values never contain '%' themselves, so unescaping is safe.
        value = Uri.UnescapeDataString(value);
        var q = QueryHelpers.ParseQuery(value.StartsWith('?') ? value : "?" + value);
        string? Get(string key) => q.TryGetValue(key, out var v) ? v.ToString() : null;

        return new HomeUiPrefs(
            QuickCollapsed: ParseBool(Get("c")),
            BoostTempC: ParseDouble(Get("t")) is { } t && t is >= QuickActionPresets.MinTempC and <= QuickActionPresets.MaxTempC ? t : null,
            BoostMinutes: ParseInt(Get("m")) is { } m
                && (m == QuickActionPresets.UntilNextChange || m is >= QuickActionPresets.MinMinutes and <= QuickActionPresets.MaxMinutes)
                ? m
                : null,
            AdjustMode: ParseBool(Get("a")),
            AdjustDeltaC: ParseDouble(Get("d")) is { } d && Math.Abs(d) is >= 0.5 and <= QuickActionPresets.MaxDropC ? d : null);
    }

    public string Serialize()
    {
        var parts = new List<string>();
        if (QuickCollapsed is { } c)
            parts.Add($"c={(c ? 1 : 0)}");
        if (BoostTempC is { } t)
            parts.Add($"t={t.ToString(CultureInfo.InvariantCulture)}");
        if (BoostMinutes is { } m)
            parts.Add($"m={m.ToString(CultureInfo.InvariantCulture)}");
        if (AdjustMode is { } a)
            parts.Add($"a={(a ? 1 : 0)}");
        if (AdjustDeltaC is { } d)
            parts.Add($"d={d.ToString(CultureInfo.InvariantCulture)}");
        return string.Join('&', parts);
    }

    private static bool? ParseBool(string? s) => s switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    private static double? ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
