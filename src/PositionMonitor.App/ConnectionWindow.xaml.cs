using System.Globalization;
using System.Text.Json;
using System.Windows;
using PositionMonitor.App.Services;
using PositionMonitor.Core.Protocol;

namespace PositionMonitor.App;

public partial class ConnectionWindow : Window
{
    private readonly ConnectionSettings _previous;
    public ConnectionSettings Settings { get; private set; }
    public string Password => PasswordInput.Password;
    public bool ConnectAfterSave { get; private set; }
    public ConnectionWindow(ConnectionSettings settings, string password)
    {
        InitializeComponent();
        _previous = settings;
        Settings = JsonSerializer.Deserialize<ConnectionSettings>(JsonSerializer.Serialize(settings))!;
        HostBox.Text = settings.Host; PortBox.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = settings.UserName; PasswordInput.Password = password;
        ClientBox.Text = settings.ClientId; TlsBox.IsChecked = settings.UseTls;
        CompanyBox.Text = settings.Company; GatewayBox.Text = settings.GatewayId;
        TopicBox.Text = settings.Topic; QosBox.SelectedIndex = settings.Qos;
        ModeBox.SelectedIndex = (int)settings.PayloadMode;
        RememberBox.IsChecked = settings.RememberPassword; AckBox.IsChecked = settings.AutoAck;
        ReplyBox.Text = settings.RawReplyTopic;
    }
    private void TlsChanged(object sender, RoutedEventArgs e)
    {
        if (PortBox is null) return;
        if (TlsBox.IsChecked == true && PortBox.Text == "1883") PortBox.Text = "8883";
        else if (TlsBox.IsChecked != true && PortBox.Text == "8883") PortBox.Text = "1883";
    }
    private void GatewayIdChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (GatewayIdDisplay is not null) GatewayIdDisplay.Text = uint.TryParse(GatewayBox.Text.Trim(), out var id) ? IdentifierDisplay.Format(id) : GatewayBox.Text.Trim() == "+" ? "全部网关" : "请输入十进制网关 ID";
        GatewayBox.ToolTip = uint.TryParse(GatewayBox.Text.Trim(), out var gatewayId) ? IdentifierDisplay.Tooltip(gatewayId) : null;
        if (GatewayIdDisplay is not null) GatewayIdDisplay.ToolTip = GatewayBox.ToolTip;
    }
    private void GenerateClick(object sender, RoutedEventArgs e)
    {
        var company = CompanyBox.Text.Trim().Trim('/');
        var gateway = GatewayBox.Text.Trim();
        if (company.Length == 0 || company.Contains('+') || company.Contains('#') || (gateway != "+" && !uint.TryParse(gateway, out _)))
        { ErrorText.Text = "请填写公司前缀，以及十进制网关 ID 或 +。"; return; }
        var topic = $"{company}/up/sgw/{gateway}/#";
        var topics = TopicBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (!topics.Contains(topic, StringComparer.Ordinal)) topics.Add(topic);
        TopicBox.Text = string.Join(Environment.NewLine, topics);
        TopicBox.CaretIndex = TopicBox.Text.Length; TopicBox.ScrollToEnd(); ErrorText.Text = "";
    }
    private void SaveClick(object sender, RoutedEventArgs e) => Save(false);
    private void ConnectClick(object sender, RoutedEventArgs e) => Save(true);
    private void Save(bool connect)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out var port)) throw new ArgumentException("端口必须是 1～65535 的整数。");
            Settings.Host = HostBox.Text.Trim(); Settings.Port = port;
            Settings.ClientId = ClientBox.Text.Trim(); Settings.UserName = UserBox.Text;
            Settings.UseTls = TlsBox.IsChecked == true; Settings.RememberPassword = RememberBox.IsChecked == true;
            Settings.Company = CompanyBox.Text.Trim(); Settings.GatewayId = GatewayBox.Text.Trim();
            Settings.Topic = TopicBox.Text.Trim(); Settings.Qos = (byte)QosBox.SelectedIndex;
            Settings.PayloadMode = (PayloadMode)ModeBox.SelectedIndex;
            Settings.AutoAck = AckBox.IsChecked == true; Settings.RawReplyTopic = ReplyBox.Text.Trim();
            Settings.Validate(Password);
            Settings.Topic = string.Join(Environment.NewLine, Settings.SubscriptionTopics);
            SettingsStore.Save(Settings, Password, _previous);
            ConnectAfterSave = connect; DialogResult = true;
        }
        catch (Exception error) { ErrorText.Text = error.Message; }
    }
}
