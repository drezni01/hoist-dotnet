using System.Security.Claims;
using Hoist.Api.Hosting;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// A host app with its own API that also serves Hoist. It listens on :5080 and :8080 (see "Urls"), and
// Hoist:Port restricts the Hoist endpoints to :8080, where hoist-react clients expect them.
// It validates HS256 tokens signed with the host app's key, so the same client can use either.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// The host's own authentication, for its own API.
var signingKey = config["Hoist:Auth:SigningKey"];
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            IssuerSigningKey = string.IsNullOrEmpty(signingKey) ? null : new SymmetricSecurityKey(Convert.FromBase64String(signingKey))
        };
    });
builder.Services.AddAuthorization();

// The Hoist client calls with credentials (the Hoist session cookie), so its origin must be listed.
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("CorsOrigins").Get<string[]>() ?? new string[0])
    .AllowCredentials()
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Hoist: settings, auth provider and (optionally) the user directory - each explicit.
builder.Services.AddHoist(o => config.GetSection("Hoist").Bind(o));
builder.Services.AddHoistJwtAuth(jwt => config.GetSection("Hoist:Auth").Bind(jwt));
// Optional: your own user directory. Without one, Hoist's default accepts any user with a valid token.
// builder.Services.AddSingleton<IHoistUserService, FakeUserService>();

var app = builder.Build();

app.UseCors();
app.UseHoist();
app.UseAuthentication();
app.UseAuthorization();

app.MapHoist();
app.MapGet("/", () => "Hoist sample host - Hoist endpoints are on Hoist:Port.");

// The host's own protected API, using the same tokens.
app.MapGet("/me", (ClaimsPrincipal user) => new { username = user.FindFirst("unique_name")?.Value }).RequireAuthorization();

// Development only: issue a token for any username, for trying the Hoist endpoints with curl.
if (app.Environment.IsDevelopment() && !string.IsNullOrEmpty(signingKey))
{
    app.MapPost("/dev/token", (string username) =>
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] { new Claim("unique_name", username), new Claim("email", $"{username}@example.com") }),
            Expires = DateTime.UtcNow.AddHours(8),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(signingKey)), SecurityAlgorithms.HmacSha256)
        };
        return new { token = new JsonWebTokenHandler().CreateToken(descriptor) };
    });
}

await app.RunAsync();

/// <summary>
/// Example user directory (not registered - see above): only alice exists. Hoist would use it for
/// authentication, the Admin Console Users tab and impersonation targets. It does not implement
/// IHoistUserProvisioner, so any other user would be refused even with a valid token.
/// </summary>
public class FakeUserService : IHoistUserService
{
    private static readonly HoistUser Alice = new HoistUser("alice", "alice@example.com", "Alice");

    public Task<HoistUser> FindAsync(string username) => Task.FromResult(username == Alice.Username ? Alice : null);

    public Task<List<HoistUser>> ListAsync(bool activeOnly) => Task.FromResult(new List<HoistUser> { Alice });
}
