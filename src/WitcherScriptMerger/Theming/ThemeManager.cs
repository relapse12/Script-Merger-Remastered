using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.Forms;

namespace WitcherScriptMerger.Theming;

internal partial class ThemeManager
{
	private enum ButtonRole
	{
		Standard,
		Accept,
		Danger
	}

	private sealed class RoundedButtonState
	{
		public int Radius;
	}

	private static readonly ConditionalWeakTable<Button, RoundedButtonState> RoundedButtons = new();

	public Theme LightTheme;

	public Theme DarkTheme;

	public static Theme CurrentTheme;

	public static Color DefaultAccentColor = Colors.GetColorByHex( "0A84FF" );

	// Mod.io retains a fixed source color across themes.
	public static readonly Color ModIoColor = Colors.GetColorByHex( "07C1D8" );

	public ThemeManager( Func<bool> windowsUsesLightTheme = null )
	{
		readWindowsLightTheme = windowsUsesLightTheme ?? ReadWindowsLightTheme;
		LightTheme = new Theme
		{
			ThemeName = "Light",
			ForeColor = Colors.GetColorByHex( "24292F" ),
			ForeDimmedColor = Colors.GetColorByHex( "57606A" ),
			ForeBrightColor = Colors.GetColorByHex( "1F2328" ),
			BackColor = Colors.GetColorByHex( "EEF1F4" ),
			OptionsPanelBackColor = Color.White,
			BackgroundPanelBackColor = Colors.GetColorByHex( "FFFFFF" ),
			LinkColor = Colors.GetColorByHex( "0969DA" ),
			BorderColor = Colors.GetColorByHex( "D0D7DE" ),
			CardBorderColor = Colors.GetColorByHex( "DDE2E7" ),
			SuccessColor = Colors.GetColorByHex( "1A7F37" ),
			StandardButtonBackColor = Colors.GetColorByHex( "F6F8FA" ),
			StandardButtonForeColor = Colors.GetColorByHex( "24292F" ),
			StandardButtonHoverColor = Colors.GetColorByHex( "EAEEF2" ),
			StandardButtonDownColor = Colors.GetColorByHex( "D8DEE4" ),
			InputBackColor = Colors.GetColorByHex( "FFFFFF" ),
			InputBorderColor = Colors.GetColorByHex( "AFB8C1" ),
			AcceptButtonBackColor = Colors.GetColorByHex( "0969DA" ),
			AcceptButtonForeColor = Colors.GetColorByHex( "FFFFFF" ),
			AcceptButtonHoverColor = Colors.GetColorByHex( "0860CA" ),
			AcceptButtonDownColor = Colors.GetColorByHex( "0757BA" ),
			DangerButtonBackColor = Colors.GetColorByHex( "CF222E" ),
			DangerButtonForeColor = Colors.GetColorByHex( "FFFFFF" ),
			DangerButtonHoverColor = Colors.GetColorByHex( "B91C27" ),
			DangerButtonDownColor = Colors.GetColorByHex( "A40E1A" ),
			DangerOutlineColor = Colors.GetColorByHex( "CF222E" ),
			MenuBackColor = Colors.GetColorByHex( "FFFFFF" ),
			MenuForeColor = Colors.GetColorByHex( "24292F" ),
			MenuSelectedBackColor = Colors.GetColorByHex( "EAEEF2" ),
			MenuSelectedForeColor = DefaultAccentColor,
			TreeViewBackColor = Color.White,
		};
		DarkTheme = new Theme
		{
			ThemeName = "Dark",
			ForeColor = Colors.GetColorByHex( "EEEEF0" ),
			ForeDimmedColor = Colors.GetColorByHex( "A4A5AA" ),
			ForeBrightColor = Colors.GetColorByHex( "FAFAFA" ),
			BackColor = Colors.GetColorByHex( "131416" ),
			OptionsPanelBackColor = Colors.GetColorByHex( "1F2023" ),
			BackgroundPanelBackColor = Colors.GetColorByHex( "1C1D20" ),
			LinkColor = DefaultAccentColor,
			BorderColor = Colors.GetColorByHex( "383A3E" ),
			CardBorderColor = Colors.GetColorByHex( "2B2C2F" ),
			SuccessColor = Colors.GetColorByHex( "34D399" ),
			StandardButtonBackColor = Colors.GetColorByHex( "25272B" ),
			StandardButtonForeColor = Colors.GetColorByHex( "EEEEF0" ),
			StandardButtonHoverColor = Colors.GetColorByHex( "2F3136" ),
			StandardButtonDownColor = Colors.GetColorByHex( "1F2023" ),
			InputBackColor = Colors.GetColorByHex( "25272B" ),
			InputBorderColor = Colors.GetColorByHex( "4A4D53" ),
			AcceptButtonBackColor = DefaultAccentColor,
			AcceptButtonForeColor = Colors.GetColorByHex( "FFFFFF" ),
			AcceptButtonHoverColor = Colors.GetColorByHex( "2996FF" ),
			AcceptButtonDownColor = Colors.GetColorByHex( "0871D9" ),
			DangerButtonBackColor = Colors.GetColorByHex( "C93F43" ),
			DangerButtonForeColor = Colors.GetColorByHex( "FFFFFF" ),
			DangerButtonHoverColor = Colors.GetColorByHex( "D64B50" ),
			DangerButtonDownColor = Colors.GetColorByHex( "A93438" ),
			DangerOutlineColor = Colors.GetColorByHex( "FF7B7F" ),
			MenuBackColor = Colors.GetColorByHex( "1C1D20" ),
			MenuForeColor = Colors.GetColorByHex( "D7D7D9" ),
			MenuSelectedBackColor = Colors.GetColorByHex( "2A2C31" ),
			MenuSelectedForeColor = DefaultAccentColor,
			TreeViewBackColor = Colors.GetColorByHex( "1C1D20" ),
		};
		SetCurrentTheme();
		UpdateThemeWithAccentColor();
	}

	// 1 is light and 2 is dark. Anything else follows Windows, with dark when that cannot be read.
	private void SetCurrentTheme()
	{
		switch ( Program.Settings.Get<int>( "ColorTheme" ) )
		{
			case 1:
				CurrentTheme = LightTheme;
				return;

			case 2:
				CurrentTheme = DarkTheme;
				return;
		}

		CurrentTheme = DarkTheme;

		try
		{
			CurrentTheme = readWindowsLightTheme() ? LightTheme : DarkTheme;
		}
		catch ( Exception )
		{
		}
	}

	private void UpdateThemeWithAccentColor()
	{
		Color accentColor = Environment.OSVersion.Version.Major >= 10 && Program.Settings.Get<bool>( "AccentColor" )
			? Colors.GetAccentColorFromRegistry()
			: GetConfiguredCustomAccentColor();

		ApplyAccentColor( accentColor );
	}

	internal void ApplyAccentColor( Color accentColor )
	{
		accentColor = Color.FromArgb( 255, accentColor.R, accentColor.G, accentColor.B );
		ApplyAccentColor( LightTheme, accentColor );
		ApplyAccentColor( DarkTheme, accentColor );
	}

	private static void ApplyAccentColor( Theme theme, Color accentColor )
	{
		theme.AcceptButtonBackColor = accentColor;
		theme.AcceptButtonHoverColor = Colors.IsLightColor( accentColor )
			? ControlPaint.Dark( accentColor, 0.06f )
			: ControlPaint.Light( accentColor, 0.12f );
		theme.AcceptButtonDownColor = ControlPaint.Dark( accentColor, 0.12f );
		theme.AcceptButtonForeColor = Colors.IsLightColor( accentColor ) ? Color.Black : Color.White;
		theme.LinkColor = accentColor;
		theme.MenuSelectedForeColor = accentColor;
	}

	internal static Color GetConfiguredCustomAccentColor()
	{
		string configuredColor = Program.Settings.Get( "CustomAccentColor" );

		if ( !string.IsNullOrWhiteSpace( configuredColor ) )
		{
			try
			{
				return Colors.GetColorByHex( configuredColor.Trim() );
			}
			catch
			{
			}
		}

		return DefaultAccentColor;
	}

	// Use outlines for pane headers and destructive actions, and a filled background for primary actions.
	private static void ModernizeButton( Button button, Form form, ButtonRole role )
	{
		int cornerRadius = ThemeUtils.GetDpiAwarePixels( 8, form );
		bool isMainAction = button.Name is "btnCreateMerges" or "btnCreateAllScriptMerges" or "btnDeleteMerges" or "btnDeleteAllMerges";
		bool isPaneHeaderButton = button.Name is "btnMainMenu" or "btnRefreshConflicts" or "btnRefreshMerged" or "btnScanDiagnostics";
		string displayText = button.Text.Replace( "&", string.Empty, StringComparison.Ordinal );
		Size textSize = TextRenderer.MeasureText( displayText, button.Font, Size.Empty,
			TextFormatFlags.NoPadding | TextFormatFlags.SingleLine );
		int requiredWidth = textSize.Width + ThemeUtils.GetDpiAwarePixels( isMainAction ? 28 : 24, form );
		int requiredHeight = textSize.Height + ThemeUtils.GetDpiAwarePixels( 12, form );

		if ( isMainAction )
		{
			button.MinimumSize = new Size(
				Math.Max( ThemeUtils.GetDpiAwarePixels( 170, form ), requiredWidth ),
				Math.Max( ThemeUtils.GetDpiAwarePixels( 42, form ), requiredHeight ) );
		}
		else
		{
			button.MinimumSize = new Size(
				Math.Max( button.MinimumSize.Width, requiredWidth ),
				Math.Max( button.MinimumSize.Height, Math.Max( ThemeUtils.GetDpiAwarePixels( 34, form ), requiredHeight ) ) );
		}

		button.BackColor = role switch
		{
			ButtonRole.Accept => CurrentTheme.AcceptButtonBackColor,
			ButtonRole.Danger => CurrentTheme.BackgroundPanelBackColor,
			_ => isPaneHeaderButton ? CurrentTheme.BackgroundPanelBackColor : CurrentTheme.StandardButtonBackColor
		};
		button.ForeColor = role switch
		{
			ButtonRole.Accept => CurrentTheme.AcceptButtonForeColor,
			ButtonRole.Danger => CurrentTheme.DangerOutlineColor,
			_ => CurrentTheme.StandardButtonForeColor
		};
		button.FlatAppearance.BorderSize = role == ButtonRole.Danger || role == ButtonRole.Standard && !isMainAction ? 1 : 0;
		button.FlatAppearance.BorderColor = role == ButtonRole.Danger ? CurrentTheme.DangerOutlineColor : CurrentTheme.BorderColor;
		button.FlatStyle = FlatStyle.Flat;
		button.UseVisualStyleBackColor = false;
		button.Cursor = Cursors.Hand;
		button.Padding = button is ModernButton
			? Padding.Empty
			: new Padding( ThemeUtils.GetDpiAwarePixels( 12, form ), 0, ThemeUtils.GetDpiAwarePixels( 12, form ), 0 );
		button.TextAlign = ContentAlignment.MiddleCenter;
		button.FlatAppearance.MouseOverBackColor = role switch
		{
			ButtonRole.Accept => CurrentTheme.AcceptButtonHoverColor,
			ButtonRole.Danger => CurrentTheme.DangerButtonHoverColor,
			_ => CurrentTheme.StandardButtonHoverColor
		};
		button.FlatAppearance.MouseDownBackColor = role switch
		{
			ButtonRole.Accept => CurrentTheme.AcceptButtonDownColor,
			ButtonRole.Danger => CurrentTheme.DangerButtonDownColor,
			_ => CurrentTheme.StandardButtonDownColor
		};

		if ( button is ModernButton modernButton )
		{
			modernButton.CornerRadius = cornerRadius;
			modernButton.ActiveForeColor = role == ButtonRole.Danger ? CurrentTheme.DangerButtonForeColor : Color.Empty;

			Region oldRegion = modernButton.Region;

			modernButton.Region = null;
			oldRegion?.Dispose();

			return;
		}

		RoundedButtonState state = RoundedButtons.GetValue( button, control =>
		{
			RoundedButtonState created = new();

			control.Resize += ( _, _ ) => UpdateRoundedButtonRegion( control, created.Radius );

			return created;
		} );

		state.Radius = cornerRadius;
		UpdateRoundedButtonRegion( button, cornerRadius );
	}

	private static void UpdateRoundedButtonRegion( Button button, int radius )
	{
		if ( button.Width <= 0 || button.Height <= 0 )
		{
			return;
		}

		int diameter = Math.Max( 2, Math.Min( radius * 2, Math.Min( button.Width, button.Height ) ) );
		Rectangle bounds = new( 0, 0, button.Width, button.Height );
		using GraphicsPath path = new();

		path.AddArc( bounds.Left, bounds.Top, diameter, diameter, 180f, 90f );
		path.AddArc( bounds.Right - diameter - 1, bounds.Top, diameter, diameter, 270f, 90f );
		path.AddArc( bounds.Right - diameter - 1, bounds.Bottom - diameter - 1, diameter, diameter, 0f, 90f );
		path.AddArc( bounds.Left, bounds.Bottom - diameter - 1, diameter, diameter, 90f, 90f );
		path.CloseFigure();

		Region oldRegion = button.Region;

		button.Region = new Region( path );
		oldRegion?.Dispose();
	}

	public void AttachColorFaderToCheckBoxes(
		Control parentControl,
		out List<ColorFader> colorFaders,
		int? fadeInDuration = null,
		int? fadeOutDuration = null )
	{
		colorFaders = new List<ColorFader>();

		foreach ( Control control in parentControl.Controls )
		{
			if ( control is Panel panel )
			{
				AttachColorFaderToCheckBoxes( panel, out var childFaders, fadeInDuration, fadeOutDuration );
				colorFaders.AddRange( childFaders );
			}
			else if ( control is CheckBox checkBox )
			{
				ColorFader colorFader = new ColorFader( checkBox, "ForeColor", fadeInDuration ?? 700, fadeOutDuration ?? 700 );

				checkBox.MouseEnter += delegate
				{
					colorFader.StartFadeIn( CurrentTheme.ForeBrightColor );
				};
				checkBox.MouseLeave += delegate
				{
					colorFader.StartFadeOut( CurrentTheme.ForeDimmedColor );
				};
				colorFaders.Add( colorFader );
			}
		}
	}

	public static void AttachColorFaderToTreeNodes( TreeView treeView, out List<TreeNodeColorFader> treeNodeColorFaders )
	{
		List<TreeNodeColorFader> treeNodeColorFadersLocal = new List<TreeNodeColorFader>();

		TraverseTreeNodes( treeView.Nodes );
		treeNodeColorFaders = treeNodeColorFadersLocal;

		void TraverseTreeNodes( TreeNodeCollection nodes )
		{
			foreach ( TreeNode node in nodes )
			{
				if ( node.Nodes.Count == 0 )
				{
					TreeNodeColorFader treeNodeColorFader = new TreeNodeColorFader( node, "ForeColor" );

					treeNodeColorFadersLocal.Add( treeNodeColorFader );
				}

				TraverseTreeNodes( node.Nodes );
			}
		}
	}

	public void ApplyThemeOnForm( Form form )
	{
		if ( form == null )
		{
			return;
		}

		if ( form.InvokeRequired )
		{
			form.Invoke( delegate
			{
				ApplyThemeOnForm( form );
			} );

			return;
		}

		UiTypography.Apply( form );
		themedForms.GetOrCreateValue( form );

		if ( form is OptionsForm options )
			options.StopColorFades();

		form.ForeColor = CurrentTheme.ForeColor;
		form.BackColor = CurrentTheme.BackColor;
		ApplyThemeToControls( form.Controls, form );
		ThemeUtils.ApplyTitleBarTheme( form, CurrentTheme.ThemeName == "Dark" );

		if ( form is MainForm main )
		{
			main.RefreshNoConflictsAppearance();
			main.RefreshModernLayoutMetrics();
			main.MinimumSize = Size.Empty;
		}
	}

	private void ApplyThemeToControls( Control.ControlCollection controls, Form form )
	{
		foreach ( Control control in controls )
		{
			if ( control.InvokeRequired )
			{
				control.Invoke( delegate
				{
					ApplyThemeToControls( control.Controls, form );
				} );

				continue;
			}

			if ( control is Button button )
			{
				ButtonRole role = button.Name == "btnDeleteAllMerges"
					? ButtonRole.Danger
					: button.Name == "btnCreateAllScriptMerges"
						|| button.Name == "btnExitNow"
						|| button.DialogResult == DialogResult.OK
						|| ReferenceEquals( form.AcceptButton, button )
						? ButtonRole.Accept
						: ButtonRole.Standard;

				ModernizeButton( button, form, role );
			}
			else if ( control is Label && (string)control.Tag == "AccentLabel" )
			{
				control.ForeColor = CurrentTheme.AcceptButtonBackColor;
			}
			else if ( control is Label && control.Tag?.ToString() == "DimmedLabel" )
			{
				control.ForeColor = CurrentTheme.ForeDimmedColor;
				control.BackColor = CurrentTheme.BackColor;
			}
			else if ( control is Label && control.Name == "lblNoConflicts" )
			{
				control.ForeColor = CurrentTheme.ForeDimmedColor;
				control.BackColor = control.Parent?.BackColor ?? CurrentTheme.BackgroundPanelBackColor;
			}
			else if ( control is Label label )
			{
				label.BackColor = label.Parent?.BackColor ?? CurrentTheme.BackColor;
				label.ForeColor = CurrentTheme.ForeColor;
			}
			else if ( control is TreeView tv )
			{
				ApplyTreeTheme( tv, form );
			}
			else if ( control is StatusStrip strip )
			{
				strip.ForeColor = CurrentTheme.ForeDimmedColor;
				strip.BackColor = CurrentTheme.BackgroundPanelBackColor;
				strip.Padding = new Padding( ThemeUtils.GetDpiAwarePixels( 8, form ), 0, ThemeUtils.GetDpiAwarePixels( 8, form ), 0 );
				strip.MinimumSize = new Size( 0, ThemeUtils.GetDpiAwarePixels( 36, form ) );
			}
			else if ( control is ListBox list )
			{
				list.BackColor = CurrentTheme.InputBackColor;
				list.ForeColor = CurrentTheme.ForeColor;
			}
			else if ( control is TextBox tb )
			{
				tb.BorderStyle = tb.Parent is ModernTextBoxHost ? BorderStyle.None : BorderStyle.FixedSingle;
				tb.BackColor = CurrentTheme.InputBackColor;
				tb.ForeColor = CurrentTheme.ForeColor;
				tb.MinimumSize = new Size(
					tb.MinimumSize.Width,
					tb.Parent is ModernTextBoxHost ? 0 : ThemeUtils.GetDpiAwarePixels( 34, form ) );
			}
			else if ( control is ModernTextBoxHost inputHost )
			{
				inputHost.ApplyTheme( CurrentTheme );
			}
			else if ( control is ComboBox comboBox )
			{
				comboBox.FlatStyle = FlatStyle.Flat;
				comboBox.BackColor = CurrentTheme.InputBackColor;
				comboBox.ForeColor = CurrentTheme.ForeColor;
			}
			else if ( control is NumericUpDown numericUpDown )
			{
				numericUpDown.BorderStyle = BorderStyle.FixedSingle;
				numericUpDown.BackColor = CurrentTheme.InputBackColor;
				numericUpDown.ForeColor = CurrentTheme.ForeColor;
			}
			else if ( control is Panel pnl && !( pnl is TableLayoutPanel ) && pnl.Name != "pnlProgress" )
			{
				if ( pnl?.Tag?.ToString() == "DialogBar" )
				{
					pnl.BackColor = CurrentTheme.BackColor;
				}

				ApplyThemeToControls( pnl.Controls, form );
			}
			else if ( control is CheckBox && form is OptionsForm )
			{
				control.ForeColor = CurrentTheme.ForeDimmedColor;
				control.BackColor = CurrentTheme.BackColor;
			}
			else
			{
				bool isWorkspaceSurface = control.Name is "tlpConflictsHeader"
					or "tlpConflictTitle"
					or "tlpMergesHeader"
					or "tlpConflictSearch"
					or "tlpMergeSearch"
					or "tlpConflictActions"
					or "tlpMergeActions";

				control.BackColor = isWorkspaceSurface ? CurrentTheme.BackgroundPanelBackColor : CurrentTheme.BackColor;
				control.ForeColor = CurrentTheme.ForeColor;

				if ( control.HasChildren )
				{
					ApplyThemeToControls( control.Controls, form );
				}
			}
		}
	}

	internal void ApplyTreeTheme( TreeView tree, Form form )
	{
		tree.ForeColor = CurrentTheme.ForeDimmedColor;
		tree.BackColor = CurrentTheme.TreeViewBackColor;
		tree.BorderStyle = BorderStyle.None;
		tree.FullRowSelect = tree is not StyledTreeView;
		tree.HideSelection = false;
		tree.Indent = ThemeUtils.GetDpiAwarePixels( 24, form );
		ThemeUtils.ApplyExplorerTheme( tree, CurrentTheme.ThemeName == "Dark" );

		if ( tree is StyledTreeView styledTree )
			styledTree.ApplyListSize();
		else
			tree.ItemHeight = (int)( 30f * ThemeUtils.GetDpiScalingFactor( form.Handle ) );
	}
}
