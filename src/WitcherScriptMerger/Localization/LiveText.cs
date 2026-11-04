#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace WitcherScriptMerger;

// Binds controls to UiText translations and reapplies them when the language changes.
internal static class LiveText
{
	// Last records the text written by this binding. A later assignment means the control is displaying
	// something else, such as a file path, and should stop receiving translations.
	private sealed class TextBinding( Func<string> read, Action<string> write, Func<string> render )
	{
		internal readonly Func<string> Read = read;
		internal readonly Action<string> Write = write;
		internal readonly Func<string> Render = render;
		internal string Last = "";
	}

	private static readonly ConditionalWeakTable<object, Dictionary<string, TextBinding>> LiveBindings = new();
	private static readonly List<WeakReference<object>> LiveTargets = new();
	private static readonly object BindingLock = new();

	internal static void ApplyLanguage( LanguagePack pack )
	{
		UiText.SetLanguage( pack );
		Forms.MessageBoxManager.RefreshLanguage();
		RefreshLiveText();
		Program.MainFrm?.RefreshLocalizedWorkspace();
	}

	internal static T Live<T>( T target, Action<T> bind ) where T : class
	{
		bind( target );

		return target;
	}

	internal static void BindText(
		Control target,
		Func<string> text ) => Bind( target, "Text", () => target.Text, value => target.Text = value, text );

	internal static void BindText(
		ToolStripItem target,
		Func<string> text ) => Bind( target, "Text", () => target.Text ?? "", value => target.Text = value, text );

	internal static void BindText(
		TreeNode target,
		Func<string> text ) => Bind( target, "Text", () => target.Text, value => target.Text = value, text );

	internal static void BindText(
		ColumnHeader target,
		Func<string> text ) => Bind( target, "Text", () => target.Text, value => target.Text = value, text );

	internal static void BindPlaceholderText(
		TextBox target,
		Func<string> text ) => Bind(
		target,
		"PlaceholderText",
		() => target.PlaceholderText,
		value => target.PlaceholderText = value,
		text );

	internal static void BindAccessibleName(
		Control target,
		Func<string> text ) => Bind(
		target,
		"AccessibleName",
		() => target.AccessibleName ?? "",
		value => target.AccessibleName = value,
		text );

	internal static void BindAccessibleDescription(
		Control target,
		Func<string> text ) => Bind(
		target,
		"AccessibleDescription",
		() => target.AccessibleDescription ?? "",
		value => target.AccessibleDescription = value,
		text );

	internal static void BindToolTipText(
		ToolStripItem target,
		Func<string> text ) => Bind(
		target,
		"ToolTipText",
		() => target.ToolTipText ?? "",
		value => target.ToolTipText = value,
		text );

	internal static void BindToolTipText(
		TreeNode target,
		Func<string> text ) => Bind( target, "ToolTipText", () => target.ToolTipText, value => target.ToolTipText = value, text );

	internal static void BindToolTip(
		ToolTip tooltip,
		Control target,
		Func<string> text ) => Bind(
		target,
		"ToolTip:" + tooltip.GetHashCode(),
		() => tooltip.GetToolTip( target ) ?? "",
		value => tooltip.SetToolTip( target, value ),
		text );

	// Keep the selected index when translated combo-box labels are rebuilt.
	internal static void BindItems( ComboBox target, Func<object[]> items )
	{
		string Render() => string.Join( "\0", items().Select( value => value.ToString() ) );

		Bind( target, "Items", () => string.Join( "\0", target.Items.Cast<object>().Select( value => value.ToString() ) ), _ =>
		{
			int selected = target.SelectedIndex;

			target.BeginUpdate();

			try
			{
				target.Items.Clear();
				target.Items.AddRange( items() );
				target.SelectedIndex = Math.Min( selected, target.Items.Count - 1 );
			}
			finally { target.EndUpdate(); }
		}, Render );
	}

	// A binding must not keep a closed window alive. Rebinding the same property replaces its previous translation.
	private static void Bind( object target, string property, Func<string> read, Action<string> write, Func<string> render )
	{
		var binding = new TextBinding( read, write, render );

		binding.Last = render();
		write( binding.Last );

		lock ( BindingLock )
		{
			if ( !LiveBindings.TryGetValue( target, out var properties ) )
			{
				properties = new();
				LiveBindings.Add( target, properties );
				LiveTargets.Add( new( target ) );
			}

			properties[property] = binding;
		}
	}

	// Copy the bindings under the lock, then update controls after releasing it.
	private static void RefreshLiveText()
	{
		KeyValuePair<object, TextBinding[]>[] targets;

		lock ( BindingLock )
		{
			LiveTargets.RemoveAll( reference => !reference.TryGetTarget( out _ ) );
			targets = LiveTargets.Select( reference => reference.TryGetTarget( out var target ) && LiveBindings.TryGetValue( target, out var properties )
				? new KeyValuePair<object, TextBinding[]>( target, properties.Values.ToArray() ) : default )
				.Where( item => item.Key != null ).ToArray();
		}

		foreach ( var target in targets )
		{
			if ( target.Key is Control { IsDisposed: true } or ToolStripItem { IsDisposed: true } )
				continue;

			foreach ( TextBinding binding in target.Value )
			{
				// A later assignment replaces the translated label, for example with a file path.
				if ( binding.Read() != binding.Last )
					continue;

				binding.Last = binding.Render();
				binding.Write( binding.Last );
			}
		}

		foreach ( Form form in Application.OpenForms.Cast<Form>().ToArray() )
		{
			form.PerformLayout();
			form.Invalidate( true );
		}
	}
}
