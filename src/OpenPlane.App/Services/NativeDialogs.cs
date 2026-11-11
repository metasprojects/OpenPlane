namespace OpenPlane.App.Services;

/// <summary>Native file dialogs. Only available in the desktop window; in a browser the page asks for a path instead.</summary>
public interface INativeDialogs
{
    bool Available { get; }
    Task<string?> PickFolder(string title, string? startFolder);
}

public sealed class NoDialogs : INativeDialogs
{
    public bool Available => false;
    public Task<string?> PickFolder(string title, string? startFolder) => Task.FromResult<string?>(null);
}
