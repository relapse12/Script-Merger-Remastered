#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace WitcherScriptMerger.Tools;

internal enum ProcessOutputStream
{
	StandardOutput,
	StandardError
}

internal sealed record ProcessOutputLine(
	ProcessOutputStream Stream,
	string Text,
	DateTimeOffset Timestamp );

// Pass arguments separately without shell quoting. A null environment value removes an inherited variable.
// The caller creates and owns the working directory.
internal sealed class ProcessRunRequest
{
	public required string FileName { get; init; }

	public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

	public string? WorkingDirectory { get; init; }

	public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

	public TimeSpan? Timeout { get; init; }

	public IProgress<ProcessOutputLine>? Progress { get; init; }

	public bool ShowWindow { get; init; }
}

// Callers interpret nonzero exit codes. Cancellation and timeout results may contain partial output.
internal sealed class ProcessRunResult
{
	public ProcessRunResult(
		ProcessRunRequest request,
		int exitCode,
		string standardOutput,
		string standardError,
		DateTimeOffset startedAt,
		TimeSpan duration,
		bool wasCanceled,
		bool timedOut )
	{
		Request = request;
		ExitCode = exitCode;
		StandardOutput = standardOutput;
		StandardError = standardError;
		StartedAt = startedAt;
		Duration = duration;
		WasCanceled = wasCanceled;
		TimedOut = timedOut;
	}

	public ProcessRunRequest Request { get; }

	public int ExitCode { get; }

	public string StandardOutput { get; }

	public string StandardError { get; }

	public DateTimeOffset StartedAt { get; }

	public TimeSpan Duration { get; }

	public bool WasCanceled { get; }

	public bool TimedOut { get; }

	public bool Succeeded => ExitCode == 0 && !WasCanceled && !TimedOut;

	public string CombinedOutput
	{
		get
		{
			if ( string.IsNullOrWhiteSpace( StandardOutput ) )
			{
				return StandardError;
			}

			if ( string.IsNullOrWhiteSpace( StandardError ) )
			{
				return StandardOutput;
			}

			return StandardOutput.TrimEnd() + Environment.NewLine + StandardError;
		}
	}
}

internal sealed class ProcessRunCanceledException : OperationCanceledException
{
	public ProcessRunCanceledException( ProcessRunResult result, CancellationToken cancellationToken )
		: base( "The external process was canceled and termination of its process tree was requested.", null, cancellationToken )
	{
		Result = result;
	}

	public ProcessRunResult Result { get; }
}

internal sealed class ProcessRunTimedOutException : TimeoutException
{
	public ProcessRunTimedOutException( ProcessRunResult result )
		: base( $"The external process exceeded its timeout of {result.Request.Timeout}; termination of its process tree was requested." )
	{
		Result = result;
	}

	public ProcessRunResult Result { get; }
}
