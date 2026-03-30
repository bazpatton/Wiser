using Android.Content;

namespace Wiser.Control;

[BroadcastReceiver(Enabled = true, Exported = true)]
[Android.App.IntentFilter([Intent.ActionBootCompleted])]
public sealed class BootCompletedReceiver : BroadcastReceiver
{
	public override void OnReceive(Context? context, Intent? intent)
	{
		if (context is null)
			return;

		BackgroundAlertScheduler.EnsureScheduled(context);
	}
}
