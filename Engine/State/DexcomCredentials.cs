namespace LFHub.Engine.State;

/// <summary>Dexcom Share credentials as LoopFollow stores them; `Server` is "US" for the US server, anything else for the international one.</summary>
public sealed record DexcomCredentials(string Username, string Password, string Server)
{
    /// Leaves the password out of the record's string form.
    public override string ToString() => $"DexcomCredentials {{ Server = {Server} }}";
}
