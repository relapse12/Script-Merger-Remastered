using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WitcherScriptMerger.Forms;

internal sealed class ScanIssuesForm : BaseForm
{
	private readonly Label explanation;
	private readonly TextBox details;
	private IReadOnlyList<string> currentIssues;
	private string divider = "";
	private bool refreshQueued;

	[StructLayout( LayoutKind.Sequential )]
	private struct TextRectangle
	{
		public int Left, Top, Right, Bottom;
	}

	[DllImport( "user32.dll", EntryPoint = "SendMessageW" )]
	private static extern nint SendMessage( nint window, int message, nint parameter, nint value );

	[DllImport( "user32.dll", EntryPoint = "SendMessageW" )]
	private static extern nint SendMessage( nint window, int message, nint parameter, ref TextRectangle value );

	// Native multiline edit controls need CRLF to preserve report paragraphs.
	internal static string FormatIssueText( IReadOnlyList<string> issues, string divider ) => string.Join(
		Environment.NewLine + Environment.NewLine + divider + Environment.NewLine + Environment.NewLine,
		issues.Select( issue => issue.ReplaceLineEndings( Environment.NewLine ) ) );

	internal ScanIssuesForm( IReadOnlyList<string> issues )
	{
		ArgumentNullException.ThrowIfNull( issues );
		currentIssues = issues.ToArray();
		LiveText.BindText( this, () => UiText.T( "ScanIssuesForm", "Diagnostics — Script Merger Remastered" ) );
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;
		MinimumSize = new Size( 540, 280 );
		ClientSize = new Size( 740, 350 );

		TableLayoutPanel layout = new TableLayoutPanel()
		{
			ColumnCount = 1,
			Dock = DockStyle.Fill,
			Padding = new Padding( 16 ),
			RowCount = 3
		};

		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Absolute, 54 ) );
		explanation = new Label()
		{
			AutoSize = true,
			MinimumSize = new Size( 0, 62 ),
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 10.5f ),
			Name = "lblScanIssueExplanation",
			Text = issues.Count == 1
				? UiText.F( "ScanIssuesForm", $"Scan complete. {issues.Count} diagnostic found. Review the details below." )
				: UiText.F( "ScanIssuesForm", $"Scan complete. {issues.Count} diagnostics found. Review the details below." ),
			TextAlign = ContentAlignment.MiddleLeft
		};
		details = LiveText.Live( new TextBox()
		{
			AccessibleName = UiText.T( "ScanIssuesForm", "Scan issue details" ),
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 10f ),
			Multiline = true,
			Name = "txtScanIssueDetails",
			ReadOnly = true,
			ScrollBars = ScrollBars.Vertical,
			WordWrap = true
		}, control => { LiveText.BindAccessibleName( control, () => UiText.T( "ScanIssuesForm", "Scan issue details" ) ); } );
		details.SizeChanged += ( _, _ ) => QueueRefreshDetails();
		details.FontChanged += ( _, _ ) => QueueRefreshDetails();
		details.DpiChangedAfterParent += ( _, _ ) => QueueRefreshDetails();
		details.HandleCreated += ( _, _ ) => { RefreshDetails(); QueueRefreshDetails(); };

		FlowLayoutPanel actions = new FlowLayoutPanel()
		{
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = false
		};
		Button close = LiveText.Live( new Button()
		{
			DialogResult = DialogResult.OK,
			Font = new Font( "Segoe UI", 10f ),
			Name = "btnCloseScanIssues",
			Size = new Size( 110, 38 ),
			Text = UiText.T( "ScanIssuesForm", "Close" )
		}, control => { LiveText.BindText( control, () => UiText.T( "ScanIssuesForm", "Close" ) ); } );

		actions.Controls.Add( close );
		layout.Controls.Add( explanation, 0, 0 );
		layout.Controls.Add( details, 0, 1 );
		layout.Controls.Add( actions, 0, 2 );
		Controls.Add( layout );
		RefreshDetails();
		AcceptButton = close;
		CancelButton = close;
		Shown += ( _, _ ) => close.Focus();
	}

	internal void UpdateIssues( IReadOnlyList<string> issues )
	{
		currentIssues = issues.ToArray();
		RefreshDetails();
		explanation.Text = issues.Count == 0 ? UiText.T( "ScanIssuesForm", "No diagnostics remain." )
			: issues.Count == 1
				? UiText.F( "ScanIssuesForm", $"Scan complete. {issues.Count} diagnostic found. Review the details below." )
				: UiText.F( "ScanIssuesForm", $"Scan complete. {issues.Count} diagnostics found. Review the details below." );
	}

	private void QueueRefreshDetails()
	{
		if ( refreshQueued || !details.IsHandleCreated )
			return;

		refreshQueued = true;
		details.BeginInvoke( new Action( () =>
		{
			refreshQueued = false;

			if ( !details.IsDisposed )
				RefreshDetails();
		} ) );
	}

	// The scrollbar reduces the usable width, so measure after it has been laid out.
	private void RefreshDetails()
	{
		if ( !details.IsHandleCreated )
			return;

		TextRectangle bounds = default;

		SendMessage( details.Handle, 0xB2, 0, ref bounds ); // EM_GETRECT

		int width = Math.Max( 0, bounds.Right - bounds.Left - 1 );
		const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
		using Graphics graphics = details.CreateGraphics();

		int Measure( int count ) => TextRenderer.MeasureText( graphics, new string( '-', count ), details.Font, Size.Empty, flags ).Width;

		int length = width / Math.Max( 1, Measure( 1 ) );

		while ( length > 0 && Measure( length ) > width )
			length--;

		while ( Measure( length + 1 ) <= width )
			length++;

		string nextDivider = new string( '-', length );
		string text = FormatIssueText( currentIssues, nextDivider );

		if ( details.Text == text )
		{
			divider = nextDivider;

			return;
		}

		bool resizing = details.Text == FormatIssueText( currentIssues, divider );

		int Position( int position )
		{
			int offset = 0, shift = 0;

			foreach ( string issue in currentIssues.Take( Math.Max( 0, currentIssues.Count - 1 ) ) )
			{
				offset += issue.ReplaceLineEndings( Environment.NewLine ).Length + 2 * Environment.NewLine.Length;

				if ( position <= offset )
					break;

				if ( position < offset + divider.Length )
					return offset + shift + Math.Min( position - offset, length );

				shift += length - divider.Length;
				offset += divider.Length + 2 * Environment.NewLine.Length;
			}

			return position + shift;
		}

		int start = resizing ? Position( details.SelectionStart ) : 0;
		int end = resizing ? Position( details.SelectionStart + details.SelectionLength ) : 0;
		int firstLine = (int)SendMessage( details.Handle, 0xCE, 0, 0 ); // EM_GETFIRSTVISIBLELINE

		divider = nextDivider;
		details.Text = text;

		if ( !resizing )
			return;

		details.Select( start, end - start );
		SendMessage( details.Handle, 0xB6, 0, firstLine - (int)SendMessage( details.Handle, 0xCE, 0, 0 ) ); // EM_LINESCROLL
	}
}
