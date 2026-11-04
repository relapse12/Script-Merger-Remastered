using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Tools;

internal static class KDiff3
{
	public static string ExePath = Program.Settings.Get( "KDiff3Path" );

	// Zero means KDiff3 saved a result. Report a missing executable as a failed comparison.
	public static int Run(
		FileMerger.MergeSource source1,
		FileMerger.MergeSource source2,
		FileInfo vanillaFile,
		string outputPath,
		bool followFileLinks )
	{
		if ( !File.Exists( ExePath ) )
		{
			Program.MainFrm.ShowError(
				UiText.Format( "KDiff3", "Can't find KDiff3 at this location:\n\n{0}", ExePath ),
				UiText.T( "KDiff3", "Missing KDiff3" ) );

			return 1;
		}

		string outputDir = Path.GetDirectoryName( outputPath );

		if ( !Directory.Exists( outputDir ) )
		{
			Directory.CreateDirectory( outputDir );
		}

		bool hasVanillaVersion = vanillaFile?.Exists ?? false;
		string source1FullName = ( followFileLinks ? source1.TextFile.ResolveTargetFileFullName() : source1.TextFile.FullName );
		string source2FullName = ( followFileLinks ? source2.TextFile.ResolveTargetFileFullName() : source2.TextFile.FullName );

		if ( !hasVanillaVersion && source1.Bundle == null && source2.Bundle == null
			&& !Program.Settings.Get<bool>( "ReviewEachMerge" )
			&& Path.GetExtension( outputPath ).Equals( ".ws", StringComparison.OrdinalIgnoreCase )
			&& WitcherScriptSymbolReader.HaveEquivalentCode( source1FullName, source2FullName ) )
		{
			if ( !source1FullName.EqualsIgnoreCase( Path.GetFullPath( outputPath ) ) )
				File.Copy( source1FullName, outputPath, overwrite: true );

			return 0;
		}

		bool autoMerge = false;

		if ( !Program.Settings.Get<bool>( "ReviewEachMerge" ) && hasVanillaVersion )
		{
			if ( source1FullName.EqualsIgnoreCase( outputPath ) && source2.Hash != null && source2.Hash.IsOutdated )
			{
				Program.MainFrm.ShowMessage(
					UiText.T(
						"KDiff3",
						"You are merging an updated mod file into a merge created with a previous version of the file.\n\nYou should carefully inspect this merge, because KDiff3's auto-solving behavior KEEPS changes from the previous version of the mod file that have been REMOVED in the new version." ),
					UiText.T( "KDiff3", "Warning" ),
					MessageBoxButtons.OK,
					MessageBoxIcon.Exclamation );
			}
			else
			{
				autoMerge = true;
			}
		}

		using var csvInputs = CsvMergeInputs.Prepare( source1FullName, source2FullName,
			hasVanillaVersion ? vanillaFile.FullName : null, outputPath,
			Path.Combine( Environment.CurrentDirectory, Paths.TempBundleContent ) );
		string args = BuildArguments(
			csvInputs.FirstPath,
			source1.Name,
			csvInputs.SecondPath,
			source2.Name,
			csvInputs.VanillaPath,
			outputPath,
			Program.Settings.Get<bool>( "AutoSkipKDiff3InfoDialogs" ),
			autoMerge && !csvInputs.HasConflicts,
			Path.GetExtension( outputPath ).Equals( ".csv", StringComparison.OrdinalIgnoreCase ) );
		Process process = Process.Start(
			Path.IsPathRooted( ExePath ) ? ExePath : Path.Combine( Environment.CurrentDirectory, ExePath ),
			args );

		process.WaitForExit();

		if ( process.ExitCode == 0 )
			csvInputs.RestoreOutputPadding( outputPath );

		return process.ExitCode;
	}

	internal static string BuildArguments(
		string source1FullName,
		string source1Name,
		string source2FullName,
		string source2Name,
		string vanillaFullName,
		string outputPath,
		bool skipInfoDialogs,
		bool autoMerge,
		bool csvInput = false )
	{
		bool hasVanillaVersion = !string.IsNullOrWhiteSpace( vanillaFullName );
		string args = hasVanillaVersion ? $"\"{vanillaFullName}\"" : string.Empty;

		args += $" \"{source1FullName}\" \"{source2FullName}\"";
		args += $" -o \"{outputPath}\"";
		args += csvInput ? " --cs \"WhiteSpace3FileMergeDefault=0\"" : " --cs \"WhiteSpace3FileMergeDefault=2\"";
		args += " --cs \"CreateBakFiles=0\"";
		args += " --cs \"LineEndStyle=1\"";
		args += " --cs \"FollowFileLinks=1\"";
		args += " --cs \"FollowDirLinks=1\"";

		if ( skipInfoDialogs )
		{
			args += " --cs \"ShowInfoDialogs=0\"";
		}

		args += " --cs \"ShowWhiteSpace=0\"";
		args += " --cs \"ShowWhiteSpaceCharacters=0\"";
		args += " --cs \"EscapeKeyQuits=1\"";
		args += hasVanillaVersion
			? $" --L1 Vanilla --L2 \"{source1Name}\" --L3 \"{source2Name}\""
			: $" --L1 \"{source1Name}\" --L2 \"{source2Name}\"";

		if ( autoMerge && hasVanillaVersion )
		{
			args += " --auto";
		}

		return args;
	}
}
