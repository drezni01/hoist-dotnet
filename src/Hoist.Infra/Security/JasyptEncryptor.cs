using System.Security.Cryptography;
using System.Text;
using Hoist.Core.Interfaces;

namespace Hoist.Infra.Security
{
    /// <summary>
    /// Wire-compatible port of the Jasypt encryptors hoist-core uses for pwd configs:
    /// - BasicTextEncryptor: PBEWithMD5AndDES, 8-byte random salt prefixed to the ciphertext,
    ///   1000 key-derivation iterations, Base64 output, hard-coded password (AppConfig.createEncryptor).
    /// - ConfigurablePasswordEncryptor with plainDigest: Base64(MD5(utf8)), no salt, 1 iteration.
    /// </summary>
    public class JasyptEncryptor : IConfigEncryptor
    {
        private const string HoistPassword = "dsd899s_*)jsk9dsl2fd223hpdj32))I@333";
        private const int SaltSize = 8;
        private const int Iterations = 1000;

        private readonly byte[] password;

        public JasyptEncryptor(string password = HoistPassword)
        {
            // PBEKeySpec with PBEWithMD5AndDES uses the low byte of each password char.
            this.password = password.Select(c => (byte)c).ToArray();
        }

        public string Encrypt(string plainText)
        {
            if (plainText == null) return null;
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            using var des = CreateDes(salt);
            var cipher = des.EncryptCbc(Encoding.UTF8.GetBytes(plainText), des.IV, PaddingMode.PKCS7);
            return Convert.ToBase64String(salt.Concat(cipher).ToArray());
        }

        public string Decrypt(string cipherText)
        {
            if (cipherText == null) return null;
            var bytes = Convert.FromBase64String(cipherText);
            var salt = bytes.Take(SaltSize).ToArray();
            using var des = CreateDes(salt);
            var plain = des.DecryptCbc(bytes.Skip(SaltSize).ToArray(), des.IV, PaddingMode.PKCS7);
            return Encoding.UTF8.GetString(plain);
        }

        public string Digest(string plainText)
        {
            if (plainText == null) return null;
            return Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(plainText.Normalize(NormalizationForm.FormC))));
        }

        /// <summary>PKCS#5 v1.5 PBKDF1 with MD5: 16 derived bytes = 8-byte DES key + 8-byte IV.</summary>
        private DES CreateDes(byte[] salt)
        {
            var derived = MD5.HashData(password.Concat(salt).ToArray());
            for (var i = 1; i < Iterations; i++) derived = MD5.HashData(derived);

            var des = DES.Create();
            des.Key = derived.Take(8).ToArray();
            des.IV = derived.Skip(8).Take(8).ToArray();
            return des;
        }
    }
}
