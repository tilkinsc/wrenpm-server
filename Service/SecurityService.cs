using System.Security.Cryptography;

interface ISecurityService
{
	Task<AuthenticationResult> AuthenticateAsync(HttpRequest request);
	Task<bool> CanDeletePackageAsync(string user, string packageName);
	string ComputeChecksum(Stream stream);
}

class SecurityService(IConfiguration config, ILogger<SecurityService> logger) : ISecurityService
{
	private readonly PackageManagerConfig _config = config
			.GetSection("PackageManager")
			.Get<PackageManagerConfig>()
			?? new();

	public Task<AuthenticationResult> AuthenticateAsync(HttpRequest request)
	{
		var apiKey = request.Headers[_config.ApiKeyHeaderName].FirstOrDefault();

		if (string.IsNullOrEmpty(apiKey) || !_config.ValidApiKeys.Contains(apiKey))
		{
			return Task.FromResult(new AuthenticationResult { IsSuccess = false });
		}

		// In a real implementation, you'd map API keys to users
		var user = $"user_{apiKey.Substring(0, Math.Min(8, apiKey.Length))}";
		return Task.FromResult(new AuthenticationResult { IsSuccess = true, User = user });
	}

	public Task<bool> CanDeletePackageAsync(string user, string packageName)
	{
		// In a real implementation, you'd check user permissions against package ownership
		// For now, allow authenticated users to delete packages
		return Task.FromResult(!string.IsNullOrEmpty(user));
	}

	public string ComputeChecksum(Stream stream)
	{
		using var sha256 = SHA256.Create();
		var hash = sha256.ComputeHash(stream);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
