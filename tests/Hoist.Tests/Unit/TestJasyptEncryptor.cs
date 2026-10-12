using Hoist.Infra.Security;

namespace Hoist.Tests.Unit
{
    /// <summary>
    /// pwd configs must stay readable by both Grails and .NET while they share a database.
    /// Vectors were produced by jasypt 1.9.3 BasicTextEncryptor / ConfigurablePasswordEncryptor(plainDigest)
    /// with hoist-core's hard-coded password (AppConfig.createEncryptor).
    /// </summary>
    [TestClass]
    public class TestJasyptEncryptor
    {
        private readonly JasyptEncryptor encryptor = new JasyptEncryptor();

        [TestMethod]
        public void TestDecryptsJasyptCiphertext()
        {
            Assert.AreEqual("secret", encryptor.Decrypt("IWFJrQO0g4sCZvM6qYGLAQ=="));
            Assert.AreEqual("p@ss w0rd!", encryptor.Decrypt("nJVWJCKlNrBzbU5Z0OKHvbE9IA1o4yEC"));
        }

        [TestMethod]
        public void TestEncryptRoundTripUsesRandomSalt()
        {
            var a = encryptor.Encrypt("some value é");
            var b = encryptor.Encrypt("some value é");
            Assert.AreNotEqual(a, b);
            Assert.AreEqual("some value é", encryptor.Decrypt(a));
            Assert.AreEqual("some value é", encryptor.Decrypt(b));
        }

        [TestMethod]
        public void TestDigestMatchesJasyptPlainDigest()
        {
            Assert.AreEqual("Xr4ilOzQ4PCOq3aQ0qbuaQ==", encryptor.Digest("secret"));
        }
    }
}
