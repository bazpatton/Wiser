using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Graphics;
using Wiser.Control.Models;
using Wiser.Control.Services;
using static Wiser.Control.IconGlyphs;

namespace Wiser.Control.ViewModels;

public sealed class RoomRowViewModel : INotifyPropertyChanged
{
	private double _targetSlider;
	private string _displayTempText;
	private string _mode;
	private bool _isTargetOff;
	private bool? _radiatorOn;
	private string? _demandSuffix = "";
	private string _heatTimingSummary = "";
	private bool _isActionBusy;

	public RoomRowViewModel(WiserRoom room, WiserDomainPayload? domain = null)
	{
		RoomId = room.Id;
		Name = room.Name ?? $"Room {room.Id}";
		_mode = room.Mode ?? "—";
		_displayTempText = FormatCurrentTemp(room);
		ApplyRadiatorState(room);
		ApplyTargetFromRoom(room);
		ApplyHeatTiming(room, domain);
	}

	public int RoomId { get; }

	public string Name { get; }

	public string Mode
	{
		get => _mode;
		private set
		{
			if (_mode == value)
				return;
			_mode = value;
			OnPropertyChanged();
		}
	}

	public string DisplayTempText
	{
		get => _displayTempText;
		private set
		{
			if (_displayTempText == value)
				return;
			_displayTempText = value;
			OnPropertyChanged();
		}
	}

	public string TargetTempText => _isTargetOff ? "Off" : $"{Math.Round(_targetSlider, 1)} °C";

	/// <summary>True when hub reports valve output on; null if not reported.</summary>
	public bool? RadiatorOn => _radiatorOn;

	public string RadiatorStatusText => _radiatorOn switch
	{
		true => string.IsNullOrEmpty(_demandSuffix) ? "Radiator on" : $"Radiator on ({_demandSuffix})",
		false => "Radiator off",
		_ => "Radiator: —",
	};

	public Color RadiatorStatusColor => _radiatorOn switch
	{
		true => Color.FromArgb("#D3542C"),
		false => Color.FromArgb("#8A8580"),
		_ => Color.FromArgb("#A8A29E"),
	};

	/// <summary>Icon for demand row (Font Awesome, matches registered app font).</summary>
	public string RadiatorStatusGlyph => _radiatorOn switch
	{
		true => FaFire,
		false => FaTemperatureLow,
		_ => FaTemperatureHalf,
	};

	/// <summary>Boost/manual end time, or next schedule change time — shown after target on the demand row.</summary>
	public string HeatTimingSummaryText => _heatTimingSummary;

	public bool HasHeatTimingSummary => !string.IsNullOrEmpty(_heatTimingSummary);

	/// <summary>True while a room-specific action is being sent to the hub.</summary>
	public bool IsActionBusy
	{
		get => _isActionBusy;
		set
		{
			if (_isActionBusy == value)
				return;
			_isActionBusy = value;
			OnPropertyChanged();
		}
	}

	public double TargetSlider
	{
		get => _targetSlider;
		set
		{
			var v = Math.Round(value, 1);
			if (Math.Abs(_targetSlider - v) < 0.01)
				return;
			_targetSlider = v;
			_isTargetOff = false;
			OnPropertyChanged();
			OnPropertyChanged(nameof(TargetTempText));
		}
	}

	public void ApplySnapshot(WiserRoom room, WiserDomainPayload? domain = null)
	{
		Mode = room.Mode ?? "—";
		DisplayTempText = FormatCurrentTemp(room);
		ApplyRadiatorState(room);
		ApplyTargetFromRoom(room);
		ApplyHeatTiming(room, domain);
	}

	private void ApplyRadiatorState(WiserRoom room)
	{
		bool? next = ParseRadiatorOn(room);
		var demandSuffix = room.PercentageDemand is > 0 and var p ? $"{p}%" : "";

		if (_radiatorOn == next && _demandSuffix == demandSuffix)
			return;

		_radiatorOn = next;
		_demandSuffix = demandSuffix;
		OnPropertyChanged(nameof(RadiatorOn));
		OnPropertyChanged(nameof(RadiatorStatusText));
		OnPropertyChanged(nameof(RadiatorStatusColor));
		OnPropertyChanged(nameof(RadiatorStatusGlyph));
	}

	private void ApplyHeatTiming(WiserRoom room, WiserDomainPayload? domain)
	{
		var text = BuildHeatTimingSummary(room, domain);
		if (_heatTimingSummary == text)
			return;

		_heatTimingSummary = text;
		OnPropertyChanged(nameof(HeatTimingSummaryText));
		OnPropertyChanged(nameof(HasHeatTimingSummary));
	}

	private static string BuildHeatTimingSummary(WiserRoom room, WiserDomainPayload? domain)
	{
		var o = FormatOverrideUntil(room);
		if (!string.IsNullOrEmpty(o))
			return o;

		var schedule = domain?.Schedule?.FirstOrDefault(s => s.Id == room.ScheduleId);
		if (schedule is null)
			return "";

		var day = WiserScheduleHints.ResolveCurrentDay(domain);
		var sec = WiserScheduleHints.ResolveCurrentSeconds(domain);
		return WiserScheduleHints.GetScheduleUntilShort(schedule, day, sec);
	}

	/// <summary>
	/// Hub sends <see cref="WiserRoom.OverrideTimeoutUnixTime"/> for boost / timed manual. Shown in preference to schedule hint.
	/// </summary>
	private static string FormatOverrideUntil(WiserRoom room)
	{
		var raw = room.OverrideTimeoutUnixTime;
		if (raw is null or 0)
			return "";

		DateTimeOffset end;
		try
		{
			var v = raw.Value;
			end = v > 10_000_000_000L
				? DateTimeOffset.FromUnixTimeMilliseconds(v)
				: DateTimeOffset.FromUnixTimeSeconds(v);
		}
		catch (ArgumentOutOfRangeException)
		{
			return "";
		}

		var local = end.ToLocalTime();
		if (local <= DateTimeOffset.Now)
			return "";

		var origin = room.SetpointOrigin;
		var prefix = origin?.Contains("Boost", StringComparison.OrdinalIgnoreCase) == true
			? "Boost until "
			: "Heat until ";

		return $"{prefix}{local:t}";
	}

	private static bool? ParseRadiatorOn(WiserRoom room)
	{
		if (room.IsDemandingHeat())
			return true;

		var s = room.ControlOutputState;
		if (string.IsNullOrWhiteSpace(s))
			return null;

		if (s.Equals("Off", StringComparison.OrdinalIgnoreCase)
			|| s.Equals("Close", StringComparison.OrdinalIgnoreCase))
			return false;

		return null;
	}

	private void ApplyTargetFromRoom(WiserRoom room)
	{
		var sp = room.CurrentSetPoint ?? room.ScheduledSetPoint;
		if (sp is not null && sp <= -100)
		{
			_isTargetOff = true;
			_targetSlider = WiserHubClient.TempMinimumC;
			OnPropertyChanged(nameof(TargetSlider));
			OnPropertyChanged(nameof(TargetTempText));
			return;
		}

		_isTargetOff = false;
		var slider = WiserHubClient.FromWiserTemp(sp ?? WiserHubClient.TempMinimumC * 10);
		if (slider < WiserHubClient.TempMinimumC || slider > WiserHubClient.TempMaximumC)
			slider = Math.Clamp(slider, WiserHubClient.TempMinimumC, WiserHubClient.TempMaximumC);
		_targetSlider = slider;
		OnPropertyChanged(nameof(TargetSlider));
		OnPropertyChanged(nameof(TargetTempText));
	}

	/// <summary>
	/// Prefer <see cref="WiserRoom.CalculatedTemperature"/> (what the hub uses for “current” temp);
	/// fall back to <see cref="WiserRoom.DisplayedTemperature"/> for older payloads.
	/// </summary>
	private static string FormatCurrentTemp(WiserRoom room)
	{
		var tenths = room.CalculatedTemperature ?? room.DisplayedTemperature;
		if (tenths is null)
			return "—";

		// Match wiserHeatAPIv2: raw hub value >= TEMP_ERROR means lost sensor / invalid
		const int tempErrorSentinel = 2000;
		var t = tenths.Value;
		if (t >= tempErrorSentinel)
			return "—";

		var c = WiserHubClient.FromWiserTemp(t);
		if (c < WiserHubClient.TempOffC + 1)
			return "—";

		return $"{c:0.#} °C";
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
