namespace RunnerManager;

/// <summary>Which button of a dialog Enter presses and the keyboard starts on.</summary>
internal enum DialogDefault { Primary, Cancel, None }

/// <summary>Rules behind the themed dialogs, kept apart from the window so they can be tested.</summary>
internal static class DialogRules
{
    /// <summary>
    /// A danger dialog starts on Cancel, so a repeated or stray Enter cancels and the destructive button needs a click
    /// or a deliberate key press. Without a Cancel button nothing is the default.
    /// </summary>
    internal static DialogDefault DefaultFor(DialogTone tone, bool hasCancel)
        => tone != DialogTone.Danger ? DialogDefault.Primary : hasCancel ? DialogDefault.Cancel : DialogDefault.None;

    /// <summary>The dialog's text for Ctrl+C, as a Windows message box copies it.</summary>
    internal static string CopyText(string heading, string message, string? detail)
        => string.Join(Environment.NewLine + Environment.NewLine,
            new[] { heading, message, detail ?? string.Empty }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
}
