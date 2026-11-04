using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ABI.System;

namespace WinRT.WitcherScriptMergerGenericHelpers;

internal static class GlobalVtableLookup
{
	[ModuleInitializer]
	internal static void InitializeGlobalVtableLookup()
	{
		ComWrappersSupport.RegisterTypeComInterfaceEntriesLookup(LookupVtableEntries);
		ComWrappersSupport.RegisterTypeRuntimeClassNameLookup(LookupRuntimeClassName);
	}

	private static ComWrappers.ComInterfaceEntry[] LookupVtableEntries(System.Type type)
	{
		switch (type.ToString())
		{
		case "System.Windows.Forms.Panel":
		case "System.Drawing.SolidBrush":
		case "System.Windows.Forms.Control":
		case "System.ComponentModel.Container":
			return new ComWrappers.ComInterfaceEntry[1]
			{
				new ComWrappers.ComInterfaceEntry
				{
					IID = IDisposableMethods.IID,
					Vtable = IDisposableMethods.AbiToProjectionVftablePtr
				}
			};
		default:
			return null;
		}
	}

	private static string LookupRuntimeClassName(System.Type type)
	{
		switch (type.ToString())
		{
		case "System.Windows.Forms.Panel":
		case "System.Drawing.SolidBrush":
		case "System.Windows.Forms.Control":
		case "System.ComponentModel.Container":
			return "Windows.Foundation.IClosable";
		default:
			return null;
		}
	}
}
