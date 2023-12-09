using System;
using System.Security.Cryptography;
using System.Text;

namespace TEAMS2HA.API
{
    public static class TokenStorage
    {
        public static void SaveToken(string token)
        {
            if (!string.IsNullOrEmpty(token))
            {
                byte[] encryptedToken = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token),
                    null,
                    DataProtectionScope.CurrentUser);
                Properties.Settings.Default.HomeassistantToken = Convert.ToBase64String(encryptedToken);
                Properties.Settings.Default.Save();
            }
        }

        public static string GetToken()
        {
            string encryptedTokenBase64 = Properties.Settings.Default.HomeassistantToken;
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
