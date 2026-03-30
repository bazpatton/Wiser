using Android.App;
using Android.Content;
using Android.OS;
using Wiser.Control.Services;

namespace Wiser.Control;

internal static class BackgroundAlertScheduler
{
	public const string AlarmAction = "com.companyname.wiser.control.BACKGROUND_TEMP_CHECK";
	private const int AlarmRequestCode = 42031;

	public static void EnsureScheduled(Context context)
	{
		var alarmManager = context.GetSystemService(Context.AlarmService) as AlarmManager;
		if (alarmManager is null)
			return;

		var pending = BuildPendingIntent(context);
		var intervalMs = (long)TimeSpan.FromMinutes(BackgroundPollingSettingsStore.IntervalMinutes).TotalMilliseconds;

		alarmManager.Cancel(pending);
		alarmManager.SetInexactRepeating(
			AlarmType.ElapsedRealtimeWakeup,
			SystemClock.ElapsedRealtime() + (long)TimeSpan.FromMinutes(2).TotalMilliseconds,
			intervalMs,
			pending);
	}

	private static PendingIntent BuildPendingIntent(Context context)
	{
		var intent = new Intent(context, typeof(TemperatureAlertAlarmReceiver));
		intent.SetAction(AlarmAction);
#pragma warning disable CA1416
		var flags = PendingIntentFlags.UpdateCurrent;
		if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
			flags |= PendingIntentFlags.Immutable;
#pragma warning restore CA1416

		return PendingIntent.GetBroadcast(
			context,
			AlarmRequestCode,
			intent,
			flags)!;
	}
}
