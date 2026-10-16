using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenUtau.Core;
using OpenUtau.Core.Vocaloid;

namespace OpenUtau.App.Controls {
    /// <summary>
    /// Imports an already-installed Vocaloid voicebank into the bridge's V5
    /// registration flow. This dialog collects paths and reports readiness;
    /// it never installs or changes activation data.
    /// </summary>
    public sealed class VocaloidImportDialog : Window {
        readonly TextBox enginePath = new TextBox { PlaceholderText = "V5 引擎安装目录", MinWidth = 240 };
        readonly TextBox voicePath = new TextBox { PlaceholderText = "V3/V4/V5 声库目录（可选声库根目录或单个声库目录）", MinWidth = 420 };
        readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly TextBlock summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly Button findEngineButton = new Button { Content = "自动查找 V5 引擎" };
        readonly Button browseEngineButton = new Button { Content = "选择引擎目录…" };
        readonly Button browseVoiceButton = new Button { Content = "选择声库目录…" };
        readonly Button importButton = new Button { Content = "导入已安装的 VOCALOID 声库" };
        readonly Button closeButton = new Button { Content = "关闭" };
        CancellationTokenSource? importCancellation;
        bool busy;

        VocaloidImportDialog() {
            Title = "导入已安装的 VOCALOID 声库";
            Width = 720;
            MinWidth = 650;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            findEngineButton.Click += async (_, _) => await FindEngineAsync();
            browseEngineButton.Click += async (_, _) => await BrowseFolderAsync(enginePath, "选择 VOCALOID5 引擎目录");
            browseVoiceButton.Click += async (_, _) => await BrowseFolderAsync(voicePath, "选择已安装的 VOCALOID 声库目录");
            importButton.Click += async (_, _) => await ImportAsync();
            closeButton.Click += (_, _) => Close();

            var explanation = new TextBlock {
                Text = "选择已安装的中文 V3/V4/V5 声库目录，可选择包含多个声库的根目录。导入只登记目录并创建 OpenUtau 歌手，不复制声库数据。状态“待渲染验证”不代表未授权；“引擎已加载授权信息”也不代表已验证出声。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
            };
            var engineRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            engineRow.Children.Add(enginePath);
            Grid.SetColumn(findEngineButton, 1); engineRow.Children.Add(findEngineButton);
            Grid.SetColumn(browseEngineButton, 2); engineRow.Children.Add(browseEngineButton);
            var voiceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            voiceRow.Children.Add(voicePath);
            Grid.SetColumn(browseVoiceButton, 1); voiceRow.Children.Add(browseVoiceButton);
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            actionRow.Children.Add(importButton);
            actionRow.Children.Add(closeButton);
            var content = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
            content.Children.Add(explanation);
            content.Children.Add(new TextBlock { Text = "VOCALOID5 引擎目录" });
            content.Children.Add(engineRow);
            content.Children.Add(new TextBlock { Text = "声库目录" });
            content.Children.Add(voiceRow);
            content.Children.Add(status);
            content.Children.Add(new ScrollViewer { Content = summary, MaxHeight = 220 });
            content.Children.Add(actionRow);
            Content = content;
            Closed += (_, _) => importCancellation?.Cancel();

            _ = FindEngineAsync();
        }

        public static Task ShowForImportAsync(Window owner) => new VocaloidImportDialog().ShowDialog(owner);

        async Task FindEngineAsync() {
            if (busy) return;
            try {
                string? path = await Task.Run(() => VocaloidVoicebankImporter.FindEngineDirectory());
                if (!string.IsNullOrWhiteSpace(path)) {
                    enginePath.Text = path;
                    status.Text = "已找到 VOCALOID5 引擎目录。";
                } else {
                    status.Text = "未能自动查找引擎，请选择 VOCALOID5 安装目录。";
                }
            } catch (Exception e) {
                status.Text = "查找引擎失败：" + e.Message;
            }
        }

        async Task BrowseFolderAsync(TextBox target, string title) {
            if (busy) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
                Title = title,
                AllowMultiple = false,
            });
            target.Text = folders?.FirstOrDefault()?.TryGetLocalPath() ?? target.Text;
        }

        async Task ImportAsync() {
            if (busy) return;
            string engine = enginePath.Text?.Trim() ?? string.Empty;
            string voice = voicePath.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(engine) || string.IsNullOrWhiteSpace(voice)) {
                status.Text = "请先选择 VOCALOID5 引擎目录和声库目录。";
                return;
            }

            busy = true;
            SetBusy(true);
            summary.Text = string.Empty;
            status.Text = "正在导入已安装的 VOCALOID 声库…";
            importCancellation = new CancellationTokenSource();
            try {
                var result = await VocaloidVoicebankImporter.ImportAsync(engine, voice, importCancellation.Token);
                string? refreshFailure = null;
                try {
                    await Task.Run(() => SingerManager.Inst.SearchAllSingers());
                    DocManager.Inst.ExecuteCmd(new SingersRefreshedNotification());
                } catch (Exception refreshError) {
                    refreshFailure = refreshError.Message;
                }
                ShowResult(result);
                if (refreshFailure != null) {
                    status.Text += "；刷新歌手列表失败：" + refreshFailure;
                }
            } catch (OperationCanceledException) {
                status.Text = "导入已取消。";
            } catch (Exception e) {
                status.Text = "导入失败：" + e.Message;
                summary.Text = "请确认声库已安装且可被所选 VOCALOID5 引擎识别，然后重试。";
            } finally {
                importCancellation.Dispose();
                importCancellation = null;
                busy = false;
                SetBusy(false);
            }
        }

        void ShowResult(VocaloidVoicebankImportResult result) {
            var voices = new List<string>();
            int catalogReadyCount = 0;
            foreach (var value in result.Voices) {
                string name = value.Name;
                int generation = value.Generation;
                if (value.Ready) catalogReadyCount++;
                string statusText = value.Ready ? "引擎已加载授权信息" : "待渲染验证";
                string generationText = generation > 0 ? $"VOCALOID {generation}" : "代际未识别";
                voices.Add($"• {name} — {generationText} — {statusText}");
            }
            string location = result.SingerDirectory;
            status.Text = $"导入完成：共识别 {voices.Count} 个声库，其中 {catalogReadyCount} 个引擎已加载授权信息。";
            summary.Text = "注册目录：" + location + (voices.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, voices) : Environment.NewLine + "没有识别到可导入的声库。请检查目录选择和引擎安装状态。");
        }

        void SetBusy(bool value) {
            enginePath.IsEnabled = !value;
            voicePath.IsEnabled = !value;
            findEngineButton.IsEnabled = !value;
            browseEngineButton.IsEnabled = !value;
            browseVoiceButton.IsEnabled = !value;
            importButton.IsEnabled = !value;
            closeButton.IsEnabled = !value;
        }
    }
}
