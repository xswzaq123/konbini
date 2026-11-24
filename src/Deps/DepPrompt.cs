using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Konbini.Deps
{
    /// <summary>UI for installing a downloadable dependency: asks first, then shows a cancellable progress dialog.</summary>
    public static class DepPrompt
    {
        /// <summary>For tests: answers the "Download?" question without showing it (null = ask the user).</summary>
        public static bool? AutoAnswer;

        /// <summary>
        /// Returns true when the dependency is ready. If it's missing, asks the user (on the UI thread) and installs it with
        /// a progress dialog. Returns false if the user declines or cancels; shows the error if installing fails.
        /// </summary>
        /// Every download is asked about first (Yes/No); nothing is ever fetched without the user saying yes.
        public static bool Ensure(IWin32Window owner, Dependency d)
        {
            if (DepManager.IsInstalled(d)) return true;
            bool damaged = DepManager.Status(d) == DepStatus.Damaged;
            string msg = (damaged ? d.DisplayName + " is damaged and needs to be downloaded again.\n\n" : "Konbini needs to download " + d.DisplayName + " for " + d.Purpose + ".\n\n")
                + "Size: " + ImageUtil.FormatBytes(d.DownloadSize) + "\nFrom: " + d.SourceName + " (secure download, checked before use)\nLicence: " + d.Licence
                + "\n\nIt's installed for your Windows account only — no administrator rights needed. Download now?";
            bool yes = AutoAnswer.HasValue ? AutoAnswer.Value
                : MessageBox.Show(owner, msg, "Konbini — download needed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) return false;
            return RunWithProgress(owner, d);
        }

        public static bool RunWithProgress(IWin32Window owner, Dependency d)
        {
            Exception error = null; bool ok = false;
            using (var dlg = new Form
            {
                Text = "Installing " + d.DisplayName, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
                ClientSize = new Size(440, 130), Font = SystemFonts.MessageBoxFont,
            })
            {
                var title = new Label { Text = d.DisplayName, AutoSize = true, Location = new Point(16, 14), Font = new Font(dlg.Font.FontFamily, 10f, FontStyle.Bold) };
                var status = Theme.MutedLabel("Starting…"); status.Location = new Point(16, 42); status.AutoSize = true;
                var bar = new SlimProgress { Location = new Point(16, 66), Width = 408, Maximum = 1000 };
                var cancel = Theme.SecondaryButton("Cancel"); cancel.Location = new Point(334, 88); cancel.Width = 90;
                dlg.Controls.AddRange(new Control[] { title, status, bar, cancel });
                Theme.Apply(dlg);
                var cts = new CancellationTokenSource();
                cancel.Click += delegate { cancel.Enabled = false; status.Text = "Stopping… (the download can be resumed later)"; cts.Cancel(); };
                dlg.FormClosing += (s, e) => { if (!ok && error == null && !cts.IsCancellationRequested) { e.Cancel = true; cts.Cancel(); } };
                dlg.Shown += delegate
                {
                    Theme.TitleBar(dlg);
                    new Thread(() =>
                    {
                        try
                        {
                            DepManager.EnsureInstalled(d, (done, total, text) =>
                            {
                                try
                                {
                                    dlg.BeginInvoke((Action)(() =>
                                    {
                                        bar.Value = (int)(1000.0 * done / Math.Max(1, total));
                                        status.Text = text + "  " + ImageUtil.FormatBytes(done) + " of " + ImageUtil.FormatBytes(total);
                                    }));
                                }
                                catch (InvalidOperationException) { }
                            }, () => cts.IsCancellationRequested);
                            ok = true;
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { error = ex; }
                        try { dlg.BeginInvoke((Action)(() => { cts.Cancel(); dlg.Close(); })); } catch (InvalidOperationException) { }
                    }) { IsBackground = true, Name = "dep-install" }.Start();
                };
                dlg.ShowDialog(owner);
            }
            if (error != null)
            {
                if (AutoAnswer.HasValue) throw error;
                MessageBox.Show(owner, error is DependencyException ? error.Message : "Couldn't install " + d.DisplayName + " (" + error.Message + ").",
                    "Konbini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return ok;
        }
    }
}
