using System.Collections.ObjectModel;
using Wiser.Control.Services;

namespace Wiser.Control;

public partial class ActionHistoryPage : ContentPage
{
	private readonly ObservableCollection<ActionHistoryRow> _rows = [];

	public ActionHistoryPage()
	{
		InitializeComponent();
		HistoryCollection.ItemsSource = _rows;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Reload();
	}

	private void Reload()
	{
		_rows.Clear();
		foreach (var e in ActionHistoryStore.Load())
			_rows.Add(new ActionHistoryRow(e));

		HistoryHintLabel.Text = _rows.Count == 0
			? "No actions yet."
			: $"{_rows.Count} action(s)";
	}

	private async void OnUndoClicked(object? sender, EventArgs e)
	{
		if (sender is not BindableObject b || b.BindingContext is not ActionHistoryRow row)
			return;

		if (!row.CanUndo)
			return;

		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync();
			using var client = new WiserHubClient(conn);
			var snapshot = await client.RefreshDomainAsync();

			var applied = await ActionUndoService.TryUndoAsync(client, snapshot, row.Entry);
			if (!applied)
			{
				await DisplayAlert("Undo", "This action cannot be undone.", "OK");
				return;
			}

			ActionHistoryStore.Remove(row.Entry.Id);
			ActionHistoryStore.Append(new ActionHistoryEntry
			{
				Title = $"Undo: {row.Entry.Title}",
				Details = "Previous room state restored.",
				UndoKind = UndoKind.None,
			});
			Reload();
		}
		catch (Exception ex)
		{
			await DisplayAlert("Undo failed", ex.Message, "OK");
		}
	}

	private async void OnClearClicked(object? sender, EventArgs e)
	{
		var confirm = await DisplayAlert("Clear history", "Remove all action history?", "Clear", "Cancel");
		if (!confirm)
			return;

		ActionHistoryStore.Clear();
		Reload();
	}
}

public sealed class ActionHistoryRow
{
	public ActionHistoryRow(ActionHistoryEntry entry)
	{
		Entry = entry;
	}

	public ActionHistoryEntry Entry { get; }
	public bool CanUndo => Entry.UndoKind != UndoKind.None;
	public string AtText => Entry.At.ToLocalTime().ToString("g");
}
