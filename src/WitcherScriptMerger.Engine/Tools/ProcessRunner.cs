#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WitcherScriptMerger.Tools;

internal interface IProcessRunner
{
	Task<ProcessRunResult> RunAsync( ProcessRunRequest request, CancellationToken cancellationToken = default );
}

internal sealed class ProcessRunner : IProcessRunner
{
	// Drain both output streams before returning. Cancellation and timeout exceptions retain any output collected.
	public async Task<ProcessRunResult> RunAsync( ProcessRunRequest request, CancellationToken cancellationToken = default )
	{
		ValidateRequest( request, cancellationToken );

		using Process process = new()
		{
			StartInfo = BuildStartInfo( request ),
			EnableRaisingEvents = true
		};
		DateTimeOffset startedAt = DateTimeOffset.UtcNow;
		Stopwatch stopwatch = Stopwatch.StartNew();
		StringBuilder stdout = new();
		StringBuilder stderr = new();
		Task stdoutTask;
		Task stderrTask;

		process.Start();
		stdoutTask = PumpAsync( process.StandardOutput, stdout, ProcessOutputStream.StandardOutput, request.Progress );
		stderrTask = PumpAsync( process.StandardError, stderr, ProcessOutputStream.StandardError, request.Progress );

		using CancellationTokenSource? timeoutSource = request.Timeout is { } processTimeout
			? new CancellationTokenSource( processTimeout )
			: null;
		using CancellationTokenSource linkedSource = timeoutSource is null
			? CancellationTokenSource.CreateLinkedTokenSource( cancellationToken )
			: CancellationTokenSource.CreateLinkedTokenSource( cancellationToken, timeoutSource.Token );

		try
		{
			await process.WaitForExitAsync( linkedSource.Token ).ConfigureAwait( false );
			await Task.WhenAll( stdoutTask, stderrTask ).ConfigureAwait( false );
		}
		catch ( OperationCanceledException ) when ( linkedSource.IsCancellationRequested )
		{
			bool canceledByCaller;
			ProcessRunResult canceledResult;

			// Termination can fail, so limit the subsequent waits for exit and redirected output.
			TryKillEntireProcessTree( process );
			await WaitForTerminationAsync( process ).ConfigureAwait( false );
			await IgnorePumpFailuresAsync( stdoutTask, stderrTask ).ConfigureAwait( false );
			stopwatch.Stop();

			canceledByCaller = cancellationToken.IsCancellationRequested;
			canceledResult = new(
				request,
				TryGetExitCode( process ),
				Snapshot( stdout ),
				Snapshot( stderr ),
				startedAt,
				stopwatch.Elapsed,
				canceledByCaller,
				!canceledByCaller );

			if ( canceledByCaller )
				throw new ProcessRunCanceledException( canceledResult, cancellationToken );

			throw new ProcessRunTimedOutException( canceledResult );
		}
		catch
		{
			TryKillEntireProcessTree( process );

			throw;
		}

		stopwatch.Stop();

		return new ProcessRunResult(
			request,
			process.ExitCode,
			stdout.ToString(),
			stderr.ToString(),
			startedAt,
			stopwatch.Elapsed,
			wasCanceled: false,
			timedOut: false );
	}

	private static void ValidateRequest( ProcessRunRequest request, CancellationToken cancellationToken )
	{
		ArgumentNullException.ThrowIfNull( request );
		cancellationToken.ThrowIfCancellationRequested();

		if ( string.IsNullOrWhiteSpace( request.FileName ) )
			throw new ArgumentException( UiText.T( "ProcessRunner", "An executable path is required." ), nameof( request ) );

		if ( request.Timeout is { } timeout && timeout <= TimeSpan.Zero )
			throw new ArgumentOutOfRangeException( nameof( request ), "A process timeout must be positive." );
	}

	private static ProcessStartInfo BuildStartInfo( ProcessRunRequest request )
	{
		string workingDirectory = string.IsNullOrWhiteSpace( request.WorkingDirectory )
			? Path.GetDirectoryName( Path.GetFullPath( request.FileName ) ) ?? Environment.CurrentDirectory
			: Path.GetFullPath( request.WorkingDirectory );

		ProcessStartInfo startInfo = new()
		{
			FileName = request.FileName,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = !request.ShowWindow,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			WindowStyle = request.ShowWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
		};

		foreach ( string argument in request.Arguments )
		{
			startInfo.ArgumentList.Add( argument );
		}

		foreach ( KeyValuePair<string, string?> variable in request.EnvironmentVariables )
		{
			if ( variable.Value is null )
			{
				startInfo.Environment.Remove( variable.Key );
			}
			else
			{
				startInfo.Environment[variable.Key] = variable.Value;
			}
		}

		return startInfo;
	}

	// A failed progress callback must not stop collecting tool output.
	private static async Task PumpAsync(
		StreamReader reader,
		StringBuilder destination,
		ProcessOutputStream stream,
		IProgress<ProcessOutputLine>? progress )
	{
		try
		{
			while ( await reader.ReadLineAsync().ConfigureAwait( false ) is { } line )
			{
				lock ( destination )
				{
					destination.AppendLine( line );
				}

				if ( progress is not null )
				{
					try
					{
						progress.Report( new ProcessOutputLine( stream, line, DateTimeOffset.UtcNow ) );
					}
					catch
					{
					}
				}
			}
		}
		catch ( IOException )
		{
		}
		catch ( ObjectDisposedException )
		{
		}
	}

	// Try the parent alone if terminating the process tree fails.
	private static void TryKillEntireProcessTree( Process process )
	{
		try
		{
			if ( !process.HasExited )
			{
				process.Kill( entireProcessTree: true );
			}
		}
		catch ( InvalidOperationException )
		{
			return;
		}
		catch ( System.ComponentModel.Win32Exception )
		{
			TryKillSingleProcess( process );
		}
		catch ( NotSupportedException )
		{
			TryKillSingleProcess( process );
		}
	}

	private static void TryKillSingleProcess( Process process )
	{
		try
		{
			if ( !process.HasExited )
			{
				process.Kill();
			}
		}
		catch
		{
		}
	}

	// Allow the caller to finish even when process termination fails.
	private static async Task WaitForTerminationAsync( Process process )
	{
		using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 5 ) );

		try
		{
			await process.WaitForExitAsync( timeout.Token ).ConfigureAwait( false );
		}
		catch ( InvalidOperationException )
		{
		}
		catch ( OperationCanceledException )
		{
		}
	}

	// A surviving child can hold a pipe open indefinitely. Stop waiting and observe any later reader failure separately.
	private static async Task IgnorePumpFailuresAsync( params Task[] pumps )
	{
		Task allPumps = Task.WhenAll( pumps );

		try
		{
			Task completed = await Task.WhenAny( allPumps, Task.Delay( TimeSpan.FromSeconds( 2 ) ) ).ConfigureAwait( false );

			if ( completed == allPumps )
			{
				await allPumps.ConfigureAwait( false );
			}
			else
			{
				_ = allPumps.ContinueWith(
					task => _ = task.Exception,
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default );
			}
		}
		catch
		{
		}
	}

	private static string Snapshot( StringBuilder builder )
	{
		lock ( builder )
		{
			return builder.ToString();
		}
	}

	private static int TryGetExitCode( Process process )
	{
		try
		{
			return process.HasExited ? process.ExitCode : -1;
		}
		catch ( InvalidOperationException )
		{
			return -1;
		}
	}
}
