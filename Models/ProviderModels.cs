using System.Text.Json.Serialization;

namespace CodexBarWin.Models;

// ═══════════════════════════════════════════════════════
//  Codex (OpenAI) — https://chatgpt.com/backend-api/wham/usage
// ═══════════════════════════════════════════════════════

public record CodexUsageResponse
{
    [JsonPropertyName("plan_type")]
    public string? PlanType { get; init; }

    [JsonPropertyName("rate_limit")]
    public CodexRateLimitDetails? RateLimit { get; init; }

    [JsonPropertyName("rate_limits")]
    public List<CodexWindowSnapshot>? RateLimits { get; init; }

    [JsonPropertyName("used_percent")]
    public double? UsedPercent { get; init; }

    [JsonPropertyName("usage_percent")]
    public double? UsagePercent { get; init; }

    [JsonPropertyName("credits")]
    public CodexCreditDetails? Credits { get; init; }
}

public record CodexRateLimitDetails
{
    [JsonPropertyName("primary_window")]
    public CodexWindowSnapshot? PrimaryWindow { get; init; }

    [JsonPropertyName("secondary_window")]
    public CodexWindowSnapshot? SecondaryWindow { get; init; }

    [JsonPropertyName("code_review_window")]
    public CodexWindowSnapshot? CodeReviewWindow { get; init; }
}

public record CodexWindowSnapshot
{
    [JsonPropertyName("used_percent")]
    public double UsedPercent { get; init; }

    [JsonPropertyName("usage_percent")]
    public double? UsagePercent { get; init; }

    [JsonPropertyName("reset_at")]
    public long? ResetAt { get; init; }

    [JsonPropertyName("limit_window_seconds")]
    public long? LimitWindowSeconds { get; init; }
}

public record CodexCreditDetails
{
    [JsonPropertyName("has_credits")]
    public bool? HasCredits { get; init; }

    [JsonPropertyName("unlimited")]
    public bool? Unlimited { get; init; }

    [JsonPropertyName("balance")]
    public string? Balance { get; init; }
}

// ═══════════════════════════════════════════════════════
//  Claude — https://claude.ai/api
// ═══════════════════════════════════════════════════════

public record ClaudeOrganization
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public record ClaudeUsageResponse
{
    [JsonPropertyName("five_hour")]
    public ClaudeUsageWindow? FiveHour { get; init; }

    [JsonPropertyName("seven_day")]
    public ClaudeUsageWindow? SevenDay { get; init; }

    [JsonPropertyName("seven_day_opus")]
    public ClaudeUsageWindow? SevenDayOpus { get; init; }

    [JsonPropertyName("seven_day_sonnet")]
    public ClaudeUsageWindow? SevenDaySonnet { get; init; }
}

public record ClaudeUsageWindow
{
    [JsonPropertyName("utilization")]
    public double? Utilization { get; init; }

    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; init; }
}

public record ClaudeExtraUsageResponse
{
    [JsonPropertyName("monthly_credit_limit")]
    public double? MonthlyCreditLimit { get; init; }

    [JsonPropertyName("used_credits")]
    public double? UsedCredits { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("is_enabled")]
    public bool? IsEnabled { get; init; }
}

public record ClaudeAccountResponse
{
    [JsonPropertyName("email_address")]
    public string? EmailAddress { get; init; }

    [JsonPropertyName("rate_limit_tier")]
    public string? RateLimitTier { get; init; }
}

// ═══════════════════════════════════════════════════════
//  Gemini — https://cloudcode-pa.googleapis.com
// ═══════════════════════════════════════════════════════

public record GeminiOAuthCredentials
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>Milliseconds since epoch.</summary>
    [JsonPropertyName("expiry_date")]
    public double? ExpiryDate { get; set; }

    public bool IsExpired()
    {
        if (ExpiryDate is null) return false;
        var expirySecs = ExpiryDate.Value / 1000.0;
        var nowSecs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return nowSecs > expirySecs;
    }
}

public record GeminiTokenRefreshResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [JsonPropertyName("expires_in")]
    public double? ExpiresIn { get; init; }
}

public record GeminiQuotaResponse
{
    [JsonPropertyName("buckets")]
    public List<GeminiQuotaBucket>? Buckets { get; init; }
}

public record GeminiQuotaBucket
{
    [JsonPropertyName("remainingFraction")]
    public double? RemainingFraction { get; init; }

    [JsonPropertyName("resetTime")]
    public string? ResetTime { get; init; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; init; }

    [JsonPropertyName("tokenType")]
    public string? TokenType { get; init; }
}

// ═══════════════════════════════════════════════════════
//  Cursor — https://cursor.com/api
// ═══════════════════════════════════════════════════════

public record CursorUsageSummary
{
    [JsonPropertyName("billingCycleStart")]
    public string? BillingCycleStart { get; init; }

    [JsonPropertyName("billingCycleEnd")]
    public string? BillingCycleEnd { get; init; }

    [JsonPropertyName("membershipType")]
    public string? MembershipType { get; init; }

    [JsonPropertyName("limitType")]
    public string? LimitType { get; init; }

    [JsonPropertyName("isUnlimited")]
    public bool? IsUnlimited { get; init; }

    [JsonPropertyName("individualUsage")]
    public CursorIndividualUsage? IndividualUsage { get; init; }
}

public record CursorIndividualUsage
{
    [JsonPropertyName("plan")]
    public CursorPlanUsage? Plan { get; init; }

    [JsonPropertyName("onDemand")]
    public CursorOnDemandUsage? OnDemand { get; init; }
}

public record CursorPlanUsage
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("used")]
    public long? Used { get; init; }

    [JsonPropertyName("limit")]
    public long? Limit { get; init; }

    [JsonPropertyName("remaining")]
    public long? Remaining { get; init; }

    [JsonPropertyName("breakdown")]
    public CursorPlanBreakdown? Breakdown { get; init; }

    [JsonPropertyName("autoPercentUsed")]
    public double? AutoPercentUsed { get; init; }

    [JsonPropertyName("apiPercentUsed")]
    public double? ApiPercentUsed { get; init; }

    [JsonPropertyName("totalPercentUsed")]
    public double? TotalPercentUsed { get; init; }
}

public record CursorPlanBreakdown
{
    [JsonPropertyName("included")]
    public long? Included { get; init; }

    [JsonPropertyName("bonus")]
    public long? Bonus { get; init; }

    [JsonPropertyName("total")]
    public long? Total { get; init; }
}

public record CursorOnDemandUsage
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("used")]
    public long? Used { get; init; }

    [JsonPropertyName("limit")]
    public long? Limit { get; init; }

    [JsonPropertyName("remaining")]
    public long? Remaining { get; init; }
}

public record CursorUserInfo
{
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("sub")]
    public string? Sub { get; init; }
}

// ═══════════════════════════════════════════════════════
//  Xingchen (iFlytek) — https://maas.xfyun.cn
// ═══════════════════════════════════════════════════════

public record XingchenUsageResponse
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    public XingchenData? Data { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public record XingchenData
{
    [JsonPropertyName("rows")]
    public List<XingchenPlanRow>? Rows { get; init; }
}

public record XingchenPlanRow
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("codingPlanUsageDTO")]
    public XingchenUsageStats? CodingPlanUsageDTO { get; init; }

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; init; }
}

public record XingchenUsageStats
{
    [JsonPropertyName("packageUsage")]
    public int PackageUsage { get; init; }

    [JsonPropertyName("packageLimit")]
    public int PackageLimit { get; init; }

    [JsonPropertyName("rp5hUsage")]
    public int Rp5hUsage { get; init; }

    [JsonPropertyName("rp5hLimit")]
    public int Rp5hLimit { get; init; }

    [JsonPropertyName("rpwUsage")]
    public int RpwUsage { get; init; }

    [JsonPropertyName("rpwLimit")]
    public int RpwLimit { get; init; }
}

