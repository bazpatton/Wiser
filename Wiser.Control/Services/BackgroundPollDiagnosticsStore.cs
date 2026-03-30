namespace Wiser.Control.Services;

/// <summary>Tracks scheduled hub polls (Android alarm / Windows timer) so users can confirm checks ran.</summary>
public static class BackgroundPollDiagnosticsStore
{
	private const string KeyLastOkUnix = "bg_poll_last_ok_unix";
	private const string KeyLastError = "bg_poll_last_error";
	private const string KeyLastAttemptUnix = "bg_poll_last_attempt_unix";

	public static void RecordAttempt()
	{
		Preferences.Set(KeyLastAttemptUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
	}

	public static void RecordSuccess()
	{
		Preferences.Set(KeyLastOkUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		Preferences.Set(KeyLastError, string.Empty);
	}

	public static void RecordFailure(string message)
	{
		var m = string.IsNullOrWhiteSpace(message) ? "Unknown error" : message.Trim();
		if (m.Length > 160)
			m = m[..160];
		Preferences.Set(KeyLastError, m);
	}

	public static string GetStatusSummaryForDisplay()
	{
		var okUnix = Preferences.Get(KeyLastOkUnix, 0L);
		var attemptUnix = Preferences.Get(KeyLastAttemptUnix, 0L);
		var err = Preferences.Get(KeyLastError, string.Empty);

		if (okUnix == 0 && attemptUnix == 0)
			return "No scheduled poll recorded yet. Open the app and wait for the first interval (about 2 minutes, then every 30–120 minutes per setting). Android also depends on battery optimization.";

		var okLocal = okUnix > 0
			? DateTimeOffset.FromUnixTimeSeconds(okUnix).ToLocalTime().ToString("g")
			: "never";

		var sb = $"Last successful background poll: {okLocal}.";
		if (!string.IsNullOrEmpty(err))
			sb += $"\nLast failure: {err}";

		return sb;
	}
}
