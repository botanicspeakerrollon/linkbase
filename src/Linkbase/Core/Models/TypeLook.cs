using Linkbase.Core.Explore;

namespace Linkbase.Core.Models;

/// <summary>
/// A colour and icon for a type that arrives without either, such as one made from a CSV file. The icon follows the type's
/// name where a word in it says what it is, and the colour is the next of a spread-out set that no other type in the model
/// uses, so a model's types stay easy to tell apart. Types with a look of their own are never changed.
/// </summary>
public static class TypeLook
{
    // Far apart in hue, and dark enough for white text.
    private static readonly string[] Colors =
    [
        "#2563eb", "#dc2626", "#16a34a", "#d97706", "#9333ea", "#0891b2",
        "#db2777", "#65a30d", "#ea580c", "#4f46e5", "#0d9488", "#a16207",
    ];

    // The first word found in a type's name wins. Every value is a Material icon's name, which a test checks.
    private static readonly (string Word, string Icon)[] Words =
    [
        ("person", "Person"), ("people", "Person"), ("customer", "Person"), ("client", "Person"), ("user", "Person"), ("employee", "Badge"),
        ("partner", "Handshake"), ("supplier", "LocalShipping"), ("vendor", "Storefront"), ("team", "Groups"), ("group", "Groups"),
        ("company", "Business"), ("organisation", "Business"), ("organization", "Business"), ("firm", "Business"), ("office", "Business"),
        ("account", "AccountBalance"), ("bank", "AccountBalance"), ("trade", "SwapHoriz"), ("transaction", "SwapHoriz"), ("payment", "Payments"),
        ("invoice", "Receipt"), ("order", "ShoppingCart"), ("product", "Inventory"), ("item", "Inventory"), ("asset", "Inventory"),
        ("alert", "NotificationImportant"), ("risk", "Warning"), ("issue", "BugReport"), ("incident", "Warning"), ("case", "Gavel"),
        ("project", "Folder"), ("task", "Task"), ("document", "Description"), ("file", "Description"), ("contract", "Description"),
        ("event", "Event"), ("meeting", "Event"), ("place", "Place"), ("location", "Place"), ("city", "LocationCity"), ("country", "Public"),
        ("station", "Train"), ("train", "Train"), ("route", "Route"), ("line", "Route"), ("vehicle", "DirectionsCar"), ("car", "DirectionsCar"),
        ("system", "Dns"), ("server", "Dns"), ("service", "Dns"), ("application", "Apps"), ("app", "Apps"), ("tag", "Label"), ("category", "Category"),
    ];

    private static readonly string[] Fallback = ["Circle", "Hexagon", "Square", "Category", "Bookmark", "Star", "Diamond", "Folder"];

    // takenColors and takenIcons are what the model's types already use, so each new type gets a colour no other type has
    // while there is one left, and an icon not already in use.
    public static (string Color, string Icon) Pick(string typeName, IEnumerable<string?> takenColors, IEnumerable<string?> takenIcons)
    {
        var colors = takenColors.Where(c => c is not null).Select(c => c!.ToLowerInvariant()).ToList();
        var color = Colors.FirstOrDefault(c => !colors.Contains(c))
            ?? Colors[colors.Count % Colors.Length];

        var icons = takenIcons.Where(i => i is not null).Select(i => i!).ToList();
        var lower = typeName.ToLowerInvariant();
        var icon = Words.Where(w => lower.Contains(w.Word)).Select(w => w.Icon).FirstOrDefault(i => !icons.Contains(i))
            ?? Fallback.FirstOrDefault(i => !icons.Contains(i))
            ?? Fallback[icons.Count % Fallback.Length];
        return (color, icon);
    }

    // Every icon this can pick, so a test can check each exists.
    public static IEnumerable<string> AllIcons => Words.Select(w => w.Icon).Concat(Fallback).Distinct();

    public static bool Exists(string icon) => GraphStyle.IconNamed(icon) is not null;
}
