using Wiser.Control.Services;

namespace Wiser.Control;

/// <summary>
/// Periodic hub refresh while the Windows app process is running (including when minimized).
/// Stops when the user closes the app; not a registered Windows scheduled task.
/// </summary>
internal static class WindowsBackgroundHubPoller
{
	private static readonly object Gate = new();
	private static CancellationTokenSource? _cts;
	private static Task? _running;

	public static void EnsureStarted()
	{
		lock (Gate)
		{
			if (_cts is not null && !_cts.IsCancellationRequested && _running is { IsCompleted: false })
				return;

			_cts?.Cancel();
			_cts?.Dispose();
			_cts = new CancellationTokenSource();
			var token = _cts.Token;
			_running = RunLoopAsync(token);
		}
	}

	/// <summary>Cancels the current loop and starts a new one (e.g. interval changed in Settings).</summary>
	public static void Restart()
	{
		lock (Gate)
		{
			_cts?.Cancel();
			_cts?.Dispose();
			_cts = null;
			_running = null;
		}

		EnsureStarted();
	}

	private static async Task RunLoopAsync(CancellationToken ct)
	{
		try
		{
			await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

			while (!ct.IsCancellationRequested)
			{
				await BackgroundHubTemperatureRunner.RunAsync().ConfigureAwait(false);
				var minutes = BackgroundPollingSettingsStore.IntervalMinutes;
				await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}
}
