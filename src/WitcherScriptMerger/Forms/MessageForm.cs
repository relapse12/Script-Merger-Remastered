using System;
using System.ComponentModel;
using System.Windows.Forms;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;

namespace WitcherScriptMerger.Forms;

[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
public class MessageForm : BaseForm
{
	private TableLayoutPanel tlpMessageForm;

	private TextBox tbMessage;

	private Panel panel1;

	public Button btnNo;

	public Button btnYes;

	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	internal string Message
	{
		get
		{
			return tbMessage.Text;
		}
		set
		{
			tbMessage.Text = value;
		}
	}

	public MessageForm()
	{
		InitializeComponent();
	}

	private void MessageForm_Load( object sender, EventArgs e )
	{
		base.ActiveControl = btnYes;
	}

	private void InitializeComponent()
	{
		this.tlpMessageForm = new System.Windows.Forms.TableLayoutPanel();
		this.tbMessage = new System.Windows.Forms.TextBox();
		this.panel1 = new System.Windows.Forms.Panel();
		this.btnNo = new System.Windows.Forms.Button();
		this.btnYes = new System.Windows.Forms.Button();
		this.tlpMessageForm.SuspendLayout();
		this.panel1.SuspendLayout();
		base.SuspendLayout();
		this.tlpMessageForm.ColumnCount = 1;
		this.tlpMessageForm.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100f));
		this.tlpMessageForm.Controls.Add(this.tbMessage, 0, 0);
		this.tlpMessageForm.Controls.Add(this.panel1, 0, 1);
		this.tlpMessageForm.Dock = System.Windows.Forms.DockStyle.Fill;
		this.tlpMessageForm.Location = new System.Drawing.Point(0, 0);
		this.tlpMessageForm.Name = "tlpMessageForm";
		this.tlpMessageForm.RowCount = 2;
		this.tlpMessageForm.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100f));
		this.tlpMessageForm.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 90f));
		this.tlpMessageForm.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20f));
		this.tlpMessageForm.Size = new System.Drawing.Size(979, 673);
		this.tlpMessageForm.TabIndex = 0;
		this.tbMessage.Dock = System.Windows.Forms.DockStyle.Fill;
		this.tbMessage.Location = new System.Drawing.Point(3, 3);
		this.tbMessage.Multiline = true;
		this.tbMessage.Name = "tbMessage";
		this.tbMessage.ReadOnly = true;
		this.tbMessage.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
		this.tbMessage.Size = new System.Drawing.Size(973, 577);
		this.tbMessage.TabIndex = 0;
		this.panel1.Controls.Add(this.btnNo);
		this.panel1.Controls.Add(this.btnYes);
		this.panel1.Dock = System.Windows.Forms.DockStyle.Fill;
		this.panel1.Location = new System.Drawing.Point(3, 586);
		this.panel1.Name = "panel1";
		this.panel1.Size = new System.Drawing.Size(973, 84);
		this.panel1.TabIndex = 1;
		this.btnNo.Anchor = System.Windows.Forms.AnchorStyles.Right;
		this.btnNo.AutoSize = true;
		this.btnNo.DialogResult = System.Windows.Forms.DialogResult.No;
		this.btnNo.Font = new System.Drawing.Font("Segoe UI", 11f);
		this.btnNo.Location = new System.Drawing.Point(741, 17);
		this.btnNo.Margin = new System.Windows.Forms.Padding(0, 0, 5, 0);
		this.btnNo.Name = "btnNo";
		this.btnNo.Size = new System.Drawing.Size(221, 50);
		this.btnNo.TabIndex = 8;
		LiveText.BindText(this.btnNo, () => UiText.T("MessageForm", "&No"));
		this.btnNo.UseVisualStyleBackColor = false;
		this.btnYes.Anchor = System.Windows.Forms.AnchorStyles.Right;
		this.btnYes.AutoSize = true;
		this.btnYes.DialogResult = System.Windows.Forms.DialogResult.Yes;
		this.btnYes.Font = new System.Drawing.Font("Segoe UI", 11f);
		this.btnYes.Location = new System.Drawing.Point(515, 17);
		this.btnYes.Margin = new System.Windows.Forms.Padding(0, 0, 5, 0);
		this.btnYes.Name = "btnYes";
		this.btnYes.Size = new System.Drawing.Size(221, 50);
		this.btnYes.TabIndex = 7;
		LiveText.BindText(this.btnYes, () => UiText.T("MessageForm", "&Yes"));
		this.btnYes.UseVisualStyleBackColor = false;
		base.AcceptButton = this.btnYes;
		base.AutoScaleDimensions = new System.Drawing.SizeF(96f, 96f);
		base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
		base.CancelButton = this.btnNo;
		base.ClientSize = new System.Drawing.Size(979, 673);
		base.Controls.Add(this.tlpMessageForm);
		base.FormBorderStyle = System.Windows.Forms.FormBorderStyle.SizableToolWindow;
		base.Name = "MessageForm";
		base.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Show;
		base.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
		LiveText.BindText(this, () => UiText.T("MessageForm", "MessageForm"));
		base.Load += new System.EventHandler(MessageForm_Load);
		this.tlpMessageForm.ResumeLayout(false);
		this.tlpMessageForm.PerformLayout();
		this.panel1.ResumeLayout(false);
		this.panel1.PerformLayout();
		base.ResumeLayout(false);
	}
}
