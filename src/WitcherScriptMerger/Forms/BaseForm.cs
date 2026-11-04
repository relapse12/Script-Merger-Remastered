using System;
using System.Windows.Forms;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

// All windows share screen-boundary checks and title-bar theming.
[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
public class BaseForm : Form
{
	public BaseForm()
	{
		InitializeComponent();
	}

	protected override void OnShown( EventArgs e )
	{
		Program.UpdateWindowSizeFromWorkingArea( this );

		if ( !base.DesignMode )
		{
			ThemeUtils.ApplyTitleBarTheme( this, ThemeManager.CurrentTheme?.ThemeName == "Dark" );
		}

		base.OnShown( e );
	}

	protected override void OnDpiChanged( DpiChangedEventArgs e )
	{
		base.OnDpiChanged( e );
		BeginInvoke( new Action( () => Program.UpdateWindowSizeFromWorkingArea( this ) ) );
	}

	private void InitializeComponent()
	{
		base.SuspendLayout();
		base.AutoScaleDimensions = new System.Drawing.SizeF(96f, 96f);
		base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
		base.ClientSize = new System.Drawing.Size(800, 450);
		base.Name = "BaseForm";
		LiveText.BindText(this, () => UiText.T("BaseForm", "BaseForm"));
		base.ResumeLayout(false);
	}
}
