using System.Reflection;

namespace LFHub.Api;

/// <summary>
/// What this hub tells the app about itself in <c>GET /v1/me</c>, and what it needs from the
/// app. Within API version 1 changes are additive: the app and the hub each use a feature only
/// once the other side has reported it.
/// </summary>
public static class HubInfo
{
    public const int Api = 1;

    /// <summary>The hub's version, from the assembly (without the build metadata).</summary>
    public static readonly string Version =
        (typeof(HubInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0")
        .Split('+')[0];

    /// <summary>Features this hub provides.</summary>
    public static readonly string[] Features = ["receipts", "speak", "heartbeat", "bundle", "latestBg", "devices", "roles", "invites", "sourceRevision", "sharedApnsKey"];

    /// <summary>The app posts a delivery receipt for every alarm push (<c>POST /v1/receipt/{token}</c>).</summary>
    public const string Receipts = "receipts";

    /// <summary>The app's notification service extension speaks the hub's Speak BG pushes.</summary>
    public const string Speak = "speak";

    /// <summary>Features an app must report before the hub turns its alarms on.</summary>
    public static readonly string[] RequiredAppFeatures = [Receipts];
}
