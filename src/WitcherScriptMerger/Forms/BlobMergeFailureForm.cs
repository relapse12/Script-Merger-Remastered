using System.Drawing;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

internal sealed class BlobMergeFailureForm : BaseForm
{
	internal TextBox Details { get; }
	internal ModernButton Toggle { get; }
	private readonly TableLayoutPanel layout;
	internal bool DetailsExpanded { get; private set; }

	internal BlobMergeFailureForm( string details )
	{
		LiveText.BindText( this, () => UiText.T( "BlobMerge", "Merge validation failed" ) );
		ShowIcon = false;
		ShowInTaskbar = false;
		MinimizeBox = false;
		MaximizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		Font = new Font( "Segoe UI", 10f );
		ClientSize = new Size( 660, 180 );
		MinimumSize = new Size( 460, 200 );
		layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding( 16 ), ColumnCount = 1, RowCount = 3 };
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Absolute, 44 ) );

		var summary = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size( 620, 0 ), Margin = new Padding( 0, 0, 0, 12 ) };

		LiveText.BindText(
			summary,
			() => UiText.T(
				"BlobMerge",
				"The merged scripts could not be validated. The merge was not applied; your existing files and merges are unchanged. Open Details to see the errors." ) );
		Details = new TextBox
		{
			Name = "txtValidationDetails",
			Dock = DockStyle.Fill,
			ReadOnly = true,
			Multiline = true,
			ScrollBars = ScrollBars.Both,
			WordWrap = false,
			Visible = false,
			Text = details.ReplaceLineEndings( System.Environment.NewLine )
		};
		LiveText.BindAccessibleName( Details, () => UiText.T( "BlobMerge", "Validation details" ) );

		var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
		var close = new ModernButton { AutoSize = true, MinimumSize = new Size( 100, 36 ), DialogResult = DialogResult.OK };

		LiveText.BindText( close, () => UiText.T( "BlobMerge", "Close" ) );
		Toggle = new ModernButton { Name = "btnValidationDetails", AutoSize = true, MinimumSize = new Size( 110, 36 ) };
		LiveText.BindText( Toggle, () => DetailsExpanded ? UiText.T( "BlobMerge", "Details ▴" ) : UiText.T( "BlobMerge", "Details ▾" ) );
		Toggle.Click += ( _, _ ) => ToggleDetails();
		actions.Controls.Add( close );
		actions.Controls.Add( Toggle );
		layout.Controls.Add( summary, 0, 0 );
		layout.Controls.Add( Details, 0, 1 );
		layout.Controls.Add( actions, 0, 2 );
		Controls.Add( layout );
		AcceptButton = close;
		CancelButton = close;
		SizeChanged += (
			_,
			_ ) => summary.MaximumSize = new Size( System.Math.Max( 200, layout.ClientSize.Width - layout.Padding.Horizontal ), 0 );
	}

	internal void ToggleDetails()
	{
		DetailsExpanded = !DetailsExpanded;
		Details.Visible = DetailsExpanded;
		LiveText.BindText( Toggle, () => DetailsExpanded ? UiText.T( "BlobMerge", "Details ▴" ) : UiText.T( "BlobMerge", "Details ▾" ) );
		ClientSize = new Size( ClientSize.Width, (int)( ( DetailsExpanded ? 440 : 180 ) * DeviceDpi / 96f ) );
		Program.UpdateWindowSizeFromWorkingArea( this );
	}
}
