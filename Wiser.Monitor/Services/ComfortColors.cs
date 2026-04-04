namespace Wiser.Monitor.Services;

/// <summary>
/// Tailwind-style 500 palette: warm (red→yellow) for above band, cold (lime→blue) for below, green in band.
/// </summary>
public static class ComfortColors
{
    public const string Red500 = "#ef4444";
    public const string Orange500 = "#f97316";
    public const string Amber500 = "#f59e0b";
    public const string Yellow500 = "#eab308";
    public const string Lime500 = "#84cc16";
    public const string Green500 = "#22c55e";
    public const string Teal500 = "#14b8a6";
    public const string Blue500 = "#3b82f6";

    /// <summary>Maps temperature vs recommended band to a fill color (floorplan pins, tiles, charts).</summary>
    public static string GetGradientColor(double avgTemp, double targetMin, double targetMax)
    {
        if (avgTemp > targetMax)
        {
            var t = Math.Clamp((avgTemp - targetMax) / 4.0, 0.0, 1.0);
            return LerpFourStops(t, Yellow500, Amber500, Orange500, Red500);
        }

        if (avgTemp < targetMin)
        {
            var t = Math.Clamp((targetMin - avgTemp) / 4.0, 0.0, 1.0);
            return LerpFourStops(t, Lime500, Green500, Teal500, Blue500);
        }

        return Green500;
    }

    public static (string Text, string Color) GetCurrentState(double currentTemp, double targetMin, double targetMax)
    {
        if (currentTemp > targetMax)
            return ("Now: too warm", Red500);
        if (currentTemp < targetMin)
            return ("Now: too cold", Blue500);
        return ("Now: in range", Green500);
    }

    private static string LerpFourStops(double t, string c0, string c1, string c2, string c3)
    {
        if (t <= 1.0 / 3.0)
            return LerpHex(c0, c1, t * 3.0);
        if (t <= 2.0 / 3.0)
            return LerpHex(c1, c2, (t - 1.0 / 3.0) * 3.0);
        return LerpHex(c2, c3, (t - 2.0 / 3.0) * 3.0);
    }

    private static string LerpHex(string a, string b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static int Chan(string h, int offset) => Convert.ToInt32(h.Substring(offset, 2), 16);
        var r = (int)Math.Round(Chan(a, 1) + (Chan(b, 1) - Chan(a, 1)) * t);
        var g = (int)Math.Round(Chan(a, 3) + (Chan(b, 3) - Chan(a, 3)) * t);
        var bl = (int)Math.Round(Chan(a, 5) + (Chan(b, 5) - Chan(a, 5)) * t);
        return $"#{r:x2}{g:x2}{bl:x2}";
    }
}
