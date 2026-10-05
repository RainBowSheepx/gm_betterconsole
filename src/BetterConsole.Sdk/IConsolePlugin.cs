namespace BetterConsole.Sdk;

/// <summary>
/// Entry point of a BetterConsole plugin. Put a public, parameterless class that implements this
/// interface into a DLL under <c>plugins\&lt;YourPlugin&gt;\</c> next to BetterConsole.exe.
/// </summary>
/// <remarks>
/// Every member is called on the UI thread. Events of <see cref="IPluginContext"/> are raised on
/// the UI thread too, so a plugin can touch WPF objects directly.
/// </remarks>
public interface IConsolePlugin
{
    /// <summary>Stable unique id, e.g. <c>"example.quickcommands"</c>. Used for the data folder and settings.</summary>
    string Id { get; }

    /// <summary>Human readable name shown in the plugin list.</summary>
    string Name { get; }

    /// <summary>One line about what the plugin does.</summary>
    string Description => "";

    /// <summary>Called once after the main window is created. Register tabs, status items and event handlers here.</summary>
    void Initialize(IPluginContext context);

    /// <summary>Called when the application exits. Save state here.</summary>
    void Shutdown() { }
}
