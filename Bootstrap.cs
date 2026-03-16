using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace FogOfWar;

[ModInitializer("OnLoad")]
public static class Bootstrap
{
	public static void OnLoad()
	{
		Log.Warn("FogOfWar loaded");
		MapVisibilityController.Initialize();
	}
}
