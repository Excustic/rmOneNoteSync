namespace rmOneNoteSyncApp.Services.Interfaces;

public interface IStartupService
{
    public bool IsStartupEnabled();
    public void SetStartup(bool enable);
}
