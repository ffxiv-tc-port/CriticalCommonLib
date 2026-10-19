using Dalamud.Plugin.Services;

namespace CriticalCommonLib.Extensions
{
    public static class ClientStateExtension
    {
        public static string GetCharacterName(this IObjectTable objectTable)
        {
            return objectTable.LocalPlayer != null ? objectTable.LocalPlayer.Name.ToString() : "";
        }
    }
}