using System;
using System.Windows.Forms;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;

namespace WitcherScriptMerger.Forms;

[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
internal class MergeReportForm : BaseForm
{
	private Label lblMergedFiles;

	private Button btnOpenMergedDir;

	private Button btnOpenMergedFile;

	private TextBox txtMergedPath;

	private Button btnOpenDir2;

	private Button btnOpenFile2;

	private TextBox txtFilePath2;

	private Button btnMergeReportOK;

	private CheckBox chkShowAfterMerge;

	private Label lblTempContentFiles;

	private TextBox txtFilePath1;

	private Button btnOpenFile1;

	private Button btnOpenDir1;

	private Label lblArrowDown;

	private Label lblMod1;

	private Label lblMod2;

	private Label lblMergedFile;

	private Label lblNewPlus;

	public MergeReportForm( int mergeNum, int mergeTotal, string file1, string file2, string outputFile, string modName1, string modName2 )
	{
		InitializeComponent();

		if ( file1 == null )
		{
			return;
		}

		if ( mergeTotal > 1 )
		{
			Text += UiText.F( "MergeReportForm", $" ({mergeNum} of {mergeTotal})" );

			if ( mergeNum < mergeTotal )
			{
				LiveText.BindText( btnMergeReportOK, () => UiText.T( "MergeReportForm", "Continue" ) );
			}
		}

		lblTempContentFiles.Visible = outputFile.StartsWithIgnoreCase( Paths.MergedBundleContent );
		lblMod1.Text = modName1;
		lblMod2.Text = modName2;
		txtFilePath1.Text = file1;
		txtFilePath2.Text = file2;
		txtMergedPath.Text = outputFile;
		chkShowAfterMerge.Checked = Program.Settings.Get<bool>( "ReportAfterMerge" );
		btnMergeReportOK.Select();
	}

	private void MergeReportForm_FormClosing( object sender, FormClosingEventArgs e )
	{
		Program.Settings.Set( "ReportAfterMerge", chkShowAfterMerge.Checked );
	}

	private void BtnOpenFile1_Click( object sender, EventArgs e )
	{
		Program.TryOpenFile( txtFilePath1.Text );
	}

	private void BtnOpenFile2_Click( object sender, EventArgs e )
	{
		Program.TryOpenFile( txtFilePath2.Text );
	}

	private void BtnOpenOutputFile_Click( object sender, EventArgs e )
	{
		Program.TryOpenFile( txtMergedPath.Text );
	}

	private void BtnOpenDir1_Click( object sender, EventArgs e )
	{
		Program.TryOpenFileLocation( txtFilePath1.Text );
	}

	private void BtnOpenDir2_Click( object sender, EventArgs e )
	{
		Program.TryOpenFileLocation( txtFilePath2.Text );
	}

	private void BtnOpenOutputDir_Click( object sender, EventArgs e )
	{
		Program.TryOpenFileLocation( txtMergedPath.Text );
	}

	private void BtnOK_Click( object sender, EventArgs e )
	{
		base.DialogResult = DialogResult.OK;
	}

	private void Txt_KeyDown( object sender, KeyEventArgs e )
	{
		if ( e.Control && e.KeyCode == Keys.A )
		{
			( sender as TextBox ).SelectAll();
		}
	}

	private void InitializeComponent()
	{
		System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(WitcherScriptMerger.Forms.MergeReportForm));
		this.lblMergedFiles = new System.Windows.Forms.Label();
		this.btnOpenMergedDir = new System.Windows.Forms.Button();
		this.btnOpenMergedFile = new System.Windows.Forms.Button();
		this.txtMergedPath = new System.Windows.Forms.TextBox();
		this.btnOpenDir2 = new System.Windows.Forms.Button();
		this.btnOpenFile2 = new System.Windows.Forms.Button();
		this.txtFilePath2 = new System.Windows.Forms.TextBox();
		this.btnMergeReportOK = new System.Windows.Forms.Button();
		this.chkShowAfterMerge = new System.Windows.Forms.CheckBox();
		this.lblTempContentFiles = new System.Windows.Forms.Label();
		this.txtFilePath1 = new System.Windows.Forms.TextBox();
		this.btnOpenFile1 = new System.Windows.Forms.Button();
		this.btnOpenDir1 = new System.Windows.Forms.Button();
		this.lblArrowDown = new System.Windows.Forms.Label();
		this.lblMod1 = new System.Windows.Forms.Label();
		this.lblMod2 = new System.Windows.Forms.Label();
		this.lblMergedFile = new System.Windows.Forms.Label();
		this.lblNewPlus = new System.Windows.Forms.Label();
		base.SuspendLayout();
		this.lblMergedFiles.AutoSize = true;
		this.lblMergedFiles.Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.lblMergedFiles.Location = new System.Drawing.Point(16, 23);
		this.lblMergedFiles.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
		this.lblMergedFiles.Name = "lblMergedFiles";
		this.lblMergedFiles.Size = new System.Drawing.Size(230, 28);
		this.lblMergedFiles.TabIndex = 5;
		LiveText.BindText(this.lblMergedFiles, () => UiText.T("MergeReportForm", "Created new merged file!"));
		this.btnOpenMergedDir.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.btnOpenMergedDir.BackColor = System.Drawing.Color.LightSteelBlue;
		this.btnOpenMergedDir.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDark;
		this.btnOpenMergedDir.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnOpenMergedDir.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnOpenMergedDir.ForeColor = System.Drawing.SystemColors.ControlText;
		this.btnOpenMergedDir.Location = new System.Drawing.Point(545, 460);
		this.btnOpenMergedDir.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.btnOpenMergedDir.Name = "btnOpenMergedDir";
		this.btnOpenMergedDir.Size = new System.Drawing.Size(388, 35);
		this.btnOpenMergedDir.TabIndex = 2;
		LiveText.BindText(this.btnOpenMergedDir, () => UiText.T("MergeReportForm", "Open Directory"));
		this.btnOpenMergedDir.UseVisualStyleBackColor = false;
		this.btnOpenMergedDir.Click += new System.EventHandler(BtnOpenOutputDir_Click);
		this.btnOpenMergedFile.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.btnOpenMergedFile.BackColor = System.Drawing.Color.LightSteelBlue;
		this.btnOpenMergedFile.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDark;
		this.btnOpenMergedFile.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnOpenMergedFile.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnOpenMergedFile.ForeColor = System.Drawing.SystemColors.ControlText;
		this.btnOpenMergedFile.Location = new System.Drawing.Point(150, 460);
		this.btnOpenMergedFile.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.btnOpenMergedFile.Name = "btnOpenMergedFile";
		this.btnOpenMergedFile.Size = new System.Drawing.Size(387, 35);
		this.btnOpenMergedFile.TabIndex = 1;
		LiveText.BindText(this.btnOpenMergedFile, () => UiText.T("MergeReportForm", "Open File"));
		this.btnOpenMergedFile.UseVisualStyleBackColor = false;
		this.btnOpenMergedFile.Click += new System.EventHandler(BtnOpenOutputFile_Click);
		this.txtMergedPath.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.txtMergedPath.BorderStyle = System.Windows.Forms.BorderStyle.None;
		this.txtMergedPath.Font = new System.Drawing.Font("Segoe UI", 10.2f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.txtMergedPath.ForeColor = System.Drawing.SystemColors.ControlText;
		this.txtMergedPath.Location = new System.Drawing.Point(24, 420);
		this.txtMergedPath.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.txtMergedPath.Name = "txtMergedPath";
		this.txtMergedPath.ReadOnly = true;
		this.txtMergedPath.Size = new System.Drawing.Size(1045, 23);
		this.txtMergedPath.TabIndex = 0;
		this.txtMergedPath.KeyDown += new System.Windows.Forms.KeyEventHandler(Txt_KeyDown);
		this.btnOpenDir2.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.btnOpenDir2.BackColor = System.Drawing.Color.LightSteelBlue;
		this.btnOpenDir2.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDark;
		this.btnOpenDir2.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnOpenDir2.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnOpenDir2.ForeColor = System.Drawing.SystemColors.ControlText;
		this.btnOpenDir2.Location = new System.Drawing.Point(545, 301);
		this.btnOpenDir2.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.btnOpenDir2.Name = "btnOpenDir2";
		this.btnOpenDir2.Size = new System.Drawing.Size(388, 35);
		this.btnOpenDir2.TabIndex = 2;
		LiveText.BindText(this.btnOpenDir2, () => UiText.T("MergeReportForm", "Open Directory"));
		this.btnOpenDir2.UseVisualStyleBackColor = false;
		this.btnOpenDir2.Click += new System.EventHandler(BtnOpenDir2_Click);
		this.btnOpenFile2.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.btnOpenFile2.BackColor = System.Drawing.Color.LightSteelBlue;
		this.btnOpenFile2.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDark;
		this.btnOpenFile2.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnOpenFile2.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnOpenFile2.ForeColor = System.Drawing.SystemColors.ControlText;
		this.btnOpenFile2.Location = new System.Drawing.Point(150, 301);
		this.btnOpenFile2.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.btnOpenFile2.Name = "btnOpenFile2";
		this.btnOpenFile2.Size = new System.Drawing.Size(387, 35);
		this.btnOpenFile2.TabIndex = 1;
		LiveText.BindText(this.btnOpenFile2, () => UiText.T("MergeReportForm", "Open File"));
		this.btnOpenFile2.UseVisualStyleBackColor = false;
		this.btnOpenFile2.Click += new System.EventHandler(BtnOpenFile2_Click);
		this.txtFilePath2.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.txtFilePath2.BorderStyle = System.Windows.Forms.BorderStyle.None;
		this.txtFilePath2.Font = new System.Drawing.Font("Segoe UI", 10.2f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.txtFilePath2.ForeColor = System.Drawing.SystemColors.ControlText;
		this.txtFilePath2.Location = new System.Drawing.Point(24, 261);
		this.txtFilePath2.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.txtFilePath2.Name = "txtFilePath2";
		this.txtFilePath2.ReadOnly = true;
		this.txtFilePath2.Size = new System.Drawing.Size(1045, 23);
		this.txtFilePath2.TabIndex = 0;
		this.txtFilePath2.KeyDown += new System.Windows.Forms.KeyEventHandler(Txt_KeyDown);
		this.btnMergeReportOK.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.btnMergeReportOK.BackColor = System.Drawing.Color.DarkSeaGreen;
		this.btnMergeReportOK.DialogResult = System.Windows.Forms.DialogResult.Cancel;
		this.btnMergeReportOK.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDarkDark;
		this.btnMergeReportOK.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnMergeReportOK.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnMergeReportOK.Location = new System.Drawing.Point(922, 551);
		this.btnMergeReportOK.Margin = new System.Windows.Forms.Padding(8);
		this.btnMergeReportOK.Name = "btnMergeReportOK";
		this.btnMergeReportOK.Size = new System.Drawing.Size(143, 35);
		this.btnMergeReportOK.TabIndex = 4;
		LiveText.BindText(this.btnMergeReportOK, () => UiText.T("MergeReportForm", "OK"));
		this.btnMergeReportOK.UseVisualStyleBackColor = false;
		this.btnMergeReportOK.Click += new System.EventHandler(BtnOK_Click);
		this.chkShowAfterMerge.AutoSize = true;
		this.chkShowAfterMerge.Font = new System.Drawing.Font("Segoe UI", 10.2f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.chkShowAfterMerge.Location = new System.Drawing.Point(17, 557);
		this.chkShowAfterMerge.Margin = new System.Windows.Forms.Padding(8);
		this.chkShowAfterMerge.Name = "chkShowAfterMerge";
		this.chkShowAfterMerge.Size = new System.Drawing.Size(292, 27);
		this.chkShowAfterMerge.TabIndex = 3;
		LiveText.BindText(this.chkShowAfterMerge, () => UiText.T("MergeReportForm", "&Show this report after each merge"));
		this.chkShowAfterMerge.UseVisualStyleBackColor = true;
		this.lblTempContentFiles.AutoSize = true;
		this.lblTempContentFiles.Location = new System.Drawing.Point(336, 18);
		this.lblTempContentFiles.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
		this.lblTempContentFiles.Name = "lblTempContentFiles";
		this.lblTempContentFiles.Size = new System.Drawing.Size(538, 40);
		this.lblTempContentFiles.TabIndex = 6;
		LiveText.BindText(this.lblTempContentFiles, () => UiText.T("MergeReportForm", "Note: The first 2 files listed below were temporarily unpacked from .bundle files.\r\nThey will be deleted when all merges are finished."));
		this.txtFilePath1.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.txtFilePath1.BorderStyle = System.Windows.Forms.BorderStyle.None;
		this.txtFilePath1.Font = new System.Drawing.Font("Segoe UI", 10.2f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.txtFilePath1.ForeColor = System.Drawing.SystemColors.ControlText;
		this.txtFilePath1.Location = new System.Drawing.Point(24, 125);
		this.txtFilePath1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.txtFilePath1.Name = "txtFilePath1";
		this.txtFilePath1.ReadOnly = true;
		this.txtFilePath1.Size = new System.Drawing.Size(1045, 23);
		this.txtFilePath1.TabIndex = 0;
		this.txtFilePath1.KeyDown += new System.Windows.Forms.KeyEventHandler(Txt_KeyDown);
		this.btnOpenFile1.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.btnOpenFile1.BackColor = System.Drawing.Color.LightSteelBlue;
		this.btnOpenFile1.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDark;
		this.btnOpenFile1.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnOpenFile1.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnOpenFile1.ForeColor = System.Drawing.SystemColors.ControlText;
		this.btnOpenFile1.Location = new System.Drawing.Point(150, 165);
		this.btnOpenFile1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.btnOpenFile1.Name = "btnOpenFile1";
		this.btnOpenFile1.Size = new System.Drawing.Size(387, 35);
		this.btnOpenFile1.TabIndex = 1;
		LiveText.BindText(this.btnOpenFile1, () => UiText.T("MergeReportForm", "Open File"));
		this.btnOpenFile1.UseVisualStyleBackColor = false;
		this.btnOpenFile1.Click += new System.EventHandler(BtnOpenFile1_Click);
		this.btnOpenDir1.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.btnOpenDir1.BackColor = System.Drawing.Color.LightSteelBlue;
		this.btnOpenDir1.FlatAppearance.BorderColor = System.Drawing.SystemColors.ControlDark;
		this.btnOpenDir1.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		this.btnOpenDir1.Font = new System.Drawing.Font("Segoe UI", 10.8f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.btnOpenDir1.ForeColor = System.Drawing.SystemColors.ControlText;
		this.btnOpenDir1.Location = new System.Drawing.Point(545, 165);
		this.btnOpenDir1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.btnOpenDir1.Name = "btnOpenDir1";
		this.btnOpenDir1.Size = new System.Drawing.Size(388, 35);
		this.btnOpenDir1.TabIndex = 2;
		LiveText.BindText(this.btnOpenDir1, () => UiText.T("MergeReportForm", "Open Directory"));
		this.btnOpenDir1.UseVisualStyleBackColor = false;
		this.btnOpenDir1.Click += new System.EventHandler(BtnOpenDir1_Click);
		this.lblArrowDown.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.lblArrowDown.AutoSize = true;
		this.lblArrowDown.Font = new System.Drawing.Font("Segoe UI", 30f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
		this.lblArrowDown.Location = new System.Drawing.Point(507, 345);
		this.lblArrowDown.Margin = new System.Windows.Forms.Padding(0);
		this.lblArrowDown.Name = "lblArrowDown";
		this.lblArrowDown.Size = new System.Drawing.Size(69, 67);
		this.lblArrowDown.TabIndex = 8;
		this.lblArrowDown.Text = "\ud83e\udc1f";
		this.lblArrowDown.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
		this.lblMod1.AutoSize = true;
		this.lblMod1.Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.lblMod1.Location = new System.Drawing.Point(51, 87);
		this.lblMod1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 5);
		this.lblMod1.Name = "lblMod1";
		this.lblMod1.Size = new System.Drawing.Size(70, 28);
		this.lblMod1.TabIndex = 9;
		LiveText.BindText(this.lblMod1, () => UiText.T("MergeReportForm", "Mod 1"));
		this.lblMod2.AutoSize = true;
		this.lblMod2.Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.lblMod2.Location = new System.Drawing.Point(51, 223);
		this.lblMod2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 5);
		this.lblMod2.Name = "lblMod2";
		this.lblMod2.Size = new System.Drawing.Size(70, 28);
		this.lblMod2.TabIndex = 10;
		LiveText.BindText(this.lblMod2, () => UiText.T("MergeReportForm", "Mod 2"));
		this.lblMergedFile.AutoSize = true;
		this.lblMergedFile.Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
		this.lblMergedFile.Location = new System.Drawing.Point(51, 382);
		this.lblMergedFile.Margin = new System.Windows.Forms.Padding(4, 0, 4, 5);
		this.lblMergedFile.Name = "lblMergedFile";
		this.lblMergedFile.Size = new System.Drawing.Size(116, 28);
		this.lblMergedFile.TabIndex = 11;
		LiveText.BindText(this.lblMergedFile, () => UiText.T("MergeReportForm", "Merged File"));
		this.lblNewPlus.Anchor = System.Windows.Forms.AnchorStyles.Top;
		this.lblNewPlus.AutoSize = true;
		this.lblNewPlus.Font = new System.Drawing.Font("Segoe UI", 45f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
		this.lblNewPlus.Location = new System.Drawing.Point(494, 178);
		this.lblNewPlus.Margin = new System.Windows.Forms.Padding(0);
		this.lblNewPlus.Name = "lblNewPlus";
		this.lblNewPlus.Size = new System.Drawing.Size(95, 100);
		this.lblNewPlus.TabIndex = 12;
		this.lblNewPlus.Text = "+";
		this.lblNewPlus.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
		base.AcceptButton = this.btnMergeReportOK;
		base.AutoScaleDimensions = new System.Drawing.SizeF(96f, 96f);
		base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
		base.CancelButton = this.btnMergeReportOK;
		base.ClientSize = new System.Drawing.Size(1082, 618);
		base.Controls.Add(this.lblMergedFile);
		base.Controls.Add(this.lblMod2);
		base.Controls.Add(this.lblMod1);
		base.Controls.Add(this.lblArrowDown);
		base.Controls.Add(this.btnOpenMergedDir);
		base.Controls.Add(this.btnOpenDir2);
		base.Controls.Add(this.btnOpenMergedFile);
		base.Controls.Add(this.btnOpenDir1);
		base.Controls.Add(this.txtMergedPath);
		base.Controls.Add(this.btnOpenFile2);
		base.Controls.Add(this.lblTempContentFiles);
		base.Controls.Add(this.txtFilePath2);
		base.Controls.Add(this.btnOpenFile1);
		base.Controls.Add(this.chkShowAfterMerge);
		base.Controls.Add(this.txtFilePath1);
		base.Controls.Add(this.btnMergeReportOK);
		base.Controls.Add(this.lblMergedFiles);
		base.Controls.Add(this.lblNewPlus);
		base.Icon = (System.Drawing.Icon)resources.GetObject("$this.Icon");
		base.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
		this.MaximumSize = new System.Drawing.Size(1892, 665);
		this.MinimumSize = new System.Drawing.Size(823, 665);
		base.Name = "MergeReportForm";
		base.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
		LiveText.BindText(this, () => UiText.T("MergeReportForm", "Merge Finished"));
		base.TopMost = true;
		base.FormClosing += new System.Windows.Forms.FormClosingEventHandler(MergeReportForm_FormClosing);
		base.ResumeLayout(false);
		base.PerformLayout();
	}
}
