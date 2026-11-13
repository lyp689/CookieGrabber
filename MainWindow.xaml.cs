using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace CookieGrabber;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CookieItem> _cookies = new();
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        CookieGrid.ItemsSource = _cookies;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // 用户数据目录：登录状态持久化，下次打开无需重新登录
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CookieGrabber", "WebView2Profile");
            Directory.CreateDirectory(dataDir);

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: dataDir);
            await Browser.EnsureCoreWebView2Async(env);

            Browser.CoreWebView2.SourceChanged += (_, args) =>
            {
                AddressBar.Text = args.IsNewDocument ? Browser.Source?.ToString() : AddressBar.Text;
            };
            Browser.CoreWebView2.DocumentTitleChanged += (_, _) => Title = $"网站 Cookies 获取工具 - {Browser.CoreWebView2.DocumentTitle}";
            Browser.NavigationCompleted += (_, _) =>
            {
                AddressBar.Text = Browser.Source?.ToString();
                UpdateBackForwardState();
            };

            UpdateBackForwardState();
            StatusText.Text = "就绪";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化浏览器组件失败：{ex.Message}\n\n请确认系统已安装 Microsoft Edge WebView2 运行时。",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "初始化失败";
        }
    }

    private void UpdateBackForwardState()
    {
        if (Browser.CoreWebView2 == null) return;
        BtnBack.IsEnabled = Browser.CoreWebView2.CanGoBack;
        BtnForward.IsEnabled = Browser.CoreWebView2.CanGoForward;
    }

    // ---------- 地址栏操作 ----------

    private void NavigateTo(string input)
    {
        if (Browser.CoreWebView2 == null) return;
        input = input?.Trim() ?? "";
        if (string.IsNullOrEmpty(input)) return;

        // 没有 scheme 时补 https://
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            input = "https://" + input;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            Browser.CoreWebView2.Navigate(uri.ToString());
            StatusText.Text = $"正在访问 {uri.Host} ...";
        }
        else
        {
            StatusText.Text = "无效的网址";
        }
    }

    private void BtnGo_Click(object sender, RoutedEventArgs e) => NavigateTo(AddressBar.Text);

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateTo(AddressBar.Text);
            Keyboard.ClearFocus();
            Browser.Focus();
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void BtnForward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Reload();
    }

    // ---------- Cookie 获取与筛选 ----------

    private string? SafeHost()
    {
        try { return Browser.Source?.Host; }
        catch { return null; }
    }

    private async void BtnGrabCookies_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 == null)
        {
            StatusText.Text = "浏览器尚未初始化完成";
            return;
        }

        try
        {
            var all = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(null);

            IEnumerable<CoreWebView2Cookie> filtered = all;
            if (ChkOnlyCurrent.IsChecked == true)
            {
                var host = SafeHost();
                if (!string.IsNullOrEmpty(host))
                {
                    var hostParts = host.ToLowerInvariant().Split('.');
                    var candidates = new List<string>();
                    for (int i = 0; i < hostParts.Length - 1; i++)
                    {
                        var suffix = string.Join('.', hostParts, i, hostParts.Length - i);
                        candidates.Add("." + suffix);
                        candidates.Add(suffix);
                    }
                    filtered = filtered.Where(c =>
                        candidates.Any(s => c.Domain.ToLowerInvariant() == s ||
                                            c.Domain.ToLowerInvariant().EndsWith(s)));
                }
            }

            _cookies.Clear();
            foreach (var c in filtered.OrderBy(x => x.Domain).ThenBy(x => x.Name))
            {
                _cookies.Add(new CookieItem
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    ExpiresText = c.IsSession ? "会话结束失效" : c.Expires.ToString("yyyy-MM-dd HH:mm:ss"),
                    Secure = c.IsSecure,
                    HttpOnly = c.IsHttpOnly,
                    SameSite = c.SameSite.ToString()
                });
            }

            StatusText.Text = $"已获取 {_cookies.Count} 条 Cookie" +
                (ChkOnlyCurrent.IsChecked == true ? $"（当前域名：{SafeHost()}）" : "（全部域名）");
        }
        catch (Exception ex)
        {
            StatusText.Text = "获取失败：" + ex.Message;
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _cookies.Clear();
        StatusText.Text = "已清空列表";
    }

    // ---------- 导出 ----------

    private string BuildCookieHeader()
    {
        var host = SafeHost();
        // 请求头应按路径最长优先、创建时间排序，这里简化为按名称排序
        return string.Join("; ",
            _cookies.Select(c => $"{c.Name}={c.Value}"));
    }

    private void BtnCopyHeader_Click(object sender, RoutedEventArgs e)
    {
        if (_cookies.Count == 0)
        {
            StatusText.Text = "请先点击「获取 Cookies」";
            return;
        }
        try
        {
            Clipboard.SetText(BuildCookieHeader());
            StatusText.Text = $"已复制 {_cookies.Count} 条 Cookie 的请求头";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void BtnExportJson_Click(object sender, RoutedEventArgs e)
    {
        ExportAndThen(path =>
        {
            var list = _cookies.Select(c => new
            {
                name = c.Name,
                value = c.Value,
                domain = c.Domain,
                path = c.Path,
                expires = c.ExpiresText,
                secure = c.Secure,
                httpOnly = c.HttpOnly,
                sameSite = c.SameSite
            });
            var json = System.Text.Json.JsonSerializer.Serialize(list,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }, "JSON 文件|*.json", "cookies.json");
    }

    private void BtnExportNetscape_Click(object sender, RoutedEventArgs e)
    {
        ExportAndThen(path =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# Generated by CookieGrabber");
            foreach (var c in _cookies)
            {
                var expiry = c.ExpiresText.StartsWith("2") && DateTime.TryParse(c.ExpiresText, out var dt)
                    ? (long)(dt - new DateTime(1970, 1, 1)).TotalSeconds
                    : 0;
                var secure = c.Secure ? "TRUE" : "FALSE";
                // include_subdomains: 域以 . 开头时为 TRUE
                var flag = c.Domain.StartsWith(".") ? "TRUE" : "FALSE";
                sb.AppendLine(string.Join('\t',
                    c.Domain, flag, c.Path, secure, expiry, c.Name, c.Value));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }, "Netscape cookies.txt|*.txt", "cookies.txt");
    }

    private void ExportAndThen(Action<string> writeAction, string filter, string defaultName)
    {
        if (_cookies.Count == 0)
        {
            StatusText.Text = "请先点击「获取 Cookies」";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            writeAction(dlg.FileName);
            StatusText.Text = $"已导出 {_cookies.Count} 条到 {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }
}

// ---------- 数据模型 ----------

public class CookieItem
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "";
    public string ExpiresText { get; set; } = "";
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public string SameSite { get; set; } = "";
}
