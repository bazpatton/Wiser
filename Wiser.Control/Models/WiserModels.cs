using System.Text.Json.Serialization;

namespace Wiser.Control.Models;

public sealed class WiserDomainPayload
{
	[JsonPropertyName("Room")]
	public List<WiserRoom>? Room { get; set; }

	[JsonPropertyName("HotWater")]
	public List<WiserHotWater>? HotWater { get; set; }

	[JsonPropertyName("HeatingChannel")]
	public List<WiserHeatingChannel>? HeatingChannel { get; set; }

	[JsonPropertyName("System")]
	public WiserSystem? System { get; set; }

	[JsonPropertyName("Schedule")]
	public List<WiserSchedule>? Schedule { get; set; }

	/// <summary>
	/// True when the boiler relay reports on and/or any room reports heat demand. Relay can lag behind TRV demand.
	/// </summary>
	public bool IsHeatingActive() =>
		HeatingChannel?.Any(c => string.Equals(c.HeatingRelayState, "On", StringComparison.OrdinalIgnoreCase)) == true
		|| Room?.Any(r => r.IsDemandingHeat()) == true;
}

public sealed class WiserRoom
{
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("Name")]
	public string? Name { get; set; }

	[JsonPropertyName("Mode")]
	public string? Mode { get; set; }

	/// <summary>Hub-reported measured/averaged room temperature (tenths of °C).</summary>
	[JsonPropertyName("CalculatedTemperature")]
	public int? CalculatedTemperature { get; set; }

	[JsonPropertyName("DisplayedTemperature")]
	public int? DisplayedTemperature { get; set; }

	[JsonPropertyName("CurrentSetPoint")]
	public int? CurrentSetPoint { get; set; }

	[JsonPropertyName("ScheduledSetPoint")]
	public int? ScheduledSetPoint { get; set; }

	[JsonPropertyName("ScheduleId")]
	public int ScheduleId { get; set; }

	/// <summary>When <c>On</c> or <c>Open</c>, output is active. Some firmware leaves this stale; use <see cref="IsDemandingHeat"/>.</summary>
	[JsonPropertyName("ControlOutputState")]
	public string? ControlOutputState { get; set; }

	[JsonPropertyName("PercentageDemand")]
	public int? PercentageDemand { get; set; }

	/// <summary>
	/// True when the hub reports this room wants heat: non-zero <see cref="PercentageDemand"/> and/or active <see cref="ControlOutputState"/> (On/Open).
	/// Matches how the Wiser app treats “calling for heat” better than output state alone.
	/// </summary>
	public bool IsDemandingHeat()
	{
		if (PercentageDemand is > 0)
			return true;
		return ControlOutputState switch
		{
			null => false,
			{ } s when s.Equals("On", StringComparison.OrdinalIgnoreCase) => true,
			{ } s when s.Equals("Open", StringComparison.OrdinalIgnoreCase) => true,
			_ => false,
		};
	}

	/// <summary>Unix time (seconds) when the current override ends; 0 if none. Used for boost / timed manual.</summary>
	[JsonPropertyName("OverrideTimeoutUnixTime")]
	public long? OverrideTimeoutUnixTime { get; set; }

	/// <summary>Hub may send <c>SetpointOrigin</c> or <c>SetPointOrigin</c>; both bind here when using case-insensitive JSON.</summary>
	[JsonPropertyName("SetpointOrigin")]
	public string? SetpointOrigin { get; set; }
}

public sealed class WiserHotWater
{
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("WaterHeatingState")]
	public string? WaterHeatingState { get; set; }
}

public sealed class WiserHeatingChannel
{
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("HeatingRelayState")]
	public string? HeatingRelayState { get; set; }

	[JsonPropertyName("RoomIds")]
	public List<int>? RoomIds { get; set; }
}

public sealed class WiserSystem
{
	[JsonPropertyName("LocalDateAndTime")]
	public WiserLocalDateTime? LocalDateAndTime { get; set; }
}

public sealed class WiserSchedule
{
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("Monday")]
	public WiserScheduleDay? Monday { get; set; }

	[JsonPropertyName("Tuesday")]
	public WiserScheduleDay? Tuesday { get; set; }

	[JsonPropertyName("Wednesday")]
	public WiserScheduleDay? Wednesday { get; set; }

	[JsonPropertyName("Thursday")]
	public WiserScheduleDay? Thursday { get; set; }

	[JsonPropertyName("Friday")]
	public WiserScheduleDay? Friday { get; set; }

	[JsonPropertyName("Saturday")]
	public WiserScheduleDay? Saturday { get; set; }

	[JsonPropertyName("Sunday")]
	public WiserScheduleDay? Sunday { get; set; }

	public WiserScheduleDay? GetDay(string dayName) => dayName switch
	{
		"Monday" => Monday,
		"Tuesday" => Tuesday,
		"Wednesday" => Wednesday,
		"Thursday" => Thursday,
		"Friday" => Friday,
		"Saturday" => Saturday,
		"Sunday" => Sunday,
		_ => null,
	};
}

public sealed class WiserScheduleDay
{
	[JsonPropertyName("SetPoints")]
	public List<WiserScheduleSetPoint>? SetPoints { get; set; }
}

public sealed class WiserScheduleSetPoint
{
	[JsonPropertyName("Time")]
	public int Time { get; set; }

	[JsonPropertyName("DegreesC")]
	public int DegreesC { get; set; }
}

public sealed class WiserLocalDateTime
{
	[JsonPropertyName("Day")]
	public string? Day { get; set; }

	[JsonPropertyName("Time")]
	public long Time { get; set; }
}
