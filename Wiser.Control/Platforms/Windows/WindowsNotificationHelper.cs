using CommunityToolkit.WinUI.Notifications;

namespace Wiser.Control;

internal static class WindowsNotificationHelper
{
	internal static void ShowToast(string title, string body)
	{
		// hintMaxLines is capped at 2 per block on Windows adaptive toasts.
		new ToastContentBuilder()
			.AddText(title, hintMaxLines: 1)
			.AddText(body, hintMaxLines: 2)
			.Show();
	}
}
