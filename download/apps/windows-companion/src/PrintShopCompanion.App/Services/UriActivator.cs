namespace PrintShopCompanion.App.Services;

using System.Windows;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Hooks the OS protocol activation. The WiX installer registers the
/// <c>printshopcompanion://</c> URL scheme with Windows; when the shopkeeper
/// clicks the "Add this PC" link on the website, Windows launches the
/// companion with the URL as the first argument. This class surfaces that
/// argument as an event so the App can route it to the pairing service.
/// </summary>
public sealed class UriActivator
{
    private readonly IServiceProvider _services;

    public UriActivator(IServiceProvider services) => _services = services;

    /// <summary>
    /// Returns the URL the OS launched us with, or null if we were launched normally.
    /// In production this hooks into WPF's <c>StartupUri</c> mechanism; for the
    /// first delivery we read it from <c>Environment.GetCommandLineArgs</c>.
    /// </summary>
    public Uri? GetActivationUri()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Uri.TryCreate(args[1], UriKind.Absolute, out var u)
            && string.Equals(u.Scheme, "printshopcompanion", StringComparison.OrdinalIgnoreCase))
            return u;
        return null;
    }
}
