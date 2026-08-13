using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LDAssistant.Services;

namespace LDAssistant.Views
{
 public partial class OcrConfigWindow : Window
 {
 private readonly OnlineOcrService _service;

 public OcrConfigWindow(OnlineOcrService service)
 {
 InitializeComponent();
 _service = service;
 LoadConfig();
 }

 private void LoadConfig()
 {
 TxtApiUrl.Text = _service.Config.ApiUrl ?? "";
 TxtApiKey.Text = _service.Config.ApiKey ?? "";
 TxtPrompt.Text = _service.Config.Prompt ?? "";

 // 引擎选择
 string engine = _service.Config.Engine ?? "siliconflow";
 for (int i = 0; i < CbEngine.Items.Count; i++)
 {
 if (CbEngine.Items[i] is ComboBoxItem item && item.Tag?.ToString() == engine)
 {
 CbEngine.SelectedIndex = i;
 break;
 }
 }

 // 模型
 string model = _service.Config.Model ?? "";
 if (!string.IsNullOrEmpty(model))
 {
 CbModel.Text = model;
 }

 UpdateVisibility();
 }

 private void UpdateVisibility()
 {
 string engine = "";
 if (CbEngine.SelectedItem is ComboBoxItem item)
 engine = item.Tag?.ToString() ?? "siliconflow";

 bool isSiliconFlow = engine == "siliconflow";
 LblModel.Visibility = isSiliconFlow ? Visibility.Visible : Visibility.Collapsed;
 CbModel.Visibility = isSiliconFlow ? Visibility.Visible : Visibility.Collapsed;
 LblPrompt.Visibility = isSiliconFlow ? Visibility.Visible : Visibility.Collapsed;
 TxtPrompt.Visibility = isSiliconFlow ? Visibility.Visible : Visibility.Collapsed;

 if (isSiliconFlow)
 {
 // 使用完整模型名（与API匹配）
 CbModel.Items.Clear();
 CbModel.Items.Add("PaddlePaddle/PaddleOCR-VL");
 CbModel.Items.Add("Qwen/Qwen2.5-VL-72B-Instruct");
 CbModel.Items.Add("deepseek-ai/DeepSeek-OCR");
 if (string.IsNullOrEmpty(CbModel.Text))
 CbModel.Text = "PaddlePaddle/PaddleOCR-VL";
 }

 // 更新 URL（仅当为空时填默认值，不覆盖用户已填的值）
 if (engine == "ocrspace" && string.IsNullOrEmpty(TxtApiUrl.Text))
 TxtApiUrl.Text = "https://api.ocr.space/parse/image";
 else if (engine == "siliconflow" && string.IsNullOrEmpty(TxtApiUrl.Text))
 TxtApiUrl.Text = "https://api.siliconflow.cn/v1/chat/completions";
 }

 private void CbEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
 {
 if (TxtApiUrl == null) return; // XAML 还在初始化
 UpdateVisibility();
 }

 private string _realKey = "";
 private bool _keyVisible = true;

 private void BtnToggleKey_Click(object sender, RoutedEventArgs e)
 {
 if (_keyVisible)
 {
 _realKey = TxtApiKey.Text;
 TxtApiKey.Text = new string('●', _realKey.Length);
 _keyVisible = false;
 BtnToggleKey.Content = "🙈";
 }
 else
 {
 TxtApiKey.Text = _realKey;
 _keyVisible = true;
 BtnToggleKey.Content = "👁";
 }
 }

 private async void BtnTest_Click(object sender, RoutedEventArgs e)
 {
 try
 {
 var config = BuildConfig();
 _service.UpdateConfig(config);
 BtnTest.Content = "⏳ 测试中...";
 BtnTest.IsEnabled = false;

 var (ok, msg) = await _service.TestConnectionAsync();

 BtnTest.IsEnabled = true;
 BtnTest.Content = ok ? "✅ 连接成功" : "❌ 连接失败";
 var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
 timer.Tick += (s, _) => { BtnTest.Content = "🔌 测试连接"; timer.Stop(); };
 timer.Start();

 if (ok)
 MessageBox.Show($"✅ 连接成功！\n\n识别结果: {msg}", "测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
 else
 MessageBox.Show($"❌ 连接失败！\n\n错误: {msg}\n\n请检查API地址、密钥和模型名称是否正确。", "测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
 }
 catch (Exception ex)
 {
 BtnTest.IsEnabled = true;
 BtnTest.Content = "🔌 测试连接";
 MessageBox.Show($"测试失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }

 private void BtnSave_Click(object sender, RoutedEventArgs e)
 {
 try
 {
 var config = BuildConfig();
 _service.UpdateConfig(config);
 DialogResult = true;
 Close();
 }
 catch (Exception ex)
 {
 MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }

 private void BtnCancel_Click(object sender, RoutedEventArgs e)
 {
 DialogResult = false;
 Close();
 }

 private void LnkRegister_Click(object sender, MouseButtonEventArgs e)
 {
 string engine = "";
 if (CbEngine.SelectedItem is ComboBoxItem item)
 engine = item.Tag?.ToString() ?? "siliconflow";

 string url = engine switch
 {
 "siliconflow" => "https://cloud.siliconflow.cn/i/hXBNjD8R",
 "ocrspace" => "https://ocr.space/ocrapi",
 _ => "https://cloud.siliconflow.cn/i/hXBNjD8R"
 };
 try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
 }

 private OnlineOcrConfig BuildConfig()
 {
 string engine = "siliconflow";
 if (CbEngine.SelectedItem is ComboBoxItem item)
 engine = item.Tag?.ToString() ?? "siliconflow";

 return new OnlineOcrConfig
 {
 Engine = engine,
 ApiUrl = TxtApiUrl.Text?.Trim(),
 ApiKey = TxtApiKey.Text?.Trim(),
 Model = CbModel.Text?.Trim(),
 Prompt = TxtPrompt.Text?.Trim()
 };
 }
 }
}
