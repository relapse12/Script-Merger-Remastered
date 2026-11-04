using System.Runtime.InteropServices;
using ABI.System;

namespace WinRT.WitcherScriptMergerVtableClasses;

internal sealed class WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails : IWinRTExposedTypeDetails
{
	public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces()
	{
		return new ComWrappers.ComInterfaceEntry[1]
		{
			new ComWrappers.ComInterfaceEntry
			{
				IID = IDisposableMethods.IID,
				Vtable = IDisposableMethods.AbiToProjectionVftablePtr
			}
		};
	}
}
