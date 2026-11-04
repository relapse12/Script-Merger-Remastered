using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace WitcherScriptMerger.Controls;

internal sealed class OptionsStackPanel : Panel
{
	private bool arranging;

	public OptionsStackPanel()
	{
		AutoSize = false;
		Dock = DockStyle.Top;
		Margin = Padding.Empty;
		DoubleBuffered = true;
	}

	private int RowHeight( Control child, int width )
	{
		if ( child is Label || child is CheckBox )
		{
			return Math.Max( child.MinimumSize.Height,
				child.GetPreferredSize( new Size( Math.Max( 1, width ), 0 ) ).Height + LogicalToDeviceUnits( 2 ) );
		}

		return child.GetPreferredSize( new Size( width, 0 ) ).Height;
	}

	public override Size GetPreferredSize( Size proposedSize )
	{
		int width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue
			? proposedSize.Width : Width;
		int height = Padding.Vertical;

		foreach ( Control child in Controls )
			height += RowHeight( child, Math.Max( 1, width - Padding.Horizontal - child.Margin.Horizontal ) ) + child.Margin.Vertical;

		return new Size( width, height );
	}

	// Moving a child can trigger layout again before this pass has finished.
	protected override void OnLayout( LayoutEventArgs e )
	{
		if ( arranging )
			return;

		arranging = true;

		try
		{
			base.OnLayout( e );

			int y = Padding.Top;

			foreach ( Control child in Controls )
			{
				int width = Math.Max( 1, ClientSize.Width - Padding.Horizontal - child.Margin.Horizontal );
				int height = RowHeight( child, width );

				child.SetBounds( Padding.Left + child.Margin.Left, y + child.Margin.Top, width, height );
				y += height + child.Margin.Vertical;
			}

			if ( Dock == DockStyle.Top )
				Height = y + Padding.Bottom;
		}
		finally { arranging = false; }
	}
}

internal sealed class OptionsColumnsPanel : Panel
{
	private bool arranging;

	[DefaultValue( false )]
	internal bool Stacked { get; set; }
	internal int ColumnCount => Stacked ? 1 : 2;

	public override Size GetPreferredSize( Size proposedSize )
	{
		int width = proposedSize.Width > 0 ? proposedSize.Width : Width;
		int height = 0;

		foreach ( Control child in Controls )
		{
			int childHeight = child.GetPreferredSize( new Size( Math.Max( 1, width / ColumnCount ), 0 ) ).Height;

			height = Stacked ? height + childHeight : Math.Max( height, childHeight );
		}

		return new Size( width, height );
	}

	protected override void OnLayout( LayoutEventArgs e )
	{
		if ( arranging )
			return;

		arranging = true;

		try
		{
			base.OnLayout( e );

			int offset = 0;

			foreach ( Control child in Controls )
			{
				int width = Math.Max( 1, ClientSize.Width / ColumnCount );
				int height = child.GetPreferredSize( new Size( width, 0 ) ).Height;

				child.SetBounds( Stacked ? 0 : offset, Stacked ? offset : 0, width, height );
				offset += Stacked ? height : width;
			}
		}
		finally { arranging = false; }
	}
}
