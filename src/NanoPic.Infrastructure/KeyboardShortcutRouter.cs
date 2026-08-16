namespace NanoPic.Infrastructure;

public enum ApplicationShortcutKey
{
    None = 0,
    O,
    A,
    I,
    Delete,
    Enter,
    F5,
    Escape
}

public enum ApplicationShortcutAction
{
    None = 0,
    AddFiles,
    AddFolder,
    SelectAll,
    InvertSelection,
    RemoveHighlighted,
    PreviewHighlighted,
    Start,
    Cancel
}

public sealed record ApplicationShortcutContext(
    ApplicationShortcutKey Key,
    bool Control,
    bool Shift,
    bool IsTextEditing,
    bool IsQueueFocused,
    bool IsRunActive,
    bool CanStart);

public static class KeyboardShortcutRouter
{
    public static ApplicationShortcutAction Resolve(ApplicationShortcutContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (context.Key == ApplicationShortcutKey.Escape && context.IsRunActive)
        {
            return ApplicationShortcutAction.Cancel;
        }

        if (context.Key == ApplicationShortcutKey.F5 && context.CanStart)
        {
            return ApplicationShortcutAction.Start;
        }

        if (context.IsTextEditing)
        {
            return ApplicationShortcutAction.None;
        }

        if (context.Control && context.Key == ApplicationShortcutKey.O)
        {
            return context.Shift ? ApplicationShortcutAction.AddFolder : ApplicationShortcutAction.AddFiles;
        }

        if (context.Control && context.Key == ApplicationShortcutKey.A)
        {
            return ApplicationShortcutAction.SelectAll;
        }

        if (context.Control && context.Key == ApplicationShortcutKey.I)
        {
            return ApplicationShortcutAction.InvertSelection;
        }

        if (context.IsQueueFocused && context.Key == ApplicationShortcutKey.Delete)
        {
            return ApplicationShortcutAction.RemoveHighlighted;
        }

        if (context.IsQueueFocused && context.Key == ApplicationShortcutKey.Enter)
        {
            return ApplicationShortcutAction.PreviewHighlighted;
        }

        return ApplicationShortcutAction.None;
    }
}
