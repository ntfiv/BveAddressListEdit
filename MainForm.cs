using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BveAddressListEdit
{
    internal sealed class MainForm : Form
    {
        private static readonly string AppTitle =
            "BVE AddressList Edit v" + typeof(MainForm).Assembly.GetName().Version.ToString(3);

        /// <summary>起動時の引数で指定された設定フォルダ（テスト用）。null なら通常どおり。</summary>
        private readonly string overrideDir;
        private readonly Dictionary<BveVersion, string> paths = new Dictionary<BveVersion, string>();

        private readonly RadioButton rbBve5;
        private readonly RadioButton rbBve6;
        private readonly LinkLabel lblPath;
        private readonly ListView list;
        private readonly Panel insertLine;
        private readonly Button btnAdd, btnRemove, btnTop, btnUp, btnDown, btnBottom, btnCleanup, btnReload, btnSave;
        private readonly ToolStripStatusLabel status;
        private readonly ToolTip toolTip = new ToolTip();

        private BveVersion version;
        private string currentPath;
        private List<string> entries = new List<string>();
        private List<string> loaded = new List<string>();   // 読み込み時（最後に保存した時）の内容
        private readonly Dictionary<string, bool> existsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool switchingVersion;

        /// <summary>リスト内ドラッグ用のデータ型</summary>
        private sealed class RowDrag { public List<int> Indices; }

        public MainForm(string overrideDir)
        {
            this.overrideDir = overrideDir;
            ResolvePaths();

            Font = new Font("Yu Gothic UI", 9f);
            Text = AppTitle;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            ClientSize = new Size(Px(760), Px(440));
            MinimumSize = new Size(Px(520), Px(320));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 上部：対象の切り替え ----
            rbBve5 = new RadioButton { Text = "BVE5", AutoSize = true, Margin = new Padding(0, 3, 12, 3) };
            rbBve6 = new RadioButton { Text = "BVE6", AutoSize = true, Margin = new Padding(0, 3, 12, 3) };
            rbBve5.CheckedChanged += (s, e) => { if (rbBve5.Checked) SwitchVersion(BveVersion.Bve5); };
            rbBve6.CheckedChanged += (s, e) => { if (rbBve6.Checked) SwitchVersion(BveVersion.Bve6); };

            var versionRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            versionRow.Controls.Add(new Label { Text = "対象:", AutoSize = true, Margin = new Padding(0, 6, 8, 3) });
            versionRow.Controls.Add(rbBve5);
            versionRow.Controls.Add(rbBve6);
            var btnFind = new Button
            {
                Text = "設定ファイルを探す...",
                AutoSize = true,
                Margin = new Padding(Px(8), 0, 0, 0),
                UseVisualStyleBackColor = true,
            };
            btnFind.Click += (s, e) => FindSettings();
            toolTip.SetToolTip(btnFind, "PC 内から BVE の設定ファイル（Preferences.xml / BveTs6.Preferences.xml）を探します");
            versionRow.Controls.Add(btnFind);

            lblPath = new LinkLabel { AutoSize = true, Margin = new Padding(0, 2, 0, 6), AutoEllipsis = true };
            lblPath.LinkClicked += (s, e) =>
            {
                if (currentPath != null) OpenInExplorer(currentPath, select: true);
                else FindSettings();
            };

            // ---- 中央：リスト ----
            list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                AllowDrop = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            list.Columns.Add("#", Px(36), HorizontalAlignment.Right);
            list.Columns.Add("状態", Px(90));
            list.Columns.Add("フォルダ", Px(500));
            list.Resize += (s, e) => FitPathColumn();
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.KeyDown += List_KeyDown;
            list.DoubleClick += (s, e) => OpenSelectedFolder();
            list.ItemDrag += List_ItemDrag;
            list.DragEnter += List_DragOver;
            list.DragOver += List_DragOver;
            list.DragLeave += (s, e) => insertLine.Visible = false;
            list.DragDrop += List_DragDrop;
            list.ContextMenuStrip = BuildContextMenu();

            insertLine = new Panel { Height = 2, BackColor = SystemColors.Highlight, Visible = false };
            list.Controls.Add(insertLine);

            // ---- 右側：ボタン ----
            btnAdd = MakeButton("追加...", "フォルダを選んで先頭に追加します（エクスプローラーからドラッグ＆ドロップでも追加できます）", AddFolders);
            btnRemove = MakeButton("削除", "選択した履歴を削除します (Delete)", RemoveSelected);
            btnTop = MakeButton("一番上へ", "選択した履歴を一番上へ移動します", MoveToTop);
            btnUp = MakeButton("↑ 上へ", "選択した履歴を1つ上へ移動します (Alt+↑)", MoveUp);
            btnDown = MakeButton("↓ 下へ", "選択した履歴を1つ下へ移動します (Alt+↓)", MoveDown);
            btnBottom = MakeButton("一番下へ", "選択した履歴を一番下へ移動します", MoveToBottom);
            btnCleanup = MakeButton("存在しない\r\nフォルダを削除", "PCに存在しないフォルダをまとめて削除します", RemoveMissing);
            btnReload = MakeButton("読み込み直す", "保存していない変更を捨てて、設定ファイルを読み込み直します (F5)", Reload);
            btnSave = MakeButton("保存", "設定ファイルに書き込みます (Ctrl+S)", Save);
            btnSave.Font = new Font(Font, FontStyle.Bold);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(8, 0, 0, 0),
            };
            buttons.Controls.Add(btnAdd);
            buttons.Controls.Add(btnRemove);
            buttons.Controls.Add(Spacer());
            buttons.Controls.Add(btnTop);
            buttons.Controls.Add(btnUp);
            buttons.Controls.Add(btnDown);
            buttons.Controls.Add(btnBottom);
            buttons.Controls.Add(Spacer());
            buttons.Controls.Add(btnCleanup);
            buttons.Controls.Add(Spacer());
            buttons.Controls.Add(btnReload);
            buttons.Controls.Add(btnSave);

            // ---- 全体 ----
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(Px(10), Px(8), Px(10), Px(6)),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(versionRow, 0, 0);
            root.SetColumnSpan(versionRow, 2);
            root.Controls.Add(lblPath, 0, 1);
            root.SetColumnSpan(lblPath, 2);
            root.Controls.Add(list, 0, 2);
            root.Controls.Add(buttons, 1, 2);

            status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var statusStrip = new StatusStrip { SizingGrip = true };
            statusStrip.Items.Add(status);

            Controls.Add(root);
            Controls.Add(statusStrip);

            InitVersionSelection();
        }

        // ================================================================
        //  読み込み・保存
        // ================================================================

        private string PathOf(BveVersion v) => paths[v];

        /// <summary>
        /// 使う設定ファイルを決める。検索で選んだ場所を覚えていればそれを、
        /// なければ ドキュメント\BveTs\Settings を使う。
        /// </summary>
        private void ResolvePaths()
        {
            foreach (BveVersion v in new[] { BveVersion.Bve5, BveVersion.Bve6 })
            {
                string name = PreferencesFile.FileName(v);
                if (overrideDir != null)
                {
                    paths[v] = Path.Combine(overrideDir, name);
                    continue;
                }
                string remembered = PathMemory.Get(v);
                paths[v] = !string.IsNullOrEmpty(remembered) && File.Exists(remembered)
                    ? remembered
                    : Path.Combine(PreferencesFile.DefaultSettingsDir, name);
            }
        }

        private void UpdateVersionRadios()
        {
            bool has5 = File.Exists(PathOf(BveVersion.Bve5));
            bool has6 = File.Exists(PathOf(BveVersion.Bve6));
            rbBve5.Text = has5 ? "BVE5" : "BVE5（設定ファイルなし）";
            rbBve6.Text = has6 ? "BVE6" : "BVE6（設定ファイルなし）";
            rbBve5.Enabled = has5;
            rbBve6.Enabled = has6;
        }

        private void InitVersionSelection()
        {
            UpdateVersionRadios();
            bool has5 = rbBve5.Enabled, has6 = rbBve6.Enabled;

            if (!has5 && !has6)
            {
                lblPath.Text = "設定ファイルが見つかりません（クリックするか「設定ファイルを探す...」で PC 内から探せます）";
                currentPath = null;
                RefreshList();
                return;
            }
            // 両方あるときは BVE6 を開く
            if (has6) rbBve6.Checked = true;
            else rbBve5.Checked = true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (currentPath != null) return;
            var r = MessageBox.Show(this,
                "BVE の設定ファイルが、いつもの場所（ドキュメント\\BveTs\\Settings）に見つかりませんでした。\r\n\r\n" +
                "PC 内から探しますか？",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) FindSettings();
        }

        /// <summary>PC 内から設定ファイルを探し、選ばれたものを開いて場所を覚える。</summary>
        private void FindSettings()
        {
            FoundFile f;
            using (var dlg = new SearchForm(Font, paths.Values.Where(File.Exists)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected == null) return;
                f = dlg.Selected;
            }
            if (string.Equals(f.Path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "このファイルは既に開いています";
                return;
            }
            if (!ConfirmDiscard()) return;

            paths[f.Version] = f.Path;
            if (overrideDir == null) PathMemory.Set(f.Version, f.Path);
            UpdateVersionRadios();

            switchingVersion = true;
            (f.Version == BveVersion.Bve5 ? rbBve5 : rbBve6).Checked = true;
            switchingVersion = false;

            version = f.Version;
            currentPath = f.Path;
            lblPath.Text = currentPath;
            toolTip.SetToolTip(lblPath, "クリックでエクスプローラーに表示");
            LoadFile(currentPath);
            status.Text = overrideDir == null
                ? "この設定ファイルを開きました。次回からもこの場所を使います"
                : "この設定ファイルを開きました";
        }

        private void SwitchVersion(BveVersion v)
        {
            if (switchingVersion) return;
            if (currentPath != null && string.Equals(PathOf(v), currentPath, StringComparison.OrdinalIgnoreCase)) return;

            if (currentPath != null && !ConfirmDiscard())
            {
                // 元の選択に戻す
                switchingVersion = true;
                (version == BveVersion.Bve5 ? rbBve5 : rbBve6).Checked = true;
                switchingVersion = false;
                return;
            }

            version = v;
            currentPath = PathOf(v);
            lblPath.Text = currentPath;
            toolTip.SetToolTip(lblPath, "クリックでエクスプローラーに表示");
            LoadFile(currentPath);
        }

        private void LoadFile(string path)
        {
            try
            {
                entries = PreferencesFile.ReadAddresses(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "設定ファイルを読み込めませんでした。\r\n\r\n" + path + "\r\n\r\n" + ex.Message,
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                entries = new List<string>();
            }
            loaded = new List<string>(entries);
            existsCache.Clear();
            RefreshList();
        }

        private void Reload()
        {
            if (currentPath == null) return;
            if (IsDirty)
            {
                var r = MessageBox.Show(this, "保存していない変更を捨てて、設定ファイルを読み込み直しますか？",
                    AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
            }
            LoadFile(currentPath);
        }

        private bool IsDirty => currentPath != null && !entries.SequenceEqual(loaded);

        private bool Save()
        {
            if (currentPath == null) return false;

            if (PreferencesFile.IsBveRunning())
            {
                var r = MessageBox.Show(this,
                    "BVE が起動中です。\r\nBVE は終了するときに設定ファイルを書き戻すため、ここで保存しても上書きされてしまいます。\r\n\r\n" +
                    "BVE を終了してから保存することをおすすめします。それでも保存しますか？",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return false;
            }

            try
            {
                // 読み込んだ後に BVE 側で履歴が変わっていないか確認
                List<string> onDisk = PreferencesFile.ReadAddresses(currentPath);
                if (!onDisk.SequenceEqual(loaded))
                {
                    var r = MessageBox.Show(this,
                        "読み込んだ後に、設定ファイルの履歴が別の場所で変更されています（BVE でフォルダを開いた など）。\r\n\r\n" +
                        "この画面の内容で上書きしますか？\r\n「いいえ」を選ぶと保存を中止します。「読み込み直す」で最新の内容を確認できます。",
                        AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes) return false;
                }

                PreferencesFile.WriteAddresses(currentPath, entries);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存できませんでした。\r\n\r\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            loaded = new List<string>(entries);
            RefreshList(SelectedIndices());
            status.Text = "保存しました（変更前の内容は " + Path.GetFileName(currentPath) + ".bak に残しています）";
            return true;
        }

        /// <summary>未保存の変更があれば保存するか尋ねる。続行してよければ true。</summary>
        private bool ConfirmDiscard()
        {
            if (!IsDirty) return true;
            var r = MessageBox.Show(this,
                Path.GetFileName(currentPath) + " に保存していない変更があります。保存しますか？",
                AppTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) return Save();
            return r == DialogResult.No;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) e.Cancel = true;
            base.OnFormClosing(e);
        }

        // ================================================================
        //  リスト表示
        // ================================================================

        private bool Exists(string path)
        {
            if (!existsCache.TryGetValue(path, out bool ok))
            {
                try { ok = Directory.Exists(path); } catch { ok = false; }
                existsCache[path] = ok;
            }
            return ok;
        }

        private void RefreshList(IEnumerable<int> select = null)
        {
            var selectSet = new HashSet<int>(select ?? Enumerable.Empty<int>());
            int top = list.TopItem?.Index ?? 0;

            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                bool ok = Exists(entries[i]);
                var item = new ListViewItem((i + 1).ToString()) { UseItemStyleForSubItems = true };
                item.SubItems.Add(ok ? "OK" : "見つからない");
                item.SubItems.Add(entries[i]);
                if (!ok) item.ForeColor = Color.Firebrick;
                item.Selected = selectSet.Contains(i);
                list.Items.Add(item);
            }
            list.EndUpdate();

            if (list.Items.Count > 0)
            {
                list.TopItem = list.Items[Math.Min(top, list.Items.Count - 1)];
                int first = selectSet.Count > 0 ? selectSet.Min() : -1;
                if (first >= 0 && first < list.Items.Count)
                {
                    list.Items[first].Focused = true;
                    list.EnsureVisible(first);
                }
            }
            FitPathColumn();
            UpdateButtons();
            UpdateStatus();
        }

        private void FitPathColumn()
        {
            if (list.Columns.Count < 3) return;
            int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width;
            list.Columns[2].Width = Math.Max(Px(200), w);
        }

        private void UpdateButtons()
        {
            bool loadedFile = currentPath != null;
            var sel = SelectedIndices();
            bool any = sel.Count > 0;
            btnAdd.Enabled = loadedFile;
            btnRemove.Enabled = any;
            btnTop.Enabled = btnUp.Enabled = any && sel[sel.Count - 1] >= sel.Count;           // まだ上に詰められる
            btnBottom.Enabled = btnDown.Enabled = any && sel[0] < entries.Count - sel.Count;   // まだ下に詰められる
            btnCleanup.Enabled = loadedFile && entries.Any(p => !Exists(p));
            btnReload.Enabled = loadedFile;
            btnSave.Enabled = IsDirty;
        }

        private void UpdateStatus()
        {
            bool dirty = IsDirty;
            Text = (dirty ? "* " : "") + AppTitle + (currentPath != null ? " - " + (version == BveVersion.Bve5 ? "BVE5" : "BVE6") : "");
            if (currentPath == null)
            {
                status.Text = "";
                return;
            }
            int missing = entries.Count(p => !Exists(p));
            status.Text = $"{entries.Count} 件" +
                          (missing > 0 ? $"（見つからないフォルダ {missing} 件）" : "") +
                          (dirty ? "　● 保存していない変更があります" : "");
        }

        private List<int> SelectedIndices() => list.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();

        // ================================================================
        //  編集操作
        // ================================================================

        private static string Normalize(string path) => path.Trim().TrimEnd('\\', '/');

        private int IndexOfPath(string path) =>
            entries.FindIndex(p => string.Equals(Normalize(p), Normalize(path), StringComparison.OrdinalIgnoreCase));

        /// <summary>フォルダを index の位置に追加する。既に登録済みのものは追加しない。</summary>
        private void InsertFolders(IEnumerable<string> folders, int index)
        {
            var added = new List<string>();
            var duplicated = new List<int>();
            foreach (string f in folders)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                int existing = IndexOfPath(f);
                if (existing >= 0) { duplicated.Add(existing); continue; }
                if (added.Any(a => string.Equals(Normalize(a), Normalize(f), StringComparison.OrdinalIgnoreCase))) continue;
                added.Add(f);
            }

            index = Math.Max(0, Math.Min(index, entries.Count));
            entries.InsertRange(index, added);
            existsCache.Clear();

            // 既存の行番号は挿入でずれる
            var select = Enumerable.Range(index, added.Count)
                .Concat(duplicated.Select(d => d >= index ? d + added.Count : d));
            RefreshList(select);

            if (duplicated.Count > 0 && added.Count == 0)
                status.Text = "選んだフォルダは既に登録されています";
            else if (duplicated.Count > 0)
                status.Text = $"{added.Count} 件追加しました（{duplicated.Count} 件は既に登録済み）";
            else if (added.Count > 0)
                status.Text = $"{added.Count} 件追加しました（保存するまで設定ファイルには反映されません）";
        }

        private void AddFolders()
        {
            if (currentPath == null) return;
            string initial = null;
            var sel = SelectedIndices();
            if (sel.Count > 0 && Exists(entries[sel[0]]))
                initial = Path.GetDirectoryName(Normalize(entries[sel[0]])) ?? entries[sel[0]];
            else
                initial = entries.FirstOrDefault(Exists);

            List<string> folders = FolderPicker.Show(this, "履歴に追加するシナリオフォルダを選択（複数可）", initial);
            if (folders.Count > 0)
                InsertFolders(folders, 0);
        }

        private void RemoveSelected()
        {
            var sel = SelectedIndices();
            if (sel.Count == 0) return;
            for (int i = sel.Count - 1; i >= 0; i--)
                entries.RemoveAt(sel[i]);
            int next = Math.Min(sel[0], entries.Count - 1);
            RefreshList(next >= 0 ? new[] { next } : null);
        }

        private void RemoveMissing()
        {
            existsCache.Clear(); // ドライブの接続状態が変わっているかもしれないので確認し直す
            var missing = entries.Where(p => !Exists(p)).ToList();
            if (missing.Count == 0)
            {
                RefreshList(SelectedIndices());
                status.Text = "存在しないフォルダはありません";
                return;
            }

            const int maxShown = 15;
            string shown = string.Join("\r\n", missing.Take(maxShown).Select(p => "・" + p));
            if (missing.Count > maxShown) shown += $"\r\n…ほか {missing.Count - maxShown} 件";
            var r = MessageBox.Show(this,
                $"次の {missing.Count} 件は PC に見つかりません。履歴から削除しますか？\r\n\r\n{shown}\r\n\r\n" +
                "※ 外付けドライブ・ネットワークドライブが外れているだけの場合もあります。",
                AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            entries = entries.Where(Exists).ToList();
            RefreshList();
            status.Text = $"{missing.Count} 件削除しました（保存するまで設定ファイルには反映されません）";
        }

        private void MoveUp()
        {
            var sel = SelectedIndices();
            var newSel = new List<int>();
            int limit = 0;
            foreach (int i in sel)
            {
                if (i - 1 >= limit)
                {
                    Swap(i, i - 1);
                    newSel.Add(i - 1);
                    limit = i;
                }
                else
                {
                    newSel.Add(i);
                    limit = i + 1;
                }
            }
            RefreshList(newSel);
        }

        private void MoveDown()
        {
            var sel = SelectedIndices();
            sel.Reverse();
            var newSel = new List<int>();
            int limit = entries.Count - 1;
            foreach (int i in sel)
            {
                if (i + 1 <= limit)
                {
                    Swap(i, i + 1);
                    newSel.Add(i + 1);
                    limit = i;
                }
                else
                {
                    newSel.Add(i);
                    limit = i - 1;
                }
            }
            RefreshList(newSel);
        }

        private void MoveToTop() => MoveSelectedTo(0);

        private void MoveToBottom() => MoveSelectedTo(entries.Count);

        /// <summary>選択行を、元の並び順のまま index（移動前の行番号での挿入位置）へ移動する。</summary>
        private void MoveSelectedTo(int index) => MoveRows(SelectedIndices(), index);

        private void MoveRows(List<int> rows, int index)
        {
            if (rows.Count == 0) return;
            var moving = rows.Select(i => entries[i]).ToList();
            int insertAt = index - rows.Count(i => i < index);
            foreach (int i in rows.OrderByDescending(i => i))
                entries.RemoveAt(i);
            insertAt = Math.Max(0, Math.Min(insertAt, entries.Count));
            entries.InsertRange(insertAt, moving);
            RefreshList(Enumerable.Range(insertAt, moving.Count));
        }

        private void Swap(int a, int b)
        {
            string t = entries[a];
            entries[a] = entries[b];
            entries[b] = t;
        }

        private void OpenSelectedFolder()
        {
            var sel = SelectedIndices();
            if (sel.Count == 0) return;
            string path = entries[sel[0]];
            if (!Exists(path))
            {
                status.Text = "このフォルダは見つかりません: " + path;
                return;
            }
            OpenInExplorer(path, select: false);
        }

        private static void OpenInExplorer(string path, bool select)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (select)
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                    Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("エクスプローラーで開く", null, (s, e) => OpenSelectedFolder());
            var copy = new ToolStripMenuItem("パスをコピー", null, (s, e) =>
            {
                var sel = SelectedIndices();
                if (sel.Count > 0) Clipboard.SetText(string.Join("\r\n", sel.Select(i => entries[i])));
            });
            var remove = new ToolStripMenuItem("削除", null, (s, e) => RemoveSelected()) { ShortcutKeyDisplayString = "Delete" };
            menu.Items.AddRange(new ToolStripItem[] { open, copy, new ToolStripSeparator(), remove });
            menu.Opening += (s, e) =>
            {
                bool any = list.SelectedIndices.Count > 0;
                open.Enabled = copy.Enabled = remove.Enabled = any;
            };
            return menu;
        }

        // ================================================================
        //  キーボード
        // ================================================================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.S:
                    if (IsDirty) Save();
                    return true;
                case Keys.F5:
                    Reload();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Up && btnUp.Enabled)
            {
                MoveUp();
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Down && btnDown.Enabled)
            {
                MoveDown();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in list.Items) item.Selected = true;
                e.Handled = true;
            }
        }

        // ================================================================
        //  ドラッグ＆ドロップ（並べ替え / エクスプローラーからの追加）
        // ================================================================

        private void List_ItemDrag(object sender, ItemDragEventArgs e)
        {
            var sel = SelectedIndices();
            if (sel.Count == 0) return;
            list.DoDragDrop(new RowDrag { Indices = sel }, DragDropEffects.Move);
            insertLine.Visible = false;
        }

        /// <summary>マウス位置から挿入位置（0..Count）を求める。</summary>
        private int DropIndexAt(Point screen, out int lineY)
        {
            Point p = list.PointToClient(screen);
            int headerBottom = list.Items.Count > 0 ? list.Items[list.TopItem?.Index ?? 0].Bounds.Top : 0;
            ListViewItem item = list.GetItemAt(Px(4), Math.Max(p.Y, headerBottom));
            if (item == null)
            {
                if (list.Items.Count == 0) { lineY = headerBottom; return 0; }
                var last = list.Items[list.Items.Count - 1].Bounds;
                if (p.Y >= last.Bottom) { lineY = last.Bottom; return list.Items.Count; }
                lineY = headerBottom;
                return list.TopItem?.Index ?? 0;
            }
            Rectangle b = item.Bounds;
            if (p.Y < b.Top + b.Height / 2) { lineY = b.Top; return item.Index; }
            lineY = b.Bottom;
            return item.Index + 1;
        }

        private static List<string> DroppedFolders(IDataObject data)
        {
            var result = new List<string>();
            if (!(data.GetData(DataFormats.FileDrop) is string[] paths)) return result;
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) result.Add(p);
                else if (File.Exists(p)) result.Add(Path.GetDirectoryName(p)); // シナリオファイルを落とした場合はそのフォルダ
            }
            return result;
        }

        private void List_DragOver(object sender, DragEventArgs e)
        {
            bool isRow = e.Data.GetDataPresent(typeof(RowDrag));
            bool isFolder = !isRow && currentPath != null && DroppedFolders(e.Data).Count > 0;
            if (!isRow && !isFolder)
            {
                e.Effect = DragDropEffects.None;
                insertLine.Visible = false;
                return;
            }
            e.Effect = isRow ? DragDropEffects.Move : DragDropEffects.Copy;

            // 端に来たら自動スクロール
            Point p = list.PointToClient(new Point(e.X, e.Y));
            if (list.Items.Count > 0 && list.TopItem != null)
            {
                int top = list.TopItem.Index;
                int rowH = list.Items[top].Bounds.Height;
                if (p.Y < list.Items[top].Bounds.Top + rowH / 2 && top > 0)
                    list.EnsureVisible(top - 1);
                else if (p.Y > list.ClientSize.Height - rowH / 2)
                {
                    int lastVisible = Math.Min(list.Items.Count - 1, top + list.ClientSize.Height / Math.Max(1, rowH));
                    list.EnsureVisible(lastVisible);
                }
            }

            DropIndexAt(new Point(e.X, e.Y), out int y);
            insertLine.SetBounds(0, Math.Max(0, y - 1), list.ClientSize.Width, 2);
            insertLine.Visible = true;
            insertLine.BringToFront();
        }

        private void List_DragDrop(object sender, DragEventArgs e)
        {
            insertLine.Visible = false;
            int index = DropIndexAt(new Point(e.X, e.Y), out _);

            if (e.Data.GetData(typeof(RowDrag)) is RowDrag rows)
            {
                MoveRows(rows.Indices, index);
                return;
            }
            var folders = DroppedFolders(e.Data);
            if (folders.Count > 0 && currentPath != null)
            {
                Activate();
                InsertFolders(folders, index);
            }
        }

        // ================================================================
        //  小物
        // ================================================================

        private int Px(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

        private Button MakeButton(string text, string tip, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                Width = Px(124),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(Px(124), Px(28)),
                Margin = new Padding(0, 0, 0, Px(4)),
                UseVisualStyleBackColor = true,
            };
            b.Click += (s, e) => { onClick(); list.Focus(); };
            toolTip.SetToolTip(b, tip);
            return b;
        }

        private Button MakeButton(string text, string tip, Func<bool> onClick) =>
            MakeButton(text, tip, () => { onClick(); });

        private Control Spacer() => new Panel { Height = Px(8), Width = 1, Margin = new Padding(0) };
    }
}
