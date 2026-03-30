using System.Text.Json;
using Wiser.Control.Models;

namespace Wiser.Control.Services;

public sealed class TrendBucket
{
	public DateTimeOffset AtUtc { get; set; }
	public double? MeasuredC { get; set; }
	public double? TargetC { get; set; }
}

public sealed class RoomTrendSeries
{
	public int RoomId { get; set; }
	public string RoomName { get; set; } = "";
	public List<TrendBucket> Buckets { get; set; } = [];
	public DateTimeOffset? LatestSampleUtc { get; set; }
	public double? LatestMeasuredC { get; set; }
	public double? LatestTargetC { get; set; }
}

internal sealed class TrendSample
{
	public DateTimeOffset AtUtc { get; set; }
	public int RoomId { get; set; }
	public string RoomName { get; set; } = "";
	public double? MeasuredC { get; set; }
	public double? TargetC { get; set; }
}

public static class TemperatureTrendStore
{
	private const string KeySamples = "temperature_trend_samples_v1";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly object Gate = new();

	public static void AppendFromDomain(WiserDomainPayload payload, DateTimeOffset atUtc)
	{
		if (payload.Room is null || payload.Room.Count == 0)
			return;

		lock (Gate)
		{
			var all = LoadAllUnsafe();
			foreach (var room in payload.Room)
			{
				var measuredTenths = room.CalculatedTemperature ?? room.DisplayedTemperature;
				var targetTenths = room.CurrentSetPoint ?? room.ScheduledSetPoint;
				all.Add(new TrendSample
				{
					AtUtc = atUtc.ToUniversalTime(),
					RoomId = room.Id,
					RoomName = string.IsNullOrWhiteSpace(room.Name) ? $"Room {room.Id}" : room.Name!,
					MeasuredC = measuredTenths is null ? null : measuredTenths.Value / 10.0,
					TargetC = targetTenths is null ? null : targetTenths.Value / 10.0,
				});
			}

			var cutoff = atUtc.ToUniversalTime().AddHours(-48);
			all = all
				.Where(s => s.AtUtc >= cutoff)
				.OrderBy(s => s.AtUtc)
				.ToList();
			SaveAllUnsafe(all);
		}
	}

	public static List<RoomTrendSeries> LoadLast24HourSeries()
	{
		lock (Gate)
		{
			var all = LoadAllUnsafe();
			var endUtc = DateTimeOffset.UtcNow;
			var startUtc = endUtc.AddHours(-24);
			var hourlyBuckets = 24;
			var bucketSpan = TimeSpan.FromHours(1);

			var recent = all.Where(s => s.AtUtc >= startUtc && s.AtUtc <= endUtc).ToList();
			var grouped = recent.GroupBy(s => s.RoomId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.AtUtc).ToList());

			var series = new List<RoomTrendSeries>();
			foreach (var pair in grouped)
			{
				var roomSamples = pair.Value;
				var roomName = roomSamples.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.RoomName))?.RoomName ?? $"Room {pair.Key}";
				var buckets = new List<TrendBucket>(hourlyBuckets);

				for (var i = 0; i < hourlyBuckets; i++)
				{
					var bucketStart = startUtc.AddHours(i);
					var bucketEnd = bucketStart.Add(bucketSpan);
					var inBucket = roomSamples.Where(s => s.AtUtc >= bucketStart && s.AtUtc < bucketEnd).ToList();
					var last = inBucket.LastOrDefault();
					buckets.Add(new TrendBucket
					{
						AtUtc = bucketEnd,
						MeasuredC = last?.MeasuredC,
						TargetC = last?.TargetC,
					});
				}

				var latest = roomSamples.LastOrDefault();
				series.Add(new RoomTrendSeries
				{
					RoomId = pair.Key,
					RoomName = roomName,
					Buckets = buckets,
					LatestSampleUtc = latest?.AtUtc,
					LatestMeasuredC = latest?.MeasuredC,
					LatestTargetC = latest?.TargetC,
				});
			}

			return series
				.OrderBy(s => s.RoomName, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
	}

	private static List<TrendSample> LoadAllUnsafe()
	{
		try
		{
			var json = Preferences.Get(KeySamples, "");
			if (string.IsNullOrWhiteSpace(json))
				return [];
			return JsonSerializer.Deserialize<List<TrendSample>>(json, JsonOptions) ?? [];
		}
		catch
		{
			return [];
		}
	}

	private static void SaveAllUnsafe(List<TrendSample> items) =>
		Preferences.Set(KeySamples, JsonSerializer.Serialize(items, JsonOptions));
}
