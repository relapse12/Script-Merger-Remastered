using System;
using System.IO;
using System.Linq;

namespace WitcherScriptMerger.Tools;

// Both W3Bundle and a configured wcc_lite use this entry point for packing and error reporting.
internal static class WccLite
{
	public static string ExePath = ResolveExecutable( Program.Settings.Get( "WccLitePath" ), AppContext.BaseDirectory );

	internal static string BundledExePath => FindBundledExecutable( AppContext.BaseDirectory );

	private static string FindBundledExecutable( string applicationDirectory )
	{
		string preferred = Path.Combine( applicationDirectory, "Tools", "W3Bundle", "W3Bundle.exe" );
		string legacy = Path.Combine( applicationDirectory, "W3Bundle", "W3Bundle.exe" );

		return File.Exists( preferred ) || !File.Exists( legacy ) ? preferred : legacy;
	}

	// An old bundled-tool path may still be saved after an update. Resolve it to the current Tools folder.
	internal static string ResolveExecutable( string configuredPath, string applicationDirectory )
	{
		if ( string.IsNullOrWhiteSpace( configuredPath ) )
		{
			string bundled = FindBundledExecutable( applicationDirectory );

			return File.Exists( bundled ) ? bundled : string.Empty;
		}

		try
		{
			string selected = Path.GetFullPath( configuredPath, applicationDirectory );
			string preferred = Path.Combine( applicationDirectory, "Tools", "W3Bundle", "W3Bundle.exe" );
			string legacy = Path.Combine( applicationDirectory, "W3Bundle", "W3Bundle.exe" );

			// Migrate only this installation's bundled paths; custom tools remain selected.
			if ( selected.Equals( preferred, StringComparison.OrdinalIgnoreCase )
				|| selected.Equals( legacy, StringComparison.OrdinalIgnoreCase ) )
			{
				string bundled = FindBundledExecutable( applicationDirectory );

				if ( File.Exists( bundled ) )
					return bundled;
			}

			return selected;
		}
		catch ( Exception error ) when ( error is ArgumentException or NotSupportedException or PathTooLongException )
		{
			return configuredPath;
		}
	}

	// Save paths inside the application folder as relative paths so the installation can be moved.
	internal static string SettingPath( string selectedPath, string applicationDirectory )
	{
		if ( string.IsNullOrWhiteSpace( selectedPath ) )
			return string.Empty;

		try
		{
			string absolute = Path.GetFullPath( ResolveExecutable( selectedPath, applicationDirectory ), applicationDirectory );
			string relative = Path.GetRelativePath( applicationDirectory, absolute );

			return relative != ".." && !relative.StartsWith( ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal )
				&& !Path.IsPathRooted( relative ) ? relative : absolute;
		}
		catch ( Exception error ) when ( error is ArgumentException or NotSupportedException or PathTooLongException )
		{
			return selectedPath;
		}
	}

	internal static bool IsW3Bundle( string executable ) =>
		Path.GetFileName( executable ).Equals( "W3Bundle.exe", StringComparison.OrdinalIgnoreCase );

	internal static string[] ToolArguments( string executable, string[] arguments ) =>
		IsW3Bundle( executable ) ? arguments.ToArray() : arguments.Concat( new[] { "-agreetoterms" } ).ToArray();

	public static int PackBundle( string sourceDir, string outputDir )
	{
		if ( !Directory.Exists( sourceDir ) )
		{
			Program.MainFrm.ShowError(
				UiText.Format( "WccLite", "Can't find content directory to pack into bundle:\n\n{0}", sourceDir ),
				UiText.T( "WccLite", "Missing Directory" ) );

			return 1;
		}

		Directory.CreateDirectory( outputDir );

		string bundlePath = Path.Combine( outputDir, "blob0.bundle" );

		DeleteOutput( bundlePath );

		int result = Run(
			new[] { "pack", $"-dir={Path.GetFullPath( sourceDir )}", $"-outdir={Path.GetFullPath( outputDir )}" },
			UiText.T( "WccLite", "Error packing merged content into a new bundle.\nThe bundle packer's output is below." ) );

		if ( result == 0 && ( !File.Exists( bundlePath ) || new FileInfo( bundlePath ).Length == 0 ) )
		{
			Program.MainFrm.ShowError( UiText.T( "WccLite", "The bundle packer exited without creating a non-empty blob0.bundle." ) );

			return 1;
		}

		return result;
	}

	// Require a nonempty metadata.store before treating the tool run as successful.
	public static int GenerateMetadata( string bundleDir )
	{
		string metadataPath = Path.Combine( bundleDir, "metadata.store" );

		DeleteOutput( metadataPath );

		int result = Run(
			new[] { "metadatastore", $"-path={Path.GetFullPath( bundleDir )}" },
			UiText.T( "WccLite", "Error generating metadata.store for the new merged bundle.\nThe bundle packer's output is below." ) );

		if ( result == 0 && ( !File.Exists( metadataPath ) || new FileInfo( metadataPath ).Length == 0 ) )
		{
			Program.MainFrm.ShowError( UiText.T( "WccLite", "The bundle packer exited without creating a non-empty metadata.store." ) );

			return 1;
		}

		return result;
	}

	public static int Run( string[] arguments, string failureMsg )
	{
		ExePath = ResolveExecutable( ExePath, AppContext.BaseDirectory );

		if ( !TryValidateInstallation( ExePath, out string validationError ) )
		{
			Program.MainFrm.ShowError(
				UiText.Format(
					"WccLite",
					"Configure W3Bundle.exe or wcc_lite.exe in Menu > Dependency Locations, then retry.\n\n{0}",
					validationError ),
				UiText.T( "WccLite", "Bundle Packer Required" ) );

			return 1;
		}

		if ( !IsW3Bundle( ExePath ) && !WccTermsConsent.EnsureAccepted( ExePath ) )
			return 1;

		try
		{
			string[] acceptedArguments = ToolArguments( ExePath, arguments );
			ProcessRunResult run = new ProcessRunner().RunAsync( new ProcessRunRequest
			{
				FileName = Path.GetFullPath( ExePath ),
				Arguments = acceptedArguments,
				WorkingDirectory = Path.GetDirectoryName( Path.GetFullPath( ExePath ) ),
				Timeout = TimeSpan.FromMinutes( 15 )
			} ).GetAwaiter().GetResult();
			bool operationFailed = run.CombinedOutput.Contains( "Wcc operation failed", StringComparison.OrdinalIgnoreCase );

			if ( !run.Succeeded || operationFailed )
			{
				string diagnostics = string.IsNullOrWhiteSpace( run.CombinedOutput )
					? UiText.Format( "WccLite", "{0} exited with code {1}.", Path.GetFileName( ExePath ), run.ExitCode )
					: string.Join( Environment.NewLine, run.CombinedOutput
						.Split( new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries )
						.TakeLast( 80 ) );

				Program.MainFrm.ShowError( failureMsg + "\n\n" + diagnostics );

				return 1;
			}

			return 0;
		}
		catch ( Exception exception )
		{
			Program.MainFrm.ShowError( failureMsg + "\n\n" + exception.GetBaseException().Message );

			return 1;
		}
	}

	public static bool TryValidateInstallation( string path, out string reason )
	{
		reason = null;

		string resolved = string.IsNullOrWhiteSpace( path ) ? string.Empty : ResolveExecutable( path, AppContext.BaseDirectory );

		if ( string.IsNullOrWhiteSpace( resolved ) || !File.Exists( resolved ) )
		{
			reason = UiText.T( "WccLite", "Select W3Bundle.exe or a Remastered-compatible wcc_lite.exe." );

			return false;
		}

		string fullPath = Path.GetFullPath( resolved );

		if ( IsW3Bundle( fullPath ) )
			return true;

		if ( !Path.GetFileName( fullPath ).Equals( "wcc_lite.exe", StringComparison.OrdinalIgnoreCase ) )
		{
			reason = UiText.T( "WccLite", "The selected file must be named W3Bundle.exe or wcc_lite.exe." );

			return false;
		}

		string termsPath = Path.Combine( Path.GetDirectoryName( fullPath ), "wcc_eula.rtf" );

		if ( !File.Exists( termsPath ) )
		{
			reason = UiText.T( "WccLite", "The selected WCC installation is incomplete because wcc_eula.rtf is not beside wcc_lite.exe." );

			return false;
		}

		return true;
	}

	private static void DeleteOutput( string path )
	{
		if ( File.Exists( path ) )
		{
			File.Delete( path );
		}
	}
}
