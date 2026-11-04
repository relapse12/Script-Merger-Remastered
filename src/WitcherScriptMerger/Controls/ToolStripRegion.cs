using System.Linq;
using System.Windows.Forms;

namespace WitcherScriptMerger.Controls;

// Hide an empty menu group together with its separators.
internal class ToolStripRegion
{
	public ToolStripItemCollection Items;

	public bool Available => Items.Cast<ToolStripItem>().Any( ( ToolStripItem item ) => item.Available );

	public ToolStripRegion( ToolStrip owner, ToolStripItem[] value )
	{
		Items = new ToolStripItemCollection( owner, value );
	}
}
