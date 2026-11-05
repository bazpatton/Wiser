using System.Text.Json;

namespace Wiser.Monitor.Tests;

public class ScheduleTimingTests
{
    private const long Now = 1_800_000_000;

    [Theory]
    [InlineData(630, 6 * 3600 + 30 * 60)]       // HHMM
    [InlineData(2230, 22 * 3600 + 30 * 60)]     // HHMM
    [InlineData(0, 0)]
    [InlineData(61200, 61200)]                  // seconds from midnight (modern firmware)
    [InlineData(90, 90 * 60)]                   // "minutes" fallback when MM >= 60
    [InlineData(-5, 0)]
    public void NormalizeSeconds_handles_each_hub_time_format(int raw, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, HubScheduleHints.NormalizeSeconds(raw));

    private static JsonElement Domain(string day, int time, string scheduleDaysJson, int scheduleId = 1) =>
        JsonDocument.Parse($$"""
            {
              "System": { "LocalDateAndTime": { "Day": "{{day}}", "Time": {{time}} } },
              "Schedule": [ { "id": 1, {{scheduleDaysJson}} } ],
              "Room": [ { "id": 7, "Name": "Living room", "ScheduleId": {{scheduleId}} } ]
            }
            """).RootElement;

    [Fact]
    public void Next_change_later_today()
    {
        var root = Domain("Monday", 1700, """
            "Monday": { "SetPoints": [ { "Time": 630, "DegreesC": 200 }, { "Time": 2230, "DegreesC": 160 } ] }
            """);

        var next = HubSchedulesParser.NextChangeByRoomId(root, Now)[7];

        Assert.Equal(Now + (5 * 3600 + 30 * 60), next.AtUnix);
        Assert.Equal(16.0, next.SetpointC);
    }

    [Fact]
    public void Next_change_rolls_over_to_tomorrow_after_last_point()
    {
        var root = Domain("Monday", 2300, """
            "Monday": { "SetPoints": [ { "Time": 630, "DegreesC": 200 }, { "Time": 2230, "DegreesC": 160 } ] },
            "Tuesday": { "SetPoints": [ { "Time": 700, "DegreesC": 190 } ] }
            """);

        var next = HubSchedulesParser.NextChangeByRoomId(root, Now)[7];

        Assert.Equal(Now + 8 * 3600, next.AtUnix); // 23:00 Mon -> 07:00 Tue
        Assert.Equal(19.0, next.SetpointC);
    }

    [Fact]
    public void Next_change_wraps_from_sunday_to_monday()
    {
        var root = Domain("Sunday", 2200, """
            "Monday": { "SetPoints": [ { "Time": 600, "DegreesC": 180 } ] }
            """);

        var next = HubSchedulesParser.NextChangeByRoomId(root, Now)[7];

        Assert.Equal(Now + 8 * 3600, next.AtUnix);
    }

    [Fact]
    public void Off_setpoint_maps_to_hub_off_sentinel()
    {
        var root = Domain("Monday", 1200, """
            "Monday": { "SetPoints": [ { "Time": 1300, "DegreesC": -200 } ] }
            """);

        var next = HubSchedulesParser.NextChangeByRoomId(root, Now)[7];

        Assert.True(WiserSetpointDisplay.IsHubOffSentinel(next.SetpointC));
    }

    [Fact]
    public void Room_without_a_matching_schedule_is_left_out()
    {
        var root = Domain("Monday", 1200, """
            "Monday": { "SetPoints": [ { "Time": 1300, "DegreesC": 200 } ] }
            """, scheduleId: 99);

        Assert.Empty(HubSchedulesParser.NextChangeByRoomId(root, Now));
    }

    [Fact]
    public void Point_at_the_current_time_is_not_the_next_change()
    {
        var root = Domain("Monday", 1300, """
            "Monday": { "SetPoints": [ { "Time": 1300, "DegreesC": 200 }, { "Time": 1400, "DegreesC": 180 } ] }
            """);

        Assert.Equal(Now + 3600, HubSchedulesParser.NextChangeByRoomId(root, Now)[7].AtUnix);
    }

    [Fact]
    public void Minutes_until_next_change_only_when_within_one_override()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(Now);

        Assert.Equal(90, TimedOverrideMath.MinutesUntilNextChange(Now + 90 * 60, now));
        Assert.Null(TimedOverrideMath.MinutesUntilNextChange(Now + 61, now)); // under the 5-minute minimum
        Assert.Null(TimedOverrideMath.MinutesUntilNextChange(Now + 241 * 60, now));
        Assert.Null(TimedOverrideMath.MinutesUntilNextChange((long?)null, now));
        Assert.Equal(240, TimedOverrideMath.MinutesUntilNextChange(Now + 240 * 60, now));
    }
}
