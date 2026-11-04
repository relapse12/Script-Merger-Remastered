#nullable enable

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WitcherScriptMerger.Forms;

internal sealed class OperationProgressForm : BaseForm
{
	private readonly Label status = new Label();
	private readonly ProgressBar progress = new ProgressBar();
	private readonly Button cancel = new Button();
	private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
	private Exception? failure;
	private object? result;

	// Closing the window counts as cancelling, and it stays open until the operation ends.
	public OperationProgressForm( string title )
	{
		Text = title;
		StartPosition = FormStartPosition.CenterParent;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowIcon = false;
		ClientSize = new Size( 560, 150 );
		status.SetBounds( 16, 16, 528, 48 );
		status.AutoEllipsis = true;
		LiveText.BindText( status, () => UiText.T( "OperationProgressForm", "Starting..." ) );
		progress.SetBounds( 16, 72, 528, 20 );
		progress.Style = ProgressBarStyle.Marquee;
		LiveText.BindText( cancel, () => UiText.T( "OperationProgressForm", "Cancel" ) );
		cancel.SetBounds( 444, 108, 100, 30 );
		cancel.Click += ( _, _ ) =>
		{
			cancel.Enabled = false;
			LiveText.BindText( status, () => UiText.T( "OperationProgressForm", "Canceling and stopping child processes..." ) );
			cancellation.Cancel();
		};
		Controls.Add( status );
		Controls.Add( progress );
		Controls.Add( cancel );
		FormClosing += ( _, e ) =>
		{
			if ( DialogResult == DialogResult.None )
			{
				e.Cancel = true;
				cancel.PerformClick();
			}
		};
	}

	public T Run<T>( IWin32Window owner, Func<IProgress<string>, CancellationToken, Task<T>> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		Shown += async ( _, _ ) =>
		{
			try
			{
				IProgress<string> reporter = new Progress<string>( message => status.Text = message );

				result = await operation( reporter, cancellation.Token ).ConfigureAwait( true );
				DialogResult = DialogResult.OK;
			}
			catch ( Exception exception )
			{
				failure = exception;
				DialogResult = exception is OperationCanceledException ? DialogResult.Cancel : DialogResult.Abort;
			}
			finally
			{
				Close();
			}
		};
		ShowDialog( owner );

		if ( failure != null )
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture( failure ).Throw();
		}

		return (T)result!;
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing )
		{
			cancellation.Dispose();
		}

		base.Dispose( disposing );
	}
}
