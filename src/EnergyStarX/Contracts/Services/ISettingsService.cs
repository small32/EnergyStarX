namespace EnergyStarX.Contracts.Services;

public interface ISettingsService
{
    bool FirstRun { get; set; }
    bool ThrottleWhenPluggedIn { get; set; }
    bool HideSystemTrayIcon { get; set; }
    string ProcessWhitelistString { get; set; }
    string ProcessBlacklistString { get; set; }
}