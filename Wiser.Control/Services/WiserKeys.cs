namespace Wiser.Control.Services;

public sealed record WiserConnection(string HubIp, string Secret);

public static class WiserKeys
{
	public static WiserConnection Parse(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			throw new InvalidOperationException("wiserkeys.params is empty.");

		text = text.TrimStart('\uFEFF');

		string? key = null;
		string? ip = null;

		foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var line = rawLine.Trim();
			if (line.StartsWith('#'))
				continue;

			var eq = line.IndexOf('=');
			if (eq <= 0)
				continue;

			var name = line[..eq].Trim();
			var value = line[(eq + 1)..].Trim();

			if (name.Equals("wiserkey", StringComparison.OrdinalIgnoreCase))
				key = value;
			else if (name.Equals("wiserhubip", StringComparison.OrdinalIgnoreCase))
				ip = value;
		}

		if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(ip))
			throw new InvalidOperationException("wiserkeys.params must contain wiserkey= and wiserhubip= lines.");

		return new WiserConnection(ip.Trim(), key.Trim());
	}

	public static async Task<WiserConnection> LoadFromAppPackageAsync(CancellationToken cancellationToken = default)
	{
		await using var stream = await FileSystem.OpenAppPackageFileAsync("wiserkeys.params").ConfigureAwait(false);
		using var reader = new StreamReader(stream);
		var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		return Parse(text);
	}
}
