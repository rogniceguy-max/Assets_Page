using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Services;
using System.Text;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class RegisterViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _userName = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _selectedDepartment = "影视资源库";
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    public RegisterViewModel(MainViewModel main)
    {
        _main = main;
        RegisterCommand = new AsyncRelayCommand(RegisterAsync, _ => !IsBusy);
        BackToLoginCommand = new RelayCommand(_ => _main.ShowLogin(), _ => !IsBusy);
    }

    public IReadOnlyList<string> Departments { get; } = ["影视资源库", "文学创作库"];
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }
    public string SelectedDepartment { get => _selectedDepartment; set => SetProperty(ref _selectedDepartment, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SubmitLabel));
                RegisterCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string SubmitLabel => IsBusy ? "正在注册…" : "注册并进入";

    public AsyncRelayCommand RegisterCommand { get; }
    public RelayCommand BackToLoginCommand { get; }

    private async Task RegisterAsync(object? _)
    {
        if (IsBusy)
            return;
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "两次输入的密码不一致。";
            return;
        }
        if (UnicodeScalarCount(Password) is < 8 or > 1024 || Password.Contains('\n') || Password.Contains('\r'))
        {
            ErrorMessage = "密码需为 8 至 1024 位，且不能包含换行符。";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _main.RegisterAsync(UserName, Password, SelectedDepartment);
        }
        catch (V2ApiException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception)
        {
            ErrorMessage = "注册失败，请检查网络后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static int UnicodeScalarCount(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }
}
