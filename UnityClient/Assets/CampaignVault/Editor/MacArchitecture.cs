using System;
using System.Reflection;
using UnityEditor.Build;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// The macOS build architecture, reached by reflection: UnityEditor.OSXStandalone
    /// only exists when Mac build support is installed, and the CI images that
    /// build Windows and Linux don't have it. A direct reference broke every
    /// release build on those images with "Scripts have compiler errors".
    /// </summary>
    internal static class MacArchitecture
    {
        private const string TypeName = "UnityEditor.OSXStandalone.UserBuildSettings";

        private static PropertyInfo Property()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(TypeName, false);
                if (type != null) { return type.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static); }
            }
            return null;
        }

        /// <summary>Null without Mac build support.</summary>
        public static OSArchitecture? Get()
        {
            var property = Property();
            return property != null ? (OSArchitecture)property.GetValue(null) : (OSArchitecture?)null;
        }

        public static void Set(OSArchitecture architecture)
        {
            var property = Property();
            if (property == null) { throw new InvalidOperationException("Mac build support isn't installed in this editor, so there's no macOS architecture to set."); }
            property.SetValue(null, architecture);
        }
    }
}
