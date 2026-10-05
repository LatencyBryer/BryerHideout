using System;
using System.Collections.Generic;

namespace BryersHideoutPlugin.Services;

public sealed record AccessLinkDestination(string Key, string Label, string Host);

public static class AccessLinkBuilder
{
    public static readonly IReadOnlyList<AccessLinkDestination> Destinations = new[]
    {
        new AccessLinkDestination("lootbox", "Lootbox", "lootbox.bybryer.com"),
        new AccessLinkDestination("minigames", "Minigames", "minigames.bybryer.com"),
        new AccessLinkDestination("mines", "Mines", "mines.bybryer.com"),
        new AccessLinkDestination("plinko", "Plinko", "plinko.bybryer.com"),
        new AccessLinkDestination("library", "Whispering Library", "library.bybryer.com"),
        new AccessLinkDestination("blackprism", "Black Prism", "blackprism.bybryer.com"),
        new AccessLinkDestination("coinflip", "Coin Flip", "coinflip.bybryer.com"),
    };

    public static string Build(string accessCode, AccessLinkDestination destination)
    {
        var code = (accessCode ?? string.Empty).Trim();
        if (code.Length == 0) throw new InvalidOperationException("This Player does not currently have an access code for the active Venue.");
        return $"https://{destination.Host}/{Uri.EscapeDataString(code)}";
    }
}
