using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wiser.Control.Models;

namespace Wiser.Control.Services;

public sealed class WiserHubClient : IDisposable
{
	public const int TempMinimumC = 5;
	public const int TempMaximumC = 30;
	public const int TempOffC = -20;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	private static readonly JsonSerializerOptions PatchJsonWrite = new()
	{
		PropertyNamingPolicy = null,
	};

	/// <summary>UTF-8 without BOM — some hubs and WinHTTP reject BOM-prefixed JSON or strict charset parsing.</summary>
	private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private readonly HttpClient _http;
	private readonly string _hubIp;
	private readonly string _secret;

	public WiserHubClient(WiserConnection connection, HttpMessageHandler? handler = null)
	{
		_hubIp = connection.HubIp;
		_secret = connection.Secret;
		_http = handler is null ? new HttpClient() : new HttpClient(handler);
		_http.Timeout = TimeSpan.FromSeconds(30);
	}

	public void Dispose() => _http.Dispose();

	private HttpRequestMessage DomainGet() =>
		new(HttpMethod.Get, $"http://{_hubIp}/data/domain/");

	private void ApplyHeaders(HttpRequestMessage request)
	{
		request.Headers.TryAddWithoutValidation("SECRET", _secret);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
	}

	public async Task<WiserDomainPayload> RefreshDomainAsync(CancellationToken cancellationToken = default)
	{
		return await WithTransientRetry(
			async ct =>
			{
				using var request = DomainGet();
				ApplyHeaders(request);
				using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
				await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
				// Buffer full body — avoids flaky "error while copying content to a stream" on some Android stacks when using ReadAsStreamAsync + JsonSerializer.DeserializeAsync.
				var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
				if (bytes.Length == 0)
					return new WiserDomainPayload();
				return JsonSerializer.Deserialize<WiserDomainPayload>(bytes, JsonOptions) ?? new WiserDomainPayload();
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async Task<JsonDocument?> RefreshNetworkAsync(CancellationToken cancellationToken = default)
	{
		return await WithTransientRetry(
			async ct =>
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{_hubIp}/data/network/");
				ApplyHeaders(request);
				using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
				await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
				var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
				var ascii = Encoding.ASCII.GetString(bytes);
				var cleaned = Regex.Replace(ascii, @"[^\x20-\x7F]", "");
				if (string.IsNullOrWhiteSpace(cleaned))
					return null;
				return JsonDocument.Parse(cleaned);
			},
			cancellationToken).ConfigureAwait(false);
	}

	public Task SetRoomTemperatureAsync(int roomId, double temperatureC, CancellationToken cancellationToken = default)
	{
		if (!IsValidTemperature(temperatureC))
			throw new ArgumentOutOfRangeException(nameof(temperatureC));

		var body = new
		{
			RequestOverride = new { Type = "Manual", SetPoint = ToWiserTemp(temperatureC) },
		};
		return PatchJsonAsync($"http://{_hubIp}/data/domain/Room/{roomId}", body, cancellationToken);
	}

	public async Task SetRoomModeAsync(WiserDomainPayload snapshot, int roomId, string mode, double boostTempC = 20, int boostMinutes = 30, CancellationToken cancellationToken = default)
	{
		var room = snapshot.Room?.FirstOrDefault(r => r.Id == roomId)
			?? throw new InvalidOperationException("Room not found in snapshot.");

		mode = mode.Trim();
		var m = mode.ToLowerInvariant();

		object patchData;
		if (m == "auto")
			patchData = new { Mode = "Auto" };
		else if (m == "boost")
		{
			if (boostTempC < TempMinimumC || boostTempC > TempMaximumC)
				throw new ArgumentOutOfRangeException(nameof(boostTempC));
			patchData = new
			{
				RequestOverride = new
				{
					Type = "Manual",
					DurationMinutes = boostMinutes,
					SetPoint = ToWiserTemp(boostTempC),
					Originator = "App",
				},
			};
		}
		else if (m == "manual")
		{
			if (string.Equals(room.Mode, "Auto", StringComparison.OrdinalIgnoreCase))
			{
				await PatchJsonAsync($"http://{_hubIp}/data/domain/Room/{roomId}", new { Mode = "Manual" }, cancellationToken).ConfigureAwait(false);
				snapshot = await RefreshDomainAsync(cancellationToken).ConfigureAwait(false);
				room = snapshot.Room?.FirstOrDefault(r => r.Id == roomId) ?? room;
			}

			var setTemp = FromWiserTemp(room.CurrentSetPoint ?? room.ScheduledSetPoint ?? TempMinimumC * 10);
			if (setTemp < TempMinimumC)
				setTemp = TempMinimumC;
			patchData = new { RequestOverride = new { Type = "Manual", SetPoint = ToWiserTemp(setTemp) } };
		}
		else if (m == "off")
		{
			if (string.Equals(room.Mode, "Auto", StringComparison.OrdinalIgnoreCase))
				await PatchJsonAsync($"http://{_hubIp}/data/domain/Room/{roomId}", new { Mode = "Manual" }, cancellationToken).ConfigureAwait(false);

			patchData = new { RequestOverride = new { Type = "Manual", SetPoint = ToWiserTemp(TempOffC) } };
		}
		else if (m == "auto_to_manual")
			patchData = new { Mode = "Manual" };
		else
			throw new ArgumentException("Mode must be auto, manual, off, or boost.", nameof(mode));

		if (m != "boost")
		{
			var cancelBoost = new
			{
				RequestOverride = new { Type = "None", DurationMinutes = 0, SetPoint = 0, Originator = "App" },
			};
			await PatchJsonAsync($"http://{_hubIp}/data/domain/Room/{roomId}", cancelBoost, cancellationToken).ConfigureAwait(false);
		}

		await PatchJsonAsync($"http://{_hubIp}/data/domain/Room/{roomId}", patchData, cancellationToken).ConfigureAwait(false);
	}

	public async Task SetHotWaterModeAsync(WiserDomainPayload snapshot, string mode, CancellationToken cancellationToken = default)
	{
		var hw = snapshot.HotWater?.FirstOrDefault()
			?? throw new InvalidOperationException("No hot water on this system.");

		mode = mode.Trim().ToLowerInvariant();
		object body = mode switch
		{
			"on" => new { RequestOverride = new { Type = "Manual", SetPoint = 1100 } },
			"off" => new { RequestOverride = new { Type = "Manual", SetPoint = -200 } },
			"auto" => new { RequestOverride = new { Type = "None", Mode = "Auto" } },
			_ => throw new ArgumentException("Hot water mode must be on, off, or auto.", nameof(mode)),
		};

		await PatchJsonAsync($"http://{_hubIp}/data/domain/HotWater/{hw.Id}/", body, cancellationToken).ConfigureAwait(false);
	}

	public async Task SetHomeAwayAsync(string mode, double? awayTemperatureC, CancellationToken cancellationToken = default)
	{
		mode = mode.Trim().ToUpperInvariant();
		object body = mode switch
		{
			"AWAY" when awayTemperatureC is null => throw new ArgumentException("Away temperature required.", nameof(awayTemperatureC)),
			"AWAY" when !IsValidTemperature(awayTemperatureC.Value) => throw new ArgumentOutOfRangeException(nameof(awayTemperatureC)),
			"AWAY" => new { type = 2, setPoint = ToWiserTemp(awayTemperatureC!.Value) },
			"HOME" => new { type = 0, setPoint = 0 },
			_ => throw new ArgumentException("Mode must be HOME or AWAY.", nameof(mode)),
		};

		await PatchJsonAsync($"http://{_hubIp}/data/domain/System/RequestOverride", body, cancellationToken).ConfigureAwait(false);
	}

	private async Task PatchJsonAsync(string url, object body, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Patch, url);
		ApplyHeaders(request);
		var json = JsonSerializer.Serialize(body, PatchJsonWrite);
		// Use plain "application/json" (no charset). charset=UTF-8 triggers "invalid UTF-8 format" on some hubs / stacks.
		var bytes = Utf8NoBom.GetBytes(json);
		request.Content = new ByteArrayContent(bytes);
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
	}

	private async Task<T> WithTransientRetry<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
	{
		const int maxAttempts = 3;
		Exception? last = null;
		for (var attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				return await action(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (attempt < maxAttempts && IsTransientHubReadFailure(ex))
			{
				last = ex;
				await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
			}
		}

		throw last ?? new InvalidOperationException("Hub request failed after retries.");
	}

	private static bool IsTransientHubReadFailure(Exception ex)
	{
		if (ex is IOException)
			return true;

		if (ex.InnerException is IOException)
			return true;

		// Common when the hub or Wi‑Fi drops bytes mid-response (often surfaced as HttpRequestException).
		var msg = ex.Message;
		if (msg.Contains("copying content", StringComparison.OrdinalIgnoreCase))
			return true;

		return false;
	}

	private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.StatusCode == HttpStatusCode.Unauthorized)
			throw new InvalidOperationException("Authentication failed. Check your hub secret in wiserkeys.params.");

		if (!response.IsSuccessStatusCode)
		{
			var text = await ReadResponseBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
			throw new HttpRequestException($"Wiser hub returned {(int)response.StatusCode}: {text}");
		}
	}

	public static int ToWiserTemp(double celsius) => (int)Math.Round(celsius * 10, MidpointRounding.AwayFromZero);

	public static double FromWiserTemp(int wiserTenths) => Math.Round(wiserTenths / 10.0, 1);

	public static bool IsValidTemperature(double celsius) =>
		celsius == TempOffC || (celsius >= TempMinimumC && celsius <= TempMaximumC);

	private static async Task<string> ReadResponseBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
		if (raw.Length == 0)
			return "(empty body)";

		// Avoid ReadAsStringAsync: strict UTF-8 validation can throw the same "invalid UTF-8" error on odd hub bodies.
		return Utf8NoBom.GetString(raw);
	}
}
