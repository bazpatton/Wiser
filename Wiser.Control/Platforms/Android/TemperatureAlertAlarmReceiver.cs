using Android.Content;
using Wiser.Control.Services;

namespace Wiser.Control;

[BroadcastReceiver(Enabled = true, Exported = false)]
[Android.App.IntentFilter([BackgroundAlertScheduler.AlarmAction])]
public sealed class TemperatureAlertAlarmReceiver : BroadcastReceiver
{
	public override void OnReceive(Context? context, Intent? intent)
	{
		if (context is null)
			return;

		var pending = GoAsync();
		_ = Task.Run(async () =>
		{
			try
			{
				await BackgroundHubTemperatureRunner.RunAsync().ConfigureAwait(false);
			}
			finally
			{
				pending?.Finish();
			}
		});
	}
}
