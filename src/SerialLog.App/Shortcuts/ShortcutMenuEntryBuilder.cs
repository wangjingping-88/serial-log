namespace SerialLog.App.Shortcuts;

internal sealed record ShortcutMenuEntry(
    string ActionId,
    string DisplayName,
    string GestureText);

internal static class ShortcutMenuEntryBuilder
{
    private const string UnassignedGestureText = "未设置";

    public static IReadOnlyList<ShortcutMenuEntry> Build(ShortcutManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        return ShortcutManager.Definitions
            .Select(definition =>
            {
                var gesture = manager.GetGesture(definition.ActionId);
                return new ShortcutMenuEntry(
                    definition.ActionId,
                    definition.DisplayName,
                    string.IsNullOrWhiteSpace(gesture) ? UnassignedGestureText : gesture);
            })
            .ToList();
    }
}
