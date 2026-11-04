using System;
using System.Windows.Forms;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	// Progress can arrive faster than the UI can paint. Limit updates within a phase to one per 100 ms.
	private Action<string, string> CreateScriptProgressReporter()
	{
		string lastPhase = "";
		long lastProgress = 0;

		return ( phase, action ) =>
		{
			long now = Environment.TickCount64;

			if ( phase == lastPhase && now - lastProgress < 100 )
				return;

			lastPhase = phase;
			lastProgress = now;

			void UpdateProgress()
			{
				lblProgressCurrentPhase.Text = phase;
				lblProgressCurrentAction.Text = action;
			}

			if ( InvokeRequired )
				Invoke( (MethodInvoker)UpdateProgress );
			else
				UpdateProgress();
		};
	}
}
