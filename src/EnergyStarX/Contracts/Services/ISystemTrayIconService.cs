namespace EnergyStarX.Contracts.Services;

public interface ISystemTrayIconService
{
    Task Initialize();

    void SetIconVisible(bool visible);
}
