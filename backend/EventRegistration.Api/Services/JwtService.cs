using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EventRegistration.Api.Data;
using EventRegistration.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace EventRegistration.Api.Services;

public class JwtSettings
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int ExpiryHours { get; set; } = 8;
}

public class JwtService
{
    private const string PasswordSetClaim = "password_set";

    private readonly JwtSettings _s;
    public JwtService(JwtSettings s) => _s = s;

    public string CreateToken(User user)
    {
        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new InvalidOperationException("The account needs a password before it can sign in.");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(PasswordSetClaim, "true"),
        };
        if (user.CanConfirmRegistrations)
            claims.Add(new Claim(Permissions.ConfirmRegistrations, "true"));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _s.Issuer,
            audience: _s.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_s.ExpiryHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static async Task ValidatePasswordAccessAsync(TokenValidatedContext context)
    {
        // A token can outlive its account, or belong to an older passwordless invitation.
        if (!int.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            context.Fail("Invalid user account.");
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.PasswordHash != "", context.HttpContext.RequestAborted))
            context.Fail("The account is unavailable or needs a password before it can sign in.");
    }
}
