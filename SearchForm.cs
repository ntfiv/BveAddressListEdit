using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BveAddressListEdit
{
    /// <summary>PC 内から BVE の設定ファイルを探して、使うファイルを選んでもらう画面。</summary>
    internal sealed class SearchForm : Form
    {
        public FoundFile Selected { get; private set; }

        private readonly HashSet<string> inUse;
        private readonly ListView list;
        private readonly Label lblPhase;
        private readonly Label lblCurrent;
        private readonly ProgressBar bar;
        private readonly Button btnStop, btnUse, btnBrowse, btnClose;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private FileSearcher searcher;
        private bool finished;

        public SearchForm(Font font, IEnumerable<string> pathsInUse)
        {
            inUse = new HashSet<string>(pathsInUse, StringComparer.OrdinalIgnoreCase);

            Font = font;
            Text = "設定ファイルを探す";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(Px(760), Px(400));
            MinimumSize = new Size(Px(480), Px(300));

            var intro = new Label
            {
                Text = "BVE の設定ファイル（Preferences.xml / BveTs6.Preferences.xml）を PC 内から探します。\r\n" +
                       "見つかったら、使うファイルを選んで「このファイルを使う」を押してください。見つかった時点で検索を中止してもかまいません。",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, Px(6)),
            };

            list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            list.Columns.Add("種類", Px(100));
            list.Columns.Add("履歴", Px(56), HorizontalAlignment.Right);
            list.Columns.Add("更新日時", Px(120));
            list.Columns.Add("場所", Px(460));
            list.Resize += (s, e) => FitPathColumn();
            list.SelectedIndexChanged += (s, e) => btnUse.Enabled = list.SelectedItems.Count > 0;
            list.DoubleClick += (s, e) => UseSelected();

            lblPhase = new Label { AutoSize = true, Margin = new Padding(0, Px(6), 0, 0) };
            lblCurrent = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = Px(18),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0),
            };
            bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, Height = Px(10), Margin = new Padding(0, 0, 0, Px(6)) };

            btnBrowse = MakeButton("ファイルを指定...", BrowseFile);
            btnStop = MakeButton("検索を中止", () => cts.Cancel());
            btnUse = MakeButton("このファイルを使う", UseSelected);
            btnUse.Enabled = false;
            btnClose = MakeButton("閉じる", Close);
            AcceptButton = btnUse;
            CancelButton = btnClose;

            var rightButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            rightButtons.Controls.Add(btnClose);
            rightButtons.Controls.Add(btnUse);
            rightButtons.Controls.Add(btnStop);

            var buttonRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttonRow.Controls.Add(btnBrowse, 0, 0);
            buttonRow.Controls.Add(rightButtons, 1, 0);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(Px(10)),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(intro, 0, 0);
            root.Controls.Add(list, 0, 1);
            root.Controls.Add(lblPhase, 0, 2);
            root.Controls.Add(lblCurrent, 0, 3);
            root.Controls.Add(bar, 0, 4);
            root.Controls.Add(buttonRow, 0, 5);
            Controls.Add(root);

            timer.Tick += (s, e) => UpdateProgress();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitPathColumn();
            StartSearch();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cts.Cancel();
            timer.Stop();
            base.OnFormClosing(e);
        }

        private void StartSearch()
        {
            searcher = new FileSearcher(cts.Token, f => Post(() => AddFound(f)));
            timer.Start();
            UpdateProgress();
            Task.Run(() =>
            {
                try { searcher.Run(); }
                catch (Exception) { }
            }).ContinueWith(_ => Post(SearchFinished), TaskScheduler.Default);
        }

        /// <summary>検索スレッドから画面を更新する（画面が閉じていたら何もしない）。</summary>
        private void Post(Action action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        private void AddFound(FoundFile f)
        {
            if (IsDisposed) return;
            string kind = (f.Version == BveVersion.Bve5 ? "BVE5" : "BVE6") + (inUse.Contains(f.Path) ? "（使用中）" : "");
            var item = new ListViewItem(kind) { Tag = f };
            item.SubItems.Add(f.Count + " 件");
            item.SubItems.Add(f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("yyyy/MM/dd HH:mm"));
            item.SubItems.Add(f.Path);
            list.Items.Add(item);
            if (list.SelectedItems.Count == 0)
                item.Selected = true;
            UpdateProgress();
        }

        private void UpdateProgress()
        {
            if (searcher == null || finished) return;
            lblPhase.Text = $"{searcher.Phase}…　{searcher.DirsScanned:N0} フォルダ確認 ／ {list.Items.Count} 件見つかりました";
            lblCurrent.Text = searcher.CurrentDir;
        }

        private void SearchFinished()
        {
            if (IsDisposed) return;
            finished = true;
            timer.Stop();
            bar.Visible = false;
            btnStop.Enabled = false;
            lblCurrent.Text = "";
            string head = cts.IsCancellationRequested ? "検索を中止しました" : "検索が終わりました";
            lblPhase.Text = list.Items.Count > 0
                ? $"{head}（{list.Items.Count} 件見つかりました）"
                : $"{head}。設定ファイルは見つかりませんでした。BVE を一度起動して終了すると作られます。";
        }

        private void UseSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            Selected = (FoundFile)list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
        }

        private void BrowseFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "BVE の設定ファイルを選択",
                Filter = "BVE の設定ファイル|Preferences.xml;BveTs6.Preferences.xml|XML ファイル (*.xml)|*.xml",
                CheckFileExists = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!PreferencesFile.TryInspect(dlg.FileName, out BveVersion version, out int count))
                {
                    MessageBox.Show(this,
                        "BVE の設定ファイルではないようです。\r\n\r\n" + dlg.FileName + "\r\n\r\n" +
                        "ファイル名が Preferences.xml（BVE5）または BveTs6.Preferences.xml（BVE6）で、BVE が書いたものを選んでください。",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Selected = new FoundFile { Path = dlg.FileName, Version = version, Count = count, Modified = File.GetLastWriteTime(dlg.FileName) };
                DialogResult = DialogResult.OK;
            }
        }

        private void FitPathColumn()
        {
            int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[2].Width;
            list.Columns[3].Width = Math.Max(Px(200), w);
        }

        private int Px(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

        private Button MakeButton(string text, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(Px(100), Px(28)),
                Margin = new Padding(Px(6), 0, 0, 0),
                UseVisualStyleBackColor = true,
            };
            b.Click += (s, e) => onClick();
            return b;
        }
    }
}
