// SPDX-License-Identifier: GPL-3.0-only
// Theme.cs: the few colours and sizes the rigPlay page uses where SimHub's own styles do not reach (fallbacks
// and status marks). Text colours are inherited from SimHub's theme on purpose. Pure: no WPF types here;
// Widgets.cs turns the hex strings into brushes.
namespace RigPlayPlugin
{
    public static class Theme
    {
        public const string Accent = "#A6C9FF";
        public const string Rule = "#40808080";

        public const string StatusIdle = "#9FAEC2";
        public const string StatusOk = "#3DDC84";
        public const string StatusWarn = "#FFB300";

        public const double SizeTitle = 22;
        public const double SizeSection = 16;
        public const double SizeBody = 13;
        public const double CaptionOpacity = 0.7;
        public const double PagePadding = 24;
        public const double PageMaxWidth = 900;
    }
}
