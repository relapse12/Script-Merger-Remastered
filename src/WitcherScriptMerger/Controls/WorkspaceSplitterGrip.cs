using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

internal sealed class WorkspaceSplitterGrip : Panel
{
	private bool hovered;
	private bool isDragging;

	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	internal bool IsDragging
	{
		get => isDragging;
		set
		{
			if ( isDragging == value )
				return;

			isDragging = value;
			Invalidate();
		}
	}

	public WorkspaceSplitterGrip()
	{
		SetStyle( ControlStyles.AllPaintingInWmPaint
			| ControlStyles.OptimizedDoubleBuffer
			| ControlStyles.ResizeRedraw
			| ControlStyles.UserPaint, true );
	}

	protected override void OnMouseEnter( EventArgs e )
	{
		base.OnMouseEnter( e );
		hovered = true;
		Invalidate();
	}

	protected override void OnMouseLeave( EventArgs e )
	{
		base.OnMouseLeave( e );
		hovered = false;
		Invalidate();
	}

	protected override void OnPaintBackground( PaintEventArgs e )
	{
		using SolidBrush background = new SolidBrush( ThemeManager.CurrentTheme.BackColor );

		e.Graphics.FillRectangle( background, ClientRectangle );
	}

	protected override void OnPaint( PaintEventArgs e )
	{
		base.OnPaint( e );

		int lineWidth = Math.Max( 1, LogicalToDeviceUnits( ( hovered || isDragging ) ? 2 : 1 ) );
		Color color = hovered || isDragging
			? ThemeManager.CurrentTheme.AcceptButtonBackColor
			: ThemeManager.CurrentTheme.InputBorderColor;
		using Pen pen = new Pen( color, lineWidth );
		int x = ClientSize.Width / 2;

		e.Graphics.DrawLine(
			pen,
			x,
			LogicalToDeviceUnits( 14 ),
			x,
			Math.Max( LogicalToDeviceUnits( 14 ), ClientSize.Height - LogicalToDeviceUnits( 14 ) ) );
	}
}
