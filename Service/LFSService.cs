
interface ILFSService
{
	Task<string> StorePackageAsync(string name, string version, Stream stream);
	Task<Stream?> GetPackageStreamAsync(string name, string version);
	Task<bool> DeletePackageAsync(string name, string version);
	Task<bool> PackageExistsAsync(string name, string version);
}

class LFSService : ILFSService
{
	private readonly string _basePath;
	private readonly ILogger<LFSService> _logger;

	public LFSService(IConfiguration config, ILogger<LFSService> logger)
	{
		var packageConfig = config.GetSection("PackageManager").Get<PackageManagerConfig>() ?? new();
		_basePath = Path.GetFullPath(packageConfig.PackageStoragePath);
		_logger = logger;
		Directory.CreateDirectory(_basePath);
	}

	public async Task<string> StorePackageAsync(string name, string version, Stream stream)
	{
		var dirPath = Path.Combine(_basePath, name, version);
		Directory.CreateDirectory(dirPath);

		var filePath = Path.Combine(dirPath, "package.zip");
		await using var fileStream = File.Create(filePath);
		await stream.CopyToAsync(fileStream);

		return filePath;
	}

	public Task<Stream?> GetPackageStreamAsync(string name, string version)
	{
		var filePath = Path.Combine(_basePath, name, version, "package.zip");

		if (!File.Exists(filePath))
			return Task.FromResult<Stream?>(null);

		return Task.FromResult<Stream?>(File.OpenRead(filePath));
	}

	public Task<bool> DeletePackageAsync(string name, string version)
	{
		try
		{
			var dirPath = Path.Combine(_basePath, name, version);
			if (Directory.Exists(dirPath))
			{
				Directory.Delete(dirPath, true);
				return Task.FromResult(true);
			}
			return Task.FromResult(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to delete package {Name}@{Version}", name, version);
			return Task.FromResult(false);
		}
	}

	public Task<bool> PackageExistsAsync(string name, string version)
	{
		var filePath = Path.Combine(_basePath, name, version, "package.zip");
		return Task.FromResult(File.Exists(filePath));
	}
}
