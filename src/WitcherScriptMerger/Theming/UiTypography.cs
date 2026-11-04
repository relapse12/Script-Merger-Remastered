using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Theming;

// Track adjusted controls so opening or reapplying a theme cannot enlarge their fonts again.
internal static class UiTypography
{
	private const float SizeIncrease = 0.75f;

	private static readonly ConditionalWeakTable<Control, object> adjustedControls = new();

	// Styled trees manage their own fonts. Children that inherit a font already received the parent's size change.
	internal static void Apply( Control root )
	{
		List<(Control Control, Font Font, bool InheritsAdjustedFont)> snapshot = new();

		Collect( root, snapshot );

		foreach ( (Control control, Font font, bool inheritsAdjustedFont) in snapshot )
		{
			if ( adjustedControls.TryGetValue( control, out _ ) )
				continue;

			adjustedControls.Add( control, new object() );

			if ( control is StyledTreeView || inheritsAdjustedFont || font.SizeInPoints > 20f )
				continue;

			control.Font = new Font( font.FontFamily, font.SizeInPoints + SizeIncrease,
				font.Style, GraphicsUnit.Point, font.GdiCharSet, font.GdiVerticalFont );
		}
	}

	private static void Collect( Control control, List<(Control, Font, bool)> snapshot )
	{
		bool inheritsAdjustedFont = control.Parent != null
			&& adjustedControls.TryGetValue( control.Parent, out _ )
			&& control.Font.Equals( control.Parent.Font );

		snapshot.Add( (control, control.Font, inheritsAdjustedFont) );

		foreach ( Control child in control.Controls )
			Collect( child, snapshot );
	}
}
