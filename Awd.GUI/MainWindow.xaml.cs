using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Awd.Core;

namespace Awd.GUI;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>拖拽启动阈值：系统默认 4px 太灵，双击的两下之间手一抖就变成拖拽、吞掉第二击
    /// （双击打开失效的根因），提到 12px —— 故意拖拽不受影响，双击的手抖不再误触。</summary>
    private const double MinDragDistance = 12.0;

    private const string UnfiledGroup = MapDoc.UnfiledName;   // 与 CLI 共用一份，别两边各写一遍

    private static readonly string[] TabTitles = { "程序", "本地", "网站", "远程" };

    /// <summary>图标提取并发闸：COM 提取不便宜，别让几十块磁贴同时冲进 IconCache。</summary>
    private static readonly SemaphoreSlim IconGate = new(2, 2);

    /// <summary>
    /// 图标槽位的 DIP 尺寸。随图标档位变（见 <see cref="ApplyTileIconScale"/>），
    /// 所以不是 const。默认值取SettingsStore 的「中」档，与 Tokens.xaml 里的初值一致。
    /// </summary>
    private double TileIconDip = SettingsStore.TileScales[SettingsStore.DefaultTileIconScale].Icon;

    /// <summary>分组筛选谓词：null=全部；""=未分类；其他=分组名精确匹配。</summary>
    private static bool GroupMatch(string? group, string? match)
        => match == null || (match == "" ? string.IsNullOrEmpty(group) : group == match);

    /// <summary>侧栏条目：色条 + 名称（可带副行）+ 计数。Match 是筛选/选中语义值：
    /// null=全部，""=未分类，分组名，站点名。</summary>
    private sealed class SideItem
    {
        public SideItem(string label, int count, string? match, string? color,
            bool isSite = false, bool hasMenu = false, string subLabel = "")
        {
            Label = label;
            Count = count;
            Match = match;
            Color = color;
            IsSite = isSite;
            HasMenu = hasMenu;
            SubLabel = subLabel;
        }

        public string Label { get; }
        public int Count { get; }
        public string CountText => $"{Count} 项";
        public string? Match { get; }
        public string? Color { get; }
        public bool IsSite { get; }
        public bool HasMenu { get; }
        public string SubLabel { get; }
        public Visibility SubVisibility => SubLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        public Brush ColorBrush
        {
            get
            {
                try
                {
                    if (!string.IsNullOrEmpty(Color))
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color));
                }
                catch { /* 非法色值退中性 */ }
                return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x59, 0x88, 0x99, 0xAA));
            }
        }
    }

    /// <summary>当前打开的地图：条目、分组、站点配色都在它身上（MapStore 落盘）。
    /// 由 App 在确认"读得出来"之后传进来 —— 别在这里做 IO，构造里抛异常等于启动消失。</summary>
    private MapDoc _doc;
    private string _docPath = "";   // 本窗口只认这一个文件：活动指针被别人改走、或另一张图坏了，都不该牵连我
    private readonly SettingsStore.Settings _settings = SettingsStore.Load();

    private readonly ObservableCollection<TileVm>[] _pages;

    private ICollectionView? _tileView;     // 程序页按分组过滤
    private ICollectionView? _rowView;      // 本地/网站页按分组过滤
    private ICollectionView? _remoteView;   // 远程页按站点过滤
    private string? _tileSideFilter;        // 程序页侧栏选中值
    private string? _sideGroupFilter;       // 本地/网站侧栏选中值
    private string? _remoteSite;            // 当前选中的易远传站点名
    private List<ErfSites.Site> _sites = new();

    private int _tab;
    private bool _sideRebuilding;           // 重建侧栏期间压住 SelectionChanged 的视图刷新
    private IntPtr _hwnd;

    private TileVm? _armedTile;      // 按下左键的条目（拖拽候选）
    private Point _armedPos;
    private FrameworkElement? _dragTile;
    private bool _dragging;
    private bool _justDragged;       // 拖完的那一下 MouseUp 不当点击
    private Image? _ghost;           // 跟随光标的半透明条目副本
    private ListBoxItem? _hotSide;   // 拖拽期间被点亮的侧栏落点

    public MainWindow(MapDoc doc, string path)
    {
        _doc = doc;
        _docPath = path;
        InitializeComponent();
        _pages = new ObservableCollection<TileVm>[4]
        {
            new(), new(), new(), new(),
        };

        _tileView = CollectionViewSource.GetDefaultView(_pages[0]);
        _tileView.Filter = o => o is TileVm t && GroupMatch(t.Entry.Group, _tileSideFilter);
        TileList.ItemsSource = _tileView;

        _rowView = CollectionViewSource.GetDefaultView(_pages[1]);
        _rowView.Filter = o => o is TileVm t && GroupMatch(t.Entry.Group, _sideGroupFilter);
        RowList.ItemsSource = _rowView;

        _remoteView = CollectionViewSource.GetDefaultView(_pages[3]);
        _remoteView.Filter = o => o is TileVm t && SiteOf(t.Entry.Target) == _remoteSite;
        RemoteList.ItemsSource = _remoteView;

        // 拖影与投放区高亮要"穿过"子元素已经 Handled 的 DragOver（列表自己会吃掉冒泡），
        // 所以用 handledEventsToo 挂在根上 —— XAML 写 DragOver= 做不到这一点。
        RootGrid.AddHandler(DragDrop.DragEnterEvent, new DragEventHandler(OnRootDragEnter), true);
        RootGrid.AddHandler(DragDrop.DragOverEvent, new DragEventHandler(OnRootDragOver), true);
        RootGrid.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler(OnRootDragLeave), true);

        LoadSites();
        ApplyTileIconScaleAtStartup();
        ReloadFromStore();
   // 启动时活动地图坏了、临时换了一张 —— 得在状态条上说清楚，别让人以为地图凭空变了
  if (App.StartupNote.Length > 0) SetStatus(App.StartupNote, warn: true);
    }

    /// <summary>
    /// 启动时把图标档位写进资源。必须在 ReloadFromStore 之前 ——
 /// 首次解码要按最终槽位的物理像素来，否则第一屏图标会先按 44 DIP 解一遍再被拉伸。
    /// </summary>
    private void ApplyTileIconScaleAtStartup()
    {
  var (tile, icon, _) = SettingsStore.TileScales[SettingsStore.ClampScale(_settings.TileIconScale)];
        Resources["Tile.Size"] = tile;
        Resources["Tile.Icon"] = icon;
    TileIconDip = icon;
         }

    /// <summary>Win11 的圆角窗口；Win10 无此属性，安静退化直角+描边。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(_hwnd, 33, ref pref, 4);
        }
        catch
        {
            // 非 Win11：保持直角 + 1px 描边的设计兜底
        }
    }

    private ObservableCollection<TileVm> Current => _pages[_tab];

    // ================== 易远传站点（复用 connections.json，只读） ==================

    private void LoadSites()
    {
        _sites = ErfSites.Load();
        SitesHint.Text = _sites.Count > 0
            ? "来自易远传站点配置"
            : "未找到站点 —— 先在易远传里添加";
        RebuildSideList(3);
    }

    private void OnSiteSelected(object sender, SelectionChangedEventArgs e)
    {
        _remoteSite = (SitesList.SelectedItem as SideItem)?.Match;
        if (_sideRebuilding) return;
        _remoteView?.Refresh();
        UpdateCountText();
        if (_remoteSite != null)
            SetStatus($"已选择站点「{_remoteSite}」—— 右侧是它的收藏，上方路径可直达或收藏");
    }

    /// <summary>erf:站点:/路径 → 站点名。</summary>
    private static string? SiteOf(string target)
    {
        if (!target.StartsWith("erf:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = target[4..];
        var i = rest.IndexOf(':');
        return i < 0 ? rest : rest[..i];
    }

    // ================== 数据 ==================

    /// <summary>落盘只写本窗口打开的那一个文件。按 doc.Name 猜路径，会在"文件名与内嵌名分家"时
    /// 写到别处去 —— 刚读回来的那张坏图没被治好，还凭空多出一张。</summary>
    private void SaveDoc() => MapStore.SaveTo(_doc, _docPath);


    private void ReloadFromStore()
    {
        // 对话框（添加清单/设置）可能写过盘，重读一次拿最新 —— 但只重读**自己那个文件**。
        // 这里原先是 OpenActive()：活动地图坏了就把主窗口构造连带炸掉，双击启动的人只看到"没反应"。
        try { _doc = MapStore.LoadFrom(_docPath); }
        catch (Exception ex)
        {
            SetStatus($"地图文件重读失败，先用内存里的内容（改动没丢）：{ex.Message}", warn: true);
        }
        foreach (var page in _pages) page.Clear();
        foreach (var f in _doc.Entries.OrderBy(f => f.Position))
            _pages[(int)f.Page].Add(new TileVm(f));
        RefreshChrome();
        foreach (var page in _pages)
            foreach (var t in page)
                LoadIconAsync(t);
        RebuildMapCombo();
        ReportHealth();
    }

    /// <summary>
    /// 加载后给所有磁贴做一次失效体检，并把结果报到状态栏。
    /// 体检在后台线程跑（Uwp 分支要枚举已安装包，冷启动时不算便宜），完了回 UI 线程应用。
    /// </summary>
    private void ReportHealth()
    {
        var all = _pages.SelectMany(p => p).ToList();
        Task.Run(() =>
        {
            var flags = new bool[all.Count];
            for (int i = 0; i < all.Count; i++) flags[i] = AppHealth.IsMissing(all[i].Entry);
            return flags;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;   // 体检失败不拦启动：条目照常在，只是没标灰
            var flags = t.Result;
            Dispatcher.Invoke(() =>
            {
                int missing = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    all[i].RefreshHealth();
                    if (all[i].IsMissing) missing++;
                }
                // 只有真的有失效项才说话 —— 每次启动都报"全部在位"是噪音
                if (missing > 0)
                    SetStatus($"{missing} 条收藏已失效（图标标灰）—— 右键「重新定位…」换新路径，或「移除收藏」", warn: true);
            });
        });
    }

    // ================== 地图（四页布局的整体，可建多张随时切） ==================

    private bool _mapComboRebuilding;

    private void RebuildMapCombo()
    {
        if (MapCombo == null) return;
        _mapComboRebuilding = true;
        try
        {
            // 读不出的地图照样列出（显示名标注），选它时尝试 Load —— .bak 能救就顺手救活
            MapCombo.ItemsSource = MapStore.Scan()
                .Select(f => new MapOpt(f.Name, f.Error != null ? f.Name + "（读不出）" : f.Name)).ToList();
            MapCombo.SelectedValue = _doc.Name;
        }
        finally
        {
            _mapComboRebuilding = false;
        }
    }

    private sealed record MapOpt(string Name, string Display);

    private void OnMapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_mapComboRebuilding) return;
        if (MapCombo.SelectedValue is not string name) return;
        if (name == _doc.Name) return;
        SwitchMap(name);
    }

    private void SwitchMap(string name)
    {
        try
        {
            _doc = MapStore.Load(name, out var loadedFrom);
            _docPath = loadedFrom;
            // 顺手治愈：主文件坏了但 .bak 完好的地图，Load 已经回退成功 —— 写回**刚读的那个文件**，
            // 下一次切换就把它修好了。按 doc.Name 猜路径不行：文件名与内嵌名分家时会写到别处去。
            MapStore.SaveTo(_doc, loadedFrom);
        }
        catch (Exception ex)
        {
            SetStatus($"切换失败：{ex.Message}", warn: true);
            RebuildMapCombo();
            return;
        }
        var s = SettingsStore.Load();
        s.ActiveMap = name;
        SettingsStore.Save(s);
        ReloadFromStore();
        SetStatus($"已切换到地图「{name}」");
    }

    private void OnMapAddClick(object sender, RoutedEventArgs e)
    {
        var dlg = new NamePromptWindow("新建地图", "地图名称（四页的分组/条目/配色/顺序存成一个文件）：");
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value;
        try
        {
            MapStore.Create(name, null); // 空白地图；要副本用 CLI 的 export/import
        }
        catch (Exception ex)
        {
            SetStatus($"新建失败：{ex.Message}", warn: true);
            return;
        }
        SwitchMap(name);
    }

    /// <summary>按各页视觉顺序重排 Position 并整体落盘（UI 线程专用）。</summary>
    private void Persist()
    {
        var all = new List<AppEntry>();
        for (int p = 0; p < _pages.Length; p++)
        {
            for (int i = 0; i < _pages[p].Count; i++)
            {
                _pages[p][i].Entry.Position = i;
                all.Add(_pages[p][i].Entry);
            }
        }
        _doc.Entries = all;      // 地图二进制按数组先后存顺序
        SaveDoc();
        RefreshChrome();
    }

    private void RefreshChrome()
    {
        UpdateEmptyState();
        UpdateCountText();
        RebuildSideList(_tab);
    }

    /// <summary>空态提示：筛到空分组时右侧不能一片空白（刚建好分组正是最需要引导语的时候）。</summary>
    private void UpdateEmptyState()
    {
        EmptyTiles.Visibility = TileList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tab == 0)
        {
            var tileLabel = _tileSideFilter == null
                ? null
                : (_tileSideFilter.Length == 0 ? UnfiledGroup : _tileSideFilter);
            EmptyTiles.Text = tileLabel != null
                ? $"「{tileLabel}」还没有收藏 —— 把条目拖到侧栏可归组"
                : "收藏还是空的 —— 点上方「从应用清单添加」";
        }

        if (_tab is 1 or 2)
        {
            EmptyRows.Visibility = RowList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var label = _sideGroupFilter == null
                ? null
                : (_sideGroupFilter.Length == 0 ? UnfiledGroup : _sideGroupFilter);
            EmptyRows.Text = label != null
                ? $"「{label}」还没有收藏"
                : _tab == 1
                    ? "收藏还是空的 —— 点上方「选择文件夹收藏…」"
                    : "收藏还是空的 —— 在上方输入网址，或点「导入书签…」";
        }
        if (_tab == 3)
        {
            EmptyRemote.Visibility = RemoteList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyRemote.Text = _remoteSite == null
                ? "未找到易远传站点 —— 先在易远传里添加站点"
                : $"「{_remoteSite}」还没有收藏 —— 上方输入路径可直达，点＋收藏";
        }
    }

    private void UpdateCountText()
    {
        var count = _tab switch
        {
            0 => TileList.Items.Count,
            3 => RemoteList.Items.Count,
            _ => RowList.Items.Count,   // 按当前侧栏筛选后的可见数
        };
        CountText.Text = $"{TabTitles[_tab]} {count} 项";
    }

    /// <summary>图标缓存落好后补一笔：改内存里这张地图再整体落盘（在 UI 线程上，await 回来还在调度器里）。
    /// 原先每次提取都重新 OpenActive() —— 那等于"扫一遍 maps 目录 + 读全图 + 写全图 + 写 bak"，
    /// 装机首启几十个图标就是几十轮全量读写，纯属白花。</summary>
    private void PersistEntry(AppEntry entry)
    {
        var idx = _doc.Entries.FindIndex(f => f.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _doc.Entries[idx] = entry;
        SaveDoc();
    }

    private async void LoadIconAsync(TileVm tile)
    {
        try
        {
            //按 DPI 换算物理像素：槽位 44 DIP 在 175% 下要 77 物理像素。
            // 只解码 44px 会把124px 的缓存源图降采样掉细节，再让 WPF 拉伸回 77 —— 必糊。
            var px = DpiScale.ToPixels(TileIconDip);
            await tile.LoadIconAsync(IconGate, PersistEntry, px);
        }
        catch (Exception ex)
        {
            SetStatus($"图标提取失败：{tile.Entry.Name} — {ex.Message}", warn: true);
        }
    }

    // ================== 标签页 ==================

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间 Tab0 的 IsChecked 就会触发一次，此时界面还没连完
        if (TileList == null || ListPage == null || RemotePage == null) return;
        _tab = sender == Tab1 ? 1 : sender == Tab2 ? 2 : sender == Tab3 ? 3 : 0;

        TilePage.Visibility = _tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListPage.Visibility = _tab is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        RemotePage.Visibility = _tab == 3 ? Visibility.Visible : Visibility.Collapsed;
        AddLocal.Visibility = _tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        AddWeb.Visibility = _tab == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (_tab is 1 or 2)
            RowList.ItemsSource = _rowView = CollectionViewSource.GetDefaultView(_pages[_tab]);

        RefreshChrome();
    }

    // ================== 启动 ==================

    private async Task LaunchAsync(AppEntry entry)
    {
        SetStatus($"启动 {entry.Name} …");
        try
        {
            var r = await Task.Run(() => AppLauncher.Launch(entry));
            SetStatus($"已启动 {entry.Name}（{r.Method}；PID {r.ProcessId?.ToString() ?? "未取得"}）");
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败：{ex.Message}", warn: true);
        }
    }

    // ================== 拖拽公共 ==================

    private DragDropEffects BeginDrag(object sender, MouseEventArgs e, TileVm vm)
    {
        _dragging = true;
        _dragTile = (FrameworkElement)sender;
        ShowGhost(_dragTile);          // 先抓图（此时原件还没压暗），再压暗原件
        ShowDropZones();
        _dragTile.Opacity = 0.55;
        try
        {
            return DragDrop.DoDragDrop((DependencyObject)sender,
                new DataObject("awd-entry", vm.Entry.Id), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _justDragged = true; // DoDragDrop 返回后 MouseUp 不再当启动
            if (_dragTile != null) _dragTile.Opacity = 1;
            _armedTile = null;
            HideGhost();
            HideDropZones();
            SetSideHot(null, null);   // 拖拽结束：侧栏落点一定熄灯，别留一块假蓝
        }
    }

    // ── 拖影：把"手里的条目"画成半透明副本跟着光标走（直接操纵，不用猜） ──

    private void ShowGhost(FrameworkElement src)
    {
        HideGhost();
        if (src.ActualWidth <= 0 || src.ActualHeight <= 0) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(src);
            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(src.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(src.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(src);
            _ghost = new Image
            {
                Source = rtb,
                Width = src.ActualWidth,
                Height = src.ActualHeight,
                Opacity = 0.9,
                IsHitTestVisible = false,
            };
            DragLayer.Children.Add(_ghost);
        }
        catch
        {
            _ghost = null;   // 拖影是锦上添花，抓图失败不能挡住拖拽本身
        }
    }

    private void MoveGhost(DragEventArgs e)
    {
        if (_ghost == null) return;
        var p = e.GetPosition(DragLayer);
        Canvas.SetLeft(_ghost, p.X - _ghost.Width / 2);
        Canvas.SetTop(_ghost, p.Y - _ghost.Height / 2);
    }

    private void HideGhost()
    {
        if (_ghost == null) return;
        DragLayer.Children.Remove(_ghost);
        _ghost = null;
    }

    // ── 投放区：拖拽时顶掉"标签条 + 输入框"，只有两个动作，不再有"拖出窗口"这种要猜的语义 ──

    private void ShowDropZones()
    {
        bool canUnfile = InNamedGroup();   // 全部 / 未分类 视图下"脱离分类"没有意义，不给它留位置
        ZoneUnfile.Visibility = canUnfile ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(ZoneDelete, canUnfile ? 1 : 0);
        Grid.SetColumnSpan(ZoneDelete, canUnfile ? 1 : 2);
        SetZoneHot(ZoneUnfile, false);
        SetZoneHot(ZoneDelete, false);
        var header = RootGrid.RowDefinitions[0].ActualHeight;
        if (header <= 0) header = (double)FindResource("Header.Height");   // 布局还没跑完时的兜底
        DropOverlay.Height = header + AddBarHeight();
        DropOverlay.Visibility = Visibility.Visible;
        DropOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, (Duration)FindResource("Anim.Fast")));
    }

    /// <summary>当前页顶部输入栏的高度 —— 投放区要严丝合缝顶掉它，不能露边。</summary>
    private double AddBarHeight() => _tab switch
    {
        0 => AddBarTile.ActualHeight,
        3 => AddBarRemote.ActualHeight,
        _ => AddBarList.ActualHeight,
    };

    private void HideDropZones()
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        SetZoneHot(ZoneUnfile, false);
        SetZoneHot(ZoneDelete, false);
    }

    private void SetZoneHot(Border zone, bool hot)
    {
        var key = ReferenceEquals(zone, ZoneDelete)
            ? (hot ? "Brush.Drop.Delete" : "Brush.Surface")
            : (hot ? "Brush.Drop.Unfile" : "Brush.Surface");
        zone.Background = (Brush)FindResource(key);
    }

    private static bool OverZone(DragEventArgs e, Border zone)
    {
        if (zone.Visibility != Visibility.Visible) return false;
        var p = e.GetPosition(zone);
        return p.X >= 0 && p.Y >= 0 && p.X <= zone.ActualWidth && p.Y <= zone.ActualHeight;
    }

    private void OnRootDragEnter(object sender, DragEventArgs e)
    {
        if (_ghost != null) _ghost.Visibility = Visibility.Visible;
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        bool carrying = e.Data.GetDataPresent("awd-entry");
        // 子元素判过落点（OnListDragOver / OnSideDragOver 会 Handled）就不要覆盖它给的 Effects ——
        // 侧栏要说"这里不能放"靠的就是 Effects=None；没有主人的地方（顶部投放区）才由这层给 Move。
        if (!e.Handled) e.Effects = carrying ? DragDropEffects.Move : DragDropEffects.None;
        if (!carrying) return;
        MoveGhost(e);
        SetZoneHot(ZoneUnfile, OverZone(e, ZoneUnfile));
        SetZoneHot(ZoneDelete, OverZone(e, ZoneDelete));
    }

    private void OnRootDragLeave(object sender, DragEventArgs e)
    {
        // 光标出了窗口：没有落点了，拖影先收起来；回到窗口内自动恢复
        if (_ghost != null) _ghost.Visibility = Visibility.Collapsed;
        SetZoneHot(ZoneUnfile, false);
        SetZoneHot(ZoneDelete, false);
    }

    private void OnDropUnfile(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("awd-entry") is not string id) return;
        e.Handled = true;
        if (FindVm(id) is { } vm) UnfileEntry(vm);
    }

    private void OnDropDelete(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("awd-entry") is not string id) return;
        e.Handled = true;
        if (FindVm(id) is { } vm) DeleteEntry(vm);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("awd-entry") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>当前是否筛在某个具名分组里 —— 只有这时"脱离分类"才有意义。
    /// （全部 / 未分类 视图下不给这个投放区：用户点名"全部那里不能有脱离分类的区域"。）</summary>
    private bool InNamedGroup() => _tab switch
    {
        0 => !string.IsNullOrEmpty(_tileSideFilter),
        1 or 2 => !string.IsNullOrEmpty(_sideGroupFilter),
        _ => false,
    };

    /// <summary>当前页集合里按 Id 找条目（拖拽落点用）。</summary>
    private TileVm? FindVm(string id) =>
        _pages[_tab].FirstOrDefault(v => v.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private void UnfileEntry(TileVm vm)
    {
        vm.Entry.Group = null;
        Persist();
        ReloadFromStore();
        SetStatus($"已把「{vm.Entry.Name}」移出分组（回到未分类）");
    }

    private void DeleteEntry(TileVm vm)
    {
        _pages[_tab].Remove(vm);
        Persist();
        SetStatus($"已移除「{vm.Entry.Name}」");
    }

    // ================== 磁贴（程序页） ==================

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _justDragged = false;
        _armedTile = (TileVm)((Border)sender).DataContext;
        _armedPos = e.GetPosition(null);
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _armedTile == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _armedPos.X) < MinDragDistance &&
            Math.Abs(pos.Y - _armedPos.Y) < MinDragDistance) return;

        BeginDrag(sender, e, _armedTile);
    }

    private async void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _justDragged) return;
        if (_armedTile is { } vm && ((Border)sender).DataContext == vm)
        {
            // 打开方式是全局设置：单击直开（手机式）或双击开（桌面习惯，单击只看目标）
            if (!_settings.DoubleClickOpen)
                await LaunchAsync(vm.Entry);
            else if (e.ClickCount >= 2)
                await LaunchAsync(vm.Entry);
            else
                SetStatus($"{vm.Entry.Name}  ←  {vm.Entry.Target}");
        }
        _armedTile = null;
    }

    private void OnTileListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("awd-entry") is not string id) return;
        var visible = TileList.Items.Cast<TileVm>().ToList();
        int vi = visible.FindIndex(v => v.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (vi < 0) return;
        ApplyReorder(_pages[0], visible, vi, ComputeGridInsertIndex(e.GetPosition(TileList), visible.Count));
    }

    /// <summary>网格插入位 = 按行优先顺序数"中心点在鼠标之前"的磁贴数（手机网格的落点语义）。</summary>
    private int ComputeGridInsertIndex(Point p, int visibleCount)
    {
        int idx = 0;
        for (int i = 0; i < visibleCount; i++)
        {
            if (TileList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
            var half = el.ActualHeight / 2;
            var c = el.TransformToAncestor(TileList)
                      .Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
            bool before = c.Y < p.Y - half ||
                          (Math.Abs(c.Y - p.Y) <= half && c.X < p.X);
            if (before) idx++;
        }
        return idx;
    }

    /// <summary>把"可见序列里的 from 位挪到 insertVi 位"落到集合上（过滤视图下自动映射到真实索引）。</summary>
    private void ApplyReorder(ObservableCollection<TileVm> page, List<TileVm> visible, int vi, int insertVi)
    {
        if (insertVi == vi || insertVi == vi + 1) return; // 没挪
        var vm = visible[vi];
        int from = page.IndexOf(vm);
        if (from < 0) return;
        page.RemoveAt(from);
        int targetIdx;
        if (insertVi >= visible.Count)
            targetIdx = page.IndexOf(visible[^1]) + 1;      // 挪到可见末尾之后
        else
        {
            targetIdx = page.IndexOf(visible[insertVi]);
            if (targetIdx > from) targetIdx--;               // 移除位移修正
        }
        targetIdx = Math.Clamp(targetIdx, 0, page.Count);
        page.Insert(targetIdx, vm);
        Persist();
        SetStatus($"已把 {vm.Entry.Name} 摆到第 {Math.Clamp(insertVi > vi ? insertVi : insertVi + 1, 1, Math.Max(page.Count, 1))} 位");
    }

    // ================== 列表行（本地/网站/远程）：单击看目标，双击启动，拖拽换位 ==================

    private async void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((Border)sender).DataContext is not TileVm vm) return;
        _justDragged = false;
        _armedTile = vm;
        _armedPos = e.GetPosition(null);
        if (e.ClickCount == 2)
        {
            if (!_settings.DoubleClickOpen) return; // 单击模式下第一下已开，第二下不再补刀
            await LaunchAsync(vm.Entry);
        }
        else if (e.ClickCount == 1)
        {
            if (_settings.DoubleClickOpen)
                SetStatus($"{vm.Entry.Name}  ←  {vm.Entry.Target}");
            else
                await LaunchAsync(vm.Entry);
        }
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _armedTile == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.Y - _armedPos.Y) < MinDragDistance &&
            Math.Abs(pos.X - _armedPos.X) < MinDragDistance) return;

        BeginDrag(sender, e, _armedTile);
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox box) return;
        if (e.Data.GetData("awd-entry") is not string id) return;
        var visible = box.Items.Cast<TileVm>().ToList();
        int vi = visible.FindIndex(v => v.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (vi < 0) return;

        int insertVi = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            if (box.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
            var half = el.ActualHeight / 2;
            var c = el.TransformToAncestor(box).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
            if (c.Y < e.GetPosition(box).Y - half) insertVi++;
        }
        ApplyReorder(_pages[_tab], visible, vi, insertVi);
    }

    // ================== 侧栏（分组/站点）：选中切换、拖入归组、右键管理 ==================

    private ListBox? SideBoxFor(int page) => page switch
    {
        0 => TileSideList,
        3 => SitesList,
        _ => SideList,
    };

    private void RebuildSideList(int page)
    {
        var box = SideBoxFor(page);
        if (box == null) return;
        _sideRebuilding = true;
        List<SideItem> items;
        try
        {
            if (page == 3)
            {
                items = _sites.Select(s => new SideItem(
                    s.Name,
                    _pages[3].Count(t => SiteOf(t.Entry.Target) == s.Name),
                    s.Name,
                    _doc.SiteColors != null && _doc.SiteColors.TryGetValue(s.Name, out var c) ? c : null,
                    isSite: true,
                    subLabel: $"{s.Type}://{s.Host}")).ToList();
            }
            else
            {
                var src = _pages[page];
                var registry = _doc.Groups.Where(g => g.Page == page).ToList();
                var dataNames = src.Select(t => t.Entry.Group)
                    .Where(g => !string.IsNullOrEmpty(g)).Select(g => g!).Distinct().ToList();
                var names = registry.Select(g => g.Name).Concat(dataNames).Distinct().ToList();
                items = new List<SideItem> { new("全部", src.Count, null, null) };
                items.AddRange(names.Select(n => new SideItem(
                    n,
                    src.Count(t => t.Entry.Group == n),
                    n,
                    registry.FirstOrDefault(g => g.Name == n)?.Color,
                    hasMenu: true)));
                items.Add(new SideItem(UnfiledGroup, src.Count(t => string.IsNullOrEmpty(t.Entry.Group)), "", null));
            }
            box.ItemsSource = items;

            string? cur = page switch { 0 => _tileSideFilter, 3 => _remoteSite, _ => _sideGroupFilter };
            var sel = items.FirstOrDefault(i => i.Match == cur);
            if (sel == null)
            {
                sel = items[0]; // 原筛选随数据消失，回"全部"或第一个站点
                if (page == 0) _tileSideFilter = null;
                else if (page == 3) _remoteSite = sel.Match;
                else _sideGroupFilter = null;
            }
            box.SelectedItem = sel;
        }
        finally
        {
            _sideRebuilding = false;
        }
        switch (page)
        {
            case 0: _tileView?.Refresh(); break;
            case 3: _remoteView?.Refresh(); break;
            default: _rowView?.Refresh(); break;
        }
    }

    private void OnTileSideSelected(object sender, SelectionChangedEventArgs e)
    {
        _tileSideFilter = (TileSideList.SelectedItem as SideItem)?.Match;
        if (_sideRebuilding) return;
        _tileView?.Refresh();
        UpdateEmptyState();
        UpdateCountText();
    }

    private void OnSideSelected(object sender, SelectionChangedEventArgs e)
    {
        _sideGroupFilter = (SideList.SelectedItem as SideItem)?.Match;
        if (_sideRebuilding) return;
        _rowView?.Refresh();
        UpdateEmptyState();
        UpdateCountText();
    }

    private void OnSideAddClick(object sender, RoutedEventArgs e)
    {
        int page = _tab is 0 or 1 or 2 ? _tab : 0; // 按钮只在其页可见
        var dlg = new NamePromptWindow("新建分组", "分组名称：");
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value;
        if (name == UnfiledGroup) { SetStatus("这个名字留给了未分类", warn: true); return; }
        if (_doc.Groups.Any(g => g.Page == page && g.Name == name) ||
            _pages[page].Any(t => t.Entry.Group == name))
        {
            SetStatus($"分组「{name}」已存在", warn: true);
            return;
        }
        _doc.Groups.Add(new GroupDef { Page = page, Name = name });
        SaveDoc();
        if (page == 0) _tileSideFilter = name;
        else _sideGroupFilter = name;
        RebuildSideList(page);
        SetStatus($"已新建分组「{name}」—— 把条目拖到侧栏即可归组");
    }

    /// <summary>光标下的侧栏条目容器（不在侧栏里返回 null）。高亮与落点都走它，保证同源。</summary>
    private static ListBoxItem? SideContainerUnder(ListBox box, object? originalSource)
        => ItemsControl.ContainerFromElement(box, originalSource as DependencyObject) as ListBoxItem;

    /// <summary>点亮/熄灭侧栏落点。DragOver 是鼠标频率，只在落点真的换了时才写状态条。</summary>
    private void SetSideHot(ListBoxItem? item, string? hint)
    {
        if (ReferenceEquals(_hotSide, item)) return;
        if (_hotSide != null) SideDropTarget.SetIsHot(_hotSide, false);
        _hotSide = item;
        if (item != null) SideDropTarget.SetIsHot(item, true);
        if (hint != null) SetStatus(hint);
    }

    /// <summary>拖到侧栏上方的反馈：可归的那一项亮起来（与顶部投放区同一套色），
    /// 不可归的不亮 + 光标禁止 + 状态条说清为什么 —— 顶部会亮、侧栏不亮，就是"侧栏不吃拖拽"的观感来源。</summary>
    private void OnSideDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox box) return;
        bool carrying = e.Data.GetDataPresent("awd-entry");
        var container = SideContainerUnder(box, e.OriginalSource);
        var item = container?.DataContext as SideItem;

        // 只有具名分组和「(未分类)」是落点：「全部」是视图不是分组，空白处没有落点
        bool droppable = carrying && item?.Match != null;
        e.Effects = droppable ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (!carrying) return;

        string hint;
        if (item == null) hint = "这里没有分组 —— 把条目拖到某个分组名上归组";
        else if (item.Match == null) hint = "「全部」是所有分组的并集，归不进去 —— 拖到具体分组名上";
        else if (item.Match.Length == 0) hint = $"放到「{item.Label}」= 把它移出所有分组";
        else hint = $"放到「{item.Label}」= 归入该分组";
        SetSideHot(droppable ? container : null, hint);
    }

    private void OnSideDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not ListBox box) return;
        // 在列表内部子元素之间游走也会抛 Leave（事件冒泡过来），先确认真的出了边界
        var p = e.GetPosition(box);
        if (p.X >= 0 && p.Y >= 0 && p.X <= box.ActualWidth && p.Y <= box.ActualHeight) return;
        SetSideHot(null, null);
    }

    /// <summary>拖条目到侧栏 = 归入该分组（拖到"未分类"= 脱离）。判定链每一步走不下去都要出声。</summary>
    private void OnSideDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox box) return;
        SetSideHot(null, null);
        if (e.Data.GetData("awd-entry") is not string id) return;
        e.Handled = true;

        var item = SideContainerUnder(box, e.OriginalSource)?.DataContext as SideItem;
        if (item == null)
        {
            SetStatus("没落在分组上 —— 要归组得拖到分组名上", warn: true);
            return;
        }
        if (item.Match == null)
        {
            SetStatus("「全部」不是分组，归不进去 —— 拖到具体分组名上", warn: true);
            return;
        }

        int page = box == TileSideList ? 0 : _tab;
        var vm = _pages[page].FirstOrDefault(t => t.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (vm == null)
        {
            SetStatus($"没在本页找到这条收藏，未做归组（{item.Label}）", warn: true);
            return;
        }

        var target = item.Match.Length == 0 ? null : item.Match;
        if (vm.Entry.Group == target)
        {
            // 本来就在这组里：世界确实不会变，但要说明为什么不变，别让人以为没反应
            SetStatus($"「{vm.Entry.Name}」本来就在「{item.Label}」里");
            return;
        }
        vm.Entry.Group = target;
        Persist();
        ReloadFromStore();
        SetStatus($"已把「{vm.Entry.Name}」移到「{item.Label}」");
    }

    /// <summary>侧栏右键：分组=重命名/设置颜色/删除；站点=设置颜色；全部/未分类=无菜单。</summary>
    private void OnSideRightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox box) return;
        if (((FrameworkElement)e.OriginalSource).DataContext is not SideItem item) return;
        int page = box == TileSideList ? 0 : box == SitesList ? 3 : _tab;
        if (item.Match == null || (!item.IsSite && item.Match == "")) return; // 全部/未分类

        var menu = new ContextMenu();
        if (!item.IsSite)
        {
            var rename = new MenuItem { Header = "重命名" };
            rename.Click += (_, _) => RenameGroup(page, item.Label);
            menu.Items.Add(rename);
        }

        var color = new MenuItem { Header = "设置颜色" };
        foreach (var s in GroupPalette.Swatches)   // 预设与 CLI 的 --color 共用 Core 里那一份
        {
            var mi = new MenuItem { Header = $"■ {s.Zh} {s.En}" };
            mi.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Hex));
            mi.Click += (_, _) => ApplySideColor(page, item.Match!, s.Hex);
            color.Items.Add(mi);
        }
        var reset = new MenuItem { Header = "■ 默认" };
        reset.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA));
        reset.Click += (_, _) => ApplySideColor(page, item.Match!, null);
        color.Items.Add(reset);
        menu.Items.Add(color);

        if (!item.IsSite)
        {
            var del = new MenuItem { Header = "删除分组" };
            del.Click += (_, _) => DeleteGroup(page, item.Label);
            menu.Items.Add(del);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RenameGroup(int page, string oldName)
    {
        var dlg = new NamePromptWindow("重命名分组", "新的分组名称：", oldName);
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value;
        if (name == oldName) return;
        if (_doc.Groups.Any(g => g.Page == page && g.Name == name))
        {
            SetStatus($"分组「{name}」已存在", warn: true);
            return;
        }
        foreach (var g in _doc.Groups.Where(g => g.Page == page && g.Name == oldName))
            g.Name = name;
        foreach (var t in _pages[page].Where(t => t.Entry.Group == oldName))
            t.Entry.Group = name;
        SaveDoc();
        Persist();
        ReloadFromStore();
        SetStatus($"分组已改名为「{name}」");
    }

    private void DeleteGroup(int page, string name)
    {
        _doc.Groups.RemoveAll(g => g.Page == page && g.Name == name);
        SaveDoc();
        foreach (var t in _pages[page].Where(t => t.Entry.Group == name))
            t.Entry.Group = null; // 条目不删，回未分类
        Persist();
        ReloadFromStore();
        SetStatus($"已删除分组「{name}」，里面的条目回到未分类");
    }

    private void ApplySideColor(int page, string match, string? hex)
    {
        if (page == 3)
        {
            if (hex == null) _doc.SiteColors.Remove(match);
            else _doc.SiteColors[match] = hex;
            SaveDoc();
        }
        else
        {
            var def = _doc.Groups.FirstOrDefault(g => g.Page == page && g.Name == match);
            if (def == null)
            {
                def = new GroupDef { Page = page, Name = match, Color = hex };
                _doc.Groups.Add(def);
            }
            else
            {
                def.Color = hex;
            }
            SaveDoc();
        }
        RebuildSideList(page);
    }

    // ================== 右键菜单（条目，两形态共用） ==================

    private TileVm? MenuTile(object sender)
    {
        // ContextMenu 是继承上下文：菜单项的 DataContext 就是条目的 TileVm
        return (sender as FrameworkElement)?.DataContext as TileVm;
    }

    private ObservableCollection<TileVm>? PageOf(TileVm vm) =>
        _pages.FirstOrDefault(c => c.Contains(vm));

    /// <summary>条目所在页的"可见顺序"：任何过滤（分组/站点）都只数看得见的。</summary>
    private List<TileVm> VisibleSeqOf(ObservableCollection<TileVm> page)
    {
        if (page == _pages[0]) return TileList.Items.Cast<TileVm>().ToList();
        if (page == _pages[3]) return RemoteList.Items.Cast<TileVm>().ToList();
        return RowList.Items.Cast<TileVm>().ToList();
    }

    private async void OnMenuLaunch(object sender, RoutedEventArgs e)
    {
        if (MenuTile(sender) is { } vm) await LaunchAsync(vm.Entry);
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        if (MenuTile(sender) is not { } vm || PageOf(vm) is not { } page) return;
        int p = Array.IndexOf(_pages, page);
        var groups = _doc.Groups.Where(g => g.Page == p).Select(g => g.Name)
            .Concat(_pages[p].Select(t => t.Entry.Group).Where(g => !string.IsNullOrEmpty(g)).Select(g => g!))
            .Concat(vm.Entry.Group != null ? new[] { vm.Entry.Group } : Array.Empty<string>())
            .Distinct();
        var dlg = new EditEntryWindow(vm.Entry,
            id => page.Any(t => t != vm && t.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)),
            groups);
        dlg.ShowDialog();
        if (!dlg.Changed) return;
        Persist();
        ReloadFromStore();
        SetStatus($"已保存对「{vm.Entry.Name}」的修改");
    }

    /// <summary>
    /// 重新定位失效的收藏：程序被挪了位置、卸载重装换了路径、或者换了机器 ——
    /// 收藏里存的绝对路径就过期了。这里让用户指个新的 exe/文件夹，保留原名与分组，只换 Target。
    /// UWP 不走这条（卸载了就是卸载了，重装会拿到新的 AUMID，该重新添加而不是改路径）。
    /// </summary>
    private void OnMenuRelocate(object sender, RoutedEventArgs e)
    {
        if (MenuTile(sender) is not { } vm) return;
        var entry = vm.Entry;

        string picked;
        if (entry.Kind == AppKind.Folder)
        {
            var fdlg = new Microsoft.Win32.OpenFolderDialog { Title = $"为「{entry.Name}」选新的位置" };
            if (fdlg.ShowDialog(this) != true) return;
            picked = fdlg.FolderName;
        }
        else if (entry.Kind is AppKind.Exe or AppKind.Lnk)
        {
            var odlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"为「{entry.Name}」选新的程序文件",
                Filter = "程序与快捷方式|*.exe;*.lnk|程序|*.exe|快捷方式|*.lnk|所有文件|*.*",
            };
            if (odlg.ShowDialog(this) != true) return;
            picked = odlg.FileName;
        }
        else
        {
            SetStatus("商店应用失效没法「改路径」—— 重新装上后从应用清单里再添加一次", warn: true);
            return;
        }

        // 新位置已经被别的收藏占了：两条同 Target 会让"移除一条另一条也坏"
        if (_pages[(int)entry.Page].Any(t => t != vm &&
                t.Entry.Target.Equals(picked, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("那个目标已经在本页收藏里了 —— 直接删掉这条旧的即可", warn: true);
            return;
        }

        var old = entry.Target;
        entry.Target = picked;
        entry.Id = AppEntry.MakeId(picked);   // id 是 target 的函数，换目标必须重算
        entry.IconPath = null;                // 旧图标是旧目标的，别留着张对不上的脸
        vm.RefreshHealth();
        Persist();
        ReloadFromStore();
        SetStatus($"「{entry.Name}」已重新定位到 {picked}");
    }

    private void OnMenuMoveUp(object sender, RoutedEventArgs e) => ShiftTile(MenuTile(sender), -1);
    private void OnMenuMoveDown(object sender, RoutedEventArgs e) => ShiftTile(MenuTile(sender), +1);

    /// <summary>在可见序列里与相邻项换位（过滤视图下只与"看得见的邻居"换）。</summary>
    private void ShiftTile(TileVm? vm, int delta)
    {
        if (vm == null || PageOf(vm) is not { } page) return;
        var visible = VisibleSeqOf(page);
        int vi = visible.IndexOf(vm);
        int ni = vi + delta;
        if (vi < 0 || ni < 0 || ni >= visible.Count) return;
        page.Move(page.IndexOf(vm), page.IndexOf(visible[ni]));
        Persist();
    }

    private void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        if (MenuTile(sender) is not { } vm || PageOf(vm) is not { } page) return;
        page.Remove(vm);
        Persist();
        SetStatus($"已移除 {vm.Entry.Name}");
    }

    // ================== 添加（输入框在页面顶部） ==================

    private void OnAddFromInventory(object sender, RoutedEventArgs e)
    {
        var dlg = new AddAppsWindow(_doc, _docPath) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Changed)
        {
            ReloadFromStore();
            SetStatus("收藏已更新");
        }
    }

    /// <summary>直接选 exe 入收藏（便携程序没有开始菜单 lnk，清单里枚举不到）。可多选。</summary>
    private void OnAddExeFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要收藏的 exe（可多选；便携程序直接选文件）",
            Filter = "程序|*.exe|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        // 工作目录留空 = 启动时默认 exe 所在目录
        var added = AddEntries(PageKind.Programs, dlg.FileNames.Select(p => new AppEntry
        {
            Name = Path.GetFileNameWithoutExtension(p),
            Kind = AppKind.Exe,
            Target = p,
        }));
        ReportAdded(added, "个 exe");
    }

    /// <summary>扫整个文件夹：里面的 exe 与 .lnk 快捷方式一次性收进来（多选对话框办不到的量）。</summary>
    private void OnAddExeFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要扫描的文件夹（会收里面的 exe 与快捷方式，含子目录）",
        };
        if (dlg.ShowDialog(this) != true) return;

        FolderScan.Result scan;
        try
        {
            scan = Task.Run(() => FolderScan.Scan(dlg.FolderName)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SetStatus($"扫描失败：{ex.Message}", warn: true);
            return;
        }

        if (scan.Entries.Count == 0)
        {
            SetStatus($"「{dlg.FolderName}」里没有找到 exe 或快捷方式（共看了 {scan.ScannedFiles} 个文件）", warn: true);
            return;
        }

        // 数量大先报个数再问一句：扫到 300 个 exe 很可能指错目录了，不该闷头全塞进来
        if (scan.Entries.Count >= 20)
        {
            var tail = scan.Truncated
                ? $"\n\n（达到 {FolderScan.MaxResults} 项上限，只扫到前 {FolderScan.MaxResults} 项）"
                : "";
            if (MessageBox.Show(this,
                    $"扫到 {scan.Entries.Count} 个可执行程序 / 快捷方式，全部加入程序页？{tail}",
                    "确认批量导入", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                SetStatus("已取消导入");
                return;
            }
        }

        var added = AddEntries(PageKind.Programs, scan.Entries);
        ReportAdded(added, "个程序");
    }

    /// <summary>
    /// 批量入库。按 Target 逐条查重（已收藏的跳过），返回 (新增, 已存在) 计数。
    /// 入参的 Kind / Arguments / WorkingDir 原样带过去 —— 文件夹里扫出来的 .lnk
    /// 必须保持 Lnk 类型和它自己的参数、工作目录，否则启动通道会走错。
    /// </summary>
    private (int Added, int Existed) AddEntries(PageKind page, IEnumerable<AppEntry> items)
    {
        int p = (int)page;
        var pending = new List<AppEntry>();
        int existed = 0;

        foreach (var src in items)
        {
            if (_pages[p].Any(t => t.Entry.Target.Equals(src.Target, StringComparison.OrdinalIgnoreCase)))
            {
                existed++;
                continue;
            }
            pending.Add(new AppEntry
            {
                Id = AppEntry.MakeId(src.Target),
                Name = src.Name,
                Kind = src.Kind,
                Page = page,
                Target = src.Target,
                Arguments = src.Arguments,
                WorkingDir = src.WorkingDir,
                Position = _pages[p].Count + pending.Count,
            });
        }

        if (pending.Count == 0) return (0, existed);

        foreach (var entry in pending)
        {
            var vm = new TileVm(entry);
            _pages[p].Add(vm);
            LoadIconAsync(vm);
        }
        Persist();
        return (pending.Count, existed);
    }

    private void ReportAdded((int Added, int Existed) r, string unit)
    {
        if (r.Added == 0)
        {
            SetStatus($"没有新增 —— {r.Existed} 个已在收藏中", warn: true);
            return;
        }
        var extra = r.Existed > 0 ? $"，跳过 {r.Existed} 个已收藏" : "";
        SetStatus($"已收藏 {r.Added} {unit}{extra}");
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要收藏的文件夹" };
        if (dlg.ShowDialog(this) != true) return;
        var name = Path.GetFileName(dlg.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = dlg.FolderName;
        AddEntry(PageKind.Local, AppKind.Folder, dlg.FolderName, name); // 默认未分类，拖到侧栏或编辑可归组
    }

    private void OnAddWeb(object sender, RoutedEventArgs e)
    {
        var target = WebUrl.Text.Trim();
        if (target.Length == 0) { SetStatus("网址是空的", warn: true); return; }
        if (!target.Contains("://")) target = "https://" + target;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            SetStatus($"网址无效：{WebUrl.Text.Trim()}", warn: true);
            return;
        }
        AddEntry(PageKind.Web, AppKind.Web, target, uri.Host); // 名称默认取域名，右键「编辑…」可改
        WebUrl.Clear();
    }

    private void OnAddRemote(object sender, RoutedEventArgs e) => AddRemoteFavorite();

    private void OnOpenRemoteDirect(object sender, RoutedEventArgs e) => OpenRemoteDirect();

    private void OnWebBoxEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAddWeb(sender, e);
        e.Handled = true;
    }

    private void OnRemoteBoxEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenRemoteDirect(); // 远程页回车 = 直达（不收藏）
        e.Handled = true;
    }

    /// <summary>当前输入的远程路径拼成 erf: 地址；站点未选或路径为空返回 null。</summary>
    private string? BuildRemoteTarget()
    {
        if (_remoteSite == null)
        {
            SetStatus("左侧没有可选站点 —— 先在易远传里添加站点", warn: true);
            return null;
        }
        var path = RemotePath.Text.Trim();
        if (path.Length == 0) { SetStatus("路径是空的", warn: true); return null; }
        if (!path.StartsWith('/')) path = "/" + path; // erf: 要求绝对路径
        return $"erf:{_remoteSite}:{path}";
    }

    /// <summary>直达：不落库，立刻打开当前输入的远程路径。</summary>
    private async void OpenRemoteDirect()
    {
        var target = BuildRemoteTarget();
        if (target == null) return;
        var seg = RemotePath.Text.Trim().TrimEnd('/');
        var name = seg.Length > 0 ? seg[(seg.LastIndexOf('/') + 1)..] : _remoteSite!;
        await LaunchAsync(new AppEntry
        {
            Id = AppEntry.MakeId(target),
            Name = name,
            Kind = AppKind.Remote,
            Target = target,
        });
    }

    private void AddRemoteFavorite()
    {
        var target = BuildRemoteTarget();
        if (target == null) return;
        var seg = RemotePath.Text.Trim().TrimEnd('/');
        var name = seg.Length > 0 ? seg[(seg.LastIndexOf('/') + 1)..] : _remoteSite!;
        AddEntry(PageKind.Remote, AppKind.Remote, target, name);
        RemotePath.Clear();
    }

    private void OnImportBookmarks(object sender, RoutedEventArgs e)
    {
        var existing = new HashSet<string>(
            _pages[(int)PageKind.Web].Select(t => t.Entry.Target),
            StringComparer.OrdinalIgnoreCase);
        var dlg = new BookmarkImportWindow(existing) { Owner = this };
        dlg.ShowDialog();
        if (dlg.DialogResult != true) return;
        if (dlg.Result.Count == 0) { SetStatus("没有可导入的新书签（都已收藏）"); return; }

        var page = _pages[(int)PageKind.Web];
        foreach (var entry in dlg.Result)
        {
            entry.Position = page.Count;
            page.Add(new TileVm(entry));
        }
        Persist();
        SetStatus($"已导入 {dlg.Result.Count} 个书签");
    }

    /// <summary>落一条收藏到指定页：查重 → 入集合 → 落盘 → 补图标。</summary>
    private void AddEntry(PageKind page, AppKind kind, string target, string name, string? group = null)
    {
        int p = (int)page;
        if (_pages[p].Any(t => t.Entry.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"{name} 已在本页收藏中");
            return;
        }
        var entry = new AppEntry
        {
            Id = AppEntry.MakeId(target),
            Name = name,
            Kind = kind,
            Page = page,
            Target = target,
            Group = string.IsNullOrEmpty(group) ? null : group,
            Position = _pages[p].Count,
        };
        var vm = new TileVm(entry);
        _pages[p].Add(vm);
        Persist();
        LoadIconAsync(vm);
        SetStatus($"已收藏 {name}");
    }

    // ================== 窗口 ==================

private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
      var dlg = new SettingsWindow(_settings, _doc.Name) { Owner = this };
     dlg.ShowDialog();
        if (dlg.DialogResult != true) return;

      bool scaleChanged = _settings.TileIconScale != dlg.Result.TileIconScale;
     _settings.DoubleClickOpen = dlg.Result.DoubleClickOpen;
    _settings.TileIconScale = dlg.Result.TileIconScale;
   SettingsStore.Save(_settings);
        if (scaleChanged) ApplyTileIconScale();

        if (dlg.RenamedMapTo != null)
        {
      _doc.Name = dlg.RenamedMapTo;
 _docPath = MapStore.PathOf(dlg.RenamedMapTo);   // Rename 已把文件挪到新落点
      ReloadFromStore();
            SetStatus($"地图已改名为「{dlg.RenamedMapTo}」");
      return;
        }

  if (scaleChanged)
        {
            SetStatus($"图标已改为「{TileScaleLabel()}」档");
            return;
        }
 SetStatus(_settings.DoubleClickOpen
            ? "已改为双击打开 —— 单击只看目标"
            : "已改为单击打开 —— 手机习惯");
    }

    /// <summary>
    /// 把图标档位写进应用级资源，磁贴尺寸/图标槽位随之变（XAML 那三处用 DynamicResource）。
    /// 换档后要重载图标：旧档位解出来的 BitmapSource 尺寸已经钉死，
    /// 直接换容器尺寸会被拉伸 —— 必须重新按新物理像素解码。
    /// </summary>
private void ApplyTileIconScale()
  {
     var (tile, icon, _) = SettingsStore.TileScales[SettingsStore.ClampScale(_settings.TileIconScale)];
        Resources["Tile.Size"] = tile;
        Resources["Tile.Icon"] = icon;
        TileIconDip = icon;   // 解码要跟着走：物理像素 = icon DIP × DPI

// 资源已换，但 TileVm 的标签高度是构造时算的 —— 重建磁贴集合，让它们重算。
  // 重建的是轻量 VM（Entry 本来就还在），图标重新走 LoadIconAsync。
      var snapshot = _pages.Select(p => p.ToList()).ToList();
        for (int i = 0; i < _pages.Length; i++)
        {
     _pages[i].Clear();
    foreach (var vm in snapshot[i]) _pages[i].Add(new TileVm(vm.Entry));
        }
        RefreshChrome();

        foreach (var page in _pages)
      foreach (var t in page)
      _ = t.LoadIconAsync(IconGate, PersistEntry, DpiScale.ToPixels(TileIconDip));
    }

    private string TileScaleLabel()
        => SettingsStore.TileScales[SettingsStore.ClampScale(_settings.TileIconScale)].Label;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void SetStatus(string text, bool warn = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty,
            warn ? "Brush.Danger" : "Brush.Text.Secondary");
    }
}
