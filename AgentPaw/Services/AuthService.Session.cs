using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AgentPaw.Data;
using AgentPaw.Models;

namespace AgentPaw.Services;

public partial class AuthService
{
    public async Task<SessionInfo?> GetSessionAsync()
    {
        if (CurrentUserId == null || CurrentTokenId == null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var token = await db.AuthTokens.FindAsync(CurrentTokenId);
        if (token == null || token.IsRevoked || (token.ExpiresAt.HasValue && token.ExpiresAt < DateTimeOffset.UtcNow))
            return null;

        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null || !user.IsActive) return null;

        return new SessionInfo
        {
            UserId = user.UserId,
            Email = user.Email,
            DisplayName = user.DisplayName,
            ProfileImageUrl = user.ProfileImageUrl
        };
    }

    public async Task LogoutAsync()
    {
        if (CurrentTokenId == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var token = await db.AuthTokens.FindAsync(CurrentTokenId);
        if (token != null)
        {
            token.IsRevoked = true;

            db.AuditLogs.Add(new AuditLog
            {
                AuditId = Guid.NewGuid().ToString(),
                UserId = CurrentUserId ?? string.Empty,
                Action = "LOGOUT",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        CurrentUserId = null;
        CurrentTokenId = null;
    }

    public async Task LogoutAllAsync()
    {
        if (CurrentUserId == null) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var sessions = await db.AuthTokens
            .Where(t => t.UserId == CurrentUserId && t.TokenType == "APP_SESSION" && !t.IsRevoked)
            .ToListAsync();

        foreach (var s in sessions)
            s.IsRevoked = true;

        await db.SaveChangesAsync();
        CurrentUserId = null;
        CurrentTokenId = null;
    }

    public async Task<List<SessionEntry>> GetSessionsAsync()
    {
        if (CurrentUserId == null) return [];

        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AuthTokens
            .Where(t => t.UserId == CurrentUserId && t.TokenType == "APP_SESSION" && !t.IsRevoked)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new SessionEntry
            {
                TokenId = t.TokenId,
                DeviceName = t.DeviceName,
                CreatedAt = t.CreatedAt,
                ExpiresAt = t.ExpiresAt,
                IsCurrent = t.TokenId == CurrentTokenId
            })
            .ToListAsync();
    }

    public async Task RevokeSessionAsync(string tokenId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var token = await db.AuthTokens.FindAsync(tokenId);
        if (token != null && token.UserId == CurrentUserId)
        {
            token.IsRevoked = true;

            db.AuditLogs.Add(new AuditLog
            {
                AuditId = Guid.NewGuid().ToString(),
                UserId = CurrentUserId ?? string.Empty,
                Action = "SESSION_FORCE_REVOKE",
                Detail = JsonSerializer.Serialize(new { revokedTokenId = tokenId }),
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 로그인 성공 후 JWT와 tokenId를 로컬 파일에 저장한다.
    /// </summary>
    public void PersistSession(string jwt, string tokenId)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentPaw", "data");
        Directory.CreateDirectory(dataDir);
        var sessionPath = Path.Combine(dataDir, ".session");
        // 세션 파일에 토큰을 암호화하여 저장한다. 평문 JWT 노출을 방지한다.
        var encryptedJwt = _encryption.Encrypt(jwt);
        File.WriteAllText(sessionPath, $"{tokenId}\n{encryptedJwt}");
    }

    /// <summary>
    /// 앱 시작 시 저장된 세션을 복원한다. 유효하면 SessionInfo를 반환한다.
    /// </summary>
    public async Task<SessionInfo?> TryRestoreSessionAsync()
    {
        var sessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentPaw", "data", ".session");

        if (!File.Exists(sessionPath)) return null;

        try
        {
            var lines = File.ReadAllLines(sessionPath);
            if (lines.Length < 2) return null;

            var tokenId = lines[0].Trim();
            var encryptedJwt = lines[1].Trim();

            // 세션 파일의 JWT는 암호화되어 저장된다. 복호화 후 검증한다.
            string jwt;
            try { jwt = _encryption.Decrypt(encryptedJwt); }
            catch { ClearPersistedSession(); return null; }

            var verified = VerifyToken(jwt);
            if (verified == null) { ClearPersistedSession(); return null; }

            await using var db = await _dbFactory.CreateDbContextAsync();
            var token = await db.AuthTokens.FindAsync(tokenId);
            if (token == null || token.IsRevoked || (token.ExpiresAt.HasValue && token.ExpiresAt < DateTimeOffset.UtcNow))
            {
                ClearPersistedSession();
                return null;
            }

            var user = await db.Users.FindAsync(verified.Value.UserId);
            if (user == null || !user.IsActive) { ClearPersistedSession(); return null; }

            CurrentUserId = user.UserId;
            CurrentTokenId = tokenId;

            return new SessionInfo
            {
                UserId = user.UserId,
                Email = user.Email,
                DisplayName = user.DisplayName,
                ProfileImageUrl = user.ProfileImageUrl
            };
        }
        catch
        {
            ClearPersistedSession();
            return null;
        }
    }

    public void ClearPersistedSession()
    {
        var sessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentPaw", "data", ".session");
        if (File.Exists(sessionPath))
            File.Delete(sessionPath);
    }

}

public class SessionInfo
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
}

public class SessionEntry
{
    public string TokenId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsCurrent { get; set; }
}


