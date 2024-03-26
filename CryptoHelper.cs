using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TEAMS2HA
{
    public static class CryptoHelper
    {
        #region Public Methods

        public static string DecryptString(string encryptedText)
        {
            // Check if the input string is null or empty
            if (string.IsNullOrEmpty(encryptedText))
            {
                // Return null or throw an exception as per your application's error handling policy
                return null; // Or throw new ArgumentNullException(nameof(encryptedText));
            }

            byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decryptedBytes);
        }

        public static string EncryptString(string plainText)
        {
            // Check if the input string is null or empty
            if (string.IsNullOrEmpty(plainText))
            {
                // Return null or throw an exception as per your application's error handling policy
                return null; // Or throw new ArgumentNullException(nameof(plainText));
            }

            byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(plainTextBytes, null, DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(encryptedBytes);
        }

        #endregion Public Methods
    }
}