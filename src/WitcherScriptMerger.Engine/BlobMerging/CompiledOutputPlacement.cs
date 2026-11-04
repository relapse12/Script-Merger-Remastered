#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// The merged blob must load between the mods it depends on and the mods that depend on it.
// Choose a folder name and, when needed, a priority that keep that order.
internal static class CompiledOutputPlacement
{
	internal static int Priority( MergePlan plan, IReadOnlyList<ModLoadSetting> settings ) => Choose( plan, settings ).Priority
		?? throw new InvalidOperationException( "The selected placement is alphabetical." );

	internal static (string Name, int? Priority) ForMerge( LoadedBlobMerge loaded, MergePlan plan )
	{
		HashSet<string?> occupied;
		string? previousMod;

		if ( !plan.NeedsCompilation )
			return (MergeBuilder.CompiledName, null);

		occupied = loaded.ConventionalFolders
			.Select( Path.GetFileName )
			.Append( loaded.LooseModName )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		foreach ( ModLoadSetting setting in loaded.Settings )
			occupied.Add( setting.ModName );

		previousMod = PreviousCompiledMod( loaded );

		// An update may keep its own folder, even though that folder and its setting now exist.
		if ( previousMod is not null )
			occupied.Remove( previousMod );

		return Choose(
			plan,
			loaded.Settings.Where( setting => !setting.ModName.Equals( previousMod, StringComparison.OrdinalIgnoreCase ) ).ToArray(),
			occupied!,
			previousMod,
			loaded.Settings.FirstOrDefault( setting => setting.ModName.Equals( previousMod, StringComparison.OrdinalIgnoreCase ) ) );
	}

	private static string? PreviousCompiledMod( LoadedBlobMerge loaded )
	{
		bool hasCompiledOutput = loaded.Previous?.Manifest.Outputs
			.Any( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ) == true;

		return hasCompiledOutput ? loaded.Previous!.Manifest.CompiledMod : null;
	}

	// Try the preferred folder name first, then other names that sort into place, then numeric priorities.
	internal static (string Name, int? Priority) Choose(
		MergePlan plan,
		IReadOnlyList<ModLoadSetting> settings,
		ISet<string>? occupied = null,
		string? preferredName = null,
		ModLoadSetting? preferredSetting = null )
	{
		string[] names;

		bool IsAvailable( string name )
		{
			return occupied?.Contains( name ) != true
				&& !settings.Any( setting => setting.ModName.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		}

		bool KeepsDependencyOrder( string name, ModLoadSetting? proposed )
		{
			return plan.ExternalEdges.All( edge =>
			{
				bool outputIsConsumer = IsOutputConsumer( edge );
				string external = outputIsConsumer ? edge.Provider : edge.Consumer;
				ModLoadSetting? setting = settings.FirstOrDefault(
					setting => setting.ModName.Equals( external, StringComparison.OrdinalIgnoreCase ) );
				int comparison = LoadOrderComparer.CompareRuntimeOrder( name, proposed, external, setting );

				return outputIsConsumer ? comparison < 0 : comparison > 0;
			} );
		}

		if ( preferredName is not null && IsAvailable( preferredName ) && KeepsDependencyOrder( preferredName, preferredSetting ) )
			return (preferredName, preferredSetting?.Priority);

		names = CandidateNames( plan )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.Where( name => Regex.IsMatch( name, @"\Amod[A-Za-z0-9_-]{1,123}\z" ) && IsAvailable( name ) )
			.ToArray();

		foreach ( string name in names )
		{
			if ( KeepsDependencyOrder( name, null ) )
				return (name, null);
		}

		foreach ( int priority in CandidatePriorities( plan, settings ) )
		{
			foreach ( string name in names )
			{
				var proposed = new ModLoadSetting( name )
				{
					Priority = priority,
					IsEnabled = true
				};

				if ( KeepsDependencyOrder( name, proposed ) )
					return (name, priority);
			}
		}

		throw new InvalidOperationException( UiText.T(
			"BlobMerge",
			"No compiled-output priority satisfies the current dependencies. Change those priorities, or include the affected conventional blobs in this merge." ) );
	}

	// An edge's consumer is a symbol of the merge's own output when it holds a ":", as in "function:Tick".
	// Otherwise it is another mod.
	private static bool IsOutputConsumer( MergeEdge edge )
	{
		return edge.Consumer.Contains( ':' );
	}

	// A name based on a dependency sorts beside it, which may avoid needing a numeric priority.
	private static IEnumerable<string> CandidateNames( MergePlan plan )
	{
		yield return MergeBuilder.CompiledName;

		yield return "modSMR_MergedCompiled";

		yield return "mod0000_SMR_MergedCompiled";

		foreach ( MergeEdge edge in plan.ExternalEdges.Where( edge => !IsOutputConsumer( edge ) ) )
			yield return edge.Consumer + "_SMR_MergedCompiled";

		foreach ( string provider in plan.ExternalEdges.Where( IsOutputConsumer ).Select( edge => edge.Provider ) )
		{
			for ( int length = 3; length < Math.Min( provider.Length, 105 ); length++ )
			{
				foreach ( char value in "-0123456789_abcdefghijklmnopqrstuvwxyz" )
					yield return provider[..length] + value + "_SMR_MergedCompiled";
			}
		}
	}

	// Numeric placement needs known consumer priorities and cannot cross a Mod.io provider.
	private static IEnumerable<int> CandidatePriorities( MergePlan plan, IReadOnlyList<ModLoadSetting> settings )
	{
		var constraints = plan.ExternalEdges.Select( edge =>
		{
			bool outputIsConsumer = IsOutputConsumer( edge );
			string external = outputIsConsumer ? edge.Provider : edge.Consumer;

			return (
				OutputIsConsumer: outputIsConsumer,
				Setting: settings.FirstOrDefault( setting => setting.ModName.Equals( external, StringComparison.OrdinalIgnoreCase ) ));
		} ).ToArray();

		bool isPossible = constraints.All( edge => edge.OutputIsConsumer ? edge.Setting?.IsExternal != true : edge.Setting is not null );
		int minimum = constraints.Where( edge => !edge.OutputIsConsumer )
			.Select( edge => edge.Setting?.Priority ?? 0 ).Append( 0 ).Max();
		int maximum = constraints.Where( edge => edge.OutputIsConsumer )
			.Select( edge => edge.Setting?.Priority ?? 9999 ).Append( 9999 ).Min();

		if ( !isPossible || minimum > maximum )
			return Enumerable.Empty<int>();

		return Enumerable.Range( minimum, maximum - minimum + 1 ).Reverse();
	}

	// An output sharing the loose merged folder needs no separate setting when that folder has no assigned priority.
	internal static string SettingsFor( BlobMergeManifest manifest, string original )
	{
		string[] ownEntries = manifest.SharedLooseFolder
			? new[] { manifest.CompiledMod }
			: new[] { manifest.LooseMod, manifest.CompiledMod };
		bool hasOwnLooseFolder = !manifest.SharedLooseFolder
			&& manifest.Outputs.Any( output => output.Path.StartsWith( manifest.LooseMod, StringComparison.Ordinal ) );
		StringBuilder result;

		if ( manifest.SharedLooseFolder && manifest.CompiledPriority is null )
			return original;

		foreach ( string mod in ownEntries )
		{
			if ( Regex.IsMatch( original, @"(?im)^\s*\[" + Regex.Escape( mod ) + @"\]\s*$" ) )
				throw new InvalidOperationException( UiText.T( "BlobMerge", "Load-order entries already exist for the merged output. Restore the previous merge first." ) );
		}

		result = new StringBuilder( original.TrimEnd() );

		if ( hasOwnLooseFolder )
			AppendEntry( result.AppendLine().AppendLine(), manifest.LooseMod, 0 );

		if ( manifest.CompiledPriority is { } priority )
			AppendEntry( result.AppendLine(), manifest.CompiledMod, priority );

		return result.ToString();
	}

	private static void AppendEntry( StringBuilder settings, string mod, int priority )
	{
		settings.Append( '[' ).Append( mod ).AppendLine( "]" );
		settings.AppendLine( "Enabled=1" );
		settings.Append( "Priority=" ).AppendLine( priority.ToString( System.Globalization.CultureInfo.InvariantCulture ) );
	}
}
