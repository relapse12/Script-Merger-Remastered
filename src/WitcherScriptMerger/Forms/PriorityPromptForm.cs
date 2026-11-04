using System;
using System.Drawing;
using System.Windows.Forms;

namespace WitcherScriptMerger.Forms;

internal class PriorityPromptForm : BaseForm
{
	private TableLayoutPanel tableLayoutPanel1;
	private TableLayoutPanel tlpTitle;
	private Label lblTitle;
	private NumericUpDown upDownPrio;
	private Button btnOK;
	private Label lblModName;
	private Label lblLoadOrderInfo;

	public PriorityPromptForm()
	{
		InitializeComponent();
		Program.ThemeMngr.ApplyThemeOnForm( this );
	}

	public int? ShowDialog( string modName, int value = 0 )
	{
		return ShowPriorityDialog(
			modName,
			value,
			minimum: 1,
			maximum: 9999,
			windowTitle: UiText.T( "PriorityPromptForm", "Set Load Order" ),
			heading: UiText.T( "PriorityPromptForm", "Set Load Order for" ),
			instructions: UiText.T( "PriorityPromptForm", "Type a priority or use the arrows. Press Enter to confirm." ) );
	}

	public int? ShowModIoDialog( string modName, int value, int maximum )
	{
		return ShowPriorityDialog(
			modName,
			value,
			minimum: 0,
			maximum: Math.Max( 0, maximum ),
			windowTitle: UiText.T( "PriorityPromptForm", "Set Mod.io Priority" ),
			heading: UiText.T( "PriorityPromptForm", "Set Mod.io Priority for" ),
			instructions: UiText.T( "PriorityPromptForm", "Choose this mod's position. Priority 0 loads first." ) );
	}

	// Null means cancelled. A current value outside the allowed range is not shown.
	private int? ShowPriorityDialog( string modName, int value, int minimum, int maximum,
		string windowTitle, string heading, string instructions )
	{
		base.Icon = Program.MainFrm.Icon;
		Text = windowTitle;
		lblTitle.Text = heading;
		lblLoadOrderInfo.Text = instructions;
		lblModName.Text = modName;
		upDownPrio.Minimum = minimum;
		upDownPrio.Maximum = maximum;
		btnOK.DialogResult = DialogResult.OK;

		if ( (decimal)value >= upDownPrio.Minimum && (decimal)value <= upDownPrio.Maximum )
		{
			upDownPrio.Value = value;
		}

		upDownPrio.Select( 0, upDownPrio.Text.Length );

		if ( ShowDialog() != DialogResult.OK )
		{
			return null;
		}

		return Convert.ToInt32( upDownPrio.Value );
	}

	private void PriorityPromptForm_KeyDown( object sender, KeyEventArgs e )
	{
		if ( e.KeyCode == Keys.Escape )
		{
			Close();
		}
	}

	private void InitializeComponent()
	{
		tableLayoutPanel1 = new TableLayoutPanel();
		tlpTitle = new TableLayoutPanel();
		lblTitle = new Label();
		lblModName = new Label();
		lblLoadOrderInfo = new Label();
		upDownPrio = new NumericUpDown();
		btnOK = new Button();
		tableLayoutPanel1.SuspendLayout();
		tlpTitle.SuspendLayout();
		((System.ComponentModel.ISupportInitialize)upDownPrio).BeginInit();
		SuspendLayout();

		tableLayoutPanel1.ColumnCount = 2;
		tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		tableLayoutPanel1.Controls.Add(tlpTitle, 0, 0);
		tableLayoutPanel1.Controls.Add(lblLoadOrderInfo, 0, 1);
		tableLayoutPanel1.Controls.Add(upDownPrio, 0, 2);
		tableLayoutPanel1.Controls.Add(btnOK, 1, 2);
		tableLayoutPanel1.Dock = DockStyle.Fill;
		tableLayoutPanel1.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
		tableLayoutPanel1.Margin = Padding.Empty;
		tableLayoutPanel1.Name = "tableLayoutPanel1";
		tableLayoutPanel1.RowCount = 3;
		tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 74f));
		tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
		tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tableLayoutPanel1.TabIndex = 0;
		tableLayoutPanel1.SetColumnSpan(tlpTitle, 2);

		tlpTitle.BackColor = Color.Transparent;
		tlpTitle.ColumnCount = 2;
		tlpTitle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		tlpTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		tlpTitle.Controls.Add(lblTitle, 0, 0);
		tlpTitle.Controls.Add(lblModName, 1, 0);
		tlpTitle.Dock = DockStyle.Fill;
		tlpTitle.Margin = Padding.Empty;
		tlpTitle.Name = "tlpTitle";
		tlpTitle.Padding = new Padding(18, 8, 18, 4);
		tlpTitle.RowCount = 1;
		tlpTitle.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		tlpTitle.TabIndex = 0;

		lblTitle.Anchor = AnchorStyles.Left;
		lblTitle.AutoSize = true;
		lblTitle.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
		lblTitle.Margin = new Padding(0, 0, 5, 0);
		lblTitle.Name = "lblTitle";
		lblTitle.TabIndex = 0;
		LiveText.BindText(lblTitle, () => UiText.T("PriorityPromptForm", "Set Load Order for"));

		lblModName.AutoSize = false;
		lblModName.Dock = DockStyle.Fill;
		lblModName.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
		lblModName.Margin = Padding.Empty;
		lblModName.Name = "lblModName";
		lblModName.TabIndex = 1;
		lblModName.Tag = "AccentLabel";
		LiveText.BindText(lblModName, () => UiText.T("PriorityPromptForm", "ModName"));
		lblModName.TextAlign = ContentAlignment.MiddleLeft;

		lblLoadOrderInfo.AutoSize = false;
		lblLoadOrderInfo.BackColor = Color.Transparent;
		tableLayoutPanel1.SetColumnSpan(lblLoadOrderInfo, 2);
		lblLoadOrderInfo.Dock = DockStyle.Fill;
		lblLoadOrderInfo.Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
		lblLoadOrderInfo.Margin = new Padding(18, 0, 18, 0);
		lblLoadOrderInfo.Name = "lblLoadOrderInfo";
		lblLoadOrderInfo.TabIndex = 2;
		LiveText.BindText(lblLoadOrderInfo, () => UiText.T("PriorityPromptForm", "Type a priority or use the arrows. Press Enter to confirm."));
		lblLoadOrderInfo.TextAlign = ContentAlignment.MiddleLeft;

		upDownPrio.Anchor = AnchorStyles.Right;
		upDownPrio.Font = new Font("Segoe UI", 18f, FontStyle.Regular, GraphicsUnit.Point);
		upDownPrio.Margin = new Padding(0, 8, 10, 12);
		upDownPrio.Name = "upDownPrio";
		upDownPrio.Size = new Size(110, 47);
		upDownPrio.TabIndex = 0;

		btnOK.Anchor = AnchorStyles.Left;
		btnOK.Font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
		btnOK.Margin = new Padding(10, 8, 0, 12);
		btnOK.Name = "btnOK";
		btnOK.Size = new Size(110, 47);
		btnOK.TabIndex = 1;
		LiveText.BindText(btnOK, () => UiText.T("PriorityPromptForm", "OK"));
		btnOK.UseVisualStyleBackColor = true;

		AcceptButton = btnOK;
		AutoScaleDimensions = new SizeF(96f, 96f);
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(520, 220);
		Controls.Add(tableLayoutPanel1);
		FormBorderStyle = FormBorderStyle.FixedSingle;
		KeyPreview = true;
		MaximizeBox = false;
		MinimizeBox = false;
		Name = "PriorityPromptForm";
		StartPosition = FormStartPosition.CenterParent;
		LiveText.BindText(this, () => UiText.T("PriorityPromptForm", "Set Load Order"));
		KeyDown += PriorityPromptForm_KeyDown;

		tableLayoutPanel1.ResumeLayout(false);
		tlpTitle.ResumeLayout(false);
		tlpTitle.PerformLayout();
		((System.ComponentModel.ISupportInitialize)upDownPrio).EndInit();
		ResumeLayout(false);
	}
}
