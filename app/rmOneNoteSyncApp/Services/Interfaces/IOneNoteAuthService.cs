using System;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface IOneNoteAuthService
{
    event EventHandler<AuthenticationStateChangedEventArgs>? AuthenticationStateChanged;
    
    bool IsAuthenticated { get; }
    string? UserName { get; }
    string? AccessToken { get; }
    DateTime? TokenExpiry { get; }
    
    Task<AuthenticationResult> SignInAsync();
    Task<AuthenticationResult> SignInSilentAsync();
    Task SignOutAsync();
    Task<string?> GetAccessTokenAsync();
}

public class AuthenticationStateChangedEventArgs : EventArgs
{
    public bool IsAuthenticated { get; set; }
    public string? UserName { get; set; }
}

public class AuthenticationResult
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? UserName { get; set; }
    public DateTime? ExpiresOn { get; set; }
    public string? ErrorMessage { get; set; }
}