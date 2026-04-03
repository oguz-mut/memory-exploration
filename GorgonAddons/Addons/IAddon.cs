namespace GorgonAddons.Addons;
using GorgonAddons.Core;

public interface IAddon
{
    string Name { get; }
    string Version { get; }
    void Initialize(GameContext ctx);
    void OnTick(GameContext ctx);
    void Shutdown();
}
