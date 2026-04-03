using System.Globalization;

namespace Wiser.Monitor.Services;

public sealed record GasWeekInsightRow(
    int IsoYear,
    int IsoWeek,
    string WeekLabel,
    decimal EstimatedGasGbp,
    double? HddSum,
    double HeatMinutesSum,
    string Narrative);

public static class GasHeatingWeekInsights
{
    public static IReadOnlyList<GasWeekInsightRow> Build(
        IReadOnlyList<GasMeterReadingRow> readingsAnyOrder,
        GasMeterReceiptRow? latestReceipt,
        IReadOnlyList<DailySummaryRow> dailyOldestFirst,
        TimeZoneInfo zone,
        int numWeeks)
    {
        numWeeks = Math.Clamp(numWeeks, 4, 16);

        decimal? unitGbpPerVol = null;
        if (latestReceipt is { VolCredit: > 0 })
            unitGbpPerVol = latestReceipt.AmountGbp / latestReceipt.VolCredit;

        var readingsAsc = readingsAnyOrder.OrderBy(r => r.ReadTs).ToList();
        var gasByWeek = new Dictionary<(int Y, int W), decimal>();

        if (unitGbpPerVol is { } u && readingsAsc.Count >= 2)
        {
            for (var i = 1; i < readingsAsc.Count; i++)
            {
                var prev = readingsAsc[i - 1];
                var curr = readingsAsc[i];
                var delta = curr.ReadingValue - prev.ReadingValue;
                if (delta < 0)
                    continue;

                var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(curr.ReadTs), zone);
                var d = DateOnly.FromDateTime(local.DateTime);
                var dt = d.ToDateTime(TimeOnly.MinValue);
                var isoYear = ISOWeek.GetYear(dt);
                var isoWeek = ISOWeek.GetWeekOfYear(dt);
                var key = (isoYear, isoWeek);
                gasByWeek[key] = gasByWeek.GetValueOrDefault(key) + delta * u;
            }
        }

        var dailyMap = dailyOldestFirst.ToDictionary(x => x.Date, StringComparer.Ordinal);

        var nowLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var dtNow = nowLocal.ToDateTime(TimeOnly.MinValue);
        var startYear = ISOWeek.GetYear(dtNow);
        var startWeek = ISOWeek.GetWeekOfYear(dtNow);
        var anchorMonday = DateOnly.FromDateTime(ISOWeek.ToDateTime(startYear, startWeek, DayOfWeek.Monday));

        var rows = new List<GasWeekInsightRow>();
        for (var i = 0; i < numWeeks; i++)
        {
            var weekMonday = anchorMonday.AddDays(-7 * i);
            var mondayDt = weekMonday.ToDateTime(TimeOnly.MinValue);
            var y = ISOWeek.GetYear(mondayDt);
            var wk = ISOWeek.GetWeekOfYear(mondayDt);

            double? hddSum = 0;
            var hasHdd = false;
            double heatSum = 0;
            for (var d = 0; d < 7; d++)
            {
                var day = weekMonday.AddDays(d);
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!dailyMap.TryGetValue(key, out var dr))
                    continue;
                if (dr.Hdd is { } h)
                {
                    hddSum += h;
                    hasHdd = true;
                }

                heatSum += dr.HeatingActiveEstimateMin;
            }

            var gasGbp = gasByWeek.GetValueOrDefault((y, wk));
            var label = $"ISO {y}-W{wk:D2} (from {weekMonday:dd/MM})";

            rows.Add(new GasWeekInsightRow(
                y,
                wk,
                label,
                gasGbp,
                hasHdd ? hddSum : null,
                heatSum,
                string.Empty));
        }

        var medianGas = Median(rows.Select(r => r.EstimatedGasGbp).Where(x => x > 0).ToList());
        var medianHdd = MedianNullable(rows.Where(r => r.HddSum is not null).Select(r => r.HddSum!.Value).ToList());

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var narrative = BuildNarrative(r, medianGas, medianHdd);
            rows[i] = r with { Narrative = narrative };
        }

        return rows;
    }

    private static string BuildNarrative(GasWeekInsightRow r, decimal medianGas, double? medianHdd)
    {
        if (r.HddSum is null)
            return "No HDD data (configure Open-Meteo lat/lon for outdoor series).";

        if (r.EstimatedGasGbp <= 0 && r.HeatMinutesSum < 1)
            return "Little heating activity and no meter interval attributed to this week.";

        if (r.EstimatedGasGbp <= 0)
            return "Heating ran but no gas cost for this week (add meter readings spanning this period).";

        var parts = new List<string>();
        if (medianHdd is { } mh && r.HddSum > mh * 1.2)
            parts.Add("Colder than typical weeks (high HDD).");
        else if (medianHdd is { } mh2 && r.HddSum < mh2 * 0.8)
            parts.Add("Milder week (lower HDD).");

        if (medianGas > 0 && r.EstimatedGasGbp > medianGas * 1.35m)
            parts.Add("Gas spend above your recent median for these weeks.");
        else if (medianGas > 0 && r.EstimatedGasGbp < medianGas * 0.65m && r.EstimatedGasGbp > 0)
            parts.Add("Gas spend below your recent median.");

        if (r.HeatMinutesSum > 600)
            parts.Add("Boiler/heating-active estimate is high.");

        return parts.Count > 0 ? string.Join(" ", parts) : "Typical week vs recent window.";
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2m : values[mid];
    }

    private static double? MedianNullable(List<double> values)
    {
        if (values.Count == 0)
            return null;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2.0 : values[mid];
    }
}
