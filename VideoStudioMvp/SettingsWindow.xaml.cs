namespace VideoStudioMvp;

public partial class SettingsWindow : System.Windows.Window
{
    private readonly Action<AppSettings> _save; private readonly AppSettings _current;
    public SettingsWindow(AppSettings current, Action<AppSettings> save) { InitializeComponent(); _current = current; _save = save; ApiKeyBox.Password = current.ApiKey; EndpointBox.Text = current.Endpoint; ModelBox.Text = current.Model; }
    private void Save_Click(object sender, System.Windows.RoutedEventArgs e) { _current.ApiKey = ApiKeyBox.Password; _current.Endpoint = EndpointBox.Text.Trim(); _current.Model = ModelBox.Text.Trim(); _save(_current); DialogResult = true; Close(); }
    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e) { Close(); }
}
