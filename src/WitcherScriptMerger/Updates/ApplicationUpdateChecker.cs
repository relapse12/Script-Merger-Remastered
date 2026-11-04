using System;
using System.Threading;
using System.Threading.Tasks;

namespace WitcherScriptMerger.Updates;

// When a check fails, Release may still contain the last saved result.
internal sealed record ApplicationUpdateResult(
	ApplicationRelease Release,
	string Error,
	string Warning,
	DateTimeOffset? LastSuccessUtc,
	bool Skipped = false );

// Automatic checks run weekly and announce each version once. Manual checks can run immediately.
internal sealed class ApplicationUpdateChecker
{
	private readonly object gate = new();
	private readonly IApplicationReleaseClient client;
	private readonly ApplicationUpdateStore store;
	private readonly string installedVersion;
	private readonly Func<DateTimeOffset> utcNow;
	private ApplicationUpdateState state;
	private Task<ApplicationUpdateResult> activeCheck;

	internal ApplicationUpdateChecker(
		IApplicationReleaseClient client,
		ApplicationUpdateStore store,
		string installedVersion,
		Func<DateTimeOffset> utcNow = null )
	{
		this.client = client;
		this.store = store;
		this.installedVersion = installedVersion;
		this.utcNow = utcNow ?? ( () => DateTimeOffset.UtcNow );
	}

	// Starts a check, or joins the one already running.
	internal Task<ApplicationUpdateResult> CheckAsync( bool manual, CancellationToken cancellationToken )
	{
		lock ( gate )
		{
			if ( activeCheck != null && !activeCheck.IsCompleted )
				return activeCheck;

			activeCheck = Task.Run( () => RunAsync( manual, cancellationToken ), cancellationToken );

			var pending = activeCheck;

			_ = pending.ContinueWith( completed =>
			{
				lock ( gate )
				{
					if ( ReferenceEquals( activeCheck, completed ) )
						activeCheck = null;
				}
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default );

			return pending;
		}
	}

	// Record failed attempts too, so an unavailable server does not cause a request on every launch.
	private async Task<ApplicationUpdateResult> RunAsync( bool manual, CancellationToken cancellationToken )
	{
		cancellationToken.ThrowIfCancellationRequested();

		string warning;

		lock ( gate )
		{
			state ??= store.Load();

			if ( !manual && !state.IsDue( utcNow() ) )
				return new( null, null, null, state.LastSuccessUtc, true );

			state.LastAttemptUtc = utcNow();
			warning = store.Save( state );
		}

		try
		{
			var release = await client.GetReleaseAsync( installedVersion, cancellationToken ).ConfigureAwait( false );

			cancellationToken.ThrowIfCancellationRequested();

			lock ( gate )
			{
				state.Release = release;
				state.LastSuccessUtc = utcNow();
				warning = store.Save( state ) ?? warning;

				return new( release, null, warning, state.LastSuccessUtc );
			}
		}
		catch ( ApplicationUpdateException error )
		{
			lock ( gate )
				return new( state.Release, error.Message, warning, state.LastSuccessUtc );
		}
	}

	internal bool ShouldNotify( ApplicationUpdateResult result )
	{
		lock ( gate )
		{
			return !result.Skipped && result.Error == null && ApplicationReleaseSelector.IsNewer( result.Release, installedVersion )
				&& ( !ApplicationReleaseSelector.TryVersion( state?.NotifiedVersion, out var notified )
					|| ApplicationReleaseSelector.TryVersion( result.Release.Version, out var latest ) && latest > notified );
		}
	}

	internal void MarkNotified( ApplicationRelease release )
	{
		lock ( gate )
		{
			if ( state == null )
				return;

			state.NotifiedVersion = release.Version;
			store.Save( state );
		}
	}
}
