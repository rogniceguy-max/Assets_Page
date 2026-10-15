using MuYingDrive.NativeWpf.Infrastructure;
using MuYingDrive.NativeWpf.Services;

namespace MuYingDrive.NativeWpf.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _userName = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    public LoginViewModel(MainViewModel main)
    {
        _main = main;
        LoginCommand = new AsyncRelayCommand(LoginAsync, _ => !IsBusy);
        RegisterCommand = new RelayCommand(_ => _main.ShowRegister(), _ => !IsBusy);
        EnterVisitorCommand = new AsyncRelayCommand(_ => _main.EnterVisitorAsync(), _ => !IsBusy);
    }

    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SubmitLabel));
                LoginCommand.RaiseCanExecuteChanged();
                RegisterCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string SubmitLabel => IsBusy ? "正在登录…" : "登录";

    public AsyncRelayCommand LoginCommand { get; }
    public RelayCommand RegisterCommand { get; }
    public AsyncRelayCommand EnterVisitorCommand { get; }

    private async Task LoginAsync(object? _)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _main.SignInAsync(UserName, Password);
        }
        catch (V2ApiException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception)
        {
            ErrorMessage = "登录失败，请检查网络后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
