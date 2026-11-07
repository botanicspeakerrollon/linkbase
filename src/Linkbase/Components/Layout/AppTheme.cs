using MudBlazor;

namespace Linkbase.Components.Layout;

// The app is dark only. Colours are set here and reach the pages as --mud-palette-* variables.
public static class AppTheme
{
    private static readonly string[] Fonts = ["Segoe UI Variable Text", "Segoe UI", "system-ui", "-apple-system", "Roboto", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#4aa3ff",
            PrimaryContrastText = "#0b1220",
            Secondary = "#2dd4bf",
            SecondaryContrastText = "#0b1220",
            Tertiary = "#a78bfa",
            Info = "#60a5fa",
            Success = "#4ade80",
            Warning = "#fbbf24",
            Error = "#f87171",
            Black = "#0d0f13",
            Background = "#14171c",
            BackgroundGray = "#191d23",
            Surface = "#1c2027",
            DrawerBackground = "#101318",
            DrawerText = "#c5cbd3",
            DrawerIcon = "#9aa3af",
            AppbarBackground = "#101318",
            AppbarText = "#e6e8eb",
            TextPrimary = "#e6e8eb",
            TextSecondary = "#9aa3af",
            TextDisabled = "#6b7480",
            ActionDefault = "#9aa3af",
            ActionDisabled = "#5a626d",
            ActionDisabledBackground = "#272c34",
            LinesDefault = "#2c323b",
            LinesInputs = "#3a414b",
            TableLines = "#262b33",
            TableStriped = "#191d23",
            TableHover = "#232831",
            Divider = "#2c323b",
            DividerLight = "#262b33",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Fonts, FontSize = "0.875rem", LineHeight = "1.45" },
            H4 = new H4Typography { FontFamily = Fonts, FontSize = "1.375rem", FontWeight = "600", LineHeight = "1.3" },
            H5 = new H5Typography { FontFamily = Fonts, FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.3" },
            H6 = new H6Typography { FontFamily = Fonts, FontSize = "0.9375rem", FontWeight = "600", LineHeight = "1.35" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Fonts, FontSize = "0.75rem", FontWeight = "600", LineHeight = "1.4" },
            Body1 = new Body1Typography { FontFamily = Fonts, FontSize = "0.875rem", LineHeight = "1.45" },
            Body2 = new Body2Typography { FontFamily = Fonts, FontSize = "0.8125rem", LineHeight = "1.45" },
            Button = new ButtonTypography { FontFamily = Fonts, FontSize = "0.8125rem", FontWeight = "500", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = Fonts, FontSize = "0.75rem", LineHeight = "1.4" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            DrawerMiniWidthLeft = "56px",
            DrawerWidthLeft = "200px",
        },
    };
}
