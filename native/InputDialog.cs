using System;
using System.Drawing;
using System.Windows.Forms;

namespace SteamTune
{
    /// <summary>深色小输入框，用于填 AppID</summary>
    internal sealed class InputDialog : Form
    {
        private readonly TextBox box;

        public string Value
        {
            get { return box.Text.Trim(); }
        }

        public InputDialog(string title, string label, string initial, string hint)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(400, 168);
            BackColor = Color.FromArgb(20, 24, 36);
            ForeColor = Color.FromArgb(232, 236, 247);
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            Label caption = new Label();
            caption.Text = label;
            caption.SetBounds(16, 14, 348, 20);
            caption.ForeColor = Color.FromArgb(200, 208, 226);
            Controls.Add(caption);

            box = new TextBox();
            box.SetBounds(16, 38, 368, 24);
            box.Text = initial ?? string.Empty;
            box.BackColor = Color.FromArgb(10, 13, 20);
            box.ForeColor = Color.FromArgb(232, 236, 247);
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(box);

            Label hintLabel = new Label();
            hintLabel.Text = hint ?? string.Empty;
            hintLabel.SetBounds(16, 68, 368, 52);
            hintLabel.ForeColor = Color.FromArgb(139, 147, 167);
            Controls.Add(hintLabel);

            Button ok = new Button();
            ok.Text = "确定";
            ok.SetBounds(216, 126, 80, 26);
            ok.DialogResult = DialogResult.OK;
            ok.FlatStyle = FlatStyle.System;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(304, 126, 80, 26);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.FlatStyle = FlatStyle.System;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
