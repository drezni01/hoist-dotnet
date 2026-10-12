using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hoist.Tests.Support
{
    /// <summary>Issues HS256 tokens shaped like typical ASP.NET host tokens (unique_name, email, name claims).</summary>
    public static class TestTokens
    {
        public static readonly string SigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("hoist-test-signing-key-0123456789-abcdefghijklmnop"));

        public static string Create(string username, string email = null, string name = null, string signingKey = null, DateTime? expires = null, string impersonator = null)
        {
            var claims = new List<Claim> { new Claim("unique_name", username) };
            if (email != null) claims.Add(new Claim("email", email));
            if (name != null) claims.Add(new Claim("name", name));
            if (impersonator != null) claims.Add(new Claim("impersonator", impersonator));

            var exp = expires ?? DateTime.UtcNow.AddHours(1);
            var key = new SymmetricSecurityKey(Convert.FromBase64String(signingKey ?? SigningKey));
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                NotBefore = exp.AddHours(-2),
                Expires = exp,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            };
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }
    }
}
