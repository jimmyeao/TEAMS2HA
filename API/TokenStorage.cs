using System;
using System.Security.Cryptography;
using System.Text;

namespace TEAMS2HA.API
{
    public static class TokenStorage
    {
        public static void SaveHomeassistantToken(string token)
        {
            SaveToken(token, "HomeassistantToken");
        }

        public static string GetHomeassistantToken()
        {
            return GetToken("HomeassistantToken");
        }

        public static void SaveTeamsToken(string token)
        {
            SaveToken(token, "TeamsToken");
        }

        public static string GetTeamsToken()
        {
            return GetToken("TeamsToken");
        }

        private static void SaveToken(string token, string settingKey)
        {
            if (!string.IsNullOrEmpty(token))
            {
                byte[] encryptedToken = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token),
                    null,
                    DataProtectionScope.CurrentUser);
                Properties.Settings.Default[settingKey] = Convert.ToBase64String(encryptedToken);
                Properties.Settings.Default.Save();
            }
        }

        private static string GetToken(string settingKey)
        {
            string encryptedTokenBase64 = Properties.Settings.Default[settingKey] as string;
            if (!string.IsNullOrEmpty(encryptedTokenBase64))
            {
                byte[] encryptedToken = Convert.FromBase64String(encryptedTokenBase64);
                byte[] decryptedToken = ProtectedData.Unprotect(
                    encryptedToken,
                    null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedToken);
            }
            return null;
        }
    }
}
